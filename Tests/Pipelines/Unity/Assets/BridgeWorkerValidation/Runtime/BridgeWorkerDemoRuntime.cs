using System;
using System.IO;
using ViewWeave;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave.BridgeWorkerValidation
{
    public sealed class BridgeWorkerDemoRuntime : MonoBehaviour
    {
        [Serializable]
        sealed class Receipt
        {
            public string kind;
            public string state;
            public string outputState;
            public uint completed;
            public uint submissions;
            public int quiltWidth;
            public int quiltHeight;
            public int views;
            public int columns;
            public int rows;
            public int nativeQuiltWidth;
            public int nativeQuiltHeight;
            public int nativeColumns;
            public int nativeRows;
            public int nativeViews;
            public float nativeAspect;
            public float nativeViewcone;
            public string buildGUID;
            public string unityVersion;
            public string graphicsDeviceType;
            public string pipelineFamily;
            public string pipelineAssetName;
            public bool isEditor;
            public bool development;
            public bool graphicsMultiThreaded;
            public bool sourceEnabled;
            public string quiltProofPng;
            public int proofTileCount;
            public int proofTilesWithRed;
            public int proofTilesWithGreen;
            public int proofViewDifferencePixels;
            public bool proofValid;
            public string proofError;
            public string lastAutoRenderError;
        }

        [SerializeField] ViewWeaveBridgeSession session;
        [SerializeField] BridgeWorkerQuiltOutput output;
        [SerializeField] QuiltRenderer quilt;
        [SerializeField] BridgeWorkerViewIndexOutput indexOutput;
        [SerializeField] ViewWeaveRig rig;
        string evidencePath;
        string lastState;
        bool wroteLive;
        bool wroteExit;
        bool indexOutputConnected;

        void Awake()
        {
            evidencePath = Environment.GetEnvironmentVariable("VIEWWEAVE_DEMO_EVIDENCE_DIR");
            if (string.IsNullOrEmpty(evidencePath)) evidencePath = Argument("--evidence-dir");
            if (string.IsNullOrEmpty(evidencePath)) evidencePath = Path.Combine(Application.dataPath, "..", "..", ".local", "windows-sdk-demo");
            evidencePath = Path.GetFullPath(evidencePath);
            Directory.CreateDirectory(evidencePath);
        }

        void Update()
        {
            if (session == null || output == null) return;
            bool outputNeedsReconnect = indexOutput != null && (quilt == null || quilt.OutputBehaviour != indexOutput || (rig != null && rig.Renderer != null && rig.Renderer.OutputBehaviour != indexOutput));
            if (session.State == ViewWeaveBridgeSession.SessionState.Rendering && indexOutput != null && (!indexOutputConnected || outputNeedsReconnect))
            {
                indexOutput.SetTarget(output);
                if (rig != null) rig.SetOutput(indexOutput);
                quilt.OutputBehaviour = indexOutput;
                indexOutputConnected = true;
            }
            else if (session.State != ViewWeaveBridgeSession.SessionState.Rendering)
            {
                indexOutputConnected = false;
            }
            if (session.State == ViewWeaveBridgeSession.SessionState.Rendering && quilt != null)
                quilt.autoRender = true;
            if (!wroteLive && session.State == ViewWeaveBridgeSession.SessionState.Rendering && output.Completed >= 120u)
                WriteLiveReceipt();
            string state = session.State + "/" + output.State;
            if (state == lastState) return;
            lastState = state;
            if (session.State == ViewWeaveBridgeSession.SessionState.Rendering || session.State == ViewWeaveBridgeSession.SessionState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Fault)
                Debug.Log("bridge_demo_state=" + state + " completed=" + output.Completed + " submissions=" + output.Submissions);
        }

        void OnApplicationQuit() { WriteExitReceipt(); }

        void WriteLiveReceipt()
        {
            wroteLive = true;
            File.WriteAllText(Path.Combine(evidencePath, "demo-live.json"), JsonUtility.ToJson(CreateReceipt("live")));
        }

        void WriteExitReceipt()
        {
            if (wroteExit) return;
            wroteExit = true;
            File.WriteAllText(Path.Combine(evidencePath, "demo-exit.json"), JsonUtility.ToJson(CreateReceipt("exit")));
        }

        Receipt CreateReceipt(string kind)
        {
            Receipt receipt = new Receipt
            {
                kind = kind,
                state = StateText(),
                outputState = OutputStateText(),
                completed = Completed(),
                submissions = Submissions(),
                lastAutoRenderError = SanitizeError(quilt == null ? string.Empty : quilt.LastAutoRenderError)
            };
            if (quilt != null && quilt.QuiltTexture != null)
            {
                receipt.quiltWidth = quilt.QuiltTexture.width;
                receipt.quiltHeight = quilt.QuiltTexture.height;
                receipt.views = quilt.settings.views;
                receipt.columns = quilt.settings.columns;
                receipt.rows = quilt.settings.rows;
            }
            BridgeDisplayProfile profile = session == null ? default : session.Profile;
            receipt.nativeQuiltWidth = profile.QuiltWidth;
            receipt.nativeQuiltHeight = profile.QuiltHeight;
            receipt.nativeColumns = profile.Columns;
            receipt.nativeRows = profile.Rows;
            receipt.nativeViews = profile.Columns * profile.Rows;
            receipt.nativeAspect = profile.Aspect;
            receipt.nativeViewcone = profile.Viewcone;
            RenderPipelineAsset pipeline = GraphicsSettings.currentRenderPipeline;
            string pipelineType = pipeline == null ? string.Empty : pipeline.GetType().FullName;
            receipt.buildGUID = Application.buildGUID;
            receipt.unityVersion = Application.unityVersion;
            receipt.graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString();
            receipt.pipelineFamily = pipelineType.IndexOf("HighDefinition", StringComparison.OrdinalIgnoreCase) >= 0 ? "hdrp" : pipelineType.IndexOf("Universal", StringComparison.OrdinalIgnoreCase) >= 0 ? "urp" : "unknown";
            receipt.pipelineAssetName = pipeline == null ? string.Empty : pipeline.name;
            receipt.isEditor = Application.isEditor;
            receipt.development = Debug.isDebugBuild;
            receipt.graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded;
            receipt.sourceEnabled = quilt == null || quilt.sourceCamera == null || quilt.sourceCamera.enabled;
            if (kind == "live" && quilt != null && quilt.QuiltTexture != null)
            {
                CaptureProof(receipt, quilt.QuiltTexture, quilt.settings.GetLayout());
                if (indexOutput != null && indexOutput.IndexedQuilt != null) CaptureIndexedProof(indexOutput.IndexedQuilt);
            }
            return receipt;
        }

        void CaptureIndexedProof(RenderTexture source)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            try
            {
                RenderTexture.active = source;
                image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
                image.Apply(false, false);
                File.WriteAllBytes(Path.Combine(evidencePath, "demo-live-indexed-quilt.png"), image.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(image);
            }
        }

        void CaptureProof(Receipt receipt, RenderTexture source, QuiltLayout layout)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture preview = RenderTexture.GetTemporary(512, 288, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            Texture2D image = new Texture2D(512, 288, TextureFormat.RGBA32, false, false);
            try
            {
                Graphics.Blit(source, preview);
                RenderTexture.active = preview;
                image.ReadPixels(new Rect(0, 0, preview.width, preview.height), 0, 0, false);
                image.Apply(false, false);
                string fileName = "demo-live-quilt.png";
                File.WriteAllBytes(Path.Combine(evidencePath, fileName), image.EncodeToPNG());
                receipt.quiltProofPng = fileName;
                int tileCount = Math.Min(layout.ViewCount, layout.Columns * layout.Rows);
                receipt.proofTileCount = tileCount;
                for (int view = 0; view < tileCount; view++)
                {
                    RectInt tile = layout.GetTile(view);
                    int x0 = tile.x * image.width / layout.QuiltWidth;
                    int y0 = tile.y * image.height / layout.QuiltHeight;
                    int x1 = Math.Max(x0 + 1, (tile.x + tile.width) * image.width / layout.QuiltWidth);
                    int y1 = Math.Max(y0 + 1, (tile.y + tile.height) * image.height / layout.QuiltHeight);
                    int red = 0;
                    int green = 0;
                    for (int y = y0; y < y1 && y < image.height; y++)
                        for (int x = x0; x < x1 && x < image.width; x++)
                        {
                            Color32 pixel = image.GetPixel(x, y);
                            if (pixel.r > 80 && pixel.r > pixel.g * 1.2f) red++;
                            if (pixel.g > 80 && pixel.g > pixel.r * 1.2f) green++;
                        }
                    if (red > 20) receipt.proofTilesWithRed++;
                    if (green > 20) receipt.proofTilesWithGreen++;
                }
                RectInt firstTile = layout.GetTile(0);
                RectInt lastTile = layout.GetTile(Math.Max(0, tileCount - 1));
                int width = Math.Min(firstTile.width, lastTile.width) * image.width / layout.QuiltWidth;
                int height = Math.Min(firstTile.height, lastTile.height) * image.height / layout.QuiltHeight;
                for (int y = 0; y < height; y++)
                    for (int x = 0; x < width; x++)
                    {
                        int ax = firstTile.x * image.width / layout.QuiltWidth + x;
                        int ay = firstTile.y * image.height / layout.QuiltHeight + y;
                        int bx = lastTile.x * image.width / layout.QuiltWidth + x;
                        int by = lastTile.y * image.height / layout.QuiltHeight + y;
                        Color32 a = image.GetPixel(Math.Min(ax, image.width - 1), Math.Min(ay, image.height - 1));
                        Color32 b = image.GetPixel(Math.Min(bx, image.width - 1), Math.Min(by, image.height - 1));
                        if (Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) > 3) receipt.proofViewDifferencePixels++;
                    }
                receipt.proofValid = File.Exists(Path.Combine(evidencePath, fileName)) && receipt.proofTileCount > 0 && receipt.proofTilesWithRed == receipt.proofTileCount && receipt.proofTilesWithGreen == receipt.proofTileCount && receipt.proofViewDifferencePixels > 20;
                if (!receipt.proofValid) receipt.proofError = "quilt proof thresholds were not met";
            }
            catch (Exception error)
            {
                receipt.quiltProofPng = string.Empty;
                receipt.proofValid = false;
                receipt.proofError = "proof capture failed: " + SanitizeError(error.Message);
                receipt.lastAutoRenderError = SanitizeError(error.Message);
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.Destroy(image);
                RenderTexture.ReleaseTemporary(preview);
            }
        }

        string StateText() { return Sanitize(session == null ? "unknown" : session.State.ToString()); }
        string OutputStateText() { return Sanitize(output == null ? "unknown" : output.State.ToString()); }
        uint Completed() { return output == null ? 0u : output.Completed; }
        uint Submissions() { return output == null ? 0u : output.Submissions; }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1];
            return null;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown";
            char[] chars = new char[value.Length];
            int count = 0;
            foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars[count++] = c;
            return new string(chars, 0, count);
        }

        static string SanitizeError(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] chars = new char[value.Length];
            int count = 0;
            foreach (char c in value) if (c >= 0x20 && c <= 0x7e) chars[count++] = c;
            return new string(chars, 0, count);
        }
    }
}
