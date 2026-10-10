using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using ViewWeave.Editor;

namespace ViewWeave.HDRP.Editor
{
    [InitializeOnLoad]
    static class HdrpViewWeaveRigProviderRegistration
    {
        static HdrpViewWeaveRigProviderRegistration() => ViewWeaveRigProviderRegistry.Register(new Provider());
        sealed class Provider : IViewWeaveRigProvider
        {
            public bool IsSupported => GraphicsSettings.currentRenderPipeline is HDRenderPipelineAsset;
            public MonoBehaviour AddAdapter(GameObject target, Camera framingCamera)
            {
                HDAdditionalCameraData targetData = target.GetComponent<HDAdditionalCameraData>();
                if (targetData == null) targetData = target.AddComponent<HDAdditionalCameraData>();
                HDAdditionalCameraData framingData = framingCamera != null ? framingCamera.GetComponent<HDAdditionalCameraData>() : null;
                if (framingData != null)
                {
                    framingData.CopyTo(targetData);
                }
                return target.GetComponent<HdrpQuiltCamera>() ?? target.AddComponent<HdrpQuiltCamera>();
            }
        }
    }
}
