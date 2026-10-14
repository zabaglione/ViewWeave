#requires -Version 7.0
param(
    [ValidateSet('stop-control', 'active-close', 'play-active-close')]
    [string]$Mode = 'stop-control',
    [ValidateSet('inherited-handles', 'redirected-pipes')]
    [string]$StdioMode = 'inherited-handles',
    [ValidateSet('editor-file', 'stdout')]
    [string]$LogMode = 'editor-file',
    [int]$ReadyTimeoutSeconds = 300,
    [int]$ExitTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
if ($LogMode -eq 'stdout' -and $StdioMode -ne 'redirected-pipes') {
    throw 'LogMode stdout requires StdioMode redirected-pipes.'
}
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$editor = 'C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe'
$project = 'E:\My project (34)'
$expectedPackageRoot = Join-Path $repo 'Distribution\hdrp'
$expectedPackageUri = 'file:E:/LookingGlassSDK/Distribution/hdrp'
$helperSource = Join-Path $PSScriptRoot 'WindowsEditorCloseValidation\EditorCloseValidationRunner.cs'
$evidenceRoot = Join-Path $repo '.local\windows-editor-close-crash-20261011'
$run = Join-Path $evidenceRoot ('hdrp-editor-' + $Mode + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
$helperName = 'ViewWeaveEditorCloseValidation_20261011.cs'
$helper = Join-Path $project ('Assets\Editor\' + $helperName)
$helperMeta = $helper + '.meta'
$packageManifestPath = Join-Path $project 'Packages\manifest.json'
$packageLockPath = Join-Path $project 'Packages\packages-lock.json'
$baselineRoot = Join-Path $run 'baseline'
$editorLog = Join-Path $run 'editor.log'
$unityLogArgument = if ($LogMode -eq 'stdout') { '-' } else { $editorLog }
$nativeTrace = Join-Path $run 'native-trace.log'
$stdoutCapture = Join-Path $run 'stdout.raw.log'
$stderrCapture = Join-Path $run 'stderr.raw.log'
$sourceHash = (Get-FileHash -LiteralPath $helperSource -Algorithm SHA256).Hash
$runnerSourceHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$ownedProcessHandle = [IntPtr]::Zero
$process = $null
$standardInputEndpoint = $null
$stdoutDrainTask = $null
$stderrDrainTask = $null
$stdoutCaptureStream = $null
$stderrCaptureStream = $null
$stdoutDrainCompleted = $null
$stderrDrainCompleted = $null
$drainDiagnosticComplete = $true
$launchUtc = [DateTime]::UtcNow
$started = $launchUtc
$ready = $null
$readyCondition = $null
$eventText = ''
$commandFingerprint = $null
$method = $null
$closeRequestedUtc = $null
$processEndUtc = $null
$nonAscii = 0
$exitCode = $null
$closeRequested = $false
$cleanupPass = $false
$originalTrace = $env:LKG_NATIVE_TRACE
$originalRoot = $env:VW_EDITOR_CLOSE_ROOT
$originalMode = $env:VW_EDITOR_CLOSE_MODE
$originalRequestedHdrp = $env:VW_EDITOR_CLOSE_REQUESTED_HDRP
$originalLockedHdrp = $env:VW_EDITOR_CLOSE_LOCKED_HDRP
$originalLockedHdrpSource = $env:VW_EDITOR_CLOSE_LOCKED_HDRP_SOURCE

Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class NativeCloseWindow {
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int processId);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);
    [DllImport("user32.dll", SetLastError=true)] public static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr handle);
}
'@

function Write-Ascii([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, [regex]::Replace($Text, '[^\x00-\x7F]', '?'), [Text.Encoding]::ASCII)
}

function Complete-RedirectedDrain([Diagnostics.Process]$Owner, [Threading.Tasks.Task]$Task, [string]$StreamName, [int]$TimeoutSeconds = 10) {
    if ($null -eq $Task) { return @{ completed = $true; recovery = 'not-redirected' } }
    if ($Task.Wait([TimeSpan]::FromSeconds($TimeoutSeconds))) {
        try {
            [void]$Task.GetAwaiter().GetResult()
            return @{ completed = $true; recovery = 'drained' }
        } catch {
            return @{ completed = $false; recovery = 'drain-task-faulted' }
        }
    }

    try {
        if ($StreamName -eq 'stdout') { $Owner.StandardOutput.Dispose() }
        elseif ($StreamName -eq 'stderr') { $Owner.StandardError.Dispose() }
    } catch { }
    [void]$Task.Wait(1000)
    return @{ completed = $false; recovery = 'timeout-reader-endpoint-closed-no-process-termination' }
}

function Get-FileManifest([string]$Root, [string[]]$RelativeRoots) {
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($relativeRoot in $RelativeRoots) {
        $absoluteRoot = Join-Path $Root $relativeRoot
        if (-not (Test-Path -LiteralPath $absoluteRoot -PathType Container)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $absoluteRoot -Recurse -File -Force) {
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
            $entries.Add([pscustomobject]@{
                path = $relative
                bytes = $file.Length
                sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            })
        }
    }
    return @($entries | Sort-Object path)
}

function Assert-OwnedPath([string]$Path, [string]$Boundary) {
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Boundary).TrimEnd('\') + '\'
    if (-not $full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) { throw 'Resolved path escaped its verified boundary.' }
    return $full
}

function Get-TargetUnityProcesses {
    $hubUnityService = 'C:\Program Files\Unity Hub\resources\unity.exe'
    @(Get-CimInstance Win32_Process | Where-Object {
        $_.Name -match '^(Unity\.exe|UnityPlayer\.exe)$' -and
        (-not $_.ExecutablePath -or -not [StringComparer]::OrdinalIgnoreCase.Equals($_.ExecutablePath, $hubUnityService))
    })
}

function Get-ProcessFingerprint([string]$CommandLine) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($CommandLine)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($algorithm.ComputeHash($bytes)).Replace('-', '') }
    finally { $algorithm.Dispose() }
}

function Restore-Tree([string]$RelativeRoot, [string]$BackupPath, [object[]]$Baseline, [string[]]$OwnedCreatedFiles) {
    $targetRoot = Join-Path $project $RelativeRoot
    $current = Get-FileManifest $project @($RelativeRoot)
    $baselineMap = @{}; foreach ($item in $Baseline) { $baselineMap[$item.path] = $item }
    $currentMap = @{}; foreach ($item in $current) { $currentMap[$item.path] = $item }
    $unexpected = [System.Collections.Generic.List[string]]::new()
    foreach ($item in $current) {
        if (-not $baselineMap.ContainsKey($item.path)) {
            $relative = $item.path.Substring($RelativeRoot.Length + 1).Replace('/', '\')
            $full = Assert-OwnedPath (Join-Path $targetRoot $relative) $targetRoot
            if ($OwnedCreatedFiles -contains $item.path) {
                Remove-Item -LiteralPath $full -Force
            } else {
                $unexpected.Add($item.path)
            }
        }
    }
    foreach ($item in $Baseline) {
        $relative = $item.path.Substring($RelativeRoot.Length + 1).Replace('/', '\')
        $target = Assert-OwnedPath (Join-Path $targetRoot $relative) $targetRoot
        $backup = Assert-OwnedPath (Join-Path $BackupPath $relative) $BackupPath
        if (-not $currentMap.ContainsKey($item.path) -or $currentMap[$item.path].sha256 -ne $item.sha256) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $backup -Destination $target -Force
        }
    }
    return @($unexpected)
}

function Get-WindowCaption([IntPtr]$Handle) {
    $buffer = [Text.StringBuilder]::new(512)
    [void][NativeCloseWindow]::GetWindowText($Handle, $buffer, $buffer.Capacity)
    return $buffer.ToString()
}
function Get-WindowClass([IntPtr]$Handle) {
    $buffer = [Text.StringBuilder]::new(256)
    [void][NativeCloseWindow]::GetClassName($Handle, $buffer, $buffer.Capacity)
    return $buffer.ToString()
}

function Get-ExactPidApplicationEvents([int]$OwnedProcessId, [DateTime]$FromUtc) {
    $result = [System.Collections.Generic.List[object]]::new()
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $FromUtc.ToLocalTime(); Id = 1000, 1001 } -ErrorAction SilentlyContinue)
    foreach ($eventRecord in $events) {
        $matched = $false
        try {
            $xml = [xml]$eventRecord.ToXml()
            foreach ($data in @($xml.Event.EventData.Data)) {
                if ($null -ne $data -and $data.Name -match 'ProcessId|AppProcessId|PID') {
                    $text = [string]$data.'#text'
                    if ($text -match '^0x([0-9a-fA-F]+)$') { $matched = ([Convert]::ToInt64($Matches[1], 16) -eq $OwnedProcessId) }
                    elseif ($text -match '^\d+$') { $matched = ([int64]$text -eq $OwnedProcessId) }
                    if ($matched) { break }
                }
            }
        } catch { }
        if ($matched) {
            $result.Add(@{ eventId = $eventRecord.Id; recordId = $eventRecord.RecordId; timeUtc = $eventRecord.TimeCreated.ToUniversalTime().ToString('o'); exactProcessIdMatch = $true })
        }
    }
    return @($result)
}

function Get-NativeTraceSummary([int]$OwnedProcessId, [string]$Path) {
    $text = if (Test-Path -LiteralPath $Path -PathType Leaf) { [IO.File]::ReadAllText($Path) } else { '' }
    $lines = @($text -split "`r?`n" | Where-Object { $_ })
    $pidLines = @($lines | Where-Object { $_ -match ('^shutdown pid=' + $OwnedProcessId + ' ') })
    $threadIds = @()
    foreach ($line in $pidLines) { if ($line -match '^shutdown pid=\d+ tid=(\d+) ') { $threadIds += $Matches[1] } }
    $threadIds = @($threadIds | Sort-Object -Unique)
    $threadPattern = if ($threadIds.Count -gt 0) { 'thread_id=(' + (($threadIds | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')' } else { '(?!)' }
    $threadLines = @($lines | Where-Object { $_ -match $threadPattern })
    $correlated = @($pidLines + $threadLines | Sort-Object -Unique)
    $load = @($correlated | Where-Object { $_ -match '^load_end result=0 thread_id=' })
    $initialize = @($correlated | Where-Object { $_ -match '^initialize_end result=0 thread_id=' })
    $uninitialize = @($pidLines | Where-Object { $_ -match 'event=uninitialize_bridge end result=1(?:\s|$)' })
    $pendingZero = @($pidLines | Where-Object { $_ -match 'stage=cleanup-end pendingTokens=0 jobCount=0 inFlightJob=0(?:\s|$)' })
    $closeComplete = @($correlated | Where-Object { $_ -match '\bclose_complete result=0\b' })
    $closeFailed = @($correlated | Where-Object { $_ -match '\bclose_failed result=-?\d+\b|\bsession_close_failed\b' })
    $finalShutdown = @($pidLines | Where-Object { $_ -match 'event=uninitialize_bridge end result=1 .*source=worker_host_final_shutdown' })
    return @{
        present = (Test-Path -LiteralPath $Path -PathType Leaf)
        sha256 = if (Test-Path -LiteralPath $Path -PathType Leaf) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash } else { $null }
        expectedNativeThreadsCorrelatedByOwnedPid = $threadIds
        loadSucceededObserved = ($load.Count -gt 0)
        initializeSucceededObserved = ($initialize.Count -gt 0)
        uninitializeBridgeSucceededForOwnedPid = ($uninitialize.Count -gt 0)
        finalShutdownUninitializeSucceededForOwnedPid = ($finalShutdown.Count -gt 0)
        pendingTokensZeroObservedForOwnedPid = ($pendingZero.Count -gt 0)
        nativeCleanupConfirmed = ($uninitialize.Count -gt 0 -and $pendingZero.Count -gt 0)
        diagnosticComplete = ($load.Count -gt 0 -and $initialize.Count -gt 0 -and $uninitialize.Count -gt 0 -and $pendingZero.Count -gt 0)
        nativeCloseCompleteCount = $closeComplete.Count
        nativeCloseFailureCount = $closeFailed.Count
        nativeTraceScope = 'load/init thread IDs are correlated through shutdown lines carrying the owned process PID; native trace has no PID on load/init rows'
    }
}

function Get-MaxTelemetry([string]$Text, [string]$Field) {
    $matches = [regex]::Matches($Text, ('\b' + [regex]::Escape($Field) + '=(\d+)'))
    if ($matches.Count -eq 0) { return $null }
    return [int](($matches | ForEach-Object { [int]$_.Groups[1].Value } | Measure-Object -Maximum).Maximum)
}

function Get-OwnedHelperCompileErrors([string]$LogPath, [string]$OwnedHelperName) {
    if (-not (Test-Path -LiteralPath $LogPath -PathType Leaf)) { return @() }
    $matchesFound = [System.Collections.Generic.List[string]]::new()
    try {
        $stream = [IO.File]::Open($LogPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
        try {
            $reader = [IO.StreamReader]::new($stream)
            while (-not $reader.EndOfStream) {
                $line = $reader.ReadLine()
                if ($line.Contains($OwnedHelperName) -and $line -match 'error CS\d+') {
                    $matchesFound.Add([regex]::Replace($line, '[^\x00-\x7F]', '?'))
                    if ($matchesFound.Count -ge 8) { break }
                }
            }
            $reader.Dispose()
        } finally { $stream.Dispose() }
    } catch { }
    return @($matchesFound)
}

if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) { throw 'Unity Editor 6000.6.4f1 was not found.' }
if (-not (Test-Path -LiteralPath $project -PathType Container)) { throw 'HDRP sample project was not found.' }
if (-not (Test-Path -LiteralPath $helperSource -PathType Leaf)) { throw 'Diagnostic helper source was not found.' }
if (-not (Test-Path -LiteralPath (Join-Path $project 'Assets\Editor') -PathType Container)) { throw 'Project Assets/Editor folder is missing.' }
if (Test-Path -LiteralPath $run) { throw 'Evidence directory already exists; refusing to reuse an earlier attempt.' }
if ((Test-Path -LiteralPath $helper) -or (Test-Path -LiteralPath $helperMeta)) { throw 'Owned helper destination already exists.' }
if (-not (Test-Path -LiteralPath $packageManifestPath -PathType Leaf) -or -not (Test-Path -LiteralPath $packageLockPath -PathType Leaf)) { throw 'HDRP package manifest or lock file is missing.' }
$manifestText = [IO.File]::ReadAllText($packageManifestPath)
if (-not $manifestText.Contains($expectedPackageUri)) { throw 'HDRP project does not use the expected local Distribution package.' }
$projectManifest = Get-Content -LiteralPath $packageManifestPath -Raw | ConvertFrom-Json
$projectLock = Get-Content -LiteralPath $packageLockPath -Raw | ConvertFrom-Json
$hdrpEntry = $projectLock.dependencies.'com.zabaglione.lookingglass.viewweave.hdrp'
if ($null -eq $hdrpEntry -or $hdrpEntry.version -ne 'file:E:/LookingGlassSDK/Distribution/hdrp') { throw 'HDRP lock entry does not resolve to the expected local package.' }
$viewWeavePackageVersion = (Get-Content -LiteralPath (Join-Path $expectedPackageRoot 'package.json') -Raw | ConvertFrom-Json).version
$requestedManifestHdrpVersion = $projectManifest.dependencies.'com.unity.render-pipelines.high-definition'
$lockedHdrpEntry = $projectLock.dependencies.'com.unity.render-pipelines.high-definition'
$lockedHdrpVersion = if ($null -ne $lockedHdrpEntry) { $lockedHdrpEntry.version } else { $null }
$lockedHdrpSource = if ($null -ne $lockedHdrpEntry) { $lockedHdrpEntry.source } else { $null }
if (-not $viewWeavePackageVersion -or -not $requestedManifestHdrpVersion -or -not $lockedHdrpVersion) {
    throw 'HDRP manifest, package lock, or local ViewWeave package metadata is incomplete.'
}

$otherUnity = Get-TargetUnityProcesses
if ($otherUnity.Count -gt 0) { throw 'Unity Editor/Player processes are present; no process was touched.' }

New-Item -ItemType Directory -Path $run -Force | Out-Null
New-Item -ItemType Directory -Path $baselineRoot -Force | Out-Null
$protectedRoots = @('Assets', 'ProjectSettings', 'Packages', 'UserSettings')
$baseline = Get-FileManifest $project $protectedRoots
Write-Ascii (Join-Path $run 'baseline-manifest.json') ($baseline | ConvertTo-Json -Depth 4)
foreach ($relative in $protectedRoots) {
    $source = Join-Path $project $relative
    if (Test-Path -LiteralPath $source -PathType Container) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $baselineRoot $relative) -Recurse
    }
}
$baselineAssets = @($baseline | Where-Object { $_.path -like 'Assets/*' })
$originalPackageManifest = (Get-FileHash -LiteralPath $packageManifestPath -Algorithm SHA256).Hash
$originalPackageLock = (Get-FileHash -LiteralPath $packageLockPath -Algorithm SHA256).Hash
$started = [DateTime]::UtcNow
$runMetadata = [ordered]@{
    schema = 1; mode = $Mode; project = $project; requestedManifestHdrpVersion = $requestedManifestHdrpVersion
    lockedHdrpVersion = $lockedHdrpVersion; lockedHdrpSource = $lockedHdrpSource; actualResolvedHdrpVersion = 'captured by PackageInfo at runtime'
    manifestLockVersionMismatch = ($requestedManifestHdrpVersion -ne $lockedHdrpVersion)
    viewWeaveHdrpPackageVersion = $viewWeavePackageVersion
    expectedPackageRoot = $expectedPackageRoot; packageUri = $expectedPackageUri
    unityEditor = $editor; unityVersion = '6000.6.4f1'; requiredGraphicsApi = 'Direct3D12'
    helperSource = [IO.Path]::GetRelativePath($repo, $helperSource).Replace('\', '/')
    helperSha256 = $sourceHash; runnerScript = [IO.Path]::GetRelativePath($repo, $PSCommandPath).Replace('\', '/')
    runnerScriptSha256 = $runnerSourceHash; startedUtc = $started.ToString('o')
    packageManifestSha256Before = $originalPackageManifest; packageLockSha256Before = $originalPackageLock
    launchMode = 'hidden GUI Editor; no batchmode; no quit'
    logMode = $LogMode; unityLogArgument = $unityLogArgument
    logArgumentDocumentation = 'https://docs.unity.com/en-us/engine/6000.6/manual/unity-editor/command-line-arguments/editor'
    stdioMode = $StdioMode; stdinRedirected = ($StdioMode -eq 'redirected-pipes')
    stdoutRedirected = ($StdioMode -eq 'redirected-pipes'); stderrRedirected = ($StdioMode -eq 'redirected-pipes')
}
Write-Ascii (Join-Path $run 'run-inputs.json') ($runMetadata | ConvertTo-Json -Depth 5)
$processStart = $null
$nativeCrashEvents = 0
$helperCopied = $false

try {
    Copy-Item -LiteralPath $helperSource -Destination $helper
    $helperCopied = $true
    $env:VW_EDITOR_CLOSE_ROOT = $run
    $env:VW_EDITOR_CLOSE_MODE = $Mode
    $env:VW_EDITOR_CLOSE_REQUESTED_HDRP = $requestedManifestHdrpVersion
    $env:VW_EDITOR_CLOSE_LOCKED_HDRP = $lockedHdrpVersion
    $env:VW_EDITOR_CLOSE_LOCKED_HDRP_SOURCE = $lockedHdrpSource
    $env:LKG_NATIVE_TRACE = $nativeTrace
    $arguments = @(
        '-projectPath', ('"' + $project + '"'),
        '-force-d3d12',
        '-executeMethod', 'ViewWeave.WindowsEditorCloseValidation.EditorCloseValidationRunner.Start',
        '-logFile', ('"' + $unityLogArgument + '"')
    )
    if ($StdioMode -eq 'redirected-pipes') {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $editor
        $startInfo.WorkingDirectory = Split-Path -Parent $editor
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @('-projectPath', $project, '-force-d3d12', '-executeMethod',
            'ViewWeave.WindowsEditorCloseValidation.EditorCloseValidationRunner.Start', '-logFile', $unityLogArgument)) {
            $startInfo.ArgumentList.Add([string]$argument)
        }
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw 'Unity Editor process did not start.' }
        $standardInputEndpoint = $process.StandardInput
        $stdoutCaptureStream = [IO.File]::Open($stdoutCapture, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $stderrCaptureStream = [IO.File]::Open($stderrCapture, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $stdoutDrainTask = $process.StandardOutput.BaseStream.CopyToAsync($stdoutCaptureStream)
        $stderrDrainTask = $process.StandardError.BaseStream.CopyToAsync($stderrCaptureStream)
    } else {
        $process = Start-Process -FilePath $editor -ArgumentList $arguments -PassThru -WindowStyle Hidden
    }
    $processStart = $process.StartTime.ToUniversalTime()
    $launchPid = $process.Id
    $ownedProcessHandle = $process.Handle
    $expectedTokens = @('-projectPath', $project, '-force-d3d12', '-executeMethod', 'ViewWeave.WindowsEditorCloseValidation.EditorCloseValidationRunner.Start', '-logFile', $unityLogArgument)
    $initialIdentity = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $launchPid)
    if ($null -eq $initialIdentity -or -not [StringComparer]::OrdinalIgnoreCase.Equals($initialIdentity.ExecutablePath, $editor)) {
        throw 'Started process did not resolve to the expected Unity Editor executable.'
    }
    foreach ($token in $expectedTokens) {
        if (-not $initialIdentity.CommandLine.Contains($token)) { throw 'Started process command line did not contain the expected owned launch arguments.' }
    }
    $initialCommandFingerprint = Get-ProcessFingerprint $initialIdentity.CommandLine
    Write-Ascii (Join-Path $run 'process-start.json') (@{
        pid = $launchPid; startTimeUtc = $processStart.ToString('o'); executable = $editor
        commandLineSha256 = $initialCommandFingerprint; requiredCommandTokens = $expectedTokens
        stdioMode = $StdioMode; redirectedStdin = ($StdioMode -eq 'redirected-pipes')
        redirectedStdout = ($StdioMode -eq 'redirected-pipes'); redirectedStderr = ($StdioMode -eq 'redirected-pipes')
        logMode = $LogMode; unityLogArgument = $unityLogArgument
        stdinEndpointRetainedUntilExit = ($StdioMode -eq 'redirected-pipes'); stdoutCapture = if ($StdioMode -eq 'redirected-pipes') { 'stdout.raw.log' } else { $null }
        stderrCapture = if ($StdioMode -eq 'redirected-pipes') { 'stderr.raw.log' } else { $null }; windowStyle = 'Hidden'
    } | ConvertTo-Json -Depth 4)

    $readyPath = Join-Path $run 'close-ready.json'
    $readyDeadline = [DateTime]::UtcNow.AddSeconds($ReadyTimeoutSeconds)
    while ([DateTime]::UtcNow -lt $readyDeadline -and -not $process.HasExited -and -not (Test-Path -LiteralPath $readyPath)) {
        if (Test-Path -LiteralPath (Join-Path $run 'helper-failed.txt')) { break }
        $compileLogPaths = if ($LogMode -eq 'stdout') { @($stdoutCapture, $stderrCapture) } else { @($editorLog) }
        $compileErrors = @($compileLogPaths | ForEach-Object { Get-OwnedHelperCompileErrors $_ $helperName })
        if ($compileErrors.Count -gt 0) {
            Write-Ascii (Join-Path $run 'helper-compile-errors.txt') ($compileErrors -join "`r`n")
            throw 'Unity reported compile errors in the owned diagnostic helper; raw editor log retained privately.'
        }
        Start-Sleep -Milliseconds 500
    }
    if (-not (Test-Path -LiteralPath $readyPath)) { throw 'Helper did not publish the required close-ready barrier; Editor left open for inspection.' }
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if (-not $ready.ready -or $ready.pid -ne $launchPid -or $ready.mode -ne $Mode -or
        $ready.unityVersion -ne '6000.6.4f1' -or $ready.graphicsApi -ne 'Direct3D12' -or
        [string]::IsNullOrWhiteSpace([string]$ready.actualResolvedHdrpVersion) -or $ready.actualResolvedHdrpVersion -eq 'unresolved' -or
        $ready.requestedManifestHdrpVersion -ne $requestedManifestHdrpVersion -or $ready.lockedHdrpVersion -ne $lockedHdrpVersion -or
        $ready.lockedHdrpSource -ne $lockedHdrpSource) {
        throw 'Close-ready manifest does not match the owned process, mode, Unity version, and D3D12 API.'
    }
    if (($Mode -eq 'stop-control' -and $ready.condition -ne 'stop-control-pending-zero') -or
        ($Mode -eq 'active-close' -and $ready.condition -ne 'active-device-output') -or
        ($Mode -eq 'play-active-close' -and $ready.condition -ne 'play-active-device-output')) {
        throw 'Close-ready state does not match the requested control condition.'
    }

    $live = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $launchPid)
    if ($null -eq $live -or -not [StringComparer]::OrdinalIgnoreCase.Equals($live.ExecutablePath, $editor)) {
        throw 'Owned process identity no longer matches the expected Unity Editor executable.'
    }
    $liveStart = if ($live.CreationDate -is [DateTime]) {
        $live.CreationDate.ToUniversalTime()
    } else {
        [Management.ManagementDateTimeConverter]::ToDateTime([string]$live.CreationDate).ToUniversalTime()
    }
    if ([Math]::Abs(($liveStart - $processStart).TotalSeconds) -gt 2) { throw 'Owned process start identity changed before close request.' }
    foreach ($token in $expectedTokens) {
        if (-not $live.CommandLine.Contains($token)) { throw 'Owned process command identity is missing a required launch token.' }
    }
    $commandFingerprint = Get-ProcessFingerprint $live.CommandLine
    if ($commandFingerprint -ne $initialCommandFingerprint) { throw 'Owned process command fingerprint changed before the close request.' }
    if ($ownedProcessHandle -eq [IntPtr]::Zero) { throw 'Owned process handle was not retained before the close request.' }
    $ownedWindows = [System.Collections.Generic.List[object]]::new()
    $callback = [NativeCloseWindow+EnumWindowsProc]{
        param([IntPtr]$window, [IntPtr]$state)
        $windowPid = 0
        [void][NativeCloseWindow]::GetWindowThreadProcessId($window, [ref]$windowPid)
        if ($windowPid -eq $launchPid) {
            $caption = Get-WindowCaption $window
            $class = Get-WindowClass $window
            $ownedWindows.Add([pscustomobject]@{ handle = $window; pid = $windowPid; caption = $caption; className = $class })
        }
        return $true
    }
    [void][NativeCloseWindow]::EnumWindows($callback, [IntPtr]::Zero)
    $windowEvidence = @($ownedWindows | ForEach-Object {
        @{ handle = ('0x{0:X}' -f $_.handle.ToInt64()); pid = $_.pid
           caption = [regex]::Replace($_.caption, '[^\x00-\x7F]', '?')
           className = [regex]::Replace($_.className, '[^\x00-\x7F]', '?') }
    })
    Write-Ascii (Join-Path $run 'owned-window-observations.json') ($windowEvidence | ConvertTo-Json -Depth 4)
    $candidates = @($ownedWindows | Where-Object {
        $_.caption.Contains('My project (34)') -and $_.className -eq 'UnityContainerWndClass'
    })
    if ($candidates.Count -ne 1) { throw 'Expected exactly one exact-owned Unity project container window; no close was requested.' }
    $mainWindow = $candidates[0].handle
    $method = 'WM_CLOSE_exact_pid_UnityContainerWndClass_project_window'
    $closeResult = [NativeCloseWindow]::PostMessage($mainWindow, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero)
    if (-not $closeResult) { throw 'No exact-owned Unity main window accepted a normal close request.' }
    $closeRequestedUtc = [DateTime]::UtcNow
    $closeRequested = $true
    Write-Ascii (Join-Path $run 'close-request.json') (@{
        requestedUtc = $closeRequestedUtc.ToString('o'); pid = $launchPid; startTimeUtc = $processStart.ToString('o')
        executable = $editor; commandLineSha256 = $commandFingerprint; requiredCommandTokens = $expectedTokens
        windowHandle = ('0x{0:X}' -f $mainWindow.ToInt64()); closeMethod = $method; accepted = $closeResult
        condition = $ready.condition; forcedTermination = $false
    } | ConvertTo-Json -Depth 4)
    $waitResult = [NativeCloseWindow]::WaitForSingleObject($ownedProcessHandle, [uint32]($ExitTimeoutSeconds * 1000))
    if ($waitResult -eq 0x00000102) {
        Write-Ascii (Join-Path $run 'close-timeout.txt') ('GUI close request timed out; process left running. pid=' + $launchPid + ' method=' + $method)
        throw 'GUI close request did not exit before deadline; process left running for inspection.'
    }
    if ($waitResult -ne 0) { throw 'Waiting on the retained owned process handle failed.' }
    $process.WaitForExit()
    $stdoutDrainResult = Complete-RedirectedDrain $process $stdoutDrainTask 'stdout'
    $stderrDrainResult = Complete-RedirectedDrain $process $stderrDrainTask 'stderr'
    $stdoutDrainCompleted = $stdoutDrainResult.completed
    $stderrDrainCompleted = $stderrDrainResult.completed
    $drainDiagnosticComplete = $stdoutDrainCompleted -and $stderrDrainCompleted
    if ($null -ne $stdoutCaptureStream) { try { $stdoutCaptureStream.Flush() } catch {} }
    if ($null -ne $stderrCaptureStream) { try { $stderrCaptureStream.Flush() } catch {} }
    if ($null -ne $standardInputEndpoint) { $standardInputEndpoint.Dispose(); $standardInputEndpoint = $null }
    [uint32]$nativeExitCode = 0
    if (-not [NativeCloseWindow]::GetExitCodeProcess($ownedProcessHandle, [ref]$nativeExitCode)) { throw 'Could not retrieve exit code from retained owned process handle.' }
    $exitCode = [int]$nativeExitCode
    $processEndUtc = [DateTime]::UtcNow

    $appEvents = @(Get-ExactPidApplicationEvents $launchPid $started)
    $nativeCrashEvents = $appEvents.Count
    $eventText = if (Test-Path -LiteralPath (Join-Path $run 'editor-events.jsonl')) { [IO.File]::ReadAllText((Join-Path $run 'editor-events.jsonl')) } else { '' }
    $nativeSummary = Get-NativeTraceSummary $launchPid $nativeTrace
    $maxCompleted = Get-MaxTelemetry $eventText 'completed'
    $maxSubmitted = Get-MaxTelemetry $eventText 'submissions'
    $maxSubmissionAttempts = Get-MaxTelemetry $eventText 'submissionAttempts'
    $startupHandleNames = [System.Collections.Generic.List[string]]::new()
    foreach ($line in ($eventText -split "`r?`n")) {
        if ($line -match '"event":"std_handle"' -and $line -match 'stage=startup' -and
            $line -match 'type=pipe' -and $line -match 'duplicateSameObject=True' -and
            $line -match 'name=(stdin|stdout|stderr)') {
            $startupHandleNames.Add($Matches[1])
        }
    }
    $startupPipeNames = @($startupHandleNames | Sort-Object -Unique)
    $nativeModuleAtStartup = 'unobserved'
    foreach ($line in ($eventText -split "`r?`n")) {
        if ($line -match '"event":"diagnostic"' -and $line -match 'stage=startup_before_output' -and
            $line -match 'nativeModuleLoaded=(true|false)') {
            $nativeModuleAtStartup = $Matches[1]
            break
        }
    }
    $summary = [ordered]@{
        schema = 1; mode = $Mode; project = $project; requestedManifestHdrpVersion = $requestedManifestHdrpVersion
        lockedHdrpVersion = $lockedHdrpVersion; lockedHdrpSource = $lockedHdrpSource; actualResolvedHdrpVersion = $ready.actualResolvedHdrpVersion
        manifestLockVersionMismatch = ($requestedManifestHdrpVersion -ne $lockedHdrpVersion); actualUnityVersion = $ready.actualUnityVersion
        viewWeaveHdrpPackageVersion = $viewWeavePackageVersion; packageUri = $expectedPackageUri
        unityVersion = '6000.6.4f1'; graphicsApi = 'Direct3D12'; pid = $launchPid; executable = $editor
        startedUtc = $processStart.ToString('o'); closeRequestedUtc = $closeRequestedUtc.ToString('o'); endedUtc = $processEndUtc.ToString('o')
        naturalExit = $true; exitCode = $exitCode; closeAccepted = $closeResult; closeMethod = $method; forcedTermination = $false
        readyCondition = $ready.condition; logMode = $LogMode; unityLogArgument = $unityLogArgument
        playModeReadyObserved = $eventText.Contains('"event":"play_output_ready"')
        playEnterRequestedObserved = $eventText.Contains('"event":"play_enter_requested"')
        playSettingsRestoredObserved = $eventText.Contains('"event":"play_settings_restored"')
        isolatedExistingSceneRootCount = $ready.isolatedExistingSceneRootCount
        playModeOptionsEnabledBefore = $ready.playModeOptionsEnabledBefore
        playModeOptionsBefore = $ready.playModeOptionsBefore
        playModeOptionsRestored = $ready.playModeOptionsRestored
        wantsToQuitObserved = $eventText.Contains('"event":"wants_to_quit"')
        quittingObserved = $eventText.Contains('"event":"quitting"'); previewReadyObserved = $eventText.Contains('"event":"preview_ready"')
        maxCompletedFramesObserved = $maxCompleted; maxSubmittedFramesObserved = $maxSubmitted
        maxSubmissionAttemptsObserved = $maxSubmissionAttempts
        applicationCrashEvents1000_1001ExactPid = $appEvents; nativeTrace = $nativeSummary
        nativeTracePresent = (Test-Path -LiteralPath $nativeTrace)
        rawEditorLog = if ($LogMode -eq 'stdout') { 'stdout.raw.log and stderr.raw.log drained by the parent for this run; Unity -logFile -; global Editor.log not used' } else { 'private local editor.log evidence; retained unchanged' }
        stdioMode = $StdioMode; stdoutRedirection = $StdioMode
        stdoutCaptureBytes = if (Test-Path -LiteralPath $stdoutCapture -PathType Leaf) { (Get-Item -LiteralPath $stdoutCapture).Length } else { 0 }
        stderrCaptureBytes = if (Test-Path -LiteralPath $stderrCapture -PathType Leaf) { (Get-Item -LiteralPath $stderrCapture).Length } else { 0 }
        stdinEndpointRetainedUntilExit = ($StdioMode -eq 'redirected-pipes')
        stdoutDrainCompleted = $stdoutDrainCompleted; stderrDrainCompleted = $stderrDrainCompleted
        drainDiagnosticComplete = $drainDiagnosticComplete
        drainTimeoutRecovery = @($stdoutDrainResult.recovery, $stderrDrainResult.recovery)
        helperStartupPipeStreams = $startupPipeNames
        helperStartupAllThreePipesConfirmed = ($startupPipeNames.Count -eq 3)
        stdioComparisonStatus = if ($StdioMode -ne 'redirected-pipes') { 'not-requested' } elseif ($startupPipeNames.Count -eq 3) { 'passed' } else { 'failed' }
        nativeModuleAtHelperStartup = $nativeModuleAtStartup
        handleObservationScope = 'Win32 standard handles/filetype/duplicated object identity; Unity CRT descriptor internals are not observable here'
        playModeScope = if ($Mode -eq 'play-active-close') { 'Generated additive fixture only; existing loaded scene roots were in-memory isolated; separate from user scene and Edit Preview' } else { $null }
        helperSourceSha256 = $sourceHash; runnerScriptSha256 = $runnerSourceHash; commandLineSha256 = $commandFingerprint
        packageManifestSha256Before = $originalPackageManifest; packageLockSha256Before = $originalPackageLock
    }
    Write-Ascii (Join-Path $run 'process-summary.json') ($summary | ConvertTo-Json -Depth 5)
    if ($exitCode -ne 0 -or $nativeCrashEvents -ne 0 -or -not $summary.wantsToQuitObserved -or -not $summary.quittingObserved -or
        -not $nativeSummary.nativeCleanupConfirmed -or -not $nativeSummary.diagnosticComplete) {
        throw 'Process close did not satisfy the natural-exit, crash-event, and lifecycle-observation gates.'
    }
    if ($Mode -eq 'play-active-close' -and
        (-not $summary.playModeReadyObserved -or -not $summary.playEnterRequestedObserved -or
         -not $summary.playSettingsRestoredObserved -or -not $ready.playModeOptionsRestored)) {
        throw 'Play-mode output or restoration gates were not observed.'
    }
    if ($StdioMode -eq 'redirected-pipes' -and (-not $summary.helperStartupAllThreePipesConfirmed -or -not $drainDiagnosticComplete)) {
        throw 'Comparison did not establish all startup PIPE handles and complete both redirected output drains.'
    }
}
catch {
    $message = [regex]::Replace($_.Exception.GetBaseException().Message, '[^\x00-\x7F]', '?')
    try { Write-Ascii (Join-Path $run 'runner-error.txt') ('Runner failed: ' + $message) } catch {}
    throw
}
finally {
    $stillRunning = $false
    if ($null -ne $process) { try { $stillRunning = -not $process.HasExited } catch { $stillRunning = $true } }
    $untrackedLaunchFound = $false
    if ($null -eq $process -and $helperCopied) {
        $untrackedLaunches = @(Get-CimInstance Win32_Process | Where-Object {
            [StringComparer]::OrdinalIgnoreCase.Equals($_.ExecutablePath, $editor) -and
            $_.CommandLine -and $_.CommandLine.Contains('ViewWeave.WindowsEditorCloseValidation.EditorCloseValidationRunner.Start') -and
            $_.CommandLine.Contains($project)
        })
        $untrackedLaunchFound = $untrackedLaunches.Count -gt 0
    }
    if (($null -eq $process -or -not $stillRunning) -and -not $untrackedLaunchFound) {
        try { $process.WaitForExit(); if ($null -eq $exitCode) { $exitCode = $process.ExitCode } } catch {}
        $stdoutDrainResult = @{ completed = $true; recovery = 'not-redirected' }
        $stderrDrainResult = @{ completed = $true; recovery = 'not-redirected' }
        try { if ($null -ne $stdoutDrainTask) { $stdoutDrainResult = Complete-RedirectedDrain $process $stdoutDrainTask 'stdout' } } catch { $stdoutDrainResult = @{ completed = $false; recovery = 'drain-check-error' } }
        try { if ($null -ne $stderrDrainTask) { $stderrDrainResult = Complete-RedirectedDrain $process $stderrDrainTask 'stderr' } } catch { $stderrDrainResult = @{ completed = $false; recovery = 'drain-check-error' } }
        $stdoutDrainCompleted = $stdoutDrainResult.completed
        $stderrDrainCompleted = $stderrDrainResult.completed
        $drainDiagnosticComplete = $drainDiagnosticComplete -and $stdoutDrainCompleted -and $stderrDrainCompleted
        try { if ($null -ne $standardInputEndpoint) { $standardInputEndpoint.Dispose(); $standardInputEndpoint = $null } } catch {}
        try { if ($null -ne $stdoutCaptureStream) { $stdoutCaptureStream.Dispose(); $stdoutCaptureStream = $null } } catch {}
        try { if ($null -ne $stderrCaptureStream) { $stderrCaptureStream.Dispose(); $stderrCaptureStream = $null } } catch {}
        $ownedCreated = @()
        if ($helperCopied) {
            if (Test-Path -LiteralPath $helper) { Remove-Item -LiteralPath (Assert-OwnedPath $helper (Join-Path $project 'Assets\Editor')) -Force }
            if (Test-Path -LiteralPath $helperMeta) { $ownedCreated += 'Assets/Editor/' + $helperName + '.meta' }
        }
        $unexpected = [System.Collections.Generic.List[string]]::new()
        foreach ($relative in $protectedRoots) {
            $backup = Join-Path $baselineRoot $relative
            $baselineSubset = @($baseline | Where-Object { $_.path.StartsWith($relative + '/', [StringComparison]::OrdinalIgnoreCase) })
            $owned = if ($relative -eq 'Assets') { $ownedCreated } else { @() }
            foreach ($item in (Restore-Tree $relative $backup $baselineSubset $owned)) { $unexpected.Add($item) }
        }
        $finalManifest = Get-FileManifest $project $protectedRoots
        $finalHashByPath = @{}; foreach ($item in $finalManifest) { $finalHashByPath[$item.path] = $item.sha256 }
        $baselineMatches = $true
        foreach ($item in $baseline) {
            if (-not $finalHashByPath.ContainsKey($item.path) -or $finalHashByPath[$item.path] -ne $item.sha256) { $baselineMatches = $false }
        }
        if ($unexpected.Count -gt 0) { Write-Ascii (Join-Path $run 'unexpected-added-files.txt') (($unexpected | Sort-Object -Unique) -join "`r`n") }
        $finalManifestText = $finalManifest | ConvertTo-Json -Depth 4
        Write-Ascii (Join-Path $run 'final-protected-manifest.json') $finalManifestText
        $manifestUnchanged = (Get-FileHash -LiteralPath $packageManifestPath -Algorithm SHA256).Hash -eq $originalPackageManifest
        $lockUnchanged = (Get-FileHash -LiteralPath $packageLockPath -Algorithm SHA256).Hash -eq $originalPackageLock
        $cleanupPass = $baselineMatches -and $unexpected.Count -eq 0 -and $manifestUnchanged -and $lockUnchanged -and
            -not (Test-Path -LiteralPath $helper) -and -not (Test-Path -LiteralPath $helperMeta)
        Write-Ascii (Join-Path $run 'cleanup-summary.json') (@{
            completedUtc = [DateTime]::UtcNow.ToString('o'); protectedBaselineRestored = $baselineMatches
            packageManifestUnchanged = $manifestUnchanged; packageLockUnchanged = $lockUnchanged
            drainDiagnosticComplete = $drainDiagnosticComplete
            stdoutDrainCompleted = $stdoutDrainCompleted; stderrDrainCompleted = $stderrDrainCompleted
            helperRemoved = -not (Test-Path -LiteralPath $helper); helperMetaRemoved = -not (Test-Path -LiteralPath $helperMeta)
            unexpectedAddedFiles = @($unexpected | Sort-Object -Unique); pass = $cleanupPass
        } | ConvertTo-Json -Depth 4)
        if ($null -ne $process -and (Test-Path -LiteralPath (Join-Path $run 'process-summary.json'))) {
            $finalSummary = Get-Content -LiteralPath (Join-Path $run 'process-summary.json') -Raw | ConvertFrom-Json
            $finalSummary | Add-Member -NotePropertyName cleanupPass -NotePropertyValue $cleanupPass -Force
            Write-Ascii (Join-Path $run 'process-summary.json') ($finalSummary | ConvertTo-Json -Depth 7)
        } elseif ($null -ne $process) {
            $fallbackEvents = if ($null -ne $process.Id) { @(Get-ExactPidApplicationEvents $process.Id $started) } else { @() }
            $fallbackNative = if ($null -ne $process.Id) { Get-NativeTraceSummary $process.Id $nativeTrace } else { @{} }
            $fallbackText = if (Test-Path -LiteralPath (Join-Path $run 'editor-events.jsonl')) { [IO.File]::ReadAllText((Join-Path $run 'editor-events.jsonl')) } else { '' }
            $fallback = [ordered]@{
                schema = 1; mode = $Mode; requestedManifestHdrpVersion = $requestedManifestHdrpVersion
                lockedHdrpVersion = $lockedHdrpVersion; lockedHdrpSource = $lockedHdrpSource; viewWeaveHdrpPackageVersion = $viewWeavePackageVersion; unityVersion = '6000.6.4f1'
                manifestLockVersionMismatch = ($requestedManifestHdrpVersion -ne $lockedHdrpVersion)
                graphicsApi = 'Direct3D12'; pid = $process.Id; executable = $editor; startedUtc = $processStart.ToString('o')
                endedUtc = [DateTime]::UtcNow.ToString('o'); naturalExit = $true; exitCode = $exitCode
                closeRequested = $closeRequested; readyCondition = if ($null -ne $ready) { $ready.condition } else { $null }
                wantsToQuitObserved = $fallbackText.Contains('"event":"wants_to_quit"')
                quittingObserved = $fallbackText.Contains('"event":"quitting"')
                playModeReadyObserved = $fallbackText.Contains('"event":"play_output_ready"')
                playEnterRequestedObserved = $fallbackText.Contains('"event":"play_enter_requested"')
                playSettingsRestoredObserved = $fallbackText.Contains('"event":"play_settings_restored"')
                previewReadyObserved = $fallbackText.Contains('"event":"preview_ready"')
                applicationCrashEvents1000_1001ExactPid = $fallbackEvents; nativeTrace = $fallbackNative
                maxCompletedFramesObserved = Get-MaxTelemetry $fallbackText 'completed'
                helperSourceSha256 = $sourceHash; cleanupPass = $cleanupPass; diagnosticComplete = $false
                stdoutDrainCompleted = $stdoutDrainCompleted; stderrDrainCompleted = $stderrDrainCompleted
                drainDiagnosticComplete = $drainDiagnosticComplete
                drainTimeoutRecovery = @($stdoutDrainResult.recovery, $stderrDrainResult.recovery)
                rawFailureReason = if (Test-Path -LiteralPath (Join-Path $run 'runner-error.txt')) { [IO.File]::ReadAllText((Join-Path $run 'runner-error.txt')) } else { 'No close-ready marker was published.' }
            }
            Write-Ascii (Join-Path $run 'process-summary.json') ($fallback | ConvertTo-Json -Depth 7)
        } else {
            Write-Ascii (Join-Path $run 'process-summary.json') (@{
                schema = 1; mode = $Mode; startedUtc = $launchUtc.ToString('o'); processStarted = $false
                helperSourceSha256 = $sourceHash; cleanupPass = $cleanupPass; diagnosticComplete = $false
                failure = if (Test-Path -LiteralPath (Join-Path $run 'runner-error.txt')) { [IO.File]::ReadAllText((Join-Path $run 'runner-error.txt')) } else { 'Unity process was not created.' }
            } | ConvertTo-Json -Depth 4)
        }
    } else {
        $reason = if ($untrackedLaunchFound) { 'Untracked Unity process matching owned launch arguments exists; project files were not restored.' } else { 'Owned Editor is still running; project files were not restored while in use.' }
        try { Write-Ascii (Join-Path $run 'cleanup-summary.json') ('{"completed":false,"pass":false,"reason":"' + $reason + '"}') } catch {}
    }
    $env:LKG_NATIVE_TRACE = $originalTrace
    $env:VW_EDITOR_CLOSE_ROOT = $originalRoot
    $env:VW_EDITOR_CLOSE_MODE = $originalMode
    $env:VW_EDITOR_CLOSE_REQUESTED_HDRP = $originalRequestedHdrp
    $env:VW_EDITOR_CLOSE_LOCKED_HDRP = $originalLockedHdrp
    $env:VW_EDITOR_CLOSE_LOCKED_HDRP_SOURCE = $originalLockedHdrpSource
    if ($ownedProcessHandle -ne [IntPtr]::Zero -and -not $stillRunning) {
        [void][NativeCloseWindow]::CloseHandle($ownedProcessHandle)
        $ownedProcessHandle = [IntPtr]::Zero
    }
}

if (-not $cleanupPass) { throw 'External project baseline restore/cleanup gate failed.' }
if ($exitCode -ne 0) { throw 'Unity Editor exited unsuccessfully.' }
Write-Output ('Windows Editor close validation completed. Evidence: ' + $run)

