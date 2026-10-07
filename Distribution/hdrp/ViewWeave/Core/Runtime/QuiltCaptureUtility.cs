using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ViewWeave
{
    [Serializable]
    public sealed class QuiltCaptureMetadata
    {
        public int width;
        public int height;
        public int rows;
        public int columns;
        public int views;
        public string viewRenderMode;
        public int lastRenderedViewCount;
        public QuiltCaptureRect usedRect;
        public QuiltCaptureGeometry geometry;
        public string timestampUtc;
    }

    [Serializable]
    public sealed class QuiltCentralViewCaptureMetadata
    {
        public QuiltCaptureMetadata quilt;
        public int logicalViewIndex;
        public int renderedViewIndex;
        public int width;
        public int height;
        public string viewRenderMode;
    }

    [Serializable]
    public sealed class QuiltCaptureRect
    {
        public int x;
        public int y;
        public int width;
        public int height;
    }

    [Serializable]
    public struct QuiltCaptureGeometry
    {
        public float viewAspect;
        public float fieldOfView;
        public float cameraDistance;
        public float viewCone;
        public float nearClip;
        public float farClip;
        public float cameraPositionX;
        public float cameraPositionY;
        public float cameraPositionZ;
        public float cameraRotationX;
        public float cameraRotationY;
        public float cameraRotationZ;
        public float cameraRotationW;
    }

    public readonly struct QuiltRenderSnapshot
    {
        public readonly bool IsValid;
        public readonly QuiltLayout Layout;
        public readonly QuiltViewRenderMode ViewRenderMode;
        public readonly QuiltCaptureSettings Settings;
        public readonly int LastRenderedViewCount;
        public readonly long Revision;
        public readonly DateTime TimestampUtc;
        public readonly QuiltCaptureGeometry Geometry;

        internal QuiltRenderSnapshot(QuiltLayout layout, QuiltViewRenderMode mode, QuiltSettings settings, int renderedViewCount, long revision, Camera camera)
        {
            IsValid = true;
            Layout = layout;
            ViewRenderMode = mode;
            Settings = new QuiltCaptureSettings(settings);
            LastRenderedViewCount = renderedViewCount;
            Revision = revision;
            TimestampUtc = DateTime.UtcNow;
            Transform cameraTransform = camera.transform;
            Vector3 position = cameraTransform.position;
            Quaternion rotation = cameraTransform.rotation;
            Geometry = new QuiltCaptureGeometry
            {
                viewAspect = settings.viewAspect,
                fieldOfView = settings.fieldOfView,
                cameraDistance = settings.cameraDistance,
                viewCone = settings.viewCone,
                nearClip = camera.nearClipPlane,
                farClip = camera.farClipPlane,
                cameraPositionX = position.x,
                cameraPositionY = position.y,
                cameraPositionZ = position.z,
                cameraRotationX = rotation.x,
                cameraRotationY = rotation.y,
                cameraRotationZ = rotation.z,
                cameraRotationW = rotation.w
            };
        }

        public QuiltCaptureMetadata ToMetadata()
        {
            return new QuiltCaptureMetadata
            {
                width = Layout.QuiltWidth,
                height = Layout.QuiltHeight,
                rows = Layout.Rows,
                columns = Layout.Columns,
                views = Layout.ViewCount,
                viewRenderMode = ViewRenderMode.ToString(),
                lastRenderedViewCount = LastRenderedViewCount,
                usedRect = new QuiltCaptureRect
                {
                    x = Layout.UsedRect.x,
                    y = Layout.UsedRect.y,
                    width = Layout.UsedRect.width,
                    height = Layout.UsedRect.height
                },
                geometry = Geometry,
                timestampUtc = TimestampUtc.ToString("O", CultureInfo.InvariantCulture)
            };
        }
    }

    public readonly struct QuiltCaptureSettings
    {
        public readonly int quiltWidth;
        public readonly int quiltHeight;
        public readonly int rows;
        public readonly int columns;
        public readonly int views;
        public readonly float viewAspect;
        public readonly float fieldOfView;
        public readonly float cameraDistance;
        public readonly float viewCone;

        internal QuiltCaptureSettings(QuiltSettings settings)
        {
            quiltWidth = settings.quiltWidth;
            quiltHeight = settings.quiltHeight;
            rows = settings.rows;
            columns = settings.columns;
            views = settings.views;
            viewAspect = settings.viewAspect;
            fieldOfView = settings.fieldOfView;
            cameraDistance = settings.cameraDistance;
            viewCone = settings.viewCone;
        }
    }

    public static class QuiltCaptureUtility
    {
        static readonly char[] platformInvalidFileNameCharacters = Path.GetInvalidFileNameChars();

        public static string BuildFileName(string name, QuiltRenderSnapshot snapshot)
        {
            string safeName = string.IsNullOrWhiteSpace(name) ? "quilt" : name.Trim();
            for (int i = 0; i < safeName.Length; ++i)
                if (IsInvalidFileNameCharacter(safeName[i])) safeName = safeName.Replace(safeName[i], '_');
            safeName = safeName.TrimEnd(' ', '.');
            if (safeName.Length == 0) safeName = "quilt";
            string aspect = snapshot.Layout.AspectRatio.ToString("0.######", CultureInfo.InvariantCulture);
            return safeName + "_qs" + snapshot.Layout.Columns.ToString(CultureInfo.InvariantCulture) + "x" + snapshot.Layout.Rows.ToString(CultureInfo.InvariantCulture) + "a" + aspect + ".png";
        }

        public static string SaveLatest(QuiltRenderer renderer, string filePath)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A PNG file path is required.", nameof(filePath));
            if (!renderer.HasRenderedQuilt) throw new InvalidOperationException("No completed quilt is available to save.");
            if (!renderer.IsLatestQuiltCurrent) throw new InvalidOperationException("The quilt is stale because its layout, density, or geometry settings changed.");
            RenderTexture quilt = renderer.QuiltTexture;
            if (quilt == null || !quilt.IsCreated()) throw new InvalidOperationException("The completed quilt texture is unavailable.");
            return Save(quilt, renderer.LatestSnapshot, filePath);
        }

        public static string SaveCentralViewLatest(QuiltRenderer renderer, string filePath)
        {
            if (renderer == null) throw new ArgumentNullException(nameof(renderer));
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A PNG file path is required.", nameof(filePath));
            if (!renderer.HasRenderedQuilt) throw new InvalidOperationException("No completed quilt is available to save.");
            if (!renderer.IsLatestQuiltCurrent) throw new InvalidOperationException("The quilt is stale because its layout, density, or geometry settings changed.");
            RenderTexture quilt = renderer.QuiltTexture;
            if (quilt == null || !quilt.IsCreated()) throw new InvalidOperationException("The completed quilt texture is unavailable.");
            return SaveCentralView(quilt, renderer.LatestSnapshot, filePath);
        }

        public static string SaveCentralView(RenderTexture quilt, QuiltRenderSnapshot snapshot, string filePath)
        {
            if (quilt == null) throw new ArgumentNullException(nameof(quilt));
            if (!quilt.IsCreated()) throw new InvalidOperationException("The quilt render texture is not created.");
            ValidateSnapshot(quilt, snapshot);
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A PNG file path is required.", nameof(filePath));
            string fullPath = Path.GetFullPath(filePath);
            if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The file extension must be .png.", nameof(filePath));
            string sidecarPath = Path.ChangeExtension(fullPath, ".json");
            if (File.Exists(fullPath) || File.Exists(sidecarPath)) throw new IOException("The output PNG or metadata sidecar already exists.");
            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);

            int logicalView = (snapshot.Layout.ViewCount - 1) / 2;
            int renderedView = QuiltViewSampling.GetSourceViewIndex(snapshot.Layout.ViewCount, snapshot.ViewRenderMode, logicalView);
            RectInt tile = snapshot.Layout.GetTile(logicalView);
            RenderTexture oldActive = RenderTexture.active;
            Texture2D pixels = null;
            string temporaryPng = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string temporaryJson = sidecarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool pngCommitted = false;
            try
            {
                pixels = new Texture2D(tile.width, tile.height, TextureFormat.RGBA32, false, false)
                {
                    name = "ViewWeave.CentralView.Readback"
                };
                RenderTexture.active = quilt;
                pixels.ReadPixels(new Rect(tile.x, tile.y, tile.width, tile.height), 0, 0, false);
                pixels.Apply(false, false);
                byte[] png = ImageConversion.EncodeToPNG(pixels);
                QuiltCentralViewCaptureMetadata metadata = new QuiltCentralViewCaptureMetadata
                {
                    quilt = snapshot.ToMetadata(),
                    logicalViewIndex = logicalView,
                    renderedViewIndex = renderedView,
                    width = tile.width,
                    height = tile.height,
                    viewRenderMode = snapshot.ViewRenderMode.ToString()
                };
                byte[] json = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(metadata, true) + "\n");
                File.WriteAllBytes(temporaryPng, png);
                File.WriteAllBytes(temporaryJson, json);
                File.Move(temporaryPng, fullPath);
                pngCommitted = true;
                File.Move(temporaryJson, sidecarPath);
                return fullPath;
            }
            catch
            {
                if (pngCommitted) TryDelete(fullPath);
                throw;
            }
            finally
            {
                try { RenderTexture.active = oldActive; }
                catch { }
                try
                {
                    if (pixels != null)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(pixels);
                        else UnityEngine.Object.DestroyImmediate(pixels);
                    }
                }
                catch { }
                TryDelete(temporaryPng);
                TryDelete(temporaryJson);
            }
        }

        public static string Save(RenderTexture quilt, QuiltRenderSnapshot snapshot, string filePath)
        {
            if (quilt == null) throw new ArgumentNullException(nameof(quilt));
            if (!quilt.IsCreated()) throw new InvalidOperationException("The quilt render texture is not created.");
            ValidateSnapshot(quilt, snapshot);
            if (string.IsNullOrWhiteSpace(filePath)) throw new ArgumentException("A PNG file path is required.", nameof(filePath));
            string fullPath = Path.GetFullPath(filePath);
            if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The file extension must be .png.", nameof(filePath));
            string sidecarPath = Path.ChangeExtension(fullPath, ".json");
            if (File.Exists(fullPath) || File.Exists(sidecarPath)) throw new IOException("The output PNG or metadata sidecar already exists.");
            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);

            RenderTexture oldActive = RenderTexture.active;
            Texture2D pixels = null;
            string temporaryPng = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string temporaryJson = sidecarPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool pngCommitted = false;
            try
            {
                pixels = new Texture2D(quilt.width, quilt.height, TextureFormat.RGBA32, false, false)
                {
                    name = "ViewWeave.Capture.Readback"
                };
                RenderTexture.active = quilt;
                pixels.ReadPixels(new Rect(0, 0, quilt.width, quilt.height), 0, 0, false);
                pixels.Apply(false, false);
                byte[] png = ImageConversion.EncodeToPNG(pixels);
                byte[] json = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(snapshot.ToMetadata(), true) + "\n");
                File.WriteAllBytes(temporaryPng, png);
                File.WriteAllBytes(temporaryJson, json);
                File.Move(temporaryPng, fullPath);
                pngCommitted = true;
                File.Move(temporaryJson, sidecarPath);
                return fullPath;
            }
            catch
            {
                if (pngCommitted) TryDelete(fullPath);
                throw;
            }
            finally
            {
                try { RenderTexture.active = oldActive; }
                catch { }
                try
                {
                    if (pixels != null)
                    {
                        if (Application.isPlaying) UnityEngine.Object.Destroy(pixels);
                        else UnityEngine.Object.DestroyImmediate(pixels);
                    }
                }
                catch { }
                TryDelete(temporaryPng);
                TryDelete(temporaryJson);
            }
        }

        static void ValidateSnapshot(RenderTexture quilt, QuiltRenderSnapshot snapshot)
        {
            if (!snapshot.IsValid || snapshot.Revision < 1 || snapshot.TimestampUtc == default(DateTime))
                throw new ArgumentException("A completed render snapshot is required.", nameof(snapshot));
            if (snapshot.Layout.QuiltWidth != quilt.width || snapshot.Layout.QuiltHeight != quilt.height)
                throw new ArgumentException("The snapshot dimensions do not match the quilt texture.", nameof(snapshot));
            if (snapshot.Layout.Rows < 1 || snapshot.Layout.Columns < 1 || snapshot.Layout.ViewCount < 1 || snapshot.Layout.ViewCount > snapshot.Layout.Rows * (long)snapshot.Layout.Columns)
                throw new ArgumentException("The snapshot quilt layout is invalid.", nameof(snapshot));
            if (snapshot.LastRenderedViewCount < 1 || snapshot.LastRenderedViewCount > snapshot.Layout.ViewCount)
                throw new ArgumentException("The snapshot rendered view count is invalid.", nameof(snapshot));
            if (snapshot.Settings.quiltWidth != quilt.width || snapshot.Settings.quiltHeight != quilt.height
                || snapshot.Settings.rows != snapshot.Layout.Rows || snapshot.Settings.columns != snapshot.Layout.Columns
                || snapshot.Settings.views != snapshot.Layout.ViewCount || snapshot.Settings.viewAspect != snapshot.Layout.AspectRatio)
                throw new ArgumentException("The snapshot settings do not match its layout.", nameof(snapshot));
        }

        static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { }
        }

        static bool IsInvalidFileNameCharacter(char value)
        {
            if (value < 0x20 || Array.IndexOf(platformInvalidFileNameCharacters, value) >= 0) return true;
            return value == '<' || value == '>' || value == ':' || value == '"' || value == '/' || value == '\\' || value == '|' || value == '?' || value == '*';
        }
    }
}
