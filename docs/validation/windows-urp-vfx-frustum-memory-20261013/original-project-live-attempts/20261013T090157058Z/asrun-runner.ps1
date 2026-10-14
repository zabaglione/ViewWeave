param(
    [Parameter(Mandatory = $false)]
    [int]$ExpectedEditorPid = 21004,
    [Parameter(Mandatory = $false)]
    [int]$ExpectedBridgePid = 29956
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
$templatePath = Join-Path $PSScriptRoot 'LivePlayWatchTemplate.cs'
$limitBytes = [long](512 * 1024 * 1024)
$privateSessionLimitBytes = [long](1024 * 1024 * 1024)
$warmupLimitBytes = [long](4 * 1024 * 1024 * 1024)
$stabilityRangeBytes = [long](16 * 1024 * 1024)
$utcTag = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ', [Globalization.CultureInfo]::InvariantCulture)
$runDir = Join-Path $PSScriptRoot ('run-' + $utcTag)
$watchLog = Join-Path $runDir 'watcher.jsonl'
$privateLog = Join-Path $runDir 'process-memory.jsonl'
$stopFlag = Join-Path $runDir 'stop.flag'
$baselinePath = Join-Path $runDir 'normal-phase-baseline.txt'
$asRunWatcher = Join-Path $runDir 'LivePlayWatch.cs'
$asRunRunnerSource = Join-Path $runDir 'Run-BoundedOriginalPlayProbe.ps1'
$asRunTemplateSource = Join-Path $runDir 'LivePlayWatchTemplate.cs'
$metadataPath = Join-Path $runDir 'run-summary.json'

function Get-ExactEditor([int]$PidValue) {
    $row = Get-CimInstance Win32_Process -Filter "ProcessId=$PidValue" -ErrorAction Stop
    if (-not $row -or $row.Name -ne 'Unity.exe' -or -not $row.ExecutablePath) { throw 'Exact Unity Editor process was not found.' }
    if (-not [IO.Path]::GetFullPath($row.ExecutablePath).Equals([IO.Path]::GetFullPath($editorExe), [StringComparison]::OrdinalIgnoreCase)) { throw 'Editor executable path did not match the fixed Unity version.' }
    if (-not $row.CommandLine -or $row.CommandLine.IndexOf($projectPath, [StringComparison]::OrdinalIgnoreCase) -lt 0) { throw 'Editor command line did not identify the fixed original project.' }
    return $row
}

function Invoke-UnityCommand([string[]]$CommandArgs) {
    $allArgs = @($CommandArgs) + @('--project-path', $projectPath, '--format', 'json', '--caller', 'plugin', '--skill', 'unity-cli')
    $captured = @(& $unityCli @allArgs 2>&1)
    $exit = $LASTEXITCODE
    if ($exit -ne 0) {
        $cliObject = $null
        try { $cliObject = (($captured | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) | ConvertFrom-Json -ErrorAction Stop } catch { }
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
    return ($captured | ForEach-Object { [string]$_ }) -join [Environment]::NewLine
}

function Get-LiveState {
    $expr = 'return string.Empty + UnityEditor.EditorApplication.isPlaying + ((char)124) + UnityEditor.EditorApplication.isPlayingOrWillChangePlaymode + ((char)124) + UnityEditor.EditorApplication.isPaused + ((char)124) + UnityEditor.EditorApplication.isCompiling + ((char)124) + UnityEngine.Time.frameCount + ((char)124) + UnityEngine.Profiling.Profiler.GetAllocatedMemoryForGraphicsDriver() + ((char)124) + UnityEditor.EditorApplication.timeSinceStartup.ToString(System.Globalization.CultureInfo.InvariantCulture);'
    $response = Invoke-UnityCommand @('command', 'eval', $expr)
    try { $parsed = $response | ConvertFrom-Json -ErrorAction Stop } catch { throw 'Unity eval response was not valid JSON.' }
    if (-not $parsed.success -or -not $parsed.data.result.success -or -not $parsed.data.result.result) { throw 'Unity eval did not return a successful scalar result.' }
    return [string]$parsed.data.result.result
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

if (-not (Test-Path -LiteralPath $unityCli -PathType Leaf) -or -not (Test-Path -LiteralPath $templatePath -PathType Leaf) -or -not (Test-Path -LiteralPath $scenePath -PathType Leaf)) {
    throw 'A required fixed-path input was not found.'
}

$exactEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | Where-Object {
    $_.CommandLine -and $_.CommandLine.IndexOf($projectPath, [StringComparison]::OrdinalIgnoreCase) -ge 0
})
$unityPidsBefore = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | ForEach-Object { [int]$_.ProcessId } | Sort-Object -Unique)
if ($exactEditors.Count -ne 1 -or [int]$exactEditors[0].ProcessId -ne $ExpectedEditorPid) { throw 'Exact original-project Editor inventory gate failed.' }
$editorRow = Get-ExactEditor $ExpectedEditorPid
$bridgeRow = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedBridgePid" -ErrorAction Stop
if (-not $bridgeRow) { throw 'Expected Bridge process was not found.' }
$editorProcess = Get-Process -Id $ExpectedEditorPid -ErrorAction Stop
if ($editorProcess.PrivateMemorySize64 -le 0 -or $editorProcess.WorkingSet64 -le 0) { throw 'External process memory counters were unavailable.' }

New-Item -ItemType Directory -Path $runDir | Out-Null
$sceneHashBefore = (Get-FileHash -Algorithm SHA256 -LiteralPath $scenePath).Hash
$templateHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $templatePath).Hash
Copy-Item -LiteralPath $PSCommandPath -Destination $asRunRunnerSource
Copy-Item -LiteralPath $templatePath -Destination $asRunTemplateSource
$runnerSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asRunRunnerSource).Hash
$templateSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asRunTemplateSource).Hash
$template = Get-Content -LiteralPath $templatePath -Raw
$template = $template.Replace('__WATCH_LOG_PATH__', $watchLog.Replace('\', '/'))
$template = $template.Replace('__STOP_FLAG_PATH__', $stopFlag.Replace('\', '/'))
$template = $template.Replace('__BASELINE_PATH__', $baselinePath.Replace('\', '/'))
[IO.File]::WriteAllText($asRunWatcher, $template, [Text.UTF8Encoding]::new($false))
$asRunHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $asRunWatcher).Hash

$preState = Get-LiveState
if ($preState.Split('|')[0] -ne 'False' -or $preState.Split('|')[1] -ne 'False') { throw 'Fresh Editor evaluation reports Play mode active or transitioning.' }
$installResponse = Invoke-UnityCommand @('command', 'eval_file', $asRunWatcher)
if ($installResponse -notmatch 'watcher_registered') { throw 'Live watcher did not report registration.' }
$watcherRegistered = $true
$editorProcess.Refresh()

$summary = [ordered]@{
    status = 'running'
    projectPath = $projectPath
    scenePath = $scenePath
    editorPid = $ExpectedEditorPid
    editorExecutable = $editorRow.ExecutablePath
    bridgePid = $ExpectedBridgePid
    unityCliCaller = 'plugin'
    unityCliSkill = 'unity-cli'
    templateSha256 = $templateHash
    asRunRunnerSha256 = $runnerSourceHash
    asRunTemplateSha256 = $templateSourceHash
    asRunWatcherSha256 = $asRunHash
    sceneSha256Before = $sceneHashBefore
    editorPlayWasActiveBefore = $false
    startOnEnableTemporarilyDisabledInMemory = $true
    sessionStartMethod = 'ViewWeaveBridgeSession.StartSession'
    connectionWindowSeconds = 10
    readyMeasurementWindowSeconds = 30
    readyRevisionLimit = 10000
    normalWarmupWindowSeconds = 2
    normalWarmupFrameLimit = 30
    warmupGrowthStopBytes = $warmupLimitBytes
    graphicsGrowthStopBytes = $limitBytes
    sessionPrivateGrowthStopBytes = $privateSessionLimitBytes
    normalStabilityRangeBytes = $stabilityRangeBytes
    normalStabilityFrames = 3
    normalStabilityMinimumSpanSeconds = 0.25
    sceneSaveCalled = $false
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
$deadline = [DateTime]::UtcNow.AddSeconds(60)

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
        [IO.File]::WriteAllText($stopFlag, 'private_growth_from_stable_normal_baseline_1gib_limit' + "`n", [Text.Encoding]::ASCII)
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
do {
    Start-Sleep -Milliseconds 300
    $postState = Get-LiveState
    if ($postState.Split('|')[0] -eq 'False' -and $postState.Split('|')[1] -eq 'False') { break }
} while ([DateTime]::UtcNow -lt $settleDeadline)

$postProcess = Get-Process -Id $ExpectedEditorPid -ErrorAction Stop
$sceneHashAfter = (Get-FileHash -Algorithm SHA256 -LiteralPath $scenePath).Hash
$bridgeAfter = Get-CimInstance Win32_Process -Filter "ProcessId=$ExpectedBridgePid" -ErrorAction Stop
$allEditorsAfter = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | Where-Object {
    $_.CommandLine -and $_.CommandLine.IndexOf($projectPath, [StringComparison]::OrdinalIgnoreCase) -ge 0
})
$unityPidsAfter = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop | ForEach-Object { [int]$_.ProcessId } | Sort-Object -Unique)
$watchLines = if (Test-Path -LiteralPath $watchLog -PathType Leaf) { @(Get-Content -LiteralPath $watchLog) } else { @() }
$events = @($watchLines | ForEach-Object { Get-EventValue $_ 'event' })
$summary.status = if ($postState.Split('|')[0] -eq 'False' -and $sceneHashAfter -eq $sceneHashBefore -and $bridgeAfter -and $allEditorsAfter.Count -eq 1) { 'bounded_observation_finished' } else { 'bounded_observation_incomplete' }
$summary.playStartUtc = $playStartedUtc.ToString('o')
$summary.sceneSha256After = $sceneHashAfter
$summary.sceneHashUnchanged = ($sceneHashAfter -eq $sceneHashBefore)
$summary.editorPrivateMemoryAfter = [long]$postProcess.PrivateMemorySize64
$summary.editorWorkingSetAfter = [long]$postProcess.WorkingSet64
$summary.bridgeStillPresent = [bool]$bridgeAfter
$summary.exactProjectEditorCountAfter = $allEditorsAfter.Count
$summary.freshStateAfter = $postState
$summary.events = $events
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
$summary.bridgeIdleAndPendingZeroObserved = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'bridge_closed_idle' }).Count)
$summary.originalRigProfileRestoreObserved = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'original_profile_restored' }).Count)
$summary.originalRigProfileRestoreFailureObserved = [bool](@($watchLines | Where-Object { (Get-EventValue $_ 'event') -eq 'original_profile_restore_failed' }).Count)
$summary.newUnityProcessPidsAfter = @($unityPidsAfter | Where-Object { $_ -notin $unityPidsBefore })
$summary.newUnityProcessCountAfter = $summary.newUnityProcessPidsAfter.Count
$summary.sceneSaveCalled = $false
$summary.endUtc = [DateTime]::UtcNow.ToString('o')
$summary | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $metadataPath -Encoding ascii
$summary | ConvertTo-Json -Depth 6
