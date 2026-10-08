using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace ViewWeave
{
    public sealed class QuiltPlaybackFrameInfo
    {
        public int Index { get; internal set; }
        public string FilePath { get; internal set; }
        public QuiltCaptureMetadata Metadata { get; internal set; }
    }

    public sealed class QuiltPlaybackInfo
    {
        public string SourcePath { get; internal set; }
        public string GeometrySource { get; internal set; }
        public QuiltLayout Layout { get; internal set; }
        public QuiltCaptureGeometry Geometry { get; internal set; }
        public bool HasCameraGeometry { get; internal set; }
        public int FramesPerSecond { get; internal set; }
        public int FrameCount { get; internal set; }
        public bool IsRecordingSequence { get; internal set; }
        public string ViewRenderMode { get; internal set; }
        public int LastRenderedViewCount { get; internal set; }
        public double DurationSeconds => FramesPerSecond < 1 ? 0d : FrameCount / (double)FramesPerSecond;
        public bool IsSequence => IsRecordingSequence;
    }

    public sealed class QuiltPlaybackSource
    {
        const long MaximumPngBytes = 512L * 1024L * 1024L;
        const long MaximumDecodedBytes = 512L * 1024L * 1024L;
        const int MaximumFrameCount = 100000;
        const int MaximumManifestBytes = 1024 * 1024;
        const long MaximumFrameLogBytes = 32L * 1024L * 1024L;
        static readonly byte[] pngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };
        static readonly Regex quiltTag = new Regex(@"(?:^|_)qs(?<columns>\d+)x(?<rows>\d+)a(?<aspect>\d+(?:\.\d+)?)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        static readonly Regex frameName = new Regex(@"^frame_(?<index>\d{6,})_qs(?<columns>\d+)x(?<rows>\d+)a(?<aspect>\d+(?:\.\d+)?)\.png$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        static readonly UTF8Encoding strictUtf8 = new UTF8Encoding(false, true);
        readonly List<QuiltPlaybackFrameInfo> frames;

        public QuiltPlaybackInfo Info { get; private set; }
        public IReadOnlyList<QuiltPlaybackFrameInfo> Frames => frames;

        QuiltPlaybackSource(List<QuiltPlaybackFrameInfo> frames, QuiltPlaybackInfo info)
        {
            this.frames = frames;
            Info = info;
        }

        public static QuiltPlaybackSource Open(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Select a quilt PNG or completed recording folder.", nameof(path));
            string fullPath = Path.GetFullPath(path);
            if (Directory.Exists(fullPath)) return OpenSequence(fullPath);
            if (!File.Exists(fullPath)) throw new FileNotFoundException("Quilt playback source does not exist.", fullPath);
            if (string.Equals(Path.GetFileName(fullPath), "manifest.json", StringComparison.OrdinalIgnoreCase))
                return OpenSequence(Path.GetDirectoryName(fullPath));
            if (!string.Equals(Path.GetExtension(fullPath), ".png", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Playback supports a full quilt PNG or a completed recording folder.");
            return OpenSingle(fullPath);
        }

        public string GetFramePath(int frameIndex)
        {
            if (frameIndex < 0 || frameIndex >= frames.Count) throw new ArgumentOutOfRangeException(nameof(frameIndex));
            return frames[frameIndex].FilePath;
        }

        public Texture2D LoadFrame(int frameIndex)
        {
            string path = GetFramePath(frameIndex);
            int width;
            int height;
            byte[] bytes = ReadPngBytesValidated(path, out width, out height);
            if (width != Info.Layout.QuiltWidth || height != Info.Layout.QuiltHeight)
                throw new InvalidDataException("A quilt PNG changed dimensions after playback validation.");
            Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false)
            {
                name = "ViewWeave.PlaybackFrame." + frameIndex.ToString("D6", CultureInfo.InvariantCulture)
            };
            try
            {
                if (!ImageConversion.LoadImage(texture, bytes, false))
                    throw new InvalidDataException("A quilt PNG could not be decoded.");
                if (texture.width != width || texture.height != height)
                    throw new InvalidDataException("Decoded quilt dimensions do not match the PNG header.");
                return texture;
            }
            catch
            {
                if (Application.isPlaying) UnityEngine.Object.Destroy(texture);
                else UnityEngine.Object.DestroyImmediate(texture);
                throw;
            }
        }

        public static void ReadPngDimensions(string path, out int width, out int height)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("A PNG file path is required.", nameof(path));
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                ReadPngHeader(stream, out width, out height);
        }

        static byte[] ReadPngBytesValidated(string path, out int width, out int height)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                ReadPngHeader(stream, out width, out height);
                if (stream.Length > int.MaxValue) throw new InvalidDataException("Quilt PNG is too large to decode safely.");
                byte[] bytes = new byte[(int)stream.Length];
                stream.Position = 0;
                int read = 0;
                while (read < bytes.Length)
                {
                    int count = stream.Read(bytes, read, bytes.Length - read);
                    if (count == 0) throw new InvalidDataException("Quilt PNG changed or was truncated while reading.");
                    read += count;
                }
                int verifiedWidth;
                int verifiedHeight;
                ValidatePngHeader(bytes, bytes.Length, out verifiedWidth, out verifiedHeight);
                if (verifiedWidth != width || verifiedHeight != height)
                    throw new InvalidDataException("Quilt PNG dimensions changed while reading.");
                return bytes;
            }
        }

        static void ReadPngHeader(Stream stream, out int width, out int height)
        {
            if (stream.Length < 24 || stream.Length > MaximumPngBytes)
                throw new InvalidDataException("Quilt PNG size is outside the supported range.");
            byte[] header = new byte[24];
            int read = 0;
            while (read < header.Length)
            {
                int count = stream.Read(header, read, header.Length - read);
                if (count == 0) throw new InvalidDataException("Quilt PNG header is truncated.");
                read += count;
            }

            ValidatePngHeader(header, stream.Length, out width, out height);
        }

        static void ValidatePngHeader(byte[] header, long fileLength, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (fileLength < 24 || fileLength > MaximumPngBytes)
                throw new InvalidDataException("Quilt PNG size is outside the supported range.");
            for (int i = 0; i < pngSignature.Length; ++i)
                if (header[i] != pngSignature[i]) throw new InvalidDataException("Quilt PNG signature is invalid.");
            if (ReadUInt32BigEndian(header, 8) != 13 || header[12] != 73 || header[13] != 72 || header[14] != 68 || header[15] != 82)
                throw new InvalidDataException("Quilt PNG does not begin with a valid IHDR chunk.");
            uint pngWidth = ReadUInt32BigEndian(header, 16);
            uint pngHeight = ReadUInt32BigEndian(header, 20);
            int maximumDimension = SystemInfo.maxTextureSize > 0 ? Math.Min(16384, SystemInfo.maxTextureSize) : 16384;
            if (pngWidth < 1 || pngHeight < 1 || pngWidth > maximumDimension || pngHeight > maximumDimension ||
                (long)pngWidth > MaximumDecodedBytes / (4L * pngHeight))
                throw new InvalidDataException("Quilt PNG dimensions exceed the supported texture size.");
            width = checked((int)pngWidth);
            height = checked((int)pngHeight);
        }

        static QuiltPlaybackSource OpenSingle(string pngPath)
        {
            int width;
            int height;
            ReadPngDimensions(pngPath, out width, out height);
            string sidecarPath = Path.ChangeExtension(pngPath, ".json");
            QuiltCaptureMetadata metadata = null;
            PlaybackLayoutData data;
            if (File.Exists(sidecarPath))
            {
                metadata = ReadFullQuiltMetadata(sidecarPath);
                data = ValidateMetadata(metadata, width, height);
                QuiltTag tag;
                if (TryParseTag(Path.GetFileNameWithoutExtension(pngPath), out tag))
                    ValidateTagMatches(tag, data.Layout, data.Geometry.viewAspect);
            }
            else
            {
                QuiltTag tag;
                if (!TryParseTag(Path.GetFileNameWithoutExtension(pngPath), out tag))
                    throw new InvalidDataException("A PNG without a sidecar must use the official _qsCxRaA filename tag; layout guessing is disabled.");
                data = CreateTaggedLayout(width, height, tag);
            }

            var info = new QuiltPlaybackInfo
            {
                SourcePath = pngPath,
                GeometrySource = metadata == null ? "filename-tag" : "sidecar",
                Layout = data.Layout,
                Geometry = data.Geometry,
                HasCameraGeometry = metadata != null,
                FramesPerSecond = 30,
                FrameCount = 1,
                IsRecordingSequence = false,
                ViewRenderMode = metadata == null ? "Unknown" : metadata.viewRenderMode,
                LastRenderedViewCount = metadata == null ? data.Layout.ViewCount : metadata.lastRenderedViewCount
            };
            var frame = new QuiltPlaybackFrameInfo { Index = 0, FilePath = pngPath, Metadata = metadata };
            return new QuiltPlaybackSource(new List<QuiltPlaybackFrameInfo> { frame }, info);
        }

        static QuiltPlaybackSource OpenSequence(string directory)
        {
            string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string manifestPath = Path.Combine(root, "manifest.json");
            string manifestText = ReadBoundedText(manifestPath, MaximumManifestBytes, "recording manifest");
            QuiltRecordingManifest manifest = JsonUtility.FromJson<QuiltRecordingManifest>(manifestText);
            if (manifest == null || !string.Equals(manifest.status, "complete", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The recording manifest is unfinished; playback requires status complete.");
            if (manifest.fps < 1 || manifest.fps > 240 || manifest.requestedFrameCount < 1 || manifest.requestedFrameCount > MaximumFrameCount || manifest.completedFrameCount != manifest.requestedFrameCount)
                throw new InvalidDataException("The completed recording does not contain every requested frame.");
            if (string.IsNullOrEmpty(manifest.finishedAtUtc) || !DateTime.TryParse(manifest.finishedAtUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                throw new InvalidDataException("The completed recording manifest has no valid finish timestamp.");
            if (!string.Equals(manifest.frameLog, "frames.jsonl", StringComparison.Ordinal))
                throw new InvalidDataException("The recording manifest must reference frames.jsonl.");

            string frameLogPath = Path.Combine(root, manifest.frameLog);
            var logInfo = new FileInfo(frameLogPath);
            if (!logInfo.Exists || logInfo.Length > MaximumFrameLogBytes)
                throw new InvalidDataException("The recording frame log is missing or too large.");
            string[] lines = File.ReadAllLines(frameLogPath, strictUtf8);
            if (lines.Length != manifest.completedFrameCount)
                throw new InvalidDataException("Frame log count does not match the completed manifest.");

            var frames = new List<QuiltPlaybackFrameInfo>(lines.Length);
            QuiltCaptureMetadata reference = null;
            QuiltLayout layout = default;
            for (int i = 0; i < lines.Length; ++i)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) throw new InvalidDataException("Frame log contains an empty entry.");
                QuiltRecordingFrame frame = JsonUtility.FromJson<QuiltRecordingFrame>(lines[i]);
                if (frame == null || frame.index != i || !IsFinite(frame.simulationTime) || !IsFinite(frame.deltaTime) || frame.deltaTime < 0f)
                    throw new InvalidDataException("Frame indexes and timing must be valid and consecutive.");
                string fileName = frame.fileName;
                if (string.IsNullOrEmpty(fileName) || Path.GetFileName(fileName) != fileName || fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
                    throw new InvalidDataException("Frame paths must be relative file names inside the recording folder.");
                Match match = frameName.Match(fileName);
                if (!match.Success || ParseFrameIndex(match.Groups["index"].Value) != i)
                    throw new InvalidDataException("Frames must use consecutive frame_######_qsCxRaA PNG names.");
                int columns = ParsePositiveInt(match.Groups["columns"].Value, "quilt columns");
                int rows = ParsePositiveInt(match.Groups["rows"].Value, "quilt rows");
                double aspect = ParseAspect(match.Groups["aspect"].Value);
                string pngPath = ResolveRecordingFile(root, fileName);
                string sidecarPath = Path.ChangeExtension(pngPath, ".json");
                int width;
                int height;
                ReadPngDimensions(pngPath, out width, out height);
                QuiltCaptureMetadata metadata = ReadFullQuiltMetadata(sidecarPath);
                PlaybackLayoutData current = ValidateMetadata(metadata, width, height);
                ValidateTagMatches(new QuiltTag(columns, rows, aspect), current.Layout, current.Geometry.viewAspect);
                if (reference == null)
                {
                    reference = metadata;
                    layout = current.Layout;
                }
                else
                {
                    ValidateMetadataMatch(reference, metadata);
                }

                frames.Add(new QuiltPlaybackFrameInfo { Index = i, FilePath = pngPath, Metadata = metadata });
            }

            var info = new QuiltPlaybackInfo
            {
                SourcePath = root,
                GeometrySource = "sidecar",
                Layout = layout,
                Geometry = reference.geometry,
                HasCameraGeometry = true,
                FramesPerSecond = manifest.fps,
                FrameCount = frames.Count,
                IsRecordingSequence = true,
                ViewRenderMode = reference.viewRenderMode,
                LastRenderedViewCount = reference.lastRenderedViewCount
            };
            return new QuiltPlaybackSource(frames, info);
        }

        static QuiltCaptureMetadata ReadFullQuiltMetadata(string path)
        {
            string text = ReadBoundedText(path, 65536, "quilt metadata sidecar");
            QuiltCaptureMetadata metadata = JsonUtility.FromJson<QuiltCaptureMetadata>(text);
            if (metadata == null || metadata.width < 1)
            {
                QuiltCentralViewCaptureMetadata central = JsonUtility.FromJson<QuiltCentralViewCaptureMetadata>(text);
                if (central != null && central.quilt != null)
                    throw new InvalidDataException("Central 2D captures cannot be played as full quilt images.");
                throw new InvalidDataException("Quilt metadata sidecar is invalid.");
            }

            return metadata;
        }

        static PlaybackLayoutData ValidateMetadata(QuiltCaptureMetadata metadata, int width, int height)
        {
            if (metadata == null || metadata.usedRect == null)
                throw new InvalidDataException("A full quilt metadata sidecar is required; central 2D captures are not supported.");
            if (metadata.width != width || metadata.height != height || metadata.columns < 1 || metadata.rows < 1 || metadata.views != (long)metadata.columns * metadata.rows)
                throw new InvalidDataException("Metadata must describe a complete quilt grid matching the PNG dimensions.");
            if (metadata.lastRenderedViewCount < 1 || metadata.lastRenderedViewCount > metadata.views || string.IsNullOrEmpty(metadata.viewRenderMode))
                throw new InvalidDataException("Metadata render mode or rendered view count is invalid.");
            QuiltViewRenderMode mode;
            if (!Enum.TryParse(metadata.viewRenderMode, true, out mode) || !Enum.IsDefined(typeof(QuiltViewRenderMode), mode))
                throw new InvalidDataException("Metadata view render mode is unsupported.");
            QuiltCaptureGeometry geometry = metadata.geometry;
            ValidateGeometry(geometry);
            QuiltLayout layout;
            try { layout = new QuiltLayout(width, height, metadata.rows, metadata.columns, metadata.views, geometry.viewAspect); }
            catch (Exception error) { throw new InvalidDataException("Metadata quilt layout is invalid: " + error.GetType().Name, error); }
            QuiltCaptureRect used = metadata.usedRect;
            if (used.x != layout.UsedRect.x || used.y != layout.UsedRect.y || used.width != layout.UsedRect.width || used.height != layout.UsedRect.height)
                throw new InvalidDataException("Metadata usedRect does not match the complete row-major quilt grid.");
            return new PlaybackLayoutData(layout, geometry);
        }

        static void ValidateMetadataMatch(QuiltCaptureMetadata expected, QuiltCaptureMetadata actual)
        {
            if (expected.width != actual.width || expected.height != actual.height || expected.rows != actual.rows || expected.columns != actual.columns || expected.views != actual.views ||
                expected.viewRenderMode != actual.viewRenderMode || expected.lastRenderedViewCount != actual.lastRenderedViewCount ||
                expected.usedRect.x != actual.usedRect.x || expected.usedRect.y != actual.usedRect.y || expected.usedRect.width != actual.usedRect.width || expected.usedRect.height != actual.usedRect.height ||
                !SameStaticGeometry(expected.geometry, actual.geometry))
                throw new InvalidDataException("All recording frames must share the same quilt layout and static view geometry.");
        }

        static bool SameStaticGeometry(QuiltCaptureGeometry left, QuiltCaptureGeometry right)
        {
            return left.viewAspect == right.viewAspect && left.fieldOfView == right.fieldOfView && left.cameraDistance == right.cameraDistance &&
                left.viewCone == right.viewCone && left.nearClip == right.nearClip && left.farClip == right.farClip &&
                left.physicalFramingEnabled == right.physicalFramingEnabled &&
                left.visibleApertureWidthMillimeters == right.visibleApertureWidthMillimeters &&
                left.visibleApertureHeightMillimeters == right.visibleApertureHeightMillimeters && left.unitsPerMeter == right.unitsPerMeter;
        }

        static void ValidateGeometry(QuiltCaptureGeometry geometry)
        {
            if (!IsFinite(geometry.viewAspect) || !IsFinite(geometry.fieldOfView) || !IsFinite(geometry.cameraDistance) || !IsFinite(geometry.viewCone) ||
                !IsFinite(geometry.nearClip) || !IsFinite(geometry.farClip) || !IsFinite(geometry.cameraPositionX) || !IsFinite(geometry.cameraPositionY) ||
                !IsFinite(geometry.cameraPositionZ) || !IsFinite(geometry.cameraRotationX) || !IsFinite(geometry.cameraRotationY) ||
                !IsFinite(geometry.cameraRotationZ) || !IsFinite(geometry.cameraRotationW) || geometry.viewAspect <= 0f || geometry.fieldOfView <= 0f ||
                geometry.fieldOfView >= 180f || geometry.cameraDistance <= 0f || geometry.viewCone < 0f || geometry.nearClip <= 0f || geometry.farClip <= geometry.nearClip)
                throw new InvalidDataException("Metadata camera geometry is invalid.");
            if (geometry.physicalFramingEnabled &&
                (!IsFinite(geometry.visibleApertureWidthMillimeters) || !IsFinite(geometry.visibleApertureHeightMillimeters) ||
                 !IsFinite(geometry.unitsPerMeter) || geometry.visibleApertureWidthMillimeters <= 0f ||
                 geometry.visibleApertureHeightMillimeters <= 0f || geometry.unitsPerMeter <= 0f))
                throw new InvalidDataException("Metadata physical framing is invalid.");
        }

        static PlaybackLayoutData CreateTaggedLayout(int width, int height, QuiltTag tag)
        {
            if (tag.Columns < 1 || tag.Rows < 1 || (long)tag.Columns * tag.Rows > 4096L)
                throw new InvalidDataException("Filename quilt grid is outside the supported range.");
            QuiltLayout layout;
            try { layout = new QuiltLayout(width, height, tag.Rows, tag.Columns, checked(tag.Columns * tag.Rows), (float)tag.Aspect); }
            catch (Exception error) { throw new InvalidDataException("Filename quilt layout is invalid: " + error.GetType().Name, error); }
            return new PlaybackLayoutData(layout, new QuiltCaptureGeometry { viewAspect = (float)tag.Aspect });
        }

        static void ValidateTagMatches(QuiltTag tag, QuiltLayout layout, float viewAspect)
        {
            if (tag.Columns != layout.Columns || tag.Rows != layout.Rows || Math.Abs(tag.Aspect - viewAspect) > 0.00001d)
                throw new InvalidDataException("Filename quilt tag does not match its metadata sidecar.");
        }

        static bool TryParseTag(string stem, out QuiltTag tag)
        {
            Match match = quiltTag.Match(stem ?? string.Empty);
            if (!match.Success)
            {
                tag = default;
                return false;
            }

            tag = new QuiltTag(ParsePositiveInt(match.Groups["columns"].Value, "quilt columns"), ParsePositiveInt(match.Groups["rows"].Value, "quilt rows"), ParseAspect(match.Groups["aspect"].Value));
            return true;
        }

        static int ParsePositiveInt(string value, string label)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed < 1)
                throw new InvalidDataException("Invalid " + label + " in quilt source metadata.");
            return parsed;
        }

        static int ParseFrameIndex(string value)
        {
            int parsed;
            if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed) || parsed < 0)
                throw new InvalidDataException("Invalid frame index in quilt recording metadata.");
            return parsed;
        }

        static double ParseAspect(string value)
        {
            double parsed;
            if (!double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed) || parsed <= 0d)
                throw new InvalidDataException("Invalid quilt aspect in filename tag.");
            return parsed;
        }

        static string ResolveRecordingFile(string root, string fileName)
        {
            string path = Path.GetFullPath(Path.Combine(root, fileName));
            string prefix = root + Path.DirectorySeparatorChar;
            StringComparison comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!path.StartsWith(prefix, comparison)) throw new InvalidDataException("A recording frame path escaped its source folder.");
            return path;
        }

        static string ReadBoundedText(string path, int maximumBytes, string label)
        {
            FileInfo file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("The " + label + " is missing.", path);
            if (file.Length < 1 || file.Length > maximumBytes) throw new InvalidDataException("The " + label + " is empty or too large.");
            return File.ReadAllText(path, strictUtf8);
        }

        static uint ReadUInt32BigEndian(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        static bool IsFinite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
        static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        readonly struct QuiltTag
        {
            public readonly int Columns;
            public readonly int Rows;
            public readonly double Aspect;
            public QuiltTag(int columns, int rows, double aspect) { Columns = columns; Rows = rows; Aspect = aspect; }
        }

        readonly struct PlaybackLayoutData
        {
            public readonly QuiltLayout Layout;
            public readonly QuiltCaptureGeometry Geometry;
            public PlaybackLayoutData(QuiltLayout layout, QuiltCaptureGeometry geometry) { Layout = layout; Geometry = geometry; }
        }
    }

    public readonly struct QuiltPlaybackStep
    {
        public readonly int FrameIndex;
        public readonly int SkippedFrameCount;
        public readonly bool HasFrame;
        public readonly bool IsEndOfStream;

        internal QuiltPlaybackStep(int frameIndex, int skippedFrameCount, bool hasFrame, bool isEndOfStream)
        {
            FrameIndex = frameIndex;
            SkippedFrameCount = skippedFrameCount;
            HasFrame = hasFrame;
            IsEndOfStream = isEndOfStream;
        }
    }

    public sealed class QuiltPlaybackCursor
    {
        readonly int frameRate;
        readonly int frameCount;
        int lastFrame = -1;
        double elapsed;
        bool isPaused;
        bool isComplete;

        public QuiltPlaybackCursor(int framesPerSecond, int totalFrames)
        {
            if (framesPerSecond < 1 || framesPerSecond > 240) throw new ArgumentOutOfRangeException(nameof(framesPerSecond));
            if (totalFrames < 1) throw new ArgumentOutOfRangeException(nameof(totalFrames));
            frameRate = framesPerSecond;
            frameCount = totalFrames;
        }

        public bool IsPaused => isPaused;
        public bool IsComplete => isComplete;
        public int CurrentFrame => lastFrame;

        public void Pause() { if (!isComplete) isPaused = true; }
        public void Resume() { if (!isComplete) isPaused = false; }

        public QuiltPlaybackStep Advance(double unscaledDeltaTime)
        {
            if (!IsFinite(unscaledDeltaTime) || unscaledDeltaTime < 0d) throw new ArgumentOutOfRangeException(nameof(unscaledDeltaTime));
            if (isComplete) return new QuiltPlaybackStep(lastFrame, 0, false, true);
            if (isPaused) return new QuiltPlaybackStep(lastFrame, 0, false, false);
            elapsed += unscaledDeltaTime;
            double framePosition = elapsed * frameRate;
            if (!double.IsInfinity(framePosition) && !double.IsNaN(framePosition))
            {
                double nearestFrameBoundary = Math.Round(framePosition);
                if (Math.Abs(framePosition - nearestFrameBoundary) <= 1e-9)
                    framePosition = nearestFrameBoundary;
            }

            if (framePosition >= frameCount && lastFrame == frameCount - 1)
            {
                isComplete = true;
                return new QuiltPlaybackStep(lastFrame, 0, false, true);
            }
            int target = framePosition >= frameCount ? frameCount - 1 : (int)Math.Floor(framePosition);
            if (target > lastFrame)
            {
                int skipped = Math.Max(0, target - lastFrame - 1);
                lastFrame = target;
                return new QuiltPlaybackStep(target, skipped, true, false);
            }

            return new QuiltPlaybackStep(lastFrame, 0, false, false);
        }

        static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }
    }
}
