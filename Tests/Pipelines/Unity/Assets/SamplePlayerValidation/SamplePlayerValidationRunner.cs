using System;
using System.Collections;
using System.IO;
using ViewWeave;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave.SamplePlayerValidation
{
    public sealed class SamplePlayerValidationRunner : MonoBehaviour
    {
        [SerializeField] Camera sourceCamera;
        [SerializeField] QuiltRenderer quilt;
        [SerializeField] BridgeWorkerQuiltOutput output;
        [SerializeField] ViewWeaveBridgeSession session;
        [SerializeField] ViewWeaveRig rig;
        [SerializeField] bool hdrp;
        [SerializeField] Transform movementTarget;
        [SerializeField] int requiredDraws = 120;
        [SerializeField] string quality = "preview";
        [SerializeField] string evidenceDirectory;
        Evidence evidence;
        string argumentError;

        void Awake()
        {
            Application.runInBackground = true;
            evidenceDirectory = Argument("--evidence-dir") ?? Path.Combine(Application.dataPath, "..", "..", ".local", "sample-player-validation");
            evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            quality = (Argument("--quality") ?? quality).ToLowerInvariant();
            if (quality != "preview" && quality != "balanced" && quality != "native") argumentError = "invalid --quality";
            evidence = new Evidence
            {
                buildGUID = Application.buildGUID,
                isEditor = Application.isEditor,
                development = Debug.isDebugBuild,
                graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                unityVersion = Application.unityVersion,
                pipeline = hdrp ? "hdrp" : "urp",
                quality = quality,
                ok = true
            };
            Directory.CreateDirectory(evidenceDirectory);
        }

        void Start()
        {
            StartCoroutine(RunGuarded());
        }

        IEnumerator RunGuarded()
        {
            yield return Drive(Run());
            PrepareCleanup();
            yield return CleanupWait();
            WriteEvidence();
            Application.Quit(evidence.ok ? 0 : 1);
        }

        IEnumerator Run()
        {
            if (Application.platform != RuntimePlatform.WindowsPlayer || Application.isEditor || !Debug.isDebugBuild || !SystemInfo.graphicsMultiThreaded || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11)
                throw new InvalidOperationException("Windows Development Player with D3D11 graphicsMultiThreaded is required");
            if (!string.IsNullOrEmpty(argumentError)) throw new ArgumentException(argumentError);
            if (requiredDraws < 1) throw new ArgumentException("requiredDraws must be positive");
            var activePipeline = GraphicsSettings.currentRenderPipeline;
            if (activePipeline == null || activePipeline.GetType().FullName.IndexOf(hdrp ? "HighDefinition" : "Universal", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("active render pipeline does not match the sample");
            evidence.pipelineAsset = activePipeline.GetType().FullName;
            if (sourceCamera == null || quilt == null || output == null || session == null || rig == null)
                throw new InvalidOperationException("Sample player validation references are incomplete");
            var material = Resources.Load<Material>(hdrp ? "ViewWeave/HdrpLit" : "ViewWeave/UrpLit");
            if (material == null) throw new InvalidOperationException("sample-owned Lit material resource is missing");
            if (sourceCamera.enabled) throw new InvalidOperationException("source camera must be disabled by the Builder");
            evidence.sourceCameraDisabled = !sourceCamera.enabled;
            if (!evidence.sourceCameraDisabled) throw new InvalidOperationException("source camera is enabled");
            quilt.autoRender = false;
            session.startOnEnable = false;
            output.initializeOnEnable = false;
            var preset = quality == "native" ? QuiltQualityPreset.Native : quality == "balanced" ? QuiltQualityPreset.Balanced : QuiltQualityPreset.Preview;
            if (!session.SetQuality(preset) || !session.StartSession())
                throw new InvalidOperationException(FirstError(session.LastError, output.LastError, "Bridge session start failed"));
            double deadline = Time.realtimeSinceStartupAsDouble + 120.0;
            while (session.State != ViewWeaveBridgeSession.SessionState.Rendering && Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (session.State == ViewWeaveBridgeSession.SessionState.Fault) throw new InvalidOperationException(FirstError(session.LastError, output.LastError, "Bridge session fault"));
                yield return null;
            }
            if (session.State != ViewWeaveBridgeSession.SessionState.Rendering) throw new TimeoutException("Bridge session did not become rendering");
            var displayProfile = session.Profile;
            var requestedProfile = ViewWeaveBridgeSession.CreateQualityProfile(displayProfile, preset);
            evidence.nativeQuiltWidth = displayProfile.QuiltWidth;
            evidence.nativeQuiltHeight = displayProfile.QuiltHeight;
            evidence.nativeColumns = displayProfile.Columns;
            evidence.nativeRows = displayProfile.Rows;
            evidence.nativeViews = displayProfile.Columns * displayProfile.Rows;
            evidence.profile = displayProfile.ToString();
            if (evidence.nativeQuiltWidth < 1 || evidence.nativeQuiltHeight < 1 || evidence.nativeViews < 1) throw new InvalidOperationException("Bridge profile dimensions are invalid");
            var generatedName = hdrp ? "ViewWeave HDRP Lit Sample Content" : "ViewWeave URP Lit Sample Content";
            var generated = GameObject.Find(generatedName);
            if (generated == null) throw new InvalidOperationException("sample generator did not create content");
            var red = generated.transform.Find("Red cube");
            var green = generated.transform.Find("Green sphere");
            if (red == null || green == null || Mathf.Abs(red.localPosition.z - green.localPosition.z) < 0.01f) throw new InvalidOperationException("sample depth objects are missing or coincident");
            movementTarget = green;
            quilt.autoRender = true;
            deadline = Time.realtimeSinceStartupAsDouble + 120.0;
            while (output.Completed < requiredDraws && Time.realtimeSinceStartupAsDouble < deadline)
            {
                if (output.State == BridgeWorkerQuiltOutput.WorkerState.Fault || output.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                    throw new InvalidOperationException(FirstError(session.LastError, output.LastError, "Bridge worker stopped"));
                yield return null;
            }
            if (output.Completed < requiredDraws) throw new TimeoutException("Bridge draw completion timeout");
            evidence.before = ReadDigest("before.png");
            if (sourceCamera.enabled) throw new InvalidOperationException("source camera became enabled during capture");
            if (quilt.settings.quiltWidth != requestedProfile.QuiltWidth || quilt.settings.quiltHeight != requestedProfile.QuiltHeight || quilt.settings.columns != requestedProfile.Columns || quilt.settings.rows != requestedProfile.Rows || quilt.settings.views != requestedProfile.Views)
                throw new InvalidOperationException("quilt settings do not match the requested quality profile");
            var beforePosition = movementTarget == null ? Vector3.zero : movementTarget.localPosition;
            if (movementTarget != null) movementTarget.localPosition += Vector3.right * 0.15f;
            var moveFrames = 0;
            var startDraws = output.Completed;
            deadline = Time.realtimeSinceStartupAsDouble + 30.0;
            while ((moveFrames < 4 || output.Completed < startDraws + 4) && Time.realtimeSinceStartupAsDouble < deadline)
            {
                moveFrames++;
                yield return null;
            }
            if (output.Completed < startDraws + 4) throw new TimeoutException("Bridge movement completion timeout");
            evidence.after = ReadDigest("after.png");
            if (sourceCamera.enabled) throw new InvalidOperationException("source camera became enabled after capture");
            if (movementTarget != null) movementTarget.localPosition = beforePosition;
            evidence.submissions = output.Submissions;
            evidence.completed = output.Completed;
            evidence.movementChanged = evidence.before.checksum != evidence.after.checksum;
            evidence.viewDifference = evidence.before.edgeDifference && evidence.after.edgeDifference;
            evidence.ok = output.Completed >= requiredDraws && evidence.before.allTilesNonBlank && evidence.after.allTilesNonBlank && evidence.before.colorEvidence && evidence.after.colorEvidence && evidence.viewDifference && evidence.movementChanged;
            if (!evidence.ok) throw new InvalidOperationException("quilt evidence did not meet nonblank, color, view, and movement requirements");
        }

        IEnumerator StopSession()
        {
            if (quilt != null) quilt.autoRender = false;
            if (session != null) session.StopSession();
            var deadline = Time.realtimeSinceStartupAsDouble + 30.0;
            while (session != null && session.State != ViewWeaveBridgeSession.SessionState.Idle && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            evidence.cleanupPending = BridgeWorkerQuiltOutput.ShutdownPendingCount;
            evidence.cleanupOk = session == null || (session.State == ViewWeaveBridgeSession.SessionState.Idle && evidence.cleanupPending == 0);
            evidence.ok &= evidence.cleanupOk;
        }

        void PrepareCleanup()
        {
            if (quilt != null) quilt.autoRender = false;
            if (session != null) session.StopSession();
        }

        IEnumerator CleanupWait()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + 30.0;
            while ((session != null && session.State != ViewWeaveBridgeSession.SessionState.Idle || BridgeWorkerQuiltOutput.ShutdownPendingCount > 0) && Time.realtimeSinceStartupAsDouble < deadline)
                yield return null;
            evidence.cleanupPending = BridgeWorkerQuiltOutput.ShutdownPendingCount;
            evidence.cleanupOk = (session == null || session.State == ViewWeaveBridgeSession.SessionState.Idle) && evidence.cleanupPending == 0;
            evidence.ok &= evidence.cleanupOk;
        }

        IEnumerator Drive(IEnumerator iterator)
        {
            try
            {
                while (true)
                {
                    bool moved;
                    try { moved = iterator.MoveNext(); }
                    catch (Exception exception) { evidence.ok = false; evidence.error = Sanitize(exception.GetType().Name + ": " + exception.Message); Debug.LogError("Sample player validation failed: " + evidence.error); yield break; }
                    if (!moved) yield break;
                    var nested = iterator.Current as IEnumerator;
                    if (nested != null) yield return Drive(nested);
                    else yield return iterator.Current;
                }
            }
            finally
            {
                var disposable = iterator as IDisposable;
                if (disposable != null) disposable.Dispose();
            }
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; ++i)
                if (chars[i] < 0x20 || chars[i] > 0x7e) chars[i] = '?';
            return new string(chars);
        }

        Digest ReadDigest(string fileName)
        {
            if (quilt.QuiltTexture == null) throw new InvalidOperationException("quilt texture is missing");
            var previous = RenderTexture.active;
            var image = new Texture2D(quilt.QuiltTexture.width, quilt.QuiltTexture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = quilt.QuiltTexture;
                image.ReadPixels(new Rect(0, 0, image.width, image.height), 0, 0);
                image.Apply(false, false);
                var pixels = image.GetPixels32();
                int columns = Mathf.Max(1, quilt.settings.columns);
                int rows = Mathf.Max(1, quilt.settings.rows);
                int tileWidth = Mathf.Max(1, image.width / columns);
                int tileHeight = Mathf.Max(1, image.height / rows);
                int nonBlankTiles = 0;
                int colorTiles = 0;
                for (int row = 0; row < rows; ++row)
                for (int column = 0; column < columns; ++column)
                {
                    int nonBlank = 0;
                    int red = 0;
                    int green = 0;
                    for (int y = 0; y < tileHeight; ++y)
                    for (int x = 0; x < tileWidth; ++x)
                    {
                        var pixel = pixels[(row * tileHeight + y) * image.width + column * tileWidth + x];
                        if (pixel.a > 8 && (pixel.r > 12 || pixel.g > 12 || pixel.b > 12)) nonBlank++;
                        if (pixel.r > pixel.g * 1.2f && pixel.r > pixel.b * 1.2f) red++;
                        if (pixel.g > pixel.r * 1.2f && pixel.g > pixel.b * 1.2f) green++;
                    }
                    if (nonBlank > 20) nonBlankTiles++;
                    if (red > 4 && green > 4) colorTiles++;
                }
                var digest = new Digest
                {
                    checksum = HashPixels(pixels),
                    allTilesNonBlank = nonBlankTiles == columns * rows,
                    colorEvidence = colorTiles == columns * rows,
                    edgeDifference = HasTileDifference(pixels, image.width, image.height, columns, rows)
                };
                File.WriteAllBytes(Path.Combine(evidenceDirectory, fileName), image.EncodeToPNG());
                return digest;
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(image);
            }
        }

        static bool HasTileDifference(Color32[] pixels, int width, int height, int columns, int rows)
        {
            if (columns < 2 || rows < 1) return false;
            int tileWidth = Mathf.Max(1, width / columns);
            int tileHeight = Mathf.Max(1, height / rows);
            for (int row = 0; row < rows; ++row)
            for (int column = 1; column < columns; ++column)
            {
                int changed = 0;
                for (int y = 0; y < tileHeight; ++y)
                for (int x = 0; x < tileWidth; ++x)
                {
                    var left = pixels[(row * tileHeight + y) * width + (column - 1) * tileWidth + x];
                    var right = pixels[(row * tileHeight + y) * width + column * tileWidth + x];
                    if (Mathf.Abs(left.r - right.r) + Mathf.Abs(left.g - right.g) + Mathf.Abs(left.b - right.b) > 24) changed++;
                }
                if (changed > 20) return true;
            }
            return false;
        }

        static string HashPixels(Color32[] pixels)
        {
            unchecked
            {
                uint hash = 2166136261u;
                foreach (var pixel in pixels) { hash = (hash ^ pixel.r) * 16777619u; hash = (hash ^ pixel.g) * 16777619u; hash = (hash ^ pixel.b) * 16777619u; }
                return hash.ToString("X8");
            }
        }

        void WriteEvidence()
        {
            File.WriteAllText(Path.Combine(evidenceDirectory, "summary.json"), JsonUtility.ToJson(evidence, true));
        }

        static string FirstError(string first, string second, string fallback)
        {
            return !string.IsNullOrEmpty(first) ? first : !string.IsNullOrEmpty(second) ? second : fallback;
        }

        static string Argument(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1];
            return null;
        }

        [Serializable] struct Digest { public string checksum; public bool allTilesNonBlank; public bool colorEvidence; public bool edgeDifference; }
        [Serializable] sealed class Evidence
        {
            public bool isEditor, development, graphicsMultiThreaded, sourceCameraDisabled, ok, cleanupOk, movementChanged, viewDifference;
            public string buildGUID, graphicsDeviceType, unityVersion, pipeline, pipelineAsset, quality, profile, error;
            public uint submissions, completed;
            public int cleanupPending;
            public int nativeQuiltWidth, nativeQuiltHeight, nativeColumns, nativeRows, nativeViews;
            public Digest before, after;
        }
    }
}
