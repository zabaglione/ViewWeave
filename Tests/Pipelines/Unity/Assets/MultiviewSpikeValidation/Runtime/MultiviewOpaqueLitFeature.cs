using System;
using LookingGlass.Community;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

namespace LookingGlass.MultiviewSpikeValidation
{
    public sealed class MultiviewOpaqueLitFeature : ScriptableRendererFeature
    {
        public RenderTexture atlasTarget;
        public int views = 8;
        public int columns = 4;
        public int rows = 2;
        public float viewCone = 30f;
        public float cameraDistance = 3f;
        public LayerMask opaqueLayerMask = -1;
        public int ExecutionCount { get; private set; }
        public void ResetExecutionCount() { ExecutionCount = 0; }
        internal void RecordExecution() { ExecutionCount++; }
        MultiviewOpaqueLitPass pass;

        public override void Create()
        {
            pass = new MultiviewOpaqueLitPass(this) { renderPassEvent = RenderPassEvent.AfterRenderingOpaques };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (pass != null && atlasTarget != null) renderer.EnqueuePass(pass);
        }

        protected override void Dispose(bool disposing)
        {
            if (pass != null) pass.Dispose();
            pass = null;
        }
    }

    sealed class MultiviewOpaqueLitPass : ScriptableRenderPass
    {
        static readonly int WorldSpaceCameraPosId = Shader.PropertyToID("_WorldSpaceCameraPos");
        sealed class PassData
        {
            public RendererListHandle[] rendererLists;
            public MultiviewOpaqueLitFeature owner;
            public TextureHandle target;
            public Matrix4x4 restoreView;
            public Matrix4x4 restoreProjection;
            public Vector3 basePosition;
            public Quaternion baseRotation;
            public Camera camera;
            public int views;
            public int columns;
            public int rows;
            public int tileWidth;
            public int tileHeight;
            public float viewCone;
            public float cameraDistance;
            public float fieldOfView;
            public float aspect;
            public float nearClip;
            public float farClip;
        }

        readonly MultiviewOpaqueLitFeature owner;
        RTHandle atlasHandle;
        public MultiviewOpaqueLitPass(MultiviewOpaqueLitFeature owner) { this.owner = owner; }

        public void Dispose()
        {
            if (atlasHandle != null) atlasHandle.Release();
            atlasHandle = null;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (owner.atlasTarget == null) return;
            UniversalRenderingData renderingData = frameData.Get<UniversalRenderingData>();
            UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();
            Camera camera = cameraData.camera;
            if (camera == null || owner.views < 1 || owner.columns < 1 || owner.rows < 1) return;
            if (atlasHandle == null || atlasHandle.rt != owner.atlasTarget)
            {
                if (atlasHandle != null) atlasHandle.Release();
                atlasHandle = RTHandles.Alloc(owner.atlasTarget);
            }
            TextureHandle target = renderGraph.ImportTexture(atlasHandle);
            FilteringSettings filtering = new FilteringSettings(RenderQueueRange.opaque, owner.opaqueLayerMask);
            TextureDesc descriptor = target.GetDescriptor(renderGraph);
            int tileWidth = descriptor.width / owner.columns;
            int tileHeight = descriptor.height / owner.rows;
            TextureDesc depthDescriptor = descriptor;
            depthDescriptor.colorFormat = GraphicsFormat.None;
            depthDescriptor.depthBufferBits = DepthBits.Depth32;
            depthDescriptor.name = "Multiview opaque Lit depth";
            TextureHandle depth = renderGraph.CreateTexture(depthDescriptor);
            using IRasterRenderGraphBuilder builder = renderGraph.AddRasterRenderPass<PassData>("Multiview opaque Lit", out PassData passData);
            passData.rendererLists = new RendererListHandle[owner.views];
            passData.owner = owner;
            passData.target = target;
            passData.restoreView = camera.worldToCameraMatrix;
            passData.restoreProjection = GL.GetGPUProjectionMatrix(camera.projectionMatrix, true);
            passData.basePosition = camera.transform.position;
            passData.baseRotation = camera.transform.rotation;
            passData.camera = camera;
            passData.views = owner.views;
            passData.columns = owner.columns;
            passData.rows = owner.rows;
            passData.tileWidth = tileWidth;
            passData.tileHeight = tileHeight;
            passData.viewCone = owner.viewCone;
            passData.cameraDistance = owner.cameraDistance;
            passData.fieldOfView = camera.fieldOfView;
            passData.aspect = camera.aspect;
            passData.nearClip = camera.nearClipPlane;
            passData.farClip = camera.farClipPlane;
            for (int index = 0; index < owner.views; ++index)
            {
                float offset = QuiltMath.ViewOffset(index, owner.views, owner.viewCone, owner.cameraDistance);
                Vector3 viewPosition = camera.transform.position + camera.transform.rotation * Vector3.right * offset;
                Matrix4x4 view = Matrix4x4.TRS(viewPosition, camera.transform.rotation, Vector3.one).inverse;
                SortingSettings viewSorting = new SortingSettings(camera) { criteria = SortingCriteria.CommonOpaque, worldToCameraMatrix = view, cameraPosition = viewPosition };
                DrawingSettings viewDrawing = new DrawingSettings(new ShaderTagId("UniversalForward"), viewSorting);
                viewDrawing.SetShaderPassName(1, new ShaderTagId("UniversalForwardOnly"));
                viewDrawing.SetShaderPassName(2, new ShaderTagId("SRPDefaultUnlit"));
                RendererListParams viewParams = new RendererListParams(renderingData.cullResults, viewDrawing, filtering);
                passData.rendererLists[index] = renderGraph.CreateRendererList(viewParams);
                builder.UseRendererList(passData.rendererLists[index]);
            }
            builder.SetRenderAttachment(target, 0);
            builder.SetRenderAttachmentDepth(depth);
            builder.AllowGlobalStateModification(true);
            builder.SetRenderFunc(static (PassData data, RasterGraphContext context) => Execute(data, context));
        }

        static void Execute(PassData data, RasterGraphContext context)
        {
            data.owner.RecordExecution();
            context.cmd.ClearRenderTarget(true, true, Color.clear);
            for (int index = 0; index < data.views; ++index)
            {
                float offset = QuiltMath.ViewOffset(index, data.views, data.viewCone, data.cameraDistance);
                Matrix4x4 projection = GL.GetGPUProjectionMatrix(QuiltMath.OffAxisProjection(data.fieldOfView, data.aspect, data.nearClip, data.farClip, offset, data.cameraDistance), true);
                Rect viewport = new Rect((index % data.columns) * data.tileWidth, (index / data.columns) * data.tileHeight, data.tileWidth, data.tileHeight);
                context.cmd.SetViewport(viewport);
                Vector3 viewPosition = data.basePosition + data.baseRotation * Vector3.right * offset;
                Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(viewPosition, data.baseRotation, Vector3.one).inverse;
                context.cmd.SetGlobalVector(WorldSpaceCameraPosId, new Vector4(viewPosition.x, viewPosition.y, viewPosition.z, 1f));
                RenderingUtils.SetViewAndProjectionMatrices(context.cmd, view, projection, true);
                context.cmd.DrawRendererList(data.rendererLists[index]);
            }
            context.cmd.SetGlobalVector(WorldSpaceCameraPosId, new Vector4(data.basePosition.x, data.basePosition.y, data.basePosition.z, 1f));
            RenderingUtils.SetViewAndProjectionMatrices(context.cmd, data.restoreView, data.restoreProjection, true);
            context.cmd.SetViewport(new Rect(0f, 0f, data.tileWidth * data.columns, data.tileHeight * data.rows));
        }
    }
}
