using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave.HDRP;

namespace ViewWeave.MacHdrpHistoryValidation
{
    // Dedicated Player fixture: no Bridge transport or physical-device claim.
    public sealed class MacHdrpHistoryValidationRunner : MonoBehaviour
    {
        public QuiltRenderer quilt;
        public HdrpQuiltCamera guard;
        public Volume volume;
        public Light fixtureLight;
        [Serializable] public sealed class Report
        {
            public bool passed;
            public string unityVersion, graphicsApi, error, budgetDiagnostic, historyStatus;
            public bool development, cameraIdentityStable, taaRetained, budgetStopped;
            public int renderedViews, nonBlackPixels, redPixels, magentaPixels, stoppedViews = -1, remainingMeterRecords = -1;
            public float fixedEV, lowLightEV, highLightEV;
            public float lowLightSeconds, highLightSeconds;
            public float normalLowRed, normalHighRed, quiltLowRed, quiltHighRed;
            public long driverBytes, textureEstimateBytes;
        }
        readonly Report report = new Report();

        IEnumerator Start()
        {
            report.unityVersion = Application.unityVersion;
            report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.development = Debug.isDebugBuild;
            report.driverBytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
            var run = Verify();
            while (true)
            {
                bool moved;
                try { moved = run.MoveNext(); }
                catch (Exception error) { Finish(false, error.ToString()); yield break; }
                if (!moved) break;
                yield return run.Current;
            }
            Finish(true, null);
        }

        IEnumerator Verify()
        {
            Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal, "Metal is required");
            Require(QualitySettings.activeColorSpace == ColorSpace.Linear, "Linear is required");
            for (int i = 0; i < 6; ++i) yield return null;
            quilt.RenderQuilt();
            report.renderedViews = quilt.LastRenderedViewCount;
            report.fixedEV = guard.SharedExposureEV;
            Require(report.renderedViews == 4 && guard.HistoryCameraCount == 4, "Four histories were not rendered");
            Require(Mathf.Abs(report.fixedEV - 6.5f) < .05f, "Fixed exposure compensation mismatch");
            var identities = new Camera[4];
            for (int i = 0; i < identities.Length; ++i)
            {
                identities[i] = guard.GetHistoryCamera(i);
                Require(identities[i] != null && identities[i] != quilt.sourceCamera, "Missing independent view camera");
                for (int j = 0; j < i; ++j) Require(identities[i] != identities[j], "Shared view camera identity");
                var data = identities[i].GetComponent<HDAdditionalCameraData>();
                Require(data.hasPersistentHistory && data.antialiasing == HDAdditionalCameraData.AntialiasingMode.TemporalAntialiasing, "HDRP history/TAA settings were lost");
            }
            report.taaRetained = true;
            yield return null;
            quilt.RenderQuilt();
            for (int i = 0; i < identities.Length; ++i) Require(identities[i] == guard.GetHistoryCamera(i), "View identity changed between frames");
            report.cameraIdentityStable = true;
            report.nonBlackPixels = InspectFixedImage(quilt.QuiltTexture);
            Require(report.nonBlackPixels > 20, "Quilt is black");
            Require(report.redPixels > 20, "Expected red Lit geometry is missing");
            Require(report.magentaPixels < 20, "Quilt contains magenta error-shader pixels");

            Require(volume.sharedProfile.TryGet(out Exposure exposure), "Exposure component is missing");
            exposure.fixedExposure.Override(10f);
            exposure.compensation.Override(0f);
            var normalTarget = new RenderTexture(128, 96, 24, RenderTextureFormat.ARGBHalf);
            normalTarget.Create();
            try
            {
                fixtureLight.intensity = 100f;
                for (int i = 0; i < 4; ++i) { yield return null; quilt.RenderQuilt(); }
                RenderPipeline.SubmitRenderRequest(quilt.referenceCamera, new RenderPipeline.StandardRequest { destination = normalTarget });
                report.normalLowRed = MeanRed(normalTarget);
                report.quiltLowRed = MeanRed(quilt.QuiltTexture);
                fixtureLight.intensity = 1600f;
                for (int i = 0; i < 4; ++i) { yield return null; quilt.RenderQuilt(); }
                RenderPipeline.SubmitRenderRequest(quilt.referenceCamera, new RenderPipeline.StandardRequest { destination = normalTarget });
                report.normalHighRed = MeanRed(normalTarget);
                report.quiltHighRed = MeanRed(quilt.QuiltTexture);
                Require(report.normalHighRed > report.normalLowRed * 2f, "Ordinary HDRP Camera did not respond to fixture lighting");
                // Quilt output is LDR, unlike the half-float ordinary Camera control.
                Require(report.quiltHighRed > report.quiltLowRed * 1.2f, "HDRP Quilt did not respond to fixture lighting");
            }
            finally { normalTarget.Release(); Destroy(normalTarget); }
            exposure.mode.Override(ExposureMode.AutomaticHistogram);
            exposure.compensation.Override(0f);
            fixtureLight.intensity = 100f;
            // GPU initialization/readback latency cannot be bounded by a fixed CPU frame count.
            float started = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - started < 2f || !guard.SharedExposureReady)
            {
                Require(Time.realtimeSinceStartup - started < 15f, "Automatic exposure warmup timed out: " + guard.HistoryStatus);
                quilt.RenderQuilt();
                yield return null;
            }
            report.lowLightSeconds = Time.realtimeSinceStartup - started;
            Require(guard.SharedExposureReady, "Automatic exposure did not become ready: " + guard.HistoryStatus);
            report.lowLightEV = guard.SharedExposureEV;
            fixtureLight.intensity = 1600f;
            started = Time.realtimeSinceStartup;
            do
            {
                Require(Time.realtimeSinceStartup - started < 15f, "Automatic exposure response timed out: " + guard.HistoryStatus);
                quilt.RenderQuilt();
                yield return null;
            }
            while (Time.realtimeSinceStartup - started < .5f || !guard.SharedExposureReady || guard.SharedExposureEV <= report.lowLightEV + 1f);
            report.highLightSeconds = Time.realtimeSinceStartup - started;
            report.highLightEV = guard.SharedExposureEV;
            Require(report.highLightEV > report.lowLightEV + 1f, "Automatic exposure did not follow fixture lighting");
            report.textureEstimateBytes = guard.HistoryTextureMemoryEstimateBytes;
            Require(report.textureEstimateBytes > 0, "Texture memory estimate is unavailable");

            quilt.ReleasePipelineRenderResources();
            started = Time.realtimeSinceStartup;
            while (HdrpSharedExposureMeter.ResourceRecordCount != 0 && Time.realtimeSinceStartup - started < 15f) yield return null;
            report.stoppedViews = guard.HistoryCameraCount;
            report.remainingMeterRecords = HdrpSharedExposureMeter.ResourceRecordCount;
            Require(report.stoppedViews == 0 && report.remainingMeterRecords == 0, "Stop did not retire histories/meter resources");
            guard.historyMemoryBudgetMB = 1;
            quilt.settings.quiltWidth = 1024;
            quilt.settings.quiltHeight = 1024;
            try { quilt.RenderQuilt(); }
            catch (InsufficientMemoryException error) { report.budgetStopped = true; report.budgetDiagnostic = error.Message; }
            Require(report.budgetStopped, "Warmup resource budget did not stop rendering");
            Require(guard.HistoryCameraCount == 0, "Budget failure retained view cameras");
            started = Time.realtimeSinceStartup;
            while (HdrpSharedExposureMeter.ResourceRecordCount != 0 && Time.realtimeSinceStartup - started < 15f) yield return null;
            report.stoppedViews = guard.HistoryCameraCount;
            report.remainingMeterRecords = HdrpSharedExposureMeter.ResourceRecordCount;
            Require(report.remainingMeterRecords == 0, "Budget failure left pending meter resources");
        }

        int InspectFixedImage(RenderTexture source)
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                texture.Apply();
                int count = 0;
                foreach (var pixel in texture.GetPixels32())
                {
                    if (pixel.r > 8 || pixel.g > 8 || pixel.b > 8) count++;
                    if (pixel.r > 20 && pixel.r > pixel.g * 1.25f && pixel.r > pixel.b * 1.25f) report.redPixels++;
                    if (pixel.r > 40 && pixel.b > 40 && pixel.g < 10) report.magentaPixels++;
                }
                string destination = Argument("-validationReport");
                if (!string.IsNullOrEmpty(destination)) File.WriteAllBytes(Path.ChangeExtension(destination, ".fixed.png"), texture.EncodeToPNG());
                return count;
            }
            finally { RenderTexture.active = previous; Destroy(texture); }
        }

        static float MeanRed(RenderTexture source)
        {
            var previous = RenderTexture.active;
            var texture = new Texture2D(source.width, source.height, TextureFormat.RGBAFloat, false, true);
            try
            {
                RenderTexture.active = source;
                texture.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                texture.Apply();
                float total = 0f;
                var pixels = texture.GetPixels();
                foreach (var pixel in pixels) total += pixel.r;
                return total / pixels.Length;
            }
            finally { RenderTexture.active = previous; Destroy(texture); }
        }

        void Finish(bool passed, string error)
        {
            report.historyStatus = guard.HistoryStatus;
            quilt.ReleasePipelineRenderResources();
            report.passed = passed;
            report.error = error;
            string destination = Argument("-validationReport");
            string json = JsonUtility.ToJson(report, true);
            if (string.IsNullOrEmpty(destination)) { Debug.LogError("-validationReport is required"); Application.Quit(1); return; }
            File.WriteAllText(destination, json);
            if (passed) Debug.Log("ViewWeave HDRP Player validation passed");
            else Debug.LogError("ViewWeave HDRP Player validation failed: " + error);
            Application.Quit(passed ? 0 : 1);
        }

        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; ++i) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
