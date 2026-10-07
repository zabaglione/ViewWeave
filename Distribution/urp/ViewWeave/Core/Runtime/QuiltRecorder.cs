using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ViewWeave
{
    public enum QuiltRecordingStatus
    {
        Idle,
        Recording,
        Complete,
        Cancelled,
        Error
    }

    [Serializable]
    public sealed class QuiltRecordingManifest
    {
        public int fps;
        public int requestedFrameCount;
        public int completedFrameCount;
        public float timeScale;
        public string status;
        public string error;
        public string startedAtUtc;
        public string finishedAtUtc;
        public string frameLog;
    }

    [Serializable]
    public sealed class QuiltRecordingFrame
    {
        public int index;
        public int unityFrame;
        public double simulationTime;
        public float deltaTime;
        public string fileName;
        public string timestampUtc;
    }

    [DisallowMultipleComponent]
    public sealed class QuiltRecorder : MonoBehaviour
    {
        static QuiltRecorder activeRecorder;
        [SerializeField] QuiltRenderer quiltRenderer;

        readonly List<QuiltRecordingFrame> capturedFrames = new List<QuiltRecordingFrame>();
        QuiltSettings startingSettings;
        QuiltViewRenderMode startingViewRenderMode;
        Camera startingSourceCamera;
        QuiltRenderer subscribedRenderer;
        float startingNearClip;
        float startingFarClip;
        float originalCaptureDeltaTime;
        float ownedCaptureDeltaTime;
        float requestedCaptureDeltaTime;
        string startedAtUtc;
        int startFrame;
        int lastCapturedUnityFrame = -1;
        int fps;
        int requestedFrameCount;
        bool ownsCaptureDeltaTime;
        string manifestPath;
        string frameLogPath;
        string recordingDirectory;
        string errorMessage = string.Empty;

        public QuiltRenderer Renderer
        {
            get => quiltRenderer;
            set
            {
                if (IsRecording && !ReferenceEquals(value, quiltRenderer)) throw new InvalidOperationException("The target renderer cannot change while recording.");
                quiltRenderer = value;
            }
        }
        public QuiltRecordingStatus Status { get; private set; }
        public string ErrorMessage => errorMessage;
        public string RecordingDirectory => recordingDirectory;
        public int CompletedFrameCount => capturedFrames.Count;
        public int RequestedFrameCount => requestedFrameCount;
        public int FramesPerSecond => fps;
        public bool IsRecording => Status == QuiltRecordingStatus.Recording;

        void Reset()
        {
            quiltRenderer = GetComponent<QuiltRenderer>();
        }

        void OnEnable()
        {
            if (quiltRenderer == null) quiltRenderer = GetComponent<QuiltRenderer>();
        }

        public void StartRecording(string outputRoot, int framesPerSecond, int frameCount)
        {
            if (!Application.isPlaying) throw new InvalidOperationException("Quilt recording is available only in Play mode.");
            if (!isActiveAndEnabled) throw new InvalidOperationException("The recorder component must be enabled.");
            if (IsRecording) throw new InvalidOperationException("This recorder is already recording.");
            if (activeRecorder != null && activeRecorder.IsRecording) throw new InvalidOperationException("Another Quilt Recorder is already active.");
            if (quiltRenderer == null || !quiltRenderer.isActiveAndEnabled) throw new InvalidOperationException("An enabled Quilt Renderer is required.");
            if (quiltRenderer.sourceCamera == null) throw new InvalidOperationException("The selected Quilt Renderer requires a source camera.");
            if (quiltRenderer.settings == null) throw new InvalidOperationException("The selected Quilt Renderer requires Quilt settings.");
            quiltRenderer.settings.Validate();
            if (!quiltRenderer.autoRender) throw new InvalidOperationException("The selected Quilt Renderer must have Auto Render enabled.");
            if (framesPerSecond < 1 || framesPerSecond > 240) throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "FPS must be between 1 and 240.");
            if (frameCount < 1) throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count must be positive.");
            if (string.IsNullOrWhiteSpace(outputRoot)) throw new ArgumentException("An output folder is required.", nameof(outputRoot));
            if (Time.timeScale != 1f) throw new InvalidOperationException("Quilt recording requires Time.timeScale == 1.");
            if (Time.captureDeltaTime != 0f) throw new InvalidOperationException("Time.captureDeltaTime is already controlled by another capture process.");

            startingSettings = quiltRenderer.settings.Clone();
            startingViewRenderMode = quiltRenderer.viewRenderMode;
            startingSourceCamera = quiltRenderer.sourceCamera;
            startingNearClip = startingSourceCamera.nearClipPlane;
            startingFarClip = startingSourceCamera.farClipPlane;
            fps = framesPerSecond;
            requestedFrameCount = frameCount;
            capturedFrames.Clear();
            errorMessage = string.Empty;
            recordingDirectory = CreateUniqueOutputDirectory(outputRoot);
            manifestPath = Path.Combine(recordingDirectory, "manifest.json");
            frameLogPath = Path.Combine(recordingDirectory, "frames.jsonl");
            originalCaptureDeltaTime = Time.captureDeltaTime;
            requestedCaptureDeltaTime = 1f / framesPerSecond;
            startFrame = Time.frameCount;
            startedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            lastCapturedUnityFrame = -1;
            Status = QuiltRecordingStatus.Recording;
            activeRecorder = this;
            subscribedRenderer = quiltRenderer;
            subscribedRenderer.QuiltRendered += OnQuiltRendered;
            try
            {
                WriteManifest();
                File.WriteAllText(frameLogPath, string.Empty);
                Time.captureDeltaTime = requestedCaptureDeltaTime;
                ownedCaptureDeltaTime = Time.captureDeltaTime;
                ownsCaptureDeltaTime = true;
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            Finish(capturedFrames.Count == requestedFrameCount ? QuiltRecordingStatus.Complete : QuiltRecordingStatus.Cancelled, string.Empty);
        }

        void Update()
        {
            if (!IsRecording) return;
            if (Time.frameCount <= startFrame) return;
            if (Time.timeScale != 1f)
            {
                Finish(QuiltRecordingStatus.Error, "Time.timeScale changed during recording.");
                return;
            }
            if (Time.captureDeltaTime != ownedCaptureDeltaTime)
            {
                Finish(QuiltRecordingStatus.Error, "Time.captureDeltaTime changed during recording.");
                return;
            }
            if (!ReferenceEquals(quiltRenderer, subscribedRenderer))
            {
                Finish(QuiltRecordingStatus.Error, "The selected Quilt Renderer changed during recording.");
                return;
            }
            if (quiltRenderer == null || !quiltRenderer.isActiveAndEnabled)
            {
                Finish(QuiltRecordingStatus.Error, "The Quilt Renderer was disabled or destroyed.");
                return;
            }
            if (!quiltRenderer.autoRender)
            {
                string reason = string.IsNullOrWhiteSpace(quiltRenderer.LastAutoRenderError) ? "Auto Render stopped during recording." : "Auto Render stopped: " + quiltRenderer.LastAutoRenderError;
                Finish(QuiltRecordingStatus.Error, reason);
                return;
            }
            if (!ConfigurationMatchesStart())
            {
                Finish(QuiltRecordingStatus.Error, "The Quilt layout, view density, or geometry settings changed during recording.");
                return;
            }
            int watchdogFrame = lastCapturedUnityFrame < 0 ? startFrame + 1 : lastCapturedUnityFrame + 1;
            if (Time.frameCount > watchdogFrame)
                Finish(QuiltRecordingStatus.Error, "No completed quilt arrived for a recording frame.");
        }

        void OnQuiltRendered(QuiltRenderer rendered)
        {
            if (!IsRecording) return;
            try
            {
                int frame = Time.frameCount;
                if (frame <= startFrame) return;
                if (!ReferenceEquals(rendered, subscribedRenderer) || !ReferenceEquals(rendered, quiltRenderer)) throw new InvalidOperationException("The selected Quilt Renderer changed during recording.");
                if (frame == lastCapturedUnityFrame) throw new InvalidOperationException("Duplicate quilt completion was detected in one Unity frame.");
                if (lastCapturedUnityFrame >= 0 && frame != lastCapturedUnityFrame + 1) throw new InvalidOperationException("A Unity frame completed without a quilt.");
                if (!ConfigurationMatchesStart()) throw new InvalidOperationException("The Quilt layout, view density, or geometry settings changed during recording.");
                if (Time.timeScale != 1f || Time.captureDeltaTime != ownedCaptureDeltaTime) throw new InvalidOperationException("The capture time settings changed during recording.");
                if (Math.Abs(Time.deltaTime - requestedCaptureDeltaTime) > Math.Max(0.000001f, requestedCaptureDeltaTime * 0.0001f))
                    throw new InvalidOperationException("Unity simulation deltaTime does not match the requested capture FPS.");

                QuiltRenderSnapshot snapshot = rendered.LatestSnapshot;
                int index = capturedFrames.Count;
                string stem = "frame_" + index.ToString("D6", CultureInfo.InvariantCulture);
                string fileName = QuiltCaptureUtility.BuildFileName(stem, snapshot);
                string filePath = Path.Combine(recordingDirectory, fileName);
                QuiltCaptureUtility.Save(rendered.QuiltTexture, snapshot, filePath);
                QuiltRecordingFrame capturedFrame = new QuiltRecordingFrame
                {
                    index = index,
                    unityFrame = frame,
                    simulationTime = Time.timeAsDouble,
                    deltaTime = Time.deltaTime,
                    fileName = fileName,
                    timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
                capturedFrames.Add(capturedFrame);
                File.AppendAllText(frameLogPath, JsonUtility.ToJson(capturedFrame) + "\n");
                lastCapturedUnityFrame = frame;
                if (capturedFrames.Count == requestedFrameCount)
                    Finish(QuiltRecordingStatus.Complete, string.Empty);
            }
            catch (Exception error) { Fail(error); }
        }

        void OnDisable()
        {
            if (IsRecording) Finish(QuiltRecordingStatus.Cancelled, "Recording was cancelled because the recorder was disabled.");
        }

        void OnDestroy()
        {
            if (IsRecording) Finish(QuiltRecordingStatus.Cancelled, "Recording was cancelled because the recorder was destroyed.");
        }

        void OnApplicationQuit()
        {
            if (IsRecording) Finish(QuiltRecordingStatus.Cancelled, "Recording was cancelled because Play mode ended.");
        }

        void OnApplicationPause(bool paused)
        {
            if (paused && IsRecording) Finish(QuiltRecordingStatus.Cancelled, "Recording was cancelled because the application was paused.");
        }

        bool ConfigurationMatchesStart()
        {
            if (quiltRenderer == null || quiltRenderer.settings == null) return false;
            QuiltSettings current = quiltRenderer.settings;
            return quiltRenderer.viewRenderMode == startingViewRenderMode
                && current.quiltWidth == startingSettings.quiltWidth
                && current.quiltHeight == startingSettings.quiltHeight
                && current.rows == startingSettings.rows
                && current.columns == startingSettings.columns
                && current.views == startingSettings.views
                && current.viewAspect == startingSettings.viewAspect
                && current.fieldOfView == startingSettings.fieldOfView
                && current.cameraDistance == startingSettings.cameraDistance
                && current.viewCone == startingSettings.viewCone
                && ReferenceEquals(quiltRenderer.sourceCamera, startingSourceCamera)
                && startingSourceCamera != null
                && startingSourceCamera.nearClipPlane == startingNearClip
                && startingSourceCamera.farClipPlane == startingFarClip;
        }

        void Fail(Exception error)
        {
            string message = error == null ? "Unknown capture error." : error.GetType().Name + ": " + error.Message;
            Finish(QuiltRecordingStatus.Error, Sanitize(message));
        }

        void Finish(QuiltRecordingStatus status, string error)
        {
            if (!IsRecording) return;
            Status = status;
            errorMessage = Sanitize(error);
            if (subscribedRenderer != null) subscribedRenderer.QuiltRendered -= OnQuiltRendered;
            subscribedRenderer = null;
            if (ReferenceEquals(activeRecorder, this)) activeRecorder = null;
            if (ownsCaptureDeltaTime && Time.captureDeltaTime == ownedCaptureDeltaTime)
                Time.captureDeltaTime = originalCaptureDeltaTime;
            ownsCaptureDeltaTime = false;
            try { WriteManifest(); }
            catch (Exception manifestError)
            {
                string manifestFailure = "Manifest write failed: " + Sanitize(manifestError.Message);
                errorMessage = string.IsNullOrEmpty(errorMessage) ? manifestFailure : errorMessage + " " + manifestFailure;
                Status = QuiltRecordingStatus.Error;
            }
        }

        void WriteManifest()
        {
            if (string.IsNullOrEmpty(manifestPath)) return;
            QuiltRecordingManifest manifest = new QuiltRecordingManifest
            {
                fps = fps,
                requestedFrameCount = requestedFrameCount,
                completedFrameCount = capturedFrames.Count,
                timeScale = 1f,
                status = Status.ToString().ToLowerInvariant(),
                error = errorMessage,
                startedAtUtc = startedAtUtc,
                finishedAtUtc = IsRecording ? string.Empty : DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                frameLog = "frames.jsonl"
            };
            string temporary = manifestPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(manifest, true) + "\n");
            File.Copy(temporary, manifestPath, true);
            File.Delete(temporary);
        }

        static string CreateUniqueOutputDirectory(string outputRoot)
        {
            string root = Path.GetFullPath(outputRoot);
            Directory.CreateDirectory(root);
            string name = "ViewWeave_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N");
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            return directory;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] buffer = new char[value.Length];
            int count = 0;
            for (int i = 0; i < value.Length; ++i)
                if (value[i] >= 0x20 && value[i] <= 0x7e) buffer[count++] = value[i];
            return new string(buffer, 0, count);
        }
    }
}
