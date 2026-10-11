using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave.Editor
{
    [InitializeOnLoad]
    public static class BridgeMetalEditorPreview
    {
        static ViewWeaveRig target, producer;
        static BridgeMetalQuiltOutput output;
        static BridgeMetalQuiltOutput configuredOutput;
        static BridgeSubpixelMode subpixelModeAtStart;
        static string runtimePathAtStart, applicationNameAtStart;
        static uint displayOrdinalAtStart;
        static QuiltEditorResourceRecord resources;
        static IDisposable owner;
        static readonly object OwnerToken = new object();
        static RenderPipelineAsset pipeline;
        static QuiltLayout initialLayout;
        static Camera initialSource, initialReference;
        static bool paused, device;
        static double nextRender;
        public static ViewWeaveRig Target => target;
        public static bool IsActive => producer != null && resources != null && !resources.IsStopped;
        public static bool IsPaused => IsActive && paused;
        public static bool OutputToDevice => device;
        public static bool IsStopping => !IsActive && (resources != null || BridgeMetalQuiltOutput.PendingRetirementCount != 0);
        public static int PendingResourceCount => QuiltEditorResourceRecord.PendingCount;
        public static RenderTexture Texture => IsActive ? producer.Renderer.QuiltTexture : null;
        public static QuiltLayout Layout => IsActive ? producer.Renderer.settings.GetLayout() : default;
        public static string Status { get; private set; } = "Idle";
        public static string LastError { get; private set; } = string.Empty;
        public static int RenderedFrames { get; private set; }
        public static uint PresentedFrames => output == null ? 0u : output.CompletedFrames;
        public static uint DroppedFrames => output == null ? 0u : output.DroppedFrames;

        static BridgeMetalEditorPreview()
        {
            EditorApplication.update += Update;
            EditorApplication.playModeStateChanged += state =>
            { if (state == PlayModeStateChange.ExitingEditMode) Stop(); };
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += Stop;
        }

        public static bool Start(ViewWeaveRig rig, bool outputToDevice, out string error)
        {
            error = string.Empty;
            if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating || Application.platform != RuntimePlatform.OSXEditor ||
                SystemInfo.graphicsDeviceType != GraphicsDeviceType.Metal || GraphicsSettings.currentRenderPipeline == null)
                return Fail("Metal preview requires stable macOS Edit Mode with an SRP and Metal.", out error);
            if (IsActive || resources != null || QuiltEditorResourceRecord.PendingCount != 0 || BridgeMetalQuiltOutput.PendingRetirementCount != 0 ||
                BridgeEditorPreview.IsActive || BridgeEditorPreview.IsStopping)
                return Fail("Close the existing preview and wait for GPU retirement before starting another preview.", out error);
            if (rig == null || !rig.isActiveAndEnabled) return Fail("An enabled ViewWeave Rig is required for preview.", out error);
            if (!rig.ValidateRig(out error)) return Fail(error, out error);
            QuiltRenderer renderer = rig.Renderer;
            if (!AllowedObject(rig.gameObject, renderer.sourceCamera.gameObject) || !AllowedObject(rig.gameObject, renderer.EffectiveCamera.gameObject))
                return Fail("Preview cameras must be loaded scene objects in the Rig's Editor Stage.", out error);
            if (!renderer.TryCheckMutation(QuiltRendererMutationKind.Transport, out error) || QuiltRendererOperationLease.IsLeased(renderer))
                return Fail(string.IsNullOrEmpty(error) ? "Stop recording or playback before starting preview." : error, out error);
            if (!QuiltRenderer.EditorPreviewRenderCallbackTrackingReady || !SystemInfo.supportsAsyncGPUReadback)
                return Fail("Editor callback tracking and asynchronous GPU retirement are required.", out error);
            target = rig;
            device = outputToDevice;
            paused = false;
            RenderedFrames = 0;
            try
            {
                initialLayout = renderer.settings.GetLayout();
                initialSource = renderer.sourceCamera;
                initialReference = renderer.referenceCamera;
                pipeline = GraphicsSettings.currentRenderPipeline;
                producer = QuiltEditorProducer.Create(rig, out resources);
                if (device)
                {
                    BridgeMetalQuiltOutput configured = rig.GetComponent<BridgeMetalQuiltOutput>();
                    if (configured == null) throw new InvalidOperationException("A Metal output component is required for device preview.");
                    if (!BridgeDisplayGeometryStore.TryConfiguration(renderer, out MonoBehaviour selected, out _, out _, out _) || selected != configured)
                        throw new InvalidOperationException("Device preview requires a selected or sole attached Metal output.");
                    configuredOutput = configured;
                    subpixelModeAtStart = configured.SubpixelMode;
                    runtimePathAtStart = configured.runtimePath;
                    applicationNameAtStart = configured.applicationName;
                    displayOrdinalAtStart = configured.displayOrdinal;
                    output = producer.gameObject.AddComponent<BridgeMetalQuiltOutput>();
                    output.rig = producer;
                    output.runtimePath = configured.runtimePath;
                    output.applicationName = configured.applicationName;
                    output.displayOrdinal = configured.displayOrdinal;
                    output.quality = configured.quality;
                    if (!output.TrySetSubpixelMode(subpixelModeAtStart, out error)) throw new InvalidOperationException(error);
                    if (!output.StartEditorPreview()) throw new InvalidOperationException(output.LastError);
                }
                owner = QuiltRendererMutationPolicy.RegisterOwner(renderer, OwnerToken, BlockMutation, out error);
                if (owner == null) throw new InvalidOperationException(error);
                if (device)
                    BridgeDisplayMetadata.Publish(rig, configuredOutput, configuredOutput.runtimePath,
                        configuredOutput.applicationName, configuredOutput.displayOrdinal, output.Profile);
                nextRender = 0;
                Status = device ? "Looking Glass preview running" : "Local preview running";
                LastError = string.Empty;
                QuiltDisplaySettingsResolver.NotifyChanged();
                EditorApplication.QueuePlayerLoopUpdate();
                return true;
            }
            catch (Exception exception) { Stop(); return Fail(exception.Message, out error); }
        }

        static bool AllowedObject(GameObject rig, GameObject value) => value != null && !EditorUtility.IsPersistent(value) &&
            value.scene.IsValid() && value.scene.isLoaded && StageUtility.GetStageHandle(rig) == StageUtility.GetStageHandle(value) &&
            (!EditorSceneManager.IsPreviewSceneObject(value) || PrefabStageUtility.GetPrefabStage(value) != null);

        static string BlockMutation(QuiltRendererMutationRequest request)
        {
            if (request.Kind == QuiltRendererMutationKind.Framing || request.Kind == QuiltRendererMutationKind.SamplingMode)
            {
                if (device && request.HasFraming && request.Framing.PhysicalFramingEnabled && output != null &&
                    !output.Profile.MatchesPhysicalAspect(request.Framing.ApertureWidthMillimeters, request.Framing.ApertureHeightMillimeters))
                    return "Physical aperture aspect must match the connected display.";
                return string.Empty;
            }
            if (request.Kind == QuiltRendererMutationKind.AutoRender && request.HasBooleanValue && !request.BooleanValue) return string.Empty;
            return "Close Metal preview before changing structure, quality, transport, recording or playback.";
        }

        public static void TogglePause()
        {
            if (!IsActive) return;
            paused = !paused;
            nextRender = 0;
            Status = paused ? "Preview paused; the last frame is held" : device ? "Looking Glass preview running" : "Local preview running";
        }

        public static void RequestRefresh() { nextRender = 0; EditorApplication.QueuePlayerLoopUpdate(); }

        static bool Fail(string reason, out string error)
        {
            error = string.IsNullOrEmpty(reason) ? "Metal preview could not start." : reason;
            if (!IsActive) { LastError = error; Status = "Preview error"; }
            return false;
        }

        public static void Stop()
        {
            if (output != null) output.StopOutput();
            if (resources != null) resources.Stop();
            owner?.Dispose();
            owner = null;
            Status = resources != null ? "Stopping preview; waiting for GPU retirement" : "Idle";
            QuiltDisplaySettingsResolver.NotifyChanged();
        }

        static void Update()
        {
            if (!IsActive)
            {
                if (resources == null)
                {
                    producer = null;
                    output = null;
                    configuredOutput = null;
                    target = null;
                    if (BridgeMetalQuiltOutput.PendingRetirementCount == 0) Status = string.IsNullOrEmpty(LastError) ? "Idle" : "Stopped with an error";
                }
                else if (!string.IsNullOrEmpty(resources.Failure)) LastError = resources.Failure;
                return;
            }
            try
            {
                if (Application.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode || target == null || !target.isActiveAndEnabled ||
                    target.Renderer == null || target.Renderer.settings == null ||
                    target.Renderer.sourceCamera != initialSource || target.Renderer.referenceCamera != initialReference ||
                    !target.Renderer.settings.GetLayout().Equals(initialLayout) || GraphicsSettings.currentRenderPipeline != pipeline)
                { Stop(); return; }
                if (output != null)
                {
                    if (configuredOutput == null || configuredOutput.SubpixelMode != subpixelModeAtStart ||
                        configuredOutput.runtimePath != runtimePathAtStart || configuredOutput.applicationName != applicationNameAtStart ||
                        configuredOutput.displayOrdinal != displayOrdinalAtStart ||
                        !BridgeDisplayGeometryStore.TryConfiguration(target.Renderer, out MonoBehaviour selected, out _, out _, out _) || selected != configuredOutput)
                        throw new InvalidOperationException("Bridge configuration changed during device preview. Close preview before changing it.");
                    output.PumpStatus();
                    if (!output.IsRunning) throw new InvalidOperationException(output.LastError);
                }
                if (paused || EditorApplication.timeSinceStartup < nextRender || QuiltRenderer.EditorPreviewRenderCallbackActive) return;
                nextRender = EditorApplication.timeSinceStartup + 0.2d;
                QuiltEditorProducer.Sync(target, producer, device);
                resources.MarkRendered();
                long revision = producer.Renderer.QuiltRevision;
                producer.Renderer.RenderEditorPreviewAtBoundary(resources);
                if (producer.Renderer.QuiltRevision != revision) ++RenderedFrames;
                Status = (device ? "Looking Glass preview running" : "Local preview running") +
                    (string.IsNullOrEmpty(producer.Renderer.LastRenderStatus) ? string.Empty : "; " + producer.Renderer.LastRenderStatus);
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
            catch (Exception exception) { Stop(); Fail(exception.Message, out _); }
        }

        internal static bool ResolveDisplayProfile(QuiltRenderer renderer, out QuiltEditorDisplayProfile profile)
        {
            profile = default;
            if (!IsActive || target == null || target.Renderer != renderer) return false;
            QuiltSettings settings = producer.Renderer.settings;
            BridgeDisplayProfile connected = output == null ? default : output.Profile;
            profile = new QuiltEditorDisplayProfile(settings.viewAspect, settings.viewCone,
                device ? QuiltDisplaySettingsSource.DevicePreview : QuiltDisplaySettingsSource.LocalPreview,
                device, device && connected.HasPhysicalCalibration, connected.PhysicalWidthMillimeters,
                connected.PhysicalHeightMillimeters, connected.CalibrationDpi, device ? connected.ToString() : "Explicit local preview");
            return true;
        }
    }
}
