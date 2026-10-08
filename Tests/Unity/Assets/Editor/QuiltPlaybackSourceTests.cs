using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using ViewWeave;

public static class QuiltPlaybackSourceTests
{
    const int Width = 101;
    const int Height = 55;
    const int Columns = 8;
    const int Rows = 6;

    [MenuItem("Tools/ViewWeave/Run Quilt Playback CPU Smoke")]
    public static void RunPlaybackCpuSmoke()
    {
        string directory = Path.Combine(Path.GetTempPath(), "ViewWeavePlayback_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            TestTaggedImageAndLayout(directory);
            TestRejectedInputs(directory);
            TestCompletedSequence(directory);
            TestCursor();
            TestRendererOperationGuard();
            Debug.Log("ViewWeave Quilt playback CPU smoke passed");
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    static void TestTaggedImageAndLayout(string directory)
    {
        string path = Path.Combine(directory, "quilt_qs8x6a0.75.png");
        WritePatternPng(path);
        QuiltPlaybackSource source = QuiltPlaybackSource.Open(path);
        Require(source.Info.Layout.ViewCount == 48, "tagged image reports all 48 logical views");
        Require(source.Info.Layout.TileWidth == 12 && source.Info.Layout.TileHeight == 9, "layout uses floor tile dimensions");
        Require(source.Info.Layout.UsedRect.width == 96 && source.Info.Layout.UsedRect.height == 54, "layout reports used pixels and preserves remainder");
        Require(source.Info.GeometrySource == "filename-tag" && !source.Info.HasCameraGeometry, "filename-only geometry is identified");
        Require(!source.Info.IsSequence && source.Info.FrameCount == 1, "single quilt is distinct from a recording sequence");

        Texture2D decoded = source.LoadFrame(0);
        try
        {
            Require(decoded.width == Width && decoded.height == Height, "decoded dimensions match validated PNG header");
            Color32[] pixels = decoded.GetPixels32();
            CheckPixel(pixels, 1, 1, TileColor(0), "bottom-left row-major view zero");
            CheckPixel(pixels, 13, 1, TileColor(1), "bottom-left row-major view one");
            CheckPixel(pixels, 1, 10, TileColor(8), "next row starts above view zero");
            CheckPixel(pixels, 85, 46, TileColor(47), "top-right view 47");
            CheckPixel(pixels, 99, 53, new Color32(255, 0, 255, 255), "unused right and top remainder stays outside the grid");
        }
        finally { UnityEngine.Object.DestroyImmediate(decoded); }
    }

    static void TestRejectedInputs(string directory)
    {
        string untagged = Path.Combine(directory, "plain.png");
        WritePatternPng(untagged);
        ExpectInvalid(() => QuiltPlaybackSource.Open(untagged), "untagged sidecarless PNG is rejected");

        string central = Path.Combine(directory, "central_qs8x6a0.75.png");
        WritePatternPng(central);
        var quilt = CreateMetadata();
        var centralMetadata = new QuiltCentralViewCaptureMetadata
        {
            quilt = quilt,
            logicalViewIndex = 23,
            renderedViewIndex = 23,
            width = 12,
            height = 9,
            viewRenderMode = "Full"
        };
        File.WriteAllText(Path.ChangeExtension(central, ".json"), JsonUtility.ToJson(centralMetadata));
        ExpectInvalid(() => QuiltPlaybackSource.Open(central), "central 2D sidecar is rejected as a full quilt");

        string inconsistent = Path.Combine(directory, "bad_qs4x12a0.75.png");
        WritePatternPng(inconsistent);
        File.WriteAllText(Path.ChangeExtension(inconsistent, ".json"), JsonUtility.ToJson(CreateMetadata()));
        ExpectInvalid(() => QuiltPlaybackSource.Open(inconsistent), "filename tag must match sidecar grid");

        string badHeader = Path.Combine(directory, "bad-header_qs8x6a0.75.png");
        File.WriteAllBytes(badHeader, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        ExpectInvalid(() => QuiltPlaybackSource.Open(badHeader), "truncated PNG header is rejected before decode");
    }

    static void TestCompletedSequence(string parent)
    {
        string directory = Path.Combine(parent, "sequence");
        Directory.CreateDirectory(directory);
        var lines = new List<string>();
        for (int i = 0; i < 3; ++i)
        {
            string fileName = "frame_" + i.ToString("D6", CultureInfo.InvariantCulture) + "_qs8x6a0.75.png";
            WritePatternPng(Path.Combine(directory, fileName));
            QuiltCaptureMetadata metadata = CreateMetadata();
            metadata.geometry.cameraPositionX = i;
            File.WriteAllText(Path.Combine(directory, Path.ChangeExtension(fileName, ".json")), JsonUtility.ToJson(metadata));
            lines.Add(JsonUtility.ToJson(new QuiltRecordingFrame
            {
                index = i,
                unityFrame = i + 1,
                simulationTime = i / 30d,
                deltaTime = 1f / 30f,
                fileName = fileName,
                timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }));
        }

        File.WriteAllText(Path.Combine(directory, "frames.jsonl"), string.Join("\n", lines) + "\n");
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonUtility.ToJson(new QuiltRecordingManifest
        {
            fps = 30,
            requestedFrameCount = 3,
            completedFrameCount = 3,
            timeScale = 1f,
            status = "complete",
            startedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            finishedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            frameLog = "frames.jsonl"
        }));

        QuiltPlaybackSource source = QuiltPlaybackSource.Open(directory);
        Require(source.Info.IsSequence && source.Info.FrameCount == 3, "complete manifest opens a recording sequence");
        Require(source.Info.FramesPerSecond == 30 && source.Info.HasCameraGeometry, "sequence metadata provides playback rate and geometry");
        Require(source.Frames[2].Metadata.geometry.cameraPositionX == 2f, "per-frame camera pose may change while static quilt geometry stays fixed");

        QuiltCaptureMetadata secondFrameMetadata = source.Frames[1].Metadata;
        secondFrameMetadata.geometry.physicalFramingEnabled = true;
        secondFrameMetadata.geometry.visibleApertureWidthMillimeters = 160f;
        secondFrameMetadata.geometry.visibleApertureHeightMillimeters = 90f;
        secondFrameMetadata.geometry.unitsPerMeter = 1f;
        File.WriteAllText(Path.Combine(directory, "frame_000001_qs8x6a0.75.json"), JsonUtility.ToJson(secondFrameMetadata));
        ExpectInvalid(() => QuiltPlaybackSource.Open(directory), "recording sequence rejects changes to physical aperture geometry");
        secondFrameMetadata.geometry.visibleApertureWidthMillimeters = 0f;
        File.WriteAllText(Path.Combine(directory, "frame_000001_qs8x6a0.75.json"), JsonUtility.ToJson(secondFrameMetadata));
        ExpectInvalid(() => QuiltPlaybackSource.Open(directory), "enabled physical framing rejects unset metadata dimensions");
        secondFrameMetadata.geometry.physicalFramingEnabled = false;
        secondFrameMetadata.geometry.visibleApertureWidthMillimeters = 0f;
        secondFrameMetadata.geometry.visibleApertureHeightMillimeters = 0f;
        secondFrameMetadata.geometry.unitsPerMeter = 1f;
        File.WriteAllText(Path.Combine(directory, "frame_000001_qs8x6a0.75.json"), JsonUtility.ToJson(secondFrameMetadata));

        string validFirstLine = lines[0];
        lines[0] = JsonUtility.ToJson(new QuiltRecordingFrame { index = -1, fileName = "frame_000000_qs8x6a0.75.png" });
        File.WriteAllText(Path.Combine(directory, "frames.jsonl"), string.Join("\n", lines) + "\n");
        ExpectInvalid(() => QuiltPlaybackSource.Open(directory), "negative logical frame index is rejected");
        lines[0] = validFirstLine;
        File.WriteAllText(Path.Combine(directory, "frames.jsonl"), string.Join("\n", lines) + "\n");

        QuiltRecordingManifest unfinished = new QuiltRecordingManifest { status = "recording", fps = 30, requestedFrameCount = 3, completedFrameCount = 3, frameLog = "frames.jsonl" };
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonUtility.ToJson(unfinished));
        ExpectInvalid(() => QuiltPlaybackSource.Open(directory), "unfinished recording is rejected");

        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonUtility.ToJson(new QuiltRecordingManifest
        {
            fps = 30,
            requestedFrameCount = 3,
            completedFrameCount = 3,
            status = "complete",
            finishedAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            frameLog = "frames.jsonl"
        }));
        lines[1] = JsonUtility.ToJson(new QuiltRecordingFrame { index = 1, fileName = "../outside.png" });
        File.WriteAllText(Path.Combine(directory, "frames.jsonl"), string.Join("\n", lines) + "\n");
        ExpectInvalid(() => QuiltPlaybackSource.Open(directory), "frame path traversal is rejected");
    }

    static void TestCursor()
    {
        var cursor = new QuiltPlaybackCursor(10, 4);
        QuiltPlaybackStep first = cursor.Advance(0d);
        Require(first.HasFrame && first.FrameIndex == 0, "playback starts on frame zero");
        cursor.Pause();
        QuiltPlaybackStep paused = cursor.Advance(1d);
        Require(!paused.HasFrame && !paused.IsEndOfStream, "pause holds the displayed image");
        cursor.Resume();
        QuiltPlaybackStep skipped = cursor.Advance(0.25d);
        Require(skipped.HasFrame && skipped.FrameIndex == 2 && skipped.SkippedFrameCount == 1, "late playback advances to real time and counts skipped frames");
        QuiltPlaybackStep last = cursor.Advance(0.1d);
        Require(last.HasFrame && last.FrameIndex == 3, "playback displays the final frame");
        QuiltPlaybackStep eof = cursor.Advance(0.05d);
        Require(eof.IsEndOfStream && cursor.IsComplete, "playback reports end of stream after the final frame duration");

        var boundary = new QuiltPlaybackCursor(10, 4);
        boundary.Advance(0d);
        boundary.Advance(0.25d);
        boundary.Advance(0.1d);
        Require(!boundary.Advance(0.05d - 1e-8).IsEndOfStream, "playback does not reach EOF before the final frame boundary");
        Require(boundary.Advance(1e-8).IsEndOfStream, "playback absorbs sub-nanoframe accumulation error at the final boundary");

        var delayed = new QuiltPlaybackCursor(30, 48);
        QuiltPlaybackStep delayedLast = delayed.Advance(double.MaxValue);
        Require(delayedLast.HasFrame && delayedLast.FrameIndex == 47, "very large delay clamps to the last frame without integer overflow");
        Require(delayed.Advance(0d).IsEndOfStream, "very large delay reaches EOF on the next poll");
    }

    static void TestRendererOperationGuard()
    {
        var root = new GameObject("QuiltPlaybackOperationGuardSmoke");
        try
        {
            QuiltRenderer renderer = root.AddComponent<QuiltRenderer>();
            QuiltRendererOperationLease recording;
            string error;
            Require(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Recording, out recording, out error), "recording operation acquires renderer ownership");
            QuiltRendererOperationLease conflicting;
            Require(!QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback, out conflicting, out error) && conflicting == null, "playback cannot take a recorder-owned renderer");
            Require(QuiltRendererOperationLease.IsLeased(renderer), "renderer ownership is visible while active");
            recording.Dispose();
            Require(!QuiltRendererOperationLease.IsLeased(renderer), "disposing operation lease releases renderer ownership");
            Require(QuiltRendererOperationLease.TryAcquire(renderer, QuiltRendererOperationKind.Playback, out conflicting, out error), "playback acquires renderer after recording releases it");
            conflicting.Dispose();
        }
        finally { UnityEngine.Object.DestroyImmediate(root); }
    }

    static QuiltCaptureMetadata CreateMetadata()
    {
        return new QuiltCaptureMetadata
        {
            width = Width,
            height = Height,
            rows = Rows,
            columns = Columns,
            views = Rows * Columns,
            viewRenderMode = "Full",
            lastRenderedViewCount = Rows * Columns,
            usedRect = new QuiltCaptureRect { x = 0, y = 0, width = 96, height = 54 },
            geometry = new QuiltCaptureGeometry
            {
                viewAspect = 0.75f,
                fieldOfView = 45f,
                cameraDistance = 3f,
                viewCone = 40f,
                nearClip = 0.1f,
                farClip = 100f,
                cameraRotationW = 1f
            },
            timestampUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
        };
    }

    static void WritePatternPng(string path)
    {
        const int channels = 4;
        var raw = new byte[Height * (1 + Width * channels)];
        for (int y = 0; y < Height; ++y)
        {
            int row = y * (1 + Width * channels);
            raw[row] = 0;
            for (int x = 0; x < Width; ++x)
            {
                Color32 color;
                if (x >= 96 || y == 0) color = new Color32(255, 0, 255, 255);
                else color = TileColor(((Height - 1 - y) / 9) * Columns + x / 12);
                int offset = row + 1 + x * channels;
                raw[offset] = color.r;
                raw[offset + 1] = color.g;
                raw[offset + 2] = color.b;
                raw[offset + 3] = color.a;
            }
        }

        byte[] zlib = CreateUncompressedZlib(raw);
        using (var stream = File.Create(path))
        {
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            stream.Write(signature, 0, signature.Length);
            var ihdr = new byte[13];
            WriteUInt32(ihdr, 0, Width);
            WriteUInt32(ihdr, 4, Height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(stream, "IHDR", ihdr);
            WriteChunk(stream, "IDAT", zlib);
            WriteChunk(stream, "IEND", new byte[0]);
        }
    }

    static byte[] CreateUncompressedZlib(byte[] input)
    {
        using (var stream = new MemoryStream())
        {
            stream.WriteByte(0x78);
            stream.WriteByte(0x01);
            int offset = 0;
            while (offset < input.Length)
            {
                int count = Math.Min(65535, input.Length - offset);
                bool final = offset + count == input.Length;
                stream.WriteByte(final ? (byte)1 : (byte)0);
                stream.WriteByte((byte)count);
                stream.WriteByte((byte)(count >> 8));
                int inverse = ~count & 0xffff;
                stream.WriteByte((byte)inverse);
                stream.WriteByte((byte)(inverse >> 8));
                stream.Write(input, offset, count);
                offset += count;
            }

            uint a = 1;
            uint b = 0;
            for (int i = 0; i < input.Length; ++i)
            {
                a = (a + input[i]) % 65521;
                b = (b + a) % 65521;
            }
            WriteUInt32(stream, (b << 16) | a);
            return stream.ToArray();
        }
    }

    static void WriteChunk(Stream stream, string type, byte[] data)
    {
        WriteUInt32(stream, (uint)data.Length);
        byte[] name = Encoding.ASCII.GetBytes(type);
        stream.Write(name, 0, name.Length);
        stream.Write(data, 0, data.Length);
        uint crc = 0xffffffff;
        for (int i = 0; i < name.Length + data.Length; ++i)
        {
            byte value = i < name.Length ? name[i] : data[i - name.Length];
            crc ^= value;
            for (int bit = 0; bit < 8; ++bit) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xedb88320u : crc >> 1;
        }
        WriteUInt32(stream, ~crc);
    }

    static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    static void WriteUInt32(byte[] bytes, int offset, int value)
    {
        uint unsigned = (uint)value;
        bytes[offset] = (byte)(unsigned >> 24);
        bytes[offset + 1] = (byte)(unsigned >> 16);
        bytes[offset + 2] = (byte)(unsigned >> 8);
        bytes[offset + 3] = (byte)unsigned;
    }

    static Color32 TileColor(int index)
    {
        return new Color32((byte)(index + 1), (byte)(index * 3 + 2), (byte)(index * 5 + 3), 255);
    }

    static void CheckPixel(Color32[] pixels, int x, int y, Color32 expected, string label)
    {
        Color32 actual = pixels[y * Width + x];
        Require(actual.r == expected.r && actual.g == expected.g && actual.b == expected.b && actual.a == expected.a, label);
    }

    static void ExpectInvalid(Action action, string label)
    {
        bool rejected = false;
        try { action(); }
        catch (Exception error) { rejected = error is InvalidDataException || error is FileNotFoundException || error is ArgumentException; }
        Require(rejected, label);
    }

    static void Require(bool value, string label)
    {
        if (!value) throw new Exception("FAIL: " + label);
    }
}
