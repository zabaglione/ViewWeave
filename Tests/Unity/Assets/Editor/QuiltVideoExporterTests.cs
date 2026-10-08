using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using ViewWeave;
using ViewWeave.Editor;

public static class QuiltVideoExporterTests
{
    sealed class FakeProcess : IQuiltVideoProcess
    {
        readonly FakeConfiguration configuration;
        bool hasExited;
        bool outputComplete;
        public bool IsStarted { get; private set; }
        public bool HasExited { get { if (configuration.ThrowHasExited) throw new InvalidOperationException("fake exit state unavailable"); return hasExited; } }
        public bool IsOutputComplete => configuration.StallOutputDrain ? false : outputComplete;
        public int ExitCode { get; private set; }
        public int ProcessId => 4242;
        public string StandardOutput { get; private set; } = string.Empty;
        public string StandardError { get; private set; } = string.Empty;
        public string Executable { get; private set; }
        public string[] Arguments { get; private set; }
        public int KillCount { get; private set; }

        public FakeProcess(FakeConfiguration value) { configuration = value; FakeConfiguration.LastCreated = value; }

        public void Start(string executable, string[] arguments)
        {
            Executable = executable;
            Arguments = arguments;
            configuration.Processes.Add(this);
            if (configuration.ThrowWhenProbeStarts && Path.GetFileNameWithoutExtension(executable).IndexOf("probe", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("fake probe process start failed");
            IsStarted = true;
            if (configuration.CancelFirstProcess && configuration.Processes.Count == 1)
            {
                File.WriteAllBytes(arguments[arguments.Length - 1], new byte[] { 7, 8 });
                hasExited = false;
                outputComplete = false;
                return;
            }
            if (Path.GetFileNameWithoutExtension(executable).IndexOf("probe", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                int width = configuration.FailProbe ? 66 : 64;
                StandardOutput = "{\"streams\":[{\"width\":" + width + ",\"height\":48,\"avg_frame_rate\":\"30/1\",\"nb_read_frames\":\"2\",\"codec_name\":\"h264\",\"pix_fmt\":\"yuv420p\"}]}";
                ExitCode = 0;
            }
            else if (configuration.FailEncoder)
            {
                File.WriteAllBytes(arguments[arguments.Length - 1], new byte[] { 4, 5 });
                StandardError = "fake encoder failure";
                ExitCode = 3;
            }
            else
            {
                File.WriteAllBytes(arguments[arguments.Length - 1], new byte[] { 1, 2, 3 });
                StandardOutput = "frame=2\n";
                ExitCode = 0;
            }
            hasExited = true;
            outputComplete = !configuration.StallOutputDrain;
        }

        public void Kill()
        {
            KillCount++;
            if (configuration.ThrowOnKill) throw new InvalidOperationException("fake kill failed");
            ExitCode = 1;
            hasExited = true;
            outputComplete = !configuration.StallOutputDrain;
        }
        public void WaitForExit(int milliseconds) { }
        public void Dispose() { }
        public void MarkExited() { hasExited = true; outputComplete = true; }
    }

    sealed class FakeConfiguration
    {
        public readonly List<FakeProcess> Processes = new List<FakeProcess>();
        public bool FailEncoder;
        public bool FailProbe;
        public bool CancelFirstProcess;
        public bool ThrowHasExited;
        public bool ThrowOnKill;
        public bool StallOutputDrain;
        public bool ThrowWhenProbeStarts;
        public static FakeConfiguration LastCreated;
        public FakeProcess LastProcess => Processes.Count == 0 ? null : Processes[Processes.Count - 1];
    }

    [MenuItem("Tools/ViewWeave/Run Quilt Video Export Validation")]
    public static void RunAll()
    {
        string root = Path.Combine(Path.GetTempPath(), "ViewWeave video test " + Guid.NewGuid().ToString("N") + " 日本語");
        Directory.CreateDirectory(root);
        try
        {
            Require(QuiltVideoExporter.QuoteWindowsArgument("C:\\Program Files\\ffmpeg.exe") == "\"C:\\Program Files\\ffmpeg.exe\"", "spaces are quoted for Windows process arguments");
            Require(QuiltVideoExporter.QuoteWindowsArgument("C:\\folder with space\\") == "\"C:\\folder with space\\\\\"", "trailing backslashes are escaped before a closing quote");
            Require(QuiltVideoExporter.QuoteWindowsArgument("a\"b") == "\"a\\\"b\"", "embedded quotes are escaped");

            string good = CreateSequence(root, "有効 sequence", 64, 48, 2, false);
            QuiltVideoSequenceInfo info = QuiltVideoExporter.ValidateSequence(good);
            Require(info.FrameCount == 2 && info.Fps == 30 && info.Width == 64 && info.Height == 48, "completed manifest and matching frame metadata validate");
            Require(info.FramePattern == "frame_%06d_qs2x2a0.75.png", "validated PNG sequence produces the expected input pattern");
            Require(QuiltVideoExporter.RecommendedOutputFileName(info) == "quilt_qs2x2a0.75.mp4", "recommended output tag uses invariant quilt geometry");
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                Require(QuiltVideoExporter.RecommendedOutputFileName(info) == "quilt_qs2x2a0.75.mp4", "recommended output aspect uses invariant decimal punctuation");
            }
            finally { CultureInfo.CurrentCulture = previousCulture; }
            Require(QuiltVideoExporter.GetOutputNameGuidance("quilt_qs2x2a0.75.mp4", info) == string.Empty, "matching output tag needs no warning");
            Require(QuiltVideoExporter.GetOutputNameGuidance("clip.mp4", info).Contains("quilt_qs2x2a0.75.mp4"), "untagged output displays the auto-detection name recommendation");
            RequireThrows(() => QuiltVideoExporter.GetOutputNameGuidance("clip_qs3x2a0.75.mp4", info), "mismatched tagged layout is rejected");

            string missing = CreateSequence(root, "missing", 64, 48, 2, false);
            File.Delete(Path.Combine(missing, "frame_000001_qs2x2a0.75.png"));
            RequireThrows(() => QuiltVideoExporter.ValidateSequence(missing), "missing frame PNG is rejected");

            string odd = CreateSequence(root, "odd", 65, 48, 2, false);
            RequireThrows(() => QuiltVideoExporter.ValidateSequence(odd), "odd dimensions are rejected before encoding");

            string mixed = CreateSequence(root, "mixed", 64, 48, 2, true);
            RequireThrows(() => QuiltVideoExporter.ValidateSequence(mixed), "mixed static quilt geometry is rejected");

            string invalidJson = CreateSequence(root, "invalid json", 64, 48, 2, false);
            File.WriteAllText(Path.Combine(invalidJson, "frame_000001_qs2x2a0.75.json"), "{broken", Encoding.UTF8);
            RequireThrows(() => QuiltVideoExporter.ValidateSequence(invalidJson), "invalid frame sidecar JSON is rejected");

            string central = CreateSequence(root, "central", 64, 48, 2, false);
            File.WriteAllText(Path.Combine(central, "frame_000000_qs2x2a0.75.json"), "{\"quilt\":{},\"width\":64,\"height\":48,\"logicalViewIndex\":1}", Encoding.UTF8);
            RequireThrows(() => QuiltVideoExporter.ValidateSequence(central), "central-view metadata is rejected as non-quilt input");
            string partialGrid = CreateSequence(root, "partial grid", 64, 48, 2, false);
            string partialPath = Path.Combine(partialGrid, "frame_000000_qs2x2a0.75.json");
            QuiltCaptureMetadata partialMetadata = JsonUtility.FromJson<QuiltCaptureMetadata>(File.ReadAllText(partialPath, Encoding.UTF8));
            partialMetadata.views = 3;
            partialMetadata.lastRenderedViewCount = 3;
            File.WriteAllText(partialPath, JsonUtility.ToJson(partialMetadata), new UTF8Encoding(false));
            RequireThrows(() => QuiltVideoExporter.ValidateSequence(partialGrid), "partial quilt grids are rejected before export");

            string existingOutput = Path.Combine(root, "existing.mp4");
            File.WriteAllBytes(existingOutput, new byte[] { 9 });
            bool factoryCalled = false;
            QuiltVideoExporter.SetProcessFactoryForTests(() => { factoryCalled = true; return new FakeProcess(new FakeConfiguration()); });
            RequireThrows(() => QuiltVideoExporter.Start(good, existingOutput, "ffmpeg", "ffprobe"), "existing output is refused");
            Require(!factoryCalled, "existing output is refused before process creation");

            FakeConfiguration success = new FakeConfiguration();
            QuiltVideoExporter.SetProcessFactoryForTests(() => new FakeProcess(success));
            string output = Path.Combine(root, "成功 result.mp4");
            QuiltVideoExportJob job = QuiltVideoExporter.Start(good, output, "ffmpeg", "ffprobe");
            job.Poll();
            job.Poll();
            Require(job.IsFinished && job.Succeeded && File.Exists(output) && File.Exists(Path.ChangeExtension(output, ".json")), "fake ffmpeg and ffprobe successful path commits both outputs");
            Require(success.Processes.Count == 2 && success.Processes[0].Arguments[success.Processes[0].Arguments.Length - 1].EndsWith(".tmp.mp4", StringComparison.Ordinal), "encoding uses a temporary MP4 before verification");
            string exportMetadata = File.ReadAllText(Path.ChangeExtension(output, ".json"), Encoding.UTF8);
            Require(exportMetadata.Contains("\"frames\": 2") && exportMetadata.Contains("\"effectiveFps\": 30") && exportMetadata.Contains("\"width\": 64") && exportMetadata.Contains("\"height\": 48") && exportMetadata.Contains("\"columns\": 2") && exportMetadata.Contains("\"rows\": 2") && exportMetadata.Contains("\"views\": 4") && exportMetadata.Contains("\"viewAspect\": 0.75") && exportMetadata.Contains("\"viewRenderMode\": \"Full\"") && exportMetadata.Contains("\"lastRenderedViewCount\": 4") && exportMetadata.Contains("\"orientation\": \"bottom-left-view0-row-major\"") && exportMetadata.Contains("\"usedRect\""), "MP4 sidecar retains portable quilt layout and orientation metadata");

            FakeConfiguration failed = new FakeConfiguration { FailEncoder = true };
            QuiltVideoExporter.SetProcessFactoryForTests(() => new FakeProcess(failed));
            string failedOutput = Path.Combine(root, "failed.mp4");
            QuiltVideoExportJob failedJob = QuiltVideoExporter.Start(good, failedOutput, "ffmpeg", "ffprobe");
            failedJob.Poll();
            Require(failedJob.IsFinished && !failedJob.Succeeded && !File.Exists(failedOutput) && !File.Exists(Path.ChangeExtension(failedOutput, ".json")) && !File.Exists(failedJob.TemporaryPathForTests), "encoder failure removes temporary output and preserves final path");
            string failureDiagnostic = File.ReadAllText(failedJob.LastDiagnosticPath, Encoding.UTF8);
            Require(failureDiagnostic.Contains("\"stage\": \"ffmpeg\"") && failureDiagnostic.Contains("\"exitCodeKnown\": true") && failureDiagnostic.Contains("\"exitCode\": 3") && failureDiagnostic.Contains(Path.GetFileName(failedJob.TemporaryPathForTests)) && !failureDiagnostic.Contains("fake encoder failure"), "failure diagnostic records process facts without raw process output");

            FakeConfiguration probeStartFailure = new FakeConfiguration { ThrowWhenProbeStarts = true };
            QuiltVideoExporter.SetProcessFactoryForTests(() => new FakeProcess(probeStartFailure));
            QuiltVideoExportJob probeStartFailureJob = QuiltVideoExporter.Start(good, Path.Combine(root, "probe-start-failure.mp4"), "ffmpeg", "ffprobe");
            probeStartFailureJob.Poll();
            string probeStartDiagnostic = File.ReadAllText(probeStartFailureJob.LastDiagnosticPath, Encoding.UTF8);
            Require(probeStartFailureJob.IsFinished && probeStartDiagnostic.Contains("\"stage\": \"ffprobe\"") && probeStartDiagnostic.Contains("\"ownedProcessId\": -1") && probeStartDiagnostic.Contains("\"exitCodeKnown\": false"), "probe start failure resets encoder PID and exit facts before launch");

            FakeConfiguration badProbe = new FakeConfiguration { FailProbe = true };
            QuiltVideoExporter.SetProcessFactoryForTests(() => new FakeProcess(badProbe));
            string rejectedOutput = Path.Combine(root, "rejected.mp4");
            QuiltVideoExportJob rejectedJob = QuiltVideoExporter.Start(good, rejectedOutput, "ffmpeg", "ffprobe");
            rejectedJob.Poll();
            rejectedJob.Poll();
            Require(rejectedJob.IsFinished && !rejectedJob.Succeeded && !File.Exists(rejectedOutput) && !File.Exists(Path.ChangeExtension(rejectedOutput, ".json")), "ffprobe mismatch prevents final output commit");

            FakeConfiguration cancelled = new FakeConfiguration { CancelFirstProcess = true };
            QuiltVideoExporter.SetProcessFactoryForTests(() => new FakeProcess(cancelled));
            string cancelledOutput = Path.Combine(root, "cancelled.mp4");
            QuiltVideoExportJob cancelledJob = QuiltVideoExporter.Start(good, cancelledOutput, "ffmpeg", "ffprobe");
            cancelledJob.Cancel();
            cancelledJob.Poll();
            Require(cancelledJob.IsFinished && !cancelledJob.Succeeded && !File.Exists(cancelledOutput) && !File.Exists(Path.ChangeExtension(cancelledOutput, ".json")) && !File.Exists(cancelledJob.TemporaryPathForTests), "cancelled owned process is stopped and cleans only after confirmed exit");

            FakeConfiguration stalledEof = new FakeConfiguration { StallOutputDrain = true };
            QuiltVideoExporter.SetProcessFactoryForTests(() => new FakeProcess(stalledEof));
            string stalledOutput = Path.Combine(root, "stalled-eof.mp4");
            QuiltVideoExportJob stalledJob = QuiltVideoExporter.Start(good, stalledOutput, "ffmpeg", "ffprobe");
            stalledJob.PollAtForTests(1d);
            Require(!stalledJob.IsFinished, "confirmed process exit waits briefly for asynchronous stream EOF");
            stalledJob.PollAtForTests(12d);
            Require(stalledJob.IsFinished && !stalledJob.Succeeded && !File.Exists(stalledJob.TemporaryPathForTests), "stalled EOF expires after a bounded drain deadline and cleans only after process exit");
            Require(File.Exists(stalledJob.LastDiagnosticPath), "stalled EOF writes a separate failure diagnostic");

            FakeConfiguration pending = new FakeConfiguration { CancelFirstProcess = true, ThrowHasExited = true, ThrowOnKill = true };
            QuiltVideoExporter.SetProcessFactoryForTests(() => new FakeProcess(pending));
            string pendingOutput = Path.Combine(root, "pending-exit.mp4");
            QuiltVideoExportJob pendingJob = QuiltVideoExporter.Start(good, pendingOutput, "ffmpeg", "ffprobe");
            string pendingTemp = pendingJob.TemporaryPathForTests;
            pendingJob.Cancel();
            int killsAfterFirstCancel = pending.LastProcess.KillCount;
            pendingJob.Cancel();
            Require(killsAfterFirstCancel == 1 && pending.LastProcess.KillCount == 1, "repeated cancel is idempotent while stop is pending");
            pendingJob.RequestLifecycleCancellationForTests();
            Require(!pendingJob.IsFinished && ReferenceEquals(QuiltVideoExporter.ActiveJob, pendingJob), "reload callback retains tracking when process exit cannot be confirmed");
            Require(File.Exists(pendingTemp) && File.Exists(pendingJob.LastDiagnosticPath), "unknown process exit preserves temp output and writes diagnostics");
            string diagnostic = File.ReadAllText(pendingJob.LastDiagnosticPath, Encoding.UTF8);
            Require(diagnostic.Contains("\"exitCodeKnown\": false") && diagnostic.Contains("\"ownedProcessId\": 4242") && !diagnostic.Contains("fake encoder failure"), "diagnostic captures stage, unknown exit, owned PID, temp path, and reason without process logs");
            pending.ThrowHasExited = false;
            pending.ThrowOnKill = false;
            pending.LastProcess.MarkExited();
            pendingJob.Poll();
            Require(pendingJob.IsFinished && !File.Exists(pendingTemp), "temp output is cleaned after exit becomes positively confirmed");

            FakeConfiguration timedOut = new FakeConfiguration { CancelFirstProcess = true, ThrowOnKill = true };
            QuiltVideoExporter.SetProcessFactoryForTests(() => new FakeProcess(timedOut));
            string timeoutOutput = Path.Combine(root, "timeout-pending.mp4");
            QuiltVideoExportJob timeoutJob = QuiltVideoExporter.Start(good, timeoutOutput, "ffmpeg", "ffprobe");
            string timeoutTemp = timeoutJob.TemporaryPathForTests;
            timeoutJob.PollAtForTests(double.MaxValue);
            Require(!timeoutJob.IsFinished && File.Exists(timeoutTemp) && File.Exists(timeoutJob.LastDiagnosticPath), "timeout with failed kill retains job and temporary file");
            timedOut.ThrowOnKill = false;
            timedOut.LastProcess.MarkExited();
            timeoutJob.Poll();
            Require(timeoutJob.IsFinished && !File.Exists(timeoutTemp), "timeout cleanup waits until the owned process exit is confirmed");
            QuiltVideoExporter.SetProcessFactoryForTests(null);
            Debug.Log("Quilt video exporter validation passed.");
        }
        finally
        {
            FakeConfiguration cleanup = FakeConfiguration.LastCreated;
            if (cleanup != null)
            {
                cleanup.ThrowHasExited = false;
                cleanup.ThrowOnKill = false;
                cleanup.StallOutputDrain = false;
                foreach (FakeProcess process in cleanup.Processes) process.MarkExited();
            }
            try
            {
                QuiltVideoExportJob active = QuiltVideoExporter.ActiveJob;
                if (active != null && !active.IsFinished)
                {
                    active.Cancel();
                    active.Poll();
                }
            }
            catch { }
            try { QuiltVideoExporter.SetProcessFactoryForTests(null); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    static string CreateSequence(string root, string name, int width, int height, int count, bool varyStaticGeometry)
    {
        string directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        QuiltRecordingManifest manifest = new QuiltRecordingManifest
        {
            fps = 30,
            requestedFrameCount = count,
            completedFrameCount = count,
            status = "Complete",
            frameLog = "frames.jsonl"
        };
        File.WriteAllText(Path.Combine(directory, "manifest.json"), JsonUtility.ToJson(manifest), new UTF8Encoding(false));
        StringBuilder frames = new StringBuilder();
        for (int i = 0; i < count; ++i)
        {
            string fileName = "frame_" + i.ToString("D6") + "_qs2x2a0.75.png";
            frames.AppendLine(JsonUtility.ToJson(new QuiltRecordingFrame { index = i, simulationTime = i / 30d, deltaTime = 1f / 30f, fileName = fileName }));
            WritePngHeader(Path.Combine(directory, fileName), width, height);
            QuiltCaptureMetadata metadata = new QuiltCaptureMetadata
            {
                width = width,
                height = height,
                rows = 2,
                columns = 2,
                views = 4,
                viewRenderMode = "Full",
                lastRenderedViewCount = 4,
                usedRect = new QuiltCaptureRect { x = 0, y = 0, width = width, height = height },
                geometry = new QuiltCaptureGeometry
                {
                    viewAspect = 0.75f,
                    fieldOfView = 60f,
                    cameraDistance = varyStaticGeometry && i == 1 ? 4f : 3f,
                    viewCone = 40f,
                    nearClip = 0.1f,
                    farClip = 100f,
                    cameraRotationW = 1f,
                    cameraPositionX = i
                }
            };
            File.WriteAllText(Path.ChangeExtension(Path.Combine(directory, fileName), ".json"), JsonUtility.ToJson(metadata), new UTF8Encoding(false));
        }
        File.WriteAllText(Path.Combine(directory, "frames.jsonl"), frames.ToString(), new UTF8Encoding(false));
        return directory;
    }

    static void WritePngHeader(string path, int width, int height)
    {
        byte[] header = { 137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 0, 0, 0, 0, 0 };
        WriteBigEndian(header, 16, width);
        WriteBigEndian(header, 20, height);
        File.WriteAllBytes(path, header);
    }

    static void WriteBigEndian(byte[] bytes, int offset, int value)
    {
        bytes[offset] = (byte)(value >> 24);
        bytes[offset + 1] = (byte)(value >> 16);
        bytes[offset + 2] = (byte)(value >> 8);
        bytes[offset + 3] = (byte)value;
    }

    static void RequireThrows(Action action, string description)
    {
        bool thrown = false;
        try { action(); }
        catch { thrown = true; }
        Require(thrown, description);
    }

    static void Require(bool condition, string description)
    {
        if (!condition) throw new InvalidOperationException("Quilt video exporter test failed: " + description);
    }
}
