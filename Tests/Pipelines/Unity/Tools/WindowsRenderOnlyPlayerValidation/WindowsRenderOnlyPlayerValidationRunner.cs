using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave;
using ViewWeave.HDRP;
using ViewWeave.PlayerValidation;

namespace ViewWeave.WindowsRenderOnlyPlayerValidation
{
    public sealed class WindowsRenderOnlyPlayerValidationRunner : MonoBehaviour
    {
        const int ExpectedViews = 4;
        const int ExpectedColumns = 2;
        const int ExpectedRows = 2;
        const int TileSize = 256;
        const float ExpectedExposureEv = 4f;
        const float ExpectedDirectionalLux = 10f;
        const double WarmupDeadlineSeconds = 45.0;
        const double RunDeadlineSeconds = 90.0;
        const double RenderReadbackReadyDeadlineSeconds = 15.0;
        const int WarmupFrameLimit = 60;

        [SerializeField] Camera sourceCamera;
        [SerializeField] QuiltRenderer quilt;
        [SerializeField] PlayerValidationOutput output;
        [SerializeField] HdrpQuiltCamera hdrpGuard;
        [SerializeField] Volume globalVolume;
        [SerializeField] Transform movingTarget;
        [SerializeField] Light fixtureDirectionalLight;

        Evidence evidence;
        string evidenceDirectory;
        readonly List<ViewCallbackEvidence> viewCallbackRecords = new List<ViewCallbackEvidence>();
        readonly List<LightStageEvidence> lightStageRecords = new List<LightStageEvidence>();
        bool[] viewImageSaved;

        void Awake()
        {
            Application.runInBackground = true;
            evidenceDirectory = Argument("--evidence-dir");
            if (string.IsNullOrWhiteSpace(evidenceDirectory))
                evidenceDirectory = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".local", "windows-render-only-player"));
            else
                evidenceDirectory = Path.GetFullPath(evidenceDirectory);
            Directory.CreateDirectory(evidenceDirectory);
            evidence = new Evidence
            {
                startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                buildGuid = Application.buildGUID,
                isEditor = Application.isEditor,
                development = Debug.isDebugBuild,
                graphicsDeviceType = SystemInfo.graphicsDeviceType.ToString(),
                graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                graphicsMultiThreaded = SystemInfo.graphicsMultiThreaded,
                ok = false
            };
        }

        void Start() { StartCoroutine(RunGuarded()); }

        IEnumerator RunGuarded()
        {
            yield return Drive(RunValidation());
            evidence.endedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            evidence.ok = string.IsNullOrEmpty(evidence.error) && evidence.allGatesPassed;
            try { File.WriteAllText(Path.Combine(evidenceDirectory, "summary.json"), JsonUtility.ToJson(evidence, true)); }
            catch (Exception error) { evidence.ok = false; evidence.error = "EvidenceWriteFailed: " + Sanitize(error.Message); }
            Application.Quit(evidence.ok ? 0 : 1);
        }

        IEnumerator RunValidation()
        {
            double deadline = Time.realtimeSinceStartupAsDouble + RunDeadlineSeconds;
            Require(Application.platform == RuntimePlatform.WindowsPlayer && !Application.isEditor, "Windows standalone Player is required.");
            Require(!Debug.isDebugBuild, "Release Player is required.");
            Require(SystemInfo.graphicsDeviceType == GraphicsDeviceType.Direct3D12, "Direct3D12 is required.");
            Require(SystemInfo.graphicsMultiThreaded, "Graphics multithreading is required.");
            RenderPipelineAsset activePipeline = GraphicsSettings.currentRenderPipeline;
            evidence.pipelineAsset = activePipeline == null ? string.Empty : activePipeline.GetType().FullName;
            Require(evidence.pipelineAsset == "UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset", "Active render pipeline is not HDRP.");
            Require(sourceCamera != null && quilt != null && output != null && hdrpGuard != null && globalVolume != null && movingTarget != null,
                "Render-only Player fixture references are incomplete.");
            Require(!sourceCamera.enabled && !quilt.autoRender, "The dedicated quilt camera must be disabled and rendering must be manual.");
            Require(sourceCamera.GetComponent("ViewWeaveBridgeSession") == null && sourceCamera.GetComponent("BridgeWorkerQuiltOutput") == null,
                "A Bridge output/session must not be present in the render-only fixture.");
            RequireFixtureLightState("startup");
            Require(quilt.OutputBehaviour == output, "Non-native PlayerValidationOutput is not assigned.");
            Require(hdrpGuard.exposureMode == HdrpQuiltExposureMode.ManualFixed && Mathf.Approximately(hdrpGuard.fallbackFixedExposureEV, ExpectedExposureEv),
                "HDRP manual fixed exposure must be EV 4.");
            Require(globalVolume.isGlobal && globalVolume.weight >= 0.999f && globalVolume.sharedProfile != null,
                "A global HDRP Volume profile is required.");
            Renderer[] sceneRenderers = FindObjectsOfType<Renderer>();
            evidence.scenePath = gameObject.scene.path;
            evidence.sceneRendererCount = sceneRenderers.Length;
            evidence.sceneRendererNames = sceneRenderers.Select(renderer => renderer == null ? string.Empty : Sanitize(renderer.name)).ToArray();
            evidence.sourceCameraPosition = sourceCamera.transform.position;
            evidence.sourceCameraForward = sourceCamera.transform.forward;
            evidence.sourceCameraFieldOfView = sourceCamera.fieldOfView;
            evidence.sourceCameraCullingMask = sourceCamera.cullingMask;
            evidence.movingTargetPosition = movingTarget.position;
            evidence.movingTargetViewport = sourceCamera.WorldToViewportPoint(movingTarget.position);
            Renderer targetRenderer = movingTarget.GetComponent<Renderer>();
            evidence.movingTargetRendererEnabled = targetRenderer != null && targetRenderer.enabled && targetRenderer.gameObject.activeInHierarchy;
            evidence.movingTargetInCameraFrustum = targetRenderer != null && GeometryUtility.TestPlanesAABB(
                GeometryUtility.CalculateFrustumPlanes(sourceCamera), targetRenderer.bounds);
            if (targetRenderer != null && targetRenderer.sharedMaterial != null)
            {
                Color materialColor = targetRenderer.sharedMaterial.HasProperty("_BaseColor")
                    ? targetRenderer.sharedMaterial.GetColor("_BaseColor") : targetRenderer.sharedMaterial.color;
                evidence.movingTargetMaterialColor = materialColor;
            }

            Exposure exposure;
            Require(globalVolume.sharedProfile.TryGet(out exposure) && exposure != null && exposure.active &&
                exposure.mode.value == ExposureMode.Fixed && Mathf.Approximately(exposure.fixedExposure.value, ExpectedExposureEv) &&
                exposure.fixedExposure.overrideState, "HDRP fixed exposure EV 4 is not configured.");
            evidence.fixedExposureMode = exposure.mode.value.ToString();
            evidence.fixedExposureEv = exposure.fixedExposure.value;
            Bloom authoredBloom;
            Require(globalVolume.sharedProfile.TryGet(out authoredBloom) && authoredBloom != null && authoredBloom.active &&
                authoredBloom.intensity.overrideState && Mathf.Approximately(authoredBloom.intensity.value, 0f),
                "HDRP fixture Volume must explicitly override active Bloom intensity to 0.");
            Fog authoredFog;
            Require(globalVolume.sharedProfile.TryGet(out authoredFog) && authoredFog != null && authoredFog.active &&
                authoredFog.enabled.overrideState && !authoredFog.enabled.value,
                "HDRP fixture Volume must explicitly keep Fog disabled.");
            VisualEnvironment authoredEnvironment;
            Require(globalVolume.sharedProfile.TryGet(out authoredEnvironment) && authoredEnvironment != null && authoredEnvironment.active &&
                authoredEnvironment.skyType.overrideState && authoredEnvironment.skyType.value == 0 &&
                authoredEnvironment.cloudType.overrideState && authoredEnvironment.cloudType.value == 0,
                "HDRP fixture Volume must explicitly set SkyType and CloudType to 0.");
            IndirectLightingController authoredIndirectLighting;
            Require(globalVolume.sharedProfile.TryGet(out authoredIndirectLighting) && authoredIndirectLighting != null && authoredIndirectLighting.active &&
                authoredIndirectLighting.indirectDiffuseLightingMultiplier.overrideState &&
                Mathf.Approximately(authoredIndirectLighting.indirectDiffuseLightingMultiplier.value, 0f),
                "HDRP fixture Volume must explicitly set indirect diffuse lighting multiplier to 0.");
            Tonemapping authoredTonemapping;
            Require(globalVolume.sharedProfile.TryGet(out authoredTonemapping) && authoredTonemapping != null && authoredTonemapping.active &&
                authoredTonemapping.mode.overrideState && authoredTonemapping.mode.value == TonemappingMode.Neutral,
                "HDRP fixture Volume must explicitly set Neutral tonemapping.");

            QuiltSettingsSnapshot settings = QuiltSettingsSnapshot.Capture(quilt.settings);
            evidence.requestedViews = quilt.settings.views;
            evidence.requestedColumns = quilt.settings.columns;
            evidence.requestedRows = quilt.settings.rows;
            evidence.requestedQuiltWidth = quilt.settings.quiltWidth;
            evidence.requestedQuiltHeight = quilt.settings.quiltHeight;
            evidence.manualExposureMode = hdrpGuard.exposureMode.ToString();
            evidence.manualExposureEv = hdrpGuard.fallbackFixedExposureEV;
            viewImageSaved = new bool[ExpectedViews];
            Require(settings.views == ExpectedViews && settings.columns == ExpectedColumns && settings.rows == ExpectedRows &&
                settings.width == TileSize * ExpectedColumns && settings.height == TileSize * ExpectedRows,
                "Fixture must use the configured compact 4-view quilt.");

            double warmupDeadline = Math.Min(deadline, Time.realtimeSinceStartupAsDouble + WarmupDeadlineSeconds);
            for (int frame = 0; frame < WarmupFrameLimit && Time.realtimeSinceStartupAsDouble < warmupDeadline; frame++)
            {
                evidence.warmupFrames = frame + 1;
                yield return null;
            }
            Require(Time.realtimeSinceStartupAsDouble < deadline, "Player validation exceeded its run deadline during warmup.");

            CaptureSceneDiagnostics();
            RequireFixtureLightState("before-direct-render");
            Color32[] directPixels = null;
            try
            {
                directPixels = RenderDirectSourceCamera();
                evidence.directStandardRequest = AnalyzeDirect(directPixels, TileSize, TileSize);
                SavePng(directPixels, TileSize, TileSize, "direct-standard-request.png");
                evidence.directRequestSucceeded = true;
            }
            catch (Exception exception)
            {
                evidence.directRequestError = Sanitize(exception.GetType().Name + ": " + exception.Message);
            }
            CaptureGlobalVolumeStackDiagnostics();
            RequireFixtureLightState("after-direct-render");

            double renderReadyStarted = Time.realtimeSinceStartupAsDouble;
            double renderReadyDeadline = Math.Min(deadline, renderReadyStarted + RenderReadbackReadyDeadlineSeconds);
            bool renderReady = false;
            while (!renderReady && Time.realtimeSinceStartupAsDouble < renderReadyDeadline)
            {
                evidence.readinessAttempts++;
                renderReady = TryRenderAndReadbackReadiness(out string readinessError);
                if (!renderReady)
                {
                    evidence.readinessLastError = readinessError;
                    yield return null;
                }
            }
            evidence.readinessElapsedMilliseconds = (float)((Time.realtimeSinceStartupAsDouble - renderReadyStarted) * 1000.0);
            evidence.readbackReady = renderReady;
            Require(renderReady, "HDRP RenderQuilt/readback did not become ready within its bounded deadline: " + evidence.readinessLastError);
            evidence.hdrpHistoryCameraCount = hdrpGuard.HistoryCameraCount;
            evidence.hdrpHistoryStatus = Sanitize(hdrpGuard.HistoryStatus);
            for (int frame = 0; frame < 2; frame++)
            {
                Require(Time.realtimeSinceStartupAsDouble < deadline, "Player validation exceeded its run deadline after readiness render.");
                yield return null;
            }

            Color32[] before = RenderAndRead("before");
            Require(Time.realtimeSinceStartupAsDouble < deadline, "Player validation deadline expired after before-capture.");
            evidence.before = AnalyzeQuilt(before, quilt.QuiltTexture.width, quilt.QuiltTexture.height);
            SavePng(before, quilt.QuiltTexture.width, quilt.QuiltTexture.height, "before.png");
            RequireFixtureLightState("after-quilt-before-capture");
            RequireSettingsUnchanged(settings, "before movement");
            RequireExposureUnchanged(exposure);
            RequireManualExposureUnchanged();
            Require(evidence.before.allTilesNonBlank, "One or more before-capture tiles are blank.");
            Require(evidence.before.allTilesHaveRedAndGreen, "Every before-capture tile must contain both red and green evidence.");
            Require(evidence.before.allViewsDiffer, "The four before-capture view tiles do not show sufficient view-to-view difference.");

            Vector3 oldPosition = movingTarget.localPosition;
            movingTarget.localPosition += new Vector3(0.45f, 0.18f, 0.20f);
            evidence.movementDelta = Vector3.Distance(oldPosition, movingTarget.localPosition);
            yield return null;
            Require(Time.realtimeSinceStartupAsDouble < deadline, "Player validation exceeded its run deadline before after-capture.");
            Color32[] after = RenderAndRead("after");
            Require(Time.realtimeSinceStartupAsDouble < deadline, "Player validation deadline expired after after-capture.");
            evidence.after = AnalyzeQuilt(after, quilt.QuiltTexture.width, quilt.QuiltTexture.height);
            evidence.changedPixels = CountChangedPixels(before, after);
            SavePng(after, quilt.QuiltTexture.width, quilt.QuiltTexture.height, "after.png");
            RequireFixtureLightState("after-movement-capture");
            Require(evidence.after.allTilesNonBlank, "One or more after-capture tiles are blank.");
            Require(evidence.after.allTilesHaveRedAndGreen, "Every after-capture tile must contain both red and green evidence.");
            Require(evidence.after.allViewsDiffer, "The four after-capture view tiles do not show sufficient view-to-view difference.");
            Require(evidence.changedPixels >= 64, "Moving the asymmetric target did not produce a measurable pixel change.");
            RequireSettingsUnchanged(settings, "after movement");
            RequireExposureUnchanged(exposure);
            RequireManualExposureUnchanged();

            evidence.allGatesPassed = true;
        }

        void CaptureSceneDiagnostics()
        {
            evidence.scenePath = gameObject.scene.path;
            evidence.sourceCameraPosition = sourceCamera.transform.position;
            evidence.sourceCameraForward = sourceCamera.transform.forward;
            evidence.sourceCameraFieldOfView = sourceCamera.fieldOfView;
            evidence.sourceCameraCullingMask = sourceCamera.cullingMask;
            evidence.sourceCameraClearFlags = sourceCamera.clearFlags.ToString();
            evidence.sourceCameraBackground = sourceCamera.backgroundColor;
            HDAdditionalCameraData cameraData = sourceCamera.GetComponent<HDAdditionalCameraData>();
            evidence.hdrpCameraDataPresent = cameraData != null;
            if (cameraData != null)
            {
                evidence.hdrpCameraVolumeLayerMask = cameraData.volumeLayerMask.value;
                evidence.hdrpCameraCustomRenderingSettings = cameraData.customRenderingSettings;
            }
            Renderer[] sceneRenderers = FindObjectsOfType<Renderer>();
            evidence.sceneRendererCount = sceneRenderers.Length;
            evidence.sceneRendererNames = sceneRenderers.Select(renderer => renderer == null ? string.Empty : Sanitize(renderer.name)).ToArray();
            evidence.sceneObjects = new[]
            {
                CaptureObjectDiagnostics("AsymmetricRedNearTarget"),
                CaptureObjectDiagnostics("AsymmetricGreenFarTarget")
            };
            evidence.hdrpHistoryCameraCountBeforeRender = hdrpGuard.HistoryCameraCount;
            evidence.hdrpHistoryStatusBeforeRender = Sanitize(hdrpGuard.HistoryStatus);
            evidence.quiltRendererType = quilt.GetType().FullName;
            evidence.quiltRendererEnabled = quilt.enabled;
            evidence.quiltSourceCameraMatches = quilt.sourceCamera == sourceCamera;
            evidence.manualExposureMode = hdrpGuard.exposureMode.ToString();
            evidence.manualExposureEv = hdrpGuard.fallbackFixedExposureEV;
        }

        SceneObjectDiagnostics CaptureObjectDiagnostics(string objectName)
        {
            GameObject target = GameObject.Find(objectName);
            Renderer renderer = target != null ? target.GetComponent<Renderer>() : null;
            Material material = renderer != null ? renderer.sharedMaterial : null;
            Color materialColor = Color.clear;
            string colorProperty = string.Empty;
            if (material != null)
            {
                string[] properties = { "_BaseColor", "_UnlitColor", "_Color" };
                foreach (string property in properties)
                {
                    if (!material.HasProperty(property)) continue;
                    materialColor = material.GetColor(property);
                    colorProperty = property;
                    break;
                }
            }
            Bounds bounds = renderer != null ? renderer.bounds : default;
            return new SceneObjectDiagnostics
            {
                name = objectName,
                found = target != null,
                sceneName = target != null ? Sanitize(target.scene.name) : string.Empty,
                scenePath = target != null ? Sanitize(target.scene.path) : string.Empty,
                activeInHierarchy = target != null && target.activeInHierarchy,
                rendererEnabled = renderer != null && renderer.enabled,
                layer = target != null ? target.layer : -1,
                cullingMaskIncludesLayer = target != null && (sourceCamera.cullingMask & (1 << target.layer)) != 0,
                position = target != null ? target.transform.position : default,
                boundsCenter = bounds.center,
                boundsSize = bounds.size,
                inCameraFrustum = renderer != null && GeometryUtility.TestPlanesAABB(GeometryUtility.CalculateFrustumPlanes(sourceCamera), bounds),
                viewportPosition = target != null ? sourceCamera.WorldToViewportPoint(target.transform.position) : default,
                shaderName = material != null && material.shader != null ? Sanitize(material.shader.name) : string.Empty,
                colorProperty = colorProperty,
                materialColor = materialColor
            };
        }

        void CaptureQuiltSnapshotDiagnostics()
        {
            QuiltRenderSnapshot snapshot = quilt.LatestSnapshot;
            evidence.quiltSnapshotValid = snapshot.IsValid;
            if (!snapshot.IsValid) return;
            evidence.quiltSnapshotViewAspect = snapshot.Geometry.viewAspect;
            evidence.quiltSnapshotFieldOfView = snapshot.Geometry.fieldOfView;
            evidence.quiltSnapshotFocusDistance = snapshot.Geometry.cameraDistance;
            evidence.quiltSnapshotViewCone = snapshot.Geometry.viewCone;
            evidence.quiltSnapshotNearClip = snapshot.Geometry.nearClip;
            evidence.quiltSnapshotFarClip = snapshot.Geometry.farClip;
            evidence.quiltSnapshotCameraPosition = new Vector3(snapshot.Geometry.cameraPositionX, snapshot.Geometry.cameraPositionY, snapshot.Geometry.cameraPositionZ);
            evidence.quiltSnapshotCameraRotation = new Quaternion(snapshot.Geometry.cameraRotationX, snapshot.Geometry.cameraRotationY,
                snapshot.Geometry.cameraRotationZ, snapshot.Geometry.cameraRotationW);
            evidence.quiltSnapshotPhysicalFramingEnabled = snapshot.PhysicalFraming.enabled;
            evidence.quiltSnapshotApertureWidthMillimeters = snapshot.PhysicalFraming.visibleApertureWidthMillimeters;
            evidence.quiltSnapshotApertureHeightMillimeters = snapshot.PhysicalFraming.visibleApertureHeightMillimeters;
            evidence.quiltSnapshotUnitsPerMeter = snapshot.PhysicalFraming.unitsPerMeter;
            evidence.quiltSnapshotRevision = snapshot.Revision;
            evidence.quiltSnapshotRenderedViews = snapshot.LastRenderedViewCount;
        }

        void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            if (camera == null) return;
            int logicalView = camera == sourceCamera ? -1 : -2;
            if (logicalView == -2)
            {
                for (int view = 0; view < ExpectedViews; view++)
                {
                    if (hdrpGuard.GetHistoryCamera(view) != camera) continue;
                    logicalView = view;
                    break;
                }
            }
            if (logicalView == -2) return;

            var record = new ViewCallbackEvidence { logicalView = logicalView };
            try
            {
                record.cameraName = Sanitize(camera.name);
                record.cameraEnabled = camera.enabled;
                record.position = camera.transform.position;
                record.forward = camera.transform.forward;
                record.fieldOfView = camera.fieldOfView;
                record.aspect = camera.aspect;
                record.nearClip = camera.nearClipPlane;
                record.farClip = camera.farClipPlane;
                record.cullingMask = camera.cullingMask;
                record.projectionRows = new[]
                {
                    camera.projectionMatrix.GetRow(0), camera.projectionMatrix.GetRow(1),
                    camera.projectionMatrix.GetRow(2), camera.projectionMatrix.GetRow(3)
                };
                RenderTexture cameraTarget = camera.targetTexture;
                RenderTexture activeTarget = RenderTexture.active;
                record.cameraTargetName = cameraTarget != null ? Sanitize(cameraTarget.name) : string.Empty;
                record.cameraTargetWidth = cameraTarget != null ? cameraTarget.width : 0;
                record.cameraTargetHeight = cameraTarget != null ? cameraTarget.height : 0;
                record.cameraTargetFormat = cameraTarget != null ? cameraTarget.graphicsFormat.ToString() : string.Empty;
                record.cameraTargetSrgb = cameraTarget != null && cameraTarget.sRGB;
                record.cameraTargetMsaa = cameraTarget != null ? cameraTarget.antiAliasing : 0;
                record.activeTargetName = activeTarget != null ? Sanitize(activeTarget.name) : string.Empty;
                record.activeTargetWidth = activeTarget != null ? activeTarget.width : 0;
                record.activeTargetHeight = activeTarget != null ? activeTarget.height : 0;

                HDAdditionalCameraData cameraData = camera.GetComponent<HDAdditionalCameraData>();
                record.cameraDataPresent = cameraData != null;
                if (cameraData != null)
                {
                    record.volumeLayerMask = cameraData.volumeLayerMask.value;
                    record.volumeAnchorOverride = cameraData.volumeAnchorOverride != null
                        ? Sanitize(cameraData.volumeAnchorOverride.name) : string.Empty;
                }

                bool foundHdCamera = TryGetExistingHDCamera(camera, out HDCamera hdCamera);
                record.hdCameraFound = foundHdCamera;
                if (foundHdCamera)
                {
                    record.hdrpPostprocessEnabled = hdCamera.frameSettings.IsEnabled(FrameSettingsField.Postprocess);
                    VolumeStack stack = hdCamera.volumeStack;
                    record.hdrpVolumeStackValid = stack != null && stack.isValid;
                    if (record.hdrpVolumeStackValid)
                    {
                        Exposure effectiveExposure = stack.GetComponent<Exposure>();
                        Bloom effectiveBloom = stack.GetComponent<Bloom>();
                        Fog effectiveFog = stack.GetComponent<Fog>();
                        VisualEnvironment effectiveEnvironment = stack.GetComponent<VisualEnvironment>();
                        IndirectLightingController effectiveIndirectLighting = stack.GetComponent<IndirectLightingController>();
                        Tonemapping effectiveTonemapping = stack.GetComponent<Tonemapping>();
                        record.effectiveExposure = effectiveExposure != null
                            ? effectiveExposure.active + ":" + effectiveExposure.mode.value + ":" + effectiveExposure.fixedExposure.value.ToString("R", CultureInfo.InvariantCulture) + ":" + effectiveExposure.compensation.value.ToString("R", CultureInfo.InvariantCulture)
                            : "missing";
                        record.effectiveBloom = effectiveBloom != null
                            ? effectiveBloom.active + ":" + effectiveBloom.intensity.value.ToString("R", CultureInfo.InvariantCulture)
                            : "missing";
                        record.effectiveFog = effectiveFog != null
                            ? effectiveFog.active + ":" + effectiveFog.enabled.value + ":" + effectiveFog.meanFreePath.value.ToString("R", CultureInfo.InvariantCulture)
                            : "missing";
                        record.effectiveVisualEnvironment = effectiveEnvironment != null
                            ? effectiveEnvironment.active + ":" + effectiveEnvironment.skyType.value
                            : "missing";
                        record.effectiveCloudType = effectiveEnvironment != null
                            ? effectiveEnvironment.active + ":" + effectiveEnvironment.cloudType.value
                            : "missing";
                        record.effectiveIndirectLighting = effectiveIndirectLighting != null
                            ? effectiveIndirectLighting.active + ":" + effectiveIndirectLighting.indirectDiffuseLightingMultiplier.value.ToString("R", CultureInfo.InvariantCulture)
                            : "missing";
                        record.effectiveTonemapping = effectiveTonemapping != null
                            ? effectiveTonemapping.active + ":" + effectiveTonemapping.mode.value
                            : "missing";
                        record.effectiveVolumeControlsMatch = effectiveEnvironment != null && effectiveEnvironment.active &&
                            effectiveEnvironment.skyType.overrideState && effectiveEnvironment.skyType.value == 0 &&
                            effectiveEnvironment.cloudType.overrideState && effectiveEnvironment.cloudType.value == 0 &&
                            effectiveIndirectLighting != null && effectiveIndirectLighting.active &&
                            effectiveIndirectLighting.indirectDiffuseLightingMultiplier.overrideState &&
                            Mathf.Approximately(effectiveIndirectLighting.indirectDiffuseLightingMultiplier.value, 0f) &&
                            effectiveTonemapping != null && effectiveTonemapping.active &&
                            effectiveTonemapping.mode.overrideState && effectiveTonemapping.mode.value == TonemappingMode.Neutral;
                    }
                }

                if (logicalView >= 0 && cameraTarget != null && cameraTarget.IsCreated() && !viewImageSaved[logicalView])
                {
                    Color32[] pixels = Readback(cameraTarget);
                    record.preCopyImage = AnalyzeDirect(pixels, cameraTarget.width, cameraTarget.height);
                    string imageName = "view-" + logicalView.ToString("D2", CultureInfo.InvariantCulture) + "-pre-copy.png";
                    SavePng(pixels, cameraTarget.width, cameraTarget.height, imageName);
                    record.preCopyImagePath = imageName;
                    viewImageSaved[logicalView] = true;
                }
                record.captureSucceeded = true;
            }
            catch (Exception exception)
            {
                record.diagnosticFailure = Sanitize(exception.GetType().Name + ": " + exception.Message);
                evidence.viewCallbackDiagnosticFailures++;
            }
            viewCallbackRecords.Add(record);
            evidence.viewCallbackRecords = viewCallbackRecords.ToArray();
        }

        static bool TryGetExistingHDCamera(Camera camera, out HDCamera hdCamera)
        {
            hdCamera = null;
            try
            {
                Type channelType = typeof(HDCamera).GetNestedType("HistoryChannel", BindingFlags.NonPublic);
                if (channelType == null) return false;
                MethodInfo tryGet = typeof(HDCamera).GetMethod("TryGet", BindingFlags.Static | BindingFlags.NonPublic,
                    null, new[] { typeof(Camera), typeof(HDCamera).MakeByRefType(), typeof(int), channelType }, null);
                if (tryGet == null) return false;
                object channel = Enum.Parse(channelType, "CustomUserHistory0");
                object[] arguments = { camera, null, 0, channel };
                bool found = (bool)tryGet.Invoke(null, arguments);
                hdCamera = found ? arguments[1] as HDCamera : null;
                return found && hdCamera != null;
            }
            catch { hdCamera = null; return false; }
        }

        Color32[] RenderDirectSourceCamera()
        {
            RenderTexture target = new RenderTexture(TileSize, TileSize, 24, RenderTextureFormat.ARGB32)
            {
                name = "WindowsRenderOnlyDirectStandardRequest",
                antiAliasing = 1,
                useMipMap = false,
                autoGenerateMips = false
            };
            bool originalGuardEnabled = hdrpGuard.enabled;
            try
            {
                Require(target.Create(), "Direct StandardRequest target creation failed.");
                hdrpGuard.enabled = false;
                var request = new RenderPipeline.StandardRequest { destination = target };
                Require(RenderPipeline.SupportsRenderRequest(sourceCamera, request), "HDRP does not support direct StandardRequest rendering for the source camera.");
                RenderPipeline.SubmitRenderRequest(sourceCamera, request);
                evidence.directRenderSubmitted = true;
                return Readback(target);
            }
            finally
            {
                hdrpGuard.enabled = originalGuardEnabled;
                target.Release();
                Destroy(target);
            }
        }

        void CaptureGlobalVolumeStackDiagnostics()
        {
            VolumeStack stack = VolumeManager.instance != null ? VolumeManager.instance.stack : null;
            evidence.globalVolumeStackValid = stack != null && stack.isValid;
            if (stack == null || !stack.isValid) return;
            Exposure exposure = stack.GetComponent<Exposure>();
            Fog fog = stack.GetComponent<Fog>();
            VisualEnvironment environment = stack.GetComponent<VisualEnvironment>();
            Bloom bloom = stack.GetComponent<Bloom>();
            evidence.globalVolumeStackExposure = exposure != null ? exposure.mode.value + ":" + exposure.fixedExposure.value.ToString("R", CultureInfo.InvariantCulture) + ":" + exposure.active : "missing";
            evidence.globalVolumeStackFog = fog != null ? fog.active + ":" + fog.enabled.value + ":" + fog.meanFreePath.value.ToString("R", CultureInfo.InvariantCulture) : "missing";
            evidence.globalVolumeStackVisualEnvironment = environment != null ? environment.active + ":" + environment.skyType.value : "missing";
            evidence.globalVolumeStackBloom = bloom != null ? bloom.active + ":" + bloom.intensity.value.ToString("R", CultureInfo.InvariantCulture) : "missing";
            evidence.effectiveHistoryCameraCountAfterDirect = hdrpGuard.HistoryCameraCount;
            evidence.effectiveHistoryStatusAfterDirect = Sanitize(hdrpGuard.HistoryStatus);
        }

        static DirectEvidence AnalyzeDirect(Color32[] pixels, int width, int height)
        {
            var result = new DirectEvidence { width = width, height = height };
            if (pixels == null) return result;
            foreach (Color32 color in pixels)
            {
                if (color.a > 8 && (color.r > 10 || color.g > 10 || color.b > 10)) result.nonBlank++;
                if (color.r > 55 && color.r > color.g * 1.25f && color.r > color.b * 1.25f) result.red++;
                if (color.g > 45 && color.g > color.r * 1.20f && color.g > color.b * 1.20f) result.green++;
                result.averageRed += color.r;
                result.averageGreen += color.g;
                result.averageBlue += color.b;
            }
            int count = Math.Max(1, pixels.Length);
            result.averageRed /= count;
            result.averageGreen /= count;
            result.averageBlue /= count;
            return result;
        }

        IEnumerator Drive(IEnumerator iterator)
        {
            try
            {
                while (true)
                {
                    bool moved;
                    object current = null;
                    try
                    {
                        moved = iterator.MoveNext();
                        if (moved) current = iterator.Current;
                    }
                    catch (Exception error)
                    {
                        evidence.error = Sanitize(error.GetType().Name + ": " + error.Message);
                        yield break;
                    }
                    if (!moved) yield break;
                    yield return current;
                }
            }
            finally { (iterator as IDisposable)?.Dispose(); }
        }

        void RenderOnce(string stage)
        {
            RequireFixtureLightState("before-quilt-" + stage);
            int submissions = output.SubmissionCount;
            quilt.RenderQuilt();
            evidence.renderCalls++;
            Require(quilt.HasRenderedQuilt && quilt.LastRenderedViewCount == ExpectedViews,
                "RenderQuilt did not render all four views during " + stage + ".");
            Require(output.SubmissionCount == submissions + 1, "Non-native output did not receive exactly one submission during " + stage + ".");
            Require(output.LastTexture == quilt.QuiltTexture, "Non-native output texture differs from the renderer quilt during " + stage + ".");
            RequireLayout(output.Layout, stage);
            RequireFixtureLightState("after-quilt-" + stage);
        }

        bool TryRenderAndReadbackReadiness(out string error)
        {
            try
            {
                RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
                RenderOnce("readiness");
                CaptureQuiltSnapshotDiagnostics();
                RenderTexture texture = quilt.QuiltTexture;
                Require(texture != null && texture.IsCreated() && texture.width == TileSize * ExpectedColumns &&
                    texture.height == TileSize * ExpectedRows, "HDRP quilt texture is not ready at the expected dimensions.");
                Color32[] pixels = Readback(texture);
                Require(pixels != null && pixels.Length == texture.width * texture.height, "HDRP quilt readback did not return a complete pixel buffer.");
                error = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                error = Sanitize(exception.GetType().Name + ": " + exception.Message);
                return false;
            }
            finally
            {
                RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
                evidence.viewCallbackRecords = viewCallbackRecords.ToArray();
            }
        }

        Color32[] RenderAndRead(string stage)
        {
            RenderOnce(stage);
            RenderTexture texture = quilt.QuiltTexture;
            Require(texture != null && texture.IsCreated(), "Quilt RenderTexture is unavailable during " + stage + ".");
            return Readback(texture);
        }

        static Color32[] Readback(RenderTexture texture)
        {
            RenderTexture previous = RenderTexture.active;
            Texture2D readback = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                RenderTexture.active = texture;
                readback.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                readback.Apply(false, false);
                return readback.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                Destroy(readback);
            }
        }

        TileEvidence AnalyzeQuilt(Color32[] pixels, int width, int height)
        {
            QuiltLayout layout = quilt.settings.GetLayout();
            var result = new TileEvidence { tiles = new TileMetric[layout.ViewCount], width = width, height = height };
            bool allNonBlank = true;
            bool allRedGreen = true;
            bool allViewsDiffer = true;
            for (int view = 0; view < layout.ViewCount; view++)
            {
                RectInt tile = layout.GetTile(view);
                var metric = new TileMetric { view = view };
                var quantizedColors = new HashSet<int>();
                long redSum = 0, greenSum = 0, blueSum = 0;
                for (int y = tile.y; y < tile.yMax; y++)
                for (int x = tile.x; x < tile.xMax; x++)
                {
                    Color32 color = pixels[y * width + x];
                    redSum += color.r; greenSum += color.g; blueSum += color.b;
                    if (color.a > 8 && (color.r > 10 || color.g > 10 || color.b > 10)) metric.nonBlank++;
                    if (color.r > 55 && color.r > color.g * 1.25f && color.r > color.b * 1.25f) metric.red++;
                    if (color.g > 45 && color.g > color.r * 1.20f && color.g > color.b * 1.20f) metric.green++;
                    quantizedColors.Add((color.r >> 4) << 8 | (color.g >> 4) << 4 | (color.b >> 4));
                }
                metric.distinctQuantizedColors = quantizedColors.Count;
                int pixelCount = Math.Max(1, tile.width * tile.height);
                metric.averageRed = (int)(redSum / pixelCount);
                metric.averageGreen = (int)(greenSum / pixelCount);
                metric.averageBlue = (int)(blueSum / pixelCount);
                result.tiles[view] = metric;
                if (metric.nonBlank < 64) allNonBlank = false;
                if (metric.red < 12 || metric.green < 12) allRedGreen = false;
                if (view > 0)
                {
                    metric.changedFromPreviousView = CountTileChanges(pixels, width, layout, view - 1, view);
                    if (metric.changedFromPreviousView < 8) allViewsDiffer = false;
                }
            }
            result.allTilesNonBlank = allNonBlank;
            result.allTilesHaveRedAndGreen = allRedGreen;
            result.allViewsDiffer = allViewsDiffer;
            return result;
        }

        static int CountTileChanges(Color32[] pixels, int width, QuiltLayout layout, int firstView, int secondView)
        {
            RectInt first = layout.GetTile(firstView);
            RectInt second = layout.GetTile(secondView);
            int changed = 0;
            for (int y = 0; y < first.height; y++)
            for (int x = 0; x < first.width; x++)
            {
                Color32 a = pixels[(first.y + y) * width + first.x + x];
                Color32 b = pixels[(second.y + y) * width + second.x + x];
                if (Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) > 24) changed++;
            }
            return changed;
        }

        static int CountChangedPixels(Color32[] before, Color32[] after)
        {
            int changed = 0;
            for (int i = 0; i < Math.Min(before.Length, after.Length); i++)
                if (Math.Abs(before[i].r - after[i].r) + Math.Abs(before[i].g - after[i].g) + Math.Abs(before[i].b - after[i].b) > 32) changed++;
            return changed;
        }

        void SavePng(Color32[] pixels, int width, int height, string fileName)
        {
            var image = new Texture2D(width, height, TextureFormat.RGBA32, false);
            try
            {
                image.SetPixels32(pixels);
                image.Apply(false, false);
                File.WriteAllBytes(Path.Combine(evidenceDirectory, fileName), image.EncodeToPNG());
            }
            finally { Destroy(image); }
        }

        void RequireLayout(QuiltLayout layout, string stage)
        {
            Require(layout.ViewCount == ExpectedViews && layout.Columns == ExpectedColumns && layout.Rows == ExpectedRows &&
                layout.QuiltWidth == TileSize * ExpectedColumns && layout.QuiltHeight == TileSize * ExpectedRows,
                "Submitted layout changed during " + stage + ".");
        }

        void RequireSettingsUnchanged(QuiltSettingsSnapshot expected, string stage)
        {
            evidence.settingsRetained = expected.Matches(quilt.settings);
            Require(evidence.settingsRetained, "Quilt settings changed during " + stage + ".");
        }

        void RequireExposureUnchanged(Exposure expected)
        {
            Exposure current = null;
            evidence.exposureRetained = globalVolume != null && globalVolume.sharedProfile != null &&
                globalVolume.sharedProfile.TryGet(out current) && current != null && current.active &&
                current.mode.value == ExposureMode.Fixed && Mathf.Approximately(current.fixedExposure.value, ExpectedExposureEv) &&
                current.fixedExposure.overrideState;
            Require(evidence.exposureRetained && expected != null && Mathf.Approximately(expected.fixedExposure.value, current.fixedExposure.value),
                "HDRP fixed exposure EV 4 did not remain configured.");
        }

        void RequireManualExposureUnchanged()
        {
            evidence.manualExposureRetained = hdrpGuard != null &&
                hdrpGuard.exposureMode == HdrpQuiltExposureMode.ManualFixed &&
                Mathf.Approximately(hdrpGuard.fallbackFixedExposureEV, ExpectedExposureEv);
            Require(evidence.manualExposureRetained, "HDRP ManualFixed EV 4 settings did not remain configured.");
        }

        void RequireFixtureLightState(string stage)
        {
            HDAdditionalLightData additionalData = fixtureDirectionalLight != null
                ? fixtureDirectionalLight.GetComponent<HDAdditionalLightData>() : null;
            var record = new LightStageEvidence
            {
                stage = Sanitize(stage),
                lightFound = fixtureDirectionalLight != null,
                additionalDataPresent = additionalData != null,
                lightType = fixtureDirectionalLight != null ? fixtureDirectionalLight.type.ToString() : string.Empty,
                lightUnit = fixtureDirectionalLight != null ? fixtureDirectionalLight.lightUnit.ToString() : string.Empty,
                intensity = fixtureDirectionalLight != null ? fixtureDirectionalLight.intensity : 0f
            };
            record.matchesExpected = record.lightFound && record.additionalDataPresent &&
                fixtureDirectionalLight.type == LightType.Directional &&
                fixtureDirectionalLight.lightUnit == LightUnit.Lux &&
                Mathf.Approximately(fixtureDirectionalLight.intensity, ExpectedDirectionalLux);
            lightStageRecords.Add(record);
            evidence.fixtureLightStages = lightStageRecords.ToArray();
            Require(record.matchesExpected,
                "HDRP fixture light must retain Directional/Lux/10 and HDAdditionalLightData during " + stage + ".");
        }

        static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; i++) if (args[i] == key) return args[i + 1];
            return null;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            char[] chars = value.ToCharArray();
            for (int i = 0; i < chars.Length; i++) if (chars[i] < 0x20 || chars[i] > 0x7e) chars[i] = '?';
            return new string(chars);
        }

        struct QuiltSettingsSnapshot
        {
            public int width, height, columns, rows, views;
            public float aspect, fov, distance, cone;
            public static QuiltSettingsSnapshot Capture(QuiltSettings value) => new QuiltSettingsSnapshot
            {
                width = value.quiltWidth, height = value.quiltHeight, columns = value.columns, rows = value.rows,
                views = value.views, aspect = value.viewAspect, fov = value.fieldOfView, distance = value.cameraDistance, cone = value.viewCone
            };
            public bool Matches(QuiltSettings value) => value != null && width == value.quiltWidth && height == value.quiltHeight &&
                columns == value.columns && rows == value.rows && views == value.views && Mathf.Approximately(aspect, value.viewAspect) &&
                Mathf.Approximately(fov, value.fieldOfView) && Mathf.Approximately(distance, value.cameraDistance) && Mathf.Approximately(cone, value.viewCone);
        }

        [Serializable] sealed class TileMetric
        {
            public int view, nonBlank, red, green, averageRed, averageGreen, averageBlue, distinctQuantizedColors, changedFromPreviousView;
        }

        [Serializable] sealed class TileEvidence
        {
            public int width, height;
            public bool allTilesNonBlank, allTilesHaveRedAndGreen, allViewsDiffer;
            public TileMetric[] tiles;
        }

        [Serializable] sealed class DirectEvidence
        {
            public int width, height, nonBlank, red, green, averageRed, averageGreen, averageBlue;
        }

        [Serializable] sealed class LightStageEvidence
        {
            public string stage, lightType, lightUnit;
            public bool lightFound, additionalDataPresent, matchesExpected;
            public float intensity;
        }

        [Serializable] sealed class SceneObjectDiagnostics
        {
            public string name, sceneName, scenePath, shaderName, colorProperty;
            public bool found, activeInHierarchy, rendererEnabled, cullingMaskIncludesLayer, inCameraFrustum;
            public int layer;
            public Vector3 position, boundsCenter, boundsSize, viewportPosition;
            public Color materialColor;
        }

        [Serializable] sealed class ViewCallbackEvidence
        {
            public int logicalView, cullingMask, volumeLayerMask, cameraTargetWidth, cameraTargetHeight, cameraTargetMsaa, activeTargetWidth, activeTargetHeight;
            public string cameraName, cameraTargetName, cameraTargetFormat, activeTargetName, volumeAnchorOverride;
            public string effectiveExposure, effectiveBloom, effectiveFog, effectiveVisualEnvironment, effectiveCloudType, effectiveIndirectLighting, effectiveTonemapping, preCopyImagePath, diagnosticFailure;
            public bool captureSucceeded, cameraEnabled, cameraDataPresent, hdCameraFound, hdrpVolumeStackValid, hdrpPostprocessEnabled, cameraTargetSrgb, effectiveVolumeControlsMatch;
            public Vector3 position, forward;
            public float fieldOfView, aspect, nearClip, farClip;
            public Vector4[] projectionRows;
            public DirectEvidence preCopyImage;
        }

        [Serializable] sealed class Evidence
        {
            public string startedUtc, endedUtc, unityVersion, buildGuid, pipelineAsset, graphicsDeviceType, graphicsDeviceVersion, fixedExposureMode, manualExposureMode, hdrpHistoryStatus, scenePath, error;
            public string sourceCameraClearFlags, globalVolumeStackExposure, globalVolumeStackFog, globalVolumeStackVisualEnvironment, globalVolumeStackBloom, effectiveHistoryStatusAfterDirect, hdrpHistoryStatusBeforeRender, quiltRendererType, directRequestError;
            public bool isEditor, development, graphicsMultiThreaded, settingsRetained, exposureRetained, manualExposureRetained, readbackReady, allGatesPassed, ok;
            public bool directRequestSucceeded, directRenderSubmitted, globalVolumeStackValid, quiltRendererEnabled, quiltSourceCameraMatches, hdrpCameraDataPresent, hdrpCameraCustomRenderingSettings;
            public bool quiltSnapshotValid, quiltSnapshotPhysicalFramingEnabled;
            public bool movingTargetRendererEnabled, movingTargetInCameraFrustum;
            public float fixedExposureEv, manualExposureEv, movementDelta, sourceCameraFieldOfView;
            public int warmupFrames, readinessAttempts, renderCalls, hdrpHistoryCameraCount, hdrpHistoryCameraCountBeforeRender, effectiveHistoryCameraCountAfterDirect, sceneRendererCount, sourceCameraCullingMask, hdrpCameraVolumeLayerMask, requestedViews, requestedColumns, requestedRows, requestedQuiltWidth, requestedQuiltHeight, changedPixels, viewCallbackDiagnosticFailures, quiltSnapshotRenderedViews;
            public long quiltSnapshotRevision;
            public float readinessElapsedMilliseconds;
            public string readinessLastError;
            public LightStageEvidence[] fixtureLightStages;
            public Vector3 sourceCameraPosition, sourceCameraForward, movingTargetPosition, movingTargetViewport;
            public Vector3 quiltSnapshotCameraPosition;
            public Quaternion quiltSnapshotCameraRotation;
            public float quiltSnapshotViewAspect, quiltSnapshotFieldOfView, quiltSnapshotFocusDistance, quiltSnapshotViewCone, quiltSnapshotNearClip, quiltSnapshotFarClip;
            public float quiltSnapshotApertureWidthMillimeters, quiltSnapshotApertureHeightMillimeters, quiltSnapshotUnitsPerMeter;
            public Color movingTargetMaterialColor, sourceCameraBackground;
            public string[] sceneRendererNames;
            public SceneObjectDiagnostics[] sceneObjects;
            public ViewCallbackEvidence[] viewCallbackRecords;
            public DirectEvidence directStandardRequest;
            public TileEvidence before, after;
        }
    }
}
