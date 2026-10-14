#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)] [int]$ExpectedEditorPid,
    [Parameter(Mandatory = $true)] [string]$ExpectedEditorStartUtc,
    [Parameter(Mandatory = $true)] [int]$ExpectedBridgePid,
    [Parameter(Mandatory = $true)] [string]$ApplyResultJsonPath,
    [Parameter(Mandatory = $true)] [string]$SkinApplyResultJsonPath,
    [ValidateSet('manual_pan', 'automated_camera_sweep')]
    [string]$CameraMotionMode = 'automated_camera_sweep'
)

$ErrorActionPreference = 'Stop'
$projectPath = [IO.Path]::GetFullPath('E:\URPSample_6.6.41f1').TrimEnd('\')
$editorExe = [IO.Path]::GetFullPath('C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe')
$unityCli = 'C:\Users\zzaba\AppData\Local\Unity\bin\unity.exe'
$watcherPath = Join-Path $PSScriptRoot 'Run-BoundedOriginalCoinNeighborProbe.ps1'
$exitPath = Join-Path $PSScriptRoot 'Run-OriginalProjectGracefulExit.ps1'
$applyPath = [IO.Path]::GetFullPath($ApplyResultJsonPath)
$skinApplyPath = [IO.Path]::GetFullPath($SkinApplyResultJsonPath)
$runnerCameraMotionMode = if ($CameraMotionMode -eq 'automated_camera_sweep') { 'coin_neighbor_sweep' } else { 'manual_pan' }
$coinTemplatePath = Join-Path $PSScriptRoot 'LiveOriginalCoinNeighborWatchTemplate.cs'

if (-not ('OriginalRemedy.SupervisorNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace OriginalRemedy {
    public static class SupervisorNative {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        const uint SYNCHRONIZE = 0x00100000;
        const uint WAIT_OBJECT_0 = 0;
        const uint WAIT_TIMEOUT = 258;
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
        [DllImport("shell32.dll", SetLastError = true)]
        static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr LocalFree(IntPtr memory);
        public static IntPtr PinProcess(uint pid) {
            IntPtr value = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE, false, pid);
            if (value == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return value;
        }
        public static bool Close(IntPtr handle) { return CloseHandle(handle); }
        public static uint Wait(IntPtr handle, uint milliseconds) { return WaitForSingleObject(handle, milliseconds); }
        public static uint ExitCode(IntPtr handle) {
            uint value;
            if (!GetExitCodeProcess(handle, out value)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return value;
        }
        public static string ProjectPath(string commandLine) {
            int count;
            IntPtr block = CommandLineToArgvW(commandLine, out count);
            if (block == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try {
                for (int i = 0; i + 1 < count; i++) {
                    string key = Marshal.PtrToStringUni(Marshal.ReadIntPtr(block, i * IntPtr.Size));
                    if (String.Equals(key, "-projectPath", StringComparison.OrdinalIgnoreCase) || String.Equals(key, "--project-path", StringComparison.OrdinalIgnoreCase)) {
                        return Marshal.PtrToStringUni(Marshal.ReadIntPtr(block, (i + 1) * IntPtr.Size));
                    }
                }
                return null;
            } finally { LocalFree(block); }
        }
    }
}
'@
}

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

function Test-SameUtcStart([object]$Observed, [DateTime]$Expected) {
    try {
        if ($Observed -is [DateTime]) {
            $observedUtc = ([DateTime]$Observed).ToUniversalTime()
        } else {
            $observedUtc = [DateTime]::Parse([string]$Observed, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
        }
        return [Math]::Abs(($observedUtc - $Expected.ToUniversalTime()).TotalSeconds) -le 1.0
    } catch {
        return $false
    }
}

function Invoke-OwnedChild([string]$FileName, [string[]]$Arguments, [int]$TimeoutMs, [string]$Label) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $FileName
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $null = $startInfo.ArgumentList.Add([string]$argument) }
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = $startInfo
    if (-not $child.Start()) { throw ($Label + '_start_failed') }
    $stdoutTask = $child.StandardOutput.ReadToEndAsync()
    $stderrTask = $child.StandardError.ReadToEndAsync()
    $exited = $child.WaitForExit($TimeoutMs)
    if (-not $exited) {
        try { $child.Kill() } catch { }
        [void]$child.WaitForExit(5000)
        $child.Dispose()
        throw ($Label + '_owned_child_timeout')
    }
    $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask), 5000)
    if (-not $drained) { $child.Dispose(); throw ($Label + '_owned_child_drain_incomplete') }
    $result = [pscustomobject]@{
        exitCode = [int]$child.ExitCode
        stdout = [string]$stdoutTask.Result
        stderr = [string]$stderrTask.Result
        processId = [int]$child.Id
    }
    $child.Dispose()
    return $result
}

function Get-ExactProjectRows {
    $all = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop)
    $matching = @()
    foreach ($row in $all) {
        if (-not $row.CommandLine) { continue }
        $token = [OriginalRemedy.SupervisorNative]::ProjectPath([string]$row.CommandLine)
        if ($token -and [IO.Path]::GetFullPath($token).TrimEnd('\').Equals($projectPath, [StringComparison]::OrdinalIgnoreCase)) { $matching += $row }
    }
    return $matching
}

$runTag = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$evidenceDir = Join-Path $PSScriptRoot ('supervised-' + $runTag)
New-Item -ItemType Directory -Path $evidenceDir | Out-Null
$pinnedHandle = [IntPtr]::Zero
$summary = [ordered]@{
    schema = 'viewweave-original-coin-neighbor-supervisor-v1'
    status = 'not-passed'
    projectPath = $projectPath
    editorPid = $ExpectedEditorPid
    expectedEditorStartUtc = $ExpectedEditorStartUtc
    bridgePid = $ExpectedBridgePid
    operation = 'original-coin-neighbor-supervised-observation'
    requestedCameraMotionMode = $CameraMotionMode
    runnerCameraMotionMode = $runnerCameraMotionMode
    watcherPath = $watcherPath
    watcherSha256 = if (Test-Path -LiteralPath $watcherPath) { Get-Sha256 $watcherPath } else { $null }
    gracefulExitRunnerSha256 = if (Test-Path -LiteralPath $exitPath) { Get-Sha256 $exitPath } else { $null }
    applyResultSha256 = if (Test-Path -LiteralPath $applyPath) { Get-Sha256 $applyPath } else { $null }
    skinApplyResultPath = $skinApplyPath
    skinApplyResultSha256 = if (Test-Path -LiteralPath $skinApplyPath) { Get-Sha256 $skinApplyPath } else { $null }
    skinProofSchema = $null
    skinProofDestinationGraphGuid = $null
    skinProofDestinationGraphSha256 = $null
    skinProofValidatedCloneNullProofSha256 = $null
    processHandlePinnedBeforeWatcher = $false
    watcherChildExitCode = $null
    watcherEvidencePassed = $false
    watcherEvidenceIdentityPassed = $false
    watcherSummaryPath = $null
    exitHelperChildExitCode = $null
    exitEvidenceIdentityPassed = $false
    exitSummaryPath = $null
    nativeWaitResult = $null
    nativeExitCode = $null
    exactProjectCountAfter = $null
    bridgeStillPresentAfter = $false
    sceneSha256Before = $null
    sceneSha256After = $null
    sourceGraphSha256Before = $null
    treatmentGraphSha256Before = $null
    treatmentGraphSha256After = $null
    nativeHandleReleased = $false
    completedUtc = $null
}

$scene = Join-Path $projectPath 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/URP_VFX_Learning_Templates.unity'
$sourceGraph = Join-Path $projectPath 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/SampleTexture2D.vfx'
$treatment = Join-Path $projectPath 'Assets/ViewWeave/ValidatedVFXOverrides/SampleTexture2D.vfx'
$skinSourcePrefab = Join-Path $projectPath 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/VFXG/Sample Skinned Mesh.prefab'
$skinSourceGraph = Join-Path $projectPath 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/SampleSkinnedMesh.vfx'
$skinTreatment = Join-Path $projectPath 'Assets/ViewWeave/ValidatedVFXOverrides/SampleSkinnedMesh.vfx'
try {
    if (-not (Test-Path -LiteralPath $watcherPath -PathType Leaf) -or -not (Test-Path -LiteralPath $coinTemplatePath -PathType Leaf) -or -not (Test-Path -LiteralPath $exitPath -PathType Leaf) -or -not (Test-Path -LiteralPath $applyPath -PathType Leaf) -or -not (Test-Path -LiteralPath $skinApplyPath -PathType Leaf) -or -not (Test-Path -LiteralPath $unityCli -PathType Leaf)) { throw 'required_input_missing' }
    if ((Get-Sha256 $applyPath) -ne '57FE58E5E8A9D4DB1B5A94DA43D441EDA69A21067A7F825EC7855D5DFD249BA1') { throw 'apply_result_hash_mismatch' }
    $apply = Get-Content -LiteralPath $applyPath -Raw | ConvertFrom-Json -ErrorAction Stop
    $skinApply = Get-Content -LiteralPath $skinApplyPath -Raw | ConvertFrom-Json -ErrorAction Stop
    if ($apply.schema -ne 'viewweave-original-prefab-graph-reference-apply-v1' -or $apply.status -ne 'applied' -or -not $apply.passGate -or $apply.destinationGraphGuid -ne '1d49c3213be77a24493c6d2c87bde98d') { throw 'apply_result_contract_mismatch' }
    if ($skinApply.schema -ne 'viewweave-original-skinnedmesh-prefab-graph-reference-apply-v1' -or $skinApply.status -ne 'applied' -or -not [bool]$skinApply.passGate -or $skinApply.destinationGraphPath -ne 'Assets/ViewWeave/ValidatedVFXOverrides/SampleSkinnedMesh.vfx' -or [string]$skinApply.destinationGraphGuid -notmatch '^[A-Fa-f0-9]{32}$' -or [string]$skinApply.destinationGraphSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or [string]$skinApply.validatedCloneNullProofSha256 -notmatch '^[A-Fa-f0-9]{64}$' -or -not [bool]$skinApply.onlyReferenceOverrideChanged -or -not [bool]$skinApply.allowInstancing -or [string]$skinApply.sourcePrefabGuid -ne '7f90fd4140e9047438f25d2a806668af' -or [string]$skinApply.oldSourceGraphSha256After -ne '65A6278E77854CC32DEB1AB748693C493AE6313F3F38E6465B4DE8CBA33D8EC1' -or [string]$skinApply.sceneSha256After -ne [string]$apply.sceneSha256After) { throw 'skin_apply_result_contract_mismatch' }
    $summary.skinProofSchema = [string]$skinApply.schema
    $summary.skinProofDestinationGraphGuid = ([string]$skinApply.destinationGraphGuid).ToLowerInvariant()
    $summary.skinProofDestinationGraphSha256 = ([string]$skinApply.destinationGraphSha256).ToUpperInvariant()
    $summary.skinProofValidatedCloneNullProofSha256 = ([string]$skinApply.validatedCloneNullProofSha256).ToUpperInvariant()
    $expectedStart = [DateTime]::Parse($ExpectedEditorStartUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
    $editor = Get-Process -Id $ExpectedEditorPid -ErrorAction Stop
    $actualStart = $editor.StartTime.ToUniversalTime()
    if ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 1.0 -or -not [IO.Path]::GetFullPath($editor.Path).Equals($editorExe, [StringComparison]::OrdinalIgnoreCase)) { throw 'editor_identity_mismatch' }
    $exactBefore = @(Get-ExactProjectRows)
    $ids = @($exactBefore | ForEach-Object { [int]$_.ProcessId })
    $primary = @($exactBefore | Where-Object { $ids -notcontains [int]$_.ParentProcessId })
    if ($primary.Count -ne 1 -or [int]$primary[0].ProcessId -ne $ExpectedEditorPid) { throw 'exact_primary_project_editor_mismatch' }
    $bridge = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedBridgePid" -ErrorAction Stop
    if (-not $bridge -or [IO.Path]::GetFileName([string]$bridge.ExecutablePath) -ne 'LookingGlassBridge.exe') { throw 'bridge_identity_mismatch' }
    $bridgeProcess = Get-Process -Id $ExpectedBridgePid -ErrorAction Stop
    $bridgeStart = $bridgeProcess.StartTime.ToUniversalTime().ToString('o')
    $summary.sceneSha256Before = Get-Sha256 $scene
    $summary.sourceGraphSha256Before = Get-Sha256 $sourceGraph
    $summary.treatmentGraphSha256Before = Get-Sha256 $treatment
    $summary.skinSourcePrefabSha256Before = Get-Sha256 $skinSourcePrefab
    $summary.skinSourceGraphSha256Before = Get-Sha256 $skinSourceGraph
    $summary.skinTreatmentGraphSha256Before = Get-Sha256 $skinTreatment
    if ($summary.sceneSha256Before -ne $apply.sceneSha256After -or $summary.sourceGraphSha256Before -ne $apply.oldSourceGraphSha256After -or $summary.treatmentGraphSha256Before -ne $apply.destinationGraphSha256 -or $summary.skinSourcePrefabSha256Before -ne $skinApply.sourcePrefabSha256After -or $summary.skinSourceGraphSha256Before -ne $skinApply.oldSourceGraphSha256After -or $summary.skinTreatmentGraphSha256Before -ne $skinApply.destinationGraphSha256) { throw 'scene_or_graph_hash_mismatch' }
    $pinnedHandle = [OriginalRemedy.SupervisorNative]::PinProcess([uint32]$ExpectedEditorPid)
    if ([OriginalRemedy.SupervisorNative]::Wait($pinnedHandle, 0) -ne 258 -or [OriginalRemedy.SupervisorNative]::ExitCode($pinnedHandle) -ne 259) { throw 'pinned_editor_not_running' }
    $summary.processHandlePinnedBeforeWatcher = $true

    $watcherDirsBefore = @(Get-ChildItem -LiteralPath $PSScriptRoot -Directory | Where-Object Name -Like 'run-*' | ForEach-Object FullName)
    $watcherArgs = @('-NoLogo','-NoProfile','-File',$watcherPath,'-ExpectedEditorPid',[string]$ExpectedEditorPid,'-ExpectedEditorStartUtc',$actualStart.ToString('o'),'-ExpectedBridgePid',[string]$ExpectedBridgePid,'-ApplyResultJsonPath',$applyPath,'-SkinApplyResultJsonPath',$skinApplyPath,'-CameraMotionMode',$runnerCameraMotionMode)
    $watcherRun = Invoke-OwnedChild 'pwsh' $watcherArgs 110000 'watcher'
    $summary.watcherChildExitCode = $watcherRun.exitCode
    $watcherOutputPath = Join-Path $evidenceDir 'watcher-child-output.txt'
    [IO.File]::WriteAllText($watcherOutputPath, $watcherRun.stdout + "`n---STDERR---`n" + $watcherRun.stderr, [Text.Encoding]::ASCII)
    $summary.watcherChildOutputSha256 = Get-Sha256 $watcherOutputPath
    $watcherDirsAfter = @(Get-ChildItem -LiteralPath $PSScriptRoot -Directory | Where-Object Name -Like 'run-*' | ForEach-Object FullName)
    $newWatcherDirs = @($watcherDirsAfter | Where-Object { $watcherDirsBefore -notcontains $_ })
    if ($newWatcherDirs.Count -eq 1) {
        $watcherSummaryPath = Join-Path $newWatcherDirs[0] 'run-summary.json'
        if (Test-Path -LiteralPath $watcherSummaryPath -PathType Leaf) {
            $summary.watcherSummaryPath = $watcherSummaryPath
            $watcherSummary = Get-Content -LiteralPath $watcherSummaryPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $summary.watcherEvidenceIdentityPassed = ([int]$watcherSummary.editorPid -eq $ExpectedEditorPid -and
                (Test-SameUtcStart $watcherSummary.editorStartUtc $actualStart) -and
                [string]$watcherSummary.originalApplyResultJsonSha256 -eq $summary.applyResultSha256 -and
                [string]$watcherSummary.skinApplyResultJsonSha256 -eq $summary.skinApplyResultSha256 -and
                [string]$watcherSummary.skinTreatmentGraphGuid -eq $summary.skinProofDestinationGraphGuid -and
                [string]$watcherSummary.skinTreatmentGraphSha256 -eq $summary.skinProofDestinationGraphSha256 -and
                [string]$watcherSummary.treatmentGraphGuid -eq [string]$apply.destinationGraphGuid -and
                [string]$watcherSummary.treatmentGraphSha256 -eq [string]$apply.destinationGraphSha256 -and
                [string]$watcherSummary.asRunRunnerSha256 -eq $summary.watcherSha256 -and
                [string]$watcherSummary.asRunTemplateSha256 -eq (Get-Sha256 $coinTemplatePath) -and
                [string]$watcherSummary.cameraMotionMode -eq $runnerCameraMotionMode)
            $summary.watcherEvidencePassed = ($summary.watcherEvidenceIdentityPassed -and
                [string]$watcherSummary.status -eq 'bounded_observation_finished' -and
                [bool]$watcherSummary.readyEvidenceGate -and [bool]$watcherSummary.freshCleanupStateGate -and
                [bool]$watcherSummary.outerPlayStopConfirmed -and [bool]$watcherSummary.evidenceFilesStableAfterPlayStop -and
                [bool]$watcherSummary.sceneHashUnchanged -and [bool]$watcherSummary.sourcePrefabHashUnchanged -and
                [bool]$watcherSummary.sourceGraphHashUnchanged -and [bool]$watcherSummary.treatmentGraphHashUnchanged -and
                (([string]$watcherSummary.skinSourcePrefabSha256Before -match '^[A-Fa-f0-9]{64}$') -and ([string]$watcherSummary.skinSourcePrefabSha256After -match '^[A-Fa-f0-9]{64}$') -and ([string]$watcherSummary.skinSourcePrefabSha256Before -eq [string]$watcherSummary.skinSourcePrefabSha256After) -and ([string]$watcherSummary.skinSourcePrefabSha256After -eq [string]$skinApply.sourcePrefabSha256After) -and [bool]$watcherSummary.skinSourcePrefabHashMatchesAppliedState) -and [bool]$watcherSummary.skinSourceGraphHashUnchanged -and
                [bool]$watcherSummary.skinTreatmentGraphHashUnchanged -and [bool]$watcherSummary.skinApplyResultHashUnchanged -and
                [bool]$watcherSummary.readyWindowCompleted -and [bool]$watcherSummary.sdkErrorAbsentInCapturedRows -and -not [bool]$watcherSummary.privateStopTriggered -and
                [bool]$watcherSummary.bridgeIdleAndPendingZeroObserved -and [bool]$watcherSummary.postPlayStartOnEnableRestorePassed -and
                [bool]$watcherSummary.cameraMotionGate -and [bool]$watcherSummary.watcherUnsubscribedObserved -and -not [bool]$watcherSummary.outerTimeoutTriggered -and -not [bool]$watcherSummary.originalRigProfileRestoreFailureObserved)
        }
    }

    $exitDirsBefore = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'exit-observations') -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName)
    $exitArgs = @('-NoLogo','-NoProfile','-File',$exitPath,'-ExpectedEditorPid',[string]$ExpectedEditorPid,'-ExpectedEditorStartUtc',$actualStart.ToString('o'),'-ExpectedBridgePid',[string]$ExpectedBridgePid,'-ScheduleExit')
    $exitRun = Invoke-OwnedChild 'pwsh' $exitArgs 60000 'graceful_exit'
    $summary.exitHelperChildExitCode = $exitRun.exitCode
    $exitOutputPath = Join-Path $evidenceDir 'exit-child-output.txt'
    [IO.File]::WriteAllText($exitOutputPath, $exitRun.stdout + "`n---STDERR---`n" + $exitRun.stderr, [Text.Encoding]::ASCII)
    $summary.exitChildOutputSha256 = Get-Sha256 $exitOutputPath
    $exitDirsAfter = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'exit-observations') -Directory -ErrorAction SilentlyContinue | ForEach-Object FullName)
    $newExitDirs = @($exitDirsAfter | Where-Object { $exitDirsBefore -notcontains $_ })
    if ($newExitDirs.Count -eq 1) {
        $exitSummaryPath = Join-Path $newExitDirs[0] 'summary.json'
        if (Test-Path -LiteralPath $exitSummaryPath -PathType Leaf) {
            $summary.exitSummaryPath = $exitSummaryPath
            $exitSummary = Get-Content -LiteralPath $exitSummaryPath -Raw | ConvertFrom-Json -ErrorAction Stop
            $summary.exitEvidenceIdentityPassed = ([int]$exitSummary.editorPid -eq $ExpectedEditorPid -and
                (Test-SameUtcStart $exitSummary.editorStartUtc $actualStart) -and
                [bool]$exitSummary.scheduledExit -and [bool]$exitSummary.naturalExitObserved -and
                [string]$exitSummary.exitMarkerValue -eq 'exit_requested_0' -and
                [string]$exitSummary.sceneSha256After -eq $summary.sceneSha256Before -and
                [string]$exitSummary.assetSha256After -eq $summary.sourceGraphSha256Before)
        }
    }

    $summary.nativeWaitResult = [OriginalRemedy.SupervisorNative]::Wait($pinnedHandle, 30000)
    if ($summary.nativeWaitResult -eq 0) { $summary.nativeExitCode = [OriginalRemedy.SupervisorNative]::ExitCode($pinnedHandle) }
    $summary.exactProjectCountAfter = @(Get-ExactProjectRows).Count
    $bridgeAfter = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedBridgePid" -ErrorAction Stop
    $bridgeAfterProcess = if ($bridgeAfter) { Get-Process -Id $ExpectedBridgePid -ErrorAction SilentlyContinue } else { $null }
    $summary.bridgeStillPresentAfter = [bool]($bridgeAfter -and $bridgeAfterProcess -and $bridgeAfterProcess.StartTime.ToUniversalTime().ToString('o') -eq $bridgeStart)
    $summary.sceneSha256After = Get-Sha256 $scene
    $summary.treatmentGraphSha256After = Get-Sha256 $treatment
    $summary.skinSourcePrefabSha256After = Get-Sha256 $skinSourcePrefab
    $summary.skinSourceGraphSha256After = Get-Sha256 $skinSourceGraph
    $summary.skinTreatmentGraphSha256After = Get-Sha256 $skinTreatment
    $summary.status = if ($summary.watcherEvidencePassed -and $summary.exitEvidenceIdentityPassed -and $summary.nativeWaitResult -eq 0 -and $summary.nativeExitCode -eq 0 -and $summary.exactProjectCountAfter -eq 0 -and $summary.bridgeStillPresentAfter -and $summary.sceneSha256After -eq $summary.sceneSha256Before -and $summary.treatmentGraphSha256After -eq $summary.treatmentGraphSha256Before -and $summary.skinSourcePrefabSha256After -eq $summary.skinSourcePrefabSha256Before -and $summary.skinSourceGraphSha256After -eq $summary.skinSourceGraphSha256Before -and $summary.skinTreatmentGraphSha256After -eq $summary.skinTreatmentGraphSha256Before) { 'watcher_observation_passed_and_editor_exited_zero' } elseif ($summary.nativeWaitResult -eq 0 -and $summary.nativeExitCode -eq 0 -and $summary.exactProjectCountAfter -eq 0 -and $summary.bridgeStillPresentAfter) { 'watcher_observation_incomplete_editor_exited_zero' } else { 'bounded_run_or_exit_gate_incomplete' }
} catch {
    $summary.status = 'supervisor_failed'
    $summary.errorType = $_.Exception.GetType().Name
    $message = [string]$_.Exception.Message
    if ($message.Length -gt 240) { $message = $message.Substring(0, 240) }
    $summary.safeError = [regex]::Replace($message, '[^\x20-\x7E]', '?')
} finally {
    if ($pinnedHandle -ne [IntPtr]::Zero) {
        try { $summary.nativeHandleReleased = [OriginalRemedy.SupervisorNative]::Close($pinnedHandle) } catch { $summary.nativeHandleReleased = $false }
    }
    $summary.completedUtc = [DateTime]::UtcNow.ToString('o')
    $summaryPath = Join-Path $evidenceDir 'supervisor-summary.json'
    $summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $summaryPath -Encoding ascii
}

$summary | ConvertTo-Json -Depth 5
if ($summary.status -ne 'watcher_observation_passed_and_editor_exited_zero') { exit 1 }

