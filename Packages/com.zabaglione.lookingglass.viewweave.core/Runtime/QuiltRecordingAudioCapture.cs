using System;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.LowLevel;
#if UNITY_EDITOR
using UnityEditor;
#endif

[assembly: InternalsVisibleTo("ViewWeave.QuiltCaptureValidation.Tests")]

namespace ViewWeave
{
    internal interface IQuiltAudioCaptureBackend
    {
        bool Start();
        int GetSampleCountForCaptureFrame();
        bool Render(NativeArray<float> samples);
        bool Stop();
    }

    internal sealed class QuiltRecordingAudioCapture
    {
        const string StopUnconfirmedSessionKey = "ViewWeave.AudioRenderer.StopUnconfirmed";
        readonly string temporaryWavPath;
        readonly string completedWavPath;
        readonly string incompleteWavPath;
        readonly int fps;
        readonly int requestedFrameCount;
        readonly int sampleRate;
        readonly Action<int, int, long, int> commitFrame;
        readonly Action<bool> boundaryCompleted;
        readonly Action<string> failed;
        FloatWavWriter writer;
        AudioConfiguration startingConfiguration;
        int pendingFrame = -1;
        int pendingIndex = -1;
        long savedSamples;
        int savedFrames;
        bool ownsRenderer;
        bool hookInstalled;
        bool disposed;
        volatile bool configurationChanged;
        int lastProcessedUnityFrame = -1;
        int startedUnityFrame = -1;
        bool hasProcessedBoundary;

        static QuiltRecordingAudioCapture active;
        static IQuiltAudioCaptureBackend backendOverrideForTests;
        static bool stopUnconfirmedForTests;
        internal static Action BeforeWavHeaderForTests;

        public bool HasPendingFrame => pendingFrame >= 0;
        public int SampleRate => sampleRate;
        public long SavedSamples => savedSamples;
        public int SavedFrames => savedFrames;
        public double AudioDurationSeconds => sampleRate > 0 ? savedSamples / (double)sampleRate : 0d;
        public double SimulationDurationSeconds => fps > 0 ? savedFrames / (double)fps : 0d;
        public bool IsFinalizedComplete { get; private set; }
        internal static bool IsStopUnconfirmed => GetStopUnconfirmedState();

        public static string ValidateEnvironment()
        {
            if (GetStopUnconfirmedState())
                return "A prior AudioRenderer.Stop did not confirm shutdown. Restart the Unity Editor before recording audio again.";
            if (!Application.isEditor || Application.platform != RuntimePlatform.WindowsEditor)
                return "Audio recording is supported only in Windows Editor Play mode.";
            if (!Application.isPlaying)
                return "Audio recording is supported only in Windows Editor Play mode.";
            AudioConfiguration configuration = AudioSettings.GetConfiguration();
            if (configuration.speakerMode != AudioSpeakerMode.Mono || AudioSettings.speakerMode != AudioSpeakerMode.Mono)
                return "Audio recording requires the active Unity audio speaker mode to be Mono.";
            if (configuration.sampleRate <= 0 || AudioSettings.outputSampleRate <= 0)
                return "The active Unity audio sample rate is invalid.";
            return string.Empty;
        }

        public QuiltRecordingAudioCapture(string directory, int framesPerSecond, int frameCount,
            Action<int, int, long, int> commitFrame, Action<bool> boundaryCompleted, Action<string> failed)
        {
            if (active != null) throw new InvalidOperationException("Another Quilt audio recording is already active.");
            string environmentError = ValidateEnvironment();
            if (!string.IsNullOrEmpty(environmentError)) throw new InvalidOperationException(environmentError);
            fps = framesPerSecond;
            requestedFrameCount = frameCount;
            this.commitFrame = commitFrame ?? throw new ArgumentNullException(nameof(commitFrame));
            this.boundaryCompleted = boundaryCompleted ?? throw new ArgumentNullException(nameof(boundaryCompleted));
            this.failed = failed ?? throw new ArgumentNullException(nameof(failed));
            startingConfiguration = AudioSettings.GetConfiguration();
            sampleRate = AudioSettings.outputSampleRate;
            temporaryWavPath = Path.Combine(directory, "audio.wav.tmp");
            completedWavPath = Path.Combine(directory, "audio.wav");
            incompleteWavPath = Path.Combine(directory, "audio.incomplete.wav");
        }

        public void Start()
        {
            if (active != null) throw new InvalidOperationException("Another Quilt audio recording is already active.");
            writer = new FloatWavWriter(temporaryWavPath, sampleRate);
            active = this;
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
            try
            {
                InstallHook();
                if (!StartRenderer())
                    throw new InvalidOperationException("AudioRenderer.Start returned false; the existing audio owner was preserved.");
                ownsRenderer = true;
                startedUnityFrame = Time.frameCount;
            }
            catch
            {
                Cleanup(false);
                disposed = true;
                throw;
            }
        }

        public bool IsConfigurationValid(out string error)
        {
            if (configurationChanged)
            {
                error = "The Unity audio configuration changed during recording.";
                return false;
            }
            try
            {
                AudioConfiguration current = AudioSettings.GetConfiguration();
                if (current.speakerMode != AudioSpeakerMode.Mono || AudioSettings.speakerMode != AudioSpeakerMode.Mono
                    || current.sampleRate != startingConfiguration.sampleRate
                    || current.dspBufferSize != startingConfiguration.dspBufferSize
                    || current.numRealVoices != startingConfiguration.numRealVoices
                    || current.numVirtualVoices != startingConfiguration.numVirtualVoices
                    || AudioSettings.outputSampleRate != sampleRate)
                {
                    error = "The Unity audio sample rate or Mono speaker mode changed during recording.";
                    return false;
                }
            }
            catch (Exception exception)
            {
                error = "The Unity audio configuration could not be read: " + Sanitize(exception.GetType().Name + ": " + exception.Message);
                return false;
            }
            error = string.Empty;
            return true;
        }

        public void SetPendingFrame(int index, int unityFrame)
        {
            if (pendingFrame >= 0) throw new InvalidOperationException("An audio frame is already pending at the render boundary.");
            pendingIndex = index;
            pendingFrame = unityFrame;
        }

        public string Complete(bool successful)
        {
            if (disposed) return string.Empty;
            disposed = true;
            return Cleanup(successful);
        }

        void OnAudioConfigurationChanged(bool deviceWasChanged)
        {
            configurationChanged = true;
        }

        static void ProcessPostLateUpdate()
        {
            QuiltRecordingAudioCapture capture = active;
            if (capture != null) capture.ProcessBoundary();
        }

        void ProcessBoundary()
        {
            if (disposed) return;
            bool committed = false;
            try
            {
                if (!IsConfigurationValid(out string configurationError)) throw new InvalidOperationException(configurationError);
                int frame = Time.frameCount;
                if (pendingFrame >= 0 && pendingFrame != frame)
                    throw new InvalidOperationException("The quilt and audio frame did not reach the same PostLateUpdate boundary.");
                if (hasProcessedBoundary && lastProcessedUnityFrame == frame)
                    throw new InvalidOperationException("The audio capture boundary ran more than once in one Unity frame.");
                if (hasProcessedBoundary && frame > lastProcessedUnityFrame + 1)
                    throw new InvalidOperationException("A Unity frame did not reach the audio capture boundary.");
                int count = GetSampleCountForCaptureFrame();
                if (count < 0 || (pendingFrame >= 0 && count == 0))
                    throw new InvalidOperationException("AudioRenderer returned an invalid sample count.");
                using (NativeArray<float> samples = new NativeArray<float>(count, Allocator.Temp, NativeArrayOptions.UninitializedMemory))
                {
                    if (!Render(samples))
                        throw new InvalidOperationException("AudioRenderer.Render returned false.");
                    if (pendingFrame >= 0)
                    {
                        long firstSample = savedSamples;
                        long checkpoint = writer.Position;
                        try
                        {
                            writer.Write(samples);
                            commitFrame(pendingIndex, frame, firstSample, count);
                            savedSamples = checked(savedSamples + count);
                            savedFrames++;
                            committed = true;
                        }
                        catch
                        {
                            writer.Truncate(checkpoint);
                            throw;
                        }
                        finally
                        {
                            pendingFrame = -1;
                            pendingIndex = -1;
                        }
                    }
                }
                lastProcessedUnityFrame = frame;
                hasProcessedBoundary = true;
                boundaryCompleted(committed);
            }
            catch (Exception exception)
            {
                pendingFrame = -1;
                pendingIndex = -1;
                failed(Sanitize(exception.GetType().Name + ": " + exception.Message));
            }
        }

        string Cleanup(bool successful)
        {
            string failure = string.Empty;
            if (active == this) active = null;
            try { AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged; }
            catch (Exception error) { failure = AppendFailure(failure, error); }
            try { if (hookInstalled) RemoveHook(); }
            catch (Exception error) { failure = AppendFailure(failure, error); }
            hookInstalled = false;
            pendingFrame = -1;
            pendingIndex = -1;
            if (ownsRenderer)
            {
                ownsRenderer = false;
                try
                {
                    if (!StopRenderer())
                    {
                        MarkStopUnconfirmed();
                        failure = AppendFailure(failure, new InvalidOperationException("AudioRenderer.Stop returned false; shutdown is unconfirmed. Restart the Unity Editor before recording audio again."));
                    }
                }
                catch (Exception error)
                {
                    MarkStopUnconfirmed();
                    failure = AppendFailure(failure, error);
                }
            }
            try
            {
                if (writer != null)
                {
                    writer.Dispose();
                    writer = null;
                }
            }
            catch (Exception error) { failure = AppendFailure(failure, error); }
            bool complete = successful && string.IsNullOrEmpty(failure) && savedFrames == requestedFrameCount;
            try
            {
                if (File.Exists(temporaryWavPath))
                {
                    string destination = complete ? completedWavPath : incompleteWavPath;
                    if (File.Exists(destination)) throw new IOException("The audio output already exists.");
                    File.Move(temporaryWavPath, destination);
                    IsFinalizedComplete = complete;
                }
            }
            catch (Exception error) { failure = AppendFailure(failure, error); }
            return failure;
        }

        static string AppendFailure(string current, Exception error)
        {
            string next = Sanitize(error.GetType().Name + ": " + error.Message);
            return string.IsNullOrEmpty(current) ? next : current + " | " + next;
        }

        void InstallHook()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (ContainsHook(loop)) throw new InvalidOperationException("A ViewWeave audio capture hook is already installed.");
            if (!InsertAfterScheduler(ref loop))
                throw new InvalidOperationException("A unique ViewWeave quilt render scheduler was not found in PostLateUpdate.");
            PlayerLoop.SetPlayerLoop(loop);
            hookInstalled = true;
        }

        static bool InsertAfterScheduler(ref PlayerLoopSystem parent)
        {
            PlayerLoopSystem[] children = parent.subSystemList;
            if (children == null) return false;
            if (parent.type != typeof(UnityEngine.PlayerLoop.PostLateUpdate))
            {
                for (int i = 0; i < children.Length; ++i)
                {
                    PlayerLoopSystem child = children[i];
                    if (InsertAfterScheduler(ref child))
                    {
                        children[i] = child;
                        parent.subSystemList = children;
                        return true;
                    }
                }
                return false;
            }
            int schedulerIndex = -1;
            int schedulerCount = 0;
            for (int i = 0; i < children.Length; ++i)
            {
                if (children[i].type == typeof(QuiltAutoRenderScheduler))
                {
                    schedulerIndex = i;
                    schedulerCount++;
                }
            }
            if (schedulerCount != 1 || schedulerIndex < 0) return false;
            PlayerLoopSystem[] expanded = new PlayerLoopSystem[children.Length + 1];
            Array.Copy(children, 0, expanded, 0, schedulerIndex + 1);
            expanded[schedulerIndex + 1] = new PlayerLoopSystem { type = typeof(QuiltRecordingAudioCapture), updateDelegate = ProcessPostLateUpdate };
            Array.Copy(children, schedulerIndex + 1, expanded, schedulerIndex + 2, children.Length - schedulerIndex - 1);
            parent.subSystemList = expanded;
            return true;
        }

        static bool ContainsHook(PlayerLoopSystem system)
        {
            if (system.type == typeof(QuiltRecordingAudioCapture)) return true;
            PlayerLoopSystem[] children = system.subSystemList;
            if (children == null) return false;
            for (int i = 0; i < children.Length; ++i)
                if (ContainsHook(children[i])) return true;
            return false;
        }

        internal string ValidateHookAndPreviousFrame(int currentFrame)
        {
            if (!HasHookImmediatelyAfterScheduler(PlayerLoop.GetCurrentPlayerLoop()))
                return "The audio capture hook is missing or no longer follows the Quilt render scheduler.";
            if (hasProcessedBoundary && currentFrame > lastProcessedUnityFrame + 1)
                return "The previous Unity frame did not reach the audio capture boundary.";
            if (!hasProcessedBoundary && currentFrame > startedUnityFrame + 1)
                return "The previous Unity frame did not reach the audio capture boundary.";
            if (pendingFrame >= 0 && pendingFrame < currentFrame)
                return "The previous quilt frame was not consumed at the audio capture boundary.";
            return string.Empty;
        }

        internal void ProcessBoundaryForTests() => ProcessBoundary();
        internal void RemoveHookForTests() => RemoveHook();

        internal static void SetBackendForTests(IQuiltAudioCaptureBackend backend)
        {
            if (active != null) throw new InvalidOperationException("A Quilt audio capture is already active.");
            backendOverrideForTests = backend ?? throw new ArgumentNullException(nameof(backend));
            stopUnconfirmedForTests = false;
        }

        internal static void ResetBackendForTests()
        {
            if (active != null) throw new InvalidOperationException("A Quilt audio capture is still active.");
            backendOverrideForTests = null;
            stopUnconfirmedForTests = false;
            BeforeWavHeaderForTests = null;
        }

        static bool HasHookImmediatelyAfterScheduler(PlayerLoopSystem loop)
        {
            PlayerLoopSystem[] children = loop.subSystemList;
            if (loop.type == typeof(UnityEngine.PlayerLoop.PostLateUpdate))
            {
                if (children == null) return false;
                int schedulerIndex = -1;
                int schedulerCount = 0;
                int hookCount = 0;
                for (int i = 0; i < children.Length; ++i)
                {
                    if (children[i].type == typeof(QuiltAutoRenderScheduler))
                    {
                        schedulerIndex = i;
                        schedulerCount++;
                    }
                    if (children[i].type == typeof(QuiltRecordingAudioCapture)) hookCount++;
                }
                return schedulerCount == 1 && hookCount == 1 && schedulerIndex >= 0 && schedulerIndex + 1 < children.Length
                    && children[schedulerIndex + 1].type == typeof(QuiltRecordingAudioCapture);
            }
            if (children == null) return false;
            for (int i = 0; i < children.Length; ++i)
                if (HasHookImmediatelyAfterScheduler(children[i])) return true;
            return false;
        }

        static bool GetStopUnconfirmedState()
        {
            if (backendOverrideForTests != null) return stopUnconfirmedForTests;
#if UNITY_EDITOR
            return SessionState.GetBool(StopUnconfirmedSessionKey, false);
#else
            return false;
#endif
        }

        static void MarkStopUnconfirmed()
        {
            if (backendOverrideForTests != null)
            {
                stopUnconfirmedForTests = true;
                return;
            }
#if UNITY_EDITOR
            SessionState.SetBool(StopUnconfirmedSessionKey, true);
#endif
        }

        static bool StartRenderer() => backendOverrideForTests != null ? backendOverrideForTests.Start() : AudioRenderer.Start();
        static int GetSampleCountForCaptureFrame() => backendOverrideForTests != null ? backendOverrideForTests.GetSampleCountForCaptureFrame() : AudioRenderer.GetSampleCountForCaptureFrame();
        static bool Render(NativeArray<float> samples) => backendOverrideForTests != null ? backendOverrideForTests.Render(samples) : AudioRenderer.Render(samples);
        static bool StopRenderer() => backendOverrideForTests != null ? backendOverrideForTests.Stop() : AudioRenderer.Stop();

        static void RemoveHook()
        {
            PlayerLoopSystem loop = PlayerLoop.GetCurrentPlayerLoop();
            if (RemoveHookRecursive(ref loop)) PlayerLoop.SetPlayerLoop(loop);
        }

        static bool RemoveHookRecursive(ref PlayerLoopSystem parent)
        {
            PlayerLoopSystem[] children = parent.subSystemList;
            if (children == null) return false;
            bool removed = false;
            int keepCount = 0;
            for (int i = 0; i < children.Length; ++i)
            {
                if (children[i].type == typeof(QuiltRecordingAudioCapture)) removed = true;
                else keepCount++;
            }
            if (removed)
            {
                PlayerLoopSystem[] kept = new PlayerLoopSystem[keepCount];
                int index = 0;
                for (int i = 0; i < children.Length; ++i)
                    if (children[i].type != typeof(QuiltRecordingAudioCapture)) kept[index++] = children[i];
                parent.subSystemList = kept;
                return true;
            }
            for (int i = 0; i < children.Length; ++i)
            {
                PlayerLoopSystem child = children[i];
                if (RemoveHookRecursive(ref child))
                {
                    children[i] = child;
                    parent.subSystemList = children;
                    return true;
                }
            }
            return false;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] chars = new char[value.Length];
            int count = 0;
            for (int i = 0; i < value.Length; ++i)
                if (value[i] >= 0x20 && value[i] <= 0x7e) chars[count++] = value[i];
            return new string(chars, 0, count);
        }

        sealed class FloatWavWriter : IDisposable
        {
            FileStream stream;
            BinaryWriter writer;
            readonly long maximumDataBytes;
            readonly int streamAudioSampleRate;
            long dataBytes;
            bool disposed;

            public long Position => dataBytes;

            public FloatWavWriter(string path, int sampleRate)
            {
                maximumDataBytes = uint.MaxValue - 36L;
                streamAudioSampleRate = sampleRate;
                stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                try
                {
                    writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, true);
                    BeforeWavHeaderForTests?.Invoke();
                    WriteHeader(0);
                }
                catch
                {
                    try { if (writer != null) writer.Dispose(); }
                    finally { stream.Dispose(); }
                    throw;
                }
            }

            public void Write(NativeArray<float> samples)
            {
                long bytes = checked((long)samples.Length * sizeof(float));
                if (dataBytes + bytes > maximumDataBytes || dataBytes + bytes > uint.MaxValue - 36L)
                    throw new InvalidOperationException("The audio recording exceeds the RIFF WAV size limit.");
                for (int i = 0; i < samples.Length; ++i) writer.Write(samples[i]);
                dataBytes += bytes;
                writer.Flush();
            }

            public void Truncate(long checkpoint)
            {
                if (checkpoint < 0 || checkpoint > dataBytes || checkpoint % sizeof(float) != 0)
                    throw new ArgumentOutOfRangeException(nameof(checkpoint));
                stream.SetLength(44 + checkpoint);
                stream.Position = 44 + checkpoint;
                dataBytes = checkpoint;
            }

            void WriteHeader(uint payloadBytes)
            {
                stream.Position = 0;
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(payloadBytes + 36u);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16u);
                writer.Write((ushort)3);
                writer.Write((ushort)1);
                writer.Write((uint)streamAudioSampleRate);
                writer.Write((uint)(streamAudioSampleRate * sizeof(float)));
                writer.Write((ushort)sizeof(float));
                writer.Write((ushort)32);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(payloadBytes);
                writer.Flush();
                stream.Position = 44;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                try { WriteHeader(checked((uint)dataBytes)); }
                finally
                {
                    try { writer.Dispose(); }
                    finally { stream.Dispose(); }
                }
            }
        }
    }
}
