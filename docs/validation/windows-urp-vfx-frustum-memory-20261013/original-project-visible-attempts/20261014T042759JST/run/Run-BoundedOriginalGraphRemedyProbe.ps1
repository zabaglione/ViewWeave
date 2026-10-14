param(
    [Parameter(Mandatory = $true)]
    [int]$ExpectedEditorPid,
    [Parameter(Mandatory = $true)]
    [string]$ExpectedEditorStartUtc,
    [Parameter(Mandatory = $true)]
    [int]$ExpectedBridgePid,
    [Parameter(Mandatory = $true)]
    [string]$ApplyResultJsonPath,
    [Parameter(Mandatory = $false)]
    [ValidateSet('manual_pan', 'automated_camera_sweep')]
    [string]$CameraMotionMode = 'manual_pan'
)

$ErrorActionPreference = 'Stop'
$watcherRegistered = $false
$script:commandFailure = $null
trap {
    $caught = $_
    if ($watcherRegistered -and -not (Test-Path -LiteralPath $stopFlag -PathType Leaf)) {
        try { [IO.File]::WriteAllText($stopFlag, 'outer_script_exception' + "`n", [Text.Encoding]::ASCII) } catch { }
    }
    $failurePath = if ($runDir -and (Test-Path -LiteralPath $runDir -PathType Container)) { Join-Path $runDir 'runner-error.json' } else { Join-Path $PSScriptRoot ('runner-error-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.json') }
    $safeMessage = [string]$caught.Exception.Message
    if ($safeMessage.Length -gt 240) { $safeMessage = $safeMessage.Substring(0, 240) }
    $safeMessage = [regex]::Replace($safeMessage, '[^\x20-\x7E]', '?')
    $failure = [ordered]@{
        status = 'runner_failed'
        exceptionType = $caught.Exception.GetType().Name
        errorCategory = [string]$caught.CategoryInfo.Category
        scriptLine = [int]$caught.InvocationInfo.ScriptLineNumber
        safeMessage = $safeMessage
        watcherRegistrationAttempted = [bool]$watcherRegistered
        stopFlagWritten = (Test-Path -LiteralPath $stopFlag -PathType Leaf)
        unityCommandFailure = $script:commandFailure
        utc = [DateTime]::UtcNow.ToString('o')
    }
    try { $failure | ConvertTo-Json -Compress -Depth 4 | Set-Content -LiteralPath $failurePath -Encoding ascii } catch { }
    [Console]::Error.WriteLine(('runner_failed_type=' + $failure.exceptionType + ';category=' + $failure.errorCategory + ';line=' + $failure.scriptLine))
    exit 1
}
$projectPath = [IO.Path]::GetFullPath('E:\URPSample_6.6.41f1')
$scenePath = Join-Path $projectPath 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/URP_VFX_Learning_Templates.unity'
$unityCli = 'C:\Users\zzaba\AppData\Local\Unity\bin\unity.exe'
$editorExe = 'C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe'
$templatePath = Join-Path $PSScriptRoot 'LiveGraphRemedyWatchTemplate.cs'
$limitBytes = [long](3 * 1024 * 1024 * 1024)
$privateSessionLimitBytes = [long](4 * 1024 * 1024 * 1024)
$warmupLimitBytes = [long](4 * 1024 * 1024 * 1024)
$sourcePrefabPath = Join-Path $projectPath 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/VFXG/Sample Texture2D.prefab'
$sourceGraphPath = Join-Path $projectPath 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/VFX/SampleTexture2D.vfx'
$expectedSourcePrefabGuid = '3a08a8ebfe6ab4642b6f075038bdc1c3'
$expectedSourceGraphGuid = 'e166f36ceb1831f46bea1e17fbc67c5a'
$expectedSourceGraphSha256 = '78D34F482963B47E6555E9218389A98A045F614F3F5B300776C78948CFE96556'
$expectedTreatmentGraphPath = 'Assets/ViewWeave/ValidatedVFXOverrides/SampleTexture2D.vfx'
$expectedTreatmentGraphSha256 = 'EF29E6CD2361E92BAE2E0CC1937F75ED8A44A5B2ACA1AEFA614F918B5371F097'
$stabilityRangeBytes = [long](16 * 1024 * 1024)
$utcTag = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$runDir = Join-Path $PSScriptRoot ('run-' + $utcTag)
$watchLog = Join-Path $runDir 'watcher.jsonl'
$batchLog = Join-Path $runDir 'vfx-batch-sweep.jsonl'
$privateLog = Join-Path $runDir 'process-memory.jsonl'
$stopFlag = Join-Path $runDir 'stop.flag'
$baselinePath = Join-Path $runDir 'normal-phase-baseline.txt'
$asRunWatcher = Join-Path $runDir 'LivePlayWatch.cs'
$asRunRestore = Join-Path $runDir 'RestoreStartOnEnable.cs'
$asRunRunnerSource = Join-Path $runDir 'Run-BoundedOriginalGraphRemedyProbe.ps1'
$asRunTemplateSource = Join-Path $runDir 'LiveGraphRemedyWatchTemplate.cs'
$metadataPath = Join-Path $runDir 'run-summary.json'

function Get-Sha256([string]$Path) {
    return (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToUpperInvariant()
}

if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 or newer is required.' }
if (-not (Test-Path -LiteralPath $ApplyResultJsonPath -PathType Leaf)) { throw 'Original project apply result JSON was not found.' }
$applyResultJsonPath = [IO.Path]::GetFullPath($ApplyResultJsonPath)
$applyResultJsonSha256 = Get-Sha256 $applyResultJsonPath
$applyRecord = Get-Content -LiteralPath $applyResultJsonPath -Raw | ConvertFrom-Json -ErrorAction Stop
if ($applyRecord.schema -ne 'viewweave-original-prefab-graph-reference-apply-v1' -or
    $applyRecord.status -ne 'applied' -or -not [bool]$applyRecord.passGate -or
    $applyRecord.destinationGraphPath -ne $expectedTreatmentGraphPath -or
    $applyRecord.destinationGraphSha256 -ne $expectedTreatmentGraphSha256 -or
    $applyRecord.sourcePrefabPath -ne 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Prefabs/VFXG/Sample Texture2D.prefab' -or
    $applyRecord.sourcePrefabGuid -ne '3a08a8ebfe6ab4642b6f075038bdc1c3' -or
    $applyRecord.oldSourceGraphSha256After -ne $expectedSourceGraphSha256 -or
    [string]$applyRecord.sceneSha256After -notmatch '^[A-Fa-f0-9]{64}$' -or
    -not [bool]$applyRecord.onlyReferenceOverrideChanged -or -not [bool]$applyRecord.allowInstancing) {
    throw 'Original apply result did not meet the reviewed graph-reference contract.'
}
$treatmentGraphGuid = ([string]$applyRecord.destinationGraphGuid).ToLowerInvariant()
if ($treatmentGraphGuid -notmatch '^[0-9a-f]{32}$' -or $treatmentGraphGuid -eq $expectedSourceGraphGuid) { throw 'Original treatment graph GUID is invalid or not distinct from the source graph.' }
$treatmentGraphPath = Join-Path $projectPath $expectedTreatmentGraphPath
$treatmentGraphMetaPath = $treatmentGraphPath + '.meta'
if (-not (Test-Path -LiteralPath $treatmentGraphPath -PathType Leaf) -or -not (Test-Path -LiteralPath $treatmentGraphMetaPath -PathType Leaf)) { throw 'Applied treatment graph or metadata is missing from the original project.' }
$treatmentGraphSha256 = Get-Sha256 $treatmentGraphPath
$treatmentGraphMetaText = [IO.File]::ReadAllText($treatmentGraphMetaPath)
$treatmentGraphMetaGuid = [regex]::Match($treatmentGraphMetaText, '(?m)^guid:\s*([0-9a-fA-F]{32})\s*$').Groups[1].Value.ToLowerInvariant()
if ($treatmentGraphSha256 -ne $expectedTreatmentGraphSha256 -or $treatmentGraphMetaGuid -ne $treatmentGraphGuid) { throw 'Applied treatment graph bytes or GUID do not match the original apply result.' }

if (-not ('OriginalRemedy.NativeCommandLine' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace OriginalRemedy {
    public static class NativeCommandLine {
        [DllImport("shell32.dll", SetLastError = true)]
        static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr LocalFree(IntPtr memory);
        public static string[] Parse(string commandLine) {
            int count;
            IntPtr block = CommandLineToArgvW(commandLine, out count);
            if (block == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try {
                string[] values = new string[count];
                for (int i = 0; i < count; i++) values[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(block, i * IntPtr.Size));
                return values;
            } finally { LocalFree(block); }
        }
    }
}
'@
}

function Get-ProjectPathFromCommandLine([string]$CommandLine) {
    $arguments = [OriginalRemedy.NativeCommandLine]::Parse($CommandLine)
    for ($index = 0; $index -lt $arguments.Length - 1; $index++) {
        if ($arguments[$index] -ieq '-projectPath' -or $arguments[$index] -ieq '--project-path') {
            try { return [IO.Path]::GetFullPath($arguments[$index + 1]).TrimEnd('\') } catch { return $null }
        }
    }
    return $null
}

function Get-ExactEditor([int]$PidValue) {
    $row = Get-CimInstance Win32_Process -Filter "ProcessId=$PidValue" -ErrorAction Stop
    if (-not $row -or $row.Name -ne 'Unity.exe' -or -not $row.ExecutablePath -or -not $row.CommandLine) { throw 'Exact Unity Editor process was not found.' }
    if (-not [IO.Path]::GetFullPath($row.ExecutablePath).Equals([IO.Path]::GetFullPath($editorExe), [StringComparison]::OrdinalIgnoreCase)) { throw 'Editor executable path did not match the fixed Unity version.' }
    if ((Get-ProjectPathFromCommandLine ([string]$row.CommandLine)) -ne $projectPath.TrimEnd('\')) { throw 'Editor project token did not identify the exact original project.' }
    $process = Get-Process -Id $PidValue -ErrorAction Stop
    $expectedStart = [DateTime]::Parse($ExpectedEditorStartUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
    $actualStart = $process.StartTime.ToUniversalTime()
    if ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 1.0) { throw 'Editor process start time did not match the pinned process.' }
    $matchingRows = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | Where-Object {
        $_.CommandLine -and (Get-ProjectPathFromCommandLine ([string]$_.CommandLine)) -eq $projectPath.TrimEnd('\')
    })
    $matchingIds = @($matchingRows | ForEach-Object { [int]$_.ProcessId })
    $primaryRows = @($matchingRows | Where-Object { $matchingIds -notcontains [int]$_.ParentProcessId })
    if ($primaryRows.Count -ne 1 -or [int]$primaryRows[0].ProcessId -ne $PidValue) { throw 'Exact project primary Editor count or PID gate failed.' }
    return [pscustomobject]@{ Cim = $row; Process = $process; StartUtc = $actualStart.ToString('o'); ProjectPrimaryCount = $primaryRows.Count; ProjectTreeProcessCount = $matchingRows.Count; ProjectDescendantCount = [Math]::Max(0, $matchingRows.Count - $primaryRows.Count) }
}

function Invoke-UnityCommand([string[]]$CommandArgs) {
    $allArgs = @($CommandArgs) + @('--project-path', $projectPath, '--format', 'json', '--caller', 'plugin', '--skill', 'unity-cli')
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $unityCli
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $allArgs) { [void]$startInfo.ArgumentList.Add([string]$argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw 'Unity CLI process did not start.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(20000)) {
        try { $process.Kill($true) } catch { }
        [void]$process.WaitForExit(5000)
        try { [void]$stdoutTask.Wait(5000) } catch { }
        try { [void]$stderrTask.Wait(5000) } catch { }
        $process.Dispose()
        throw 'Unity CLI command exceeded the bounded 20 second deadline.'
    }
    try { [void]$stdoutTask.Wait(5000); [void]$stderrTask.Wait(5000) } catch { }
    $stdoutText = if ($stdoutTask.IsCompleted) { [string]$stdoutTask.Result } else { '' }
    $stderrText = if ($stderrTask.IsCompleted) { [string]$stderrTask.Result } else { '' }
    $exit = $process.ExitCode
    $process.Dispose()
    $captured = @($stdoutText -split "`r?`n" | Where-Object { $_ -ne '' })
    if ($exit -ne 0) {
        $cliObject = $null
        try { $cliObject = $stdoutText | ConvertFrom-Json -ErrorAction Stop } catch { }
        $errors = @()
        if ($cliObject -and $cliObject.errors) {
            foreach ($item in @($cliObject.errors | Select-Object -First 3)) {
                $message = [string]$item.message
                $message = [regex]::Replace($message, '(?i)(access[_ -]?token|token|authorization|license[_ -]?key)\s*[:=]\s*[^\s,;]+', '$1=[redacted]')
                $message = [regex]::Replace($message, '[^\x20-\x7E]', '?')
                if ($message.Length -gt 240) { $message = $message.Substring(0, 240) }
                $errors += [ordered]@{ code = [string]$item.code; message = $message }
            }
        }
        $resultSuccess = $null
        $resultMessage = $null
        if ($cliObject -and $cliObject.data -and $cliObject.data.result) {
            if ($cliObject.data.result.PSObject.Properties.Name -contains 'success') { $resultSuccess = [bool]$cliObject.data.result.success }
            foreach ($field in @('error', 'message')) {
                if ($cliObject.data.result.PSObject.Properties.Name -contains $field -and $cliObject.data.result.$field) { $resultMessage = [string]$cliObject.data.result.$field; break }
            }
        }
        if ($resultMessage) {
            $resultMessage = [regex]::Replace($resultMessage, '(?i)(access[_ -]?token|token|authorization|license[_ -]?key)\s*[:=]\s*[^\s,;]+', '$1=[redacted]')
            $resultMessage = [regex]::Replace($resultMessage, '[^\x20-\x7E]', '?')
            if ($resultMessage.Length -gt 240) { $resultMessage = $resultMessage.Substring(0, 240) }
        }
        $script:commandFailure = [ordered]@{ command = (($CommandArgs | Select-Object -First 2) -join ' '); exitCode = $exit; outerSuccess = if ($cliObject) { [bool]$cliObject.success } else { $null }; dataResultSuccess = $resultSuccess; errors = @($errors); dataResultMessage = $resultMessage }
        throw ('Unity CLI command failed with exit ' + $exit)
    }
    if ($stderrText.Length -gt 0) {
        $safeStderr = [regex]::Replace($stderrText, '[^\x20-\x7E]', '?')
        if ($safeStderr.Length -gt 240) { $safeStderr = $safeStderr.Substring(0, 240) }
        if ($safeStderr -match '(?i)error|exception|failed') { throw ('Unity CLI wrote a bounded diagnostic: ' + $safeStderr) }
    }
    return $stdoutText.Trim()
}

function Get-LiveState {
    $expr = 'var rs = UnityEngine.Object.FindObjectsByType<ViewWeave.QuiltRenderer>(UnityEngine.FindObjectsInactive.Include); if (rs.Length != 1) return "invalid_renderer_count"; var r = rs[0]; var s = r.GetComponent<ViewWeave.ViewWeaveBridgeSession>(); var o = r.GetComponent<ViewWeave.BridgeWorkerQuiltOutput>(); var rig = r.GetComponent<ViewWeave.ViewWeaveRig>(); var uc = r.sourceCamera == null ? null : r.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>(); System.Func<UnityEngine.Camera,string> pose = c => c == null ? "null" : c.GetEntityId().ToString() + ";p=" + c.transform.position.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + c.transform.position.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + c.transform.position.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + ";q=" + c.transform.rotation.x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + c.transform.rotation.y.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + c.transform.rotation.z.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "," + c.transform.rotation.w.ToString("R", System.Globalization.CultureInfo.InvariantCulture); return string.Empty + UnityEditor.EditorApplication.isPlaying + ((char)124) + UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode + ((char)124) + UnityEditor.EditorApplication.isPaused + ((char)124) + UnityEditor.EditorApplication.isCompiling + ((char)124) + UnityEngine.Time.frameCount + ((char)124) + UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() + ((char)124) + UnityEditor.EditorApplication.timeSinceStartup.ToString(System.Globalization.CultureInfo.InvariantCulture) + ((char)124) + rs.Length + ((char)124) + s.State + ((char)124) + o.State + ((char)124) + ViewWeave.BridgeWorkerQuiltOutput.ShutdownPendingCount + ((char)124) + (uc == null ? -1 : uc.HistoryCameraCount) + ((char)124) + (r.QuiltTexture == null) + ((char)124) + r.autoRender + ((char)124) + rig.AutoRender + ((char)124) + s.startOnEnable + ((char)124) + rig.Profile.QuiltWidth + "x" + rig.Profile.QuiltHeight + "," + rig.Profile.Columns + "x" + rig.Profile.Rows + ",views=" + rig.Profile.Views + ((char)124) + pose(r.sourceCamera) + ((char)124) + pose(r.referenceCamera) + ((char)124) + s.GetEntityId().ToString() + ((char)124) + UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path + ((char)124) + string.IsNullOrEmpty(r.LastAutoRenderError) + ((char)124) + string.IsNullOrEmpty(s.LastError) + ((char)124) + string.IsNullOrEmpty(o.LastError);'
    $response = Invoke-UnityCommand @('command', 'eval', $expr)
    try { $parsed = $response | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Unity eval response was not valid JSON.' }
    if (-not $parsed.success -or -not $parsed.data.result.success -or -not $parsed.data.result.result) { throw 'Unity eval did not return a successful scalar result.' }
    return [string]$parsed.data.result.result
}

function Test-CleanupSettledState {
    param([string]$State, [string]$ExpectedSessionEntityId, [string]$ExpectedSceneAssetPath, [bool]$WatcherUnsubscribed)
    if (-not $WatcherUnsubscribed -or [string]::IsNullOrWhiteSpace($State)) { return $false }
    $fields = $State.Split('|')
    return $fields.Length -ge 24 -and
        $fields[0] -eq 'False' -and $fields[1] -eq 'False' -and
        $fields[7] -eq '1' -and $fields[8] -eq 'Idle' -and $fields[9] -eq 'Closed' -and
        $fields[10] -eq '0' -and $fields[11] -eq '0' -and $fields[12] -eq 'True' -and
        $fields[13] -eq 'False' -and $fields[14] -eq 'False' -and
        $fields[19] -eq $ExpectedSessionEntityId -and $fields[20] -eq $ExpectedSceneAssetPath -and
        $fields[21] -eq 'True' -and $fields[22] -eq 'True' -and $fields[23] -eq 'True'
}

function Write-AsciiJsonLine([string]$Path, [hashtable]$Value) {
    $json = $Value | ConvertTo-Json -Compress -Depth 5
    [IO.File]::AppendAllText($Path, $json + "`n", [Text.Encoding]::ASCII)
}

function Get-EventValue([string]$Line, [string]$Name) {
    $match = [regex]::Match($Line, '(?:^|\|)' + [regex]::Escape($Name) + '=([^|]*)')
    if ($match.Success) { return $match.Groups[1].Value }
    return $null
}

function Get-EvidenceFileState([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    $item = Get-Item -LiteralPath $Path
    return [ordered]@{ length = [long]$item.Length; lastWriteUtc = $item.LastWriteTimeUtc.ToString('o'); sha256 = Get-Sha256 $Path }
}

if (-not (Test-Path -LiteralPath $unityCli -PathType Leaf) -or -not (Test-Path -LiteralPath $templatePath -PathType Leaf) -or -not (Test-Path -LiteralPath $scenePath -PathType Leaf) -or -not (Test-Path -LiteralPath $sourcePrefabPath -PathType Leaf) -or -not (Test-Path -LiteralPath $sourceGraphPath -PathType Leaf)) {
    throw 'A required fixed-path input was not found.'
}
$sourcePrefabMetaPath = $sourcePrefabPath + '.meta'
$sourceGraphMetaPath = $sourceGraphPath + '.meta'
if (-not (Test-Path -LiteralPath $sourcePrefabMetaPath -PathType Leaf) -or -not (Test-Path -LiteralPath $sourceGraphMetaPath -PathType Leaf)) { throw 'A source asset metadata file was not found.' }
$sourcePrefabSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourcePrefabPath).Hash
$sourceGraphSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceGraphPath).Hash
$sourcePrefabGuid = [regex]::Match([IO.File]::ReadAllText($sourcePrefabMetaPath), '(?m)^guid:\s*([0-9a-fA-F]{32})\s*$').Groups[1].Value.ToLowerInvariant()
$sourceGraphGuid = [regex]::Match([IO.File]::ReadAllText($sourceGraphMetaPath), '(?m)^guid:\s*([0-9a-fA-F]{32})\s*$').Groups[1].Value.ToLowerInvariant()
if ($sourcePrefabGuid -ne $expectedSourcePrefabGuid -or $sourceGraphGuid -ne $expectedSourceGraphGuid -or $sourceGraphSha256 -ne $expectedSourceGraphSha256) { throw 'Source graph or prefab identity did not match the reviewed values.' }

$allUnityRows = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop)
$exactEditors = @($allUnityRows | Where-Object { $_.CommandLine -and (Get-ProjectPathFromCommandLine ([string]$_.CommandLine)) -eq $projectPath.TrimEnd('\') })
$unityPidsBefore = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | ForEach-Object { [int]$_.ProcessId } | Sort-Object -Unique)
if (@($exactEditors | Where-Object { @($exactEditors | ForEach-Object { [int]$_.ProcessId }) -notcontains [int]$_.ParentProcessId }).Count -ne 1) { throw 'Exact original-project primary Editor inventory gate failed.' }
$editorRow = Get-ExactEditor $ExpectedEditorPid
$editorProcess = $editorRow.Process
$bridgeRow = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedBridgePid" -ErrorAction Stop
if (-not $bridgeRow) { throw 'Expected Bridge process was not found.' }
$bridgeProcess = Get-Process -Id $ExpectedBridgePid -ErrorAction Stop
$bridgeStartUtc = $bridgeProcess.StartTime.ToUniversalTime().ToString('o')
if ($editorProcess.PrivateMemorySize64 -le 0 -or $editorProcess.WorkingSet64 -le 0) { throw 'External process memory counters were unavailable.' }

New-Item -ItemType Directory -Path $runDir | Out-Null
$sceneHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $scenePath).Hash
$sourcePrefabShaBefore = Get-Sha256 $sourcePrefabPath
$sourceGraphShaBefore = Get-Sha256 $sourceGraphPath
$applySourcePrefabSha = ([string]$applyRecord.sourcePrefabSha256After).ToUpperInvariant()
if ($applySourcePrefabSha -notmatch '^[A-F0-9]{64}$' -or $sourcePrefabShaBefore -ne $applySourcePrefabSha) { throw 'Current original prefab does not match the verified apply result.' }
if ($sceneHashBefore -ne ([string]$applyRecord.sceneSha256After).ToUpperInvariant()) { throw 'Current original scene does not match the verified apply result.' }
$templateHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $templatePath).Hash
if ($sourcePrefabShaBefore -ne $sourcePrefabSha256 -or $sourceGraphShaBefore -ne $sourceGraphSha256) { throw 'Source VFX file hashes changed during the runner preflight.' }
Copy-Item -LiteralPath $PSCommandPath -Destination $asRunRunnerSource
Copy-Item -LiteralPath $templatePath -Destination $asRunTemplateSource
$asRunPrefabSource = Join-Path $runDir 'source-Sample Texture2D.prefab'
$asRunPrefabMeta = Join-Path $runDir 'source-Sample Texture2D.prefab.meta'
$asRunGraphSource = Join-Path $runDir 'source-SampleTexture2D.vfx'
$asRunTreatmentGraph = Join-Path $runDir 'treatment-SampleTexture2D.vfx'
$asRunTreatmentGraphMeta = Join-Path $runDir 'treatment-SampleTexture2D.vfx.meta'
$asRunApplyResult = Join-Path $runDir 'original-apply-result.json'
Copy-Item -LiteralPath $sourcePrefabPath -Destination $asRunPrefabSource
Copy-Item -LiteralPath $sourcePrefabMetaPath -Destination $asRunPrefabMeta
Copy-Item -LiteralPath $sourceGraphPath -Destination $asRunGraphSource
Copy-Item -LiteralPath $treatmentGraphPath -Destination $asRunTreatmentGraph
Copy-Item -LiteralPath $treatmentGraphMetaPath -Destination $asRunTreatmentGraphMeta
Copy-Item -LiteralPath $applyResultJsonPath -Destination $asRunApplyResult
$runnerSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asRunRunnerSource).Hash
$templateSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asRunTemplateSource).Hash
$template = Get-Content -LiteralPath $templatePath -Raw
$template = $template.Replace('__WATCH_LOG_PATH__', $watchLog.Replace('\', '/'))
$template = $template.Replace('__BATCH_LOG_PATH__', $batchLog.Replace('\', '/'))
$template = $template.Replace('__STOP_FLAG_PATH__', $stopFlag.Replace('\', '/'))
$template = $template.Replace('__BASELINE_PATH__', $baselinePath.Replace('\', '/'))
$template = $template.Replace('__CAMERA_MOTION_MODE__', $CameraMotionMode)
if ($template.Contains('__CAMERA_MOTION_MODE__')) { throw 'Camera motion mode template substitution was incomplete.' }
if ($template.Contains('__TREATMENT_GRAPH_GUID__') -or $template.Contains('__TREATMENT_GRAPH_PATH__')) {
    $template = $template.Replace('__TREATMENT_GRAPH_GUID__', $treatmentGraphGuid)
    $template = $template.Replace('__TREATMENT_GRAPH_PATH__', $expectedTreatmentGraphPath)
}
if ($template.Contains('__TREATMENT_GRAPH_GUID__') -or $template.Contains('__TREATMENT_GRAPH_PATH__')) { throw 'Treatment graph template substitution was incomplete.' }
[IO.File]::WriteAllText($asRunWatcher, $template, [Text.UTF8Encoding]::new($false))
$asRunHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asRunWatcher).Hash

$preState = Get-LiveState
$preStateFields = $preState.Split('|')
if ($preStateFields.Length -lt 24 -or $preStateFields[0] -ne 'False' -or $preStateFields[1] -ne 'False') { throw 'Fresh Editor evaluation reports an invalid state or Play mode active.' }
$expectedStartOnEnable = $preStateFields[15]
$expectedSessionEntityId = $preStateFields[19]
$expectedSceneAssetPath = $preStateFields[20]
$expectedScenePath = ($scenePath.Substring($projectPath.TrimEnd('\').Length).TrimStart('\')).Replace('\', '/')
if ([string]::IsNullOrWhiteSpace($expectedSessionEntityId) -or $expectedSceneAssetPath -ne $expectedScenePath) { throw 'Pre-Play session identity or exact scene path did not match.' }
$installResponse = Invoke-UnityCommand @('command', 'eval_file', $asRunWatcher)
if ($installResponse -notmatch 'watcher_registered') { throw 'Live watcher did not report registration.' }
$watcherRegistered = $true
$editorProcess.Refresh()

$summary = [ordered]@{
    status = 'running'
    freshStateBeforePlay = $preState
    projectPath = $projectPath
    scenePath = $scenePath
    editorPid = $ExpectedEditorPid
    editorExecutable = $editorRow.ExecutablePath
    editorStartUtc = $editorRow.StartUtc
    projectPrimaryEditorCountBefore = $editorRow.ProjectPrimaryCount
    projectTreeUnityProcessCountBefore = $editorRow.ProjectTreeProcessCount
    projectDescendantCountBefore = $editorRow.ProjectDescendantCount
    bridgePid = $ExpectedBridgePid
    bridgeStartUtc = $bridgeStartUtc
    unityCliCaller = 'plugin'
    unityCliSkill = 'unity-cli'
    templateSha256 = $templateHash
    asRunRunnerSha256 = $runnerSourceHash
    asRunTemplateSha256 = $templateSourceHash
    asRunWatcherSha256 = $asRunHash
    cameraMotionMode = $CameraMotionMode
    sceneSha256Before = $sceneHashBefore
    sourcePrefabPath = $sourcePrefabPath
    sourcePrefabGuid = $sourcePrefabGuid
    sourcePrefabSha256Before = $sourcePrefabShaBefore
    sourceGraphPath = $sourceGraphPath
    sourceGraphGuid = $sourceGraphGuid
    sourceGraphSha256Before = $sourceGraphShaBefore
    sourcePrefabAllowInstancingTrueFromApplyResult = [bool]$applyRecord.allowInstancing
    sourcePrefabOnlyGraphReferenceOverrideFromApplyResult = [bool]$applyRecord.onlyReferenceOverrideChanged
    treatmentGraphPath = $expectedTreatmentGraphPath
    treatmentGraphGuid = $treatmentGraphGuid
    treatmentGraphSha256 = $treatmentGraphSha256
    treatmentGraphMetaGuid = $treatmentGraphMetaGuid
    treatmentAllowInstancingExpected = $true
    originalApplyResultJsonPath = $applyResultJsonPath
    originalApplyResultJsonSha256 = $applyResultJsonSha256
    originalApplyResultAsRunSha256 = Get-Sha256 $asRunApplyResult
    sourcePrefabAsRunSha256 = Get-Sha256 $asRunPrefabSource
    sourceGraphAsRunSha256 = Get-Sha256 $asRunGraphSource
    treatmentGraphAsRunSha256 = Get-Sha256 $asRunTreatmentGraph
    treatmentGraphMetaAsRunSha256 = Get-Sha256 $asRunTreatmentGraphMeta
    editorPlayWasActiveBefore = $false
    startOnEnableTemporarilyDisabledInMemory = [bool]::Parse($expectedStartOnEnable)
    expectedStartOnEnableAfterPlay = [bool]::Parse($expectedStartOnEnable)
    expectedSessionEntityId = $expectedSessionEntityId
    expectedScenePathAfterPlay = $expectedScenePath
    sceneSaveCalled = $false
    sessionStartMethod = 'ViewWeaveBridgeSession.StartSession'
    connectionWindowSeconds = 10
    readyMeasurementWindowSeconds = 30
    cameraSweepSettleSeconds = 1.25
    cameraSweepStableSeconds = 0.25
    cameraSweepYawAmplitudeDegrees = 15.0
    cameraSweepCycleSeconds = 4.0
    playStartWaitSeconds = 30
    outerRunnerWindowSeconds = 75
    readyRevisionLimit = 10000
    normalWarmupWindowSeconds = 5
    normalWarmupFrameLimit = 6000
    normalWarmupTimeoutSemantics = 'Fail closed unless the existing three distinct samples span at least 0.25 seconds and satisfy the existing stability range; bounded by five seconds or 6000 frames. Existing memory guards remain active.'
    warmupGrowthStopBytes = $warmupLimitBytes
    graphicsGrowthStopBytes = $limitBytes
    sessionPrivateGrowthStopBytes = $privateSessionLimitBytes
    growthGuardSemantics = 'Observed process-counter growth thresholds checked after allocation; overshoot is possible and values are not physical-VRAM caps.'
    graphicsGrowthBaselineSource = 'stable_normal_phase_raw_baseline_fixed_before_StartSession'
    sessionPrivateGrowthBaselineSource = 'stable_normal_phase_private_baseline_acknowledged_before_StartSession'
    prePlayWarmupGrowthChecksRemainActive = $true
    sdkHistoryBudgetMutated = $false
    normalStabilityRangeBytes = $stabilityRangeBytes
    normalStabilityFrames = 3
    normalStabilityMinimumSpanSeconds = 0.25
    processCimUsed = $true
    privateMemoryBaseline = [long]$editorProcess.PrivateMemorySize64
    workingSetBaseline = [long]$editorProcess.WorkingSet64
    privateMemoryPeak = [long]$editorProcess.PrivateMemorySize64
    workingSetPeak = [long]$editorProcess.WorkingSet64
    watcherRegistered = $watcherRegistered
    privateStopTriggered = $false
    normalWarmupStable = $false
    normalStabilityCandidateObserved = $false
    prePlayRawBaseline = $null
    normalPhaseRawBaseline = $null
    normalWarmupPrivateBaseline = $null
    normalWarmupPrivateRangeBytes = $null
    normalWarmupPrivateSampleSpanSeconds = $null
    privatePhaseBaseline = $null
    outerTimeoutTriggered = $false
    processResidualCim = $null
}
Write-AsciiJsonLine $privateLog @{ utc = [DateTime]::UtcNow.ToString('o'); phase = 'before_play'; pid = $ExpectedEditorPid; privateBytes = [long]$editorProcess.PrivateMemorySize64; workingSetBytes = [long]$editorProcess.WorkingSet64 }

$playResponse = Invoke-UnityCommand @('command', 'editor_play')
$summary.status = 'playing'
$playStartedUtc = [DateTime]::UtcNow
$normalPrivateBaseline = $null
$sessionPrivateBaseline = $null
$phasePrivateBaseline = $null
$phaseBaselineAcknowledged = $false
$normalStabilityCandidate = $false
$candidateRawBaseline = $null
$privateSamples = [System.Collections.Generic.List[object]]::new()
$seenEvents = 0
$stopSent = $false
$deadline = [DateTime]::UtcNow.AddSeconds(75)

while ([DateTime]::UtcNow -lt $deadline) {
    Start-Sleep -Milliseconds 200
    $editorProcess.Refresh()
    if ($editorProcess.HasExited) { throw 'The shared Editor process exited unexpectedly.' }
    $currentPrivate = [long]$editorProcess.PrivateMemorySize64
    $currentWorkingSet = [long]$editorProcess.WorkingSet64
    $sampleUtc = [DateTime]::UtcNow
    $privateSamples.Add([pscustomobject]@{ utc = $sampleUtc; privateBytes = $currentPrivate; workingSetBytes = $currentWorkingSet })
    while ($privateSamples.Count -gt 8) { $privateSamples.RemoveAt(0) }
    if ($currentPrivate -gt $summary.privateMemoryPeak) { $summary.privateMemoryPeak = $currentPrivate }
    if ($currentWorkingSet -gt $summary.workingSetPeak) { $summary.workingSetPeak = $currentWorkingSet }
    Write-AsciiJsonLine $privateLog @{ utc = $sampleUtc.ToString('o'); phase = if ($phaseBaselineAcknowledged) { 'session_monitor' } else { 'play_monitor' }; pid = $ExpectedEditorPid; privateBytes = $currentPrivate; workingSetBytes = $currentWorkingSet; phasePrivateBaseline = $phasePrivateBaseline }

    $lines = if (Test-Path -LiteralPath $watchLog -PathType Leaf) { @(Get-Content -LiteralPath $watchLog) } else { @() }
    while ($seenEvents -lt $lines.Count) {
        $line = $lines[$seenEvents]
        $kind = Get-EventValue $line 'event'
        if ($kind -eq 'watcher_registered') { $summary.prePlayRawBaseline = [long](Get-EventValue $line 'rawGraphicsBytes') }
        if ($kind -eq 'play_started_normal_only' -and $null -eq $normalPrivateBaseline) { $normalPrivateBaseline = $currentPrivate }
        if ($kind -eq 'normal_phase_stability_candidate') { $normalStabilityCandidate = $true; $summary.normalStabilityCandidateObserved = $true; $candidateRawBaseline = [long](Get-EventValue $line 'rawGraphicsBytes') }
        if ($kind -eq 'normal_phase_baseline_fixed') {
            $phaseBaselineAcknowledged = $true
            $phaseReasonParts = ([string](Get-EventValue $line 'reason')).Split(',')
            if ($phaseReasonParts.Length -gt 0 -and $phaseReasonParts[0] -match '^\d+$') { $phasePrivateBaseline = [long]$phaseReasonParts[0]; $sessionPrivateBaseline = $phasePrivateBaseline; $summary.privatePhaseBaseline = $phasePrivateBaseline; $summary.normalPhaseRawBaseline = [long](Get-EventValue $line 'rawGraphicsBytes') }
        }
        if ($kind -eq 'bridge_start_called' -and (Get-EventValue $line 'reason') -eq 'accepted' -and $null -eq $sessionPrivateBaseline) { $sessionPrivateBaseline = $currentPrivate }
        $seenEvents++
    }

    if (-not $stopSent -and ($currentPrivate -le 0 -or $currentWorkingSet -le 0)) {
        [IO.File]::WriteAllText($stopFlag, 'external_memory_counter_unavailable' + "`n", [Text.Encoding]::ASCII)
        $summary.privateStopTriggered = $true
        $stopSent = $true
    }
    if (-not $stopSent -and $currentPrivate - [long]$summary.privateMemoryBaseline -ge $warmupLimitBytes) {
        [IO.File]::WriteAllText($stopFlag, 'private_growth_from_preplay_4gib_limit' + "`n", [Text.Encoding]::ASCII)
        $summary.privateStopTriggered = $true
        $stopSent = $true
    }

    if (-not $stopSent -and $phaseBaselineAcknowledged -and $null -ne $phasePrivateBaseline -and $currentPrivate - $phasePrivateBaseline -ge $privateSessionLimitBytes) {
        [IO.File]::WriteAllText($stopFlag, 'private_growth_from_stable_normal_baseline_4gib_limit' + "`n", [Text.Encoding]::ASCII)
        $summary.privateStopTriggered = $true
        $stopSent = $true
    }

    if (-not $stopSent -and $normalStabilityCandidate -and -not $phaseBaselineAcknowledged -and -not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
        $recent = @($privateSamples | Select-Object -Last 3)
        if ($recent.Count -eq 3) {
            $spanSeconds = ($recent[2].utc - $recent[0].utc).TotalSeconds
            $values = @([long]$recent[0].privateBytes, [long]$recent[1].privateBytes, [long]$recent[2].privateBytes)
            $rangeBytes = ($values | Measure-Object -Maximum).Maximum - ($values | Measure-Object -Minimum).Minimum
            if ($spanSeconds -ge 0.25 -and $rangeBytes -le $stabilityRangeBytes -and ($values | Where-Object { $_ -le 0 }).Count -eq 0) {
                $phasePrivateBaseline = $currentPrivate
                $ack = 'stable|' + $phasePrivateBaseline.ToString([Globalization.CultureInfo]::InvariantCulture) + '|' + ([long]$candidateRawBaseline).ToString([Globalization.CultureInfo]::InvariantCulture) + '|3|' + $spanSeconds.ToString('F3', [Globalization.CultureInfo]::InvariantCulture) + '|' + ([long]$rangeBytes).ToString([Globalization.CultureInfo]::InvariantCulture)
                $ackTemp = $baselinePath + '.tmp'
                [IO.File]::WriteAllText($ackTemp, $ack + "`n", [Text.Encoding]::ASCII)
                Move-Item -LiteralPath $ackTemp -Destination $baselinePath
                $phaseBaselineAcknowledged = $true
                $summary.normalWarmupStable = $true
                $summary.normalWarmupPrivateBaseline = $phasePrivateBaseline
                $summary.normalWarmupPrivateRangeBytes = [long]$rangeBytes
                $summary.normalWarmupPrivateSampleSpanSeconds = $spanSeconds
                $summary.normalWarmupRawCandidateBaseline = [long]$candidateRawBaseline
                $summary.privatePhaseBaseline = $phasePrivateBaseline
                $sessionPrivateBaseline = $phasePrivateBaseline
                Write-AsciiJsonLine $privateLog @{ utc = [DateTime]::UtcNow.ToString('o'); phase = 'normal_baseline_ack_written'; pid = $ExpectedEditorPid; privateBytes = $currentPrivate; workingSetBytes = $currentWorkingSet; rawCandidateBaseline = [long]$candidateRawBaseline; privateRangeBytes = [long]$rangeBytes; sampleSpanSeconds = $spanSeconds }
            }
        }
    }
    if ($lines | Where-Object { (Get-EventValue $_ 'event') -in @('play_stop_requested', 'play_start_cancelled', 'play_ended_unexpectedly') } | Select-Object -First 1) { break }
}

if ([DateTime]::UtcNow -ge $deadline) {
    $summary.outerTimeoutTriggered = $true
    if (-not $stopSent) { [IO.File]::WriteAllText($stopFlag, 'outer_timeout' + "`n", [Text.Encoding]::ASCII) }
}

$settleDeadline = [DateTime]::UtcNow.AddSeconds(15)
$postState = $null
$watcherUnsubscribedBeforeCleanupSnapshots = $false
do {
    Start-Sleep -Milliseconds 300
    $postState = Get-LiveState
    $settleWatchLines = if (Test-Path -LiteralPath $watchLog -PathType Leaf) { @(Get-Content -LiteralPath $watchLog) } else { @() }
    $watcherUnsubscribedBeforeCleanupSnapshots = @($settleWatchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'watcher_unsubscribed' }).Count -gt 0
    if (Test-CleanupSettledState -State $postState -ExpectedSessionEntityId $expectedSessionEntityId -ExpectedSceneAssetPath $expectedScenePath -WatcherUnsubscribed $watcherUnsubscribedBeforeCleanupSnapshots) { break }
} while ([DateTime]::UtcNow -lt $settleDeadline)

$postStoppedFields = $postState.Split('|')
if ($postStoppedFields.Length -lt 24 -or $postStoppedFields[0] -ne 'False' -or $postStoppedFields[1] -ne 'False' -or $postStoppedFields[7] -ne '1' -or $postStoppedFields[8] -ne 'Idle' -or $postStoppedFields[9] -ne 'Closed' -or
    $postStoppedFields[10] -ne '0' -or $postStoppedFields[11] -ne '0' -or $postStoppedFields[12] -ne 'True' -or
    $postStoppedFields[13] -ne 'False' -or $postStoppedFields[14] -ne 'False' -or
$postStoppedFields[19] -ne $expectedSessionEntityId -or $postStoppedFields[20] -ne $expectedSceneAssetPath -or
$postStoppedFields[21] -ne 'True' -or $postStoppedFields[22] -ne 'True' -or $postStoppedFields[23] -ne 'True' -or -not $watcherUnsubscribedBeforeCleanupSnapshots) {
    throw 'Post-Play session, scene, or cleanup invariants failed before restoring startOnEnable.'
}
$sceneBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($expectedScenePath))
$sessionBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($expectedSessionEntityId))
$watchLogBase64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($watchLog.Replace('\', '/')))
$expectedFlagLiteral = if ([bool]::Parse($expectedStartOnEnable)) { 'true' } else { 'false' }
$restoreCode = @"
return new System.Func<string>(() =>
{
    var renderers = UnityEngine.Object.FindObjectsByType<ViewWeave.QuiltRenderer>(UnityEngine.FindObjectsInactive.Include);
    if (renderers.Length != 1) return "restore_failed_renderer_count";
    var renderer = renderers[0];
    var session = renderer.GetComponent<ViewWeave.ViewWeaveBridgeSession>();
    var output = renderer.GetComponent<ViewWeave.BridgeWorkerQuiltOutput>();
    var rig = renderer.GetComponent<ViewWeave.ViewWeaveRig>();
    string expectedScene = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("$sceneBase64"));
    string expectedSession = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("$sessionBase64"));
    string logPath = System.Text.Encoding.UTF8.GetString(System.Convert.FromBase64String("$watchLogBase64"));
    var scene = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene();
    if (UnityEditor.EditorApplication.isPlaying || UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode) return "restore_failed_editor_playing";
    if (!scene.IsValid() || !scene.isLoaded || scene.path != expectedScene) return "restore_failed_scene_identity";
    if (session == null || output == null || rig == null || session.GetEntityId().ToString() != expectedSession) return "restore_failed_session_identity";
    if (session.State != ViewWeave.ViewWeaveBridgeSession.SessionState.Idle || output.State != ViewWeave.BridgeWorkerQuiltOutput.WorkerState.Closed ||
        ViewWeave.BridgeWorkerQuiltOutput.ShutdownPendingCount != 0 || renderer.sourceCamera == null ||
        renderer.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>() == null || renderer.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>().HistoryCameraCount != 0 ||
        renderer.QuiltTexture != null || renderer.autoRender || rig.AutoRender) return "restore_failed_cleanup_invariants";
    bool expected = $expectedFlagLiteral;
    bool before = session.startOnEnable;
    if (before != expected) session.startOnEnable = expected;
    if (session.startOnEnable != expected) return "restore_failed_flag_value";
    string row = "event=postplay_start_on_enable_restored|utc=" + System.DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture) +
        "|sessionEntityId=" + expectedSession + "|scenePath=" + expectedScene + "|expected=" + expected + "|before=" + before +
        "|after=" + session.startOnEnable + "|sceneSaveCalled=False";
    System.IO.File.AppendAllText(logPath, row + System.Environment.NewLine, System.Text.Encoding.ASCII);
    return "start_on_enable_restored|session=" + expectedSession + "|scene=" + expectedScene + "|value=" + session.startOnEnable;
})();
"@
[IO.File]::WriteAllText($asRunRestore, $restoreCode, [Text.UTF8Encoding]::new($false))
$restoreSourceHash = Get-Sha256 $asRunRestore
$restoreResponse = Invoke-UnityCommand @('command', 'eval_file', $asRunRestore)
try { $restoreParsed = $restoreResponse | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Post-Play restore response was not valid JSON.' }
$restoreResult = if ($restoreParsed.data.result.result) { [string]$restoreParsed.data.result.result } else { '' }
$postPlayRestorePassed = [bool]($restoreParsed.success -and $restoreParsed.data.result.success -and $restoreResult -match '^start_on_enable_restored\|')
if (-not $postPlayRestorePassed) {
    $safeRestoreResult = [regex]::Replace($restoreResult, '[^\x20-\x7E]', '?')
    if ($safeRestoreResult.Length -gt 160) { $safeRestoreResult = $safeRestoreResult.Substring(0, 160) }
    [IO.File]::AppendAllText($watchLog, 'event=postplay_start_on_enable_restore_failed|utc=' + [DateTime]::UtcNow.ToString('o') + '|sessionEntityId=' + $expectedSessionEntityId + '|scenePath=' + $expectedScenePath + '|result=' + $safeRestoreResult + '|sceneSaveCalled=False' + "`n", [Text.Encoding]::ASCII)
}
$summary.postPlayStartOnEnableRestoreSourcePath = $asRunRestore
$summary.postPlayStartOnEnableRestoreSourceSha256 = $restoreSourceHash
$summary.postPlayStartOnEnableRestoreResponse = $restoreResult
$summary.postPlayStartOnEnableRestorePassed = $postPlayRestorePassed
$postStateFirst = Get-LiveState
Start-Sleep -Milliseconds 300
$postState = Get-LiveState
$postRestoreRows = @(Get-Content -LiteralPath $watchLog | Where-Object { (Get-EventValue $_ 'event') -eq 'postplay_start_on_enable_restored' })
$postStateFirstFields = $postStateFirst.Split('|')
$postStateFields = $postState.Split('|')
$freshCleanupStateGate = $preStateFields.Length -ge 24 -and $postStateFirstFields.Length -ge 24 -and $postStateFields.Length -ge 24 -and
    $postStateFirstFields[0] -eq 'False' -and $postStateFields[0] -eq 'False' -and $postStateFirstFields[1] -eq 'False' -and $postStateFields[1] -eq 'False' -and
    $postStateFirstFields[7] -eq '1' -and $postStateFields[7] -eq '1' -and $postStateFirstFields[8] -eq 'Idle' -and $postStateFields[8] -eq 'Idle' -and
    $postStateFirstFields[9] -eq 'Closed' -and $postStateFields[9] -eq 'Closed' -and $postStateFirstFields[10] -eq '0' -and $postStateFields[10] -eq '0' -and
    $postStateFirstFields[11] -eq '0' -and $postStateFields[11] -eq '0' -and $postStateFirstFields[12] -eq 'True' -and $postStateFields[12] -eq 'True' -and
    $postStateFirstFields[13] -eq 'False' -and $postStateFields[13] -eq 'False' -and $postStateFirstFields[14] -eq 'False' -and $postStateFields[14] -eq 'False' -and
    $postStateFirstFields[15] -eq $preStateFields[15] -and $postStateFields[15] -eq $preStateFields[15] -and
    $postStateFirstFields[16] -eq $preStateFields[16] -and $postStateFields[16] -eq $preStateFields[16] -and
    $postStateFirstFields[17] -eq $preStateFields[17] -and $postStateFields[17] -eq $preStateFields[17] -and
    $postStateFirstFields[18] -eq $preStateFields[18] -and $postStateFields[18] -eq $preStateFields[18] -and
    $postStateFirstFields[19] -eq $preStateFields[19] -and $postStateFields[19] -eq $preStateFields[19] -and
    $postStateFirstFields[20] -eq $preStateFields[20] -and $postStateFields[20] -eq $preStateFields[20] -and
    $postStateFirstFields[21] -eq 'True' -and $postStateFields[21] -eq 'True' -and
    $postStateFirstFields[22] -eq 'True' -and $postStateFields[22] -eq 'True' -and
    $postStateFirstFields[23] -eq 'True' -and $postStateFields[23] -eq 'True' -and
    $postStateFirstFields[15] -eq $preStateFields[15] -and $postStateFields[15] -eq $preStateFields[15] -and
    $postRestoreRows.Count -eq 1 -and (Get-EventValue $postRestoreRows[0] 'sessionEntityId') -eq $expectedSessionEntityId -and
    (Get-EventValue $postRestoreRows[0] 'scenePath') -eq $expectedScenePath -and (Get-EventValue $postRestoreRows[0] 'expected') -eq $expectedStartOnEnable -and
    (Get-EventValue $postRestoreRows[0] 'after') -eq $expectedStartOnEnable -and (Get-EventValue $postRestoreRows[0] 'sceneSaveCalled') -eq 'False'
$watchStateOne = Get-EvidenceFileState $watchLog
$batchStateOne = Get-EvidenceFileState $batchLog
$privateStateOne = Get-EvidenceFileState $privateLog
Start-Sleep -Milliseconds 300
$watchStateTwo = Get-EvidenceFileState $watchLog
$batchStateTwo = Get-EvidenceFileState $batchLog
$privateStateTwo = Get-EvidenceFileState $privateLog
$evidenceFilesStable = ($null -ne $watchStateOne -and $null -ne $batchStateOne -and $null -ne $privateStateOne -and
    $watcherUnsubscribedBeforeCleanupSnapshots -and
    ($watchStateOne | ConvertTo-Json -Compress) -ceq ($watchStateTwo | ConvertTo-Json -Compress) -and
    ($batchStateOne | ConvertTo-Json -Compress) -ceq ($batchStateTwo | ConvertTo-Json -Compress) -and
    ($privateStateOne | ConvertTo-Json -Compress) -ceq ($privateStateTwo | ConvertTo-Json -Compress))

$postProcess = Get-Process -Id $ExpectedEditorPid -ErrorAction Stop
$sceneHashAfter = (Get-FileHash -Algorithm SHA256 -LiteralPath $scenePath).Hash
$sourcePrefabShaAfter = Get-Sha256 $sourcePrefabPath
$sourceGraphShaAfter = Get-Sha256 $sourceGraphPath
$treatmentGraphShaAfter = Get-Sha256 $treatmentGraphPath
$treatmentGraphMetaShaAfter = Get-Sha256 $treatmentGraphMetaPath
$applyResultShaAfter = Get-Sha256 $applyResultJsonPath
$treatmentGraphMetaGuidAfter = [regex]::Match([IO.File]::ReadAllText($treatmentGraphMetaPath), '(?m)^guid:\s*([0-9a-fA-F]{32})\s*$').Groups[1].Value.ToLowerInvariant()
$bridgeAfter = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedBridgePid" -ErrorAction Stop
$bridgeProcessAfter = Get-Process -Id $ExpectedBridgePid -ErrorAction Stop
$allUnityRowsAfter = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop)
$allEditorsAfter = @($allUnityRowsAfter | Where-Object { $_.CommandLine -and (Get-ProjectPathFromCommandLine ([string]$_.CommandLine)) -eq $projectPath.TrimEnd('\') })
$projectUnityIdsAfter = @($allEditorsAfter | ForEach-Object { [int]$_.ProcessId })
$exactEditorRowsAfter = @($allEditorsAfter | Where-Object {
    $_.ExecutablePath -and [IO.Path]::GetFullPath([string]$_.ExecutablePath).Equals([IO.Path]::GetFullPath($editorExe), [StringComparison]::OrdinalIgnoreCase)
})
$primaryEditorRowsAfter = @($exactEditorRowsAfter | Where-Object { $projectUnityIdsAfter -notcontains [int]$_.ParentProcessId })
$exactPrimaryEditorCountAfter = $primaryEditorRowsAfter.Count
$expectedPrimaryEditorAfter = $exactPrimaryEditorCountAfter -eq 1 -and [int]$primaryEditorRowsAfter[0].ProcessId -eq $ExpectedEditorPid
$unityPidsAfter = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | ForEach-Object { [int]$_.ProcessId } | Sort-Object -Unique)
$watchLines = if (Test-Path -LiteralPath $watchLog -PathType Leaf) { @(Get-Content -LiteralPath $watchLog) } else { @() }
$events = @($watchLines | ForEach-Object { Get-EventValue $_ 'event' })
$normalSampleRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'normal_only_sample' })
$normalSampleRawValues = @($normalSampleRows | ForEach-Object { [long](Get-EventValue $_ 'rawGraphicsBytes') })
$normalSampleTimes = @($normalSampleRows | ForEach-Object { [DateTime]::Parse((Get-EventValue $_ 'utc'), [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind) })
$normalSampleSpanSeconds = if ($normalSampleTimes.Count -ge 2) { ($normalSampleTimes[-1] - $normalSampleTimes[0]).TotalSeconds } else { $null }
$normalSampleRawMinimum = if ($normalSampleRawValues.Count -gt 0) { [long](($normalSampleRawValues | Measure-Object -Minimum).Minimum) } else { $null }
$normalSampleRawMaximum = if ($normalSampleRawValues.Count -gt 0) { [long](($normalSampleRawValues | Measure-Object -Maximum).Maximum) } else { $null }
$summary.status = if ($postState.Split('|')[0] -eq 'False' -and $sceneHashAfter -eq $sceneHashBefore -and $bridgeAfter -and $expectedPrimaryEditorAfter) { 'bounded_observation_finished' } else { 'bounded_observation_incomplete' }
$summary.playStartUtc = $playStartedUtc.ToString('o')
$summary.sceneSha256After = $sceneHashAfter
$summary.sceneHashUnchanged = ($sceneHashAfter -eq $sceneHashBefore)
$summary.sourcePrefabSha256After = $sourcePrefabShaAfter
$summary.sourcePrefabHashUnchanged = ($sourcePrefabShaAfter -eq $sourcePrefabShaBefore)
$summary.sourceGraphSha256After = $sourceGraphShaAfter
$summary.sourceGraphHashUnchanged = ($sourceGraphShaAfter -eq $sourceGraphShaBefore)
$summary.treatmentGraphSha256After = $treatmentGraphShaAfter
$summary.treatmentGraphHashUnchanged = ($treatmentGraphShaAfter -eq $treatmentGraphSha256)
$summary.treatmentGraphMetaSha256After = $treatmentGraphMetaShaAfter
$summary.treatmentGraphMetaHashUnchanged = ($treatmentGraphMetaShaAfter -eq (Get-Sha256 $asRunTreatmentGraphMeta))
$summary.treatmentGraphGuidAfter = $treatmentGraphMetaGuidAfter
$summary.treatmentGraphGuidUnchanged = ($treatmentGraphMetaGuidAfter -eq $treatmentGraphGuid)
$summary.originalApplyResultSha256After = $applyResultShaAfter
$summary.originalApplyResultHashUnchanged = ($applyResultShaAfter -eq $applyResultJsonSha256)
$summary.editorPrivateMemoryAfter = [long]$postProcess.PrivateMemorySize64
$summary.editorWorkingSetAfter = [long]$postProcess.WorkingSet64
$summary.bridgeStillPresent = [bool]$bridgeAfter
$summary.bridgeIdentityUnchanged = [bool]($bridgeAfter -and $bridgeProcessAfter.StartTime.ToUniversalTime().ToString('o') -eq $bridgeStartUtc)
$summary.exactProjectEditorCountAfter = $exactPrimaryEditorCountAfter
$summary.exactProjectPrimaryEditorPidAfter = if ($primaryEditorRowsAfter.Count -eq 1) { [int]$primaryEditorRowsAfter[0].ProcessId } else { $null }
$summary.projectTreeUnityProcessCountAfter = $allEditorsAfter.Count
$summary.projectDescendantUnityProcessCountAfter = [Math]::Max(0, $allEditorsAfter.Count - $exactPrimaryEditorCountAfter)
$summary.freshStateAfter = $postState
$summary.freshStateAfterFirst = $postStateFirst
$summary.freshCleanupStateGate = [bool]$freshCleanupStateGate
$summary.evidenceFileStabilityChecked = $true
$summary.evidenceFilesStableAfterPlayStop = [bool]$evidenceFilesStable
$summary.watcherUnsubscribedBeforeCleanupSnapshots = [bool]$watcherUnsubscribedBeforeCleanupSnapshots
$summary.evidenceFileStateFirst = [ordered]@{ watcher = $watchStateOne; batch = $batchStateOne; private = $privateStateOne }
$summary.evidenceFileStateSecond = [ordered]@{ watcher = $watchStateTwo; batch = $batchStateTwo; private = $privateStateTwo }
$summary.normalWarmupSampleCount = $normalSampleRows.Count
$summary.normalWarmupObservedRawMinimumBytes = $normalSampleRawMinimum
$summary.normalWarmupObservedRawMaximumBytes = $normalSampleRawMaximum
$summary.normalWarmupObservedSampleSpanSeconds = $normalSampleSpanSeconds
$summary.normalWarmupPhaseEnded = (@($events | Where-Object { $_ -in @('normal_phase_stability_candidate', 'normal_phase_stability_timeout') }).Count -gt 0)
$summary.events = $events
$historySnapshotRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'history_aa_snapshot' })
$batchSnapshotRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'batch_sweep' })
$historyStartRow = @($historySnapshotRows | Where-Object { (Get-EventValue $_ 'reason') -match '^ready_start,' } | Select-Object -First 1)
$historyEndRow = @($historySnapshotRows | Where-Object { (Get-EventValue $_ 'reason') -match '^ready_end,' } | Select-Object -First 1)
$batchStartRow = @($batchSnapshotRows | Where-Object { (Get-EventValue $_ 'reason') -match '^ready_start,' } | Select-Object -First 1)
$batchEndRow = @($batchSnapshotRows | Where-Object { (Get-EventValue $_ 'reason') -match '^ready_end,' } | Select-Object -First 1)
$historyRowPass = { param($row) $row -and (Get-EventValue $row 'reason') -match 'layoutViews=48' -and (Get-EventValue $row 'reason') -match 'renderedViews=12' -and (Get-EventValue $row 'reason') -match 'mapCount=12' -and (Get-EventValue $row 'reason') -match 'distinctCameraCount=12' -and (Get-EventValue $row 'reason') -match 'indicesMatch=True' -and (Get-EventValue $row 'reason') -match 'allTemporalAA=True' }
$batchRowPass = { param($row) $row -and (Get-EventValue $row 'reason') -match 'activeComponents=32' -and (Get-EventValue $row 'reason') -match 'uniqueAssets=32' -and (Get-EventValue $row 'reason') -match 'allAvailable=True' -and (Get-EventValue $row 'reason') -match 'selectedTreatmentGuid=' -and (Get-EventValue $row 'reason') -match 'selectedTreatmentPath=Assets/ViewWeave/ValidatedVFXOverrides/SampleTexture2D.vfx' -and (Get-EventValue $row 'reason') -match 'selectedTreatmentAllowInstancing=True' -and (Get-EventValue $row 'reason') -match 'selectedTreatmentGate=True' }
$summary.readyHistoryCameraAaStartGate = [bool](& $historyRowPass $historyStartRow)
$summary.readyHistoryCameraAaEndGate = [bool](& $historyRowPass $historyEndRow)
$startHistoryReason = if ($historyStartRow) { [string](Get-EventValue $historyStartRow 'reason') } else { '' }
$endHistoryReason = if ($historyEndRow) { [string](Get-EventValue $historyEndRow 'reason') } else { '' }
$startViewMatch = [regex]::Match($startHistoryReason, 'viewCount=(\d+)')
$endViewMatch = [regex]::Match($endHistoryReason, 'viewCount=(\d+)')
$summary.readyHistoryCameraAaViewCountStart = if ($startViewMatch.Success) { [int]$startViewMatch.Groups[1].Value } else { $null }
$summary.readyHistoryCameraAaViewCountEnd = if ($endViewMatch.Success) { [int]$endViewMatch.Groups[1].Value } else { $null }
$summary.readyBatchSweepStartGate = [bool](& $batchRowPass $batchStartRow)
$summary.readyBatchSweepEndGate = [bool](& $batchRowPass $batchEndRow)
$summary.readyBatchSweepLogPath = if (Test-Path -LiteralPath $batchLog -PathType Leaf) { $batchLog } else { $null }
$summary.readyBatchSweepLogSha256 = if (Test-Path -LiteralPath $batchLog -PathType Leaf) { Get-Sha256 $batchLog } else { $null }
$summary.readyEvidenceGate = $summary.readyHistoryCameraAaStartGate -and $summary.readyHistoryCameraAaEndGate -and $summary.readyBatchSweepStartGate -and $summary.readyBatchSweepEndGate
$summary.eventLogPath = $watchLog
$summary.privateMemoryLogPath = $privateLog
$summary.stopFlagPath = if (Test-Path -LiteralPath $stopFlag) { $stopFlag } else { $null }
$summary.outerPlayStopConfirmed = ($postState.Split('|')[0] -eq 'False')
$summary.normalOnlyPrivateBaselineDescriptive = $normalPrivateBaseline
$summary.afterStartSessionPrivateSampleDescriptive = $sessionPrivateBaseline
$summary.prePlayPrivateBaseline = [long]$summary.privateMemoryBaseline
$summary.warmupPrivateGrowthLimitBytes = $warmupLimitBytes
$summary.sessionPrivateGrowthLimitBytes = $privateSessionLimitBytes
$summary.privateDeltaFromBeforePlayPeak = [long]$summary.privateMemoryPeak - [long]$summary.privateMemoryBaseline
$summary.workingSetDeltaFromBeforePlayPeak = [long]$summary.workingSetPeak - [long]$summary.workingSetBaseline
$summary.bridgeStartAccepted = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'bridge_start_called' -and (Get-EventValue $_ 'reason') -eq 'accepted' }).Count)
$summary.readyProfileObserved = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'bridge_ready_measurement_started' }).Count)
$readyWindowCompleteRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'session_stop_requested' -and (Get-EventValue $_ 'reason') -eq 'bounded_ready_window_complete' })
$summary.readyWindowCompleted = $readyWindowCompleteRows.Count -eq 1
$summary.sdkErrorAbsentInCapturedRows = -not (@($watchLines | Where-Object {
    (Get-EventValue $_ 'event') -in @('connecting_fault', 'ready_fault', 'bridge_connect_exception', 'bridge_start_exception') -or
    (Get-EventValue $_ 'lastAutoErrorPresent') -eq 'True' -or (Get-EventValue $_ 'sessionErrorPresent') -eq 'True'
}).Count -gt 0)
$summary.bridgeIdleAndPendingZeroObserved = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'bridge_closed_idle' }).Count)
$summary.watcherUnsubscribedObserved = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'watcher_unsubscribed' }).Count)
$summary.originalRigProfileRestoreObserved = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'original_profile_restored' }).Count)
$summary.originalRigProfileRestoreFailureObserved = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'original_profile_restore_failed' }).Count)
$limitStopBatchRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'limit_stop_batch_snapshot' })
$limitStopBatchReason = if ($limitStopBatchRows.Count -eq 1) { [string](Get-EventValue $limitStopBatchRows[0] 'reason') } else { '' }
$limitStopAttemptedMatch = [regex]::Match($limitStopBatchReason, '(?:^|,)snapshotAttempted=(True|False)(?:,|$)')
$limitStopPassedMatch = [regex]::Match($limitStopBatchReason, '(?:^|,)snapshotPassed=(True|False)(?:,|$)')
$summary.limitStopBatchSnapshotCount = $limitStopBatchRows.Count
$summary.limitStopBatchSnapshotReason = $limitStopBatchReason
$summary.limitStopBatchSnapshotAttempted = $limitStopBatchRows.Count -eq 1 -and $limitStopAttemptedMatch.Success -and $limitStopAttemptedMatch.Groups[1].Value -eq 'True'
$summary.limitStopBatchSnapshotPassed = $limitStopBatchRows.Count -eq 1 -and $limitStopPassedMatch.Success -and $limitStopPassedMatch.Groups[1].Value -eq 'True'
$cameraSweepSamples = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'camera_sweep_revision_sample' })
$cameraSweepVisibleSamples = @($cameraSweepSamples | Where-Object { (Get-EventValue $_ 'reason') -match '(^|,)visibilityEligible=True(,|$)' })
$cameraSweepRestoreRow = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'camera_sweep_restored' } | Select-Object -Last 1)
$cameraSweepRestoreReason = if ($cameraSweepRestoreRow.Count -gt 0) { [string](Get-EventValue $cameraSweepRestoreRow[0] 'reason') } else { '' }
$cameraSweepMinYaw = [regex]::Match($cameraSweepRestoreReason, '(?:^|,)minYaw=(-?\d+(?:\.\d+)?)')
$cameraSweepMaxYaw = [regex]::Match($cameraSweepRestoreReason, '(?:^|,)maxYaw=(-?\d+(?:\.\d+)?)')
$cameraSweepPoseChanges = [regex]::Match($cameraSweepRestoreReason, '(?:^|,)poseChanges=(\d+)')
$lastCameraSweepReason = if ($cameraSweepSamples.Count -gt 0) { [string](Get-EventValue $cameraSweepSamples[-1] 'reason') } else { '' }
$lastCameraSweepPoseChanges = [regex]::Match($lastCameraSweepReason, '(?:^|,)poseChangeCount=(\d+)')
$summary.cameraSweepRevisionSampleCount = $cameraSweepSamples.Count
$summary.cameraSweepVisibilityEligibleRevisionSampleCount = $cameraSweepVisibleSamples.Count
$summary.cameraSweepAppliedPoseChanged = $lastCameraSweepPoseChanges.Success -and [int]$lastCameraSweepPoseChanges.Groups[1].Value -gt 0
$summary.cameraSweepRestored = $cameraSweepRestoreRow.Count -gt 0
$summary.cameraSweepLeftExcursionReached = $cameraSweepRestoreReason -match '(?:^|,)leftVisited=True(?:,|$)'
$summary.cameraSweepRightExcursionReached = $cameraSweepRestoreReason -match '(?:^|,)rightVisited=True(?:,|$)'
$summary.cameraSweepPoseChanges = if ($cameraSweepPoseChanges.Success) { [int]$cameraSweepPoseChanges.Groups[1].Value } else { 0 }
$summary.cameraSweepMinimumYawDegrees = if ($cameraSweepMinYaw.Success) { [double]::Parse($cameraSweepMinYaw.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) } else { $null }
$summary.cameraSweepMaximumYawDegrees = if ($cameraSweepMaxYaw.Success) { [double]::Parse($cameraSweepMaxYaw.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) } else { $null }
$summary.cameraSweepRestorationGate = $CameraMotionMode -eq 'manual_pan' -or ($summary.cameraSweepRestored -and $summary.cameraSweepLeftExcursionReached -and $summary.cameraSweepRightExcursionReached -and $summary.cameraSweepPoseChanges -gt 0 -and $summary.cameraSweepMinimumYawDegrees -le -1.0 -and $summary.cameraSweepMaximumYawDegrees -ge 1.0)
$cameraAnchorRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'camera_anchor_applied' })
$cameraAnchorSourceRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'camera_anchor_source_captured' })
$cameraAnchorObservedRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'camera_anchor_source_observed' })
$cameraAnchorFailureRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -in @('camera_anchor_precondition_failed', 'camera_sweep_precondition_failed') })
$summary.cameraAnchorAppliedObserved = $CameraMotionMode -eq 'manual_pan' -or $cameraAnchorRows.Count -eq 1
$summary.cameraAnchorAppliedReason = if ($cameraAnchorRows.Count -eq 1) { [string](Get-EventValue $cameraAnchorRows[0] 'reason') } else { $null }
$summary.cameraAnchorSourceObserved = $CameraMotionMode -eq 'manual_pan' -or $cameraAnchorSourceRows.Count -eq 1
$summary.cameraAnchorSourceReason = if ($cameraAnchorSourceRows.Count -eq 1) { [string](Get-EventValue $cameraAnchorSourceRows[0] 'reason') } else { $null }
$summary.cameraAnchorResolutionFailureCount = $cameraAnchorFailureRows.Count
$summary.cameraAnchorResolutionFailureReasons = @($cameraAnchorFailureRows | ForEach-Object { [string](Get-EventValue $_ 'reason') })
$summary.cameraAnchorObservedReason = if ($cameraAnchorObservedRows.Count -eq 1) { [string](Get-EventValue $cameraAnchorObservedRows[0] 'reason') } else { $null }
$sourceRootEntityIdMatch = [regex]::Match([string]$summary.cameraAnchorSourceReason, '(?:^|,)rootEntityId=([^,|]+)')
$observedRootEntityIdMatch = [regex]::Match([string]$summary.cameraAnchorObservedReason, '(?:^|,)rootEntityId=([^,|]+)')
$appliedRootEntityIdMatch = [regex]::Match([string]$summary.cameraAnchorAppliedReason, '(?:^|,)rootEntityId=([^,|]+)')
$sourceAnchorEntityIdMatch = [regex]::Match([string]$summary.cameraAnchorSourceReason, '(?:^|,)anchorEntityId=([^,|]+)')
$observedAnchorEntityIdMatch = [regex]::Match([string]$summary.cameraAnchorObservedReason, '(?:^|,)anchorEntityId=([^,|]+)')
$appliedAnchorEntityIdMatch = [regex]::Match([string]$summary.cameraAnchorAppliedReason, '(?:^|,)anchorEntityId=([^,|]+)')
$summary.cameraAnchorSourceRootEntityId = if ($sourceRootEntityIdMatch.Success) { $sourceRootEntityIdMatch.Groups[1].Value } else { $null }
$summary.cameraAnchorObservedRootEntityId = if ($observedRootEntityIdMatch.Success) { $observedRootEntityIdMatch.Groups[1].Value } else { $null }
$summary.cameraAnchorAppliedRootEntityId = if ($appliedRootEntityIdMatch.Success) { $appliedRootEntityIdMatch.Groups[1].Value } else { $null }
$summary.cameraAnchorSourceAnchorEntityId = if ($sourceAnchorEntityIdMatch.Success) { $sourceAnchorEntityIdMatch.Groups[1].Value } else { $null }
$summary.cameraAnchorObservedAnchorEntityId = if ($observedAnchorEntityIdMatch.Success) { $observedAnchorEntityIdMatch.Groups[1].Value } else { $null }
$summary.cameraAnchorAppliedAnchorEntityId = if ($appliedAnchorEntityIdMatch.Success) { $appliedAnchorEntityIdMatch.Groups[1].Value } else { $null }
$summary.cameraAnchorGate = $CameraMotionMode -eq 'manual_pan' -or ($cameraAnchorSourceRows.Count -eq 1 -and $cameraAnchorObservedRows.Count -eq 1 -and $cameraAnchorRows.Count -eq 1 -and
    $summary.cameraAnchorSourceReason -match 'source=SamplesShowcase\.samplesPrefabs' -and
    $summary.cameraAnchorSourceReason -match 'currentPrefabCaptured=True' -and
    $summary.cameraAnchorSourceReason -match 'treatmentRootGuid=3a08a8ebfe6ab4642b6f075038bdc1c3' -and
    $summary.cameraAnchorSourceReason -match 'originalCellGuid=7837c49e89b12db43b2a59edba6bff6b' -and
    $summary.cameraAnchorObservedReason -match 'source=SamplesShowcase\.samplesPrefabs' -and
    $summary.cameraAnchorAppliedReason -match 'source=SamplesShowcase\.samplesPrefabs' -and
    $sourceRootEntityIdMatch.Success -and $observedRootEntityIdMatch.Success -and $appliedRootEntityIdMatch.Success -and
    $sourceAnchorEntityIdMatch.Success -and $observedAnchorEntityIdMatch.Success -and $appliedAnchorEntityIdMatch.Success -and
    $summary.cameraAnchorSourceRootEntityId -eq $summary.cameraAnchorObservedRootEntityId -and
    $summary.cameraAnchorSourceRootEntityId -eq $summary.cameraAnchorAppliedRootEntityId -and
    $summary.cameraAnchorSourceAnchorEntityId -eq $summary.cameraAnchorObservedAnchorEntityId -and
    $summary.cameraAnchorSourceAnchorEntityId -eq $summary.cameraAnchorAppliedAnchorEntityId -and
    $summary.cameraAnchorAppliedReason -match 'treatmentRootGuid=3a08a8ebfe6ab4642b6f075038bdc1c3' -and
    $summary.cameraAnchorAppliedReason -match 'originalCellGuid=7837c49e89b12db43b2a59edba6bff6b' -and
    $summary.cameraAnchorAppliedReason -match 'anchorPath=.*Cell_01\.prefab/ViewPoint')
$centerHoldStartRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'camera_center_hold_started' })
$centerHoldCompletedRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'camera_center_hold_completed' })
$centerHoldFailedRows = @($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'camera_center_hold_failed' })
$centerHoldReason = if ($centerHoldCompletedRows.Count -eq 1) { [string](Get-EventValue $centerHoldCompletedRows[0] 'reason') } else { '' }
$centerHoldElapsedMatch = [regex]::Match($centerHoldReason, '(?:^|,)elapsedSeconds=(\d+(?:\.\d+)?)')
$centerHoldRevisionMatch = [regex]::Match($centerHoldReason, '(?:^|,)completedRevisionCount=(\d+)')
$centerHoldVisibleMatch = [regex]::Match($centerHoldReason, '(?:^|,)visibleRevisionCount=(\d+)')
$centerHoldSpanMatch = [regex]::Match($centerHoldReason, '(?:^|,)eligibleSpanSeconds=(\d+(?:\.\d+)?)')
$summary.cameraCenterHoldStartedCount = $centerHoldStartRows.Count
$summary.cameraCenterHoldCompletedCount = $centerHoldCompletedRows.Count
$summary.cameraCenterHoldFailedCount = $centerHoldFailedRows.Count
$summary.cameraCenterHoldElapsedSeconds = if ($centerHoldElapsedMatch.Success) { [double]::Parse($centerHoldElapsedMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) } else { $null }
$summary.cameraCenterHoldCompletedRevisions = if ($centerHoldRevisionMatch.Success) { [int]$centerHoldRevisionMatch.Groups[1].Value } else { 0 }
$summary.cameraCenterHoldVisibleRevisions = if ($centerHoldVisibleMatch.Success) { [int]$centerHoldVisibleMatch.Groups[1].Value } else { 0 }
$summary.cameraCenterHoldEligibleSpanSeconds = if ($centerHoldSpanMatch.Success) { [double]::Parse($centerHoldSpanMatch.Groups[1].Value, [Globalization.CultureInfo]::InvariantCulture) } else { $null }
$summary.cameraCenterHoldGate = $CameraMotionMode -eq 'manual_pan' -or ($centerHoldStartRows.Count -eq 1 -and $centerHoldCompletedRows.Count -eq 1 -and $centerHoldFailedRows.Count -eq 0 -and
    $summary.cameraCenterHoldCompletedRevisions -ge 3 -and $summary.cameraCenterHoldVisibleRevisions -ge 3 -and
    $null -ne $summary.cameraCenterHoldElapsedSeconds -and $summary.cameraCenterHoldElapsedSeconds -le 3.0 -and
    $null -ne $summary.cameraCenterHoldEligibleSpanSeconds -and $summary.cameraCenterHoldEligibleSpanSeconds -ge 0.5)
$summary.cameraSweepRevisionObservationGate = $CameraMotionMode -eq 'manual_pan' -or ($cameraSweepSamples.Count -gt 0 -and $summary.cameraSweepAppliedPoseChanged)
$summary.cameraSweepVisibilityEligibilityGate = $CameraMotionMode -eq 'manual_pan' -or ($cameraSweepSamples.Count -gt 0 -and $cameraSweepVisibleSamples.Count -gt 0)
$summary.cameraSweepPoseAssociation = if ($CameraMotionMode -eq 'automated_camera_sweep') { 'Pose is sampled after a quilt revision is observed; no render-callback pose attribution is claimed.' } else { 'manual_pan' }
$summary.cameraMotionGate = $summary.cameraAnchorGate -and $summary.cameraCenterHoldGate -and $summary.cameraSweepRestorationGate -and $summary.cameraSweepRevisionObservationGate -and $summary.cameraSweepVisibilityEligibilityGate
$summary.status = if ($postState.Split('|')[0] -eq 'False' -and $postState.Split('|')[1] -eq 'False' -and
    $sceneHashAfter -eq $sceneHashBefore -and $sourcePrefabShaAfter -eq $sourcePrefabShaBefore -and $sourceGraphShaAfter -eq $sourceGraphShaBefore -and
    $treatmentGraphShaAfter -eq $treatmentGraphSha256 -and $treatmentGraphMetaGuidAfter -eq $treatmentGraphGuid -and $applyResultShaAfter -eq $applyResultJsonSha256 -and
    $summary.bridgeIdentityUnchanged -and $expectedPrimaryEditorAfter -and $summary.readyEvidenceGate -and $summary.readyWindowCompleted -and
    $summary.sdkErrorAbsentInCapturedRows -and -not $summary.privateStopTriggered -and $summary.bridgeIdleAndPendingZeroObserved -and
    $summary.watcherUnsubscribedObserved -and $summary.postPlayStartOnEnableRestorePassed -and $freshCleanupStateGate -and $evidenceFilesStable -and
    $summary.cameraMotionGate -and -not $summary.outerTimeoutTriggered -and -not $summary.originalRigProfileRestoreFailureObserved) { 'bounded_observation_finished' } else { 'bounded_observation_incomplete' }
$summary.newUnityProcessPidsAfter = @($unityPidsAfter | Where-Object { $_ -notin $unityPidsBefore })
$summary.newUnityProcessCountAfter = $summary.newUnityProcessPidsAfter.Count
$summary.sceneSaveCalled = $false
$summary.endUtc = [DateTime]::UtcNow.ToString('o')
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadataPath -Encoding ascii
$summary | ConvertTo-Json -Depth 6
