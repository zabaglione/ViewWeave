using System;
using System.Collections;
using System.IO;
using LookingGlass.Community;
using LookingGlass.Community.URP;
using UnityEngine;
using UnityEngine.Rendering;

namespace LookingGlass.MultiviewSpikeValidation
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
            IEnumerator iterator = Run();
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
            if (requestTarget != null) { requestTarget.Release(); Destroy(requestTarget); }
            bool proof = WriteEvidence(reference, featureImage, dimImage);
            Destroy(reference);
            Destroy(featureImage);
            if (dimImage != null) Destroy(dimImage);
            Application.Quit(proof ? 0 : 1);
        }

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
