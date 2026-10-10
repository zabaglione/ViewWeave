using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace ViewWeave.HDRP
{
    /// <summary>
    /// Measures HDRP exposure with a persistent central camera. A result includes exposure
    /// compensation; apply it as Fixed exposure with compensation set to zero.
    /// </summary>
    public sealed class HdrpSharedExposureMeter : IDisposable
    {
        const string ShaderResourceName = "ViewWeaveHdrpExposureSample";
        const string PreparingStatus = "HDRP exposure is preparing the central camera history.";
        const string PendingStatus = "HDRP exposure is waiting for the central camera GPU sample.";
        const string ReadyStatus = "HDRP exposure is shared from the central camera.";

        MeterResources resources;
        Camera sourceCamera;
        RenderPipelineAsset pipelineAsset;
        long generation;
        long nextRequestSequence;
        long acceptedRequestSequence;
        long lastRenderFrame = long.MinValue;
        bool lastRenderWasPlaying;
        bool disposed;
        bool hasMeasurement;
        float measuredMultiplier;
        string failure;
#if UNITY_EDITOR
        long editorUpdate;
#endif

        public HdrpSharedExposureMeter()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += AdvanceEditorFrame;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload += Dispose;
            UnityEditor.EditorApplication.quitting += Dispose;
#endif
        }

        /// <summary>True while the current central camera has a completed, valid GPU sample.</summary>
        public bool IsReady => !disposed && hasMeasurement && failure == null;

        /// <summary>True while a readback belonging to the current measurement session is pending.</summary>
        public bool HasPendingReadback => resources != null && resources.PendingReadbacks != 0;

        /// <summary>Number of live or retiring GPU resource sets owned by all exposure meters.</summary>
        public static int ResourceRecordCount => HdrpExposureRetirementRecord.RecordCount;

        /// <summary>
        /// The failure that stopped measurement, or an empty string while preparing, pending, or
        /// ready. Failures remain set until Reset starts a new measurement session.
        /// </summary>
        public string LastError => failure ?? string.Empty;

        /// <summary>
        /// Advances measurement at most once per player frame or Editor update, without waiting
        /// for the GPU. Call again on later frames while this returns false and LastError is empty.
        /// After correcting an error, call Reset before trying again. The source camera
        /// supplies framing, clip planes, and culling; sourceData supplies the HDRP settings and
        /// may belong to a different reference camera. Pass the authored HDRP settings before
        /// quilt-only volume overrides are installed.
        /// </summary>
        public bool TryMeasure(Camera source, HDAdditionalCameraData sourceData, QuiltSettings settings,
            QuiltLayout layout, out float effectiveFixedEV, out string status)
        {
            effectiveFixedEV = 0f;
            if (disposed)
                return SetFailure("HDRP exposure meter has been disposed.", out status);
            if (failure != null)
            {
                status = failure;
                return false;
            }
            if (source == null || sourceData == null || settings == null)
                return SetFailure("HDRP exposure measurement requires a framing camera, reference HDRP camera data, and quilt settings.", out status);
            if (layout.TileWidth < 1 || layout.TileHeight < 1)
                return SetFailure("HDRP exposure measurement requires a valid quilt tile size.", out status);
            var activePipeline = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            if (activePipeline == null || !activePipeline.currentPlatformRenderPipelineSettings.supportCustomPass)
                return SetFailure("HDRP exposure measurement requires HDRP Custom Pass support.", out status);
            if (!SystemInfo.supportsAsyncGPUReadback ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.Render) ||
                !SystemInfo.IsFormatSupported(GraphicsFormat.R32_SFloat, GraphicsFormatUsage.ReadPixels))
                return SetFailure("HDRP exposure measurement requires asynchronous R32 float GPU readback.", out status);
            if (sourceData.hasCustomRender)
                return SetFailure("HDRP exposure measurement does not support a custom camera render callback.", out status);

            try
            {
                if (resources == null || sourceCamera != source || pipelineAsset != activePipeline ||
                    resources.RenderTarget.width != layout.TileWidth || resources.RenderTarget.height != layout.TileHeight)
                {
                    ResetMeasurement();
                    sourceCamera = source;
                    pipelineAsset = activePipeline;
                    resources = new MeterResources(layout.TileWidth, layout.TileHeight);
                }

                long frame = CurrentFrame;
                bool playing = Application.isPlaying;
                if (resources.PendingReadbacks == 0 && (lastRenderFrame != frame || lastRenderWasPlaying != playing))
                {
                    lastRenderFrame = frame;
                    lastRenderWasPlaying = playing;
                    RenderAndRequestSample(source, sourceData, settings);
                }
            }
            catch (Exception exception)
            {
                failure = "HDRP exposure measurement failed: " + exception.Message;
                hasMeasurement = false;
            }

            if (failure != null)
            {
                status = failure;
                return false;
            }
            if (!hasMeasurement)
            {
                status = resources != null && resources.RenderCount > 1 ? PendingStatus : PreparingStatus;
                return false;
            }

            // HDRP's public conversion includes lensImperfectionExposureScale. Re-convert the
            // retained multiplier when returning it, so delayed callbacks cannot preserve a
            // CPU conversion made with a different current lens attenuation setting.
            if (!TryConvertMultiplierToEffectiveEV(measuredMultiplier, out effectiveFixedEV))
                return SetFailure("HDRP exposure measurement produced an invalid effective exposure.", out status);
            status = ReadyStatus;
            return true;
        }

        /// <summary>Converts the already compensated multiplier using HDRP's current lens scale.</summary>
        public static bool TryConvertMultiplierToEffectiveEV(float multiplier, out float effectiveFixedEV)
        {
            effectiveFixedEV = 0f;
            float lensScale = ColorUtils.lensImperfectionExposureScale;
            if (!IsPositiveFinite(multiplier) || !IsPositiveFinite(lensScale)) return false;
            float converted = ColorUtils.ConvertExposureToEV100(multiplier);
            if (float.IsNaN(converted) || float.IsInfinity(converted)) return false;
            effectiveFixedEV = converted;
            return true;
        }

        /// <summary>Clears LastError, starts a new measurement generation, and discards old results.</summary>
        public void Reset()
        {
            if (disposed) return;
            ResetMeasurement();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= AdvanceEditorFrame;
            UnityEditor.AssemblyReloadEvents.beforeAssemblyReload -= Dispose;
            UnityEditor.EditorApplication.quitting -= Dispose;
#endif
            ResetMeasurement();
        }

        void ResetMeasurement()
        {
            generation++;
            var previous = resources;
            resources = null;
            sourceCamera = null;
            pipelineAsset = null;
            hasMeasurement = false;
            measuredMultiplier = 0f;
            acceptedRequestSequence = 0;
            failure = null;
            lastRenderFrame = long.MinValue;
            previous?.Retire();
        }

        bool SetFailure(string message, out string status)
        {
            hasMeasurement = false;
            failure = message;
            status = message;
            return false;
        }

        void RenderAndRequestSample(Camera source, HDAdditionalCameraData sourceData, QuiltSettings settings)
        {
            var current = resources;
            Camera camera = current.Camera;
            camera.CopyFrom(source);
            camera.enabled = false;
            camera.transform.SetPositionAndRotation(source.transform.position, source.transform.rotation);
            camera.ResetWorldToCameraMatrix();
            camera.ResetCullingMatrix();
            camera.targetTexture = current.RenderTarget;
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.aspect = settings.viewAspect;
            camera.fieldOfView = settings.fieldOfView;
            camera.allowDynamicResolution = false;
            camera.projectionMatrix = QuiltMath.OffAxisProjection(settings.fieldOfView, settings.viewAspect,
                source.nearClipPlane, source.farClipPlane, 0f, settings.cameraDistance);

            sourceData.CopyTo(current.CameraData);
            current.CameraData.hasPersistentHistory = true;
            current.CameraData.allowDynamicResolution = false;
            current.CameraData.xrRendering = false;
            // Enabling our pass must not activate unused, serialized source overrides.
            if (!sourceData.customRenderingSettings)
                current.CameraData.renderingPathCustomFrameSettingsOverrideMask = default;
            current.CameraData.customRenderingSettings = true;
            current.CameraData.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.CustomPass, true);
            current.CameraData.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.CustomPass] = true;

            current.Pass.Executed = false;
            current.Pass.ExposureEnabled = false;
            var request = new RenderPipeline.StandardRequest { destination = current.RenderTarget };
            if (!RenderPipeline.SupportsRenderRequest(camera, request))
                throw new NotSupportedException("The active HDRP pipeline does not support standard camera render requests.");

            bool sampleQueued = false;
            current.MarkSubmitted();
            try
            {
                // The full tile is rendered for metering; the 1x1 target only copies HDRP's
                // multiplier. Mark ownership before a render that can partially submit and fail.
                RenderPipeline.SubmitRenderRequest(camera, request);
                if (!current.Pass.Executed)
                    throw new InvalidOperationException("The central camera exposure Custom Pass did not execute.");
                if (!current.Pass.ExposureEnabled)
                    throw new InvalidOperationException("Exposure Control is disabled for the central HDRP camera.");

                bool acceptSample = current.RenderCount > 0 && current.PreviousMode == current.Pass.ExposureMode;
                if (!acceptSample) hasMeasurement = false;
                current.PreviousMode = current.Pass.ExposureMode;
                current.RenderCount++;
                long requestGeneration = generation;
                long sequence = ++nextRequestSequence;
                // This request is queued after the complete camera render. Even discarded warmup
                // samples retain both targets until the GPU callback has completed.
                current.PendingReadbacks++;
                try
                {
                    AsyncGPUReadback.Request(current.SampleTarget, 0,
                        result => CompleteSample(current, requestGeneration, sequence, acceptSample, result));
                    sampleQueued = true;
                }
                catch
                {
                    current.CompleteReadback();
                    throw;
                }
            }
            finally
            {
                // A missing pass, disabled exposure, failed render, or rejected sample request
                // still needs ordered GPU proof. Retire queues its own target readbacks, whose
                // serialized ownership also survives a reload if their callbacks are lost.
                if (!sampleQueued) current.Retire();
            }
        }

        void CompleteSample(MeterResources sampledResources, long sampledGeneration, long sequence,
            bool acceptSample, AsyncGPUReadbackRequest request)
        {
            try
            {
                if (disposed || failure != null || sampledGeneration != generation || sampledResources != resources ||
                    sourceCamera == null || sequence <= acceptedRequestSequence) return;
                if (request.hasError)
                {
                    hasMeasurement = false;
                    failure = "HDRP central camera exposure GPU readback failed.";
                    return;
                }
                if (!acceptSample) return;
                var data = request.GetData<float>();
                if (data.Length != 1 || !TryConvertMultiplierToEffectiveEV(data[0], out _))
                {
                    hasMeasurement = false;
                    failure = "HDRP central camera returned an invalid exposure multiplier.";
                    return;
                }
                measuredMultiplier = data[0];
                acceptedRequestSequence = sequence;
                hasMeasurement = true;
            }
            catch (Exception exception)
            {
                if (!disposed && sampledGeneration == generation && sampledResources == resources)
                {
                    hasMeasurement = false;
                    failure = "HDRP exposure GPU sample could not be read: " + exception.Message;
                }
            }
            finally
            {
                sampledResources.CompleteReadback();
            }
        }

        static bool IsPositiveFinite(float value) => value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);

        long CurrentFrame
        {
            get
            {
#if UNITY_EDITOR
                if (!Application.isPlaying) return editorUpdate;
#endif
                return Time.frameCount;
            }
        }

#if UNITY_EDITOR
        void AdvanceEditorFrame() { editorUpdate++; }
#endif

        sealed class MeterResources
        {
            public readonly GameObject CameraObject;
            public readonly Camera Camera;
            public readonly HDAdditionalCameraData CameraData;
            public readonly CustomPassVolume Volume;
            public readonly ExposureSamplePass Pass;
            public readonly Material SampleMaterial;
            public readonly RenderTexture RenderTarget;
            public readonly RenderTexture SampleTarget;
            readonly HdrpExposureRetirementRecord retirement;
            public int PendingReadbacks;
            public int RenderCount;
            public ExposureMode PreviousMode;
            bool retired;
            bool released;

            public MeterResources(int width, int height)
            {
                try
                {
                    Shader shader = Resources.Load<Shader>(ShaderResourceName);
                    if (shader == null || !shader.isSupported)
                        throw new NotSupportedException("The HDRP exposure sampling shader is unavailable or unsupported.");
                    SampleMaterial = new Material(shader) { name = "ViewWeave HDRP Exposure Sample", hideFlags = HideFlags.HideAndDontSave };
                    RenderTarget = CreateTarget(width, height, GraphicsFormat.R16G16B16A16_SFloat, "ViewWeave HDRP Exposure Camera");
                    SampleTarget = CreateTarget(1, 1, GraphicsFormat.R32_SFloat, "ViewWeave HDRP Exposure Sample");
                    CameraObject = new GameObject("ViewWeave HDRP Exposure Meter") { hideFlags = HideFlags.HideAndDontSave };
                    Camera = CameraObject.AddComponent<Camera>();
                    Camera.enabled = false;
                    CameraData = CameraObject.AddComponent<HDAdditionalCameraData>();
                    CameraData.hasPersistentHistory = true;
                    Pass = new ExposureSamplePass(this)
                    {
                        name = "ViewWeave HDRP Exposure Sample",
                        targetColorBuffer = CustomPass.TargetBuffer.None,
                        targetDepthBuffer = CustomPass.TargetBuffer.None
                    };
                    Volume = CameraObject.AddComponent<CustomPassVolume>();
                    Volume.isGlobal = true;
                    Volume.targetCamera = Camera;
                    Volume.injectionPoint = CustomPassInjectionPoint.BeforePostProcess;
                    Volume.customPasses.Add(Pass);
                    retirement = HdrpExposureRetirementRecord.Create(CameraObject, SampleMaterial, RenderTarget, SampleTarget);
                }
                catch
                {
                    Release();
                    throw;
                }
            }

            public void MarkSubmitted() { retirement.MarkSubmitted(); }

            public void Retire()
            {
                retired = true;
                if (Pass != null) Pass.enabled = false;
                if (Volume != null) Volume.enabled = false;
                if (Camera != null) Camera.enabled = false;
                // Begin independent retirement proof even if the measurement callback has not
                // returned. Its ordered readbacks also cover that earlier measurement request.
                if (retirement != null) retirement.Retire();
                if (PendingReadbacks == 0) Release();
            }

            public void CompleteReadback()
            {
                PendingReadbacks--;
                if (retired && PendingReadbacks == 0) Release();
            }

            void Release()
            {
                if (released) return;
                released = true;
                if (!ReferenceEquals(retirement, null))
                {
                    if (retirement != null) retirement.Retire();
                    return;
                }
                // Construction failed before any GPU work or ownership record existed.
                if (Camera != null) Camera.targetTexture = null;
                CoreUtils.Destroy(CameraObject);
                CoreUtils.Destroy(SampleMaterial);
                if (RenderTarget != null) { RenderTarget.Release(); CoreUtils.Destroy(RenderTarget); }
                if (SampleTarget != null) { SampleTarget.Release(); CoreUtils.Destroy(SampleTarget); }
            }

            static RenderTexture CreateTarget(int width, int height, GraphicsFormat format, string name)
            {
                var descriptor = new RenderTextureDescriptor(width, height)
                {
                    graphicsFormat = format,
                    depthBufferBits = 0,
                    msaaSamples = 1,
                    useMipMap = false,
                    autoGenerateMips = false,
                    useDynamicScale = false
                };
                var target = new RenderTexture(descriptor)
                {
                    name = name,
                    hideFlags = HideFlags.HideAndDontSave,
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                if (target.Create()) return target;
                CoreUtils.Destroy(target);
                throw new NotSupportedException("The HDRP exposure render texture could not be created.");
            }
        }

        [Serializable]
        sealed class ExposureSamplePass : CustomPass
        {
            [NonSerialized] readonly MeterResources resources;
            public bool Executed { get; set; }
            public bool ExposureEnabled { get; set; }
            public ExposureMode ExposureMode { get; private set; }

            public ExposureSamplePass(MeterResources resources) { this.resources = resources; }

            protected override bool executeInSceneView => false;

            protected override void Execute(CustomPassContext context)
            {
                if (resources == null || context.hdCamera.camera != resources.Camera) return;
                Executed = true;
                ExposureEnabled = context.hdCamera.frameSettings.IsEnabled(FrameSettingsField.ExposureControl);
                if (!ExposureEnabled) return;
                var exposure = context.hdCamera.volumeStack.GetComponent<Exposure>();
                ExposureMode = exposure.mode.value;
                context.cmd.SetRenderTarget(resources.SampleTarget);
                context.cmd.SetViewport(new Rect(0f, 0f, 1f, 1f));
                context.cmd.DrawProcedural(Matrix4x4.identity, resources.SampleMaterial, 0, MeshTopology.Triangles, 3);
                // HDRP restores the camera target and viewport when this Custom Pass returns.
            }
        }
    }
}
