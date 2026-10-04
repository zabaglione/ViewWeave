using NUnit.Framework;
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.TestTools;
using LookingGlass.Community;
using Object = UnityEngine.Object;

namespace LookingGlass.BridgeSessionValidation
{
    public sealed class BridgeSessionValidationTests
    {
        [Test]
        public void InvalidQualityIsRejectedWithoutChangingQuality()
        {
            GameObject host = CreateHost(false);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                QuiltQualityPreset before = session.Quality;
                Assert.IsFalse(session.SetQuality((QuiltQualityPreset)99));
                Assert.AreEqual(before, session.Quality);
                Assert.IsTrue(IsAscii(session.LastError));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void IdleQualityUpdateIsPersistedWithoutStartingWorker()
        {
            GameObject host = CreateHost(false);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle, session.State);
                Assert.IsTrue(session.SetQuality(QuiltQualityPreset.Native));
                Assert.AreEqual(QuiltQualityPreset.Native, session.Quality);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle, session.State);
                Assert.IsTrue(IsAscii(session.LastError));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void EditorStartIsRejectedWithAsciiDiagnostic()
        {
            GameObject host = CreateHost(false);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                host.SetActive(true);
                Assert.IsFalse(session.StartSession());
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Fault, session.State);
                StringAssert.Contains("Windows Player", session.LastError);
                Assert.IsTrue(IsAscii(session.LastError));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void DisabledSessionCannotStart()
        {
            GameObject host = CreateHost(false);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                session.enabled = false;
                Assert.IsFalse(session.StartSession());
                Assert.IsTrue(IsAscii(session.LastError));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void StopIsIdempotentAndDoesNotEnableAutoRender()
        {
            GameObject host = CreateHost(false);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                ViewWeaveRig rig = host.GetComponent<ViewWeaveRig>();
                session.StopSession();
                session.StopSession();
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle, session.State);
                Assert.IsFalse(rig.AutoRender);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void DuplicateStartDoesNotReplaceFailureState()
        {
            GameObject host = CreateHost(false);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                Assert.IsFalse(session.StartSession());
                string firstError = session.LastError;
                Assert.IsFalse(session.StartSession());
                Assert.IsNotEmpty(firstError);
                Assert.IsNotEmpty(session.LastError);
                Assert.IsTrue(IsAscii(session.LastError));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void ConnectingTimeoutFaultsWithoutForcingWorkerRelease()
        {
            GameObject host = CreateHost(true);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                BridgeWorkerQuiltOutput output = host.GetComponent<BridgeWorkerQuiltOutput>();
                Type outputType = typeof(BridgeWorkerQuiltOutput);
                FieldInfo outputState = outputType.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo sessionState = typeof(ViewWeaveBridgeSession).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo connectingSince = typeof(ViewWeaveBridgeSession).GetField("connectingSince", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo rigAutoRender = typeof(ViewWeaveRig).GetField("autoRender", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo update = typeof(ViewWeaveBridgeSession).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(outputState);
                Assert.IsNotNull(sessionState);
                Assert.IsNotNull(connectingSince);
                Assert.IsNotNull(rigAutoRender);
                Assert.IsNotNull(update);
                ViewWeaveRig rig = host.GetComponent<ViewWeaveRig>();
                rigAutoRender.SetValue(rig, true);
                outputState.SetValue(output, BridgeWorkerQuiltOutput.WorkerState.Connecting);
                sessionState.SetValue(session, ViewWeaveBridgeSession.SessionState.Connecting);
                connectingSince.SetValue(session, Time.realtimeSinceStartup - 11f);
                update.Invoke(session, null);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Fault, session.State);
                StringAssert.Contains("connection timed out", session.LastError);
                Assert.IsTrue(IsAscii(session.LastError));
                Assert.IsFalse(rig.AutoRender);
                Assert.AreEqual(BridgeWorkerQuiltOutput.WorkerState.Closed, output.State);
                Assert.AreEqual(0, BridgeWorkerQuiltOutput.ShutdownPendingCount);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CompletionWatchdogFaultsOnlyAfterRenderingProgressStalls()
        {
            GameObject host = CreateHost(true);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                BridgeWorkerQuiltOutput output = host.GetComponent<BridgeWorkerQuiltOutput>();
                ViewWeaveRig rig = host.GetComponent<ViewWeaveRig>();
                Type outputType = typeof(BridgeWorkerQuiltOutput);
                FieldInfo outputState = outputType.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo submissions = outputType.GetField("<Submissions>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo sessionState = typeof(ViewWeaveBridgeSession).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo watchdogArmed = typeof(ViewWeaveBridgeSession).GetField("completionWatchdogArmed", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo watchdogSubmissions = typeof(ViewWeaveBridgeSession).GetField("watchdogSubmissions", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo watchdogCompleted = typeof(ViewWeaveBridgeSession).GetField("watchdogCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo progressSince = typeof(ViewWeaveBridgeSession).GetField("completionProgressSince", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo update = typeof(ViewWeaveBridgeSession).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(outputState); Assert.IsNotNull(submissions); Assert.IsNotNull(sessionState);
                Assert.IsNotNull(watchdogArmed); Assert.IsNotNull(watchdogSubmissions); Assert.IsNotNull(watchdogCompleted); Assert.IsNotNull(progressSince); Assert.IsNotNull(update);
                typeof(ViewWeaveRig).GetField("autoRender", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(rig, true);
                typeof(ViewWeaveBridgeSession).GetField("profileApplied", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, true);
                outputState.SetValue(output, BridgeWorkerQuiltOutput.WorkerState.Ready);
                submissions.SetValue(output, 10u);
                sessionState.SetValue(session, ViewWeaveBridgeSession.SessionState.Rendering);
                watchdogArmed.SetValue(session, true);
                watchdogSubmissions.SetValue(session, 10u);
                watchdogCompleted.SetValue(session, 0u);
                progressSince.SetValue(session, Time.realtimeSinceStartup - 11f);
                update.Invoke(session, null);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Fault, session.State);
                StringAssert.Contains("completion timed out", session.LastError);
                Assert.IsFalse(rig.AutoRender);
                Assert.IsTrue(IsAscii(session.LastError));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CompletionWatchdogIgnoresExistingSubmissionCounterWithoutNewSubmission()
        {
            GameObject host = CreateHost(true);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                BridgeWorkerQuiltOutput output = host.GetComponent<BridgeWorkerQuiltOutput>();
                ViewWeaveRig rig = host.GetComponent<ViewWeaveRig>();
                Type outputType = typeof(BridgeWorkerQuiltOutput);
                outputType.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, BridgeWorkerQuiltOutput.WorkerState.Ready);
                outputType.GetField("<Submissions>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(output, 20u);
                typeof(ViewWeaveBridgeSession).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, ViewWeaveBridgeSession.SessionState.Rendering);
                typeof(ViewWeaveBridgeSession).GetField("watchdogSubmissions", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, 20u);
                typeof(ViewWeaveBridgeSession).GetField("watchdogCompleted", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, 20u);
                typeof(ViewWeaveBridgeSession).GetField("completionWatchdogArmed", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, false);
                typeof(ViewWeaveBridgeSession).GetField("completionProgressSince", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, Time.realtimeSinceStartup - 11f);
                typeof(ViewWeaveRig).GetField("autoRender", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(rig, true);
                typeof(ViewWeaveBridgeSession).GetField("profileApplied", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, true);
                typeof(ViewWeaveBridgeSession).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(session, null);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Rendering, session.State);
                Assert.IsTrue(rig.AutoRender);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void CompletionWatchdogRefreshesOnProgressAndRearmsAfterAutoRenderResume()
        {
            GameObject host = CreateHost(true);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                BridgeWorkerQuiltOutput output = host.GetComponent<BridgeWorkerQuiltOutput>();
                ViewWeaveRig rig = host.GetComponent<ViewWeaveRig>();
                Type outputType = typeof(BridgeWorkerQuiltOutput);
                Type statusType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("LookingGlass.Community.BridgeWorkerNative", true).GetNestedType("Status", BindingFlags.Public | BindingFlags.NonPublic);
                FieldInfo status = outputType.GetField("status", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo outputState = outputType.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo submissions = outputType.GetField("<Submissions>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo sessionState = typeof(ViewWeaveBridgeSession).GetField("<State>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo armed = typeof(ViewWeaveBridgeSession).GetField("completionWatchdogArmed", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo completedBaseline = typeof(ViewWeaveBridgeSession).GetField("watchdogCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo since = typeof(ViewWeaveBridgeSession).GetField("completionProgressSince", BindingFlags.Instance | BindingFlags.NonPublic);
                FieldInfo auto = typeof(ViewWeaveRig).GetField("autoRender", BindingFlags.Instance | BindingFlags.NonPublic);
                MethodInfo update = typeof(ViewWeaveBridgeSession).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
                object value = Activator.CreateInstance(statusType);
                SetStatusField(statusType, value, "completed", 4u);
                status.SetValue(output, value);
                outputState.SetValue(output, BridgeWorkerQuiltOutput.WorkerState.Ready);
                submissions.SetValue(output, 5u);
                sessionState.SetValue(session, ViewWeaveBridgeSession.SessionState.Rendering);
                typeof(ViewWeaveBridgeSession).GetField("profileApplied", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(session, true);
                armed.SetValue(session, true); completedBaseline.SetValue(session, 3u); since.SetValue(session, Time.realtimeSinceStartup - 11f);
                auto.SetValue(rig, true); float beforeRefresh = Time.realtimeSinceStartup; update.Invoke(session, null);
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Rendering, session.State);
                Assert.IsTrue((bool)armed.GetValue(session));
                Assert.Greater((float)since.GetValue(session), beforeRefresh - 0.1f);
                since.SetValue(session, Time.realtimeSinceStartup - 11f);
                auto.SetValue(rig, false); update.Invoke(session, null);
                Assert.IsFalse((bool)armed.GetValue(session));
                auto.SetValue(rig, true); update.Invoke(session, null);
                Assert.IsFalse((bool)armed.GetValue(session));
                submissions.SetValue(output, 6u); update.Invoke(session, null);
                Assert.IsTrue((bool)armed.GetValue(session));
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        [Test]
        public void InvalidProfilePresetIsRejectedBeforeProfileUse()
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => ViewWeaveBridgeSession.CreateQualityProfile(default, (QuiltQualityPreset)99));
        }

        [Test]
        public void CorePreviewProfileConversionKeepsGridAndViewCount()
        {
            QuiltQualityProfile profile = QuiltQualityProfile.Preview(1920, 1080, 8, 6, 1f, 40f);
            Assert.AreEqual(48, profile.Views);
            Assert.AreEqual(8, profile.Columns);
            Assert.AreEqual(6, profile.Rows);
            Assert.AreEqual(0, profile.QuiltWidth % profile.Columns);
            Assert.AreEqual(0, profile.QuiltHeight % profile.Rows);
        }

        [Test]
        public void CoreNativeProfilePreservesNonDivisibleDeviceDimensions()
        {
            QuiltQualityProfile profile = QuiltQualityProfile.Native(4096, 4096, 5, 9, 1f, 40f);
            Assert.AreEqual(4096, profile.QuiltWidth);
            Assert.AreEqual(4096, profile.QuiltHeight);
            Assert.AreEqual(45, profile.Views);
            QuiltLayout layout = new QuiltLayout(profile.QuiltWidth, profile.QuiltHeight, profile.Rows, profile.Columns, profile.Views, profile.Aspect);
            Assert.AreEqual(4095, layout.UsedRect.width);
            Assert.AreEqual(4095, layout.UsedRect.height);
            Assert.AreEqual(1, layout.UnusedRight);
            Assert.AreEqual(1, layout.UnusedTop);
        }

        [Test]
        public void BridgeStatusAcceptsNativeNonDivisibleGridAndRejectsZeroFloor()
        {
            Type nativeType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("LookingGlass.Community.BridgeWorkerNative", true);
            Type statusType = nativeType.GetNestedType("Status", BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo validator = typeof(BridgeWorkerQuiltOutput).GetMethod("IsValidStatus", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(statusType);
            Assert.IsNotNull(validator);

            object valid = Activator.CreateInstance(statusType);
            SetStatusField(statusType, valid, "width", 1024u);
            SetStatusField(statusType, valid, "height", 1024u);
            SetStatusField(statusType, valid, "quiltWidth", 4096);
            SetStatusField(statusType, valid, "quiltHeight", 4096);
            SetStatusField(statusType, valid, "quiltColumns", 5);
            SetStatusField(statusType, valid, "quiltRows", 9);
            SetStatusField(statusType, valid, "aspect", 1f);
            SetStatusField(statusType, valid, "viewcone", 40f);
            Assert.IsTrue((bool)validator.Invoke(null, new[] { valid }));

            object invalid = Activator.CreateInstance(statusType);
            SetStatusField(statusType, invalid, "width", 1024u);
            SetStatusField(statusType, invalid, "height", 1024u);
            SetStatusField(statusType, invalid, "quiltWidth", 4);
            SetStatusField(statusType, invalid, "quiltHeight", 1024);
            SetStatusField(statusType, invalid, "quiltColumns", 5);
            SetStatusField(statusType, invalid, "quiltRows", 1);
            SetStatusField(statusType, invalid, "aspect", 1f);
            SetStatusField(statusType, invalid, "viewcone", 40f);
            Assert.IsFalse((bool)validator.Invoke(null, new[] { invalid }));
        }

        static void SetStatusField(Type statusType, object status, string name, object value)
        {
            statusType.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).SetValue(status, value);
        }

        [Test]
        public void ExplicitAbsoluteRuntimePathHasPriority()
        {
            string explicitPath = Path.Combine(Path.GetTempPath(), "bridge-session-explicit-" + Guid.NewGuid().ToString("N") + ".dll");
            string environmentPath = Path.Combine(Path.GetTempPath(), "bridge-session-environment-missing-" + Guid.NewGuid().ToString("N") + ".dll");
            string previous = Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH");
            try
            {
                File.WriteAllText(explicitPath, "test");
                Environment.SetEnvironmentVariable("LKG_BRIDGE_PATH", environmentPath);
                Assert.IsTrue(BridgeRuntimeLocator.TryResolve(explicitPath, out string resolved, out string error), error);
                Assert.AreEqual(Path.GetFullPath(explicitPath), resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LKG_BRIDGE_PATH", previous);
                if (File.Exists(explicitPath)) File.Delete(explicitPath);
            }
        }

        [Test]
        public void MissingAndRelativeRuntimePathsAreRejected()
        {
            string missingPath = Path.Combine(Path.GetTempPath(), "missing-bridge-" + Guid.NewGuid().ToString("N") + ".dll");
            Assert.IsFalse(BridgeRuntimeLocator.TryResolve(missingPath, out _, out string missingError));
            Assert.IsTrue(IsAscii(missingError));
            Assert.IsFalse(BridgeRuntimeLocator.TryResolve("relative-bridge.dll", out _, out string relativeError));
            Assert.IsTrue(IsAscii(relativeError));
        }

        [Test]
        public void EnvironmentRuntimePathResolvesWithoutNativeLoad()
        {
            string environmentPath = Path.Combine(Path.GetTempPath(), "bridge-session-environment-" + Guid.NewGuid().ToString("N") + ".dll");
            string previous = Environment.GetEnvironmentVariable("LKG_BRIDGE_PATH");
            try
            {
                File.WriteAllText(environmentPath, "test");
                Environment.SetEnvironmentVariable("LKG_BRIDGE_PATH", environmentPath);
                Assert.IsTrue(BridgeRuntimeLocator.TryResolve(string.Empty, out string resolved, out string error), error);
                Assert.AreEqual(Path.GetFullPath(environmentPath), resolved);
            }
            finally
            {
                Environment.SetEnvironmentVariable("LKG_BRIDGE_PATH", previous);
                if (File.Exists(environmentPath)) File.Delete(environmentPath);
            }
        }

        [UnityTest]
        public IEnumerator CoreAutoRenderFailureIsAsciiAndResettable()
        {
            GameObject host = new GameObject("BridgeSessionValidation.AutoFailure");
            try
            {
            host.SetActive(false);
            Camera camera = host.AddComponent<Camera>();
            QuiltRenderer renderer = host.AddComponent<QuiltRenderer>();
            renderer.sourceCamera = camera;
            renderer.settings.quiltWidth = 8;
            renderer.settings.quiltHeight = 8;
            renderer.settings.columns = 1;
            renderer.settings.rows = 1;
            renderer.settings.views = 1;
            renderer.settings.viewAspect = 1f;
            renderer.settings.viewCone = 1f;
            camera.enabled = true;
            renderer.autoRender = true;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Looking Glass quilt auto render stopped: .*sourceCamera must be disabled.*"));
            host.SetActive(true);
            yield return null;
            yield return null;
            Assert.IsFalse(renderer.autoRender);
            StringAssert.Contains("sourceCamera must be disabled", renderer.LastAutoRenderError);
            Assert.IsTrue(IsAscii(renderer.LastAutoRenderError));
            renderer.ResetAutoRenderError();
            Assert.IsEmpty(renderer.LastAutoRenderError);
            renderer.enabled = false;
            renderer.enabled = true;
            camera.enabled = true;
            renderer.autoRender = true;
            LogAssert.Expect(LogType.Error, new System.Text.RegularExpressions.Regex("Looking Glass quilt auto render stopped: .*sourceCamera must be disabled.*"));
            yield return null;
            yield return null;
            Assert.IsFalse(renderer.autoRender);
            Assert.IsNotEmpty(renderer.LastAutoRenderError);
            }
            finally
            {
                Object.DestroyImmediate(host);
            }
        }

        static GameObject CreateHost(bool active)
        {
            GameObject host = new GameObject("BridgeSessionValidation");
            host.SetActive(active);
            host.AddComponent<ViewWeaveBridgeSession>();
            return host;
        }

        static bool IsAscii(string value)
        {
            if (value == null) return true;
            foreach (char valueChar in value) if (valueChar > 0x7e) return false;
            return true;
        }
    }
}
