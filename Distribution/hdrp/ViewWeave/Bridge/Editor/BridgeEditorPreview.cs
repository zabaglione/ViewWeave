using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using ViewWeave;

namespace ViewWeave.Editor
{
    [InitializeOnLoad]
    internal static class BridgeEditorPreview
    {
        const double RenderIntervalSeconds = 0.2;
        static readonly object OwnerToken = new object();

        static ViewWeaveRig target;
        static GameObject producerObject;
        static Camera producerCamera;
        static ViewWeaveRig producerRig;
        static QuiltRenderer producerRenderer;
        static BridgeWorkerQuiltOutput producerOutput;
        static BridgeEditorPreviewRetentionRecord retentionRecord;
        static ViewWeaveBridgeSession sourceSession;
        static BridgeWorkerQuiltOutput sourceOutput;
        static RenderPipelineAsset pipelineAtStart;
        static QuiltSettings sourceSettingsAtStart;
        static QuiltQualityPreset qualityAtStart;
        static string configuredRuntimePathAtStart;
        static string configuredApplicationNameAtStart;
        static string runtimePathAtStart;
        static string applicationNameAtStart;
        static uint displayOrdinalAtStart;
        static bool outputToDevice;
        static bool active;
        static bool stopping;
        static bool rendering;
        static bool profileApplied;
        static bool restartAfterStop;
        static bool refreshRequested;
        static double nextRenderTime;
        static float connectStartedAt;
        static int lastMsaaSamples;
        static string status = "Idle";
        static string lastError = string.Empty;
        static QuiltLayout layout;

        static BridgeEditorPreview()
        {
            BridgeEditorPreviewRetention.ResetProofsAfterDomainReload();
            BridgeEditorPreviewRetention.RegisterEditorCallbacks(StopForLifecycle, DetachRetainedProducer);
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            BridgeEditorPreviewRetention.RequestStop(false);
        }

        internal static ViewWeaveRig Target => target;
        internal static bool IsActive => active && !stopping;
        internal static bool IsStopping => stopping || BridgeEditorPreviewRetention.HasStoppingRecords;
        internal static string Status => status;
        internal static string LastError => lastError;
        internal static RenderTexture Texture => IsActive && producerRenderer != null ? producerRenderer.QuiltTexture : null;
        internal static QuiltLayout Layout => layout;

        internal static bool Start(ViewWeaveRig rig, bool outputToDeviceValue, out string error)
        {
            error = string.Empty;
            if (Application.isPlaying)
                return FailStart("Editor Rig Preview can only start in Edit Mode.", out error);
            if (IsActive || stopping)
                return FailStart("An Editor Rig Preview is already active or stopping.", out error);
            if (BridgeEditorPreviewRetention.HasPending)
                return FailStart("Previous Editor preview resources are still retained pending GPU proof.", out error);
            if (rig == null || !rig.isActiveAndEnabled)
                return FailStart("Select an active ViewWeaveRig before starting preview.", out error);
            if (!ValidatePreviewStage(rig, out string stageError))
                return FailStart(stageError, out error);
            if (!rig.ValidateRig(out string rigError))
                return FailStart("The selected ViewWeaveRig is invalid: " + Sanitize(rigError), out error);
            if (!BridgeGraphicsBackendPolicy.TrySelect(
                BridgeGraphicsBackendRoute.EditorPreview,
                Application.platform,
                Application.isEditor,
                Application.isPlaying,
                SystemInfo.graphicsDeviceType,
                SystemInfo.renderingThreadingMode,
                out _,
                out string backendError))
                return FailStart(backendError, out error);
            if (BridgeGraphicsBackendPolicy.SelectCurrentGpuProofMode() == BridgeGpuProofMode.Unsupported)
                return FailStart("Editor preview requires a supported GPU completion proof mode.", out error);
            if (producerObject != null)
                return FailStart("An earlier preview producer has not completed cleanup.", out error);

            target = rig;
            outputToDevice = outputToDeviceValue;
            sourceSession = rig.GetComponent<ViewWeaveBridgeSession>();
            sourceOutput = rig.Renderer.OutputBehaviour as BridgeWorkerQuiltOutput;
            if (sourceSession != null && sourceSession.State != ViewWeaveBridgeSession.SessionState.Idle && sourceSession.State != ViewWeaveBridgeSession.SessionState.Fault)
                return ResetFailedStart("Stop the regular Bridge session before starting Edit Mode preview.", out error);
            if (outputToDeviceValue && (BridgeWorkerLifecycle.RecoveryPending || BridgeWorkerQuiltOutput.HasNativeHandle ||
                BridgeWorkerQuiltOutput.ShutdownPendingCount != 0))
                return ResetFailedStart("Bridge worker recovery or another native worker is still active.", out error);

            if (outputToDevice && !ResolveDeviceConfiguration(out string resolveError)) return ResetFailedStart(resolveError, out error);
            configuredRuntimePathAtStart = sourceSession == null ? string.Empty : sourceSession.runtimePath;
            configuredApplicationNameAtStart = sourceSession == null ? "ViewWeave" : sourceSession.applicationName;
            pipelineAtStart = GraphicsSettings.currentRenderPipeline;
            sourceSettingsAtStart = rig.Renderer.settings.Clone();
            qualityAtStart = sourceSession == null ? QuiltQualityPreset.Preview : sourceSession.Quality;

            try
            {
                BuildProducer(rig);
                if (outputToDevice)
                {
                    retentionRecord.SetWorkerProofExpected(true);
                    if (producerOutput == null || !producerOutput.TryBeginEditorPreview(OwnerToken, retentionRecord, out error))
                    {
                        string beginError = string.IsNullOrEmpty(error) ? "Bridge Worker preview could not start." : error;
                        if (retentionRecord != null && retentionRecord.WorkerRecoveryRecord == null)
                        {
                            retentionRecord.SetWorkerProofExpected(false);
                            DestroyUnrenderedProducer();
                            ResetState();
                        }
                        else BeginStop(false, beginError);
                        error = beginError;
                        lastError = beginError;
                        status = "Error";
                        return false;
                    }
                    connectStartedAt = Time.realtimeSinceStartup;
                    status = "Connecting to Looking Glass";
                }
                else
                {
                    producerRig.ApplyProfile(rig.Profile);
                    CopySourceSettings(rig, false);
                    profileApplied = true;
                    status = "Local preview running";
                }
                active = true;
                stopping = false;
                lastMsaaSamples = 0;
                lastError = string.Empty;
                refreshRequested = true;
                nextRenderTime = 0;
                QueueEditorRender();
                return true;
            }
            catch (Exception exception)
            {
                string message = "Editor Rig Preview setup failed: " + Sanitize(exception.Message);
                if (CanAbandonProducerWithoutGpu())
                {
                    DestroyUnrenderedProducer();
                    ResetState();
                }
                else if (producerObject != null)
                {
                    BeginStop(false, message);
                }
                else ResetState();
                error = message;
                lastError = message;
                status = "Error";
                return false;
            }
        }

        internal static bool Stop()
        {
            if (active && !stopping) BeginStop(false, string.Empty);
            else if (BridgeEditorPreviewRetention.HasPending) BridgeEditorPreviewRetention.Pump();
            return true;
        }

        internal static void RequestRefresh()
        {
            if (!IsActive) return;
            refreshRequested = true;
            QueueEditorRender();
        }

        static bool ResolveDeviceConfiguration(out string error)
        {
            error = string.Empty;
            string configuredPath = sourceSession == null ? string.Empty : sourceSession.runtimePath;
            if (!BridgeRuntimeLocator.TryResolve(configuredPath, out runtimePathAtStart, out error)) return false;
            applicationNameAtStart = sourceSession == null || string.IsNullOrWhiteSpace(sourceSession.applicationName) ? "ViewWeave" : sourceSession.applicationName;
            displayOrdinalAtStart = sourceSession == null ? 0u : sourceSession.displayOrdinal;
            return true;
        }

        static void BuildProducer(ViewWeaveRig sourceRig)
        {
            Camera sourceCamera = sourceRig.Renderer.sourceCamera;
            Camera framingCamera = sourceRig.Renderer.EffectiveCamera;
            producerObject = new GameObject("ViewWeave Editor Preview") { hideFlags = HideFlags.HideAndDontSave | HideFlags.DontUnloadUnusedAsset };
            producerObject.SetActive(false);
            producerCamera = producerObject.AddComponent<Camera>();
            producerCamera.CopyFrom(sourceCamera);
            producerCamera.enabled = false;
            producerCamera.targetTexture = null;
            producerCamera.transform.SetPositionAndRotation(sourceCamera.transform.position, sourceCamera.transform.rotation);
            producerRenderer = producerObject.AddComponent<QuiltRenderer>();
            retentionRecord = BridgeEditorPreviewRetentionRecord.Create(producerObject, producerRenderer);
            if (!producerRenderer.BeginEditorPreviewRetention(retentionRecord))
                throw new InvalidOperationException("QuiltRenderer could not attach its Core texture retention owner.");
            producerRenderer.sourceCamera = producerCamera;
            producerRenderer.referenceCamera = framingCamera;
            producerRenderer.followReferenceFieldOfView = sourceRig.Renderer.followReferenceFieldOfView;
            producerRenderer.useRenderRequests = sourceRig.Renderer.useRenderRequests;
            producerRenderer.viewRenderMode = sourceRig.Renderer.viewRenderMode;
            producerRenderer.settings = sourceRig.Renderer.settings.Clone();
            producerRig = producerObject.AddComponent<ViewWeaveRig>();
            producerRig.ApplyProfile(sourceRig.Profile);
            if (outputToDevice)
            {
                producerOutput = producerObject.AddComponent<BridgeWorkerQuiltOutput>();
                producerOutput.initializeOnEnable = false;
                producerOutput.runtimePath = runtimePathAtStart;
                producerOutput.applicationName = applicationNameAtStart;
                producerOutput.displayOrdinal = displayOrdinalAtStart;
                producerOutput.zoom = sourceOutput == null ? 1f : sourceOutput.zoom;
            }
            if (!ViewWeaveRigProviderRegistry.TryAddAdapter(producerObject, framingCamera, out MonoBehaviour adapter))
                throw new InvalidOperationException("The active render pipeline has no ViewWeave adapter provider.");
            CopyPipelineGuardSettings(sourceRig.Renderer.GuardBehaviour, adapter);
            CopyAdditionalCameraData(sourceCamera, producerCamera);
            producerRig.SetGuard(adapter);
            producerRig.SetOutput(null);
            producerRig.AutoRender = false;
            if (!producerRig.ConfigureComponents()) throw new InvalidOperationException("The Editor preview producer could not configure its Camera and adapter.");
            producerRenderer.referenceCamera = framingCamera;
            producerRenderer.followReferenceFieldOfView = sourceRig.Renderer.followReferenceFieldOfView;
            producerRenderer.viewRenderMode = sourceRig.Renderer.viewRenderMode;
            producerRenderer.settings = sourceRig.Renderer.settings.Clone();
            producerObject.SetActive(true);
            CopySourceCamera(sourceRig);
        }

        static void CopyAdditionalCameraData(Camera source, Camera destination)
        {
            Component sourceHdrpData = source.GetComponent("HDAdditionalCameraData");
            Component destinationHdrpData = destination.GetComponent("HDAdditionalCameraData");
            if (sourceHdrpData != null && destinationHdrpData != null) EditorUtility.CopySerialized(sourceHdrpData, destinationHdrpData);
        }

        static void CopyPipelineGuardSettings(MonoBehaviour source, MonoBehaviour destination)
        {
            if (source == null || destination == null) return;
            Type type = source.GetType();
            if (type.FullName == null || (!type.FullName.Contains("HdrpQuiltCamera") && !type.FullName.Contains("HDRP"))) return;
            CopyPublicField(source, destination, "adaptUnsupportedEffects");
            CopyPublicField(source, destination, "fallbackFixedExposure");
            CopyPublicField(source, destination, "fallbackFixedExposureEV");
        }

        static void CopyPublicField(object source, object destination, string name)
        {
            FieldInfo field = source.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public);
            if (field != null && field.FieldType == destination.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public)?.FieldType)
                field.SetValue(destination, field.GetValue(source));
        }

        static void CopySourceCamera(ViewWeaveRig sourceRig)
        {
            Camera sourceCamera = sourceRig.Renderer.sourceCamera;
            Camera framingCamera = sourceRig.Renderer.EffectiveCamera;
            producerCamera.CopyFrom(sourceCamera);
            producerCamera.enabled = false;
            producerCamera.targetTexture = null;
            producerCamera.transform.SetPositionAndRotation(sourceCamera.transform.position, sourceCamera.transform.rotation);
            producerRenderer.referenceCamera = framingCamera;
            producerRenderer.followReferenceFieldOfView = sourceRig.Renderer.followReferenceFieldOfView;
            producerRenderer.viewRenderMode = sourceRig.Renderer.viewRenderMode;
            producerRenderer.settings.fieldOfView = sourceRig.Renderer.settings.fieldOfView;
            producerRenderer.settings.cameraDistance = sourceRig.Renderer.settings.cameraDistance;
            producerRenderer.settings.viewAspect = outputToDevice && profileApplied
                ? producerRenderer.settings.viewAspect
                : sourceRig.Renderer.settings.viewAspect;
            if (!outputToDevice)
            {
                producerRenderer.settings.viewCone = sourceRig.Renderer.settings.viewCone;
                producerRenderer.settings.quiltWidth = sourceRig.Renderer.settings.quiltWidth;
                producerRenderer.settings.quiltHeight = sourceRig.Renderer.settings.quiltHeight;
                producerRenderer.settings.columns = sourceRig.Renderer.settings.columns;
                producerRenderer.settings.rows = sourceRig.Renderer.settings.rows;
                producerRenderer.settings.views = sourceRig.Renderer.settings.views;
            }
            CopyLivePreviewControls(sourceRig.Renderer, sourceRig.Renderer.GuardBehaviour,
                producerRenderer, producerRig == null ? null : producerRig.Renderer.GuardBehaviour,
                sourceOutput, producerOutput);
        }

        internal static void CopyLivePreviewControls(QuiltRenderer sourceRenderer, MonoBehaviour sourceGuard,
            QuiltRenderer destinationRenderer, MonoBehaviour destinationGuard,
            BridgeWorkerQuiltOutput sourceOutput, BridgeWorkerQuiltOutput destinationOutput)
        {
            if (sourceRenderer == null || destinationRenderer == null) return;
            destinationRenderer.useRenderRequests = sourceRenderer.useRenderRequests;
            CopyPipelineGuardSettings(sourceGuard, destinationGuard);
            if (sourceOutput != null && destinationOutput != null) destinationOutput.zoom = sourceOutput.zoom;
        }

        static void CopySourceSettings(ViewWeaveRig sourceRig, bool useDeviceProfile)
        {
            producerRenderer.settings = sourceRig.Renderer.settings.Clone();
            producerRenderer.viewRenderMode = sourceRig.Renderer.viewRenderMode;
            if (useDeviceProfile && profileApplied)
            {
                producerRenderer.settings.viewAspect = producerRig.Profile.Aspect;
                producerRenderer.settings.viewCone = producerRig.Profile.ViewCone;
                producerRenderer.settings.quiltWidth = producerRig.Profile.QuiltWidth;
                producerRenderer.settings.quiltHeight = producerRig.Profile.QuiltHeight;
                producerRenderer.settings.columns = producerRig.Profile.Columns;
                producerRenderer.settings.rows = producerRig.Profile.Rows;
                producerRenderer.settings.views = producerRig.Profile.Views;
            }
            CopySourceCamera(sourceRig);
        }

        static void Update()
        {
            BridgeEditorPreviewRetention.Pump();
            if (stopping)
            {
                if (!BridgeEditorPreviewRetention.HasPending) CompleteStop();
                else QueueEditorRender();
                return;
            }
            if (!active) return;
            if (Application.isPlaying || target == null || !target.isActiveAndEnabled)
            {
                BeginStop(false, "Preview stopped because its target is unavailable or the Editor entered Play mode.");
                return;
            }
            if (GraphicsSettings.currentRenderPipeline != pipelineAtStart)
            {
                restartAfterStop = true;
                BeginStop(false, "Preview is rebuilding after a render-pipeline change.");
                return;
            }
            if (!target.ValidateRig(out string validationError))
            {
                BeginStop(false, "Preview stopped because the target rig is no longer valid: " + Sanitize(validationError));
                return;
            }
            if (!ValidatePreviewStage(target, out string stageError))
            {
                BeginStop(false, stageError);
                return;
            }
            if (NeedsRebuild())
            {
                restartAfterStop = true;
                BeginStop(false, "Preview is rebuilding after a layout, quality, or MSAA change.");
                return;
            }

            try
            {
                if (outputToDevice)
                {
                    producerOutput.Poll();
                    if (producerOutput.State == BridgeWorkerQuiltOutput.WorkerState.Fault || producerOutput.State == BridgeWorkerQuiltOutput.WorkerState.Closed)
                    {
                        string reason = string.IsNullOrEmpty(producerOutput.LastError) ? "Bridge Worker closed before preview was ready." : producerOutput.LastError;
                        BeginStop(false, reason);
                        return;
                    }
                    if (!producerOutput.Ready)
                    {
                        if (Time.realtimeSinceStartup - connectStartedAt > 10f) BeginStop(false, "Bridge Worker connection timed out after 10 seconds.");
                        QueueEditorRender();
                        return;
                    }
                    if (!profileApplied && !ApplyDeviceProfile())
                    {
                        BeginStop(false, lastError);
                        return;
                    }
                }

                CopySourceCamera(target);
                if (refreshRequested || EditorApplication.timeSinceStartup >= nextRenderTime) RenderPreview();
                QueueEditorRender();
            }
            catch (Exception exception)
            {
                BeginStop(false, "Editor preview stopped after an error: " + Sanitize(exception.Message));
            }
        }

        static bool ApplyDeviceProfile()
        {
            BridgeDisplayProfile displayProfile = producerOutput.Profile;
            if (!displayProfile.IsValid) { lastError = "Bridge Worker returned an invalid display profile."; return false; }
            try
            {
                QuiltQualityPreset quality = sourceSession == null ? QuiltQualityPreset.Preview : sourceSession.Quality;
                QuiltQualityProfile selected = ViewWeaveBridgeSession.CreateQualityProfile(displayProfile, quality);
                producerRig.ApplyProfile(selected);
                producerRenderer.settings = target.Renderer.settings.Clone();
                selected.ApplyTo(producerRenderer.settings);
                producerRenderer.settings.fieldOfView = target.Renderer.settings.fieldOfView;
                producerRenderer.settings.cameraDistance = target.Renderer.settings.cameraDistance;
                producerRenderer.viewRenderMode = target.Renderer.viewRenderMode;
                profileApplied = true;
                qualityAtStart = quality;
                layout = producerRenderer.settings.GetLayout();
                status = "Looking Glass preview running";
                lastError = string.Empty;
                return true;
            }
            catch (Exception exception)
            {
                lastError = "Could not apply the display profile: " + Sanitize(exception.Message);
                return false;
            }
        }

        static bool NeedsRebuild()
        {
            QuiltSettings current = target.Renderer.settings;
            if (current == null || sourceSettingsAtStart == null) return true;
            if (current.quiltWidth != sourceSettingsAtStart.quiltWidth || current.quiltHeight != sourceSettingsAtStart.quiltHeight ||
                current.columns != sourceSettingsAtStart.columns || current.rows != sourceSettingsAtStart.rows || current.views != sourceSettingsAtStart.views ||
                current.viewAspect != sourceSettingsAtStart.viewAspect) return true;
            if (outputToDevice)
            {
                QuiltQualityPreset quality = sourceSession == null ? QuiltQualityPreset.Preview : sourceSession.Quality;
                if (quality != qualityAtStart) return true;
                if (sourceSession != null && (sourceSession.runtimePath != configuredRuntimePathAtStart ||
                    sourceSession.applicationName != configuredApplicationNameAtStart || sourceSession.displayOrdinal != displayOrdinalAtStart)) return true;
            }
            QuiltLayout desiredLayout = producerRenderer == null ? current.GetLayout() : producerRenderer.settings.GetLayout();
            int desiredMsaa = GetDesiredMsaaSamples(target.Renderer.sourceCamera, target.Renderer.GuardBehaviour as IQuiltMsaaProvider, desiredLayout);
            return lastMsaaSamples != 0 && desiredMsaa != lastMsaaSamples;
        }

        internal static int GetDesiredMsaaSamples(Camera incomingSourceCamera, IQuiltMsaaProvider incomingGuard, QuiltLayout desiredLayout)
        {
            if (incomingSourceCamera == null || !incomingSourceCamera.allowMSAA || incomingGuard == null) return 1;
            int requested = incomingGuard.GetMsaaSampleCount(incomingSourceCamera);
            if (requested <= 1) return 1;
            var descriptor = new RenderTextureDescriptor(desiredLayout.TileWidth, desiredLayout.TileHeight, RenderTextureFormat.ARGB32, 24) { msaaSamples = requested };
            int supported = SystemInfo.GetRenderTextureSupportedMSAASampleCount(descriptor);
            return supported == 2 || supported == 4 || supported == 8 ? supported : 1;
        }

        static void RenderPreview()
        {
            if (rendering || producerRenderer == null || producerOutput == null && outputToDevice) return;
            rendering = true;
            refreshRequested = false;
            try
            {
                EditorApplication.QueuePlayerLoopUpdate();
                producerRenderer.RenderQuilt();
                layout = producerRenderer.QuiltTexture == null ? producerRenderer.settings.GetLayout() : producerRenderer.LatestSnapshot.Layout;
                if (outputToDevice && !producerOutput.TrySubmitEditorPreview(OwnerToken, producerRenderer.QuiltTexture, layout))
                    throw new InvalidOperationException(string.IsNullOrEmpty(producerOutput.LastError) ? "Bridge Worker rejected the preview quilt." : producerOutput.LastError);
                lastMsaaSamples = producerRenderer.ViewAntiAliasingSamples;
                nextRenderTime = EditorApplication.timeSinceStartup + RenderIntervalSeconds;
                status = outputToDevice ? "Looking Glass preview running" : "Local preview running";
                lastError = string.Empty;
                SceneView.RepaintAll();
            }
            finally { rendering = false; }
        }

        static void BeginStop(bool restart, string reason)
        {
            if (!active && !stopping && producerObject == null) return;
            if (!string.IsNullOrEmpty(reason)) { lastError = Sanitize(reason); status = "Stopping preview"; }
            active = false;
            stopping = true;
            refreshRequested = false;
            restartAfterStop |= restart;
            nextRenderTime = double.PositiveInfinity;
            BridgeEditorPreviewRetentionRecord record = retentionRecord;
            if (producerObject != null)
            {
                if (record == null)
                {
                    record = BridgeEditorPreviewRetentionRecord.Create(producerObject, producerRenderer);
                    retentionRecord = record;
                }
                record.MarkStopRequested();
                if (producerOutput != null)
                {
                    bool handedOff = producerOutput.EndEditorPreview(OwnerToken, record);
                    if (!handedOff && record.WorkerRecoveryRecord == null && producerOutput.SessionGeneration == 0)
                        record.SetWorkerProofExpected(false);
                    if (!handedOff && record.WorkerProofExpectedForDiagnostics)
                        record.SetDiagnostic("Bridge Worker preview ownership handoff is unproven; producer resources remain retained.");
                }
                else record.SetWorkerProofExpected(false);
                DetachRetainedProducer(record);
                if (record.Detached)
                {
                    producerObject = null;
                    producerCamera = null;
                    producerRig = null;
                    producerRenderer = null;
                    producerOutput = null;
                    profileApplied = false;
                }
            }
            if (!BridgeEditorPreviewRetention.HasPending)
            {
                stopping = false;
                if (restartAfterStop) CompleteStop();
                else if (string.IsNullOrEmpty(lastError)) status = "Idle";
            }
            else QueueEditorRender();
        }

        static void DetachRetainedProducer(BridgeEditorPreviewRetentionRecord record)
        {
            if (record == null || record.Detached) return;
            QuiltRenderer renderer = record.Renderer;
            if (renderer == null)
            {
                if (!record.SetTextures(Array.Empty<RenderTexture>()))
                    throw new InvalidOperationException("retained preview record has not accepted the stop request");
                return;
            }
            if (renderer.IsRendering) return;
            RenderTexture[] owned = renderer.DetachOwnedRenderTextures();
            if (!record.SetTextures(owned)) throw new InvalidOperationException("retained preview record rejected Core-owned textures");
        }

        static void CompleteStop()
        {
            stopping = false;
            BridgeEditorPreviewRetention.Pump();
            if (BridgeEditorPreviewRetention.HasPending) { stopping = true; return; }
            producerObject = null;
            producerCamera = null;
            producerRig = null;
            producerRenderer = null;
            producerOutput = null;
            retentionRecord = null;
            bool restart = restartAfterStop;
            restartAfterStop = false;
            ViewWeaveRig nextTarget = target;
            bool nextDevice = outputToDevice;
            status = string.IsNullOrEmpty(lastError) ? "Idle" : "Stopped with an error";
            if (restart && nextTarget != null && nextTarget.isActiveAndEnabled)
            {
                if (!Start(nextTarget, nextDevice, out string error))
                {
                    lastError = error;
                    status = "Rebuild failed";
                }
            }
        }

        static bool StopForLifecycle()
        {
            if (active || stopping || producerObject != null)
                BeginStop(false, "Preview is stopping for an Editor transition.");
            else
                BridgeEditorPreviewRetention.MarkOrphanedRecordsStopRequested();
            return true;
        }

        static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode) BeginStop(false, "Preview stopped before entering Play mode.");
        }

        static void OnBeforeAssemblyReload()
        {
            BeginStop(false, "Preview is stopping before assembly reload.");
            BridgeEditorPreviewRetention.Pump(true);
        }

        static void OnEditorQuitting()
        {
            BeginStop(false, "Preview is stopping before Editor quit.");
            BridgeEditorPreviewRetention.Pump(true);
        }

        static void QueueEditorRender()
        {
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        static bool FailStart(string message, out string error)
        {
            lastError = Sanitize(message);
            status = "Error";
            error = lastError;
            return false;
        }

        static bool ResetFailedStart(string message, out string error)
        {
            target = null;
            sourceSession = null;
            sourceOutput = null;
            return FailStart(message, out error);
        }

        static void DestroyUnrenderedProducer()
        {
            if (retentionRecord != null)
            {
                retentionRecord.SetWorkerProofExpected(false);
                retentionRecord.MarkStopRequested();
                retentionRecord.SetTextures(Array.Empty<RenderTexture>());
                BridgeEditorPreviewRetention.Pump();
            }
            else if (producerObject != null) UnityEngine.Object.DestroyImmediate(producerObject);
            producerObject = null;
            producerCamera = null;
            producerRig = null;
            producerRenderer = null;
            producerOutput = null;
            retentionRecord = null;
        }

        static bool CanAbandonProducerWithoutGpu()
        {
            if (producerObject == null) return false;
            if (retentionRecord != null && retentionRecord.WorkerRecoveryRecord != null) return false;
            if (producerRenderer != null && producerRenderer.QuiltTexture != null) return false;
            return producerOutput == null || producerOutput.State == BridgeWorkerQuiltOutput.WorkerState.Closed ||
                producerOutput.State == BridgeWorkerQuiltOutput.WorkerState.Fault;
        }

        static bool ValidatePreviewStage(ViewWeaveRig rig, out string error)
        {
            error = string.Empty;
            if (rig == null || rig.Renderer == null || EditorUtility.IsPersistent(rig.gameObject))
            {
                error = "Editor Rig Preview requires a nonpersistent ViewWeave Rig in a loaded scene.";
                return false;
            }
            Camera sourceCamera = rig.Renderer.sourceCamera;
            Camera focusCamera = rig.Renderer.EffectiveCamera;
            if (!IsPreviewObjectAllowed(rig.gameObject, rig.gameObject, out error) ||
                !IsPreviewObjectAllowed(rig.gameObject, sourceCamera == null ? null : sourceCamera.gameObject, out error) ||
                !IsPreviewObjectAllowed(rig.gameObject, focusCamera == null ? null : focusCamera.gameObject, out error))
                return false;
            return true;
        }

        static bool IsPreviewObjectAllowed(GameObject rigObject, GameObject candidate, out string error)
        {
            error = string.Empty;
            if (candidate == null || EditorUtility.IsPersistent(candidate) || !candidate.scene.IsValid() || !candidate.scene.isLoaded)
            {
                error = "Editor Rig Preview Camera and Focus Camera must belong to loaded scene objects.";
                return false;
            }
            bool rigPreview = EditorSceneManager.IsPreviewSceneObject(rigObject);
            bool candidatePreview = EditorSceneManager.IsPreviewSceneObject(candidate);
            if (rigPreview || candidatePreview)
            {
                if (rigPreview && candidatePreview && rigObject.scene == candidate.scene && PrefabStageUtility.GetPrefabStage(rigObject) != null)
                    return true;
                error = "Editor Rig Preview cannot use assets, isolated preview scenes, or objects across Prefab Stage boundaries.";
                return false;
            }
            if (StageUtility.GetStageHandle(rigObject) != StageUtility.GetStageHandle(candidate))
            {
                error = "Editor Rig Preview Rig, Camera, and Focus Camera must belong to the same Editor Stage.";
                return false;
            }
            return true;
        }

        static void ResetState()
        {
            active = false;
            stopping = false;
            rendering = false;
            profileApplied = false;
            refreshRequested = false;
            sourceSession = null;
            sourceOutput = null;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char character in value) if (character >= 0x20 && character <= 0x7e) chars.Add(character);
            return new string(chars.ToArray());
        }
    }
}
