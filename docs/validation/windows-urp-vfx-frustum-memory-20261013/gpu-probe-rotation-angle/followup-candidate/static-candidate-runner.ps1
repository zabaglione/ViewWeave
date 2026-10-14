$ErrorActionPreference = 'Stop'
$repo = 'REPO_ROOT'
$localRoot = Join-Path $repo '.local/windows-urp-vfx-frustum-memory-20261013'
$project = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$editor = 'C:/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe'
$source = Join-Path $localRoot 'Shared/FrustumMemoryProbeTests.cs'
$cloneSource = Join-Path $project 'Assets/SceneComparisonTests/FrustumMemoryProbeTests.cs'
$cloneMeta = $cloneSource + '.meta'
$filter = 'ViewWeave.SceneComparison.Tests.FrustumMemoryProbeTests.RotationAngleOriginalSceneOneViewFrustumLifecycle'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$archive = Join-Path $localRoot "URP/SandboxResults/$stamp-RotationAngle"
$projectEvidence = Join-Path $project "TestResults/FrustumMemory/$stamp-RotationAngle"
$xmlPath = Join-Path $archive 'results.xml'
$logPath = Join-Path $archive 'editor-private.log'
$summaryPath = Join-Path $archive 'summary.json'
$snapshotSource = Join-Path $archive 'asrun-source.cs'
$snapshotClone = Join-Path $archive 'asrun-clone-source.cs'
$snapshotMeta = Join-Path $archive 'asrun-clone-source.meta'
$snapshotRunner = Join-Path $archive 'asrun-runner.ps1'
$unityLock = Join-Path $project 'Temp/UnityLockfile'
$runId = [guid]::NewGuid().ToString('N')
$process = $null
$started = $false
$startError = $null
$pidOwned = $null
$creationUtc = $null
$exitCode = $null
$timedOut = $false
$killedOwnedProcess = $false
$ownerVerifiedBeforeKill = $false
$naturalExit = $false
$drainIncomplete = $null
$stdoutTask = $null
$stderrTask = $null
$lockStatusBefore = 'not-checked'
$lockStatusAfter = 'not-checked'
$lockAfterOk = $false
$lockProbeException = $null
$rawXml = $null
$nunit = $null
$xmlNames = @()
$xmlPasses = 0
$xmlFailures = 0
$xmlSkipped = 0
$xmlInconclusive = 0
$xmlParseError = $null
$probeResult = $null
$probeResultParseError = $null
$probeEvidencePath = Join-Path $projectEvidence 'evidence.txt'
$probeFields = @{}
$archiveCreated = $false

function Test-UnityLockAvailable([string]$Path, [ref]$Status, [ref]$ErrorType) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { $Status.Value = 'absent'; return $true }
    $stream = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        $Status.Value = 'opened-exclusive-and-closed'
        return $true
    }
    catch {
        $Status.Value = 'unavailable-or-locked'
        $ErrorType.Value = $_.Exception.GetType().Name
        return $false
    }
    finally { if ($null -ne $stream) { $stream.Dispose() } }
}

function Get-ExactProcessIdentity([Diagnostics.Process]$OwnedProcess, [string]$ExpectedExe, [string]$ExpectedStartUtc) {
    try {
        $OwnedProcess.Refresh()
        if ($OwnedProcess.HasExited) { return $false }
        if ($OwnedProcess.Id -le 0) { return $false }
        $actualStart = $OwnedProcess.StartTime.ToUniversalTime().ToString('o')
        if ($actualStart -ne $ExpectedStartUtc) { return $false }
        $actualExe = [IO.Path]::GetFullPath($OwnedProcess.MainModule.FileName)
        $expected = [IO.Path]::GetFullPath($ExpectedExe)
        return $actualExe.Equals($expected, [StringComparison]::OrdinalIgnoreCase)
    }
    catch { return $false }
}

$lockOk = $false
$passGate = $false
$sourceHash = $null
$cloneHash = $null
$runnerHash = $null
$sourceHashAfter = $null
$cloneHashAfter = $null
$runnerHashAfter = $null
$nunitExact = $false
$probeGate = $false
$copyErrors = @()

if (-not (Test-Path -LiteralPath $project -PathType Container) -or -not (Test-Path -LiteralPath $editor -PathType Leaf)) { throw 'Fixed Editor or isolated project is missing.' }
if (-not (Test-Path -LiteralPath $source -PathType Leaf) -or -not (Test-Path -LiteralPath $cloneSource -PathType Leaf) -or -not (Test-Path -LiteralPath $cloneMeta -PathType Leaf)) { throw 'Frozen source, clone source, or clone metadata is missing.' }
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$cloneHash = (Get-FileHash -LiteralPath $cloneSource -Algorithm SHA256).Hash
$runnerHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
if ($sourceHash -ne '5DA3D0079981A8EE8414A0ABE84652A30D44E8C368F6E62D0B1D52A1108825F2') { throw 'Frozen probe source hash does not match the approved candidate.' }
if ($cloneHash -ne $sourceHash) { throw 'Canonical and clone source hashes differ.' }
if (Test-Path -LiteralPath $archive) { throw 'Refusing to overwrite an existing archive directory.' }
if (Test-Path -LiteralPath $projectEvidence) { throw 'Refusing to overwrite an existing evidence directory.' }
if (Test-Path -LiteralPath $xmlPath) { throw 'Refusing to overwrite an existing NUnit XML file.' }
New-Item -ItemType Directory -Path $archive | Out-Null
$archiveCreated = $true
Copy-Item -LiteralPath $source -Destination $snapshotSource
Copy-Item -LiteralPath $cloneSource -Destination $snapshotClone
Copy-Item -LiteralPath $cloneMeta -Destination $snapshotMeta
Copy-Item -LiteralPath $PSCommandPath -Destination $snapshotRunner
$sourceBytes = (Get-Item -LiteralPath $source).Length
$metaHash = (Get-FileHash -LiteralPath $cloneMeta -Algorithm SHA256).Hash
$lockOk = Test-UnityLockAvailable $unityLock ([ref]$lockStatusBefore) ([ref]$lockProbeException)

if ($lockOk) {
    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName = $editor
    $psi.WorkingDirectory = $project
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardInput = $false
    foreach ($arg in @('-batchmode','-projectPath',$project,'-runTests','-testPlatform','PlayMode','-testFilter',$filter,'-testResults',$xmlPath,'-force-d3d12','-logFile',$logPath)) { [void]$psi.ArgumentList.Add($arg) }
    $psi.Environment['VW_FRUSTUM_MEMORY_EVIDENCE_DIR'] = $projectEvidence
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $psi
    try {
        $started = $process.Start()
        if (-not $started) { throw 'Process.Start returned false.' }
        $pidOwned = $process.Id
        $creationUtc = $process.StartTime.ToUniversalTime().ToString('o')
        $stdoutTask = $process.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
        $stderrTask = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
        $completed = $process.WaitForExit(180000)
        if ($completed) {
            $exitCode = $process.ExitCode
            $naturalExit = $true
        }
        else {
            $timedOut = $true
            $ownerVerifiedBeforeKill = Get-ExactProcessIdentity $process $editor $creationUtc
            if ($ownerVerifiedBeforeKill) {
                $process.Kill()
                $killedOwnedProcess = $true
                [void]$process.WaitForExit(10000)
            }
        }
    }
    catch { $startError = $_.Exception.GetType().Name }
    finally {
        if ($started) {
            try {
                if ($process.HasExited) { if ($null -eq $exitCode) { $exitCode = $process.ExitCode; $naturalExit = -not $timedOut } }
                $drainIncomplete = -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutTask,$stderrTask),10000)
            }
            catch { $drainIncomplete = $true }
        }
        else { $drainIncomplete = $false }
        $processStillRunning = $false
        if ($started) { try { $process.Refresh(); $processStillRunning = -not $process.HasExited } catch { $processStillRunning = $true } }
        $lockAfterException = $null
        $lockStatusAfter = 'not-checked'
        $lockAfterOk = Test-UnityLockAvailable $unityLock ([ref]$lockStatusAfter) ([ref]$lockAfterException)
        if ($null -ne $process) { $process.Dispose() }
    }
}
else { $processStillRunning = $null }

if (Test-Path -LiteralPath $xmlPath -PathType Leaf) {
    try {
        [xml]$xmlDoc = [IO.File]::ReadAllText($xmlPath)
        $testRun = $xmlDoc.SelectSingleNode('//test-run')
        if ($testRun) { $nunit = [ordered]@{ total=[int]$testRun.total; passed=[int]$testRun.passed; failed=[int]$testRun.failed; skipped=[int]$testRun.skipped; inconclusive=[int]$testRun.inconclusive; result=[string]$testRun.result } }
        $caseNodes = @($xmlDoc.SelectNodes('//test-case'))
        $xmlNames = @($caseNodes | ForEach-Object { [string]$_.fullname })
        $xmlPasses = @($caseNodes | Where-Object { $_.result -eq 'Passed' }).Count
        $xmlFailures = @($caseNodes | Where-Object { $_.result -eq 'Failed' }).Count
        $xmlSkipped = @($caseNodes | Where-Object { $_.result -eq 'Skipped' }).Count
        $xmlInconclusive = @($caseNodes | Where-Object { $_.result -eq 'Inconclusive' }).Count
    }
    catch { $xmlParseError = $_.Exception.GetType().Name }
}
if (Test-Path -LiteralPath (Join-Path $projectEvidence 'result.json') -PathType Leaf) {
    try { $probeResult = Get-Content -LiteralPath (Join-Path $projectEvidence 'result.json') -Raw | ConvertFrom-Json }
    catch { $probeResultParseError = $_.Exception.GetType().Name }
}
if (Test-Path -LiteralPath $probeEvidencePath -PathType Leaf) {
    foreach ($line in Get-Content -LiteralPath $probeEvidencePath) {
        $match = [regex]::Match([string]$line, '^([A-Za-z0-9]+)=(.*)$')
        if ($match.Success) { $probeFields[$match.Groups[1].Value] = $match.Groups[2].Value }
    }
}
if ($probeResult -and $probeResult.status -eq 'observed' -and $probeResult.graphName -eq 'RotationAngle' -and
    $probeResult.graphSha256Before -and $probeResult.graphSha256Before -eq $probeResult.graphSha256After -and $probeResult.graphSourceUnchanged -and
    $probeResult.sceneDiskUnchanged -and $probeResult.sourceSceneDiskUnchanged -and -not $probeResult.sceneSavedDuringProbe -and
    $probeResult.setupCountersValid -and $probeResult.activationCountersValid -and -not $probeResult.safetyStopReached -and
    $probeResult.historyReleased -and $probeResult.quiltTextureReleased -and $probeResult.referenceTextureReleased -and
    $probeFields['activeVfxAfterCleanup'] -eq '0' -and $probeResult.targetErrorCount -eq 0 -and
    $probeFields['historyAAOff'] -eq 'True' -and $probeFields['runtimeDataValid'] -eq 'True' -and $probeFields['historyCameraFramingMatricesMatched'] -eq 'True' -and
    $probeResult.maxAlive -gt 0) { $probeGate = $true }
$nunitExact = $null -ne $nunit -and $nunit.total -eq 1 -and $nunit.passed -eq 1 -and $nunit.failed -eq 0 -and $nunit.skipped -eq 0 -and $nunit.inconclusive -eq 0 -and $xmlNames.Count -eq 1 -and $xmlNames[0] -eq $filter -and $xmlPasses -eq 1 -and $xmlFailures -eq 0 -and $xmlSkipped -eq 0 -and $xmlInconclusive -eq 0
$sourceHashAfter = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$cloneHashAfter = (Get-FileHash -LiteralPath $cloneSource -Algorithm SHA256).Hash
$runnerHashAfter = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$passGate = $lockOk -and $lockAfterOk -and $started -and $naturalExit -and -not $timedOut -and $exitCode -eq 0 -and $drainIncomplete -eq $false -and -not $processStillRunning -and $nunitExact -and $probeGate -and $sourceHashAfter -eq $sourceHash -and $cloneHashAfter -eq $cloneHash -and $runnerHashAfter -eq $runnerHash

if ($archiveCreated) {
    if (Test-Path -LiteralPath $xmlPath -PathType Leaf) { }
    foreach ($name in @('result.json','evidence.txt','inside-off.png','inside-on.png')) {
        $candidate = Join-Path $projectEvidence $name
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $destination = Join-Path $archive "probe-$name"
            try { Copy-Item -LiteralPath $candidate -Destination $destination -ErrorAction Stop }
            catch { $copyErrors += $_.Exception.GetType().Name }
        }
    }
}
$xmlInfo = if (Test-Path -LiteralPath $xmlPath -PathType Leaf) { Get-Item -LiteralPath $xmlPath } else { $null }
$summary = [ordered]@{
    schema='viewweave-frustum-memory-sandbox-runner-v1'
    status=if($passGate){'owned-process-bounded-observation'}else{'not-passed'}
    legacyFullRunnerPass=$false
    legacyCimProcessInventory='unavailable-not-used'
    legacyResidualCount=$null
    graph='RotationAngle'; testFilter=$filter; expectedTestCount=1; testPlatform='PlayMode'; graphicsApiRequested='D3D12'
    projectPath=$project; unityExecutable=$editor; runId=$runId; startedUtc=$creationUtc; endedUtc=[DateTime]::UtcNow.ToString('o')
    processId=$pidOwned; processStarted=[bool]$started; processStartErrorType=$startError; processNaturallyExited=[bool]$naturalExit; editorExitCode=$exitCode
    timeout=$timedOut; killedOwnedProcess=$killedOwnedProcess; ownerVerifiedBeforeKill=$ownerVerifiedBeforeKill; processStillRunning=$processStillRunning
    stdoutStderrDrained=($drainIncomplete -eq $false); outputDrainIncomplete=if($null -eq $drainIncomplete){$null}else{[bool]$drainIncomplete}
    lockPath=$unityLock; lockProbeBefore=$lockStatusBefore; lockProbeAfter=$lockStatusAfter; lockProbeAfterGate=[bool]$lockAfterOk; lockProbeErrorType=$lockProbeException
    canonicalSourcePath=$source; canonicalSourceSha256=$sourceHash; canonicalSourceSha256After=$sourceHashAfter; sourceBytes=(Get-Item -LiteralPath $source).Length
    cloneSourcePath=$cloneSource; cloneSourceSha256=$cloneHash; cloneSourceSha256After=$cloneHashAfter; cloneMetaSha256=$metaHash
    runnerPath=$PSCommandPath; runnerSha256=$runnerHash; runnerSha256After=$runnerHashAfter
    asRunSource='asrun-source.cs'; asRunCloneSource='asrun-clone-source.cs'; asRunCloneMeta='asrun-clone-source.meta'; asRunRunner='asrun-runner.ps1'
    archivePath=$archive; evidenceDirectory=$projectEvidence; evidenceStatus=if($probeResult){$probeResult.status}else{'missing'}; evidenceCopyErrorTypes=$copyErrors
    probeFailurePhase=if($probeResult){$probeResult.failurePhase}else{$null}; probeFailureCode=if($probeResult){$probeResult.failureCode}else{$null}; probeFailureType=if($probeResult){$probeResult.failureType}else{$null}
    probeSafetyGate=[bool]$probeGate; probeHistoryReleased=if($probeResult){$probeResult.historyReleased}else{$false}; probeQuiltTextureReleased=if($probeResult){$probeResult.quiltTextureReleased}else{$false}
    probeActiveEffectCountAfterCleanup=$probeFields['activeVfxAfterCleanup']; probeHistoryAaOff=$probeFields['historyAAOff']; probeRuntimeDataValid=$probeFields['runtimeDataValid']; probeCameraMatricesMatched=$probeFields['historyCameraFramingMatricesMatched']; probeTargetErrorCount=if($probeResult){$probeResult.targetErrorCount}else{$null}; probeRendererEnabledBeforeActivation=if($probeResult){$probeResult.effectRendererEnabledBeforeActivation}else{$null}; probeRendererChangedForProbe=if($probeResult){$probeResult.effectRendererEnabledChangedForProbe}else{$null}; probeRendererRestored=if($probeResult){$probeResult.effectRendererEnabledRestored}else{$false}
    xmlPath=if($xmlInfo){$xmlPath}else{$null}; xmlBytes=if($xmlInfo){$xmlInfo.Length}else{$null}; xmlSha256=if($xmlInfo){(Get-FileHash -LiteralPath $xmlPath -Algorithm SHA256).Hash}else{$null}; xmlParseErrorType=$xmlParseError
    nunit=$nunit; xmlTestCaseFullNames=$xmlNames; xmlPassCount=$xmlPasses; xmlFailureCount=$xmlFailures; xmlSkippedCount=$xmlSkipped; xmlInconclusiveCount=$xmlInconclusive; nunitExactPass=[bool]$nunitExact; probeResultParseErrorType=$probeResultParseError
    rawEditorLogPath=$logPath; rawEditorLogShared=$false; passGate=[bool]$passGate
}
[IO.File]::WriteAllText($summaryPath,($summary | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Get-Content -LiteralPath $summaryPath -Raw | Write-Output
if (-not $passGate) { exit 1 }
