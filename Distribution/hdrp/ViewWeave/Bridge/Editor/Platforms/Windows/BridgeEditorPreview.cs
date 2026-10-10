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
        internal enum PreviewState { Idle, Running, Pausing, Paused, Resuming, Closing }
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
        static IDisposable sourceMutationOwnerRegistration;
        static IDisposable producerMutationOwnerRegistration;
        static RenderPipelineAsset pipelineAtStart;
        static QuiltSettings sourceSettingsAtStart;
        static QuiltQualityPreset qualityAtStart;
        static string configuredRuntimePathAtStart;
        static string configuredApplicationNameAtStart;
        static string configuredOutputRuntimePathAtStart;
        static string configuredOutputApplicationNameAtStart;
        static uint configuredOutputDisplayOrdinalAtStart;
        static string runtimePathAtStart;
        static string applicationNameAtStart;
        static uint displayOrdinalAtStart;
        static bool outputToDevice;
        static bool active;
        static bool stopping;
        static bool resumeAfterPause;
        static bool nativePauseRequestPending;
        static PreviewState previewState;
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
        static string lastSessionProfileSignature = string.Empty;

        static BridgeEditorPreview()
        {
            QuiltDisplaySettingsResolver.RegisterProvider(BridgeEditorDisplaySettings.ResolveDisplayProfile);
            _ = QuiltRenderer.EditorPreviewRenderCallbackTrackingReady;
            BridgeEditorPreviewRetention.ResetProofsAfterDomainReload();
            BridgeEditorPreviewRetention.RegisterEditorCallbacks(StopForLifecycle, DetachRetainedProducer);
            BridgeDisplayDiscovery.SnapshotPublished += OnConnectedDisplaySnapshotPublished;
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.quitting += OnEditorQuitting;
            BridgeEditorPreviewRetention.RequestStop(false);
        }

        internal static ViewWeaveRig Target => target;
        internal static bool IsActive => active && !stopping;
        internal static bool IsPaused => previewState == PreviewState.Paused;
        internal static bool IsTransitioningPause => previewState == PreviewState.Pausing || previewState == PreviewState.Resuming;
        internal static bool OutputToDevice => outputToDevice;
        internal static PreviewState State => previewState;
        internal static bool IsStopping => stopping || BridgeEditorPreviewRetention.HasStoppingRecords;
        internal static bool CanApplyDisplaySnapshotToRig(ViewWeaveRig rig)
        {
            if (!IsActive || target != rig || !outputToDevice) return true;
            return producerOutput != null && producerOutput.Ready && !producerOutput.RequiresReconnect;
        }
        internal static bool CanRefreshDisplaysForRig(ViewWeaveRig rig)
        {
            if (!IsActive || target != rig || !outputToDevice) return true;
            return producerOutput != null && producerOutput.Ready && !producerOutput.RequiresReconnect;
        }
        internal static string Status => status;
        internal static string LastError => lastError;
        internal static RenderTexture Texture => IsActive && producerRenderer != null ? producerRenderer.QuiltTexture : null;
        internal static QuiltLayout Layout => layout;

        internal static bool Start(ViewWeaveRig rig, bool outputToDeviceValue, out string error)
        {
            error = string.Empty;
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                return FailStart("Editor Rig Preview can only start in Edit Mode.", out error);
            if (!QuiltRenderer.EditorPreviewRenderCallbackTrackingReady)
                return FailStart("Editor Rig Preview requires healthy SRP render callback tracking.", out error);
            if (QuiltRenderer.EditorPreviewRenderCallbackActive)
                return FailStart("Editor Rig Preview must start outside SRP render callbacks.", out error);
            if (GraphicsSettings.currentRenderPipeline == null)
                return FailStart("Editor Rig Preview requires an active Scriptable Render Pipeline.", out error);
            if (active && !stopping)
            {
                if (target != rig || outputToDevice != outputToDeviceValue)
                    return FailStart("A different Rig or preview mode is already retained. Close the current preview before switching targets.", out error);
                if (previewState == PreviewState.Pausing)
                {
                    resumeAfterPause = true;
                    error = string.Empty;
                    return true;
                }
                if (previewState == PreviewState.Paused)
                    return RequestResume(rig, outputToDeviceValue, out error);
                return FailStart("Editor Rig Preview is already active or resuming.", out error);
            }
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
            if (!rig.Renderer.TryGetEffectiveSettings(out _, out string framingError))
                return FailStart("The selected ViewWeaveRig physical framing is invalid: " + Sanitize(framingError), out error);
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
            if (outputToDevice && rig.Renderer.physicalFraming != null && rig.Renderer.physicalFraming.enabled && sourceOutput != null && sourceOutput.zoom != 1f)
                return ResetFailedStart("Physical 1:1 framing requires Bridge zoom to be exactly 1.", out error);
            if (outputToDeviceValue && (BridgeWorkerLifecycle.RecoveryPending || BridgeWorkerQuiltOutput.HasNativeHandle ||
                BridgeWorkerQuiltOutput.ShutdownPendingCount != 0))
                return ResetFailedStart("Bridge worker recovery or another native worker is still active.", out error);
            if (outputToDeviceValue && BridgeDisplayDiscovery.IsBusy)
                return ResetFailedStart("Bridge display metadata is refreshing or cleaning up. Wait before starting a device preview.", out error);

            if (outputToDevice && !ResolveDeviceConfiguration(out string resolveError)) return ResetFailedStart(resolveError, out error);
            configuredRuntimePathAtStart = sourceSession == null ? string.Empty : sourceSession.runtimePath;
            configuredApplicationNameAtStart = sourceSession == null ? "ViewWeave" : sourceSession.applicationName;
            configuredOutputRuntimePathAtStart = sourceOutput == null ? string.Empty : sourceOutput.runtimePath;
            configuredOutputApplicationNameAtStart = sourceOutput == null ? "ViewWeave" : sourceOutput.applicationName;
            configuredOutputDisplayOrdinalAtStart = sourceOutput == null ? 0u : sourceOutput.displayOrdinal;
            pipelineAtStart = GraphicsSettings.currentRenderPipeline;
            sourceSettingsAtStart = rig.Renderer.settings.Clone();
            qualityAtStart = sourceSession == null ? QuiltQualityPreset.Balanced : sourceSession.Quality;

            rig.Renderer.SetMutationOwnerRequired(true);
            sourceMutationOwnerRegistration = QuiltRendererMutationPolicy.RegisterOwner(rig.Renderer, OwnerToken,
                GetSourceMutationBlockReason, out string mutationRegistrationError);
            if (sourceMutationOwnerRegistration == null)
            {
                rig.Renderer.SetMutationOwnerRequired(false);
                return ResetFailedStart("Editor preview could not register renderer ownership: " + Sanitize(mutationRegistrationError), out error);
            }

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
                previewState = PreviewState.Running;
                resumeAfterPause = false;
                QuiltDisplaySettingsResolver.NotifyChanged();
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
            if (active && !stopping) BeginPause();
            else if (BridgeEditorPreviewRetention.HasPending) BridgeEditorPreviewRetention.Pump();
            return true;
        }

        internal static void CloseForTargetChange(string reason)
        {
            if (active || stopping || producerObject != null) BeginStop(false, reason);
        }

        internal static void RequestRefresh()
        {
            if (!IsActive) return;
            refreshRequested = true;
            if (previewState == PreviewState.Running) QueueEditorRender();
        }

        internal static bool TryRequestDisplayRefresh(BridgeWorkerQuiltOutput rigOutput, out string error)
        {
            return TryRequestDisplayRefresh(rigOutput, out _, out error);
        }

        internal static bool TryRequestDisplayRefresh(BridgeWorkerQuiltOutput rigOutput,
            out BridgeWorkerQuiltOutput requestOutput, out string error)
        {
            requestOutput = rigOutput;
            if (rigOutput == null)
            {
                error = "A Bridge worker output is required to refresh connected displays.";
                return false;
            }
            ViewWeaveBridgeSession sourceSession = rigOutput.GetComponent<ViewWeaveBridgeSession>();
            if (sourceSession != null && (sourceSession.State == ViewWeaveBridgeSession.SessionState.Connecting ||
                sourceSession.State == ViewWeaveBridgeSession.SessionState.Reconfiguring ||
                sourceSession.State == ViewWeaveBridgeSession.SessionState.Closing ||
                sourceSession.State == ViewWeaveBridgeSession.SessionState.Fault))
            {
                error = "Connected displays cannot refresh while the Session is connecting, reconfiguring, stopping, or recovering.";
                return false;
            }
            if (outputToDevice && IsActive && producerOutput != null && target != null &&
                rigOutput.GetComponent<ViewWeaveRig>() == target)
                requestOutput = producerOutput;
            return requestOutput.TryRequestDisplayRefresh(out error);
        }

        static bool ResolveDeviceConfiguration(out string error)
        {
            error = string.Empty;
            string configuredPath = sourceSession != null ? sourceSession.runtimePath : sourceOutput == null ? string.Empty : sourceOutput.runtimePath;
            if (!BridgeRuntimeLocator.TryResolve(configuredPath, out runtimePathAtStart, out error)) return false;
            string configuredApplication = sourceSession != null ? sourceSession.applicationName : sourceOutput == null ? "ViewWeave" : sourceOutput.applicationName;
            applicationNameAtStart = string.IsNullOrWhiteSpace(configuredApplication) ? "ViewWeave" : configuredApplication;
            displayOrdinalAtStart = sourceSession != null ? sourceSession.displayOrdinal : sourceOutput == null ? 0u : sourceOutput.displayOrdinal;
            return true;
        }

        static void BeginPause()
        {
            if (!active || stopping || previewState == PreviewState.Paused || previewState == PreviewState.Pausing) return;
            refreshRequested = false;
            resumeAfterPause = false;
            if (!outputToDevice)
            {
                previewState = PreviewState.Paused;
                status = "Local preview paused";
                lastError = string.Empty;
                QuiltDisplaySettingsResolver.NotifyChanged();
                return;
            }
            if (producerOutput == null || !producerOutput.Ready)
            {
                BeginStop(false, "The Bridge worker was not Ready, so the preview could not be paused safely and is closing instead.");
                return;
            }
            previewState = PreviewState.Pausing;
            nativePauseRequestPending = true;
            status = "Pausing preview";
            lastError = string.Empty;
            QuiltDisplaySettingsResolver.NotifyChanged();
            TryIssueNativePauseRequest();
        }

        static bool TryIssueNativePauseRequest()
        {
            if (!nativePauseRequestPending) return true;
            if (producerOutput == null || producerOutput.EditorPreviewImageValidationPending) return false;
            if (!producerOutput.TrySetEditorPreviewPaused(OwnerToken, true, out string error))
            {
                if (producerOutput.EditorPreviewImageValidationPending) return false;
                BeginStop(false, "Reusable preview pause failed: " + Sanitize(error));
                return false;
            }
            nativePauseRequestPending = false;
            return true;
        }

        static bool RequestResume(ViewWeaveRig rig, bool deviceOutput, out string error)
        {
            error = string.Empty;
            if (rig == null || rig != target || deviceOutput != outputToDevice || producerObject == null || producerRenderer == null)
                return FailStart("The paused preview no longer matches this Rig and output mode. Close it before starting another preview.", out error);
            if (!rig.isActiveAndEnabled)
                return FailStart("The paused preview target is no longer active.", out error);
            if (!ValidatePreviewStage(rig, out string stageError))
                return FailStart("The paused preview target is no longer valid: " + Sanitize(stageError), out error);
            if (!rig.ValidateRig(out string rigError))
                return FailStart("The paused preview target is no longer valid: " + Sanitize(rigError), out error);
            if (GraphicsSettings.currentRenderPipeline != pipelineAtStart)
            {
                restartAfterStop = true;
                BeginStop(true, "Preview is rebuilding after a render-pipeline change.");
                return true;
            }
            if (NeedsRebuild())
            {
                restartAfterStop = true;
                BeginStop(true, "Preview is rebuilding after a layout, quality, or MSAA change.");
                return true;
            }
            if (deviceOutput)
            {
                if (producerOutput == null || !producerOutput.Ready || producerOutput.RequiresReconnect)
                {
                    BeginStop(false, "The paused Bridge connection is no longer valid. Stop and reconnect before resuming.");
                    return FailStart("The paused Bridge connection is no longer valid. Stop and reconnect before resuming.", out error);
                }
                if (producerOutput.EditorPreviewImageValidationPending)
                {
                    resumeAfterPause = true;
                    return true;
                }
                if (!producerOutput.TryGetEditorPreviewPausePhase(OwnerToken, out BridgeWorkerQuiltOutput.EditorPreviewPausePhase nativePhase, out error))
                {
                    if (producerOutput.EditorPreviewImageValidationPending)
                    {
                        resumeAfterPause = true;
                        error = string.Empty;
                        return true;
                    }
                    BeginStop(false, "Bridge pause state could not be verified: " + Sanitize(error));
                    return false;
                }
                if (nativePhase != BridgeWorkerQuiltOutput.EditorPreviewPausePhase.Paused)
                {
                    if (previewState == PreviewState.Pausing)
                    {
                        resumeAfterPause = true;
                        error = string.Empty;
                        return true;
                    }
                    error = "Bridge worker has not acknowledged the paused state yet.";
                    return false;
                }
                if (!producerOutput.TrySetEditorPreviewPaused(OwnerToken, false, out error))
                {
                    if (producerOutput.EditorPreviewImageValidationPending)
                    {
                        resumeAfterPause = true;
                        error = string.Empty;
                        return true;
                    }
                    BeginStop(false, "Reusable preview resume failed: " + Sanitize(error));
                    return false;
                }
                resumeAfterPause = false;
                previewState = PreviewState.Resuming;
                status = "Resuming preview";
                lastError = string.Empty;
                QuiltDisplaySettingsResolver.NotifyChanged();
                QueueEditorRender();
                return true;
            }

            CompleteLocalResume();
            return true;
        }

        static void CompleteLocalResume()
        {
            CopySourceSettings(target, false);
            previewState = PreviewState.Running;
            refreshRequested = true;
            nextRenderTime = 0;
            status = "Local preview running";
            lastError = string.Empty;
            QuiltDisplaySettingsResolver.NotifyChanged();
            QueueEditorRender();
        }

        static void CompleteDeviceResume()
        {
            if (producerOutput != null) producerOutput.CompleteEditorPreviewResume(OwnerToken);
            CopySourceSettings(target, true);
            previewState = PreviewState.Running;
            refreshRequested = true;
            nextRenderTime = 0;
            status = "Looking Glass preview running";
            lastError = string.Empty;
            QuiltDisplaySettingsResolver.NotifyChanged();
            QueueEditorRender();
        }

        static void BuildProducer(ViewWeaveRig sourceRig)
        {
            Camera sourceCamera = sourceRig.Renderer.sourceCamera;
            Camera framingCamera = sourceRig.Renderer.EffectiveCamera;
            Camera explicitReferenceCamera = sourceRig.Renderer.referenceCamera;
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
            producerMutationOwnerRegistration = QuiltRendererMutationPolicy.RegisterOwner(producerRenderer, OwnerToken,
                GetProducerMutationBlockReason, out string producerMutationError);
            if (producerMutationOwnerRegistration == null)
                throw new InvalidOperationException("Editor preview could not register its private producer: " + Sanitize(producerMutationError));
            producerRenderer.sourceCamera = producerCamera;
            producerRenderer.referenceCamera = explicitReferenceCamera;
            producerRenderer.followReferenceFieldOfView = sourceRig.Renderer.followReferenceFieldOfView;
            producerRenderer.useRenderRequests = sourceRig.Renderer.useRenderRequests;
            producerRenderer.viewRenderMode = sourceRig.Renderer.viewRenderMode;
            producerRenderer.settings = sourceRig.Renderer.settings.Clone();
            producerRenderer.physicalFraming = sourceRig.Renderer.physicalFraming == null ? new QuiltPhysicalFraming() : sourceRig.Renderer.physicalFraming.Clone();
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
            producerRenderer.referenceCamera = explicitReferenceCamera;
            producerRenderer.followReferenceFieldOfView = sourceRig.Renderer.followReferenceFieldOfView;
            producerRenderer.physicalFraming = sourceRig.Renderer.physicalFraming == null ? new QuiltPhysicalFraming() : sourceRig.Renderer.physicalFraming.Clone();
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
            producerRenderer.referenceCamera = sourceRig.Renderer.referenceCamera;
            producerRenderer.followReferenceFieldOfView = sourceRig.Renderer.followReferenceFieldOfView;
            producerRenderer.physicalFraming = sourceRig.Renderer.physicalFraming == null ? new QuiltPhysicalFraming() : sourceRig.Renderer.physicalFraming.Clone();
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
            RefreshLiveSessionProfiles();
            bool hasPreviewWork = active || stopping || BridgeEditorPreviewRetention.HasPending;
            if (hasPreviewWork && !QuiltRenderer.EditorPreviewRenderCallbackTrackingReady)
            {
                lastError = "Editor preview is paused because SRP render callback tracking is unavailable or inconsistent.";
                status = "Preview paused";
                return;
            }
            if (hasPreviewWork && QuiltRenderer.EditorPreviewRenderCallbackActive)
            {
                QueueEditorRender();
                return;
            }
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
                restartAfterStop = previewState == PreviewState.Running;
                BeginStop(restartAfterStop, previewState == PreviewState.Running
                    ? "Preview is rebuilding after a render-pipeline change."
                    : "Preview closed after a render-pipeline change while paused or transitioning.");
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
            if (NeedsRebuild() && previewState != PreviewState.Paused)
            {
                restartAfterStop = previewState == PreviewState.Running;
                BeginStop(restartAfterStop, previewState == PreviewState.Running
                    ? "Preview is rebuilding after a layout, quality, or MSAA change."
                    : "Preview closed after an incompatible change during a pause transition.");
                return;
            }
            if (outputToDevice && target.Renderer.physicalFraming != null && target.Renderer.physicalFraming.enabled &&
                ((sourceOutput != null && sourceOutput.zoom != 1f) ||
                 (producerOutput != null && producerOutput.zoom != 1f)))
            {
                BeginStop(false, "Physical 1:1 framing stopped because Bridge zoom changed from 1.");
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
                    if (producerOutput.RequiresReconnect)
                    {
                        BeginStop(false, string.IsNullOrEmpty(producerOutput.DisplayRefreshStatus)
                            ? "The selected Looking Glass profile changed. Stop and reconnect before rendering."
                            : producerOutput.DisplayRefreshStatus);
                        return;
                    }
                    if (!profileApplied && !ApplyDeviceProfile())
                    {
                        BeginStop(false, lastError);
                        return;
                    }
                }

                CopySourceCamera(target);
                if (outputToDevice && profileApplied && target.Renderer.physicalFraming != null && target.Renderer.physicalFraming.enabled && producerOutput != null &&
                    !producerOutput.Profile.MatchesPhysicalAspect(target.Renderer.physicalFraming.visibleApertureWidthMillimeters, target.Renderer.physicalFraming.visibleApertureHeightMillimeters))
                {
                    BeginStop(false, "Preview stopped because the physical aperture aspect no longer matches the connected display.");
                    return;
                }
                if (AdvancePauseState()) return;
                if (!producerRenderer.TryGetEffectiveSettings(out _, out string framingError))
                    throw new InvalidOperationException("Physical framing is invalid: " + Sanitize(framingError));
                if (refreshRequested || EditorApplication.timeSinceStartup >= nextRenderTime) RenderPreview();
                QueueEditorRender();
            }
            catch (Exception exception)
            {
                BeginStop(false, "Editor preview stopped after an error: " + Sanitize(exception.Message));
            }
        }

        static bool AdvancePauseState()
        {
            if (previewState == PreviewState.Running || previewState == PreviewState.Idle || previewState == PreviewState.Closing)
                return false;
            if (!outputToDevice)
            {
                if (previewState == PreviewState.Pausing) previewState = PreviewState.Paused;
                if (previewState == PreviewState.Paused && resumeAfterPause)
                {
                    resumeAfterPause = false;
                    CompleteLocalResume();
                    return false;
                }
                return previewState != PreviewState.Running;
            }
            if (producerOutput == null || !producerOutput.Ready)
            {
                BeginStop(false, "The paused Bridge worker is no longer Ready.");
                return true;
            }
            if (producerOutput.EditorPreviewImageValidationPending) return true;
            if (previewState == PreviewState.Pausing && nativePauseRequestPending)
            {
                TryIssueNativePauseRequest();
                return true;
            }
            if (!producerOutput.TryGetEditorPreviewPausePhase(OwnerToken, out BridgeWorkerQuiltOutput.EditorPreviewPausePhase nativePhase, out string error))
            {
                if (producerOutput.EditorPreviewImageValidationPending) return true;
                BeginStop(false, "Bridge pause state could not be verified: " + Sanitize(error));
                return true;
            }
            if (nativePhase == BridgeWorkerQuiltOutput.EditorPreviewPausePhase.Faulted)
            {
                BeginStop(false, "Bridge worker faulted while pausing or resuming the preview.");
                return true;
            }
            switch (previewState)
            {
                case PreviewState.Pausing:
                    if (nativePhase == BridgeWorkerQuiltOutput.EditorPreviewPausePhase.Paused)
                    {
                        previewState = PreviewState.Paused;
                        status = "Looking Glass preview paused";
                        QuiltDisplaySettingsResolver.NotifyChanged();
                        if (resumeAfterPause)
                        {
                            if (!RequestResume(target, true, out error))
                                BeginStop(false, "Preview resume request failed: " + Sanitize(error));
                        }
                    }
                    return true;
                case PreviewState.Paused:
                    if (resumeAfterPause)
                    {
                        if (!RequestResume(target, true, out error))
                            BeginStop(false, "Preview resume request failed: " + Sanitize(error));
                    }
                    return true;
                case PreviewState.Resuming:
                    if (nativePhase == BridgeWorkerQuiltOutput.EditorPreviewPausePhase.Running)
                    {
                        CompleteDeviceResume();
                        return false;
                    }
                    return true;
                default:
                    return false;
            }
        }

        static void RefreshLiveSessionProfiles()
        {
            ViewWeaveBridgeSession[] sessions = Resources.FindObjectsOfTypeAll<ViewWeaveBridgeSession>();
            System.Text.StringBuilder signature = new System.Text.StringBuilder();
            for (int i = 0; i < sessions.Length; ++i)
            {
                ViewWeaveBridgeSession session = sessions[i];
                if (session == null || !session.gameObject.scene.IsValid() || !session.gameObject.scene.isLoaded) continue;
                QuiltRenderer renderer = session.GetComponent<ViewWeaveRig>()?.Renderer;
                if (renderer == null) continue;
                BridgeWorkerQuiltOutput sessionOutput = session.GetComponent<BridgeWorkerQuiltOutput>();
                BridgeDisplayProfile profile = session.Profile;
                QuiltPhysicalFraming physical = renderer.physicalFraming;
                signature.Append(renderer.GetEntityId().ToString()).Append(':').Append((int)session.State).Append(':')
                    .Append(session.Quality).Append(':').Append(session.runtimePath).Append(':').Append(session.applicationName).Append(':')
                    .Append(session.displayOrdinal).Append(':');
                if (sessionOutput != null)
                    signature.Append(sessionOutput.Ready).Append(':').Append(sessionOutput.applicationName).Append(':')
                        .Append(sessionOutput.displayOrdinal).Append(':').Append(sessionOutput.zoom.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':');
                if (session.State == ViewWeaveBridgeSession.SessionState.Rendering)
                    signature.Append(profile.DisplayIndex).Append(':').Append(profile.Width).Append('x').Append(profile.Height).Append(':')
                        .Append(profile.Aspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                        .Append(profile.Viewcone.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                        .Append(profile.HasPhysicalCalibration).Append(':')
                        .Append(profile.PhysicalWidthMillimeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('x')
                        .Append(profile.PhysicalHeightMillimeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                if (physical != null)
                    signature.Append(':').Append(physical.enabled).Append(':')
                        .Append(physical.visibleApertureWidthMillimeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('x')
                        .Append(physical.visibleApertureHeightMillimeters.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                        .Append(physical.unitsPerMeter.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                if (renderer.settings != null)
                    signature.Append(':').Append(renderer.settings.viewAspect.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                        .Append(QuiltFramingResolver.EffectiveFieldOfView(renderer).ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                        .Append(renderer.settings.cameraDistance.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append(':')
                        .Append(renderer.settings.viewCone.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                signature.Append(';');
            }
            string next = signature.ToString();
            if (string.Equals(next, lastSessionProfileSignature, StringComparison.Ordinal)) return;
            lastSessionProfileSignature = next;
            QuiltDisplaySettingsResolver.NotifyChanged();
        }

        static bool ApplyDeviceProfile()
        {
            BridgeDisplayProfile displayProfile = producerOutput.Profile;
            if (!displayProfile.IsValid) { lastError = "Bridge Worker returned an invalid display profile."; return false; }
            QuiltPhysicalFraming physical = target.Renderer.physicalFraming;
            if (physical != null && physical.enabled && !displayProfile.MatchesPhysicalAspect(physical.visibleApertureWidthMillimeters, physical.visibleApertureHeightMillimeters))
            {
                lastError = "Physical aperture aspect does not match the connected display profile.";
                return false;
            }
            try
            {
                QuiltQualityPreset quality = sourceSession == null ? QuiltQualityPreset.Balanced : sourceSession.Quality;
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
                QuiltDisplaySettingsResolver.NotifyChanged();
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
                QuiltQualityPreset quality = sourceSession == null ? QuiltQualityPreset.Balanced : sourceSession.Quality;
                if (quality != qualityAtStart) return true;
                if (sourceSession != null && (sourceSession.runtimePath != configuredRuntimePathAtStart ||
                    sourceSession.applicationName != configuredApplicationNameAtStart || sourceSession.displayOrdinal != displayOrdinalAtStart)) return true;
                if (sourceOutput != null && (sourceOutput.runtimePath != configuredOutputRuntimePathAtStart ||
                    sourceOutput.applicationName != configuredOutputApplicationNameAtStart || sourceOutput.displayOrdinal != configuredOutputDisplayOrdinalAtStart)) return true;
            }
            QuiltLayout desiredLayout = producerRenderer == null ? current.GetLayout() : producerRenderer.settings.GetLayout();
            int desiredMsaa = GetDesiredMsaaSamples(target.Renderer.sourceCamera, target.Renderer.GuardBehaviour as IQuiltMsaaProvider, desiredLayout);
            return lastMsaaSamples != 0 && desiredMsaa != lastMsaaSamples;
        }

        static string GetSourceMutationBlockReason(QuiltRendererMutationRequest request)
        {
            if (request.Kind == QuiltRendererMutationKind.AutoRender && request.HasBooleanValue && !request.BooleanValue)
                return string.Empty;
            if (!active || stopping || target == null || target.Renderer == null)
                return "The Editor preview is stopping or does not own an active Rig.";
            switch (request.Kind)
            {
                case QuiltRendererMutationKind.Framing:
                    if (request.HasFraming && request.Framing.PhysicalFramingEnabled)
                    {
                        if (!IsFinite(request.Framing.ApertureWidthMillimeters) || !IsFinite(request.Framing.ApertureHeightMillimeters) ||
                            !IsFinite(request.Framing.UnitsPerMeter) || request.Framing.ApertureWidthMillimeters <= 0f ||
                            request.Framing.ApertureHeightMillimeters <= 0f || request.Framing.UnitsPerMeter <= 0f)
                            return "Physical framing dimensions must be finite and greater than zero.";
                        if (outputToDevice)
                        {
                            if (producerOutput == null || !producerOutput.Ready || !producerOutput.Profile.IsValid)
                                return "Physical framing is available after the device preview profile is Ready.";
                            if (!IsFinite(producerOutput.zoom) || producerOutput.zoom != 1f ||
                                !producerOutput.Profile.MatchesPhysicalAspect(request.Framing.ApertureWidthMillimeters,
                                    request.Framing.ApertureHeightMillimeters))
                                return "Physical framing requires a matching connected display aspect and Bridge zoom of 1.";
                        }
                    }
                    return string.Empty;
                case QuiltRendererMutationKind.SamplingMode:
                case QuiltRendererMutationKind.Quality:
                    return string.Empty;
                case QuiltRendererMutationKind.AutoRender:
                    return request.HasBooleanValue && request.BooleanValue
                        ? "Auto Render cannot be enabled while the Editor preview owns this Rig."
                        : string.Empty;
                case QuiltRendererMutationKind.OutputZoom:
                    if (!request.HasFloatValue || !IsFinite(request.FloatValue) || request.FloatValue <= 0f)
                        return "Bridge zoom must be finite and greater than zero.";
                    if (target.Renderer.physicalFraming != null && target.Renderer.physicalFraming.enabled && request.FloatValue != 1f)
                        return "Physical 1:1 framing requires Bridge zoom to be exactly 1.";
                    return outputToDevice && (producerOutput == null || !producerOutput.Ready)
                        ? "Bridge zoom can change after the device preview is Ready."
                        : string.Empty;
                default:
                    return "The Editor preview owns this Rig; stop preview before changing its structure or transport.";
            }
        }

        static string GetProducerMutationBlockReason(QuiltRendererMutationRequest request)
        {
            if (producerObject == null || retentionRecord == null || stopping)
                return "The private Editor preview producer is stopping.";
            return string.Empty;
        }

        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

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
                producerRenderer.RenderEditorPreviewAtBoundary(retentionRecord);
                layout = producerRenderer.QuiltTexture == null ? producerRenderer.settings.GetLayout() : producerRenderer.LatestSnapshot.Layout;
                if (outputToDevice && !producerOutput.TrySubmitEditorPreview(OwnerToken, producerRenderer.QuiltTexture, layout))
                {
                    if (producerOutput.LastSubmissionOutcome == BridgeWorkerSubmissionOutcome.RetryableBusy)
                    {
                        status = "Bridge worker is busy; retrying the preview quilt.";
                        nextRenderTime = EditorApplication.timeSinceStartup;
                        return;
                    }
                    throw new InvalidOperationException(string.IsNullOrEmpty(producerOutput.LastError) ? "Bridge Worker rejected the preview quilt." : producerOutput.LastError);
                }
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
            bool firstStopRequest = !stopping;
            if (firstStopRequest && !string.IsNullOrEmpty(reason))
            {
                lastError = Sanitize(reason);
                status = "Stopping preview";
            }
            active = false;
            stopping = true;
            previewState = PreviewState.Closing;
            resumeAfterPause = false;
            nativePauseRequestPending = false;
            QuiltDisplaySettingsResolver.NotifyChanged();
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
                CompleteStop();
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
            previewState = PreviewState.Idle;
            nativePauseRequestPending = false;
            DisposeMutationOwnerRegistrations();
            bool restart = restartAfterStop;
            restartAfterStop = false;
            ViewWeaveRig nextTarget = target;
            bool nextDevice = outputToDevice;
            status = string.IsNullOrEmpty(lastError) ? "Idle" : "Stopped with an error";
            QuiltDisplaySettingsResolver.NotifyChanged();
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
            DisposeMutationOwnerRegistrations();
            active = false;
            stopping = false;
            previewState = PreviewState.Idle;
            nativePauseRequestPending = false;
            resumeAfterPause = false;
            rendering = false;
            profileApplied = false;
            refreshRequested = false;
            sourceSession = null;
            sourceOutput = null;
            QuiltDisplaySettingsResolver.NotifyChanged();
        }

        static void DisposeMutationOwnerRegistrations()
        {
            if (sourceMutationOwnerRegistration != null)
            {
                sourceMutationOwnerRegistration.Dispose();
                sourceMutationOwnerRegistration = null;
                if (target != null && target.Renderer != null) target.Renderer.SetMutationOwnerRequired(false);
            }
            if (producerMutationOwnerRegistration != null)
            {
                producerMutationOwnerRegistration.Dispose();
                producerMutationOwnerRegistration = null;
            }
        }

        internal static bool ResolveDisplayProfile(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile)
        {
            profile = default;
            if (renderer == null) return false;
            bool sameTarget = target != null && target.Renderer == renderer;
            bool producerReady = producerRenderer != null && producerRenderer.settings != null &&
                                 (!outputToDevice || producerOutput != null && producerOutput.Ready);
            bool needsRebuild = !producerReady || target == null || target.Renderer == null || NeedsRebuild();
            if (IsPreviewProfileCurrent(active, stopping, profileApplied, sameTarget, producerReady, needsRebuild) &&
                (!outputToDevice || IsCurrentDevicePreviewProfile(renderer)))
            {
                BridgeDisplayProfile device = outputToDevice && producerOutput != null ? producerOutput.Profile : default;
                QuiltSettings settings = producerRenderer.settings;
                profile = new QuiltEditorDisplayProfile(settings.viewAspect, settings.viewCone,
                    outputToDevice ? QuiltDisplaySettingsSource.DevicePreview : QuiltDisplaySettingsSource.LocalPreview,
                    outputToDevice, outputToDevice && device.HasPhysicalCalibration, device.PhysicalWidthMillimeters,
                    device.PhysicalHeightMillimeters, device.CalibrationDpi, outputToDevice ? device.ToString() : "Explicit local preview");
                return true;
            }
            ViewWeaveBridgeSession session = renderer.GetComponent<ViewWeaveBridgeSession>();
            if (session != null && session.TryGetEditorDisplayProfile(renderer, out BridgeDisplayProfile connected))
            {
                profile = new QuiltEditorDisplayProfile(connected.Aspect, connected.Viewcone, QuiltDisplaySettingsSource.BridgeSession,
                    true, connected.HasPhysicalCalibration, connected.PhysicalWidthMillimeters, connected.PhysicalHeightMillimeters,
                    connected.CalibrationDpi, connected.ToString());
                return true;
            }
            BridgeWorkerQuiltOutput output = renderer.OutputBehaviour as BridgeWorkerQuiltOutput;
            BridgeDisplaySnapshot snapshot = output == null ? null : output.CurrentConfigurationDisplaySnapshot;
            ViewWeaveBridgeSession displaySession = renderer.GetComponent<ViewWeaveBridgeSession>();
            uint ordinal = displaySession != null ? displaySession.displayOrdinal : output == null ? 0u : output.displayOrdinal;
            if (snapshot != null && snapshot.TryGetDisplay(ordinal, out BridgeDisplayProfile cached))
            {
                profile = new QuiltEditorDisplayProfile(cached.Aspect, cached.Viewcone, QuiltDisplaySettingsSource.ConnectedDisplaySnapshot,
                    true, cached.HasPhysicalCalibration, cached.PhysicalWidthMillimeters, cached.PhysicalHeightMillimeters,
                    cached.CalibrationDpi, "Last explicit Bridge display refresh, generation " + snapshot.Generation + ": " + cached);
                return true;
            }
            return false;
        }

        static void OnConnectedDisplaySnapshotPublished(BridgeDisplaySnapshot snapshot)
        {
            QuiltDisplaySettingsResolver.NotifyChanged();
            SceneView.RepaintAll();
        }

        static bool IsCurrentDevicePreviewProfile(QuiltRenderer renderer)
        {
            if (!outputToDevice || producerOutput == null || producerRenderer == null || producerRenderer.settings == null)
                return false;
            return IsDevicePreviewProfileCurrent(renderer, producerOutput.Ready, producerOutput.Profile, NeedsRebuild(), producerOutput.zoom);
        }

        internal static bool IsPreviewProfileCurrent(bool activePreview, bool isStopping, bool hasAppliedProfile,
            bool sameTarget, bool producerReady, bool needsRebuild)
        {
            return activePreview && !isStopping && hasAppliedProfile && sameTarget && producerReady && !needsRebuild;
        }

        internal static bool IsDevicePreviewProfileCurrent(QuiltRenderer renderer, bool workerReady,
            BridgeDisplayProfile profile, bool needsRebuild, float zoom)
        {
            if (renderer == null || !workerReady || !profile.IsValid || needsRebuild) return false;
            if (renderer.physicalFraming != null && renderer.physicalFraming.enabled)
            {
                if (zoom != 1f) return false;
                if (!profile.MatchesPhysicalAspect(renderer.physicalFraming.visibleApertureWidthMillimeters,
                        renderer.physicalFraming.visibleApertureHeightMillimeters))
                    return false;
            }
            return true;
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
