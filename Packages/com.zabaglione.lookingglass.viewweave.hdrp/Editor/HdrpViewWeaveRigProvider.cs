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
            public MonoBehaviour AddAdapter(GameObject target)
            {
                if (target.GetComponent<HDAdditionalCameraData>() == null) target.AddComponent<HDAdditionalCameraData>();
                return target.GetComponent<HdrpQuiltCamera>() ?? target.AddComponent<HdrpQuiltCamera>();
            }
        }
    }
}
