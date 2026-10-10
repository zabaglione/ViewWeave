using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ViewWeave.URP;

namespace ViewWeave.MacUrpHistoryValidation
{
    // Rendering-only fixture; no Bridge/device or optical validation is claimed.
    public sealed class MacUrpHistoryValidationRunner : MonoBehaviour
    {
        public QuiltRenderer quilt;
        public UrpQuiltCamera guard;
        public Transform bar;
        [Serializable] public sealed class Report
        {
            public bool passed, development, stableCameras, independentTextures, historyAdvanced, sameFrameStable, autoRenderAdvanced, budgetStopped;
            public string unityVersion, graphicsApi, error, budgetDiagnostic;
            public int[] firstVersions = new int[4], lastVersions = new int[4];
            public int changedTaaPixels, changedMotionPixels, greenPixels, stoppedViews = -1;
            public long driverBytes, textureEstimateBytes;
        }
        readonly Report report = new Report();
        IEnumerator Start()
        {
            report.unityVersion = Application.unityVersion; report.graphicsApi = SystemInfo.graphicsDeviceType.ToString();
            report.development = Debug.isDebugBuild; report.driverBytes = Profiler.GetAllocatedMemoryForGraphicsDriver();
            var run = Verify();
            while (true)
            {
                bool moved;
                try { moved = run.MoveNext(); } catch (Exception error) { Finish(false, error.ToString()); yield break; }
                if (!moved) break;
                yield return run.Current;
            }
            Finish(true, null);
        }
        IEnumerator Verify()
        {
            Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Metal, "Metal is required");
            var cameras = new Camera[4]; var histories = new TaaHistory[4]; var textures = new RenderTexture[4];
            for (int i = 0; i < 4; i++) yield return null;
            for (int frame = 0; frame < 16; frame++)
            {
                quilt.RenderQuilt(); Require(quilt.LastRenderedViewCount == 4 && guard.HistoryCameraCount == 4, "Four views were not rendered");
                for (int view = 0; view < 4; view++)
                {
                    var camera = guard.GetHistoryCamera(view); Require(camera != null && !camera.enabled, "Missing disabled history camera");
                    var history = camera.GetComponent<UniversalAdditionalCameraData>().history.GetHistoryForRead<TaaHistory>();
                    Require(history != null && history.GetAccumulationTexture()?.rt != null, "TAA accumulation texture was not created");
                    if (frame == 0)
                    {
                        cameras[view] = camera; histories[view] = history; textures[view] = history.GetAccumulationTexture().rt;
                        report.firstVersions[view] = history.GetAccumulationVersion();
                        for (int j = 0; j < view; j++) Require(textures[view] != textures[j] && histories[view] != histories[j], "View histories were shared");
                    }
                    else Require(cameras[view] == camera && histories[view] == history, "View identity changed across frames");
                    report.lastVersions[view] = history.GetAccumulationVersion();
                }
                yield return null;
            }
            report.stableCameras = report.independentTextures = true;
            for (int view = 0; view < 4; view++) Require(report.lastVersions[view] > report.firstVersions[view], "TAA history did not accumulate");
            report.historyAdvanced = true;
            quilt.RenderQuilt(); int version = histories[0].GetAccumulationVersion();
            quilt.RenderQuilt(); Require(histories[0].GetAccumulationVersion() == version, "Same-frame redraw advanced history version"); report.sameFrameStable = true;
            int autoVersion = version; quilt.autoRender = true;
            for (int i = 0; i < 8; i++) yield return null;
            Require(quilt.autoRender && histories[0].GetAccumulationVersion() > autoVersion, "Automatic Quilt rendering did not advance TAA");
            report.autoRenderAdvanced = true; quilt.autoRender = false;
            Color32[] temporal = Capture("taa");
            foreach (Color32 pixel in temporal) if (pixel.g > 30 && pixel.g > pixel.r * 2 && pixel.g > pixel.b * 2) report.greenPixels++;
            Require(report.greenPixels > 100, "Quilt geometry was missing");
            var settingsData = quilt.referenceCamera.GetComponent<UniversalAdditionalCameraData>();
            settingsData.antialiasing = AntialiasingMode.None;
            for (int i = 0; i < 4; i++) { yield return null; quilt.RenderQuilt(); }
            var control = Capture("off"); report.changedTaaPixels = CountChanged(temporal, control);
            Require(report.changedTaaPixels > 20, "TAA had no meaningful pixel effect");
            settingsData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
            bar.position += new Vector3(.35f, .1f, 0f);
            for (int i = 0; i < 8; i++) { yield return null; quilt.RenderQuilt(); }
            report.changedMotionPixels = CountChanged(temporal, Capture("moved"));
            Require(report.changedMotionPixels > 100, "Moving geometry did not update Quilt pixels");
            report.textureEstimateBytes = guard.HistoryTextureMemoryEstimateBytes;
            Require(report.textureEstimateBytes > 0, "Resource estimates were unavailable");
            quilt.ReleasePipelineRenderResources(); yield return null;
            report.stoppedViews = guard.HistoryCameraCount; Require(report.stoppedViews == 0, "Stop retained histories");
            foreach (Camera camera in cameras) Require(camera == null, "Owned history cameras were not destroyed");
            guard.historyMemoryBudgetMB = 1; quilt.settings.quiltWidth = 1536; quilt.settings.quiltHeight = 1024;
            try { quilt.RenderQuilt(); } catch (InsufficientMemoryException error) { report.budgetStopped = true; report.budgetDiagnostic = error.Message; }
            Require(report.budgetStopped && guard.HistoryCameraCount == 0, "Budget failure did not stop and release histories");
            yield return null;
        }
        Color32[] Capture(string label)
        {
            var previous = RenderTexture.active; var target = quilt.QuiltTexture;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = target; image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0); image.Apply();
                string reportPath = Argument("-validationReport");
                if (!string.IsNullOrEmpty(reportPath)) File.WriteAllBytes(Path.ChangeExtension(reportPath, label + ".png"), image.EncodeToPNG());
                return image.GetPixels32();
            }
            finally { RenderTexture.active = previous; Destroy(image); }
        }
        static int CountChanged(Color32[] left, Color32[] right)
        {
            int count = 0;
            for (int i = 0; i < left.Length; i++)
                if (Math.Abs(left[i].r - right[i].r) + Math.Abs(left[i].g - right[i].g) + Math.Abs(left[i].b - right[i].b) > 8) count++;
            return count;
        }
        void Finish(bool passed, string error)
        {
            quilt.autoRender = false; quilt.ReleasePipelineRenderResources(); report.passed = passed; report.error = error;
            string destination = Argument("-validationReport");
            if (string.IsNullOrEmpty(destination)) { Debug.LogError("-validationReport is required"); Application.Quit(1); return; }
            File.WriteAllText(destination, JsonUtility.ToJson(report, true));
            if (passed) Debug.Log("ViewWeave URP Player validation passed"); else Debug.LogError("ViewWeave URP Player validation failed: " + error);
            Application.Quit(passed ? 0 : 1);
        }
        static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == key) return args[i + 1]; return null;
        }
    }
}
