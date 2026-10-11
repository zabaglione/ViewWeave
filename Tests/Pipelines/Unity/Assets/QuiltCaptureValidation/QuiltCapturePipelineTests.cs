#if UNITY_EDITOR
using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEditor;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.URP;
using ViewWeave.HDRP;
using HD = UnityEngine.Rendering.HighDefinition;

namespace ViewWeave.QuiltCaptureValidation
{
    public sealed class QuiltCapturePipelineTests
    {
        [UnityTest]
        public IEnumerator UrpCaptureAndThreeFrameRecording()
        {
            return RunPipelineAndRecording(false);
        }

        [UnityTest]
        public IEnumerator HdrpCaptureAndThreeFrameRecording()
        {
            return RunPipelineAndRecording(true);
        }

        [UnityTest]
        public IEnumerator UrpReferenceCameraSyncsWorldPoseAndFieldOfViewWithoutMutation()
        {
            return RunReferenceCameraSync(false);
        }

        [UnityTest]
        public IEnumerator HdrpReferenceCameraSyncsWorldPoseAndFieldOfViewWithoutMutation()
        {
            return RunReferenceCameraSync(true);
        }

        [UnityTest]
        public IEnumerator UrpMonoAudioRecordingPairsPngFramesAndPauseResumeSamples()
        {
            return RunMonoAudioRecording(false);
        }

        [UnityTest]
        public IEnumerator HdrpMonoAudioRecordingPairsPngFramesAndPauseResumeSamples()
        {
            return RunMonoAudioRecording(true);
        }

        [UnityTest]
        public IEnumerator UrpAudioStartRejectPreservesExistingRendererOwner()
        {
            Fixture fixture = null;
            bool testStartedRenderer = false;
            bool rendererStoppedByRecorder = false;
            bool ownedRendererStopSucceeded = false;
            float originalDelta = Time.captureDeltaTime;
            string outputRoot = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioStartReject_" + Guid.NewGuid().ToString("N"));
            try
            {
                AudioConfiguration configuration = AudioSettings.GetConfiguration();
                if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor)
                    Assert.Ignore("Audio recording integration is limited to Windows Editor Play mode.");
                if (configuration.speakerMode != AudioSpeakerMode.Mono || AudioSettings.speakerMode != AudioSpeakerMode.Mono)
                    Assert.Ignore("The active Unity audio speaker mode is not Mono.");
                if (!AudioRenderer.Start())
                    Assert.Ignore("AudioRenderer already has an owner; the test did not stop it.");
                testStartedRenderer = true;
                fixture = new Fixture(false);
                fixture.Setup();
                fixture.Renderer.autoRender = true;
                QuiltRecorder recorder = fixture.CameraObject.AddComponent<QuiltRecorder>();
                recorder.Renderer = fixture.Renderer;
                Time.captureDeltaTime = 0f;

                LogAssert.Expect(LogType.Error, "Error: AudioRender.Start() called while system was already recording.");
                InvalidOperationException startFailure = Assert.Throws<InvalidOperationException>(() => recorder.StartRecordingWithAudio(outputRoot, 30, 2));
                Assert.IsNotNull(startFailure, "AudioRenderer.Start should reject a concurrent capture owner.");
                Assert.IsFalse(recorder.IsRecording);
                Assert.AreEqual(QuiltRecordingStatus.Error, recorder.Status);
                Assert.AreEqual(originalDelta, Time.captureDeltaTime, 0.000001f, "a rejected audio start restores the capture timestep");
                Assert.IsTrue(fixture.Renderer.autoRender, "a rejected audio start preserves renderer state");
                int sampleCount = AudioRenderer.GetSampleCountForCaptureFrame();
                using (NativeArray<float> output = new NativeArray<float>(sampleCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                    Assert.IsTrue(AudioRenderer.Render(output), "the pre-existing owner remains active after the rejected start");
            }
            finally
            {
                if (fixture != null)
                {
                    QuiltRecorder recorder = fixture.CameraObject != null ? fixture.CameraObject.GetComponent<QuiltRecorder>() : null;
                    if (recorder != null && recorder.IsRecording)
                    {
                        recorder.StopRecording();
                        rendererStoppedByRecorder = true;
                    }
                    fixture.Dispose();
                }
                Time.captureDeltaTime = originalDelta;
                if (testStartedRenderer && !rendererStoppedByRecorder) ownedRendererStopSucceeded = AudioRenderer.Stop();
                try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); }
                catch { }
            }
            if (testStartedRenderer && !rendererStoppedByRecorder)
                Assert.IsTrue(ownedRendererStopSucceeded, "the test-owned AudioRenderer.Stop should succeed");
            yield return null;
        }

        [UnityTest]
        public IEnumerator AudioRendererStopFalseBlocksFurtherAudioStartsWithoutNativeCalls()
        {
            RequireWindowsEditorMono();
            string directory = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioStopFalse_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var backend = new FakeAudioBackend { StopResult = false };
            QuiltRecordingAudioCapture capture = null;
            PlayerLoopSystem originalLoop = InstallTestSchedulerNode();
            try
            {
                QuiltRecordingAudioCapture.SetBackendForTests(backend);
                capture = CreateFakeCapture(directory, null, error => { });
                capture.Start();
                string cleanupError = capture.Complete(false);
                Assert.That(cleanupError, Does.Contain("Stop returned false"));
                Assert.AreEqual(1, backend.StartCalls);
                Assert.AreEqual(1, backend.StopCalls);
                Assert.IsTrue(QuiltRecordingAudioCapture.IsStopUnconfirmed);
                Assert.That(QuiltRecordingAudioCapture.ValidateEnvironment(), Does.Contain("Restart the Unity Editor"));
                Assert.IsTrue(File.Exists(Path.Combine(directory, "audio.incomplete.wav")));
            }
            finally
            {
                if (capture != null) capture.Complete(false);
                QuiltRecordingAudioCapture.ResetBackendForTests();
                PlayerLoop.SetPlayerLoop(originalLoop);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator AudioHookLossAndMissedPreviousBoundaryAreReported()
        {
            RequireWindowsEditorMono();
            string directory = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioHookLoss_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var backend = new FakeAudioBackend();
            QuiltRecordingAudioCapture capture = null;
            PlayerLoopSystem originalLoop = InstallTestSchedulerNode();
            try
            {
                QuiltRecordingAudioCapture.SetBackendForTests(backend);
                capture = CreateFakeCapture(directory, null, error => { });
                capture.Start();
                Assert.IsEmpty(capture.ValidateHookAndPreviousFrame(Time.frameCount));
                Assert.That(capture.ValidateHookAndPreviousFrame(Time.frameCount + 2), Does.Contain("previous Unity frame"));
                capture.RemoveHookForTests();
                string error = capture.ValidateHookAndPreviousFrame(Time.frameCount);
                Assert.That(error, Does.Contain("hook is missing"));
                Assert.AreEqual(1, backend.StartCalls);
                Assert.AreEqual(0, backend.StopCalls);
            }
            finally
            {
                if (capture != null) capture.Complete(false);
                QuiltRecordingAudioCapture.ResetBackendForTests();
                PlayerLoop.SetPlayerLoop(originalLoop);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator WavHeaderInitializationFailureClosesFileBeforeAudioBackendStarts()
        {
            RequireWindowsEditorMono();
            string directory = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioHeaderFailure_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var backend = new FakeAudioBackend();
            try
            {
                QuiltRecordingAudioCapture.SetBackendForTests(backend);
                QuiltRecordingAudioCapture.BeforeWavHeaderForTests = () => throw new IOException("Injected WAV header failure.");
                QuiltRecordingAudioCapture capture = CreateFakeCapture(directory, null, error => { });
                Assert.Throws<IOException>(() => capture.Start());
                Assert.AreEqual(0, backend.StartCalls, "WAV initialization must finish before AudioRenderer.Start is called.");
                string temporaryWav = Path.Combine(directory, "audio.wav.tmp");
                Assert.IsTrue(File.Exists(temporaryWav));
                File.Delete(temporaryWav);
                using (new FileStream(temporaryWav, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            }
            finally
            {
                QuiltRecordingAudioCapture.ResetBackendForTests();
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator AudioFrameCommitIoFailureRollsBackSamplesAndKeepsIncompleteWav()
        {
            RequireWindowsEditorMono();
            string directory = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioCommitFailure_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var backend = new FakeAudioBackend();
            QuiltRecordingAudioCapture capture = null;
            string failure = string.Empty;
            PlayerLoopSystem originalLoop = InstallTestSchedulerNode();
            try
            {
                QuiltRecordingAudioCapture.SetBackendForTests(backend);
                capture = CreateFakeCapture(directory, (index, frame, first, count) => throw new IOException("Injected frame commit failure."), error => failure = error);
                capture.Start();
                capture.SetPendingFrame(0, Time.frameCount);
                capture.ProcessBoundaryForTests();
                Assert.That(failure, Does.Contain("Injected frame commit failure"));
                Assert.AreEqual(0L, capture.SavedSamples);
                Assert.AreEqual(0, capture.SavedFrames);
                Assert.AreEqual(1, backend.StartCalls);
                Assert.AreEqual(0, backend.StopCalls);
                Assert.IsEmpty(capture.Complete(false));
                Assert.AreEqual(1, backend.StopCalls);
                byte[] wav = File.ReadAllBytes(Path.Combine(directory, "audio.incomplete.wav"));
                Assert.AreEqual(44, wav.Length, "failed frame PCM is rolled back rather than exposed as completed audio");
            }
            finally
            {
                if (capture != null) capture.Complete(false);
                QuiltRecordingAudioCapture.ResetBackendForTests();
                PlayerLoop.SetPlayerLoop(originalLoop);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator DspSampleCountAboveNominalFrameEstimateIsAccepted()
        {
            RequireWindowsEditorMono();
            string directory = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioDspRounding_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            int sampleRate = AudioSettings.outputSampleRate;
            int nominalEstimate = (int)Math.Ceiling(sampleRate / 240d + 1d);
            var backend = new FakeAudioBackend { SampleCount = nominalEstimate + 1 };
            QuiltRecordingAudioCapture capture = null;
            PlayerLoopSystem originalLoop = InstallTestSchedulerNode();
            try
            {
                QuiltRecordingAudioCapture.SetBackendForTests(backend);
                long committedSampleCount = -1;
                capture = CreateFakeCapture(directory, (index, frame, first, count) => committedSampleCount = count, error => Assert.Fail(error), 240, 1);
                capture.Start();
                capture.SetPendingFrame(0, Time.frameCount);
                capture.ProcessBoundaryForTests();
                Assert.AreEqual(nominalEstimate + 1L, capture.SavedSamples);
                Assert.AreEqual(capture.SavedSamples, committedSampleCount);
                Assert.AreEqual(1, capture.SavedFrames);
                Assert.IsEmpty(capture.Complete(true));
                Assert.IsTrue(capture.IsFinalizedComplete);
                Assert.IsTrue(File.Exists(Path.Combine(directory, "audio.wav")));
            }
            finally
            {
                if (capture != null) capture.Complete(false);
                QuiltRecordingAudioCapture.ResetBackendForTests();
                PlayerLoop.SetPlayerLoop(originalLoop);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator DuplicateAudioBoundaryDoesNotCommitSamplesTwice()
        {
            RequireWindowsEditorMono();
            string directory = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioDuplicateBoundary_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var backend = new FakeAudioBackend();
            QuiltRecordingAudioCapture capture = null;
            PlayerLoopSystem originalLoop = InstallTestSchedulerNode();
            string failure = string.Empty;
            try
            {
                QuiltRecordingAudioCapture.SetBackendForTests(backend);
                capture = CreateFakeCapture(directory, (index, frame, first, count) => { }, error => failure = error);
                capture.Start();
                capture.SetPendingFrame(0, Time.frameCount);
                capture.ProcessBoundaryForTests();
                long samplesAfterFirstBoundary = capture.SavedSamples;
                capture.ProcessBoundaryForTests();
                Assert.AreEqual(1, capture.SavedFrames);
                Assert.AreEqual(samplesAfterFirstBoundary, capture.SavedSamples);
                Assert.That(failure, Does.Contain("more than once"));
            }
            finally
            {
                if (capture != null) capture.Complete(false);
                QuiltRecordingAudioCapture.ResetBackendForTests();
                PlayerLoop.SetPlayerLoop(originalLoop);
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            yield return null;
        }

        [UnityTest]
        public IEnumerator UrpMonoAudioPauseThenStopRestoresFilesAndCaptureTime()
        {
            return RunMonoAudioTermination(false, false);
        }

        [UnityTest]
        public IEnumerator UrpMonoAudioPauseThenDisableRestoresFilesAndCaptureTime()
        {
            return RunMonoAudioTermination(false, true);
        }

        [UnityTest]
        public IEnumerator UrpAudioStartStopPreservesPlayingAndScheduledSources()
        {
            Fixture fixture = null;
            GameObject audioRoot = null;
            AudioClip playingClip = null;
            AudioClip scheduledClip = null;
            string outputRoot = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioSourcePreservation_" + Guid.NewGuid().ToString("N"));
            float originalDelta = Time.captureDeltaTime;
            bool originalListenerPause = AudioListener.pause;
            float originalListenerVolume = AudioListener.volume;
            AudioListener listener = null;
            AudioSource playingSource = null;
            AudioSource scheduledSource = null;
            bool playingBefore = false;
            int playingClipSamples = 0;
            double scheduledAt = 0d;
            try
            {
                RequireWindowsEditorMono();
                fixture = new Fixture(false);
                fixture.Setup();
                audioRoot = new GameObject("QuiltAudioSourcePreservation.Root");
                listener = audioRoot.AddComponent<AudioListener>();
                playingSource = audioRoot.AddComponent<AudioSource>();
                scheduledSource = audioRoot.AddComponent<AudioSource>();
                playingClip = CreateToneClip("QuiltAudioPlayingClip");
                scheduledClip = CreateToneClip("QuiltAudioScheduledClip");
                playingSource.clip = playingClip;
                playingSource.loop = true;
                playingSource.spatialBlend = 0f;
                playingSource.Play();
                scheduledSource.clip = scheduledClip;
                scheduledSource.loop = true;
                scheduledSource.spatialBlend = 0f;
                scheduledAt = AudioSettings.dspTime + 0.75d;
                scheduledSource.PlayScheduled(scheduledAt);
                yield return null;
                playingBefore = playingSource.isPlaying;
                playingClipSamples = playingSource.timeSamples;
                Assert.IsTrue(playingBefore, "the test-owned source starts playing before recording begins");
                fixture.Renderer.autoRender = true;
                QuiltRecorder recorder = fixture.CameraObject.AddComponent<QuiltRecorder>();
                recorder.Renderer = fixture.Renderer;
                Time.captureDeltaTime = 0f;
                recorder.StartRecordingWithAudio(outputRoot, 30, 3);
                Assert.AreEqual(QuiltRecordingStatus.Recording, recorder.Status);
                recorder.StopRecording();
                Assert.AreEqual(QuiltRecordingStatus.Cancelled, recorder.Status);
                Assert.AreEqual(originalDelta, Time.captureDeltaTime, 0.000001f);
                Assert.AreSame(playingClip, playingSource.clip);
                Assert.AreSame(scheduledClip, scheduledSource.clip);
                Assert.AreEqual(playingBefore, playingSource.isPlaying);
                Assert.That(Math.Abs(playingSource.timeSamples - playingClipSamples), Is.LessThan(playingClip.samples), "the playing clip remains assigned and advances normally");
                Assert.AreEqual(originalListenerPause, AudioListener.pause);
                Assert.AreEqual(originalListenerVolume, AudioListener.volume, 0.000001f);
                Assert.IsTrue(listener.enabled);
                yield return new WaitForSecondsRealtime(0.9f);
                Assert.IsTrue(scheduledSource.isPlaying, "PlayScheduled continues after ViewWeave stops AudioRenderer");
                Assert.AreSame(scheduledClip, scheduledSource.clip);
            }
            finally
            {
                if (fixture != null)
                {
                    QuiltRecorder recorder = fixture.CameraObject != null ? fixture.CameraObject.GetComponent<QuiltRecorder>() : null;
                    if (recorder != null && recorder.IsRecording) recorder.StopRecording();
                    fixture.Dispose();
                }
                if (playingSource != null) playingSource.Stop();
                if (scheduledSource != null) scheduledSource.Stop();
                if (audioRoot != null) UnityEngine.Object.DestroyImmediate(audioRoot);
                if (playingClip != null) UnityEngine.Object.DestroyImmediate(playingClip);
                if (scheduledClip != null) UnityEngine.Object.DestroyImmediate(scheduledClip);
                AudioListener.pause = originalListenerPause;
                AudioListener.volume = originalListenerVolume;
                Time.captureDeltaTime = originalDelta;
                try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); }
                catch { }
            }
        }

        IEnumerator RunMonoAudioTermination(bool hdrp, bool disableRecorder)
        {
            Fixture fixture = null;
            string outputRoot = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioTermination_" + Guid.NewGuid().ToString("N"));
            float originalDelta = Time.captureDeltaTime;
            float originalScale = Time.timeScale;
            try
            {
                RequireWindowsEditorMono();
                fixture = new Fixture(hdrp);
                fixture.Setup();
                fixture.Renderer.autoRender = true;
                fixture.Renderer.ResetAutoRenderError();
                Time.timeScale = 1f;
                yield return WaitForAutomaticQuiltReadiness(fixture);
                fixture.Renderer.ResetAutoRenderError();
                QuiltRecorder recorder = fixture.CameraObject.AddComponent<QuiltRecorder>();
                recorder.Renderer = fixture.Renderer;
                Time.captureDeltaTime = 0f;
                recorder.StartRecordingWithAudio(outputRoot, 30, 5);
                for (int wait = 0; wait < 120 && recorder.CompletedFrameCount < 1 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(1, recorder.CompletedFrameCount, recorder.ErrorMessage);
                recorder.PauseRecording();
                Assert.AreEqual(QuiltRecordingStatus.Paused, recorder.Status);
                for (int wait = 0; wait < 3; ++wait) yield return null;
                if (disableRecorder) recorder.enabled = false;
                else recorder.StopRecording();
                Assert.AreEqual(QuiltRecordingStatus.Cancelled, recorder.Status);
                Assert.AreEqual(originalDelta, Time.captureDeltaTime, 0.000001f);
                Assert.AreEqual(1, recorder.CompletedFrameCount);
                string directory = recorder.RecordingDirectory;
                string[] frameLog = File.ReadAllLines(Path.Combine(directory, "frames.jsonl"));
                Assert.AreEqual(recorder.CompletedFrameCount, frameLog.Length);
                Assert.IsTrue(File.Exists(Path.Combine(directory, "audio.incomplete.wav")));
                Assert.IsFalse(File.Exists(Path.Combine(directory, "audio.wav")));
                Assert.AreEqual(0, Directory.GetFiles(Path.Combine(directory, "pending")).Length);
                QuiltRecordingManifest manifest = JsonUtility.FromJson<QuiltRecordingManifest>(File.ReadAllText(Path.Combine(directory, "manifest.json")));
                Assert.AreEqual(QuiltRecordingStatus.Cancelled.ToString().ToLowerInvariant(), manifest.status);
                Assert.AreEqual(1, manifest.completedFrameCount);
                QuiltRecordingFrame frame = JsonUtility.FromJson<QuiltRecordingFrame>(frameLog[0]);
                Assert.AreEqual(manifest.audioSampleCount, frame.audioLastSampleExclusive);
                Assert.Greater(frame.audioSampleCount, 0);
            }
            finally
            {
                if (fixture != null)
                {
                    QuiltRecorder recorder = fixture.CameraObject != null ? fixture.CameraObject.GetComponent<QuiltRecorder>() : null;
                    if (recorder != null && recorder.IsRecording) recorder.StopRecording();
                    fixture.Dispose();
                }
                Time.captureDeltaTime = originalDelta;
                Time.timeScale = originalScale;
                try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); }
                catch { }
            }
        }

        static void RequireWindowsEditorMono()
        {
            AudioConfiguration configuration = AudioSettings.GetConfiguration();
            if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor || !Application.isPlaying)
                Assert.Ignore("Audio recording validation requires Windows Editor Play mode.");
            if (configuration.speakerMode != AudioSpeakerMode.Mono || AudioSettings.speakerMode != AudioSpeakerMode.Mono)
                Assert.Ignore("The active Unity audio speaker mode is not Mono.");
        }

        static QuiltRecordingAudioCapture CreateFakeCapture(string directory, Action<int, int, long, int> commit, Action<string> failed, int fps = 30, int frameCount = 2)
        {
            return new QuiltRecordingAudioCapture(directory, fps, frameCount,
                commit ?? ((index, frame, first, count) => { }),
                committed => { },
                failed);
        }

        static PlayerLoopSystem InstallTestSchedulerNode()
        {
            PlayerLoopSystem original = PlayerLoop.GetCurrentPlayerLoop();
            PlayerLoopSystem testLoop = original;
            if (!InsertTestSchedulerNode(ref testLoop)) throw new InvalidOperationException("PostLateUpdate was not found in the current PlayerLoop.");
            PlayerLoop.SetPlayerLoop(testLoop);
            return original;
        }

        static bool InsertTestSchedulerNode(ref PlayerLoopSystem parent)
        {
            PlayerLoopSystem[] children = parent.subSystemList;
            if (parent.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
            {
                if (children == null) children = Array.Empty<PlayerLoopSystem>();
                for (int i = 0; i < children.Length; ++i)
                    if (children[i].type == typeof(QuiltAutoRenderScheduler)) return true;
                Array.Resize(ref children, children.Length + 1);
                children[children.Length - 1] = new PlayerLoopSystem { type = typeof(QuiltAutoRenderScheduler) };
                parent.subSystemList = children;
                return true;
            }
            if (children == null) return false;
            for (int i = 0; i < children.Length; ++i)
            {
                PlayerLoopSystem child = children[i];
                if (!InsertTestSchedulerNode(ref child)) continue;
                children[i] = child;
                parent.subSystemList = children;
                return true;
            }
            return false;
        }

        static AudioClip CreateToneClip(string clipName)
        {
            int sampleRate = AudioSettings.outputSampleRate;
            float[] samples = new float[sampleRate];
            for (int i = 0; i < samples.Length; ++i) samples[i] = Mathf.Sin(2f * Mathf.PI * 440f * i / sampleRate) * 0.1f;
            AudioClip clip = AudioClip.Create(clipName, samples.Length, 1, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        sealed class FakeAudioBackend : IQuiltAudioCaptureBackend
        {
            public bool StopResult = true;
            public int StartCalls;
            public int StopCalls;
            public int SampleCount = 128;

            public bool Start()
            {
                StartCalls++;
                return true;
            }

            public int GetSampleCountForCaptureFrame() => SampleCount;

            public bool Render(NativeArray<float> samples)
            {
                for (int i = 0; i < samples.Length; ++i) samples[i] = 0.125f;
                return true;
            }

            public bool Stop()
            {
                StopCalls++;
                return StopResult;
            }
        }

        IEnumerator RunMonoAudioRecording(bool hdrp)
        {
            Fixture fixture = null;
            GameObject audioRoot = null;
            AudioClip clip = null;
            string outputRoot = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioValidation_" + Guid.NewGuid().ToString("N"));
            float originalDelta = Time.captureDeltaTime;
            float originalScale = Time.timeScale;
            try
            {
                AudioConfiguration audioConfiguration = AudioSettings.GetConfiguration();
                if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor)
                    Assert.Ignore("Audio recording integration is limited to Windows Editor Play mode.");
                if (audioConfiguration.speakerMode != AudioSpeakerMode.Mono || AudioSettings.speakerMode != AudioSpeakerMode.Mono)
                    Assert.Ignore("The active Unity audio speaker mode is not Mono.");
                fixture = new Fixture(hdrp);
                fixture.Setup();
                audioRoot = new GameObject("QuiltAudioValidation.Source");
                audioRoot.AddComponent<AudioListener>();
                AudioSource source = audioRoot.AddComponent<AudioSource>();
                source.playOnAwake = false;
                source.loop = true;
                source.spatialBlend = 0f;
                source.volume = 0.25f;
                int samples = AudioSettings.outputSampleRate;
                float[] tone = new float[samples];
                for (int i = 0; i < tone.Length; ++i) tone[i] = Mathf.Sin(2f * Mathf.PI * 440f * i / samples) * 0.25f;
                clip = AudioClip.Create("QuiltAudioValidationTone", samples, 1, samples, false);
                clip.SetData(tone, 0);
                source.clip = clip;
                source.Play();
                fixture.Renderer.autoRender = true;
                fixture.Renderer.ResetAutoRenderError();
                Time.timeScale = 1f;
                yield return WaitForAutomaticQuiltReadiness(fixture);
                fixture.Renderer.ResetAutoRenderError();
                QuiltRecorder recorder = fixture.CameraObject.AddComponent<QuiltRecorder>();
                recorder.Renderer = fixture.Renderer;
                Time.captureDeltaTime = 0f;
                recorder.StartRecordingWithAudio(outputRoot, 30, 4);
                for (int wait = 0; wait < 120 && recorder.CompletedFrameCount < 1 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(1, recorder.CompletedFrameCount, "the first quilt/audio pair commits together: " + recorder.ErrorMessage);
                recorder.PauseRecording();
                Assert.AreEqual(QuiltRecordingStatus.Paused, recorder.Status);
                int pausedCount = recorder.CompletedFrameCount;
                for (int wait = 0; wait < 3; ++wait) yield return null;
                Assert.AreEqual(pausedCount, recorder.CompletedFrameCount, "paused audio is rendered and discarded without committing quilt frames");
                recorder.ResumeRecording();
                for (int wait = 0; wait < 240 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(QuiltRecordingStatus.Complete, recorder.Status, recorder.ErrorMessage);
                Assert.AreEqual(4, recorder.CompletedFrameCount);
                Assert.AreEqual(originalDelta, Time.captureDeltaTime, 0.000001f);

                string wavPath = Path.Combine(recorder.RecordingDirectory, "audio.wav");
                Assert.IsTrue(File.Exists(wavPath));
                byte[] wav = File.ReadAllBytes(wavPath);
                Assert.GreaterOrEqual(wav.Length, 44);
                Assert.AreEqual("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
                Assert.AreEqual("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
                Assert.AreEqual((ushort)3, BitConverter.ToUInt16(wav, 20), "WAV data is IEEE float");
                Assert.AreEqual((ushort)1, BitConverter.ToUInt16(wav, 22), "WAV data is mono");
                Assert.AreEqual((uint)AudioSettings.outputSampleRate, BitConverter.ToUInt32(wav, 24));
                Assert.AreEqual((ushort)32, BitConverter.ToUInt16(wav, 34));
                Assert.AreEqual((uint)(wav.Length - 44), BitConverter.ToUInt32(wav, 40));
                float peak = 0f;
                for (int offset = 44; offset + sizeof(float) <= wav.Length; offset += sizeof(float))
                    peak = Mathf.Max(peak, Mathf.Abs(BitConverter.ToSingle(wav, offset)));
                Assert.Greater(peak, 0.001f, "the owned AudioSource clip is present in the captured mix");
                string[] records = File.ReadAllLines(Path.Combine(recorder.RecordingDirectory, "frames.jsonl"));
                Assert.AreEqual(4, records.Length);
                long nextSample = 0;
                foreach (string record in records)
                {
                    QuiltRecordingFrame frame = JsonUtility.FromJson<QuiltRecordingFrame>(record);
                    Assert.AreEqual(nextSample, frame.audioFirstSample);
                    Assert.Greater(frame.audioSampleCount, 0);
                    Assert.AreEqual(frame.audioFirstSample + frame.audioSampleCount, frame.audioLastSampleExclusive);
                    nextSample = frame.audioLastSampleExclusive;
                }
                Assert.AreEqual((long)(wav.Length - 44) / sizeof(float), nextSample);
                QuiltRecordingManifest manifest = JsonUtility.FromJson<QuiltRecordingManifest>(File.ReadAllText(Path.Combine(recorder.RecordingDirectory, "manifest.json")));
                Assert.IsTrue(manifest.audioEnabled);
                Assert.AreEqual(nextSample, manifest.audioSampleCount);
                Assert.AreEqual(4d / 30d, manifest.simulationDurationSeconds, 0.000001d);
                Assert.That(manifest.durationResidualSeconds, Is.EqualTo(manifest.audioDurationSeconds - manifest.simulationDurationSeconds).Within(0.000001d));
            }
            finally
            {
                if (fixture != null)
                {
                    QuiltRecorder recorder = fixture.CameraObject != null ? fixture.CameraObject.GetComponent<QuiltRecorder>() : null;
                    if (recorder != null && recorder.IsRecording) recorder.StopRecording();
                    fixture.Dispose();
                }
                if (audioRoot != null) UnityEngine.Object.DestroyImmediate(audioRoot);
                if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
                Time.captureDeltaTime = originalDelta;
                Time.timeScale = originalScale;
                try { if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true); }
                catch { }
            }
        }

        static IEnumerator WaitForAutomaticQuiltReadiness(Fixture fixture)
        {
            QuiltRenderer renderer = fixture.Renderer;
            long startRevision = renderer.QuiltRevision;
            int startFrame = Time.frameCount;
            double startTime = Time.realtimeSinceStartupAsDouble;
            double initializationDeadline = startTime + 60.0;
            while (Time.realtimeSinceStartupAsDouble < initializationDeadline &&
                   (!renderer.HasRenderedQuilt || renderer.QuiltRevision <= startRevision))
            {
                if (!string.IsNullOrEmpty(renderer.LastAutoRenderError)) break;
                yield return null;
            }

            long initialRevision = renderer.QuiltRevision;
            int initialFrame = Time.frameCount;
            double initialElapsed = Time.realtimeSinceStartupAsDouble - startTime;
            bool initialCompleted = renderer.HasRenderedQuilt && initialRevision > startRevision;
            bool initialWithinDeadline = initialElapsed <= 60.0;
            bool initialEditorUpdating = EditorApplication.isUpdating;
            bool initialEditorCompiling = EditorApplication.isCompiling;
            string initialError = renderer.LastAutoRenderError ?? string.Empty;
            if (!initialCompleted || !initialWithinDeadline || !string.IsNullOrEmpty(initialError))
            {
                string initializationContext = "initialCompleted=" + initialCompleted.ToString().ToLowerInvariant() +
                    " initialWithinDeadline=" + initialWithinDeadline.ToString().ToLowerInvariant() +
                    " initialFrame=" + initialFrame.ToString(CultureInfo.InvariantCulture) +
                    " startFrame=" + startFrame.ToString(CultureInfo.InvariantCulture) +
                    " startRevision=" + startRevision.ToString(CultureInfo.InvariantCulture) +
                    " initialRevision=" + initialRevision.ToString(CultureInfo.InvariantCulture) +
                    " elapsedSeconds=" + initialElapsed.ToString("F3", CultureInfo.InvariantCulture) +
                    " editorUpdating=" + initialEditorUpdating.ToString().ToLowerInvariant() +
                    " editorCompiling=" + initialEditorCompiling.ToString().ToLowerInvariant() +
                    " lastAutoRenderError=" + SanitizeAscii(initialError);
                string initializationPath = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioAutoRenderInitialization_" + Guid.NewGuid().ToString("N") + ".txt");
                File.WriteAllText(initializationPath, initializationContext + "\n", System.Text.Encoding.ASCII);
                Assert.IsTrue(initialCompleted && initialWithinDeadline, "Audio readiness initialization did not complete within 60 seconds; diagnostic=" + initializationPath + " " + initializationContext);
                Assert.IsTrue(string.IsNullOrEmpty(initialError), "Automatic quilt rendering reported an initialization error; diagnostic=" + initializationPath + " " + initializationContext);
            }

            long previousRevision = initialRevision;
            int consecutiveRevisionChanges = 0;
            int steadyStartFrame = Time.frameCount;
            double steadyStartTime = Time.realtimeSinceStartupAsDouble;
            double deadline = steadyStartTime + 10.0;
            string lastError = string.Empty;
            while (Time.realtimeSinceStartupAsDouble < deadline)
            {
                yield return null;
                if (Time.realtimeSinceStartupAsDouble > deadline) break;
                long revision = renderer.QuiltRevision;
                consecutiveRevisionChanges = revision == previousRevision + 1 ? consecutiveRevisionChanges + 1 : 0;
                previousRevision = revision;
                lastError = renderer.LastAutoRenderError ?? string.Empty;
                if (!string.IsNullOrEmpty(lastError)) break;
                if (consecutiveRevisionChanges >= 2 && renderer.HasRenderedQuilt) break;
            }

            string pipeline = GraphicsSettings.currentRenderPipeline == null
                ? "BuiltIn"
                : GraphicsSettings.currentRenderPipeline.GetType().FullName;
            double steadyElapsed = Time.realtimeSinceStartupAsDouble - steadyStartTime;
            bool steadyWithinDeadline = steadyElapsed <= 10.0;
            bool ready = steadyWithinDeadline && consecutiveRevisionChanges >= 2 && renderer.HasRenderedQuilt && string.IsNullOrEmpty(lastError);
            string diagnostic = "result=" + (ready ? "ready" : "failed") + "\n" +
                "initialCompleted=" + initialCompleted.ToString().ToLowerInvariant() + "\n" +
                "initialWithinDeadline=" + initialWithinDeadline.ToString().ToLowerInvariant() + "\n" +
                "initialFrame=" + initialFrame.ToString(CultureInfo.InvariantCulture) + "\n" +
                "initialRevision=" + initialRevision.ToString(CultureInfo.InvariantCulture) + "\n" +
                "initialElapsedSeconds=" + initialElapsed.ToString("F3", CultureInfo.InvariantCulture) + "\n" +
                "initialEditorUpdating=" + initialEditorUpdating.ToString().ToLowerInvariant() + "\n" +
                "initialEditorCompiling=" + initialEditorCompiling.ToString().ToLowerInvariant() + "\n" +
                "steadyStartFrame=" + steadyStartFrame.ToString(CultureInfo.InvariantCulture) + "\n" +
                "steadyElapsedSeconds=" + steadyElapsed.ToString("F3", CultureInfo.InvariantCulture) + "\n" +
                "steadyWithinDeadline=" + steadyWithinDeadline.ToString().ToLowerInvariant() + "\n" +
                "steadyEditorUpdating=" + EditorApplication.isUpdating.ToString().ToLowerInvariant() + "\n" +
                "steadyEditorCompiling=" + EditorApplication.isCompiling.ToString().ToLowerInvariant() + "\n" +
                "frame=" + Time.frameCount.ToString(CultureInfo.InvariantCulture) + "\n" +
                "startRevision=" + startRevision.ToString(CultureInfo.InvariantCulture) + "\n" +
                "endRevision=" + renderer.QuiltRevision.ToString(CultureInfo.InvariantCulture) + "\n" +
                "consecutiveRevisionChanges=" + consecutiveRevisionChanges.ToString(CultureInfo.InvariantCulture) + "\n" +
                "hasRenderedQuilt=" + renderer.HasRenderedQuilt.ToString().ToLowerInvariant() + "\n" +
                "pipeline=" + SanitizeAscii(pipeline) + "\n" +
                "lastAutoRenderError=" + SanitizeAscii(lastError) + "\n";
            string diagnosticPath = Path.Combine(Path.GetTempPath(), "ViewWeaveAudioAutoRenderReadiness_" + Guid.NewGuid().ToString("N") + ".txt");
            File.WriteAllText(diagnosticPath, diagnostic, System.Text.Encoding.ASCII);
            string context = "diagnostic=" + diagnosticPath + " " + diagnostic.Replace("\n", " ");
            Assert.IsTrue(steadyWithinDeadline, "Audio recording automatic quilt readiness exceeded its 10 second steady-state deadline; " + context);
            Assert.GreaterOrEqual(consecutiveRevisionChanges, 2, "Audio recording requires two consecutive automatic quilt revisions before startup; " + context);
            Assert.IsTrue(renderer.HasRenderedQuilt, "Audio recording requires a rendered quilt before startup; " + context);
            Assert.IsTrue(string.IsNullOrEmpty(lastError), "Automatic quilt rendering reported an error before audio recording; " + context);
        }

        static string SanitizeAscii(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; ++i)
                if (chars[i] < 0x20 || chars[i] > 0x7e) chars[i] = '?';
            return new string(chars);
        }

        IEnumerator RunReferenceCameraSync(bool hdrp)
        {
            Fixture fixture = null;
            GameObject referenceParent = null;
            GameObject referenceObject = null;
            GameObject sourceParent = null;
            RenderTexture referenceTarget = null;
            try
            {
                fixture = new Fixture(hdrp);
                fixture.Setup();
                sourceParent = new GameObject("Capture.SourceParent");
                sourceParent.transform.position = new Vector3(1f, 3f, -2f);
                sourceParent.transform.rotation = Quaternion.Euler(4f, 15f, -3f);
                sourceParent.transform.localScale = new Vector3(2f, 0.6f, 1.7f);
                fixture.CameraObject.transform.SetParent(sourceParent.transform, true);
                referenceParent = new GameObject("Capture.ReferenceParent");
                referenceParent.transform.position = new Vector3(-3f, 2f, 5f);
                referenceParent.transform.rotation = Quaternion.Euler(9f, -17f, 4f);
                referenceParent.transform.localScale = new Vector3(3f, 0.5f, 2f);
                referenceObject = new GameObject("Capture.ReferenceCamera");
                referenceObject.transform.SetParent(referenceParent.transform, false);
                Camera reference = referenceObject.AddComponent<Camera>();
                reference.transform.SetPositionAndRotation(new Vector3(7f, 1.5f, -4f), Quaternion.Euler(13f, 41f, -8f));
                reference.fieldOfView = 67f;
                reference.nearClipPlane = 0.31f;
                reference.farClipPlane = 177f;
                reference.enabled = true;
                referenceTarget = new RenderTexture(8, 8, 0);
                reference.targetTexture = referenceTarget;
                Vector3 originalPosition = reference.transform.position;
                Quaternion originalRotation = reference.transform.rotation;
                float originalFov = reference.fieldOfView;
                float originalNear = reference.nearClipPlane;
                float originalFar = reference.farClipPlane;
                int originalMask = reference.cullingMask;
                bool originalEnabled = reference.enabled;

                fixture.Renderer.referenceCamera = reference;
                fixture.Renderer.followReferenceFieldOfView = true;
                fixture.Renderer.settings.fieldOfView = 42f;
                fixture.Renderer.RenderQuilt();

                Assert.That(Vector3.Distance(fixture.Camera.transform.position, originalPosition), Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(fixture.Camera.transform.rotation, originalRotation), Is.LessThan(0.001f));
                Assert.That(fixture.Camera.fieldOfView, Is.EqualTo(originalFov).Within(0.001f));
                Assert.That(fixture.Renderer.settings.fieldOfView, Is.EqualTo(originalFov).Within(0.001f));
                Assert.That(reference.transform.position, Is.EqualTo(originalPosition));
                Assert.That(reference.transform.rotation, Is.EqualTo(originalRotation));
                Assert.That(reference.fieldOfView, Is.EqualTo(originalFov).Within(0.001f));
                Assert.That(reference.nearClipPlane, Is.EqualTo(originalNear).Within(0.001f));
                Assert.That(reference.farClipPlane, Is.EqualTo(originalFar).Within(0.001f));
                Assert.That(reference.cullingMask, Is.EqualTo(originalMask));
                Assert.That(reference.enabled, Is.EqualTo(originalEnabled));
                Assert.That(reference.targetTexture, Is.SameAs(referenceTarget));
                Assert.IsTrue(fixture.Renderer.IsLatestQuiltCurrent);

                reference.transform.position += Vector3.right;
                Assert.IsFalse(fixture.Renderer.IsLatestQuiltCurrent, "a moved reference camera invalidates the previous quilt");
                fixture.Renderer.RenderQuilt();
                Assert.That(Vector3.Distance(fixture.Camera.transform.position, reference.transform.position), Is.LessThan(0.0001f));
                Assert.IsTrue(fixture.Renderer.IsLatestQuiltCurrent);
                reference.fieldOfView = 71f;
                Assert.IsFalse(fixture.Renderer.IsLatestQuiltCurrent, "a changed followed FOV invalidates the previous quilt");

                fixture.Renderer.followReferenceFieldOfView = false;
                fixture.Renderer.settings.fieldOfView = 53f;
                fixture.Renderer.RenderQuilt();
                Assert.That(fixture.Renderer.settings.fieldOfView, Is.EqualTo(53f).Within(0.001f));
                Assert.That(fixture.Camera.fieldOfView, Is.EqualTo(67f).Within(0.001f), "the dedicated camera keeps its own lens unchanged when Quilt FOV following is disabled");
                Assert.That(fixture.Renderer.LatestSnapshot.Settings.fieldOfView, Is.EqualTo(53f).Within(0.001f));
                Assert.That(reference.fieldOfView, Is.EqualTo(71f).Within(0.001f));

                fixture.Renderer.referenceCamera = null;
                Vector3 sourcePosition = fixture.Camera.transform.position;
                Quaternion sourceRotation = fixture.Camera.transform.rotation;
                fixture.Renderer.RenderQuilt();
                Assert.That(Vector3.Distance(fixture.Camera.transform.position, sourcePosition), Is.LessThan(0.0001f));
                Assert.That(Quaternion.Angle(fixture.Camera.transform.rotation, sourceRotation), Is.LessThan(0.001f));
            }
            finally
            {
                if (referenceObject != null)
                {
                    Camera reference = referenceObject.GetComponent<Camera>();
                    UnityEngine.Object.DestroyImmediate(referenceObject);
                }
                if (referenceTarget != null) UnityEngine.Object.DestroyImmediate(referenceTarget);
                if (referenceParent != null) UnityEngine.Object.DestroyImmediate(referenceParent);
                if (fixture != null) fixture.Dispose();
                if (sourceParent != null) UnityEngine.Object.DestroyImmediate(sourceParent);
            }
            yield return null;
        }

        [Test]
        public void QuiltFrustumFocusUsesWorldCameraPositionAndDistance()
        {
            GameObject parent = new GameObject("Frustum.Parent");
            GameObject cameraObject = new GameObject("Frustum.Camera");
            try
            {
                parent.transform.position = new Vector3(2f, -4f, 1f);
                parent.transform.rotation = Quaternion.Euler(21f, 34f, -12f);
                parent.transform.localScale = new Vector3(2f, 0.4f, 3f);
                cameraObject.transform.SetParent(parent.transform, false);
                cameraObject.transform.localPosition = new Vector3(1.2f, -0.5f, 0.7f);
                cameraObject.transform.localRotation = Quaternion.Euler(-7f, 19f, 5f);
                QuiltSettings settings = new QuiltSettings { fieldOfView = 73f, cameraDistance = 4.25f, viewAspect = 0.8f, viewCone = 30f, views = 48, rows = 6, columns = 8 };
                Assert.IsTrue(QuiltFrustumGeometry.TryBuild(cameraObject.transform, settings, 0.1f, 100f, settings.fieldOfView, out QuiltFrustumGeometry.Geometry geometry));
                Vector3 center = Vector3.zero;
                foreach (Vector3 point in geometry.focusPlane) center += point;
                center /= geometry.focusPlane.Length;
                Assert.That(Vector3.Distance(center, cameraObject.transform.position + cameraObject.transform.forward * settings.cameraDistance), Is.LessThan(0.0001f));
                Assert.That(Vector3.Distance(cameraObject.transform.position, center), Is.EqualTo(settings.cameraDistance).Within(0.0001f));
                Assert.IsTrue(QuiltFrustumGeometry.TryBuild(cameraObject.transform, settings, 0.1f, 100f, 41f, out QuiltFrustumGeometry.Geometry referenceFovGeometry));
                float wideFocusWidth = Vector3.Distance(geometry.focusPlane[0], geometry.focusPlane[3]);
                float narrowFocusWidth = Vector3.Distance(referenceFovGeometry.focusPlane[0], referenceFovGeometry.focusPlane[3]);
                Assert.That(narrowFocusWidth, Is.LessThan(wideFocusWidth), "Scene gizmo geometry should use the provided live reference FOV");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(parent);
            }
        }

        [UnityTest]
        public IEnumerator ReferenceCameraRejectsSelfOrthographicAndDescendantReferences()
        {
            yield return null;
            Fixture fixture = null;
            GameObject referenceObject = null;
            try
            {
                fixture = new Fixture(false);
                fixture.Setup();
                fixture.Renderer.RenderQuilt();
                Assert.IsTrue(fixture.Renderer.IsLatestQuiltCurrent);
                fixture.Renderer.referenceCamera = fixture.Camera;
                Assert.IsFalse(fixture.Renderer.IsLatestQuiltCurrent, "switching from no reference to an invalid self-reference invalidates the prior snapshot");
                InvalidOperationException self = Assert.Throws<InvalidOperationException>(() => fixture.Renderer.RenderQuilt());
                StringAssert.Contains("different from sourceCamera", self.Message);

                referenceObject = new GameObject("Capture.InvalidReference");
                Camera reference = referenceObject.AddComponent<Camera>();
                fixture.Renderer.referenceCamera = reference;
                reference.orthographic = true;
                InvalidOperationException orthographic = Assert.Throws<InvalidOperationException>(() => fixture.Renderer.RenderQuilt());
                StringAssert.Contains("perspective", orthographic.Message);

                reference.orthographic = false;
                referenceObject.transform.SetParent(fixture.Camera.transform, false);
                InvalidOperationException descendant = Assert.Throws<InvalidOperationException>(() => fixture.Renderer.RenderQuilt());
                StringAssert.Contains("child or descendant", descendant.Message);

            }
            finally
            {
                if (fixture != null) fixture.Dispose();
                if (referenceObject != null) UnityEngine.Object.DestroyImmediate(referenceObject);
            }
            yield return null;
        }

        IEnumerator RunPipelineAndRecording(bool hdrp)
        {
            Fixture fixture = null;
            string outputRoot = Path.Combine(Path.GetTempPath(), "ViewWeaveQuiltCaptureTests", Guid.NewGuid().ToString("N"));
            float oldCaptureDelta = Time.captureDeltaTime;
            float oldTimeScale = Time.timeScale;
            CultureInfo oldCulture = Thread.CurrentThread.CurrentCulture;
            try
            {
                Assert.AreEqual(0f, oldCaptureDelta, "test requires no other capture process");
                Time.timeScale = 1f;
                fixture = new Fixture(hdrp);
                fixture.Setup();
                yield return null;
                yield return null;

                float warmupStarted = Time.realtimeSinceStartup;
                while (!fixture.Renderer.HasRenderedQuilt && Time.realtimeSinceStartup - warmupStarted < 10f)
                {
                    fixture.Renderer.RenderQuilt();
                    if (!fixture.Renderer.HasRenderedQuilt) yield return null;
                }
                Assert.IsTrue(fixture.Renderer.HasRenderedQuilt,
                    "pipeline output must be ready before completion-listener assertions begin");

                int notifications = 0;
                bool callbackObservedRestoredCamera = false;
                Action<QuiltRenderer> listener = rendered =>
                {
                    notifications++;
                    callbackObservedRestoredCamera = !rendered.IsRendering && rendered.sourceCamera.transform.position == fixture.CameraPosition && rendered.sourceCamera.enabled == false;
                };
                fixture.Renderer.QuiltRendered += listener;
                try
                {
                    // FollowScene exposure may need asynchronous metering before the first quilt.
                    double deadline = Time.realtimeSinceStartupAsDouble + 15d;
                    while (notifications == 0 && Time.realtimeSinceStartupAsDouble < deadline)
                    {
                        fixture.Renderer.RenderQuilt();
                        if (notifications == 0) yield return null;
                    }
                }
                finally { fixture.Renderer.QuiltRendered -= listener; }
                Assert.AreEqual(1, notifications, "manual render should emit one completion");
                Assert.IsTrue(callbackObservedRestoredCamera, "completion runs after camera restoration");
                Assert.IsTrue(fixture.Renderer.IsLatestQuiltCurrent);
                Assert.AreEqual(8, fixture.Renderer.LatestSnapshot.LastRenderedViewCount);

                bool listenerAfterThrowRan = false;
                bool recursiveRenderRejected = false;
                Action<QuiltRenderer> throwingListener = rendered => throw new InvalidOperationException("listener failure for test");
                Action<QuiltRenderer> laterListener = rendered =>
                {
                    listenerAfterThrowRan = true;
                    try { rendered.RenderQuilt(); }
                    catch (InvalidOperationException) { recursiveRenderRejected = true; }
                };
                LogAssert.Expect(LogType.Error, "ViewWeave quilt completion listener failed: InvalidOperationException: listener failure for test");
                fixture.Renderer.QuiltRendered += throwingListener;
                fixture.Renderer.QuiltRendered += laterListener;
                fixture.Renderer.RenderQuilt();
                fixture.Renderer.QuiltRendered -= throwingListener;
                fixture.Renderer.QuiltRendered -= laterListener;
                Assert.IsTrue(listenerAfterThrowRan, "one listener failure does not skip later listeners");
                Assert.IsTrue(recursiveRenderRejected, "render recursion from a completion callback is rejected");
                Assert.IsTrue(fixture.Renderer.HasRenderedQuilt);
                Assert.Greater(CountDistinctPixels(fixture.Renderer.QuiltTexture), 1, "real pipeline output contains visible RGB variation");

                Directory.CreateDirectory(outputRoot);
                foreach (QuiltViewRenderMode mode in new[] { QuiltViewRenderMode.Half, QuiltViewRenderMode.Quarter })
                {
                    fixture.Renderer.viewRenderMode = mode;
                    fixture.Renderer.RenderQuilt();
                    int logicalView = (fixture.Renderer.LatestSnapshot.Layout.ViewCount - 1) / 2;
                    int renderedView = QuiltViewSampling.GetSourceViewIndex(fixture.Renderer.LatestSnapshot.Layout.ViewCount, mode, logicalView);
                    string centralPath = Path.Combine(outputRoot, mode.ToString().ToLowerInvariant() + "_central.png");
                    QuiltCaptureUtility.SaveCentralViewLatest(fixture.Renderer, centralPath);
                    QuiltCentralViewCaptureMetadata centralMetadata = JsonUtility.FromJson<QuiltCentralViewCaptureMetadata>(File.ReadAllText(Path.ChangeExtension(centralPath, ".json")));
                    Assert.AreEqual(logicalView, centralMetadata.logicalViewIndex, "central capture records its logical tile");
                    Assert.AreEqual(renderedView, centralMetadata.renderedViewIndex, "central capture records its nearest rendered source tile");
                    AssertCentralPngMatchesTile(centralPath, fixture.Renderer.QuiltTexture, fixture.Renderer.LatestSnapshot.Layout.GetTile(logicalView));
                }
                fixture.Renderer.viewRenderMode = QuiltViewRenderMode.Full;
                fixture.Renderer.RenderQuilt();

                RenderTexture sentinel = new RenderTexture(4, 4, 0, RenderTextureFormat.ARGB32);
                sentinel.Create();
                RenderTexture originalActive = RenderTexture.active;
                Texture2D pattern = null;
                string imagePath = Path.Combine(outputRoot, hdrp ? "hdrp_qs4x2a0.75.png" : "urp_qs4x2a0.75.png");
                try
                {
                    RenderTexture.active = sentinel;
                    pattern = CreateCornerPattern(fixture.Renderer.QuiltTexture.width, fixture.Renderer.QuiltTexture.height);
                    Graphics.Blit(pattern, fixture.Renderer.QuiltTexture);
                    RenderTexture.active = sentinel;
                    Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                    string expectedName = (hdrp ? "hdrp" : "urp") + "_qs4x2a0.75.png";
                    Assert.AreEqual(expectedName, QuiltCaptureUtility.BuildFileName(hdrp ? "hdrp" : "urp", fixture.Renderer.LatestSnapshot), "suffix uses invariant render aspect, not tile pixel ratio");
                    Thread.CurrentThread.CurrentCulture = oldCulture;
                    QuiltCaptureUtility.SaveLatest(fixture.Renderer, imagePath);
                    Assert.AreSame(sentinel, RenderTexture.active, "successful readback restores active render texture");
                    Assert.IsTrue(File.Exists(imagePath));
                    Assert.IsTrue(File.Exists(Path.ChangeExtension(imagePath, ".json")));
                    AssertPngDimensionsAndOrientation(imagePath, fixture.Renderer.QuiltTexture.width, fixture.Renderer.QuiltTexture.height);
                    QuiltCaptureMetadata metadata = JsonUtility.FromJson<QuiltCaptureMetadata>(File.ReadAllText(Path.ChangeExtension(imagePath, ".json")));
                    Assert.AreEqual(fixture.Renderer.QuiltTexture.width, metadata.width);
                    Assert.AreEqual(fixture.Renderer.QuiltTexture.height, metadata.height);
                    Assert.AreEqual(2, metadata.rows);
                    Assert.AreEqual(4, metadata.columns);
                    Assert.AreEqual(8, metadata.views);
                    Assert.AreEqual("Full", metadata.viewRenderMode);
                    Assert.AreEqual(8, metadata.lastRenderedViewCount);
                    Assert.AreEqual(0, metadata.usedRect.x);
                    Assert.AreEqual(0, metadata.usedRect.y);
                    Assert.AreEqual(256, metadata.usedRect.width);
                    Assert.AreEqual(192, metadata.usedRect.height);
                    Assert.AreEqual(0.75f, metadata.geometry.viewAspect);
                    Assert.IsFalse(string.IsNullOrWhiteSpace(metadata.timestampUtc));
                    Assert.Throws<IOException>(() => QuiltCaptureUtility.SaveLatest(fixture.Renderer, imagePath), "existing PNG and sidecar are not overwritten");
                    Assert.AreSame(sentinel, RenderTexture.active, "failed save preserves active render texture");
                }
                finally
                {
                    Thread.CurrentThread.CurrentCulture = oldCulture;
                    RenderTexture.active = originalActive;
                    if (pattern != null) UnityEngine.Object.DestroyImmediate(pattern);
                    sentinel.Release();
                    UnityEngine.Object.DestroyImmediate(sentinel);
                }

                fixture.OutputThrows = true;
                Assert.Throws<InvalidOperationException>(() => fixture.Renderer.RenderQuilt(), "output failure should propagate from manual render");
                Assert.IsFalse(fixture.Renderer.HasRenderedQuilt, "a failed render invalidates the prior quilt snapshot");
                Assert.Throws<InvalidOperationException>(() => QuiltCaptureUtility.SaveLatest(fixture.Renderer, Path.Combine(outputRoot, "failed.png")));

                fixture.OutputThrows = false;
                QuiltRecorder recorder = fixture.CameraObject.AddComponent<QuiltRecorder>();
                recorder.Renderer = fixture.Renderer;
                fixture.Renderer.autoRender = true;
                yield return null;
                string oldCultureName = Thread.CurrentThread.CurrentCulture.Name;
                Assert.AreEqual(oldCultureName, Thread.CurrentThread.CurrentCulture.Name, "culture restored before asynchronous recording");
                recorder.StartRecording(outputRoot, 10, 3);
                for (int wait = 0; wait < 240 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(QuiltRecordingStatus.Complete, recorder.Status, "three-frame recording should complete: " + recorder.ErrorMessage);
                Assert.AreEqual(3, recorder.CompletedFrameCount);
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "captureDeltaTime restored after completion");
                QuiltRecordingManifest manifest = JsonUtility.FromJson<QuiltRecordingManifest>(File.ReadAllText(Path.Combine(recorder.RecordingDirectory, "manifest.json")));
                Assert.AreEqual("complete", manifest.status);
                Assert.AreEqual(10, manifest.fps);
                Assert.AreEqual(3, manifest.requestedFrameCount);
                Assert.AreEqual(3, manifest.completedFrameCount);
                string[] records = File.ReadAllLines(Path.Combine(recorder.RecordingDirectory, "frames.jsonl"));
                Assert.AreEqual(3, records.Length);
                QuiltRecordingFrame first = JsonUtility.FromJson<QuiltRecordingFrame>(records[0]);
                QuiltRecordingFrame second = JsonUtility.FromJson<QuiltRecordingFrame>(records[1]);
                QuiltRecordingFrame third = JsonUtility.FromJson<QuiltRecordingFrame>(records[2]);
                Assert.AreEqual(first.unityFrame + 1, second.unityFrame);
                Assert.AreEqual(second.unityFrame + 1, third.unityFrame);
                Assert.AreEqual(0.1f, first.deltaTime, 0.0001f);
                Assert.AreEqual(0.1f, second.deltaTime, 0.0001f);
                Assert.AreEqual(0.1f, third.deltaTime, 0.0001f);
                Assert.AreEqual(0.1d, second.simulationTime - first.simulationTime, 0.001d);
                Assert.AreEqual(0.1d, third.simulationTime - second.simulationTime, 0.001d);
                for (int frame = 0; frame < 3; ++frame)
                {
                    string png = Path.Combine(recorder.RecordingDirectory, "frame_" + frame.ToString("D6", CultureInfo.InvariantCulture) + "_qs4x2a0.75.png");
                    Assert.IsTrue(File.Exists(png), "expected frame file " + frame);
                    Assert.IsTrue(File.Exists(Path.ChangeExtension(png, ".json")), "expected frame sidecar " + frame);
                }

                recorder.StartRecording(outputRoot, 25, 3);
                for (int wait = 0; wait < 240 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(QuiltRecordingStatus.Complete, recorder.Status, "25 FPS recording should complete: " + recorder.ErrorMessage);
                Assert.AreEqual(3, recorder.CompletedFrameCount);
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "captureDeltaTime restored after 25 FPS completion");
                QuiltRecordingManifest manifest25 = JsonUtility.FromJson<QuiltRecordingManifest>(File.ReadAllText(Path.Combine(recorder.RecordingDirectory, "manifest.json")));
                Assert.AreEqual(25, manifest25.fps);
                Assert.AreEqual(3, manifest25.completedFrameCount);
                string[] records25 = File.ReadAllLines(Path.Combine(recorder.RecordingDirectory, "frames.jsonl"));
                Assert.AreEqual(3, records25.Length);
                QuiltRecordingFrame first25 = JsonUtility.FromJson<QuiltRecordingFrame>(records25[0]);
                QuiltRecordingFrame second25 = JsonUtility.FromJson<QuiltRecordingFrame>(records25[1]);
                QuiltRecordingFrame third25 = JsonUtility.FromJson<QuiltRecordingFrame>(records25[2]);
                Assert.AreEqual(first25.unityFrame + 1, second25.unityFrame);
                Assert.AreEqual(second25.unityFrame + 1, third25.unityFrame);
                Assert.AreEqual(0.04f, first25.deltaTime, 0.0001f);
                Assert.AreEqual(0.04f, second25.deltaTime, 0.0001f);
                Assert.AreEqual(0.04f, third25.deltaTime, 0.0001f);
                Assert.AreEqual(0.04d, second25.simulationTime - first25.simulationTime, 0.001d);
                Assert.AreEqual(0.04d, third25.simulationTime - second25.simulationTime, 0.001d);

                recorder.StartRecording(outputRoot, 10, 5);
                while (recorder.CompletedFrameCount == 0 && recorder.IsRecording) yield return null;
                Assert.AreEqual(QuiltRecordingStatus.Recording, recorder.Status, "recording is active before pause");
                int framesBeforePause = recorder.CompletedFrameCount;
                recorder.PauseRecording();
                Assert.AreEqual(QuiltRecordingStatus.Paused, recorder.Status);
                Assert.IsTrue(recorder.IsRecording && recorder.IsPaused, "paused recorder keeps its active session ownership");
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "pause restores the prior capture timestep");
                QuiltRecordingManifest pausedManifest = JsonUtility.FromJson<QuiltRecordingManifest>(File.ReadAllText(Path.Combine(recorder.RecordingDirectory, "manifest.json")));
                Assert.AreEqual("paused", pausedManifest.status);
                Assert.IsTrue(string.IsNullOrEmpty(pausedManifest.finishedAtUtc), "paused manifest remains open");
                Assert.Throws<InvalidOperationException>(() => fixture.SecondRecorder.StartRecording(outputRoot, 20, 3), "a paused recorder keeps exclusive ownership");
                for (int wait = 0; wait < 3; ++wait) yield return null;
                Assert.AreEqual(framesBeforePause, recorder.CompletedFrameCount, "paused recorder does not save frames");
                recorder.ResumeRecording();
                Assert.AreEqual(QuiltRecordingStatus.Recording, recorder.Status);
                Assert.AreEqual(0.1f, Time.captureDeltaTime, 0.0001f, "resume reinstates the requested simulation timestep");
                for (int wait = 0; wait < 240 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(QuiltRecordingStatus.Complete, recorder.Status, "resumed recording completes the original frame count: " + recorder.ErrorMessage);
                Assert.AreEqual(5, recorder.CompletedFrameCount);
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "completion after resume restores the original capture timestep");
                string[] resumedRecords = File.ReadAllLines(Path.Combine(recorder.RecordingDirectory, "frames.jsonl"));
                Assert.AreEqual(5, resumedRecords.Length);
                QuiltRecordingFrame beforePause = JsonUtility.FromJson<QuiltRecordingFrame>(resumedRecords[framesBeforePause - 1]);
                QuiltRecordingFrame afterResume = JsonUtility.FromJson<QuiltRecordingFrame>(resumedRecords[framesBeforePause]);
                Assert.Greater(afterResume.unityFrame, beforePause.unityFrame + 1, "paused Unity frames are omitted from the recording");
                Assert.AreEqual(0.1f, afterResume.deltaTime, 0.0001f, "the first post-resume saved frame uses the requested timestep");

                recorder.StartRecording(outputRoot, 20, 10);
                recorder.PauseRecording();
                Time.captureDeltaTime = 0.04f;
                float externalDeltaDuringPause = Time.captureDeltaTime;
                Assert.Throws<InvalidOperationException>(() => recorder.ResumeRecording(), "resume rejects an externally owned capture timestep");
                Assert.AreEqual(QuiltRecordingStatus.Error, recorder.Status);
                Assert.AreEqual(externalDeltaDuringPause, Time.captureDeltaTime, "failed resume preserves the external capture timestep");
                Time.captureDeltaTime = oldCaptureDelta;

                recorder.StartRecording(outputRoot, 20, 10);
                recorder.PauseRecording();
                QuiltSettings settingsBeforePausedEdit = fixture.Renderer.settings.Clone();
                fixture.Renderer.settings.cameraDistance += 0.25f;
                Assert.Throws<InvalidOperationException>(() => recorder.ResumeRecording(), "resume rejects geometry changes made while paused");
                Assert.AreEqual(QuiltRecordingStatus.Error, recorder.Status);
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "failed geometry resume does not leave a capture timestep behind");
                fixture.Renderer.settings = settingsBeforePausedEdit;

                recorder.StartRecording(outputRoot, 20, 10);
                recorder.PauseRecording();
                recorder.StopRecording();
                Assert.AreEqual(QuiltRecordingStatus.Cancelled, recorder.Status, "stopping a paused recording finalizes it as cancelled");
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "stopping a paused recording leaves the restored timestep intact");

                recorder.StartRecording(outputRoot, 20, 10);
                recorder.PauseRecording();
                recorder.enabled = false;
                Assert.AreEqual(QuiltRecordingStatus.Cancelled, recorder.Status, "disabling a paused recorder cleans up the session");
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "disabling a paused recorder leaves no owned timestep");
                recorder.enabled = true;

                if (!hdrp)
                {
                    for (int testFps = 1; testFps <= 240; ++testFps)
                    {
                        recorder.StartRecording(outputRoot, testFps, 1);
                        float setterReadback = Time.captureDeltaTime;
                        Assert.Greater(setterReadback, 0f, "Unity should accept captureDeltaTime for " + testFps + " FPS");
                        Assert.AreEqual(1f / testFps, setterReadback, 0.000001f, "setter readback should represent requested FPS " + testFps);
                        recorder.StopRecording();
                        Assert.AreEqual(QuiltRecordingStatus.Cancelled, recorder.Status, "same-frame stop is a cancellation for " + testFps + " FPS");
                        Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "captureDeltaTime restored after same-frame stop for " + testFps + " FPS");
                    }
                }

                recorder.StartRecording(outputRoot, 20, 40);
                QuiltSettings originalSettings = fixture.Renderer.settings.Clone();
                fixture.Renderer.settings.cameraDistance += 0.25f;
                for (int wait = 0; wait < 30 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(QuiltRecordingStatus.Error, recorder.Status, "geometry changes stop recording");
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "captureDeltaTime restored after configuration failure");
                fixture.Renderer.settings = originalSettings;

                recorder.StartRecording(outputRoot, 20, 20);
                Assert.Throws<InvalidOperationException>(() => fixture.SecondRecorder.StartRecording(outputRoot, 20, 3), "only one recorder may own captureDeltaTime");
                recorder.StopRecording();
                Assert.AreEqual(QuiltRecordingStatus.Cancelled, recorder.Status);
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "captureDeltaTime restored after cancellation");

                recorder.StartRecording(outputRoot, 20, 20);
                SerializedObject recorderObject = new SerializedObject(recorder);
                recorderObject.FindProperty("quiltRenderer").objectReferenceValue = null;
                recorderObject.ApplyModifiedPropertiesWithoutUndo();
                for (int wait = 0; wait < 30 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(QuiltRecordingStatus.Error, recorder.Status, "changing the serialized target stops recording");
                Assert.AreEqual(oldCaptureDelta, Time.captureDeltaTime, "captureDeltaTime restored after renderer reference change");
                recorder.Renderer = fixture.Renderer;
                recorder.StartRecording(outputRoot, 20, 1);
                for (int wait = 0; wait < 30 && recorder.IsRecording; ++wait) yield return null;
                Assert.AreEqual(QuiltRecordingStatus.Complete, recorder.Status, "the old renderer subscription was removed on failure");
                Assert.AreEqual(1, recorder.CompletedFrameCount);

                recorder.StartRecording(outputRoot, 20, 20);
                Time.captureDeltaTime = 0.04f;
                float externallyObservedCaptureDelta = Time.captureDeltaTime;
                Assert.AreNotEqual(1f / 20f, externallyObservedCaptureDelta, "external captureDeltaTime differs from the recorder value");
                recorder.StopRecording();
                Assert.AreEqual(externallyObservedCaptureDelta, Time.captureDeltaTime, "external captureDeltaTime changes are preserved on stop");
                Time.captureDeltaTime = oldCaptureDelta;

                Time.timeScale = 0.5f;
                Assert.Throws<InvalidOperationException>(() => recorder.StartRecording(outputRoot, 20, 3), "timeScale other than one is rejected");
                Time.timeScale = 1f;
                Time.captureDeltaTime = 0.05f;
                Assert.Throws<InvalidOperationException>(() => recorder.StartRecording(outputRoot, 20, 3), "another captureDeltaTime owner is rejected");
                Time.captureDeltaTime = oldCaptureDelta;
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = oldCulture;
                Time.timeScale = oldTimeScale;
                Time.captureDeltaTime = oldCaptureDelta;
                if (fixture != null) fixture.Dispose();
                if (Directory.Exists(outputRoot)) Directory.Delete(outputRoot, true);
            }
        }

        static Texture2D CreateCornerPattern(int width, int height)
        {
            Texture2D texture = new Texture2D(width, height, TextureFormat.RGBA32, false, false);
            Color32[] colors = new Color32[width * height];
            Color32[] corners = { new Color32(255, 0, 0, 255), new Color32(0, 255, 0, 255), new Color32(0, 0, 255, 255), new Color32(255, 255, 0, 255) };
            for (int y = 0; y < height; ++y)
                for (int x = 0; x < width; ++x)
                {
                    bool top = y >= height / 2;
                    bool right = x >= width / 2;
                    colors[y * width + x] = corners[(top ? 2 : 0) + (right ? 1 : 0)];
                }
            for (int y = height / 2 - 2; y < height / 2 + 2; ++y)
                for (int x = width / 2 - 2; x < width / 2 + 2; ++x)
                    colors[y * width + x] = new Color32(118, 118, 118, 255);
            texture.SetPixels32(colors);
            texture.Apply(false, false);
            return texture;
        }

        static void AssertPngDimensionsAndOrientation(string path, int expectedWidth, int expectedHeight)
        {
            Texture2D decoded = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                Assert.IsTrue(ImageConversion.LoadImage(decoded, File.ReadAllBytes(path), false));
                Assert.AreEqual(expectedWidth, decoded.width);
                Assert.AreEqual(expectedHeight, decoded.height);
                AssertColor(decoded.GetPixel(expectedWidth / 2, expectedHeight / 2), new Color32(118, 118, 118, 255), "midrange display color survives PNG conversion");
                AssertColor(decoded.GetPixel(1, 1), new Color32(255, 0, 0, 255), "bottom-left pixel orientation");
                AssertColor(decoded.GetPixel(expectedWidth - 2, 1), new Color32(0, 255, 0, 255), "bottom-right pixel orientation");
                AssertColor(decoded.GetPixel(1, expectedHeight - 2), new Color32(0, 0, 255, 255), "top-left pixel orientation");
                AssertColor(decoded.GetPixel(expectedWidth - 2, expectedHeight - 2), new Color32(255, 255, 0, 255), "top-right pixel orientation");
            }
            finally { UnityEngine.Object.DestroyImmediate(decoded); }
        }

        static void AssertCentralPngMatchesTile(string path, RenderTexture quilt, RectInt tile)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D expected = new Texture2D(tile.width, tile.height, TextureFormat.RGBA32, false, false);
            Texture2D actual = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
            try
            {
                RenderTexture.active = quilt;
                expected.ReadPixels(new Rect(tile.x, tile.y, tile.width, tile.height), 0, 0, false);
                expected.Apply(false, false);
                Assert.IsTrue(ImageConversion.LoadImage(actual, File.ReadAllBytes(path), false));
                Assert.AreEqual(tile.width, actual.width);
                Assert.AreEqual(tile.height, actual.height);
                CollectionAssert.AreEqual(expected.GetPixels32(), actual.GetPixels32(), "central PNG pixels equal the selected output tile");
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(expected);
                UnityEngine.Object.DestroyImmediate(actual);
            }
        }

        static void AssertColor(Color actual, Color32 expected, string label)
        {
            Assert.LessOrEqual(Mathf.Abs(actual.r * 255f - expected.r), 2f, label + " red");
            Assert.LessOrEqual(Mathf.Abs(actual.g * 255f - expected.g), 2f, label + " green");
            Assert.LessOrEqual(Mathf.Abs(actual.b * 255f - expected.b), 2f, label + " blue");
            Assert.LessOrEqual(Mathf.Abs(actual.a * 255f - expected.a), 2f, label + " alpha");
        }

        sealed class Fixture
        {
            readonly bool hdrp;
            readonly string name;
            RenderPipelineAsset oldDefault;
            RenderPipelineAsset oldQuality;
            RenderTexture oldActive;
            UniversalRenderPipelineAsset urpAsset;
            UniversalRendererData urpData;
            HD.HDRenderPipelineAsset hdrpAsset;
            Material material;
            Material sphereMaterial;

            public Fixture(bool useHdrp) { hdrp = useHdrp; name = useHdrp ? "HDRP" : "URP"; }
            public GameObject CameraObject { get; private set; }
            public QuiltRenderer Renderer { get; private set; }
            public Vector3 CameraPosition => new Vector3(0f, 0f, -3f);
            public QuiltRecorder SecondRecorder { get; private set; }
            public bool OutputThrows
            {
                get => ((CaptureThrowingOutputForTests)Renderer.OutputBehaviour).ThrowOnSubmit;
                set => ((CaptureThrowingOutputForTests)Renderer.OutputBehaviour).ThrowOnSubmit = value;
            }

            public void Setup()
            {
                oldDefault = GraphicsSettings.defaultRenderPipeline;
                oldQuality = QualitySettings.renderPipeline;
                oldActive = RenderTexture.active;
                if (hdrp)
                {
                    hdrpAsset = ScriptableObject.CreateInstance<HD.HDRenderPipelineAsset>();
                    GraphicsSettings.defaultRenderPipeline = hdrpAsset;
                    QualitySettings.renderPipeline = hdrpAsset;
                }
                else
                {
                    urpData = ScriptableObject.CreateInstance<UniversalRendererData>();
                    urpData.postProcessData = AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                    urpAsset = UniversalRenderPipelineAsset.Create(urpData);
                    urpAsset.supportsHDR = true;
                    GraphicsSettings.defaultRenderPipeline = urpAsset;
                    QualitySettings.renderPipeline = urpAsset;
                }

                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.name = "Capture.TestCube." + name;
                cube.transform.position = new Vector3(-0.45f, 0f, 0.5f);
                cube.transform.localScale = Vector3.one * 0.8f;
                string shaderName = hdrp ? "HDRP/Unlit" : "Universal Render Pipeline/Unlit";
                Shader shader = Shader.Find(shaderName);
                if (shader == null) throw new InvalidOperationException("Required shader unavailable: " + shaderName);
                material = new Material(shader);
                material.SetColor(hdrp ? "_UnlitColor" : "_BaseColor", Color.red);
                cube.GetComponent<Renderer>().sharedMaterial = material;

                GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                sphere.name = "Capture.TestSphere." + name;
                sphere.transform.position = new Vector3(0.55f, 0.1f, 0.9f);
                sphereMaterial = new Material(shader);
                sphereMaterial.SetColor(hdrp ? "_UnlitColor" : "_BaseColor", Color.green);
                sphere.GetComponent<Renderer>().sharedMaterial = sphereMaterial;

                CameraObject = new GameObject("Capture.Camera." + name);
                Camera camera = CameraObject.AddComponent<Camera>();
                camera.transform.position = CameraPosition;
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                if (hdrp)
                {
                    HD.HDAdditionalCameraData hdData = CameraObject.AddComponent<HD.HDAdditionalCameraData>();
                    hdData.volumeLayerMask = ~0;
                    hdData.clearColorMode = HD.HDAdditionalCameraData.ClearColorMode.Color;
                    hdData.backgroundColorHDR = Color.black;
                    Renderer = CameraObject.AddComponent<QuiltRenderer>();
                    Renderer.GuardBehaviour = CameraObject.AddComponent<HdrpQuiltCamera>();
                }
                else
                {
                    UniversalAdditionalCameraData urpData = CameraObject.AddComponent<UniversalAdditionalCameraData>();
                    urpData.renderType = CameraRenderType.Base;
                    urpData.renderPostProcessing = true;
                    Renderer = CameraObject.AddComponent<QuiltRenderer>();
                    Renderer.GuardBehaviour = CameraObject.AddComponent<UrpQuiltCamera>();
                }
                Renderer.sourceCamera = camera;
                Renderer.settings.quiltWidth = 256;
                Renderer.settings.quiltHeight = 192;
                Renderer.settings.columns = 4;
                Renderer.settings.rows = 2;
                Renderer.settings.views = 8;
                Renderer.settings.viewAspect = 0.75f;
                Renderer.settings.fieldOfView = 50f;
                Renderer.settings.cameraDistance = 3f;
                Renderer.settings.viewCone = 20f;
                CubeObject = cube;
                SphereObject = sphere;
                Renderer.OutputBehaviour = CameraObject.AddComponent<CaptureThrowingOutputForTests>();
                RecorderObject = new GameObject("Capture.SecondRecorder");
                SecondRecorder = RecorderObject.AddComponent<QuiltRecorder>();
                SecondRecorder.Renderer = Renderer;
            }

            public void Dispose()
            {
                RenderTexture.active = oldActive;
                if (CameraObject != null) UnityEngine.Object.DestroyImmediate(CameraObject);
                if (RecorderObject != null) UnityEngine.Object.DestroyImmediate(RecorderObject);
                if (CubeObject != null) UnityEngine.Object.DestroyImmediate(CubeObject);
                if (SphereObject != null) UnityEngine.Object.DestroyImmediate(SphereObject);
                if (material != null) UnityEngine.Object.DestroyImmediate(material);
                if (sphereMaterial != null) UnityEngine.Object.DestroyImmediate(sphereMaterial);
                if (hdrpAsset != null) UnityEngine.Object.DestroyImmediate(hdrpAsset);
                if (urpAsset != null) UnityEngine.Object.DestroyImmediate(urpAsset);
                if (urpData != null) UnityEngine.Object.DestroyImmediate(urpData);
                GraphicsSettings.defaultRenderPipeline = oldDefault;
                QualitySettings.renderPipeline = oldQuality;
            }

            public GameObject CubeObject { get; private set; }
            public GameObject SphereObject { get; private set; }
            public Camera Camera => CameraObject == null ? null : CameraObject.GetComponent<Camera>();
            public GameObject RecorderObject { get; private set; }
        }

        static int CountDistinctPixels(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D image = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false, false);
            try
            {
                RenderTexture.active = texture;
                image.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0, false);
                image.Apply(false, false);
                HashSet<int> colors = new HashSet<int>();
                Color32[] pixels = image.GetPixels32();
                for (int i = 0; i < pixels.Length; ++i)
                {
                    Color32 color = pixels[i];
                    if (color.r != 0 || color.g != 0 || color.b != 0)
                        colors.Add((color.r << 16) | (color.g << 8) | color.b);
                }
                return colors.Count;
            }
            finally
            {
                RenderTexture.active = previous;
                UnityEngine.Object.DestroyImmediate(image);
            }
        }
    }

    public sealed class CaptureThrowingOutputForTests : MonoBehaviour, IQuiltOutput
    {
        public bool ThrowOnSubmit;
        public RenderTexture QuiltTexture => null;
        public QuiltLayout Layout => default;
        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            if (ThrowOnSubmit) throw new InvalidOperationException("intentional output failure");
        }
    }
}
#endif
