param(
    [Parameter(Mandatory=$true)][ValidateSet('Green','Coin')][string]$Probe,
    [int]$TimeoutSeconds = 150
)
$ErrorActionPreference = 'Stop'
$base = 'REPO_ROOT/.local/windows-vfx-scene-comparison-20261013'
$project = Join-Path $base 'URP/UnityProject'
$editor = 'C:/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe'
$sharedSource = Join-Path $base 'Shared/HdrIntermediateProbeTests.cs'
$cloneSource = Join-Path $project 'Assets/SceneComparisonTests/HdrIntermediateProbeTests.cs'
$candidatePackage = Join-Path $base 'CandidatePackages/com.zabaglione.lookingglass.viewweave.urp'
$candidateRuntimeSource = Join-Path $candidatePackage 'Runtime/UrpViewHistoryRenderer.cs'
$results = Join-Path $base 'URP/ResultsPhaseC'
$projectResults = Join-Path $project 'TestResults'
New-Item -ItemType Directory -Path $results -Force | Out-Null
New-Item -ItemType Directory -Path $projectResults -Force | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$filter = if ($Probe -eq 'Green') { 'ViewWeave.SceneComparison.Tests.HdrIntermediateProbeTests.ObserveMultiStripGpuEventsOnOriginalSceneWithHdrIntermediate' } else { 'ViewWeave.SceneComparison.Tests.HdrIntermediateProbeTests.ObserveRotationAngleOnOriginalSceneWithHdrIntermediate' }
$evidenceDir = Join-Path $projectResults "hdrcandidate-$($Probe.ToLowerInvariant())-$stamp"
$xml = Join-Path $results "hdrcandidate-$($Probe.ToLowerInvariant())-$stamp.xml"
$editorLog = Join-Path $results "hdrcandidate-$($Probe.ToLowerInvariant())-$stamp-editor.log"
$record = Join-Path $results "hdrcandidate-$($Probe.ToLowerInvariant())-$stamp-runner.json"
if (-not (Test-Path -LiteralPath $editor)) { throw 'Configured Unity Editor executable is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $project 'Packages/manifest.json'))) { throw 'Isolated URP project manifest is missing.' }
if (-not (Test-Path -LiteralPath $sharedSource) -or -not (Test-Path -LiteralPath $cloneSource)) { throw 'HDR-intermediate probe source is missing from the shared and clone paths.' }
if (-not (Test-Path -LiteralPath $candidateRuntimeSource)) { throw 'Candidate URP package source is missing.' }
$sharedSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $sharedSource).Hash
$cloneSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $cloneSource).Hash
if ($sharedSourceHash -ne $cloneSourceHash) { throw 'Shared and clone Hdr-target probe source hashes differ.' }
$sourceBytes = (Get-Item -LiteralPath $sharedSource).Length
$candidatePackageSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $candidateRuntimeSource).Hash
foreach ($path in @($evidenceDir,$xml,$editorLog,$record)) { if (Test-Path -LiteralPath $path) { throw 'Refusing to overwrite an existing probe artifact.' } }

function Get-ExactProjectProcessRows([string]$projectPath) {
    $rows = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop
    $full = [IO.Path]::GetFullPath($projectPath).Replace('/','\').TrimEnd('\')
    @($rows | Where-Object {
        $command = [string]$_.CommandLine
        $match = [regex]::Match($command, '(?:^|\s)-projectPath(?:\s+|=)(?:"([^"]+)"|(\S+))', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) { return $false }
        $candidate = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        try { [IO.Path]::GetFullPath($candidate).Replace('/','\').TrimEnd('\').Equals($full,[StringComparison]::OrdinalIgnoreCase) } catch { $false }
    } | ForEach-Object { [ordered]@{ processId=[int]$_.ProcessId; executablePath=$_.ExecutablePath; creationTimeUtc=([datetime]$_.CreationDate).ToUniversalTime().ToString('o') } })
}

$before = @()
try { $before = Get-ExactProjectProcessRows $project }
catch {
    $summary = [ordered]@{ status='blocked-process-inventory-unavailable'; probe=$Probe; testFilter=$filter; projectPath=$project; probeSourcePath=$sharedSource; probeSourceSha256=$sharedSourceHash; probeSourceBytes=$sourceBytes; processInventoryAvailableBefore=$false; started=$false; errorType=$_.Exception.GetType().Name }
    [IO.File]::WriteAllText($record,($summary | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    Get-Content -LiteralPath $record -Raw | Write-Output
    exit 2
}
if ($before.Count -gt 0) {
    $summary = [ordered]@{ status='blocked-exact-project-editor-already-running'; probe=$Probe; testFilter=$filter; projectPath=$project; probeSourcePath=$sharedSource; probeSourceSha256=$sharedSourceHash; probeSourceBytes=$sourceBytes; processInventoryAvailableBefore=$true; matchingBefore=$before; started=$false }
    [IO.File]::WriteAllText($record,($summary | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    Get-Content -LiteralPath $record -Raw | Write-Output
    exit 2
}

$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $editor
$psi.WorkingDirectory = $project
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
foreach ($arg in @('-batchmode','-projectPath',$project,'-runTests','-testPlatform','PlayMode','-testFilter',$filter,'-testResults',$xml,'-force-d3d12','-logFile',$editorLog)) { [void]$psi.ArgumentList.Add($arg) }
$psi.Environment['VW_VFX_HDR_PP_PROBE_EVIDENCE_DIR'] = $evidenceDir
$psi.Environment['VW_URP_HDR_INTERMEDIATE_PROBE'] = '1'
$psi.Environment['VW_URP_HDR_CANDIDATE_PATH'] = $candidatePackage

$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi
$stdoutDrain = $null
$stderrDrain = $null
$started = $false
$timedOut = $false
$exitCode = $null
$editorPid = $null
$startUtc = $null
$startCreationUtc = $null
$startErrorType = $null
$timeoutKillAttempted = $false
$timeoutKillErrorType = $null
try {
    $started = $process.Start()
    if (-not $started) { throw 'Editor process start returned false.' }
    $editorPid = $process.Id
    $startUtc = $process.StartTime.ToUniversalTime().ToString('o')
    $startCreationUtc = $process.StartTime.ToUniversalTime().ToString('o')
    $stdoutDrain = $process.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
    $stderrDrain = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
    if ($process.WaitForExit([Math]::Max(1,$TimeoutSeconds) * 1000)) { $exitCode = $process.ExitCode }
    else {
        $timedOut = $true
        $timeoutKillAttempted = $true
        try {
            if (-not $process.HasExited) { $process.Kill($true) }
            if ($process.WaitForExit(10000)) { $exitCode = $process.ExitCode }
        } catch { $timeoutKillErrorType = $_.Exception.GetType().Name }
    }
} catch { $startErrorType = $_.Exception.GetType().Name }
finally {
    $endedUtc = [DateTime]::UtcNow.ToString('o')
    $drainIncomplete = $false
    if ($started -and $process.HasExited) {
        try { $drainIncomplete = -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutDrain,$stderrDrain),10000) } catch { $drainIncomplete = $true }
    } elseif ($started) { $drainIncomplete = $true }
    $after = @()
    $afterAvailable = $true
    try { $after = Get-ExactProjectProcessRows $project } catch { $afterAvailable = $false }
    $stillRunning = $false
    if ($started) { try { $stillRunning = -not $process.HasExited } catch { $stillRunning = $true } }
    $nunit = $null
    $xmlTestCaseNames = @()
    $xmlBytes = $null
    $xmlHash = $null
    $xmlParseError = $null
    if (Test-Path -LiteralPath $xml) {
        $xmlBytes = (Get-Item -LiteralPath $xml).Length
        $xmlHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $xml).Hash
        try {
            [xml]$doc = [IO.File]::ReadAllText($xml)
            $run = $doc.SelectSingleNode('//test-run')
            if ($run) {
                $xmlTestCaseNames = @($doc.SelectNodes('//test-case') | ForEach-Object { [string]$_.fullname })
                $nunit = [ordered]@{ total=[int]$run.total; passed=[int]$run.passed; failed=[int]$run.failed; skipped=[int]$run.skipped; inconclusive=[int]$run.inconclusive; result=$run.result; testCaseFullNames=$xmlTestCaseNames }
            } else { $xmlParseError = 'MissingTestRunRoot' }
        } catch { $xmlParseError = $_.Exception.GetType().Name }
    }
    $evidenceFiles = @()
    if (Test-Path -LiteralPath $evidenceDir) {
        $evidenceFiles = @(Get-ChildItem -LiteralPath $evidenceDir -File | ForEach-Object { [ordered]@{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash } })
    }
    $exactCaseGate = $null -ne $nunit -and $nunit.total -eq 1 -and $nunit.passed -eq 1 -and $nunit.failed -eq 0 -and $nunit.skipped -eq 0 -and $nunit.inconclusive -eq 0 -and
        $nunit.result -eq 'Passed' -and $xmlTestCaseNames.Count -eq 1 -and $xmlTestCaseNames[0] -eq $filter
    $pass = $started -and -not $timedOut -and $null -ne $exitCode -and $exitCode -eq 0 -and -not $drainIncomplete -and $afterAvailable -and $after.Count -eq 0 -and $exactCaseGate
    $summary = [ordered]@{
        status=if($pass){'bounded-observation-passed'}elseif($timedOut){'timeout-result-incomplete'}else{'not-passed'}
        probe=$Probe; testFilter=$filter; testPlatform='PlayMode'; expectedTestCount=1; graphicsApi='D3D12'; projectPath=$project; unityExecutable=$editor
        probeSourcePath=$sharedSource; probeSourceSha256=$sharedSourceHash; probeSourceBytes=$sourceBytes
        candidatePackagePath=$candidatePackage; candidateUrpRenderSourceSha256=$candidatePackageSourceHash
        startedUtc=$startUtc; startedProcessCreationUtc=$startCreationUtc; endedUtc=$endedUtc; editorProcessId=$editorPid; editorExitCode=$exitCode; timeout=$timedOut; timeoutSeconds=$TimeoutSeconds
        timeoutKillAttempted=$timeoutKillAttempted; timeoutKillErrorType=$timeoutKillErrorType
        startErrorType=$startErrorType; stdoutAndStderrDrained=$started -and -not $drainIncomplete; processInventoryAvailableBefore=$true; matchingBefore=$before
        processInventoryAvailableAfter=$afterAvailable; matchingAfter=$after; processObjectStillRunning=$stillRunning
        xmlPath=$xml; xmlBytes=$xmlBytes; xmlSha256=$xmlHash; xmlParseErrorType=$xmlParseError; nunit=$nunit; exactTestCaseGate=$exactCaseGate
        editorLogPath=$editorLog; editorLogShared=$false; evidenceDirectory=$evidenceDir; evidenceFiles=$evidenceFiles; boundedObservationGate=$pass
    }
    [IO.File]::WriteAllText($record,($summary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $process.Dispose()
}
Get-Content -LiteralPath $record -Raw | Write-Output
if (-not $pass) { exit 1 }
