using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using ViewWeave.Editor;

namespace ViewWeave.URP.Editor
{
    [InitializeOnLoad]
    static class UrpViewWeaveRigProviderRegistration
    {
        static UrpViewWeaveRigProviderRegistration() => ViewWeaveRigProviderRegistry.Register(new Provider());
        sealed class Provider : IViewWeaveRigProvider
        {
            public bool IsSupported => GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset;
            public MonoBehaviour AddAdapter(GameObject target, Camera framingCamera)
            {
                UniversalAdditionalCameraData targetData = target.GetComponent<UniversalAdditionalCameraData>();
                if (targetData == null) targetData = target.AddComponent<UniversalAdditionalCameraData>();
                UniversalAdditionalCameraData framingData = framingCamera != null ? framingCamera.GetComponent<UniversalAdditionalCameraData>() : null;
                if (framingData != null) EditorUtility.CopySerialized(framingData, targetData);
                return target.GetComponent<UrpQuiltCamera>() ?? target.AddComponent<UrpQuiltCamera>();
            }
        }
    }
}
