using System;
using System.Collections;
using System.IO;
using ViewWeave;
using ViewWeave.URP;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace ViewWeave.MultiviewSpikeValidation
{
    public sealed class MultiviewSpikeValidationRunner : MonoBehaviour
    {
        [SerializeField] Camera sourceCamera;
        [SerializeField] QuiltRenderer quilt;
        [SerializeField] MultiviewOpaqueLitFeature feature;
        [SerializeField] RenderTexture atlasTarget;
        [SerializeField] Light keyLight;
        [SerializeField] Renderer edgeRenderer;
        RenderTexture requestTarget;
        BenchmarkRun benchmarkResult;
        Texture2D referenceImage;
        Texture2D featureImage;
        Texture2D dimImage;
        const int Views = 8;
        const int TileWidth = 128;
        const int TileHeight = 96;

        void Awake() { Application.runInBackground = true; }
        void Start() { StartCoroutine(RunGuarded()); }

        IEnumerator RunGuarded()
        {
            IEnumerator iterator = Drive(Run());
            Exception failure = null;
            while (true)
            {
                bool moved;
                object current = null;
                try
                {
                    moved = iterator.MoveNext();
                    if (moved) current = iterator.Current;
                }
                catch (Exception error)
                {
                    failure = error;
                    break;
                }
                if (!moved) break;
                yield return current;
            }
            (iterator as IDisposable)?.Dispose();
            feature.SetActive(false);
            sourceCamera.enabled = false;
            if (requestTarget != null) { requestTarget.Release(); Destroy(requestTarget); requestTarget = null; }
            if (referenceImage != null) { Destroy(referenceImage); referenceImage = null; }
            if (featureImage != null) { Destroy(featureImage); featureImage = null; }
            if (dimImage != null) { Destroy(dimImage); dimImage = null; }
            if (failure != null)
            {
                string directory = Argument("--evidence-dir");
                if (string.IsNullOrEmpty(directory)) directory = Path.Combine(Application.dataPath, "..", "..", ".local", "multiview-spike", Application.buildGUID);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "error.txt"), Sanitize(failure.GetType().Name + ": " + failure.Message));
                Application.Quit(1);
            }
        }

        IEnumerator Drive(IEnumerator iterator)
        {
            try
            {
                while (true)
                {
                    bool moved;
                    object current = null;
                    moved = iterator.MoveNext();
                    if (moved) current = iterator.Current;
                    if (!moved) yield break;
                    IEnumerator nested = current as IEnumerator;
                    if (nested == null) { yield return current; continue; }
                    IEnumerator child = Drive(nested);
                    try { while (child.MoveNext()) yield return child.Current; }
                    finally { (child as IDisposable)?.Dispose(); }
                }
            }
            finally { (iterator as IDisposable)?.Dispose(); }
        }

        IEnumerator Run()
        {
            if (sourceCamera == null || quilt == null || feature == null || atlasTarget == null) throw new InvalidOperationException("multiview fixture references are incomplete");
            if (!atlasTarget.IsCreated()) atlasTarget.Create();
            float oldFieldOfView = sourceCamera.fieldOfView;
            float oldAspect = sourceCamera.aspect;
            requestTarget = new RenderTexture(TileWidth, TileHeight, 24, RenderTextureFormat.ARGB32) { name = "MultiviewSpike.Request" };
            requestTarget.Create();
            quilt.settings.columns = 4;
            quilt.settings.rows = 2;
            quilt.settings.views = Views;
            quilt.settings.quiltWidth = TileWidth * 4;
            quilt.settings.quiltHeight = TileHeight * 2;
            quilt.settings.viewAspect = 128f / 96f;
            quilt.settings.cameraDistance = 3f;
            quilt.settings.viewCone = 30f;
            feature.viewCone = 30f;
            sourceCamera.fieldOfView = 50f;
            sourceCamera.aspect = TileWidth / (float)TileHeight;
            quilt.autoRender = false;
            sourceCamera.enabled = false;
            feature.SetActive(false);
            yield return null;
            quilt.RenderQuilt();
            Texture2D reference = Read(quilt.QuiltTexture);
            referenceImage = reference;
            Matrix4x4 oldCullingMatrix = sourceCamera.cullingMatrix;
            bool oldOcclusion = sourceCamera.useOcclusionCulling;
            try
            {
                SetConservativeCulling(sourceCamera, quilt.settings.viewCone, quilt.settings.cameraDistance);
                sourceCamera.useOcclusionCulling = false;
                sourceCamera.aspect = TileWidth / (float)TileHeight;
                feature.ResetExecutionCount();
                feature.SetActive(true);
                if (!RenderPipeline.SupportsRenderRequest(sourceCamera, new RenderPipeline.StandardRequest { destination = requestTarget })) throw new InvalidOperationException("standard request is unavailable");
                RenderPipeline.SubmitRenderRequest(sourceCamera, new RenderPipeline.StandardRequest { destination = requestTarget });
                yield return null;
                if (feature.ExecutionCount != 1) throw new InvalidOperationException("feature execution count was not one");
            }
            finally
            {
                sourceCamera.enabled = false;
                sourceCamera.cullingMatrix = oldCullingMatrix;
                sourceCamera.useOcclusionCulling = oldOcclusion;
                sourceCamera.aspect = oldAspect;
                sourceCamera.fieldOfView = oldFieldOfView;
                feature.SetActive(false);
            }
            Texture2D featureImage = Read(atlasTarget);
            this.featureImage = featureImage;
            Texture2D dimImage = null;
            float oldIntensity = keyLight == null ? 0f : keyLight.intensity;
            if (keyLight != null)
            {
                try
                {
                    keyLight.intensity = .1f;
                    SetConservativeCulling(sourceCamera, quilt.settings.viewCone, quilt.settings.cameraDistance);
                    sourceCamera.useOcclusionCulling = false;
                    sourceCamera.aspect = TileWidth / (float)TileHeight;
                    feature.ResetExecutionCount();
                    feature.SetActive(true);
                    RenderPipeline.SubmitRenderRequest(sourceCamera, new RenderPipeline.StandardRequest { destination = requestTarget });
                    yield return null;
                    if (feature.ExecutionCount != 1) throw new InvalidOperationException("dim feature execution count was not one");
                    feature.SetActive(false);
                dimImage = Read(atlasTarget);
                this.dimImage = dimImage;
                }
                finally
                {
                    keyLight.intensity = oldIntensity;
                    sourceCamera.cullingMatrix = oldCullingMatrix;
                    sourceCamera.useOcclusionCulling = oldOcclusion;
                    sourceCamera.aspect = oldAspect;
                    sourceCamera.fieldOfView = oldFieldOfView;
                    feature.SetActive(false);
                }
            }
            bool proof = WriteEvidence(reference, featureImage, dimImage);
            if (proof && Argument("--benchmark") == "1") yield return DriveBenchmark();
            Destroy(reference);
            Destroy(featureImage);
            if (dimImage != null) Destroy(dimImage);
            Application.Quit(proof ? 0 : 1);
        }

        IEnumerator DriveBenchmark()
        {
            string directory = Argument("--evidence-dir");
            if (string.IsNullOrEmpty(directory)) directory = Path.Combine(Application.dataPath, "..", "..", ".local", "multiview-spike", Application.buildGUID);
            float oldFieldOfView = sourceCamera.fieldOfView;
            float oldAspect = sourceCamera.aspect;
            Matrix4x4 oldCullingMatrix = sourceCamera.cullingMatrix;
            bool oldOcclusion = sourceCamera.useOcclusionCulling;
            bool oldFeatureActive = feature.isActive;
            const int gcCounterProbeBytes = 8192;
            long gcCounterProbeBefore = GC.GetAllocatedBytesForCurrentThread();
            byte[] gcCounterProbe = new byte[gcCounterProbeBytes];
            long gcCounterProbeAfter = GC.GetAllocatedBytesForCurrentThread();
            GC.KeepAlive(gcCounterProbe);
            long gcCounterProbeDelta = gcCounterProbeAfter - gcCounterProbeBefore;
            bool gcCounterValidated = gcCounterProbeDelta >= gcCounterProbeBytes;
            try
            {
                sourceCamera.enabled = false;
                sourceCamera.fieldOfView = 50f;
                sourceCamera.aspect = TileWidth / (float)TileHeight;
                SetConservativeCulling(sourceCamera, quilt.settings.viewCone, quilt.settings.cameraDistance);
                BenchmarkRun[] baseline = new BenchmarkRun[3];
                BenchmarkRun[] featureRuns = new BenchmarkRun[3];
                for (int run = 0; run < 3; ++run)
                {
                    yield return MeasureBenchmark(false);
                    baseline[run] = benchmarkResult;
                    yield return MeasureBenchmark(true);
                    featureRuns[run] = benchmarkResult;
                    yield return null;
                }
                string grdMode = "unknown";
                UniversalRenderPipelineAsset urpAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (urpAsset != null) grdMode = urpAsset.gpuResidentDrawerMode.ToString();
                File.WriteAllText(Path.Combine(directory, "benchmark.json"), "{\n  \"buildGUID\": \"" + Application.buildGUID + "\",\n  \"graphicsDevice\": \"" + SystemInfo.graphicsDeviceType + "\",\n  \"graphicsMultiThreaded\": " + SystemInfo.graphicsMultiThreaded.ToString().ToLowerInvariant() + ",\n  \"actualUnityVersion\": \"" + Application.unityVersion + "\",\n  \"actualApplication.isEditor\": " + Application.isEditor.ToString().ToLowerInvariant() + ",\n  \"actualDebug.isDebugBuild\": " + Debug.isDebugBuild.ToString().ToLowerInvariant() + ",\n  \"renderThreadTimingAvailable\": false,\n  \"tileWidth\": 128,\n  \"tileHeight\": 96,\n  \"views\": 8,\n  \"rendererListCountFeature\": 8,\n  \"grdMode\": \"" + grdMode + "\",\n  \"gpuTimingAvailable\": false,\n  \"gcCounterProbeBytes\": " + gcCounterProbeBytes + ",\n  \"gcCounterProbeDelta\": " + gcCounterProbeDelta + ",\n  \"gcCounterValidated\": " + gcCounterValidated.ToString().ToLowerInvariant() + ",\n  \"baseline\": " + JsonUtility.ToJson(new BenchmarkRuns { runs = baseline, aggregate = Aggregate(baseline) }) + ",\n  \"feature\": " + JsonUtility.ToJson(new BenchmarkRuns { runs = featureRuns, aggregate = Aggregate(featureRuns) }) + "\n}\n");
            }
            finally
            {
                feature.SetActive(oldFeatureActive);
                sourceCamera.enabled = false;
                sourceCamera.fieldOfView = oldFieldOfView;
                sourceCamera.aspect = oldAspect;
                sourceCamera.cullingMatrix = oldCullingMatrix;
                sourceCamera.useOcclusionCulling = oldOcclusion;
            }
        }

        IEnumerator MeasureBenchmark(bool featureMode)
        {
            float[] samples = new float[1000];
            long[] gcPerSample = new long[samples.Length];
            float oldFov = sourceCamera.fieldOfView;
            float oldAspect = sourceCamera.aspect;
            Matrix4x4 oldCulling = sourceCamera.cullingMatrix;
            bool oldOcclusion = sourceCamera.useOcclusionCulling;
            sourceCamera.fieldOfView = 50f;
            sourceCamera.aspect = TileWidth / (float)TileHeight;
            sourceCamera.enabled = false;
            if (featureMode) { SetConservativeCulling(sourceCamera, quilt.settings.viewCone, quilt.settings.cameraDistance); sourceCamera.useOcclusionCulling = false; feature.SetActive(true); }
            else { sourceCamera.ResetCullingMatrix(); feature.SetActive(false); }
            try
            {
                for (int i = 0; i < 120; ++i)
                {
                    if (featureMode) feature.ResetExecutionCount();
                    SubmitBenchmark(featureMode);
                    if (featureMode && feature.ExecutionCount != 1) throw new InvalidOperationException("benchmark warmup feature execution count was not one");
                    yield return null;
                }
                long gcTotal = 0;
                for (int i = 0; i < samples.Length; ++i)
                {
                    if (featureMode) feature.ResetExecutionCount();
                    long gcBefore = GC.GetAllocatedBytesForCurrentThread();
                    long start = System.Diagnostics.Stopwatch.GetTimestamp();
                    SubmitBenchmark(featureMode);
                    long end = System.Diagnostics.Stopwatch.GetTimestamp();
                    long gcAfter = GC.GetAllocatedBytesForCurrentThread();
                    samples[i] = (float)((end - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
                    gcPerSample[i] = gcAfter - gcBefore;
                    gcTotal += gcPerSample[i];
                    if (featureMode && feature.ExecutionCount != 1) throw new InvalidOperationException("benchmark feature execution count was not one");
                    yield return null;
                }
                float[] rawSamples = (float[])samples.Clone();
                Array.Sort(samples);
                benchmarkResult = new BenchmarkRun { samplesMs = rawSamples, gcBytesPerSample = gcPerSample, sampleCount = rawSamples.Length, warmupCount = 120, requestCount = featureMode ? 1 : 8, samplesMsSorted = samples, meanMs = Mean(samples), p50Ms = samples[500], p95Ms = samples[949], gcBytes = gcTotal };
            }
            finally { feature.SetActive(false); sourceCamera.enabled = false; sourceCamera.fieldOfView = oldFov; sourceCamera.aspect = oldAspect; sourceCamera.cullingMatrix = oldCulling; sourceCamera.useOcclusionCulling = oldOcclusion; }
        }

        void SubmitBenchmark(bool featureMode)
        {
            if (!featureMode) { quilt.RenderQuilt(); return; }
            RenderPipeline.SubmitRenderRequest(sourceCamera, new RenderPipeline.StandardRequest { destination = requestTarget });
        }

        static float Mean(float[] values) { float sum = 0f; for (int i = 0; i < values.Length; ++i) sum += values[i]; return sum / values.Length; }

        static BenchmarkAggregate Aggregate(BenchmarkRun[] runs)
        {
            float[] all = new float[runs.Length * 1000]; int offset = 0; long gc = 0;
            for (int i = 0; i < runs.Length; ++i) { Array.Copy(runs[i].samplesMs, 0, all, offset, runs[i].samplesMs.Length); offset += runs[i].samplesMs.Length; gc += runs[i].gcBytes; }
            Array.Sort(all);
            return new BenchmarkAggregate { sampleCount = all.Length, meanMs = Mean(all), p50Ms = all[all.Length / 2], p95Ms = all[(int)(all.Length * .95f)], gcBytes = gc };
        }

        [Serializable] sealed class BenchmarkRun { public float[] samplesMs; public float[] samplesMsSorted; public long[] gcBytesPerSample; public int sampleCount; public int warmupCount; public int requestCount; public float meanMs; public float p50Ms; public float p95Ms; public long gcBytes; }
        [Serializable] sealed class BenchmarkAggregate { public int sampleCount; public float meanMs; public float p50Ms; public float p95Ms; public long gcBytes; }
        [Serializable] sealed class BenchmarkRuns { public BenchmarkRun[] runs; public BenchmarkAggregate aggregate; public int runCount = 3; public int samplesPerRun = 1000; public int warmupSamples = 120; public int requestCountBaseline = 8; public int requestCountFeature = 1; public bool gpuTimingAvailable; }

        static Texture2D Read(RenderTexture source)
        {
            RenderTexture old = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                Texture2D image = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false);
                image.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
                image.Apply(false, false);
                return image;
            }
            finally { RenderTexture.active = old; }
        }

        static bool WriteEvidence(Texture2D reference, Texture2D featureImage, Texture2D dimImage)
        {
            string directory = Argument("--evidence-dir");
            if (string.IsNullOrEmpty(directory)) directory = Path.Combine(Application.dataPath, "..", "..", ".local", "multiview-spike", Application.buildGUID);
            Directory.CreateDirectory(directory);
            int[] referenceTiles = CountGreenTiles(reference);
            int[] featureTiles = CountGreenTiles(featureImage);
            Vector2[] referenceCentroids = ComputeGreenCentroids(reference);
            Vector2[] featureCentroids = ComputeGreenCentroids(featureImage);
            int[] tileErrors = ComputeTileErrors(reference, featureImage);
            int[] brightDimChanges = dimImage == null ? new int[Views] : ComputeTileErrors(featureImage, dimImage);
            int[] referenceRedTiles = CountRedTiles(reference);
            int[] featureRedTiles = CountRedTiles(featureImage);
            bool greenPass = AllAbove(referenceTiles, 20) && AllAbove(featureTiles, 20);
            bool centroidPass = CentroidsWithin(referenceCentroids, featureCentroids, 2f);
            bool imageErrorPass = AllBelow(tileErrors, TileWidth * TileHeight / 20);
            bool lightPass = dimImage != null && AllAbove(brightDimChanges, 20) && MeanBrightness(featureImage) > MeanBrightness(dimImage);
            bool variationPass = CentroidVariation(referenceCentroids) > 1f && CentroidVariation(featureCentroids) > 1f;
            bool edgePass = HasAnyAbove(referenceRedTiles, 20) && HasAnyAbove(featureRedTiles, 20);
            File.WriteAllText(Path.Combine(directory, "result.json"), "{\n  \"buildGUID\": \"" + Application.buildGUID + "\",\n  \"unityVersion\": \"" + Application.unityVersion + "\",\n  \"views\": 8,\n  \"requestCountReference\": 8,\n  \"requestCountFeature\": 1,\n  \"referenceGreenPixels\": " + CountGreen(reference) + ",\n  \"featureGreenPixels\": " + CountGreen(featureImage) + ",\n  \"referenceTileGreenPixels\": [" + string.Join(",", referenceTiles) + "],\n  \"featureTileGreenPixels\": [" + string.Join(",", featureTiles) + "],\n  \"featureAllTilesNonBlank\": " + AllAbove(featureTiles, 20).ToString().ToLowerInvariant() + "\n}\n");
            File.WriteAllBytes(Path.Combine(directory, "reference.png"), reference.EncodeToPNG());
            File.WriteAllBytes(Path.Combine(directory, "feature.png"), featureImage.EncodeToPNG());
            File.WriteAllText(Path.Combine(directory, "runtime.txt"), "buildGUID=" + Application.buildGUID + "\nunityVersion=" + Application.unityVersion + "\ngraphicsDevice=" + SystemInfo.graphicsDeviceType + "\ngraphicsMultiThreaded=" + SystemInfo.graphicsMultiThreaded + "\ndevelopment=" + Debug.isDebugBuild + "\n");
            if (dimImage != null) File.WriteAllBytes(Path.Combine(directory, "feature-dim.png"), dimImage.EncodeToPNG());
            File.WriteAllText(Path.Combine(directory, "centroids.txt"), "reference=" + FormatCentroids(referenceCentroids) + "\nfeature=" + FormatCentroids(featureCentroids) + "\n");
            File.WriteAllText(Path.Combine(directory, "tile-errors.txt"), string.Join(",", tileErrors) + "\n");
            if (dimImage != null) File.WriteAllText(Path.Combine(directory, "light-difference.txt"), CountDifferent(featureImage, dimImage).ToString() + "\n");
            File.WriteAllText(Path.Combine(directory, "proof.json"), "{\n  \"greenPass\": " + greenPass.ToString().ToLowerInvariant() + ",\n  \"centroidPass\": " + centroidPass.ToString().ToLowerInvariant() + ",\n  \"imageErrorPass\": " + imageErrorPass.ToString().ToLowerInvariant() + ",\n  \"lightPass\": " + lightPass.ToString().ToLowerInvariant() + ",\n  \"variationPass\": " + variationPass.ToString().ToLowerInvariant() + ",\n  \"edgePass\": " + edgePass.ToString().ToLowerInvariant() + ",\n  \"featureExecutionCount\": 1\n}\n");
            File.WriteAllText(Path.Combine(directory, "red-tiles.txt"), "reference=" + string.Join(",", referenceRedTiles) + "\nfeature=" + string.Join(",", featureRedTiles) + "\n");
            return greenPass && centroidPass && imageErrorPass && lightPass && variationPass && edgePass;
        }

        static int CountGreen(Texture2D image)
        {
            int count = 0;
            Color32[] pixels = image.GetPixels32();
            for (int i = 0; i < pixels.Length; ++i) if (pixels[i].g > 15 && pixels[i].g > pixels[i].r * 1.5f && pixels[i].g > pixels[i].b * 1.5f) count++;
            return count;
        }

        static int[] CountGreenTiles(Texture2D image)
        {
            int[] result = new int[Views];
            Color32[] pixels = image.GetPixels32();
            for (int view = 0; view < Views; ++view)
            {
                int tileX = view % 4;
                int tileY = view / 4;
                for (int y = 0; y < TileHeight; ++y)
                    for (int x = 0; x < TileWidth; ++x)
                    {
                        Color32 pixel = pixels[(tileY * TileHeight + y) * image.width + tileX * TileWidth + x];
                        if (pixel.g > 15 && pixel.g > pixel.r * 1.5f && pixel.g > pixel.b * 1.5f) result[view]++;
                    }
            }
            return result;
        }

        static int[] CountRedTiles(Texture2D image)
        {
            int[] result = new int[Views];
            Color32[] pixels = image.GetPixels32();
            for (int view = 0; view < Views; ++view)
                for (int y = 0; y < TileHeight; ++y)
                    for (int x = 0; x < TileWidth; ++x)
                    {
                        int index = ((view / 4) * TileHeight + y) * image.width + (view % 4) * TileWidth + x;
                        Color32 pixel = pixels[index];
                        if (pixel.r > 15 && pixel.r > pixel.g * 1.5f && pixel.r > pixel.b * 1.5f) result[view]++;
                    }
            return result;
        }

        static bool HasAnyAbove(int[] values, int threshold)
        {
            for (int i = 0; i < values.Length; ++i) if (values[i] > threshold) return true;
            return false;
        }

        static Vector2[] ComputeGreenCentroids(Texture2D image)
        {
            Vector2[] sums = new Vector2[Views];
            int[] counts = new int[Views];
            Color32[] pixels = image.GetPixels32();
            for (int view = 0; view < Views; ++view)
                for (int y = 0; y < TileHeight; ++y)
                    for (int x = 0; x < TileWidth; ++x)
                    {
                        int tileX = view % 4;
                        int tileY = view / 4;
                        Color32 pixel = pixels[(tileY * TileHeight + y) * image.width + tileX * TileWidth + x];
                        if (pixel.g > 15 && pixel.g > pixel.r * 1.5f && pixel.g > pixel.b * 1.5f)
                        {
                            sums[view] += new Vector2(x, y);
                            counts[view]++;
                        }
                    }
            for (int view = 0; view < Views; ++view) if (counts[view] > 0) sums[view] /= counts[view];
            return sums;
        }

        static string FormatCentroids(Vector2[] values)
        {
            var result = new System.Text.StringBuilder();
            for (int i = 0; i < values.Length; ++i)
            {
                if (i > 0) result.Append(';');
                result.Append(values[i].x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                result.Append(',');
                result.Append(values[i].y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
            return result.ToString();
        }

        static int[] ComputeTileErrors(Texture2D reference, Texture2D feature)
        {
            int[] result = new int[Views];
            Color32[] a = reference.GetPixels32();
            Color32[] b = feature.GetPixels32();
            for (int view = 0; view < Views; ++view)
                for (int y = 0; y < TileHeight; ++y)
                    for (int x = 0; x < TileWidth; ++x)
                    {
                        int tileX = view % 4;
                        int tileY = view / 4;
                        int index = (tileY * TileHeight + y) * reference.width + tileX * TileWidth + x;
                        int difference = Mathf.Abs(a[index].r - b[index].r) + Mathf.Abs(a[index].g - b[index].g) + Mathf.Abs(a[index].b - b[index].b);
                        if (difference > 8) result[view]++;
                    }
            return result;
        }

        static int CountDifferent(Texture2D first, Texture2D second)
        {
            Color32[] a = first.GetPixels32();
            Color32[] b = second.GetPixels32();
            int count = 0;
            for (int i = 0; i < a.Length; ++i) if (Mathf.Abs(a[i].r - b[i].r) + Mathf.Abs(a[i].g - b[i].g) + Mathf.Abs(a[i].b - b[i].b) > 8) count++;
            return count;
        }

        static bool AllBelow(int[] values, int threshold)
        {
            for (int i = 0; i < values.Length; ++i) if (values[i] > threshold) return false;
            return true;
        }

        static bool CentroidsWithin(Vector2[] first, Vector2[] second, float maxDistance)
        {
            for (int i = 0; i < first.Length; ++i) if (Vector2.Distance(first[i], second[i]) > maxDistance) return false;
            return true;
        }

        static float CentroidVariation(Vector2[] values)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            for (int i = 0; i < values.Length; ++i) { min = Mathf.Min(min, values[i].x); max = Mathf.Max(max, values[i].x); }
            return max - min;
        }

        static float MeanBrightness(Texture2D image)
        {
            Color32[] pixels = image.GetPixels32();
            long total = 0;
            for (int i = 0; i < pixels.Length; ++i) total += pixels[i].r + pixels[i].g + pixels[i].b;
            return total / (float)(pixels.Length * 3);
        }

        static bool AllAbove(int[] values, int threshold)
        {
            for (int i = 0; i < values.Length; ++i) if (values[i] <= threshold) return false;
            return true;
        }

        static void SetConservativeCulling(Camera camera, float viewCone, float distance)
        {
            float nearClip = camera.nearClipPlane;
            float farClip = camera.farClipPlane;
            float vertical = Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * .5f);
            float horizontal = vertical * camera.aspect;
            float eye = Mathf.Tan(viewCone * Mathf.Deg2Rad * .5f) * distance;
            float maxSlope = horizontal;
            foreach (float z in new[] { nearClip, farClip })
                maxSlope = Mathf.Max(maxSlope, horizontal + Mathf.Abs(eye * (1f - z / distance)) / z);
            float unionHalfWidth = nearClip * maxSlope;
            float unionHalfHeight = nearClip * vertical;
            Matrix4x4 projection = Matrix4x4.Frustum(-unionHalfWidth, unionHalfWidth, -unionHalfHeight, unionHalfHeight, nearClip, farClip);
            camera.cullingMatrix = projection * camera.worldToCameraMatrix;
        }

        static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "unknown error";
            var chars = new System.Collections.Generic.List<char>(value.Length);
            foreach (char valueChar in value) if (valueChar >= 0x20 && valueChar <= 0x7e) chars.Add(valueChar);
            return new string(chars.ToArray());
        }

        static string Argument(string key)
        {
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == key) return args[i + 1];
            return null;
        }
    }
}
