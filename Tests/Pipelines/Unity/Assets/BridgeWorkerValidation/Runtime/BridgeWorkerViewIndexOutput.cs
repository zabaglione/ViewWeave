using System;
using ViewWeave;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace ViewWeave.BridgeWorkerValidation
{
    [DisallowMultipleComponent]
    public sealed class BridgeWorkerViewIndexOutput : MonoBehaviour, IQuiltOutput
    {
        [SerializeField] BridgeWorkerQuiltOutput target;
        [SerializeField] bool viewIndexDebug;
        [SerializeField] Shader indexShader;
        Material indexMaterial;
        RenderTexture indexedQuilt;

        public RenderTexture QuiltTexture { get; private set; }
        public QuiltLayout Layout { get; private set; }
        public RenderTexture IndexedQuilt => indexedQuilt;
        public RenderTexture SubmittedTexture { get; private set; }
        public bool ViewIndexDebug { get => viewIndexDebug; set => viewIndexDebug = value; }

        public void SetTarget(BridgeWorkerQuiltOutput value) => target = value;

        void Awake()
        {
            if (indexShader == null) indexShader = Shader.Find("ViewWeave/BridgeWorkerViewIndexDebug");
            string argument = Argument("--view-index-debug");
            if (argument == "0" || argument == "1") viewIndexDebug = argument == "1";
            if (viewIndexDebug && indexShader == null) throw new InvalidOperationException("view index debug shader is unavailable");
        }

        public void Submit(RenderTexture quiltTexture, QuiltLayout layout)
        {
            QuiltTexture = quiltTexture;
            Layout = layout;
            if (!viewIndexDebug)
            {
                SubmittedTexture = quiltTexture;
                if (target != null) target.Submit(quiltTexture, layout);
                return;
            }
            if (quiltTexture == null) throw new InvalidOperationException("view index debug received a null quilt");
            if (indexShader == null) throw new InvalidOperationException("view index debug shader is unavailable");
            EnsureResources(quiltTexture);
            indexMaterial.SetFloat("_Columns", layout.Columns);
            indexMaterial.SetFloat("_Rows", layout.Rows);
            indexMaterial.SetFloat("_ViewCount", layout.ViewCount);
            indexMaterial.SetFloat("_TileAspect", layout.AspectRatio);
            Graphics.Blit(quiltTexture, indexedQuilt, indexMaterial);
            SubmittedTexture = indexedQuilt;
            if (target != null) target.Submit(indexedQuilt, layout);
        }

        void EnsureResources(RenderTexture source)
        {
            if (indexMaterial == null) indexMaterial = new Material(indexShader) { name = "BridgeWorkerViewIndexDebug.Material" };
            if (indexedQuilt != null && (indexedQuilt.width != source.width || indexedQuilt.height != source.height || indexedQuilt.graphicsFormat != source.graphicsFormat))
            {
                ReleaseTexture();
            }
            if (indexedQuilt == null)
            {
                RenderTextureDescriptor descriptor = source.descriptor;
                descriptor.depthBufferBits = 0;
                descriptor.msaaSamples = 1;
                descriptor.useMipMap = false;
                descriptor.autoGenerateMips = false;
                descriptor.graphicsFormat = source.graphicsFormat;
                indexedQuilt = new RenderTexture(descriptor) { name = "BridgeWorkerViewIndexDebug.Quilt", filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                indexedQuilt.Create();
                if (!indexedQuilt.IsCreated()) throw new InvalidOperationException("view index debug render texture creation failed");
            }
        }

        void OnDisable() { ReleaseTexture(); }
        void OnDestroy()
        {
            ReleaseTexture();
            if (indexMaterial != null) Destroy(indexMaterial);
            indexMaterial = null;
        }

        void ReleaseTexture()
        {
            if (indexedQuilt == null) return;
            if (RenderTexture.active == indexedQuilt) RenderTexture.active = null;
            if (indexedQuilt.IsCreated()) indexedQuilt.Release();
            Destroy(indexedQuilt);
            indexedQuilt = null;
        }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
