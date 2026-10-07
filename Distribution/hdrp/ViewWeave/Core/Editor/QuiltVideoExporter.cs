using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace ViewWeave.Editor
{
    public interface IQuiltVideoProcess : IDisposable
    {
        bool HasExited { get; }
        bool IsStarted { get; }
        bool IsOutputComplete { get; }
        int ExitCode { get; }
        int ProcessId { get; }
        string StandardOutput { get; }
        string StandardError { get; }
        void Start(string executable, string[] arguments);
        void Kill();
        void WaitForExit(int milliseconds);
    }

    sealed class QuiltVideoSystemProcess : IQuiltVideoProcess
    {
        readonly StringBuilder stdout = new StringBuilder();
        readonly StringBuilder stderr = new StringBuilder();
        readonly object outputLock = new object();
        Process process;
        volatile bool outputComplete;
        volatile bool errorComplete;
        bool started;
        public bool HasExited => process == null || !started || process.HasExited;
        public bool IsStarted => started;
        public bool IsOutputComplete => !started || outputComplete && errorComplete;
        public int ExitCode => process.ExitCode;
        public int ProcessId => process == null ? -1 : process.Id;
        public string StandardOutput { get { lock (outputLock) return stdout.ToString(); } }
        public string StandardError { get { lock (outputLock) return stderr.ToString(); } }

        public void Start(string executable, string[] arguments)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = QuiltVideoExporter.BuildWindowsArgumentString(arguments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            outputComplete = false;
            errorComplete = false;
            process.OutputDataReceived += OnOutput;
            process.ErrorDataReceived += OnError;
            if (!process.Start()) throw new InvalidOperationException("Could not start " + Path.GetFileName(executable) + ".");
            started = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        void OnOutput(object sender, DataReceivedEventArgs args) { if (args.Data == null) { outputComplete = true; return; } lock (outputLock) AppendBounded(stdout, args.Data); }
        void OnError(object sender, DataReceivedEventArgs args) { if (args.Data == null) { errorComplete = true; return; } lock (outputLock) AppendBounded(stderr, args.Data); }
        static void AppendBounded(StringBuilder target, string line)
        {
            target.AppendLine(line);
            const int maximum = 65536;
            if (target.Length > maximum) target.Remove(0, target.Length - maximum);
        }
        public void Kill() { if (process != null && started && !process.HasExited) process.Kill(); }
        public void WaitForExit(int milliseconds) { if (process != null) process.WaitForExit(milliseconds); }
        public void Dispose()
        {
            if (process == null) return;
            try { process.OutputDataReceived -= OnOutput; process.ErrorDataReceived -= OnError; } catch { }
            process.Dispose();
            process = null;
        }
    }

    public sealed class QuiltVideoSequenceInfo
    {
        public string DirectoryPath;
        public string ManifestPath;
        public string FramePattern;
        public string ManifestSha256;
        public int Width;
        public int Height;
        public int Fps;
        public int FrameCount;
        public int Columns;
        public int Rows;
        public int Views;
        public float ViewAspect;
        public string ViewRenderMode;
        public int LastRenderedViewCount;
        public int UsedX;
        public int UsedY;
        public int UsedWidth;
        public int UsedHeight;
    }

    [Serializable]
    sealed class QuiltVideoExportSidecar
    {
        public int frames;
        public int effectiveFps;
        public string codec;
        public string pixelFormat;
        public string sourceManifestSha256;
        public int width;
        public int height;
        public int columns;
        public int rows;
        public int views;
        public float viewAspect;
        public string viewRenderMode;
        public int lastRenderedViewCount;
        public QuiltCaptureRect usedRect;
        public string orientation;
        public string compression;
    }

    [Serializable]
    sealed class QuiltVideoFailureDiagnostic
    {
        public string stage;
        public bool exitCodeKnown;
        public int exitCode;
        public int ownedProcessId;
        public string temporaryPath;
        public string reason;
        public string createdAtUtc;
    }

    [Serializable]
    sealed class FfprobeDocument
    {
        public FfprobeStream[] streams;
    }

    [Serializable]
    sealed class FfprobeStream
    {
        public int width;
        public int height;
        public string avg_frame_rate;
        public string nb_read_frames;
        public string codec_name;
        public string pix_fmt;
    }

    public static class QuiltVideoExporter
    {
        static readonly Regex frameNamePattern = new Regex(@"^frame_(\d{6,})_qs(\d+)x(\d+)a([0-9]+(?:\.[0-9]+)?)\.png$", RegexOptions.CultureInvariant);
        static readonly Regex outputNamePattern = new Regex(@"_qs(\d+)x(\d+)a([0-9]+(?:\.[0-9]+)?)$", RegexOptions.CultureInvariant);
        static QuiltVideoExportJob activeJob;
        static Func<IQuiltVideoProcess> processFactory = () => new QuiltVideoSystemProcess();

        public static QuiltVideoExportJob ActiveJob => activeJob;

        public static QuiltVideoSequenceInfo ValidateSequence(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Select a recording folder.");
            string fullDirectory = Path.GetFullPath(directory);
            string manifestPath = Path.Combine(fullDirectory, "manifest.json");
            string framesPath = Path.Combine(fullDirectory, "frames.jsonl");
            if (!File.Exists(manifestPath) || !File.Exists(framesPath)) throw new InvalidDataException("The folder must contain manifest.json and frames.jsonl.");

            QuiltRecordingManifest manifest = JsonUtility.FromJson<QuiltRecordingManifest>(File.ReadAllText(manifestPath, Encoding.UTF8));
            if (manifest == null || !string.Equals(manifest.status, "Complete", StringComparison.OrdinalIgnoreCase) && !string.Equals(manifest.status, "complete", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The recording manifest is not complete.");
            if (manifest.fps < 1 || manifest.fps > 240) throw new InvalidDataException("Recording FPS must be between 1 and 240.");
            if (manifest.requestedFrameCount <= 0 || manifest.completedFrameCount != manifest.requestedFrameCount)
                throw new InvalidDataException("The recording must contain every requested frame.");
            if (!string.Equals(manifest.frameLog, "frames.jsonl", StringComparison.Ordinal)) throw new InvalidDataException("The manifest does not point to the supported frames.jsonl log.");

            string[] lines = File.ReadAllLines(framesPath, Encoding.UTF8);
            if (lines.Length != manifest.completedFrameCount) throw new InvalidDataException("Frame log count does not match the manifest.");
            QuiltVideoSequenceInfo info = new QuiltVideoSequenceInfo
            {
                DirectoryPath = fullDirectory,
                ManifestPath = manifestPath,
                ManifestSha256 = ComputeSha256(manifestPath),
                Fps = manifest.fps,
                FrameCount = manifest.completedFrameCount
            };

            QuiltCaptureMetadata reference = null;
            string commonSuffix = null;
            int digits = 0;
            for (int i = 0; i < lines.Length; ++i)
            {
                if (string.IsNullOrWhiteSpace(lines[i])) throw new InvalidDataException("Frame log contains an empty entry.");
                QuiltRecordingFrame frame = JsonUtility.FromJson<QuiltRecordingFrame>(lines[i]);
                if (frame == null || frame.index != i) throw new InvalidDataException("Frame indexes must be consecutive and start at zero.");
                if (!IsFinite(frame.simulationTime) || !IsFinite(frame.deltaTime) || frame.deltaTime < 0f) throw new InvalidDataException("Frame timing values must be finite.");
                string fileName = frame.fileName;
                if (string.IsNullOrEmpty(fileName) || Path.GetFileName(fileName) != fileName || fileName.IndexOf('/') >= 0 || fileName.IndexOf('\\') >= 0)
                    throw new InvalidDataException("Frame filenames must be simple relative leaf names.");
                Match match = frameNamePattern.Match(fileName);
                if (!match.Success || int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) != i)
                    throw new InvalidDataException("Frame filenames must use the consecutive frame_######_qs...png pattern.");
                int frameColumns = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                int frameRows = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                string suffix = fileName.Substring(match.Groups[1].Length + 6);
                if (i == 0)
                {
                    digits = match.Groups[1].Length;
                    commonSuffix = suffix;
                    info.Columns = frameColumns;
                    info.Rows = frameRows;
                    info.FramePattern = "frame_%0" + digits.ToString(CultureInfo.InvariantCulture) + "d" + commonSuffix;
                }
                else if (match.Groups[1].Length != digits || suffix != commonSuffix || frameColumns != info.Columns || frameRows != info.Rows)
                    throw new InvalidDataException("Every frame must use the same PNG filename pattern and quilt layout.");

                string pngPath = Path.Combine(fullDirectory, fileName);
                string jsonPath = Path.ChangeExtension(pngPath, ".json");
                if (!File.Exists(pngPath) || !File.Exists(jsonPath)) throw new InvalidDataException("A frame PNG or its metadata sidecar is missing.");
                int pngWidth;
                int pngHeight;
                ReadPngDimensions(pngPath, out pngWidth, out pngHeight);
                QuiltCaptureMetadata metadata = JsonUtility.FromJson<QuiltCaptureMetadata>(File.ReadAllText(jsonPath, Encoding.UTF8));
                ValidateMetadata(metadata, pngWidth, pngHeight, frameColumns, frameRows);
                double taggedAspect = double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                if (Math.Abs(taggedAspect - metadata.geometry.viewAspect) > 0.00001d) throw new InvalidDataException("Frame filename aspect does not match its sidecar geometry.");
                if (reference == null)
                {
                    reference = metadata;
                    info.Width = pngWidth;
                    info.Height = pngHeight;
                    info.Views = metadata.views;
                    info.ViewAspect = metadata.geometry.viewAspect;
                    info.ViewRenderMode = metadata.viewRenderMode;
                    info.LastRenderedViewCount = metadata.lastRenderedViewCount;
                    info.UsedX = metadata.usedRect.x;
                    info.UsedY = metadata.usedRect.y;
                    info.UsedWidth = metadata.usedRect.width;
                    info.UsedHeight = metadata.usedRect.height;
                }
                else
                {
                    if (pngWidth != info.Width || pngHeight != info.Height) throw new InvalidDataException("All quilt frames must have matching PNG dimensions.");
                    ValidateMetadataMatch(reference, metadata);
                }
            }
            if ((info.Width & 1) != 0 || (info.Height & 1) != 0) throw new InvalidDataException("H.264 yuv420p requires even quilt width and height; automatic resizing is disabled.");
            return info;
        }

        static void ValidateMetadata(QuiltCaptureMetadata metadata, int pngWidth, int pngHeight, int columns, int rows)
        {
            if (metadata == null || metadata.usedRect == null) throw new InvalidDataException("Frame metadata is not a full quilt sidecar. Central-view captures cannot be exported as quilts.");
            if (metadata.width != pngWidth || metadata.height != pngHeight || metadata.columns != columns || metadata.rows != rows || metadata.views <= 0 || metadata.views != (long)columns * rows || metadata.lastRenderedViewCount <= 0 || string.IsNullOrEmpty(metadata.viewRenderMode))
                throw new InvalidDataException("Frame sidecar layout does not match its PNG and filename.");
            if (metadata.usedRect.x < 0 || metadata.usedRect.y < 0 || metadata.usedRect.width <= 0 || metadata.usedRect.height <= 0 || (long)metadata.usedRect.x + metadata.usedRect.width > pngWidth || (long)metadata.usedRect.y + metadata.usedRect.height > pngHeight)
                throw new InvalidDataException("Frame sidecar contains an invalid used rectangle.");
            QuiltCaptureGeometry g = metadata.geometry;
            if (!IsFinite(g.viewAspect) || !IsFinite(g.fieldOfView) || !IsFinite(g.cameraDistance) || !IsFinite(g.viewCone) || !IsFinite(g.nearClip) || !IsFinite(g.farClip) ||
                !IsFinite(g.cameraPositionX) || !IsFinite(g.cameraPositionY) || !IsFinite(g.cameraPositionZ) || !IsFinite(g.cameraRotationX) || !IsFinite(g.cameraRotationY) || !IsFinite(g.cameraRotationZ) || !IsFinite(g.cameraRotationW))
                throw new InvalidDataException("Frame sidecar camera geometry must contain finite values.");
            if (g.viewAspect <= 0f || g.fieldOfView <= 0f || g.fieldOfView >= 180f || g.cameraDistance <= 0f || g.viewCone < 0f || g.nearClip <= 0f || g.farClip <= g.nearClip)
                throw new InvalidDataException("Frame sidecar camera geometry is outside supported ranges.");
        }

        static void ValidateMetadataMatch(QuiltCaptureMetadata expected, QuiltCaptureMetadata actual)
        {
            QuiltCaptureGeometry a = expected.geometry;
            QuiltCaptureGeometry b = actual.geometry;
            if (expected.width != actual.width || expected.height != actual.height || expected.rows != actual.rows || expected.columns != actual.columns || expected.views != actual.views ||
                expected.viewRenderMode != actual.viewRenderMode || expected.lastRenderedViewCount != actual.lastRenderedViewCount || expected.usedRect.x != actual.usedRect.x || expected.usedRect.y != actual.usedRect.y || expected.usedRect.width != actual.usedRect.width || expected.usedRect.height != actual.usedRect.height ||
                a.viewAspect != b.viewAspect || a.fieldOfView != b.fieldOfView || a.cameraDistance != b.cameraDistance || a.viewCone != b.viewCone || a.nearClip != b.nearClip || a.farClip != b.farClip)
                throw new InvalidDataException("Frame metadata contains mixed quilt layout or camera settings.");
        }

        static void ReadPngDimensions(string path, out int width, out int height)
        {
            byte[] header = new byte[24];
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Read(header, 0, header.Length) != header.Length) throw new InvalidDataException("A frame PNG header is truncated.");
            }
            byte[] signature = { 137, 80, 78, 71, 13, 10, 26, 10 };
            for (int i = 0; i < signature.Length; ++i) if (header[i] != signature[i]) throw new InvalidDataException("A frame has an invalid PNG signature.");
            if (header[12] != 73 || header[13] != 72 || header[14] != 68 || header[15] != 82) throw new InvalidDataException("A frame PNG has no IHDR chunk.");
            width = ReadBigEndianInt32(header, 16);
            height = ReadBigEndianInt32(header, 20);
            if (width <= 0 || height <= 0) throw new InvalidDataException("A frame PNG has invalid dimensions.");
        }

        static int ReadBigEndianInt32(byte[] bytes, int offset) => (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        static bool IsFinite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        static string ComputeSha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                byte[] hash = sha.ComputeHash(stream);
                StringBuilder result = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; ++i) result.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        public static string QuoteWindowsArgument(string value)
        {
            if (value == null) throw new ArgumentNullException(nameof(value));
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return value;
            StringBuilder quoted = new StringBuilder(value.Length + 2).Append('"');
            int slashes = 0;
            for (int i = 0; i < value.Length; ++i)
            {
                char c = value[i];
                if (c == '\\') { ++slashes; continue; }
                if (c == '"') quoted.Append('\\', slashes * 2 + 1).Append('"');
                else quoted.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            quoted.Append('\\', slashes * 2).Append('"');
            return quoted.ToString();
        }

        public static string BuildWindowsArgumentString(IList<string> arguments)
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < arguments.Count; ++i)
            {
                if (i > 0) result.Append(' ');
                result.Append(QuoteWindowsArgument(arguments[i]));
            }
            return result.ToString();
        }

        public static string RecommendedOutputFileName(QuiltVideoSequenceInfo info)
        {
            if (info == null || info.Columns <= 0 || info.Rows <= 0 || !IsFinite(info.ViewAspect) || info.ViewAspect <= 0f) throw new ArgumentException("A validated quilt sequence is required.");
            return "quilt_qs" + info.Columns.ToString(CultureInfo.InvariantCulture) + "x" + info.Rows.ToString(CultureInfo.InvariantCulture) + "a" + info.ViewAspect.ToString("0.######", CultureInfo.InvariantCulture) + ".mp4";
        }

        public static string GetOutputNameGuidance(string outputPath, QuiltVideoSequenceInfo info)
        {
            string recommended = RecommendedOutputFileName(info);
            string name = Path.GetFileNameWithoutExtension(outputPath ?? string.Empty);
            Match match = outputNamePattern.Match(name);
            if (!match.Success) return "For Looking Glass Studio auto-detection, use " + recommended + ". An untagged MP4 may need its quilt layout set manually.";
            int columns = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int rows = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            double aspect = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            if (columns != info.Columns || rows != info.Rows || Math.Abs(aspect - info.ViewAspect) > 0.000001d)
                throw new InvalidDataException("Output filename quilt tag does not match the source sequence layout.");
            return string.Empty;
        }

        public static QuiltVideoExportJob Start(string recordingDirectory, string outputPath, string ffmpegPath, string ffprobePath)
        {
            if (activeJob != null && !activeJob.IsFinished) throw new InvalidOperationException("A quilt video export is already running.");
            QuiltVideoSequenceInfo info = ValidateSequence(recordingDirectory);
            string finalPath = Path.GetFullPath(outputPath);
            GetOutputNameGuidance(finalPath, info);
            if (!string.Equals(Path.GetExtension(finalPath), ".mp4", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Choose an output path ending in .mp4.");
            string sidecarPath = Path.ChangeExtension(finalPath, ".json");
            if (File.Exists(finalPath) || File.Exists(sidecarPath)) throw new IOException("The output MP4 or its metadata sidecar already exists.");
            string outputDirectory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrEmpty(outputDirectory)) throw new ArgumentException("The output directory is invalid.");
            Directory.CreateDirectory(outputDirectory);
            activeJob = new QuiltVideoExportJob(info, finalPath, sidecarPath, ffmpegPath, ffprobePath);
            activeJob.Start();
            return activeJob;
        }

        internal static void SetActiveJob(QuiltVideoExportJob job) => activeJob = job;
        public static void SetProcessFactoryForTests(Func<IQuiltVideoProcess> factory)
        {
            if (activeJob != null && !activeJob.IsFinished) throw new InvalidOperationException("Cannot replace the process factory while an export is running.");
            processFactory = factory ?? (() => new QuiltVideoSystemProcess());
        }
        internal static IQuiltVideoProcess CreateProcess() => processFactory();
    }

    public sealed class QuiltVideoExportJob
    {
        const double TimeoutSeconds = 1800d;
        readonly QuiltVideoSequenceInfo sequence;
        readonly string finalPath;
        readonly string sidecarPath;
        readonly string ffmpegPath;
        readonly string ffprobePath;
        readonly string temporaryPath;
        readonly string temporarySidecarPath;
        IQuiltVideoProcess process;
        double startedAt;
        double exitObservedAt = -1d;
        const double OutputDrainTimeoutSeconds = 10d;
        bool probing;
        bool cancelling;
        bool committed;
        int ownedProcessId = -1;
        int lastExitCode;
        bool lastExitCodeKnown;
        bool diagnosticWritten;
        bool pendingWarningWritten;
        bool killAttempted;
        double lastKillAttemptAt = -1d;
        double stopRequestedAt = -1d;

        public bool IsFinished { get; private set; }
        public bool Succeeded { get; private set; }
        public string Status { get; private set; } = "Preparing";
        public string Error { get; private set; } = string.Empty;
        public float Progress { get; private set; }
        public string LastDiagnosticPath { get; private set; } = string.Empty;
        public string TemporaryPathForTests => temporaryPath;
        public int OwnedProcessIdForTests => ownedProcessId;

        internal QuiltVideoExportJob(QuiltVideoSequenceInfo info, string output, string sidecar, string ffmpeg, string ffprobe)
        {
            sequence = info;
            finalPath = output;
            sidecarPath = sidecar;
            ffmpegPath = string.IsNullOrWhiteSpace(ffmpeg) ? "ffmpeg" : ffmpeg.Trim();
            ffprobePath = string.IsNullOrWhiteSpace(ffprobe) ? "ffprobe" : ffprobe.Trim();
            temporaryPath = Path.Combine(Path.GetDirectoryName(output), "." + Path.GetFileNameWithoutExtension(output) + "." + Guid.NewGuid().ToString("N") + ".tmp.mp4");
            temporarySidecarPath = sidecar + "." + Guid.NewGuid().ToString("N") + ".tmp";
        }

        internal void Start()
        {
            try
            {
                EditorApplication.update += Poll;
                AssemblyReloadEvents.beforeAssemblyReload += CancelForLifecycle;
                EditorApplication.quitting += CancelForLifecycle;
                Launch(ffmpegPath, BuildFfmpegArguments());
                Status = "Encoding";
            }
            catch (Exception exception) { FinishFailure(exception.Message); }
        }

        string[] BuildFfmpegArguments()
        {
            string inputPattern = Path.Combine(sequence.DirectoryPath, sequence.FramePattern);
            return new[] { "-n", "-nostdin", "-hide_banner", "-loglevel", "warning", "-progress", "pipe:1", "-nostats", "-framerate", sequence.Fps.ToString(CultureInfo.InvariantCulture), "-start_number", "0", "-i", inputPattern, "-an", "-frames:v", sequence.FrameCount.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart", temporaryPath };
        }

        void Launch(string executable, string[] arguments)
        {
            ownedProcessId = -1;
            lastExitCode = 0;
            lastExitCodeKnown = false;
            diagnosticWritten = false;
            pendingWarningWritten = false;
            killAttempted = false;
            lastKillAttemptAt = -1d;
            exitObservedAt = -1d;
            process = QuiltVideoExporter.CreateProcess();
            process.Start(executable, arguments);
            try { ownedProcessId = process.ProcessId; } catch { ownedProcessId = -1; }
            startedAt = EditorApplication.timeSinceStartup;
            exitObservedAt = -1d;
            killAttempted = false;
        }

        public void Poll() => PollAt(EditorApplication.timeSinceStartup);

        public void PollAtForTests(double now) => PollAt(now);

        void PollAt(double now)
        {
            if (IsFinished) return;
            if (process == null) return;
            try
            {
                if (!SafeIsStarted(process))
                {
                    FinishFailure("The owned process could not be started.");
                    return;
                }
                if (!SafeHasExited(process))
                {
                    UpdateProgressFromOutput();
                    if (cancelling)
                    {
                        KillOwnedProcess();
                        if (!pendingWarningWritten && stopRequestedAt >= 0d && now - stopRequestedAt > 10d)
                        {
                            pendingWarningWritten = true;
                            WriteFailureDiagnostic(Error);
                            LogPendingProcess("owned process did not exit after the stop request; temporary output is retained");
                        }
                    }
                    if (now - startedAt > TimeoutSeconds) CancelWithError("The owned encoder process timed out.", now);
                    return;
                }
                if (!SafeOutputComplete(process))
                {
                    if (exitObservedAt < 0d) exitObservedAt = now;
                    if (now - exitObservedAt <= OutputDrainTimeoutSeconds) return;
                    lastExitCodeKnown = TryGetExitCode(process, out lastExitCode);
                    ReadAndDisposeProcess();
                    diagnosticWritten = false;
                    FinishFailure("Process exited, but redirected output did not finish draining before the deadline.");
                    return;
                }
                lastExitCodeKnown = TryGetExitCode(process, out lastExitCode);
                process.WaitForExit(0);
                int exitCode = lastExitCode;
                string output = ReadAndDisposeProcess();
                diagnosticWritten = false;
                if (cancelling) { FinishFailure(Error.Length == 0 ? "Export cancelled." : Error); return; }
                if (!lastExitCodeKnown) { FinishFailure("Process exited, but its exit code could not be read."); return; }
                if (exitCode != 0) { FinishFailure("Process failed with exit code " + exitCode.ToString(CultureInfo.InvariantCulture) + "."); return; }
                if (!probing)
                {
                    if (!File.Exists(temporaryPath)) { FinishFailure("Encoder exited successfully but did not create an MP4."); return; }
                    probing = true;
                    Status = "Verifying encoded video";
                    string[] probeArgs = { "-v", "error", "-select_streams", "v:0", "-count_frames", "-show_entries", "stream=width,height,avg_frame_rate,nb_read_frames,codec_name,pix_fmt", "-of", "json", temporaryPath };
                    Launch(ffprobePath, probeArgs);
                    return;
                }
                VerifyProbe(output);
                Commit();
                Succeeded = true;
                Progress = 1f;
                Status = "Complete";
                Finish();
            }
            catch (Exception exception) { FinishFailure(exception.Message); }
        }

        void UpdateProgressFromOutput()
        {
            string[] lines = process.StandardOutput.Split('\n');
            for (int i = lines.Length - 1; i >= 0; --i)
            {
                string line = lines[i].Trim();
                if (!line.StartsWith("frame=", StringComparison.Ordinal)) continue;
                int value;
                if (int.TryParse(line.Substring(6).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                {
                    double fraction = Math.Max(0d, Math.Min(1d, (double)value / sequence.FrameCount));
                    Progress = (float)fraction * (probing ? 0.05f : 0.9f);
                }
                break;
            }
        }

        string ReadAndDisposeProcess()
        {
            string result = process.StandardOutput;
            process.Dispose();
            process = null;
            return result;
        }

        void VerifyProbe(string output)
        {
            FfprobeDocument document = JsonUtility.FromJson<FfprobeDocument>(output);
            if (document == null || document.streams == null || document.streams.Length == 0) throw new InvalidDataException("ffprobe returned no video stream metadata.");
            FfprobeStream stream = document.streams[0];
            int frames;
            if (stream.width != sequence.Width || stream.height != sequence.Height || !string.Equals(stream.codec_name, "h264", StringComparison.OrdinalIgnoreCase) || !string.Equals(stream.pix_fmt, "yuv420p", StringComparison.Ordinal) || !int.TryParse(stream.nb_read_frames, NumberStyles.Integer, CultureInfo.InvariantCulture, out frames) || frames != sequence.FrameCount || !FrameRateMatches(stream.avg_frame_rate, sequence.Fps))
                throw new InvalidDataException("ffprobe output does not match the requested dimensions, frame rate, frame count, H.264 codec, and yuv420p format.");
        }

        static bool FrameRateMatches(string value, int fps)
        {
            if (string.IsNullOrEmpty(value)) return false;
            string[] parts = value.Split('/');
            double numerator;
            double denominator;
            if (parts.Length != 2 || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out numerator) || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out denominator) || denominator == 0d) return false;
            return Math.Abs(numerator / denominator - fps) < 0.0001d;
        }

        void Commit()
        {
            if (File.Exists(finalPath) || File.Exists(sidecarPath)) throw new IOException("The output MP4 or metadata sidecar appeared during export.");
            QuiltVideoExportSidecar metadata = new QuiltVideoExportSidecar
            {
                frames = sequence.FrameCount,
                effectiveFps = sequence.Fps,
                codec = "h264",
                pixelFormat = "yuv420p",
                sourceManifestSha256 = sequence.ManifestSha256,
                width = sequence.Width,
                height = sequence.Height,
                columns = sequence.Columns,
                rows = sequence.Rows,
                views = sequence.Views,
                viewAspect = sequence.ViewAspect,
                viewRenderMode = sequence.ViewRenderMode,
                lastRenderedViewCount = sequence.LastRenderedViewCount,
                usedRect = new QuiltCaptureRect { x = sequence.UsedX, y = sequence.UsedY, width = sequence.UsedWidth, height = sequence.UsedHeight },
                orientation = "bottom-left-view0-row-major",
                compression = "Lossy H.264 CRF 18. Pixel-identical output is not guaranteed."
            };
            File.WriteAllText(temporarySidecarPath, JsonUtility.ToJson(metadata, true) + "\n", new UTF8Encoding(false));
            File.Move(temporaryPath, finalPath);
            try { File.Move(temporarySidecarPath, sidecarPath); }
            catch { TryDelete(finalPath); throw; }
            committed = true;
        }

        public void Cancel()
        {
            if (IsFinished || cancelling) return;
            cancelling = true;
            Error = "Export cancelled.";
            Status = "Cancelling";
            stopRequestedAt = EditorApplication.timeSinceStartup;
            KillOwnedProcess();
            if (process == null) FinishFailure(Error);
        }

        void CancelWithError(string message, double now)
        {
            if (IsFinished) return;
            if (cancelling) { KillOwnedProcess(); return; }
            cancelling = true;
            Error = message;
            Status = "Stopping encoder";
            stopRequestedAt = now;
            KillOwnedProcess();
        }

        void CancelForLifecycle()
        {
            if (IsFinished) return;
            if (!cancelling)
            {
                cancelling = true;
                Error = "Export cancelled because the Editor is shutting down or reloading.";
                stopRequestedAt = EditorApplication.timeSinceStartup;
            }
            KillOwnedProcess();
            if (process != null)
            {
                try { if (!SafeHasExited(process)) process.WaitForExit(3000); } catch { }
                if (SafeHasExited(process))
                {
                    if (SafeOutputComplete(process)) Poll();
                    else
                    {
                        WriteFailureDiagnostic(Error);
                        LogPendingProcess("output drain is pending during Editor reload or quit");
                    }
                }
                else
                {
                    WriteFailureDiagnostic(Error);
                    LogPendingProcess("owned process exit could not be confirmed during Editor reload or quit");
                }
            }
        }

        static bool SafeHasExited(IQuiltVideoProcess value) { try { return value != null && value.HasExited; } catch { return false; } }
        static bool SafeIsStarted(IQuiltVideoProcess value)
        {
            if (value == null) return false;
            try { return value.IsStarted; }
            catch { return true; }
        }
        static bool SafeOutputComplete(IQuiltVideoProcess value) { try { return value != null && value.IsOutputComplete; } catch { return false; } }
        static bool TryGetExitCode(IQuiltVideoProcess value, out int code)
        {
            try { code = value.ExitCode; return true; }
            catch { code = 0; return false; }
        }

        void KillOwnedProcess()
        {
            if (process == null || !SafeIsStarted(process)) return;
            if (SafeHasExited(process)) return;
            double now = EditorApplication.timeSinceStartup;
            if (killAttempted && now - lastKillAttemptAt < 1d) return;
            killAttempted = true;
            lastKillAttemptAt = now;
            try { process.Kill(); }
            catch { WriteFailureDiagnostic(Error.Length == 0 ? "Could not stop the owned process." : Error); }
        }

        void FinishFailure(string message)
        {
            if (process != null && !SafeIsStarted(process))
            {
                try { process.Dispose(); } catch { }
                process = null;
            }
            if (process != null && !SafeHasExited(process))
            {
                Error = SanitizeAscii(message);
                Status = "Stopping process";
                if (!cancelling) cancelling = true;
                if (stopRequestedAt < 0d) stopRequestedAt = EditorApplication.timeSinceStartup;
                KillOwnedProcess();
                WriteFailureDiagnostic(Error);
                return;
            }
            Error = SanitizeAscii(message);
            Status = "Failed: " + Error;
            WriteFailureDiagnostic(Error);
            TryDelete(temporaryPath);
            TryDelete(temporarySidecarPath);
            Finish();
        }

        void WriteFailureDiagnostic(string reason)
        {
            if (diagnosticWritten) return;
            diagnosticWritten = true;
            string path = Path.Combine(Path.GetDirectoryName(finalPath), Path.GetFileNameWithoutExtension(finalPath) + "." + Guid.NewGuid().ToString("N") + ".viewweave-failure.json");
            QuiltVideoFailureDiagnostic diagnostic = new QuiltVideoFailureDiagnostic
            {
                stage = probing ? "ffprobe" : "ffmpeg",
                exitCodeKnown = lastExitCodeKnown,
                exitCode = lastExitCode,
                ownedProcessId = ownedProcessId,
                temporaryPath = temporaryPath,
                reason = SanitizeAscii(reason),
                createdAtUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.WriteLine(JsonUtility.ToJson(diagnostic, true));
                LastDiagnosticPath = path;
            }
            catch { }
        }

        void LogPendingProcess(string reason)
        {
            string pid = ownedProcessId < 0 ? "unknown" : ownedProcessId.ToString(CultureInfo.InvariantCulture);
            UnityEngine.Debug.LogWarning("ViewWeave export " + SanitizeAscii(reason) + "; stage=" + (probing ? "ffprobe" : "ffmpeg") + "; pid=" + pid + "; temp=" + SanitizeAscii(temporaryPath));
        }

        void Finish()
        {
            if (process != null && !SafeIsStarted(process))
            {
                try { process.Dispose(); } catch { }
                process = null;
            }
            if (process != null && !SafeHasExited(process)) return;
            IsFinished = true;
            if (process != null)
            {
                try { ReadAndDisposeProcess(); }
                catch { try { process.Dispose(); } catch { } process = null; }
            }
            EditorApplication.update -= Poll;
            AssemblyReloadEvents.beforeAssemblyReload -= CancelForLifecycle;
            EditorApplication.quitting -= CancelForLifecycle;
            if (ReferenceEquals(QuiltVideoExporter.ActiveJob, this)) QuiltVideoExporter.SetActiveJob(null);
        }

        static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

        static string SanitizeAscii(string text)
        {
            if (string.IsNullOrEmpty(text)) return "Unknown export error.";
            StringBuilder result = new StringBuilder(Math.Min(text.Length, 1200));
            for (int i = 0; i < text.Length && result.Length < 1200; ++i)
            {
                char c = text[i];
                result.Append(c >= 32 && c <= 126 ? c : '?');
            }
            return result.ToString();
        }

        public void RequestLifecycleCancellationForTests() => CancelForLifecycle();
    }
}
