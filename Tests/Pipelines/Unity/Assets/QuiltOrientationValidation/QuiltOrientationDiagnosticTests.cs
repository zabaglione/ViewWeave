#if UNITY_INCLUDE_TESTS && UNITY_EDITOR_WIN
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.TestTools;
using ViewWeave;
using ViewWeave.URP;
using ViewWeave.HDRP;

namespace ViewWeave.QuiltOrientationValidation
{
    public sealed class QuiltOrientationDiagnosticTests
    {
        sealed class ViewIdSource : MonoBehaviour
        {
            public int Count;
            Camera target;
            MonoBehaviour renderGuard;
            bool srp;
            public bool Capturing;
            public MonoBehaviour RenderGuard { set { renderGuard = value; } }
            void OnEnable()
            {
                target = GetComponent<Camera>();
                srp = GraphicsSettings.currentRenderPipeline != null;
                if (srp) RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
            }
            void OnDisable()
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
            }
            public void OnPreRender()
            {
                if (!srp && Capturing) SetViewId();
            }
            void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
            {
                if (!srp || !Capturing || camera == null || camera.targetTexture == null || !IsGuardViewCamera(camera)) return;
                if (camera.targetTexture.width == QuiltOrientationDiagnosticTests.Width / QuiltOrientationDiagnosticTests.Columns &&
                    camera.targetTexture.height == QuiltOrientationDiagnosticTests.Height / QuiltOrientationDiagnosticTests.Rows)
                    SetViewId();
            }
            bool IsGuardViewCamera(Camera camera)
            {
                if (renderGuard is UrpQuiltCamera urp)
                {
                    for (int view = 0; view < QuiltOrientationDiagnosticTests.Views; view++)
                        if (urp.GetHistoryCamera(view) == camera) return true;
                }
                else if (renderGuard is HdrpQuiltCamera hdrp)
                {
                    for (int view = 0; view < QuiltOrientationDiagnosticTests.Views; view++)
                        if (hdrp.GetHistoryCamera(view) == camera) return true;
                }
                return false;
            }
            void SetViewId()
            {
                Shader.SetGlobalFloat("_OrientationViewId", Count);
                Count++;
            }
        }

        [Serializable]
        sealed class Sample
        {
            public int returnedRowIndex;
            public int rawColumnLeftToRight;
            public int centerR;
            public int centerG;
            public int centerB;
            public int centerA;
            public int byteLowYR;
            public int byteLowYG;
            public int byteLowYB;
            public int byteHighYR;
            public int byteHighYG;
            public int byteHighYB;
            public int rawLeftG;
            public int rawRightG;
        }

        [Serializable]
        sealed class CaptureSamples
        {
            public string capture;
            public Sample[] tiles;
        }

        [Serializable]
        sealed class CaptureRecord
        {
            public string graphicsApi;
            public string graphicsDeviceVersion;
            public string renderPipeline;
            public bool graphicsUvStartsAtTop;
            public string captureFormat;
            public string sentinelLegend = "red=logical UV top; blue=logical UV bottom; green=per-view code with a small right-half marker; alpha=opaque";
            public string viewIdEncoding = "green base=(renderIndex+1)/9; right half adds 0.03 before render-target encoding";
            public string readbackRowOrder = "Unity AsyncGPUReadback RGBA32 byte order; index row 0 is recorded as returned";
            public string sampleCoordinateDefinition = "byteLowY samples use returned byte-array y=tileY+tileHeight/4; byteHighY samples use y=tileY+3*tileHeight/4; physical display top/bottom is not inferred";
            public int width;
            public int height;
            public int columns;
            public int rows;
            public int viewCount;
            public int renderCallbackCount;
            public int requestedMsaaSamples;
            public int effectiveViewAntiAliasing;
            public bool resolveTextureCreated;
            public string[] viewRenderOrder;
            public FileRecord[] files;
            public CaptureSamples[] samples;
        }

        [Serializable]
        sealed class FileRecord
        {
            public string name;
            public string sha256;
            public int byteCount;
        }

        const int Width = 320;
        const int Height = 160;
        const int Columns = 4;
        const int Rows = 2;
        const int Views = 8;

        [UnityTest]
        public IEnumerator BuiltinCapturesActualQuiltAndBothTransferOrientations()
        {
            return Capture("BuiltIn");
        }

        [UnityTest]
        public IEnumerator UrpRequestCapturesActualQuiltAndBothTransferOrientations()
        {
            return Capture("URP");
        }

        [UnityTest]
        public IEnumerator HdrpRequestCapturesActualQuiltAndBothTransferOrientations()
        {
            return Capture("HDRP");
        }

        IEnumerator Capture(string pipelineKind)
        {
            bool useUrp = pipelineKind == "URP";
            bool useHdrp = pipelineKind == "HDRP";
            Assert.IsTrue(pipelineKind == "BuiltIn" || useUrp || useHdrp, "unsupported orientation test pipeline");
            string evidence = Environment.GetEnvironmentVariable("LKG_ORIENTATION_EVIDENCE_DIR");
            Assert.IsFalse(string.IsNullOrWhiteSpace(evidence), "LKG_ORIENTATION_EVIDENCE_DIR must identify a unique evidence folder.");
            Directory.CreateDirectory(evidence);

            GameObject rig = null;
            GameObject surface = null;
            Material material = null;
            RenderTexture currentFlip = null;
            RenderTexture noFlip = null;
            RenderTexture productionCopy = null;
            RenderPipelineAsset previousDefault = GraphicsSettings.defaultRenderPipeline;
            RenderPipelineAsset previousQuality = QualitySettings.renderPipeline;
            UniversalRendererData urpData = null;
            UniversalRenderPipelineAsset urpAsset = null;
            HDRenderPipelineAsset hdrpAsset = null;
            int requestedMsaa = ReadRequestedMsaaSamples();
            try
            {
                if (useUrp)
                {
                    urpData = ScriptableObject.CreateInstance<UniversalRendererData>();
                    urpData.postProcessData = UnityEditor.AssetDatabase.LoadAssetAtPath<PostProcessData>("Packages/com.unity.render-pipelines.universal/Runtime/Data/PostProcessData.asset");
                    Assert.IsNotNull(urpData.postProcessData, "URP package PostProcessData must be available");
                    urpAsset = UniversalRenderPipelineAsset.Create(urpData);
                    urpAsset.supportsHDR = true;
                    urpAsset.msaaSampleCount = requestedMsaa;
                    GraphicsSettings.defaultRenderPipeline = urpAsset;
                    QualitySettings.renderPipeline = urpAsset;
                }
                else if (useHdrp)
                {
                    hdrpAsset = ScriptableObject.CreateInstance<HDRenderPipelineAsset>();
                    GraphicsSettings.defaultRenderPipeline = hdrpAsset;
                    QualitySettings.renderPipeline = hdrpAsset;
                }

                Shader shader = Shader.Find("Hidden/ViewWeave/QuiltOrientationDiagnostic");
                Assert.IsNotNull(shader, "orientation diagnostic shader must be imported");
                material = new Material(shader);
                surface = GameObject.CreatePrimitive(PrimitiveType.Quad);
                surface.name = "QuiltOrientationSentinelSurface";
                surface.transform.position = new Vector3(0f, 0f, 2f);
                surface.transform.localScale = new Vector3(5f, 5f, 1f);
                Renderer surfaceRenderer = surface.GetComponent<Renderer>();
                surfaceRenderer.sharedMaterial = material;

                rig = new GameObject("QuiltOrientationDiagnosticRig");
                Camera camera = rig.AddComponent<Camera>();
                camera.enabled = false;
                camera.allowMSAA = requestedMsaa > 1;
                camera.orthographic = false;
                camera.fieldOfView = 50f;
                camera.nearClipPlane = 0.01f;
                camera.farClipPlane = 20f;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                if (useUrp)
                {
                    UniversalAdditionalCameraData cameraData = rig.AddComponent<UniversalAdditionalCameraData>();
                    cameraData.renderType = CameraRenderType.Base;
                    cameraData.renderPostProcessing = false;
                }
                else if (useHdrp)
                {
                    HDAdditionalCameraData cameraData = rig.AddComponent<HDAdditionalCameraData>();
                    cameraData.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color;
                    cameraData.backgroundColorHDR = Color.black;
                }
                ViewIdSource viewIds = rig.AddComponent<ViewIdSource>();

                QuiltRenderer renderer = rig.AddComponent<QuiltRenderer>();
                renderer.sourceCamera = camera;
                renderer.autoRender = false;
                renderer.viewRenderMode = QuiltViewRenderMode.Full;
                renderer.useRenderRequests = true;
                renderer.physicalFraming = new QuiltPhysicalFraming();
                if (useUrp) renderer.GuardBehaviour = rig.AddComponent<UrpQuiltCamera>();
                else if (useHdrp) renderer.GuardBehaviour = rig.AddComponent<HdrpQuiltCamera>();
                viewIds.RenderGuard = renderer.GuardBehaviour;
                renderer.settings = new QuiltSettings
                {
                    quiltWidth = Width,
                    quiltHeight = Height,
                    columns = Columns,
                    rows = Rows,
                    views = Views,
                    viewAspect = 1f,
                    fieldOfView = camera.fieldOfView,
                    cameraDistance = 1f,
                    viewCone = 40f
                };

                viewIds.Capturing = true;
                if (useHdrp)
                {
                    float deadline = Time.realtimeSinceStartup + 15f;
                    while (!renderer.HasRenderedQuilt && Time.realtimeSinceStartup <= deadline)
                    {
                        renderer.RenderQuilt();
                        if (!renderer.HasRenderedQuilt) yield return null;
                    }
                }
                else
                {
                    renderer.RenderQuilt();
                }
                viewIds.Capturing = false;
                HdrpQuiltCamera hdrpGuard = useHdrp ? renderer.GuardBehaviour as HdrpQuiltCamera : null;
                Assert.IsTrue(renderer.HasRenderedQuilt, "the actual QuiltRenderer render must complete; status=" + renderer.LastRenderStatus + " exposureReady=" + (hdrpGuard != null && hdrpGuard.SharedExposureReady) + " historyStatus=" + (hdrpGuard == null ? string.Empty : hdrpGuard.HistoryStatus));
                Assert.AreEqual(Views, renderer.LastRenderedViewCount, "every logical view must be rendered");
                Assert.AreEqual(Views, viewIds.Count, "per-view sentinel must advance once per source camera render");
                Assert.IsNotNull(renderer.QuiltTexture);
                Assert.AreEqual(Width, renderer.QuiltTexture.width);
                Assert.AreEqual(Height, renderer.QuiltTexture.height);
                Assert.IsTrue(renderer.QuiltTexture.IsCreated());
                RenderTexture viewTexture = GetPrivateTexture(renderer, "viewTexture");
                RenderTexture resolveTexture = GetPrivateTexture(renderer, "resolveTexture");
                Assert.IsNotNull(viewTexture, "QuiltRenderer must retain its actual per-view render target");
                Assert.AreEqual(requestedMsaa, viewTexture.antiAliasing, "actual per-view RenderTexture MSAA must match the requested mode");
                if (requestedMsaa > 1)
                    Assert.IsTrue(resolveTexture != null && resolveTexture.IsCreated(), "MSAA views must use the actual created resolve texture");
                else
                    Assert.IsTrue(resolveTexture == null || !resolveTexture.IsCreated(), "single-sample views must not retain an MSAA resolve texture");

                byte[] sourceBytes = ReadRgba(renderer.QuiltTexture);
                CaptureSamples sourceSamples = new CaptureSamples { capture = "source-quilt", tiles = SampleGrid(sourceBytes) };
                currentFlip = CreateTransfer(renderer.QuiltTexture, true);
                noFlip = CreateTransfer(renderer.QuiltTexture, false);
                productionCopy = CreateProductionTransfer(renderer.QuiltTexture);
                byte[] currentBytes = ReadRgba(currentFlip);
                byte[] noFlipBytes = ReadRgba(noFlip);
                byte[] productionBytes = ReadRgba(productionCopy);
                CaptureSamples flippedSamples = new CaptureSamples { capture = "transfer-current-flip", tiles = SampleGrid(currentBytes) };
                CaptureSamples unflippedSamples = new CaptureSamples { capture = "transfer-no-flip", tiles = SampleGrid(noFlipBytes) };
                CaptureSamples productionSamples = new CaptureSamples { capture = "transfer-production-copy", tiles = SampleGrid(productionBytes) };
                AssertSentinels(sourceSamples.tiles, "source quilt");
                AssertSentinels(flippedSamples.tiles, "current flip transfer");
                AssertSentinels(unflippedSamples.tiles, "no-flip transfer");
                AssertSentinels(productionSamples.tiles, "production copy transfer");
                CollectionAssert.AreEqual(sourceBytes, productionBytes, "production transfer helper must preserve the actual QuiltRenderer storage byte-for-byte");

                string[] names = { "source-quilt.rgba8", "transfer-current-flip.rgba8", "transfer-no-flip.rgba8", "transfer-production-copy.rgba8" };
                byte[][] payloads = { sourceBytes, currentBytes, noFlipBytes, productionBytes };
                var files = new FileRecord[payloads.Length];
                for (int index = 0; index < payloads.Length; index++)
                {
                    string path = Path.Combine(evidence, names[index]);
                    File.WriteAllBytes(path, payloads[index]);
                    files[index] = new FileRecord { name = names[index], sha256 = Sha256(payloads[index]), byteCount = payloads[index].Length };
                }

                var record = new CaptureRecord
                {
                    graphicsApi = SystemInfo.graphicsDeviceType.ToString(),
                    graphicsDeviceVersion = SystemInfo.graphicsDeviceVersion,
                    renderPipeline = GraphicsSettings.currentRenderPipeline == null ? "BuiltIn" : GraphicsSettings.currentRenderPipeline.GetType().FullName,
                    graphicsUvStartsAtTop = SystemInfo.graphicsUVStartsAtTop,
                    captureFormat = renderer.QuiltTexture.graphicsFormat.ToString(),
                    width = Width,
                    height = Height,
                    columns = Columns,
                    rows = Rows,
                    viewCount = Views,
                    renderCallbackCount = viewIds.Count,
                    requestedMsaaSamples = requestedMsaa,
                    effectiveViewAntiAliasing = viewTexture.antiAliasing,
                    resolveTextureCreated = resolveTexture != null && resolveTexture.IsCreated(),
                    viewRenderOrder = new[] { GraphicsSettings.currentRenderPipeline == null ? "render-index increments in sourceCamera.OnPreRender" : "render-index increments in beginCameraRendering for each rendered view camera target" },
                    files = files,
                    samples = new[] { sourceSamples, flippedSamples, unflippedSamples, productionSamples }
                };
                File.WriteAllText(Path.Combine(evidence, "producer-orientation.json"), JsonUtility.ToJson(record, true) + "\n", new UTF8Encoding(false));
                Debug.Log("ORIENTATION_CAPTURE_OK api=" + record.graphicsApi + " size=" + Width + "x" + Height + " grid=" + Columns + "x" + Rows + " views=" + Views + " callbacks=" + viewIds.Count);
            }
            finally
            {
                if (rig != null) UnityEngine.Object.Destroy(rig);
                if (surface != null) UnityEngine.Object.Destroy(surface);
                if (material != null) UnityEngine.Object.Destroy(material);
                if (currentFlip != null) { if (RenderTexture.active == currentFlip) RenderTexture.active = null; currentFlip.Release(); UnityEngine.Object.Destroy(currentFlip); }
                if (noFlip != null) { if (RenderTexture.active == noFlip) RenderTexture.active = null; noFlip.Release(); UnityEngine.Object.Destroy(noFlip); }
                if (productionCopy != null) { if (RenderTexture.active == productionCopy) RenderTexture.active = null; productionCopy.Release(); UnityEngine.Object.Destroy(productionCopy); }
                GraphicsSettings.defaultRenderPipeline = previousDefault;
                QualitySettings.renderPipeline = previousQuality;
                if (urpAsset != null) UnityEngine.Object.Destroy(urpAsset);
                if (urpData != null) UnityEngine.Object.Destroy(urpData);
                if (hdrpAsset != null) UnityEngine.Object.Destroy(hdrpAsset);
            }
            yield return null;
        }

        static RenderTexture CreateTransfer(RenderTexture source, bool flip)
        {
            var descriptor = new RenderTextureDescriptor(source.width, source.height, source.graphicsFormat, 0)
            {
                msaaSamples = 1,
                mipCount = 1,
                volumeDepth = 1,
                dimension = TextureDimension.Tex2D,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = source.sRGB
            };
            var result = new RenderTexture(descriptor)
            {
                name = flip ? "QuiltOrientationCurrentFlip" : "QuiltOrientationNoFlip",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            Assert.IsTrue(result.Create());
            if (flip) Graphics.Blit(source, result, new Vector2(1f, -1f), new Vector2(0f, 1f));
            else Graphics.Blit(source, result, new Vector2(1f, 1f), new Vector2(0f, 0f));
            return result;
        }

        static RenderTexture CreateProductionTransfer(RenderTexture source)
        {
            var descriptor = new RenderTextureDescriptor(source.width, source.height, source.graphicsFormat, 0)
            {
                msaaSamples = 1,
                mipCount = 1,
                volumeDepth = 1,
                dimension = TextureDimension.Tex2D,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = source.sRGB
            };
            var result = new RenderTexture(descriptor)
            {
                name = "QuiltOrientationProductionTransfer",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            Assert.IsTrue(result.Create());
            MethodInfo copy = typeof(BridgeWorkerQuiltOutput).GetMethod("CopyQuiltToTransferTexture", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.IsNotNull(copy, "the Windows worker production transfer helper must exist");
            copy.Invoke(null, new object[] { source, result });
            return result;
        }

        static RenderTexture GetPrivateTexture(QuiltRenderer renderer, string fieldName)
        {
            FieldInfo field = typeof(QuiltRenderer).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "QuiltRenderer private texture field must exist: " + fieldName);
            return field.GetValue(renderer) as RenderTexture;
        }

        static int ReadRequestedMsaaSamples()
        {
            string value = Environment.GetEnvironmentVariable("LKG_ORIENTATION_MSAA");
            if (string.IsNullOrWhiteSpace(value)) return 1;
            int samples;
            Assert.IsTrue(int.TryParse(value, out samples) && (samples == 1 || samples == 4), "LKG_ORIENTATION_MSAA must be 1 or 4");
            return samples;
        }

        static byte[] ReadRgba(RenderTexture source)
        {
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(source, 0, TextureFormat.RGBA32);
            while (!request.done) { request.WaitForCompletion(); }
            Assert.IsFalse(request.hasError, "AsyncGPUReadback of rendered quilt/transfer must succeed");
            NativeArray<byte> data = request.GetData<byte>();
            byte[] output = new byte[data.Length];
            data.CopyTo(output);
            Assert.AreEqual(source.width * source.height * 4, output.Length);
            return output;
        }

        static Sample[] SampleGrid(byte[] data)
        {
            int tileWidth = Width / Columns;
            int tileHeight = Height / Rows;
            var result = new Sample[Rows * Columns];
            int index = 0;
            for (int rawRow = 0; rawRow < Rows; rawRow++)
            {
                for (int column = 0; column < Columns; column++)
                {
                    int left = column * tileWidth;
                    int top = rawRow * tileHeight;
                    result[index++] = new Sample
                    {
                        returnedRowIndex = rawRow,
                        rawColumnLeftToRight = column,
                        centerR = Channel(data, left + tileWidth / 2, top + tileHeight / 2, 0),
                        centerG = Channel(data, left + tileWidth / 2, top + tileHeight / 2, 1),
                        centerB = Channel(data, left + tileWidth / 2, top + tileHeight / 2, 2),
                        centerA = Channel(data, left + tileWidth / 2, top + tileHeight / 2, 3),
                        byteLowYR = Channel(data, left + tileWidth / 2, top + tileHeight / 4, 0),
                        byteLowYG = Channel(data, left + tileWidth / 2, top + tileHeight / 4, 1),
                        byteLowYB = Channel(data, left + tileWidth / 2, top + tileHeight / 4, 2),
                        byteHighYR = Channel(data, left + tileWidth / 2, top + tileHeight * 3 / 4, 0),
                        byteHighYG = Channel(data, left + tileWidth / 2, top + tileHeight * 3 / 4, 1),
                        byteHighYB = Channel(data, left + tileWidth / 2, top + tileHeight * 3 / 4, 2),
                        rawLeftG = Channel(data, left + tileWidth / 4, top + tileHeight / 2, 1),
                        rawRightG = Channel(data, left + tileWidth * 3 / 4, top + tileHeight / 2, 1)
                    };
                }
            }
            return result;
        }

        static void AssertSentinels(Sample[] samples, string label)
        {
            Assert.AreEqual(Rows * Columns, samples.Length, label + " sample count");
            var viewCodes = new System.Collections.Generic.HashSet<int>();
            for (int index = 0; index < samples.Length; index++)
            {
                Sample sample = samples[index];
                viewCodes.Add(sample.centerG);
                Assert.AreNotEqual(sample.byteLowYR, sample.byteHighYR, label + " low/high byte-Y red sentinel for storage tile " + index);
                Assert.AreNotEqual(sample.byteLowYB, sample.byteHighYB, label + " low/high byte-Y blue sentinel for storage tile " + index);
                Assert.AreNotEqual(sample.rawLeftG, sample.rawRightG, label + " left/right green sentinel for storage tile " + index);
                Assert.AreEqual(255, sample.centerA, label + " opaque sentinel alpha for storage tile " + index);
            }
            Assert.AreEqual(Rows * Columns, viewCodes.Count, label + " all tile centers must contain unique view IDs");
        }

        static int Channel(byte[] data, int x, int y, int channel) => data[(y * Width + x) * 4 + channel];

        static string Sha256(byte[] data)
        {
            using (SHA256 hash = SHA256.Create())
            {
                byte[] digest = hash.ComputeHash(data);
                var result = new StringBuilder(digest.Length * 2);
                for (int index = 0; index < digest.Length; index++) result.Append(digest[index].ToString("x2"));
                return result.ToString();
            }
        }
    }
}
#endif
