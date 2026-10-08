#if UNITY_EDITOR
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace ViewWeave
{
    [InitializeOnLoad]
    internal static class QuiltRendererEditorCallbackTracker
    {
        static int contextDepth;
        static int cameraDepth;
        static bool registered;
        static bool inconsistent;

        static QuiltRendererEditorCallbackTracker()
        {
            Register();
        }

        internal static bool IsReady => registered && !inconsistent;
        internal static bool IsInsideRenderCallback => !IsReady || contextDepth > 0 || cameraDepth > 0;
        internal static int ContextDepth => contextDepth;
        internal static int CameraDepth => cameraDepth;

        static void Register()
        {
            if (registered) return;
            RenderPipelineManager.beginContextRendering -= OnBeginContextRendering;
            RenderPipelineManager.endContextRendering -= OnEndContextRendering;
            RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
            RenderPipelineManager.beginContextRendering += OnBeginContextRendering;
            RenderPipelineManager.endContextRendering += OnEndContextRendering;
            RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;
            registered = true;
        }

        internal static void OnBeginContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            Increment(ref contextDepth);
        }

        internal static void OnEndContextRendering(ScriptableRenderContext context, List<Camera> cameras)
        {
            Decrement(ref contextDepth);
        }

        internal static void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            Increment(ref cameraDepth);
        }

        internal static void OnEndCameraRendering(ScriptableRenderContext context, Camera camera)
        {
            Decrement(ref cameraDepth);
        }

        static void Increment(ref int depth)
        {
            if (depth == int.MaxValue)
            {
                inconsistent = true;
                return;
            }
            depth++;
        }

        static void Decrement(ref int depth)
        {
            if (depth <= 0)
            {
                inconsistent = true;
                depth = 0;
                return;
            }
            depth--;
        }
    }
}
#endif
