using NUnit.Framework;
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.TestTools;
using ViewWeave;
using Object = UnityEngine.Object;

namespace ViewWeave.BridgeSessionValidation
{
    public sealed class BridgeSessionValidationTests
    {
        [Test]
        [TestCase(BridgeSubpixelMode.Automatic)]
        [TestCase(BridgeSubpixelMode.ClassicRgb)]
        [TestCase(BridgeSubpixelMode.CalibratedCells)]
        public void WindowsWorkerAcceptsEveryValidSubpixelMode(BridgeSubpixelMode mode)
        {
            GameObject host = CreateHost(false);
            try
            {
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                Assert.That(session.SubpixelMode, Is.EqualTo(BridgeSubpixelMode.Automatic), "Missing/initial mode values must remain Automatic.");
                Assert.That(session.TrySetSubpixelMode(mode, out string error), Is.True, error);
                Assert.That(IsAscii(error), Is.True);
                Assert.That(session.SubpixelMode, Is.EqualTo(mode));
                Assert.That(BridgeSubpixelPolicy.Validate(mode, out error), Is.True, error);
                Assert.That(session.State, Is.EqualTo(ViewWeaveBridgeSession.SessionState.Idle));
                Assert.That(session.SubpixelMode, Is.EqualTo(mode));
                Assert.That(host.GetComponent<BridgeWorkerQuiltOutput>().State, Is.EqualTo(BridgeWorkerQuiltOutput.WorkerState.Closed));
                Assert.That(session.TrySetSubpixelMode((BridgeSubpixelMode)999, out error), Is.False);
                Assert.That(IsAscii(error), Is.True);
                Assert.That(BridgeSubpixelPolicy.Validate((BridgeSubpixelMode)999, out error), Is.False);
                Assert.That(IsAscii(error), Is.True);
                Assert.That(session.SubpixelMode, Is.EqualTo(mode));
                Assert.That(host.GetComponent<BridgeWorkerQuiltOutput>().State, Is.EqualTo(BridgeWorkerQuiltOutput.WorkerState.Closed));
            }
            finally { Object.DestroyImmediate(host); }
        }

        [Test]
        public void WindowsSubpixelOptionsAndStatusAbiHaveStableLayout()
        {
            Assert.That(BridgeWorkerNative.HasStableSubpixelAbiLayout(), Is.True);
            Assert.That(Marshal.SizeOf(typeof(BridgeWorkerNative.WorkerOutputOptionsV1)), Is.EqualTo(16));
            Assert.That(Marshal.OffsetOf(typeof(BridgeWorkerNative.WorkerOutputOptionsV1), "subpixelMode").ToInt32(), Is.EqualTo(8));
            Assert.That(Marshal.OffsetOf(typeof(BridgeWorkerNative.WorkerOutputOptionsV1), "linearColorSpace").ToInt32(), Is.EqualTo(12));
            Assert.That(Marshal.SizeOf(typeof(BridgeWorkerNative.WorkerDrawOptionsV1)), Is.EqualTo(16));
            Assert.That(Marshal.OffsetOf(typeof(BridgeWorkerNative.WorkerDrawOptionsV1), "sourceEncoding").ToInt32(), Is.EqualTo(8));
            Assert.That(Marshal.OffsetOf(typeof(BridgeWorkerNative.WorkerDrawOptionsV1), "reserved0").ToInt32(), Is.EqualTo(12));
            Assert.That(Marshal.SizeOf(typeof(BridgeWorkerNative.SubpixelStatusV1)), Is.EqualTo(48));
            Assert.That(Marshal.OffsetOf(typeof(BridgeWorkerNative.SubpixelStatusV1), "effectiveMode").ToInt32(), Is.EqualTo(16));
            Assert.That(Marshal.OffsetOf(typeof(BridgeWorkerNative.SubpixelStatusV1), "presentAccepted").ToInt32(), Is.EqualTo(44));
            Assert.That(BridgeWorkerNative.WorkerOutputOptionsVersion, Is.EqualTo(1u));
            Assert.That(BridgeWorkerNative.SubpixelStatusVersion, Is.EqualTo(1u));
            Assert.That(BridgeWorkerNative.WorkerDrawOptionsVersion, Is.EqualTo(1u));
            Assert.That(BridgeWorkerNative.WorkerSourceEncodingUnorm, Is.EqualTo(0u));
            Assert.That(BridgeWorkerNative.WorkerSourceEncodingSrgb, Is.EqualTo(1u));
            Assert.That(BridgeWorkerNative.SubpixelBackendNativeD3D11, Is.EqualTo(1u));
            Assert.That(BridgeWorkerNative.InvalidSubpixelMode, Is.EqualTo(uint.MaxValue));
        }

        [Test]
        public void LoadedNativePendingAbiFailureDoesNotReportCleanShutdown()
        {
            Assert.That(BridgeWorkerQuiltOutput.GetNativePendingCount(0, false, () => 3), Is.EqualTo(3));
            Assert.That(BridgeWorkerQuiltOutput.GetNativePendingCount(2, true, () => 1), Is.EqualTo(2));
            Assert.That(BridgeWorkerQuiltOutput.GetNativePendingCount(0, false, () => throw new DllNotFoundException()), Is.EqualTo(0),
                "A library that was never loaded has no native work to report.");
            Assert.That(BridgeWorkerQuiltOutput.GetNativePendingCount(0, true, () => throw new DllNotFoundException()), Is.EqualTo(1),
                "A known-loaded Native library without managed pending handles cannot be reported clean.");
            Assert.That(BridgeWorkerQuiltOutput.GetNativePendingCount(0, true, () => throw new EntryPointNotFoundException()), Is.EqualTo(1),
                "A loaded library missing the required pending-count ABI must remain unproven, not clean.");
            Assert.That(BridgeWorkerQuiltOutput.GetNativePendingCount(4, true, () => throw new EntryPointNotFoundException()), Is.EqualTo(4));
        }

        [Test]
        public void SharedQuiltInputPolicyAcceptsNonDefaultCompleteGrid()
        {
            var texture = new RenderTexture(15, 10, 0);
            try
            {
                var layout = new QuiltLayout(15, 10, 2, 3, 6, 1.25f);
                Assert.That(BridgeQuiltInputPolicy.TryValidate(texture, layout, out string error), Is.True, error);
                Assert.That(IsAscii(error), Is.True);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void SharedQuiltInputPolicyRejectsTextureLayoutDimensionMismatch()
        {
            var texture = new RenderTexture(16, 10, 0);
            try
            {
                var layout = new QuiltLayout(15, 10, 2, 3, 6);
                Assert.That(BridgeQuiltInputPolicy.TryValidate(texture, layout, out string error), Is.False);
                Assert.That(IsAscii(error), Is.True);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void SharedQuiltInputPolicyRejectsPaddedTextureDimensions()
        {
            var texture = new RenderTexture(16, 10, 0);
            try
            {
                var paddedLayout = new QuiltLayout(16, 10, 2, 3, 6);
                Assert.That(BridgeQuiltInputPolicy.TryValidate(texture, paddedLayout, out string error), Is.False);
                Assert.That(IsAscii(error), Is.True);
            }
            finally { Object.DestroyImmediate(texture); }
        }

        [Test]
        public void SharedQuiltInputPolicyRejectsPartialAndDefaultLayouts()
        {
            var texture = new RenderTexture(15, 10, 0);
            try
            {
                var partialLayout = new QuiltLayout(15, 10, 2, 3, 5);
                Assert.That(BridgeQuiltInputPolicy.TryValidate(texture, partialLayout, out string partialError), Is.False);
                Assert.That(IsAscii(partialError), Is.True);
                Assert.That(BridgeQuiltInputPolicy.TryValidate(texture, default, out string defaultError), Is.False);
                Assert.That(IsAscii(defaultError), Is.True);
            }
            finally { Object.DestroyImmediate(texture); }
        }

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
        public void AutostartRemainsPendingWhileConnectedDisplayRefreshOwnsNativeMetadata()
        {
            GameObject host = CreateHost(false);
            FieldInfo pendingStart = typeof(ViewWeaveBridgeSession).GetField("pendingStart", BindingFlags.Instance | BindingFlags.NonPublic);
            FieldInfo recoveryPending = typeof(BridgeDisplayDiscovery).GetField("recoveryPending", BindingFlags.Static | BindingFlags.NonPublic);
            MethodInfo update = typeof(ViewWeaveBridgeSession).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                Assert.IsNotNull(pendingStart);
                Assert.IsNotNull(recoveryPending);
                Assert.IsNotNull(update);
                ViewWeaveBridgeSession session = host.GetComponent<ViewWeaveBridgeSession>();
                host.GetComponent<BridgeWorkerQuiltOutput>().initializeOnEnable = false;
                host.SetActive(true);
                pendingStart.SetValue(session, true);
                recoveryPending.SetValue(null, true);

                update.Invoke(session, null);

                Assert.IsTrue((bool)pendingStart.GetValue(session), "Autostart must wait until metadata cleanup releases SDK ownership.");
                Assert.AreEqual(ViewWeaveBridgeSession.SessionState.Idle, session.State);
                Assert.IsTrue(BridgeDisplayDiscovery.IsBusy);
            }
            finally
            {
                recoveryPending?.SetValue(null, false);
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
                RegisterSyntheticSessionOwner(session);
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
                RegisterSyntheticSessionOwner(session);
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
                RegisterSyntheticSessionOwner(session);
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
                Type statusType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeWorkerNative", true).GetNestedType("Status", BindingFlags.Public | BindingFlags.NonPublic);
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
                RegisterSyntheticSessionOwner(session);
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
        public void QualityPresetKeepsPreviewBalancedAndNativeDimensionsDistinct()
        {
            var native = new BridgeNative.DisplayInfo
            {
                index = 0, width = 3840, height = 2160, aspect = 16f / 9f, viewcone = 40f,
                quiltWidth = 4096, quiltHeight = 4096, quiltColumns = 8, quiltRows = 6
            };
            var profile = new BridgeDisplayProfile(native);
            QuiltQualityProfile preview = ViewWeaveBridgeSession.CreateQualityProfile(profile, QuiltQualityPreset.Preview);
            QuiltQualityProfile balanced = ViewWeaveBridgeSession.CreateQualityProfile(profile, QuiltQualityPreset.Balanced);
            QuiltQualityProfile nativeProfile = ViewWeaveBridgeSession.CreateQualityProfile(profile, QuiltQualityPreset.Native);

            Assert.AreEqual(1024, preview.QuiltWidth);
            Assert.AreEqual(2048, balanced.QuiltWidth);
            Assert.AreEqual(4096, nativeProfile.QuiltWidth);
            Assert.AreEqual(1026, preview.QuiltHeight);
            Assert.AreEqual(2046, balanced.QuiltHeight);
            Assert.AreEqual(4092, nativeProfile.QuiltHeight);
            Assert.AreEqual(4096, profile.QuiltHeight, "The raw Bridge metadata must not be rewritten.");
            var texture = new RenderTexture(nativeProfile.QuiltWidth, nativeProfile.QuiltHeight, 0);
            try
            {
                var layout = new QuiltLayout(nativeProfile.QuiltWidth, nativeProfile.QuiltHeight,
                    nativeProfile.Rows, nativeProfile.Columns, nativeProfile.Views, nativeProfile.Aspect);
                Assert.That(BridgeQuiltInputPolicy.TryValidate(texture, layout, out string error), Is.True, error);
            }
            finally { UnityEngine.Object.DestroyImmediate(texture); }
            Assert.AreEqual(48, preview.Views);
            Assert.AreEqual(48, balanced.Views);
            Assert.AreEqual(48, nativeProfile.Views);
        }

        [TestCase(7u, 1920u, 1080u, 4000, 4000, 1000, 999, 2000, 1998, 5, 9, 35f, 120f)]
        [TestCase(42u, 1440u, 2560u, 4092, 4092, 1023, 1020, 2046, 2046, 11, 6, 54f, 300f)]
        [TestCase(301u, 5120u, 2880u, 8192, 8192, 2048, 2046, 4096, 4098, 8, 6, 50f, 190f)]
        public void GenericConnectedDisplayProfilesPreserveDeviceMetadata(uint displayIndex, uint width, uint height,
            int quiltWidth, int quiltHeight, int previewWidth, int previewHeight, int balancedWidth, int balancedHeight,
            int columns, int rows, float viewcone, float dpi)
        {
            float aspect = width / (float)height;
            var native = new BridgeNative.DisplayInfo
            {
                index = displayIndex,
                width = width,
                height = height,
                aspect = aspect,
                viewcone = viewcone,
                quiltWidth = quiltWidth,
                quiltHeight = quiltHeight,
                quiltColumns = columns,
                quiltRows = rows
            };
            var physical = new BridgeWorkerNative.PhysicalDisplayInfoV1
            {
                structSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(BridgeWorkerNative.PhysicalDisplayInfoV1)),
                version = 1,
                pixelWidth = width,
                pixelHeight = height,
                dpi = dpi,
                valid = 1
            };
            var profile = new BridgeDisplayProfile(native, physical);

            Assert.IsTrue(profile.IsValid, "Synthetic metadata should form a valid profile; it is not a real model claim.");
            Assert.IsTrue(profile.HasPhysicalCalibration, "DPI-derived physical dimensions should be retained.");
            Assert.AreEqual(displayIndex, profile.DisplayIndex);
            Assert.AreEqual(width, profile.Width);
            Assert.AreEqual(height, profile.Height);
            Assert.AreEqual(aspect, profile.Aspect, 0.0001f);
            Assert.AreEqual(viewcone, profile.Viewcone, 0.0001f);
            Assert.AreEqual(quiltWidth, profile.QuiltWidth);
            Assert.AreEqual(quiltHeight, profile.QuiltHeight);
            Assert.AreEqual(columns, profile.Columns);
            Assert.AreEqual(rows, profile.Rows);
            Assert.AreEqual(dpi, profile.CalibrationDpi, 0.001f);
            Assert.AreEqual(width * 25.4f / dpi, profile.PhysicalWidthMillimeters, 0.01f);
            Assert.AreEqual(height * 25.4f / dpi, profile.PhysicalHeightMillimeters, 0.01f);

            foreach (QuiltQualityPreset preset in new[] { QuiltQualityPreset.Preview, QuiltQualityPreset.Balanced, QuiltQualityPreset.Native })
            {
                QuiltQualityProfile quality = ViewWeaveBridgeSession.CreateQualityProfile(profile, preset);
                Assert.AreEqual(columns, quality.Columns, preset + " columns");
                Assert.AreEqual(rows, quality.Rows, preset + " rows");
                Assert.AreEqual(columns * rows, quality.Views, preset + " all quilt views");
                Assert.AreEqual(aspect, quality.Aspect, 0.0001f, preset + " device aspect");
                Assert.AreEqual(viewcone, quality.ViewCone, 0.0001f, preset + " device viewcone");
                if (preset == QuiltQualityPreset.Preview)
                {
                    Assert.AreEqual(previewWidth, quality.QuiltWidth, "Preview quilt width");
                    Assert.AreEqual(previewHeight, quality.QuiltHeight, "Preview quilt height");
                }
                else if (preset == QuiltQualityPreset.Balanced)
                {
                    Assert.AreEqual(balancedWidth, quality.QuiltWidth, "Balanced quilt width");
                    Assert.AreEqual(balancedHeight, quality.QuiltHeight, "Balanced quilt height");
                }
                else
                {
                    Assert.AreEqual(quiltWidth / columns * columns, quality.QuiltWidth, "Native used quilt width");
                    Assert.AreEqual(quiltHeight / rows * rows, quality.QuiltHeight, "Native used quilt height");
                }

                var texture = new RenderTexture(quality.QuiltWidth, quality.QuiltHeight, 0);
                try
                {
                    var layout = new QuiltLayout(quality.QuiltWidth, quality.QuiltHeight, quality.Rows,
                        quality.Columns, quality.Views, quality.Aspect);
                    Assert.That(BridgeQuiltInputPolicy.TryValidate(texture, layout, out string inputError), Is.True,
                        preset + ": " + inputError);
                }
                finally { UnityEngine.Object.DestroyImmediate(texture); }

                var settings = new QuiltSettings();
                Assert.AreEqual(4096, settings.quiltWidth, "baseline quilt width");
                Assert.AreEqual(4096, settings.quiltHeight, "baseline quilt height");
                Assert.AreEqual(8, settings.columns, "baseline columns");
                Assert.AreEqual(6, settings.rows, "baseline rows");
                Assert.AreEqual(48, settings.views, "baseline views");
                Assert.AreEqual(1f, settings.viewAspect, "baseline aspect");
                Assert.AreEqual(40f, settings.viewCone, "baseline viewcone");

                quality.ApplyTo(settings);
                Assert.AreEqual(quality.QuiltWidth, settings.quiltWidth, preset + " applied quilt width");
                Assert.AreEqual(quality.QuiltHeight, settings.quiltHeight, preset + " applied quilt height");
                Assert.AreEqual(columns, settings.columns, preset + " applied columns");
                Assert.AreEqual(rows, settings.rows, preset + " applied rows");
                Assert.AreEqual(columns * rows, settings.views, preset + " applied views");
                Assert.AreEqual(aspect, settings.viewAspect, 0.0001f, preset + " applied aspect");
                Assert.AreEqual(viewcone, settings.viewCone, 0.0001f, preset + " applied viewcone");
                Assert.AreEqual(width * 25.4f / dpi, profile.PhysicalWidthMillimeters, 0.01f, preset + " retained physical width");
                Assert.AreEqual(height * 25.4f / dpi, profile.PhysicalHeightMillimeters, 0.01f, preset + " retained physical height");
            }
        }

        [Test]
        public void ActiveDisplayRefreshRequiresTheSameSelectedProfile()
        {
            BridgeDisplayProfile active = MakeDisplayProfile(16f / 9f, 50f);
            var unchanged = new BridgeDisplaySnapshot(4, new uint[] { 2 }, new[] { active });
            Assert.IsTrue(BridgeWorkerQuiltOutput.TryMatchRefreshedDisplay(active, 2, 2, unchanged, out string error), error);

            var removed = new BridgeDisplaySnapshot(5, new uint[0], new BridgeDisplayProfile[0]);
            Assert.IsFalse(BridgeWorkerQuiltOutput.TryMatchRefreshedDisplay(active, 2, 2, removed, out _));
            Assert.IsFalse(BridgeWorkerQuiltOutput.TryMatchRefreshedDisplay(active, 2, 3, unchanged, out _));

            BridgeDisplayProfile changed = MakeDisplayProfile(16f / 9f, 45f);
            var changedSnapshot = new BridgeDisplaySnapshot(6, new uint[] { 2 }, new[] { changed });
            Assert.IsFalse(BridgeWorkerQuiltOutput.TryMatchRefreshedDisplay(active, 2, 2, changedSnapshot, out _));
        }

        [Test]
        public void CachedDisplayCalibrationIsBoundToItsRuntimeApplicationAndOrdinal()
        {
            Assert.IsTrue(BridgeWorkerQuiltOutput.IsConnectedSnapshotConfigurationCurrent(false,
                @"C:\Bridge", "ViewWeave", 2, @"C:\Bridge", "ViewWeave", 2));
            Assert.IsFalse(BridgeWorkerQuiltOutput.IsConnectedSnapshotConfigurationCurrent(false,
                @"C:\Bridge", "ViewWeave", 2, @"C:\OtherBridge", "ViewWeave", 2));
            Assert.IsFalse(BridgeWorkerQuiltOutput.IsConnectedSnapshotConfigurationCurrent(false,
                @"C:\Bridge", "ViewWeave", 2, @"C:\Bridge", "OtherApp", 2));
            Assert.IsFalse(BridgeWorkerQuiltOutput.IsConnectedSnapshotConfigurationCurrent(false,
                @"C:\Bridge", "ViewWeave", 2, @"C:\Bridge", "ViewWeave", 3));
            Assert.IsFalse(BridgeWorkerQuiltOutput.IsConnectedSnapshotConfigurationCurrent(true,
                @"C:\Bridge", "ViewWeave", 2, @"C:\Bridge", "ViewWeave", 2));
        }

        static BridgeDisplayProfile MakeDisplayProfile(float aspect, float viewcone)
        {
            var display = new BridgeNative.DisplayInfo
            {
                index = 7, width = 3840, height = 2160, aspect = aspect, viewcone = viewcone,
                quiltWidth = 4096, quiltHeight = 4096, quiltColumns = 8, quiltRows = 6
            };
            var physical = new BridgeWorkerNative.PhysicalDisplayInfoV1
            {
                structSize = 24, version = 1, pixelWidth = 3840, pixelHeight = 2160, dpi = 283f, valid = 1
            };
            return new BridgeDisplayProfile(display, physical);
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
            Type nativeType = typeof(BridgeWorkerQuiltOutput).Assembly.GetType("ViewWeave.BridgeWorkerNative", true);
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

        static void RegisterSyntheticSessionOwner(ViewWeaveBridgeSession session)
        {
            Assert.IsNotNull(session);
            ViewWeaveRig rig = session.GetComponent<ViewWeaveRig>();
            Assert.IsNotNull(rig);
            BridgeWorkerQuiltOutput output = session.GetComponent<BridgeWorkerQuiltOutput>();
            Assert.IsNotNull(output);
            QuiltRenderer renderer = rig.Renderer != null ? rig.Renderer : rig.GetComponent<QuiltRenderer>();
            Assert.IsNotNull(renderer);
            renderer.SetMutationOwnerRequired(true);
            IDisposable registration = QuiltRendererMutationPolicy.RegisterOwner(renderer, session,
                _ => string.Empty, out string error);
            Assert.IsNotNull(registration, error);
            FieldInfo field = typeof(ViewWeaveBridgeSession).GetField("mutationOwnerRegistration", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            field.SetValue(session, registration);

            SetSessionSnapshot(session, "sourceCameraAtStart", renderer.sourceCamera);
            SetSessionSnapshot(session, "referenceCameraAtStart", renderer.referenceCamera);
            SetSessionSnapshot(session, "guardAtStart", renderer.GuardBehaviour);
            SetSessionSnapshot(session, "outputBehaviourAtStart", renderer.OutputBehaviour);
            SetSessionSnapshot(session, "useRenderRequestsAtStart", renderer.useRenderRequests);
            SetSessionSnapshot(session, "outputRuntimePathAtStart", output.runtimePath ?? string.Empty);
            SetSessionSnapshot(session, "outputApplicationNameAtStart", output.applicationName ?? string.Empty);
            SetSessionSnapshot(session, "outputDisplayOrdinalAtStart", output.displayOrdinal);
            SetSessionSnapshot(session, "profileRuntimePathAtStart", session.runtimePath ?? string.Empty);
            SetSessionSnapshot(session, "profileApplicationNameAtStart", session.applicationName ?? string.Empty);
            SetSessionSnapshot(session, "profileDisplayOrdinalAtStart", session.displayOrdinal);
        }

        static void SetSessionSnapshot(ViewWeaveBridgeSession session, string name, object value)
        {
            FieldInfo field = typeof(ViewWeaveBridgeSession).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, name);
            field.SetValue(session, value);
        }

        static bool IsAscii(string value)
        {
            if (value == null) return true;
            foreach (char valueChar in value) if (valueChar > 0x7e) return false;
            return true;
        }
    }
}
