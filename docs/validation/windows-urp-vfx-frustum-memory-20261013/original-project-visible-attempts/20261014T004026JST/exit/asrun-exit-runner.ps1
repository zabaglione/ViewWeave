#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)] [int]$ExpectedEditorPid,
    [Parameter(Mandatory = $true)] [string]$ExpectedEditorStartUtc,
    [Parameter(Mandatory = $true)] [int]$ExpectedBridgePid,
    [switch]$ScheduleExit
)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath('E:\URPSample_6.6.41f1')
$unity = 'C:\Users\zzaba\AppData\Local\Unity\bin\unity.exe'
$editorExe = [IO.Path]::GetFullPath('C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe')
$scene = Join-Path $project 'Assets\Samples\Visual Effect Graph\17.6.0\Learning Templates\Scenes\URP_VFX_Learning_Templates.unity'
$asset = Join-Path $project 'Assets\Samples\Visual Effect Graph\17.6.0\Learning Templates\VFX\SampleTexture2D.vfx'
$assetMeta = $asset + '.meta'
$template = Join-Path $PSScriptRoot 'ScheduleEditorExitEval.cs.txt'
$outRoot = Join-Path $PSScriptRoot 'exit-observations'
$ErrorActionPreference = 'Stop'

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

function Get-ProjectToken([string]$commandLine) {
    $args = [OriginalRemedy.NativeCommandLine]::Parse($commandLine)
    for ($i = 0; $i -lt $args.Length - 1; $i++) {
        if ($args[$i] -ieq '-projectPath' -or $args[$i] -ieq '--project-path') {
            try { return [IO.Path]::GetFullPath($args[$i + 1]).TrimEnd('\') } catch { return $null }
        }
    }
    return $null
}

function Get-ExactEditor {
    $row = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedEditorPid" -ErrorAction Stop
    if (-not $row -or $row.Name -ne 'Unity.exe' -or -not $row.ExecutablePath -or -not $row.CommandLine) { throw 'exact_editor_missing' }
    if (-not [IO.Path]::GetFullPath($row.ExecutablePath).Equals($editorExe, [StringComparison]::OrdinalIgnoreCase)) { throw 'editor_binary_mismatch' }
    $token = Get-ProjectToken $row.CommandLine
    if (-not $token -or -not $token.Equals($project.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'editor_project_token_mismatch' }
    $proc = Get-Process -Id $ExpectedEditorPid -ErrorAction Stop
    $actualStart = $proc.StartTime.ToUniversalTime()
    $expectedStart = [DateTime]::Parse($ExpectedEditorStartUtc, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal).ToUniversalTime()
    if ([Math]::Abs(($actualStart - $expectedStart).TotalSeconds) -gt 1.0) { throw 'editor_start_time_mismatch' }
    $matchingProjectRows = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | Where-Object {
        $_.CommandLine -and (Get-ProjectToken $_.CommandLine) -eq $project.TrimEnd('\')
    })
    $matchingIds = @($matchingProjectRows | ForEach-Object { [int]$_.ProcessId })
    $primaryRows = @($matchingProjectRows | Where-Object { $matchingIds -notcontains [int]$_.ParentProcessId })
    if ($primaryRows.Count -ne 1 -or [int]$primaryRows[0].ProcessId -ne $ExpectedEditorPid) { throw 'exact_project_primary_editor_count_mismatch' }
    return [pscustomobject]@{
        Cim = $row
        Process = $proc
        StartUtc = $actualStart.ToString('o')
        ProjectPrimaryCount = $primaryRows.Count
        ProjectTreeProcessCount = $matchingProjectRows.Count
        ProjectDescendantCount = [Math]::Max(0, $matchingProjectRows.Count - $primaryRows.Count)
    }
}

function Invoke-UnityJson([string[]]$arguments) {
    $allArgs = @($arguments) + @('--project-path', $project, '--format', 'json')
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $unity
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($arg in $allArgs) { $null = $startInfo.ArgumentList.Add([string]$arg) }
    $cliProcess = [Diagnostics.Process]::new()
    $cliProcess.StartInfo = $startInfo
    if (-not $cliProcess.Start()) { throw 'unity_cli_process_start_failed' }
    $stdoutTask = $cliProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $cliProcess.StandardError.ReadToEndAsync()
    if (-not $cliProcess.WaitForExit(30000)) {
        try { $cliProcess.Kill() } catch {}
        $cliProcess.WaitForExit(5000) | Out-Null
        try { [void][Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask), [TimeSpan]::FromSeconds(5)) } catch {}
        $cliProcess.Dispose()
        throw 'unity_cli_timeout'
    }
    try {
        $cliProcess.WaitForExit()
        $drained = $false
        try { $drained = [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutTask, $stderrTask), [TimeSpan]::FromSeconds(5)) } catch { $drained = $false }
        if (-not $drained) { throw 'unity_cli_output_drain_incomplete' }
        $body = $stdoutTask.GetAwaiter().GetResult()
        $null = $stderrTask.GetAwaiter().GetResult()
        $exit = $cliProcess.ExitCode
    }
    finally { $cliProcess.Dispose() }
    try { $json = $body | ConvertFrom-Json -ErrorAction Stop } catch {
        $script:LastUnityCliError = [ordered]@{ exitCode = $exit; code = 'NON_JSON_RESPONSE'; message = 'Response was not valid JSON.' }
        throw 'unity_cli_non_json_response'
    }
    if ($exit -ne 0 -or -not $json.success) {
        $code = if ($json.errors.Count -gt 0) { [string]$json.errors[0].code } else { 'unknown' }
        $message = if ($json.errors.Count -gt 0) { [string]$json.errors[0].message } else { [string]$json.message }
        $message = [regex]::Replace($message, "(?i)(access[_-]?token|authorization|bearer|token)\s*[:=]\s*[^,\s;]+", '$1=[REDACTED]')
        $message = [regex]::Replace($message, '[^\x20-\x7E]', '?')
        if ($message.Length -gt 400) { $message = $message.Substring(0, 400) }
        $script:LastUnityCliError = [ordered]@{ exitCode = $exit; code = $code; message = $message }
        throw ('unity_cli_failed_' + $code)
    }
    return $json
}

function Invoke-ReadOnlyState {
    $expr = 'var rs=UnityEngine.Object.FindObjectsByType<ViewWeave.QuiltRenderer>(UnityEngine.FindObjectsInactive.Include); if(rs.Length!=1)return "rendererCount="+rs.Length; var r=rs[0]; var g=r.GetComponent<ViewWeave.ViewWeaveRig>(); var s=r.GetComponent<ViewWeave.ViewWeaveBridgeSession>(); var o=r.GetComponent<ViewWeave.BridgeWorkerQuiltOutput>(); var a=UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene(); var u=r.sourceCamera==null?null:r.sourceCamera.GetComponent<ViewWeave.URP.UrpQuiltCamera>(); return "playing="+UnityEditor.EditorApplication.isPlaying+"|willPlay="+UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode+"|compiling="+UnityEditor.EditorApplication.isCompiling+"|scene="+a.path+"|sceneCount="+UnityEngine.SceneManagement.SceneManager.sceneCount+"|dirty="+a.isDirty+"|session="+s.State+"|worker="+o.State+"|pending="+ViewWeave.BridgeWorkerQuiltOutput.ShutdownPendingCount+"|auto="+r.autoRender+"|rigAuto="+g.AutoRender+"|history="+(u==null?-1:u.HistoryCameraCount)+"|quiltTextureNull="+(r.QuiltTexture==null)+"|lastAutoError="+(!System.String.IsNullOrEmpty(r.LastAutoRenderError))+"|sessionError="+(!System.String.IsNullOrEmpty(s.LastError))+"|frame="+UnityEngine.Time.frameCount+"|raw="+UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver()+"|quality="+s.Quality+"|viewMode="+r.viewRenderMode+"|profile="+g.Profile.QuiltWidth+"x"+g.Profile.QuiltHeight+","+g.Profile.Columns+"x"+g.Profile.Rows+",views="+g.Profile.Views;'
    $result = Invoke-UnityJson @('command', 'eval', '--caller', 'plugin', '--skill', 'unity-cli', $expr)
    if (-not $result.data.result.success) { throw 'readonly_live_state_failed' }
    return [string]$result.data.result.result
}

if (-not (Test-Path -LiteralPath $unity -PathType Leaf) -or -not (Test-Path -LiteralPath $scene -PathType Leaf) -or -not (Test-Path -LiteralPath $asset -PathType Leaf) -or -not (Test-Path -LiteralPath $assetMeta -PathType Leaf) -or -not (Test-Path -LiteralPath $template -PathType Leaf)) { throw 'fixed_input_missing' }
$version = (@(& $unity --version 2>$null) -join ' ').Trim()
if ($version -ne '1.0.0-beta.13') { throw 'unity_cli_version_mismatch' }
$editor = Get-ExactEditor
$bridge = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedBridgePid" -ErrorAction Stop
if (-not $bridge -or $bridge.Name -ne 'LookingGlassBridge.exe') { throw 'expected_bridge_missing' }
$bridgeStartUtc = (Get-Process -Id $ExpectedBridgePid -ErrorAction Stop).StartTime.ToUniversalTime().ToString('o')
$sceneHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $scene).Hash
$assetHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash
$assetGuid = (Select-String -LiteralPath $assetMeta -Pattern '^guid:\s*([0-9a-fA-F]{32})\s*$').Matches.Groups[1].Value
if ($assetGuid -ne 'e166f36ceb1831f46bea1e17fbc67c5a') { throw 'sample_texture_guid_mismatch' }
try {
    $stateBefore = Invoke-ReadOnlyState
}
catch {
    if ($script:LastUnityCliError) {
        if (-not (Test-Path -LiteralPath $outRoot -PathType Container)) { New-Item -ItemType Directory -Path $outRoot | Out-Null }
        $errorTag = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
        $errorDir = Join-Path $outRoot ($errorTag + '-cli-error')
        if (-not (Test-Path -LiteralPath $errorDir)) {
            New-Item -ItemType Directory -Path $errorDir | Out-Null
            $failure = [ordered]@{
                status = 'readonly_eval_failed_before_exit_schedule'
                capturedUtc = [DateTime]::UtcNow.ToString('o')
                editorPid = $ExpectedEditorPid
                editorStartUtc = $editor.StartUtc
                projectPath = $project
                unityCliError = $script:LastUnityCliError
                editorExitScheduled = $false
                bridgeTouched = $false
            }
            $failure | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $errorDir 'safe-error.json') -Encoding ascii
        }
    }
    throw
}
$fields = @{}
foreach ($part in $stateBefore.Split('|')) { $pair = $part.Split('=', 2); if ($pair.Length -eq 2) { $fields[$pair[0]] = $pair[1] } }
$required = @{
    playing = 'False'; willPlay = 'False'; compiling = 'False'; sceneCount = '1'; quiltTextureNull = 'True';
    scene = 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/URP_VFX_Learning_Templates.unity';
    dirty = 'False'; session = 'Idle'; worker = 'Closed'; pending = '0';
    auto = 'False'; rigAuto = 'False'; history = '0'; lastAutoError = 'False'; sessionError = 'False'
}
foreach ($key in $required.Keys) { if ($fields[$key] -cne $required[$key]) { throw ('live_gate_failed_' + $key) } }

$tag = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$runDir = Join-Path $outRoot $tag
if (Test-Path -LiteralPath $runDir) { throw 'evidence_path_exists' }
New-Item -ItemType Directory -Path $runDir | Out-Null
$summary = [ordered]@{
    status = if ($ScheduleExit) { 'exit_requested_after_fresh_gates' } else { 'readonly_preflight_passed' }
    capturedUtc = [DateTime]::UtcNow.ToString('o')
    unityCliVersion = $version
    projectPath = $project
    editorPid = $ExpectedEditorPid
    editorExecutable = $editor.Cim.ExecutablePath
    editorStartUtc = $editor.StartUtc
    projectPrimaryEditorCountBefore = $editor.ProjectPrimaryCount
    projectTreeUnityProcessCountBefore = $editor.ProjectTreeProcessCount
    projectDescendantUnityProcessCountBefore = $editor.ProjectDescendantCount
    bridgePid = $ExpectedBridgePid
    bridgeStartUtc = $bridgeStartUtc
    scenePath = $scene
    sceneSha256Before = $sceneHashBefore
    assetPath = $asset
    assetGuid = $assetGuid
    assetSha256Before = $assetHashBefore
    liveStateBefore = $stateBefore
    scheduledExit = [bool]$ScheduleExit
    naturalExitObserved = $false
    editorExitCode = $null
    exactProjectEditorCountAfter = $null
    bridgeStillPresentAfter = $null
    sceneSha256After = $null
    assetSha256After = $null
    noAssetOrSceneWriteClaim = $true
    exitMarkerPath = $null
}
Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $runDir 'asrun-exit-runner.ps1')
Copy-Item -LiteralPath $template -Destination (Join-Path $runDir 'asrun-exit-eval-template.cs.txt')

if ($ScheduleExit) {
    if (-not $editor.Process -or $editor.Process.HasExited) { throw 'editor_handle_invalid_before_schedule' }
    $marker = Join-Path $runDir 'exit-scheduled.txt'
    if ($stateBefore -notmatch 'scene=.*dirty=False' -or $sceneHashBefore.Length -ne 64) { throw 'exit_state_gate_failed' }
    $expr = (Get-Content -LiteralPath $template -Raw).Replace('__EXIT_MARKER_PATH__', $marker.Replace('\', '/'))
    $response = Invoke-UnityJson @('command', 'eval', '--caller', 'plugin', '--skill', 'unity-cli', $expr)
    $scheduled = [string]$response.data.result.result
    if (-not $response.data.result.success -or $scheduled -ne 'exit_scheduled_after_cli_response') { throw 'exit_schedule_eval_failed' }
    $summary.exitMarkerPath = $marker
    $summary.exitScheduleResponse = $scheduled
    $natural = $editor.Process.WaitForExit(30000)
    $summary.naturalExitObserved = [bool]$natural
    if ($natural) { $summary.editorExitCode = $editor.Process.ExitCode }
    $exactAfter = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | Where-Object {
        $_.CommandLine -and (Get-ProjectToken $_.CommandLine) -eq $project.TrimEnd('\')
    })
    $summary.exactProjectEditorCountAfter = $exactAfter.Count
    $bridgeAfter = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedBridgePid" -ErrorAction Stop
    $summary.bridgeStillPresentAfter = [bool]$bridgeAfter
    $summary.sceneSha256After = (Get-FileHash -Algorithm SHA256 -LiteralPath $scene).Hash
    $summary.assetSha256After = (Get-FileHash -Algorithm SHA256 -LiteralPath $asset).Hash
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'exit_marker_missing' }
    $summary.exitMarkerValue = (Get-Content -LiteralPath $marker -Raw).Trim()
    if (-not $natural -or $summary.editorExitCode -ne 0 -or $summary.exitMarkerValue -ne 'exit_requested_0' -or $exactAfter.Count -ne 0 -or -not $bridgeAfter -or $summary.sceneSha256After -ne $sceneHashBefore -or $summary.assetSha256After -ne $assetHashBefore) { $summary.status = 'graceful_exit_gate_incomplete' }
    else { $summary.status = 'natural_exit_zero_bridge_retained_sources_unchanged' }
}

$summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDir 'summary.json') -Encoding ascii
$summary | ConvertTo-Json -Depth 5
