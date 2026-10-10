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
        Error,
        Paused
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
        public bool audioEnabled;
        public string audioFile;
        public string audioFormat;
        public int audioChannels;
        public int audioSampleRate;
        public long audioSampleCount;
        public double audioDurationSeconds;
        public double simulationDurationSeconds;
        public double durationResidualSeconds;
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
        public long audioFirstSample;
        public int audioSampleCount;
        public long audioLastSampleExclusive;
    }

    [DisallowMultipleComponent]
    public sealed class QuiltRecorder : MonoBehaviour
    {
        static QuiltRecorder activeRecorder;
        [SerializeField] QuiltRenderer quiltRenderer;

        readonly List<QuiltRecordingFrame> capturedFrames = new List<QuiltRecordingFrame>();
        QuiltSettings startingSettings;
        QuiltSettings startingEffectiveSettings;
        QuiltPhysicalFraming startingPhysicalFraming;
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
        bool captureAudio;
        bool skipNextQuilt;
        bool pauseAfterPendingFrame;
        bool stopAfterPendingFrame;
        string pendingPngPath;
        string pendingSidecarPath;
        QuiltRecordingFrame pendingFrameRecord;
        QuiltRecordingAudioCapture audioCapture;
        QuiltRendererOperationLease operationLease;
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
        public bool IsRecording => Status == QuiltRecordingStatus.Recording || Status == QuiltRecordingStatus.Paused;
        public bool IsPaused => Status == QuiltRecordingStatus.Paused;
        public bool IsAudioRecording => captureAudio && IsRecording;

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
            StartRecordingInternal(outputRoot, framesPerSecond, frameCount, false);
        }

        public void StartRecordingWithAudio(string outputRoot, int framesPerSecond, int frameCount)
        {
            StartRecordingInternal(outputRoot, framesPerSecond, frameCount, true);
        }

        void StartRecordingInternal(string outputRoot, int framesPerSecond, int frameCount, bool includeAudio)
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
            if (!quiltRenderer.TryGetEffectiveSettings(out QuiltSettings effectiveSettings, out string framingError))
                throw new InvalidOperationException("The selected Quilt Renderer framing is invalid: " + framingError);
            if (framesPerSecond < 1 || framesPerSecond > 240) throw new ArgumentOutOfRangeException(nameof(framesPerSecond), "FPS must be between 1 and 240.");
            if (frameCount < 1) throw new ArgumentOutOfRangeException(nameof(frameCount), "Frame count must be positive.");
            if (string.IsNullOrWhiteSpace(outputRoot)) throw new ArgumentException("An output folder is required.", nameof(outputRoot));
            if (Time.timeScale != 1f) throw new InvalidOperationException("Quilt recording requires Time.timeScale == 1.");
            if (Time.captureDeltaTime != 0f) throw new InvalidOperationException("Time.captureDeltaTime is already controlled by another capture process.");
            if (includeAudio)
            {
                string audioEnvironmentError = QuiltRecordingAudioCapture.ValidateEnvironment();
                if (!string.IsNullOrEmpty(audioEnvironmentError)) throw new InvalidOperationException(audioEnvironmentError);
            }
            if (!QuiltRendererOperationLease.TryAcquire(quiltRenderer, QuiltRendererOperationKind.Recording, out operationLease, out string leaseError))
                throw new InvalidOperationException(leaseError);

            try
            {
                startingSettings = quiltRenderer.settings.Clone();
                startingEffectiveSettings = effectiveSettings.Clone();
                startingPhysicalFraming = quiltRenderer.physicalFraming == null ? new QuiltPhysicalFraming() : quiltRenderer.physicalFraming.Clone();
                startingViewRenderMode = quiltRenderer.viewRenderMode;
                startingSourceCamera = quiltRenderer.sourceCamera;
                startingNearClip = startingSourceCamera.nearClipPlane;
                startingFarClip = startingSourceCamera.farClipPlane;
                fps = framesPerSecond;
                requestedFrameCount = frameCount;
                captureAudio = includeAudio;
                skipNextQuilt = false;
                pauseAfterPendingFrame = false;
                stopAfterPendingFrame = false;
                capturedFrames.Clear();
                errorMessage = string.Empty;
                recordingDirectory = CreateUniqueOutputDirectory(outputRoot);
                manifestPath = Path.Combine(recordingDirectory, "manifest.json");
                frameLogPath = Path.Combine(recordingDirectory, "frames.jsonl");
                File.WriteAllText(frameLogPath, string.Empty);
                if (captureAudio) Directory.CreateDirectory(Path.Combine(recordingDirectory, "pending"));
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
                    Time.captureDeltaTime = requestedCaptureDeltaTime;
                    ownedCaptureDeltaTime = Time.captureDeltaTime;
                    ownsCaptureDeltaTime = true;
                    if (captureAudio)
                    {
                        audioCapture = new QuiltRecordingAudioCapture(recordingDirectory, fps, requestedFrameCount,
                            CommitAudioFrame, OnAudioBoundaryCompleted, OnAudioCaptureFailed);
                        audioCapture.Start();
                    }
                }
                catch (Exception error)
                {
                    Fail(error);
                    throw;
                }
            }
            catch (Exception error)
            {
                if (IsRecording) Fail(error);
                else ReleaseOperationLease();
                throw;
            }
        }

        public void StopRecording()
        {
            if (!IsRecording) return;
            if (captureAudio && audioCapture != null && audioCapture.HasPendingFrame)
            {
                stopAfterPendingFrame = true;
                return;
            }
            Finish(capturedFrames.Count == requestedFrameCount ? QuiltRecordingStatus.Complete : QuiltRecordingStatus.Cancelled, string.Empty);
        }

        /// <summary>Cancel immediately, including a paused recording or pending audio frame, and release the renderer lease.</summary>
        public void CancelRecording()
        {
            if (IsRecording) Finish(QuiltRecordingStatus.Cancelled, "Recording was cancelled.");
        }

        public void PauseRecording()
        {
            if (Status != QuiltRecordingStatus.Recording) throw new InvalidOperationException("The recorder is not actively recording.");
            string invalidState = GetRecordingStateError(true);
            if (!string.IsNullOrEmpty(invalidState))
            {
                Finish(QuiltRecordingStatus.Error, invalidState);
                throw new InvalidOperationException(invalidState);
            }

            if (captureAudio && audioCapture != null && audioCapture.HasPendingFrame)
            {
                pauseAfterPendingFrame = true;
                return;
            }
            ApplyPause();
        }

        public void ResumeRecording()
        {
            if (!IsPaused) throw new InvalidOperationException("The recorder is not paused.");
            string invalidState = GetRecordingStateError(false);
            if (!string.IsNullOrEmpty(invalidState))
            {
                Finish(QuiltRecordingStatus.Error, invalidState);
                throw new InvalidOperationException(invalidState);
            }

            try
            {
                Time.captureDeltaTime = requestedCaptureDeltaTime;
                ownedCaptureDeltaTime = Time.captureDeltaTime;
                ownsCaptureDeltaTime = true;
                startFrame = Time.frameCount;
                skipNextQuilt = true;
                if (audioCapture == null && captureAudio)
                    throw new InvalidOperationException("The audio capture session is no longer available.");
                Status = QuiltRecordingStatus.Recording;
                WriteManifest();
            }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
        }

        void ApplyPause()
        {
            if (ownsCaptureDeltaTime && Time.captureDeltaTime == ownedCaptureDeltaTime)
                Time.captureDeltaTime = originalCaptureDeltaTime;
            ownsCaptureDeltaTime = false;
            Status = QuiltRecordingStatus.Paused;
            try { WriteManifest(); }
            catch (Exception error)
            {
                Fail(error);
                throw;
            }
        }

        void Update()
        {
            if (!IsRecording) return;
            if (captureAudio)
            {
                if (audioCapture == null)
                {
                    Finish(QuiltRecordingStatus.Error, "The audio capture session is no longer available.");
                    return;
                }
                string audioBoundaryError = audioCapture.ValidateHookAndPreviousFrame(Time.frameCount);
                if (!string.IsNullOrEmpty(audioBoundaryError))
                {
                    Finish(QuiltRecordingStatus.Error, audioBoundaryError);
                    return;
                }
            }
            if (Status != QuiltRecordingStatus.Recording) return;
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
            if (audioCapture != null && !audioCapture.IsConfigurationValid(out string audioError))
            {
                Finish(QuiltRecordingStatus.Error, audioError);
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
            int watchdogFrame = Math.Max(startFrame + 1, lastCapturedUnityFrame + 1);
            if (Time.frameCount > watchdogFrame)
                Finish(QuiltRecordingStatus.Error, "No completed quilt arrived for a recording frame.");
        }

        void OnQuiltRendered(QuiltRenderer rendered)
        {
            if (Status != QuiltRecordingStatus.Recording) return;
            try
            {
                int frame = Time.frameCount;
                if (skipNextQuilt)
                {
                    skipNextQuilt = false;
                    startFrame = frame;
                    return;
                }
                if (frame <= startFrame) return;
                if (!ReferenceEquals(rendered, subscribedRenderer) || !ReferenceEquals(rendered, quiltRenderer)) throw new InvalidOperationException("The selected Quilt Renderer changed during recording.");
                if (frame == lastCapturedUnityFrame) throw new InvalidOperationException("Duplicate quilt completion was detected in one Unity frame.");
                int expectedFrame = Math.Max(startFrame, lastCapturedUnityFrame) + 1;
                if (frame != expectedFrame) throw new InvalidOperationException("A Unity frame completed without a quilt.");
                if (!ConfigurationMatchesStart()) throw new InvalidOperationException("The Quilt layout, view density, or geometry settings changed during recording.");
                if (Time.timeScale != 1f || Time.captureDeltaTime != ownedCaptureDeltaTime) throw new InvalidOperationException("The capture time settings changed during recording.");
                if (Math.Abs(Time.deltaTime - requestedCaptureDeltaTime) > Math.Max(0.000001f, requestedCaptureDeltaTime * 0.0001f))
                    throw new InvalidOperationException("Unity simulation deltaTime does not match the requested capture FPS.");

                QuiltRenderSnapshot snapshot = rendered.LatestSnapshot;
                int index = capturedFrames.Count;
                string stem = "frame_" + index.ToString("D6", CultureInfo.InvariantCulture);
                string fileName = QuiltCaptureUtility.BuildFileName(stem, snapshot);
                string filePath = Path.Combine(recordingDirectory, fileName);
                QuiltRecordingFrame capturedFrame = new QuiltRecordingFrame
                {
                    index = index,
                    unityFrame = frame,
                    simulationTime = Time.timeAsDouble,
                    deltaTime = Time.deltaTime,
                    fileName = fileName,
                    timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
                };
                if (captureAudio)
                {
                    if (audioCapture == null) throw new InvalidOperationException("The audio capture session is unavailable.");
                    string pendingPath = Path.Combine(recordingDirectory, "pending", fileName);
                    QuiltCaptureUtility.Save(rendered.QuiltTexture, snapshot, pendingPath);
                    pendingPngPath = pendingPath;
                    pendingSidecarPath = Path.ChangeExtension(pendingPath, ".json");
                    pendingFrameRecord = capturedFrame;
                    audioCapture.SetPendingFrame(index, frame);
                    return;
                }
                QuiltCaptureUtility.Save(rendered.QuiltTexture, snapshot, filePath);
                capturedFrames.Add(capturedFrame);
                File.AppendAllText(frameLogPath, JsonUtility.ToJson(capturedFrame) + "\n");
                lastCapturedUnityFrame = frame;
                if (capturedFrames.Count == requestedFrameCount)
                    Finish(QuiltRecordingStatus.Complete, string.Empty);
            }
            catch (Exception error) { Fail(error); }
        }

        void CommitAudioFrame(int index, int unityFrame, long firstSample, int sampleCount)
        {
            if (pendingFrameRecord == null || pendingFrameRecord.index != index || pendingFrameRecord.unityFrame != unityFrame)
                throw new InvalidOperationException("The pending quilt does not match the audio frame.");
            string finalPng = Path.Combine(recordingDirectory, pendingFrameRecord.fileName);
            string finalJson = Path.ChangeExtension(finalPng, ".json");
            long originalLogLength = new FileInfo(frameLogPath).Length;
            bool pngMoved = false;
            bool jsonMoved = false;
            try
            {
                if (File.Exists(finalPng) || File.Exists(finalJson)) throw new IOException("A recording frame output already exists.");
                pendingFrameRecord.audioFirstSample = firstSample;
                pendingFrameRecord.audioSampleCount = sampleCount;
                pendingFrameRecord.audioLastSampleExclusive = checked(firstSample + sampleCount);
                File.Move(pendingPngPath, finalPng);
                pngMoved = true;
                File.Move(pendingSidecarPath, finalJson);
                jsonMoved = true;
                File.AppendAllText(frameLogPath, JsonUtility.ToJson(pendingFrameRecord) + "\n");
                capturedFrames.Add(pendingFrameRecord);
                lastCapturedUnityFrame = unityFrame;
                pendingPngPath = null;
                pendingSidecarPath = null;
                pendingFrameRecord = null;
            }
            catch
            {
                try
                {
                    using (FileStream log = new FileStream(frameLogPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                    {
                        log.SetLength(originalLogLength);
                        log.Flush(true);
                    }
                }
                catch { }
                if (jsonMoved) TryDelete(finalJson);
                if (pngMoved) TryDelete(finalPng);
                throw;
            }
        }

        void OnAudioBoundaryCompleted(bool committed)
        {
            if (!IsRecording) return;
            if (committed && capturedFrames.Count == requestedFrameCount)
            {
                Finish(QuiltRecordingStatus.Complete, string.Empty);
                return;
            }
            if (pauseAfterPendingFrame)
            {
                pauseAfterPendingFrame = false;
                ApplyPause();
                return;
            }
            if (stopAfterPendingFrame)
            {
                stopAfterPendingFrame = false;
                Finish(capturedFrames.Count == requestedFrameCount ? QuiltRecordingStatus.Complete : QuiltRecordingStatus.Cancelled, string.Empty);
            }
        }

        void OnAudioCaptureFailed(string error)
        {
            if (IsRecording) Finish(QuiltRecordingStatus.Error, error);
        }

        static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
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
            if (!quiltRenderer.TryGetEffectiveSettings(out QuiltSettings currentEffective, out _)) return false;
            QuiltSettings current = quiltRenderer.settings;
            return quiltRenderer.viewRenderMode == startingViewRenderMode
                && current.quiltWidth == startingSettings.quiltWidth
                && current.quiltHeight == startingSettings.quiltHeight
                && current.rows == startingSettings.rows
                && current.columns == startingSettings.columns
                && current.views == startingSettings.views
                && current.viewAspect == startingSettings.viewAspect
                && (quiltRenderer.referenceCamera != null && quiltRenderer.followReferenceFieldOfView || current.fieldOfView == startingSettings.fieldOfView)
                && current.cameraDistance == startingSettings.cameraDistance
                && current.viewCone == startingSettings.viewCone
                && currentEffective.viewAspect == startingEffectiveSettings.viewAspect
                && currentEffective.fieldOfView == startingEffectiveSettings.fieldOfView
                && currentEffective.cameraDistance == startingEffectiveSettings.cameraDistance
                && currentEffective.viewCone == startingEffectiveSettings.viewCone
                && startingPhysicalFraming.Matches(quiltRenderer.physicalFraming)
                && ReferenceEquals(quiltRenderer.sourceCamera, startingSourceCamera)
                && startingSourceCamera != null
                && startingSourceCamera.nearClipPlane == startingNearClip
                && startingSourceCamera.farClipPlane == startingFarClip;
        }

        string GetRecordingStateError(bool requireOwnedCaptureDelta)
        {
            if (!Application.isPlaying) return "Quilt recording requires Play mode.";
            if (!isActiveAndEnabled) return "The recorder component must be enabled.";
            if (!ReferenceEquals(activeRecorder, this)) return "This recorder no longer owns the active capture session.";
            if (!ReferenceEquals(quiltRenderer, subscribedRenderer) || quiltRenderer == null || !quiltRenderer.isActiveAndEnabled)
                return "The selected Quilt Renderer changed or was disabled.";
            if (!quiltRenderer.autoRender) return "Auto Render stopped during recording.";
            if (!ConfigurationMatchesStart()) return "The Quilt layout, view density, or geometry settings changed during recording.";
            if (captureAudio)
            {
                if (audioCapture == null) return "The audio capture session is no longer available.";
                if (!audioCapture.IsConfigurationValid(out string audioError)) return audioError;
            }
            if (Time.timeScale != 1f) return "Quilt recording requires Time.timeScale == 1.";
            if (requireOwnedCaptureDelta)
            {
                if (!ownsCaptureDeltaTime || Time.captureDeltaTime != ownedCaptureDeltaTime)
                    return "Time.captureDeltaTime changed during recording.";
            }
            else if (ownsCaptureDeltaTime || Time.captureDeltaTime != 0f)
                return "Time.captureDeltaTime is already controlled by another capture process.";
            return string.Empty;
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
            QuiltRecordingAudioCapture completedAudio = audioCapture;
            if (completedAudio != null)
            {
                string audioError = completedAudio.Complete(status == QuiltRecordingStatus.Complete && capturedFrames.Count == requestedFrameCount);
                if (!string.IsNullOrEmpty(audioError))
                {
                    errorMessage = string.IsNullOrEmpty(errorMessage) ? audioError : errorMessage + " " + audioError;
                    Status = QuiltRecordingStatus.Error;
                }
                try { WriteAudioMetadata(completedAudio); }
                catch (Exception metadataError)
                {
                    string metadataFailure = "Audio metadata write failed: " + Sanitize(metadataError.Message);
                    errorMessage = string.IsNullOrEmpty(errorMessage) ? metadataFailure : errorMessage + " " + metadataFailure;
                    Status = QuiltRecordingStatus.Error;
                }
            }
            TryDelete(pendingPngPath);
            TryDelete(pendingSidecarPath);
            pendingPngPath = null;
            pendingSidecarPath = null;
            pendingFrameRecord = null;
            if (subscribedRenderer != null) subscribedRenderer.QuiltRendered -= OnQuiltRendered;
            subscribedRenderer = null;
            if (ReferenceEquals(activeRecorder, this)) activeRecorder = null;
            try
            {
                if (ownsCaptureDeltaTime && Time.captureDeltaTime == ownedCaptureDeltaTime)
                    Time.captureDeltaTime = originalCaptureDeltaTime;
            }
            catch (Exception restoreError)
            {
                errorMessage = string.IsNullOrEmpty(errorMessage) ? "Capture delta restore failed: " + Sanitize(restoreError.Message) : errorMessage + " Capture delta restore failed: " + Sanitize(restoreError.Message);
                Status = QuiltRecordingStatus.Error;
            }
            finally
            {
                ownsCaptureDeltaTime = false;
                try { ReleaseOperationLease(); }
                catch (Exception leaseError)
                {
                    errorMessage = string.IsNullOrEmpty(errorMessage) ? "Operation lease release failed: " + Sanitize(leaseError.Message) : errorMessage + " Operation lease release failed: " + Sanitize(leaseError.Message);
                    Status = QuiltRecordingStatus.Error;
                }
            }
            try { WriteManifest(); }
            catch (Exception manifestError)
            {
                string manifestFailure = "Manifest write failed: " + Sanitize(manifestError.Message);
                errorMessage = string.IsNullOrEmpty(errorMessage) ? manifestFailure : errorMessage + " " + manifestFailure;
                Status = QuiltRecordingStatus.Error;
            }
            audioCapture = null;
        }

        void ReleaseOperationLease()
        {
            if (operationLease == null) return;
            operationLease.Dispose();
            operationLease = null;
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
                frameLog = "frames.jsonl",
                audioEnabled = captureAudio,
                audioFile = captureAudio && audioCapture != null ? (audioCapture.IsFinalizedComplete ? "audio.wav" : "audio.incomplete.wav") : string.Empty,
                audioFormat = captureAudio ? "IEEE_FLOAT_32" : string.Empty,
                audioChannels = captureAudio ? 1 : 0,
                audioSampleRate = captureAudio && audioCapture != null ? audioCapture.SampleRate : 0,
                audioSampleCount = captureAudio && audioCapture != null ? audioCapture.SavedSamples : 0,
                audioDurationSeconds = captureAudio && audioCapture != null ? audioCapture.AudioDurationSeconds : 0d,
                simulationDurationSeconds = captureAudio && audioCapture != null ? audioCapture.SimulationDurationSeconds : 0d,
                durationResidualSeconds = captureAudio && audioCapture != null ? audioCapture.AudioDurationSeconds - audioCapture.SimulationDurationSeconds : 0d
            };
            string temporary = manifestPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(manifest, true) + "\n");
            File.Copy(temporary, manifestPath, true);
            File.Delete(temporary);
        }

        void WriteAudioMetadata(QuiltRecordingAudioCapture capture)
        {
            if (capture == null || string.IsNullOrEmpty(recordingDirectory)) return;
            double simulationSeconds = capturedFrames.Count / (double)fps;
            double audioSeconds = capture.AudioDurationSeconds;
            var metadata = new AudioRecordingMetadata
            {
                status = capture.IsFinalizedComplete ? "complete" : "incomplete",
                file = capture.IsFinalizedComplete ? "audio.wav" : "audio.incomplete.wav",
                format = "IEEE_FLOAT_32",
                channels = 1,
                sampleRate = capture.SampleRate,
                completedFrames = capturedFrames.Count,
                samplesPerChannel = capture.SavedSamples,
                audioDurationSeconds = audioSeconds,
                simulationDurationSeconds = simulationSeconds,
                durationResidualSeconds = audioSeconds - simulationSeconds
            };
            string path = Path.Combine(recordingDirectory, "audio.json");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(metadata, true) + "\n");
            if (File.Exists(path)) File.Delete(path);
            File.Move(temporary, path);
        }

        [Serializable]
        sealed class AudioRecordingMetadata
        {
            public string status;
            public string file;
            public string format;
            public int channels;
            public int sampleRate;
            public int completedFrames;
            public long samplesPerChannel;
            public double audioDurationSeconds;
            public double simulationDurationSeconds;
            public double durationResidualSeconds;
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
