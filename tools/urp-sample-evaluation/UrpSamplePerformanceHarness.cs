using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.VFX;
using ViewWeave;
using ViewWeave.URP;

public static class UrpSamplePerformanceHarness
{
    const int WarmupFrames = 60;
    const int SampleFrames = 120;
    const int BoundaryDiscardFrames = 2;
    const int QuiltWidth = 7680;
    const int QuiltHeight = 4320;
    const int QuiltColumns = 8;
    const int QuiltRows = 6;
    const float FixedSimulationDelta = 1f / 60f;
    static readonly string[] SceneNames = { "TerminalScene", "GardenScene", "CockpitScene", "OasisScene" };
    static readonly Phase[] Phases =
    {
        new Phase("off_before", null, false),
        new Phase("full48", QuiltViewRenderMode.Full, false),
        new Phase("off_after", null, false),
        new Phase("half24", QuiltViewRenderMode.Half, false),
        new Phase("quarter12", QuiltViewRenderMode.Quarter, false),
        new Phase("full48_repeat", QuiltViewRenderMode.Full, false)
    };
    static readonly Phase[] VolumeStackComparisonPhases =
    {
        new Phase("volume_reuse_a_off", QuiltViewRenderMode.Full, false),
        new Phase("volume_reuse_b_on", QuiltViewRenderMode.Full, true),
        new Phase("volume_reuse_a2_off", QuiltViewRenderMode.Full, false)
    };
    static Phase[] activePhases;

    static GameObject temporaryRoot;
    static Camera sampleCamera;
    static QuiltRenderer quiltRenderer;
    static Camera sourceCamera;
    static UniversalAdditionalCameraData sourceCameraData;
    static UniversalAdditionalCameraData sampleCameraData;
    static UrpQuiltCamera sampleUrpCamera;
    static StringBuilder csv;
    static StringBuilder summaryCsv;
    static string outputFile;
    static int phaseIndex;
    static int phaseFrame;
    static int measuredSamples;
    static int lastSampleUnityFrame = -1;
    static int lastObservedRenderedViews;
    static long lastObservedSubmissionTicks;
    static int cameraCallbackFrame = -1;
    static int cameraCallbackCount;
    static int currentFrameCameraCallbacks;
    static int volumeStackCallbackFrame = -1;
    static int volumeStackCallbacksThisFrame;
    static VolumeStack volumeStackFirstThisFrame;
    static bool volumeStackIdentityStableThisFrame;
    static bool volumeManagerStackMatchedThisFrame;
    static int volumeStackEndCallbacksThisFrame;
    static bool volumeStackLeaseActiveAtBegin;
    static bool volumeStackViaScriptingAtBegin;
    static bool volumeStackBeginEndMatchedThisFrame;
    static int renderVerificationMisses;
    static int phaseExpectedViews;
    static float originalTimeScale;
    static int originalQualityLevel;
    static bool qualityChanged;
    static bool[] originalDriverEnabled;
    static Behaviour[] cameraDrivers;
    static VisualEffect[] cachedVfx;
    static PlayableDirector[] cachedDirectors;
    static double[] directorTimes;
    static FrameTiming[] frameTimingBuffer;
    static Sample[] sampleBuffer;
    static Vector3 initialCameraPosition;
    static Quaternion initialCameraRotation;
    static float initialCameraFov;
    static float maxCameraPositionDelta;
    static float maxCameraRotationDelta;
    static float maxCameraFovDelta;
    static int cachedRendererIndex = -1;
    static int cachedVolumeMask;
    static string cachedVolumeTrigger = "null";
    static string cachedRendererName = "null";
    static string cachedRenderType = "null";
    static int phaseValidCpu;
    static int phaseValidGpu;
    static int phaseGpuUnique;
    static int phaseWallSampleCount;
    static int phaseUnverifiedRenderFrames;
    static int phaseSettleFrames;
    static int phaseCameraStackReuseVerifiedSamples;
    static bool running;
    static bool gateOnly;
    static float evaluationCameraDistance;
    static string failure;
    static float originalCaptureDeltaTime;
    static long lastWallTicks;
    static ulong lastGpuTimestamp;
    static int gpuUnique;
    static readonly List<double> wallFrames = new List<double>(SampleFrames);
    static readonly List<double> cpuFrames = new List<double>(SampleFrames);
    static readonly List<double> gpuFrames = new List<double>(SampleFrames);
    static readonly List<double> submissions = new List<double>(SampleFrames);
    static readonly List<double> viewSubmissions = new List<double>(SampleFrames);
    struct Sample
    {
        public int unityFrame;
        public double simulationTime;
        public double wallFrameMs;
        public double cpuMs;
        public double gpuMs;
        public ulong gpuTimestamp;
        public double submissionMs;
        public double viewMs;
        public int renderedViews;
        public int cameraCallbacks;
        public int activeVfx;
        public Vector3 cameraPosition;
        public double nearClip;
        public double farClip;
        public double[] directors;
        public bool cameraStackReuseRequested;
        public bool cameraStackReuseVerified;
    }

    sealed class Phase
    {
        public readonly string name;
        public readonly QuiltViewRenderMode? mode;
        public readonly bool? reuseCameraOwnedVolumeStack;
        public Phase(string name, QuiltViewRenderMode? mode, bool? reuseCameraOwnedVolumeStack) { this.name = name; this.mode = mode; this.reuseCameraOwnedVolumeStack = reuseCameraOwnedVolumeStack; }
    }

    public static string Start(string outputDirectory)
    {
        return Start(outputDirectory, 10f);
    }

    public static string Start(string outputDirectory, float cameraDistance)
    {
        return StartInternal(outputDirectory, false, cameraDistance);
    }

    public static string StartVolumeStackComparison(string outputDirectory)
    {
        return StartVolumeStackComparison(outputDirectory, 10f);
    }

    public static string StartVolumeStackComparison(string outputDirectory, float cameraDistance)
    {
        return StartInternal(outputDirectory, false, cameraDistance, VolumeStackComparisonPhases);
    }

    public static string StartGate(string outputDirectory)
    {
        return StartGate(outputDirectory, 10f);
    }

    public static string StartGate(string outputDirectory, float cameraDistance)
    {
        return StartInternal(outputDirectory, true, cameraDistance);
    }

    static string StartInternal(string outputDirectory, bool gate, float cameraDistance, Phase[] selectedPhases = null)
    {
        if (running) throw new InvalidOperationException("an evaluation is already running");
        if (cameraDistance <= 0f) throw new ArgumentOutOfRangeException(nameof(cameraDistance), "camera distance must be positive");
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("enter Play Mode before starting the evaluation");
        var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
        if (!SceneNames.Any(name => scene.name == name)) throw new InvalidOperationException("active scene is not one of the four URP sample scenes");
        if (!(GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset)) throw new InvalidOperationException("the active render pipeline is not URP");
        if (Time.captureDeltaTime > 0f) throw new InvalidOperationException("Time.captureDeltaTime is already owned by another capture");

        sourceCamera = Camera.main;
        if (sourceCamera == null)
            sourceCamera = UnityEngine.Object.FindObjectsByType<Camera>()
                .FirstOrDefault(camera => camera.enabled && camera.gameObject.activeInHierarchy && camera.cameraType == CameraType.Game);
        if (sourceCamera == null) throw new InvalidOperationException("no active game camera was found");
        var sourceData = sourceCamera.GetComponent<UniversalAdditionalCameraData>();
        if (sourceData == null) throw new InvalidOperationException("source camera has no UniversalAdditionalCameraData");
        sourceCameraData = sourceData;

        outputDirectory = string.IsNullOrWhiteSpace(outputDirectory)
            ? "E:/LookingGlassSDK/.local/urp-sample-evaluation/results"
            : outputDirectory;
        Directory.CreateDirectory(outputDirectory);
        outputFile = Path.Combine(outputDirectory, scene.name + "_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".csv");
        csv = new StringBuilder(256 * 1024);
        summaryCsv = new StringBuilder(2048);
        summaryCsv.AppendLine("phase,views,wall_p50_ms,wall_p95_ms,cpu_p50_ms,gpu_p50_ms,cpu_submission_p50_ms,last_view_cpu_p50_ms,cpu_valid_samples,gpu_valid_unique_samples,gpu_valid_fraction,wall_samples,rendered_views,proxy_unverified_frames,texture_size,max_camera_position_delta,max_camera_rotation_delta,max_camera_fov_delta,auto_render_error,volume_stack_reuse_requested,volume_stack_reuse_verified_samples");
        csv.AppendLine("scene,phase,phaseFrame,unityFrame,simulationTime,wallFrameMs,wallFps,captureDeltaTime,directorTimes,activeVfxCount,cameraPosition,cpuFrameMs,gpuFrameMs,gpuTimestamp,cpuSubmissionMs,lastViewCpuMs,renderedViews,cameraCallbacks,textureSize,gpuUniqueSamples,camera,renderer,rendererIndex,rendererType,volumeMask,volumeTrigger,nearClip,farClip,bridge,transport,cameraDistance,cameraStackReuseRequested,cameraStackReuseVerified");
        gateOnly = gate;
        evaluationCameraDistance = cameraDistance;
        activePhases = selectedPhases ?? Phases;
        phaseIndex = gateOnly ? 1 : 0;
        phaseFrame = 0;
        measuredSamples = 0;
        failure = null;
        lastGpuTimestamp = 0;
        gpuUnique = 0;
        ClearPhaseSamples();
        originalCaptureDeltaTime = Time.captureDeltaTime;
        originalTimeScale = Time.timeScale;
        originalQualityLevel = QualitySettings.GetQualityLevel();

        try
        {
            int pcHighQuality = Array.IndexOf(QualitySettings.names, "PC High");
            if (pcHighQuality >= 0 && pcHighQuality != originalQualityLevel)
            {
                QualitySettings.SetQualityLevel(pcHighQuality);
                qualityChanged = true;
            }
            temporaryRoot = new GameObject("[ViewWeave URP Sample Evaluation]");
            sampleCamera = temporaryRoot.AddComponent<Camera>();
            sampleCamera.CopyFrom(sourceCamera);
            sampleCamera.gameObject.tag = sourceCamera.gameObject.tag;
            sampleCamera.transform.SetParent(sourceCamera.transform, false);
            sampleCamera.transform.localPosition = Vector3.zero;
            sampleCamera.transform.localRotation = Quaternion.identity;
            sampleCamera.fieldOfView = initialCameraFov = sourceCamera.fieldOfView;
            sampleCamera.enabled = false;
            sampleCamera.targetTexture = null;

            var sampleData = temporaryRoot.AddComponent<UniversalAdditionalCameraData>();
            EditorUtility.CopySerialized(sourceData, sampleData);
            sampleCameraData = sampleData;
            CacheSampleMetadata(sampleData);
            sampleUrpCamera = temporaryRoot.AddComponent<UrpQuiltCamera>();
            quiltRenderer = temporaryRoot.AddComponent<QuiltRenderer>();
            quiltRenderer.sourceCamera = sampleCamera;
            quiltRenderer.GuardBehaviour = sampleUrpCamera;
            quiltRenderer.OutputBehaviour = null;
            quiltRenderer.useRenderRequests = true;
            quiltRenderer.autoRender = false;
            quiltRenderer.settings = new QuiltSettings
            {
                quiltWidth = QuiltWidth,
                quiltHeight = QuiltHeight,
                columns = QuiltColumns,
                rows = QuiltRows,
                views = QuiltColumns * QuiltRows,
                viewAspect = sourceCamera.aspect,
                fieldOfView = sourceCamera.fieldOfView,
                cameraDistance = evaluationCameraDistance,
                viewCone = 40f
            };

            CacheSceneDrivers();
            sampleBuffer = new Sample[SampleFrames];
            frameTimingBuffer = new FrameTiming[1];
            directorTimes = new double[cachedDirectors.Length];
            for (int i = 0; i < sampleBuffer.Length; i++) sampleBuffer[i].directors = new double[cachedDirectors.Length];
            initialCameraPosition = sourceCamera.transform.position;
            initialCameraRotation = sourceCamera.transform.rotation;
            lastSampleUnityFrame = Time.frameCount;
            Time.captureDeltaTime = FixedSimulationDelta;
            lastWallTicks = Stopwatch.GetTimestamp();
            running = true;
            ConfigurePhase(activePhases[phaseIndex]);
            EditorApplication.update += Tick;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            return "started scene=" + scene.name + " camera=" + sourceCamera.name + " tag=" + sourceCamera.tag + " position=" + FormatVector(sourceCamera.transform.position) + " fov=" + sourceCamera.fieldOfView.ToString("R", CultureInfo.InvariantCulture) + " clip=" + sourceCamera.nearClipPlane.ToString("R", CultureInfo.InvariantCulture) + ":" + sourceCamera.farClipPlane.ToString("R", CultureInfo.InvariantCulture) + " cullingMask=" + sourceCamera.cullingMask + " clearFlags=" + sourceCamera.clearFlags + " renderer=" + sampleData.scriptableRenderer?.GetType().Name + " rendererIndex=" + cachedRendererIndex + " volumeMask=" + cachedVolumeMask + " volumeTrigger=" + cachedVolumeTrigger + " quality=" + QualitySettings.GetQualityLevel() + " geometry=synthetic_7680x4320_8x6_focus" + cameraDistance.ToString("R", CultureInfo.InvariantCulture) + "_cone40 sourceAspect=" + sourceCamera.aspect.ToString("R", CultureInfo.InvariantCulture) + " tileAspect=" + ((float)(QuiltWidth / QuiltColumns) / (QuiltHeight / QuiltRows)).ToString("R", CultureInfo.InvariantCulture) + " output=" + outputFile;
        }
        catch
        {
            RestoreRunState();
            throw;
        }
    }

    public static string Status()
    {
        return running
            ? "running scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " phase=" + activePhases[phaseIndex].name + " phaseFrame=" + phaseFrame + " measured=" + measuredSamples
            : "idle";
    }

    public static string Abort(string reason)
    {
        if (!running) return "idle";
        failure = string.IsNullOrWhiteSpace(reason) ? "aborted" : Ascii(reason);
        Finish();
        return "aborted output=" + outputFile;
    }

    static void Tick()
    {
        try { TickCore(); }
        catch (Exception error)
        {
            failure = Ascii(error.GetType().Name + ": " + error.Message);
            Finish();
        }
    }

    static void TickCore()
    {
        if (!running) return;
        if (!EditorApplication.isPlaying) throw new InvalidOperationException("Play Mode exited before completion");
        if (Mathf.Abs(Time.captureDeltaTime - FixedSimulationDelta) > 0.000001f) throw new InvalidOperationException("Time.captureDeltaTime changed during evaluation");

        int unityFrame = Time.frameCount;
        if (unityFrame == lastSampleUnityFrame) return;
        if (unityFrame - lastSampleUnityFrame != 1) throw new InvalidOperationException("Unity frame gap detected; one-frame sampling guarantee was lost");
        lastSampleUnityFrame = unityFrame;
        currentFrameCameraCallbacks = cameraCallbackFrame == unityFrame ? cameraCallbackCount : 0;
        long nowTicks = Stopwatch.GetTimestamp();
        double wallFrameMs = (nowTicks - lastWallTicks) * 1000.0 / Stopwatch.Frequency;
        lastWallTicks = nowTicks;
        sourceCamera.transform.GetPositionAndRotation(out var sourcePosition, out var sourceRotation);
        sampleCamera.fieldOfView = sourceCamera.fieldOfView;
        sampleCamera.cullingMask = sourceCamera.cullingMask;
        sampleCamera.clearFlags = sourceCamera.clearFlags;
        sampleCamera.backgroundColor = sourceCamera.backgroundColor;
        sampleCamera.nearClipPlane = sourceCamera.nearClipPlane;
        sampleCamera.farClipPlane = sourceCamera.farClipPlane;
        sampleCamera.useOcclusionCulling = sourceCamera.useOcclusionCulling;
        sampleCamera.allowHDR = sourceCamera.allowHDR;
        sampleCamera.allowMSAA = sourceCamera.allowMSAA;
        sampleCameraData.volumeLayerMask = sourceCameraData.volumeLayerMask;
        sampleCameraData.volumeTrigger = sourceCameraData.volumeTrigger;
        sampleCameraData.renderPostProcessing = sourceCameraData.renderPostProcessing;
        maxCameraPositionDelta = Mathf.Max(maxCameraPositionDelta, Vector3.Distance(initialCameraPosition, sourcePosition));
        maxCameraRotationDelta = Mathf.Max(maxCameraRotationDelta, Quaternion.Angle(initialCameraRotation, sourceRotation));
        maxCameraFovDelta = Mathf.Max(maxCameraFovDelta, Mathf.Abs(initialCameraFov - sourceCamera.fieldOfView));

        var phase = activePhases[phaseIndex];
        if (!string.IsNullOrEmpty(quiltRenderer.LastAutoRenderError))
            throw new InvalidOperationException("auto render error: " + quiltRenderer.LastAutoRenderError);
        FrameTimingManager.CaptureFrameTimings();
        double cpuMs = 0.0;
        double gpuMs = 0.0;
        ulong gpuTimestamp = 0;
        if (FrameTimingManager.GetLatestTimings(1, frameTimingBuffer) > 0)
        {
            if (frameTimingBuffer[0].cpuFrameTime > 0.0) cpuMs = frameTimingBuffer[0].cpuFrameTime;
            if (frameTimingBuffer[0].gpuFrameTime > 0.0 && frameTimingBuffer[0].frameStartTimestamp > lastGpuTimestamp)
            {
                gpuMs = frameTimingBuffer[0].gpuFrameTime;
                gpuTimestamp = frameTimingBuffer[0].frameStartTimestamp;
                lastGpuTimestamp = gpuTimestamp;
            }
        }

        bool quiltActive = phase.mode.HasValue;
        long submissionTicks = quiltActive ? quiltRenderer.LastCpuSubmissionTicks : 0;
        int renderedViews = quiltActive ? quiltRenderer.LastRenderedViewCount : 0;
        double submissionMs = submissionTicks > 0 ? submissionTicks * 1000.0 / Stopwatch.Frequency : 0.0;
        double viewMs = quiltActive && quiltRenderer.LastViewCpuTicks > 0 ? quiltRenderer.LastViewCpuTicks * 1000.0 / Stopwatch.Frequency : 0.0;
        bool freshRenderProxy = phaseExpectedViews > 0 && renderedViews == phaseExpectedViews && currentFrameCameraCallbacks == phaseExpectedViews;
        if (freshRenderProxy)
            renderVerificationMisses = 0;
        else if (phaseExpectedViews > 0 && phaseFrame > WarmupFrames + BoundaryDiscardFrames)
        {
            renderVerificationMisses++;
            phaseUnverifiedRenderFrames++;
        }
        lastObservedRenderedViews = renderedViews;
        lastObservedSubmissionTicks = submissionTicks;
        if (renderVerificationMisses > 10) throw new InvalidOperationException("automatic quilt render was not verified for 10 consecutive Unity frames");

        for (int i = 0; i < cachedDirectors.Length; i++) directorTimes[i] = cachedDirectors[i] != null ? cachedDirectors[i].time : double.NaN;
        int activeVfx = 0;
        for (int i = 0; i < cachedVfx.Length; i++) if (cachedVfx[i] != null && cachedVfx[i].isActiveAndEnabled) activeVfx++;

        phaseFrame++;
        if (gateOnly)
        {
            if (phaseFrame >= 8)
            {
                if (renderedViews != 48 || currentFrameCameraCallbacks != 48 || quiltRenderer.QuiltTexture == null || !quiltRenderer.QuiltTexture.IsCreated() || submissionTicks <= 0)
                    failure = "gate_failed: automatic full48 render was not verified";
                Finish();
            }
            return;
        }
        int firstSampleFrame = WarmupFrames + BoundaryDiscardFrames;
        if (phaseFrame > firstSampleFrame && measuredSamples < SampleFrames)
        {
            var sample = new Sample
            {
                unityFrame = unityFrame,
                simulationTime = Time.time,
                wallFrameMs = wallFrameMs,
                cpuMs = cpuMs,
                gpuMs = gpuMs,
                gpuTimestamp = gpuTimestamp,
                submissionMs = submissionMs,
                viewMs = viewMs,
                renderedViews = renderedViews,
                cameraCallbacks = currentFrameCameraCallbacks,
                activeVfx = activeVfx,
                cameraPosition = sourcePosition,
                nearClip = sampleCamera.nearClipPlane,
                farClip = sampleCamera.farClipPlane,
                directors = sampleBuffer[measuredSamples].directors,
                cameraStackReuseRequested = phase.reuseCameraOwnedVolumeStack == true,
                cameraStackReuseVerified = phase.reuseCameraOwnedVolumeStack == true &&
                    volumeStackCallbackFrame == unityFrame &&
                    volumeStackCallbacksThisFrame == phaseExpectedViews &&
                    volumeStackFirstThisFrame != null &&
                    volumeStackIdentityStableThisFrame &&
                    volumeManagerStackMatchedThisFrame &&
                    volumeStackEndCallbacksThisFrame == phaseExpectedViews &&
                    volumeStackBeginEndMatchedThisFrame
            };
            Array.Copy(directorTimes, sample.directors, directorTimes.Length);
            if (sample.cameraStackReuseVerified) phaseCameraStackReuseVerifiedSamples++;
            sampleBuffer[measuredSamples++] = sample;
            phaseWallSampleCount++;
            if (wallFrameMs > 0.0) wallFrames.Add(wallFrameMs);
            if (cpuMs > 0.0) { cpuFrames.Add(cpuMs); phaseValidCpu++; }
            if (gpuMs > 0.0 && gpuTimestamp != 0) { gpuFrames.Add(gpuMs); gpuUnique++; phaseValidGpu++; }
            if (phase.mode.HasValue && submissionMs > 0.0) { submissions.Add(submissionMs); viewSubmissions.Add(viewMs); }
        }

        if (measuredSamples < SampleFrames) return;
        SerializeSamples(phase);
        AddSummary(phase, renderedViews, phase.mode.HasValue ? TextureSize() : "single-camera");
        if (phase.reuseCameraOwnedVolumeStack == true && phaseCameraStackReuseVerifiedSamples != SampleFrames)
        {
            failure = "camera-owned VolumeStack path verified " + phaseCameraStackReuseVerifiedSamples + "/" + SampleFrames + " measured samples";
            Finish();
            return;
        }
        phaseIndex++;
        phaseFrame = 0;
        measuredSamples = 0;
        ClearPhaseSamples();
        gpuUnique = 0;
        phaseValidCpu = phaseValidGpu = phaseWallSampleCount = 0;
        phaseUnverifiedRenderFrames = 0;
        renderVerificationMisses = 0;
        if (phaseIndex >= activePhases.Length) Finish();
        else ConfigurePhase(activePhases[phaseIndex]);
    }

    static void AddSummary(Phase phase, int renderedViews, string textureSize)
    {
        AppendSummaryRow(phase, "_p50", Percentile(wallFrames, 0.50), Percentile(cpuFrames, 0.50), Percentile(gpuFrames, 0.50), Percentile(submissions, 0.50), Percentile(viewSubmissions, 0.50), renderedViews, textureSize);
        AppendSummaryRow(phase, "_p95", Percentile(wallFrames, 0.95), Percentile(cpuFrames, 0.95), Percentile(gpuFrames, 0.95), Percentile(submissions, 0.95), Percentile(viewSubmissions, 0.95), renderedViews, textureSize);
    }

    static void CacheSampleMetadata(UniversalAdditionalCameraData data)
    {
        cachedRendererIndex = -1;
        var serialized = new SerializedObject(data);
        var property = serialized.FindProperty("m_RendererIndex");
        if (property != null) cachedRendererIndex = property.intValue;
        cachedVolumeMask = data.volumeLayerMask.value;
        cachedVolumeTrigger = data.volumeTrigger != null ? data.volumeTrigger.name : "null";
        cachedRendererName = data.scriptableRenderer != null ? data.scriptableRenderer.GetType().Name : "null";
        cachedRenderType = data.renderType.ToString();
    }

    static void CacheSceneDrivers()
    {
        sourceCamera.transform.GetPositionAndRotation(out var poseBeforeDisable, out var rotationBeforeDisable);
        float fovBeforeDisable = sourceCamera.fieldOfView;
        var components = sourceCamera.GetComponentsInParent<Behaviour>(true);
        var drivers = new List<Behaviour>();
        var enabledStates = new List<bool>();
        foreach (var behaviour in components)
        {
            if (behaviour == null || behaviour == sourceCamera) continue;
            var typeName = behaviour.GetType().Name;
            if (typeName != "CinemachineBrain" && typeName != "FirstPersonController" && typeName != "PlayerManager") continue;
            drivers.Add(behaviour);
            enabledStates.Add(behaviour.enabled);
        }
        cameraDrivers = drivers.ToArray();
        originalDriverEnabled = enabledStates.ToArray();
        try
        {
            for (int i = 0; i < cameraDrivers.Length; i++)
                if (cameraDrivers[i] != null && originalDriverEnabled[i]) cameraDrivers[i].enabled = false;
        }
        finally
        {
            sourceCamera.transform.SetPositionAndRotation(poseBeforeDisable, rotationBeforeDisable);
            sourceCamera.fieldOfView = fovBeforeDisable;
        }
        cachedVfx = UnityEngine.Object.FindObjectsByType<VisualEffect>(FindObjectsSortMode.None);
        cachedDirectors = UnityEngine.Object.FindObjectsByType<PlayableDirector>(FindObjectsSortMode.None);
    }

    static void ConfigurePhase(Phase phase)
    {
        quiltRenderer.autoRender = phase.mode.HasValue;
        phaseExpectedViews = phase.mode.HasValue ? 48 / (int)phase.mode.Value : 0;
        if (phase.mode.HasValue) quiltRenderer.viewRenderMode = phase.mode.Value;
        if (phase.reuseCameraOwnedVolumeStack.HasValue && sampleUrpCamera != null)
            sampleUrpCamera.reuseCameraOwnedVolumeStackForQuilt = phase.reuseCameraOwnedVolumeStack.Value;
        volumeStackCallbackFrame = -1;
        volumeStackCallbacksThisFrame = 0;
        volumeStackEndCallbacksThisFrame = 0;
        volumeStackFirstThisFrame = null;
        volumeStackIdentityStableThisFrame = true;
        volumeManagerStackMatchedThisFrame = true;
        volumeStackBeginEndMatchedThisFrame = true;
        volumeStackLeaseActiveAtBegin = false;
        volumeStackViaScriptingAtBegin = false;
        phaseCameraStackReuseVerifiedSamples = 0;
        phaseFrame = 0;
        lastObservedSubmissionTicks = quiltRenderer.LastCpuSubmissionTicks;
        lastObservedRenderedViews = quiltRenderer.LastRenderedViewCount;
        lastWallTicks = Stopwatch.GetTimestamp();
    }

    static string TextureSize()
    {
        var texture = quiltRenderer != null ? quiltRenderer.QuiltTexture : null;
        return texture == null ? "null" : texture.width + "x" + texture.height;
    }

    static void SerializeSamples(Phase phase)
    {
        string sceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        string textureSize = TextureSize();
        for (int i = 0; i < measuredSamples; i++)
        {
            var sample = sampleBuffer[i];
            csv.AppendLine(string.Join(",", new[]
            {
                Csv(sceneName), Csv(phase.name), I(i + 1), I(sample.unityFrame), F(sample.simulationTime),
                F(sample.wallFrameMs), F(sample.wallFrameMs > 0.0 ? 1000.0 / sample.wallFrameMs : 0.0), F(FixedSimulationDelta),
                Csv(FormatDirectorTimes(sample.directors)), I(sample.activeVfx), Csv(FormatVector(sample.cameraPosition)), F(sample.cpuMs), F(sample.gpuMs),
                sample.gpuTimestamp.ToString(CultureInfo.InvariantCulture), F(sample.submissionMs), F(sample.viewMs), I(sample.renderedViews), I(sample.cameraCallbacks), Csv(phase.mode.HasValue ? textureSize : "single-camera"), I(gpuUnique),
                Csv(sourceCamera.name), Csv(cachedRendererName), I(cachedRendererIndex), Csv(cachedRenderType), I(cachedVolumeMask), Csv(cachedVolumeTrigger),
                F(sample.nearClip), F(sample.farClip), "not_started", "not_measured", F(evaluationCameraDistance), B(sample.cameraStackReuseRequested), B(sample.cameraStackReuseVerified)
            }));
        }
    }

    static string FormatDirectorTimes(double[] values)
    {
        var builder = new StringBuilder(64);
        for (int i = 0; i < values.Length; i++)
        {
            if (i > 0) builder.Append(';');
            builder.Append(cachedDirectors[i] != null ? cachedDirectors[i].name : "missing");
            builder.Append('=');
            builder.Append(values[i].ToString("R", CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    static void AppendSummaryRow(Phase phase, string suffix, double wallMs, double cpuMs, double gpuMs, double submissionMs, double viewMs, int renderedViews, string textureSize)
    {
        csv.AppendLine(string.Join(",", new[]
        {
            Csv(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name), Csv(phase.name + suffix), I(SampleFrames), I(Time.frameCount), F(Time.time),
            F(wallMs), F(wallMs > 0.0 ? 1000.0 / wallMs : 0.0), F(Time.captureDeltaTime), Csv(GetDirectorTimes()), I(CountActiveVfx()), Csv(FormatVector(sampleCamera.transform.position)),
            F(cpuMs), F(gpuMs), "", F(submissionMs), F(viewMs), I(renderedViews), Csv(textureSize), I(gpuUnique), Csv(sourceCamera.name), Csv(sampleDataRenderer()),
            I(sampleDataRendererIndex()), Csv(sampleDataRenderType()), Csv(sampleDataVolumeMask()), Csv(sampleDataVolumeTrigger()), F(sampleCamera.nearClipPlane), F(sampleCamera.farClipPlane), "not_started", "not_measured", F(evaluationCameraDistance),
            B(phase.reuseCameraOwnedVolumeStack == true), I(phaseCameraStackReuseVerifiedSamples)
        }));
        summaryCsv.AppendLine(string.Join(",", new[]
        {
            Csv(phase.name + suffix), I(phaseExpectedViews), F(wallMs), F(wallMs), F(cpuMs), F(gpuMs), F(submissionMs), F(viewMs),
            I(phaseValidCpu), I(phaseValidGpu), F(phaseValidGpu / (double)SampleFrames), I(phaseWallSampleCount), I(renderedViews), I(phaseUnverifiedRenderFrames), Csv(textureSize),
            F(maxCameraPositionDelta), F(maxCameraRotationDelta), F(maxCameraFovDelta), Csv(quiltRenderer != null ? quiltRenderer.LastAutoRenderError : ""),
            B(phase.reuseCameraOwnedVolumeStack == true), I(phaseCameraStackReuseVerifiedSamples)
        }));
    }

    static void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode && running)
        {
            failure = "Play Mode exited before completion";
            Finish();
        }
    }

    static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera != sampleCamera) return;
        int frame = Time.frameCount;
        if (cameraCallbackFrame != frame)
        {
            cameraCallbackFrame = frame;
            cameraCallbackCount = 0;
        }
        cameraCallbackCount++;

        if (volumeStackCallbackFrame != frame) return;
        volumeStackEndCallbacksThisFrame++;
        VolumeStack current = sampleCameraData != null ? sampleCameraData.volumeStack : null;
        bool leaseActive = sampleUrpCamera != null && sampleUrpCamera.CameraOwnedVolumeStackLeaseActive;
        bool viaScripting = sampleCamera != null && sampleCamera.GetVolumeFrameworkUpdateMode() == VolumeFrameworkUpdateMode.ViaScripting;
        if (current == null || current != volumeStackFirstThisFrame || !leaseActive || !volumeStackLeaseActiveAtBegin || !viaScripting || !volumeStackViaScriptingAtBegin || VolumeManager.instance.stack != current)
            volumeStackBeginEndMatchedThisFrame = false;
    }

    static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
    {
        if (camera != sampleCamera) return;
        int frame = Time.frameCount;
        if (volumeStackCallbackFrame != frame)
        {
            volumeStackCallbackFrame = frame;
            volumeStackCallbacksThisFrame = 0;
            volumeStackEndCallbacksThisFrame = 0;
            volumeStackFirstThisFrame = null;
            volumeStackIdentityStableThisFrame = true;
            volumeManagerStackMatchedThisFrame = true;
            volumeStackBeginEndMatchedThisFrame = true;
            volumeStackLeaseActiveAtBegin = sampleUrpCamera != null && sampleUrpCamera.CameraOwnedVolumeStackLeaseActive;
            volumeStackViaScriptingAtBegin = sampleCamera.GetVolumeFrameworkUpdateMode() == VolumeFrameworkUpdateMode.ViaScripting;
        }

        volumeStackCallbacksThisFrame++;
        VolumeStack current = sampleCameraData != null ? sampleCameraData.volumeStack : null;
        if (current == null)
        {
            volumeStackIdentityStableThisFrame = false;
            volumeManagerStackMatchedThisFrame = false;
            volumeStackBeginEndMatchedThisFrame = false;
            return;
        }
        if (volumeStackFirstThisFrame == null) volumeStackFirstThisFrame = current;
        else if (volumeStackFirstThisFrame != current) volumeStackIdentityStableThisFrame = false;
        if (VolumeManager.instance.stack != current) volumeManagerStackMatchedThisFrame = false;
        if (!volumeStackLeaseActiveAtBegin || !volumeStackViaScriptingAtBegin) volumeStackBeginEndMatchedThisFrame = false;
    }

    static string sampleDataRenderer() => cachedRendererName;
    static int sampleDataRendererIndex() => cachedRendererIndex;
    static string sampleDataRenderType() => cachedRenderType;
    static string sampleDataVolumeMask() => cachedVolumeMask.ToString(CultureInfo.InvariantCulture);
    static string sampleDataVolumeTrigger() => cachedVolumeTrigger;
    static int CountActiveVfx()
    {
        int count = 0;
        if (cachedVfx == null) return count;
        for (int i = 0; i < cachedVfx.Length; i++) if (cachedVfx[i] != null && cachedVfx[i].isActiveAndEnabled) count++;
        return count;
    }
    static string GetDirectorTimes()
    {
        if (cachedDirectors == null) return string.Empty;
        for (int i = 0; i < cachedDirectors.Length; i++) directorTimes[i] = cachedDirectors[i] != null ? cachedDirectors[i].time : double.NaN;
        return FormatDirectorTimes(directorTimes);
    }
    static string FormatVector(Vector3 value) => value.x.ToString("R", CultureInfo.InvariantCulture) + ";" + value.y.ToString("R", CultureInfo.InvariantCulture) + ";" + value.z.ToString("R", CultureInfo.InvariantCulture);

    static void Finish()
    {
        if (!running) return;
        running = false;
        EditorApplication.update -= Tick;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
        try
        {
            if (quiltRenderer != null && quiltRenderer.QuiltTexture != null) SaveQuiltEvidence();
            if (csv != null && !string.IsNullOrEmpty(outputFile))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputFile));
                File.WriteAllText(outputFile, csv.ToString(), Encoding.ASCII);
                File.WriteAllText(Path.ChangeExtension(outputFile, ".status.txt"), failure ?? (gateOnly ? "gate_complete" : "complete"), Encoding.ASCII);
                File.WriteAllText(Path.ChangeExtension(outputFile, ".summary.csv"), summaryCsv.ToString(), Encoding.ASCII);
            }
            UnityEngine.Debug.Log("URP_SAMPLE_EVALUATION_DONE|" + (failure ?? "complete") + "|" + outputFile);
        }
        catch (Exception error)
        {
            UnityEngine.Debug.LogError("URP_SAMPLE_EVALUATION_WRITE_FAILED|" + error.GetType().Name);
        }
        RestoreRunState();
    }

    static void SaveQuiltEvidence()
    {
        var source = quiltRenderer.QuiltTexture;
        if (source == null || !source.IsCreated()) return;
        var previousActive = RenderTexture.active;
        Texture2D readback = null;
        try
        {
            RenderTexture.active = source;
            readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0, false);
            readback.Apply(false, false);
            var pngPath = Path.ChangeExtension(outputFile, ".png");
            File.WriteAllBytes(pngPath, readback.EncodeToPNG());

            var pixels = readback.GetPixels32();
            int tileWidth = source.width / QuiltColumns;
            int tileHeight = source.height / QuiltRows;
            int strideX = Mathf.Max(1, tileWidth / 32);
            int strideY = Mathf.Max(1, tileHeight / 24);
            int nonEmptyTiles = 0;
            long magentaSamples = 0;
            int sampledTiles = 0;
            for (int tileY = 0; tileY < QuiltRows; tileY++)
            for (int tileX = 0; tileX < QuiltColumns; tileX++)
            {
                bool nonEmpty = false;
                for (int y = tileY * tileHeight; y < (tileY + 1) * tileHeight; y += strideY)
                for (int x = tileX * tileWidth; x < (tileX + 1) * tileWidth; x += strideX)
                {
                    var color = pixels[y * source.width + x];
                    sampledTiles++;
                    if (color.r + color.g + color.b > 12) nonEmpty = true;
                    if (color.r > 240 && color.g < 32 && color.b > 240) magentaSamples++;
                }
                if (nonEmpty) nonEmptyTiles++;
            }

            var report = "{\n" +
                "  \"scene\": \"" + Json(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name) + "\",\n" +
                "  \"png\": \"" + Json(pngPath) + "\",\n" +
                "  \"width\": " + source.width + ",\n" +
                "  \"height\": " + source.height + ",\n" +
                "  \"views\": " + quiltRenderer.LastRenderedViewCount + ",\n" +
                "  \"nonEmptyTiles\": " + nonEmptyTiles + ",\n" +
                "  \"totalTiles\": " + (QuiltColumns * QuiltRows) + ",\n" +
                "  \"sampledPixels\": " + sampledTiles + ",\n" +
                "  \"sampledMagentaPixels\": " + magentaSamples + ",\n" +
                "  \"rendererIndex\": " + sampleDataRendererIndex() + ",\n" +
                "  \"renderer\": \"" + Json(sampleDataRenderer()) + "\",\n" +
                "  \"renderType\": \"" + Json(sampleDataRenderType()) + "\",\n" +
                "  \"volumeMask\": \"" + Json(sampleDataVolumeMask()) + "\",\n" +
                "  \"volumeTrigger\": \"" + Json(sampleDataVolumeTrigger()) + "\",\n" +
                "  \"activeVfx\": " + CountActiveVfx() + ",\n" +
                "  \"directorTimes\": \"" + Json(GetDirectorTimes()) + "\",\n" +
                "  \"bridge\": \"not_started\",\n" +
            "  \"transport\": \"not_measured\",\n" +
            "  \"autoRenderProxyUnverifiedFrames\": " + phaseUnverifiedRenderFrames + "\n" + "}\n";
            File.WriteAllText(Path.ChangeExtension(outputFile, ".image-audit.json"), report, Encoding.ASCII);
        }
        finally
        {
            RenderTexture.active = previousActive;
            if (readback != null) UnityEngine.Object.DestroyImmediate(readback);
        }
    }

    static void RestoreCaptureDeltaTime()
    {
        if (Mathf.Abs(Time.captureDeltaTime - FixedSimulationDelta) < 0.000001f) Time.captureDeltaTime = originalCaptureDeltaTime;
    }

    static void RestoreQuality()
    {
        if (qualityChanged)
        {
            QualitySettings.SetQualityLevel(originalQualityLevel);
            qualityChanged = false;
        }
    }

    static void RestoreCameraDrivers()
    {
        try
        {
            if (cameraDrivers != null && originalDriverEnabled != null)
            {
                for (int i = 0; i < cameraDrivers.Length && i < originalDriverEnabled.Length; i++)
                {
                    if (cameraDrivers[i] == null) continue;
                    try { cameraDrivers[i].enabled = originalDriverEnabled[i]; }
                    catch (Exception error) { LogRestoreFailure("camera_driver_" + i, error); }
                }
            }
        }
        finally
        {
            cameraDrivers = null;
            originalDriverEnabled = null;
        }
    }

    static void RestoreRunState()
    {
        running = false;
        EditorApplication.update -= Tick;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;

        try { RestoreCaptureDeltaTime(); }
        catch (Exception error) { LogRestoreFailure("capture_delta_time", error); }
        try { Time.timeScale = originalTimeScale; }
        catch (Exception error) { LogRestoreFailure("time_scale", error); }
        try { RestoreQuality(); }
        catch (Exception error) { LogRestoreFailure("quality", error); }
        try { RestoreCameraDrivers(); }
        catch (Exception error) { LogRestoreFailure("camera_drivers", error); }
        try { Cleanup(); }
        catch (Exception error) { LogRestoreFailure("temporary_objects", error); }
    }

    static void LogRestoreFailure(string operation, Exception error)
    {
        UnityEngine.Debug.LogError("URP_SAMPLE_EVALUATION_RESTORE_FAILED|" + Ascii(operation) + "|" + Ascii(error.GetType().Name));
    }

    static void Cleanup()
    {
        if (temporaryRoot != null) UnityEngine.Object.Destroy(temporaryRoot);
        temporaryRoot = null;
        sampleCamera = null;
        quiltRenderer = null;
        sourceCamera = null;
        sourceCameraData = null;
        sampleCameraData = null;
        sampleUrpCamera = null;
        gateOnly = false;
        cachedVfx = null;
        cachedDirectors = null;
        directorTimes = null;
        sampleBuffer = null;
        frameTimingBuffer = null;
    }

    static void ClearPhaseSamples()
    {
        wallFrames.Clear();
        cpuFrames.Clear();
        gpuFrames.Clear();
        submissions.Clear();
        viewSubmissions.Clear();
    }

    static double Percentile(List<double> values, double percentile)
    {
        if (values == null || values.Count == 0) return 0.0;
        var ordered = values.OrderBy(value => value).ToArray();
        int index = Mathf.Clamp(Mathf.CeilToInt((float)(percentile * ordered.Length)) - 1, 0, ordered.Length - 1);
        return ordered[index];
    }

    static string Csv(string value) => "\"" + (value ?? string.Empty).Replace("\"", "\"\"").Replace("\r", " ").Replace("\n", " ") + "\"";
    static string Json(string value) => (value ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
    static string Ascii(string value) => new string((value ?? string.Empty).Where(character => character >= 0x20 && character <= 0x7e).ToArray());
    static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
    static string F(double value) => value.ToString("R", CultureInfo.InvariantCulture);
    static string B(bool value) => value ? "true" : "false";
}
