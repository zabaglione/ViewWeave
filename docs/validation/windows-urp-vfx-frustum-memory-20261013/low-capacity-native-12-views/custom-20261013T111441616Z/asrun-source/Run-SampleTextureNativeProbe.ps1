param(
    [Parameter(Mandatory=$true)][ValidateSet('Auto','Custom')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$FixtureResultDirectory,
    [ValidateRange(1,12)][int]$RenderedViews = 1,
    [ValidateRange(0,2)][int]$RestartCycles = 0,
    [int]$TimeoutSeconds = 300
)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path 'E:/LookingGlassSDK').Path
$localRoot = Join-Path $repo '.local/windows-urp-vfx-frustum-memory-20261013'
$project = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$projectResults = Join-Path $project 'TestResults'
$baseRunner = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/Tools/Run-UnityIsolated.ps1'
$baseResults = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/Results'
$sharedSource = Join-Path $localRoot 'Shared/SampleTextureNativeProbeTests.cs'
$sharedRunner = $PSCommandPath
$cloneSource = Join-Path $project 'Assets/SceneComparisonTests/SampleTextureNativeProbeTests.cs'
$cloneMeta = $cloneSource + '.meta'
$cloneAsmdef = Join-Path $project 'Assets/SceneComparisonTests/ViewWeave.SceneComparison.Tests.asmdef'
$fixtureRoot = [IO.Path]::GetFullPath($FixtureResultDirectory)
$fixtureJson = Join-Path $fixtureRoot 'fixture-result.json'
$fixtureRunSummary = Join-Path $fixtureRoot 'summary.json'
$fixtureRunRunner = Join-Path $fixtureRoot 'runner.json'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$runId = "SampleTextureNative-$stamp-$Mode"
$results = Join-Path $localRoot "URP/Results/$runId"
$projectEvidence = Join-Path $projectResults "SampleTextureNative/$runId"
$retiredCloneSource = Join-Path $results 'asrun-source/clone-adapter-before-update.cs'
$runSummaryPath = Join-Path $results 'summary.json'
$runnerRecordPath = Join-Path $results 'runner.json'
$xmlCopyPath = Join-Path $results 'results.xml'
$nativeResultCopyPath = Join-Path $results 'native-result.json'
$filter = 'ViewWeave.SceneComparison.Tests.SampleTextureNativeProbeTests.ComparesLowCapacitySampleTextureOnNaturalAutoRender'
$adapterGuid = '969899aa219149a3b5d54fea5e1d0b62'
$sourceHash = $null
$runnerHash = $null
$cloneHashBefore = $null
$cloneHashAfter = $null
$metaHash = $null
$invocationErrorType = $null
$invocationErrorMessage = $null
$underlying = $null
$underlyingPath = $null
$xmlNames = @()
$nunitExact = $false
$editorLogIncluded = $false
$testEvidence = $null
$copyExitCode = $null

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Assert-UnderRoot([string]$Candidate, [string]$Root, [string]$Label) {
    $fullCandidate = [IO.Path]::GetFullPath($Candidate).TrimEnd('\')
    $fullRoot = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $fullCandidate.StartsWith($fullRoot, [StringComparison]::OrdinalIgnoreCase)) { throw "$Label must remain under the approved results root." }
}

function Get-ExactProjectRows([string]$ProjectPath) {
    $rows = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop
    $full = [IO.Path]::GetFullPath($ProjectPath).Replace('/','\').TrimEnd('\')
    @($rows | Where-Object {
        $command = [string]$_.CommandLine
        $match = [regex]::Match($command, '(?:^|\s)-projectPath(?:\s+|=)(?:"([^"]+)"|(\S+))', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) { return $false }
        $candidate = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        try { [IO.Path]::GetFullPath($candidate).Replace('/','\').TrimEnd('\').Equals($full,[StringComparison]::OrdinalIgnoreCase) } catch { $false }
    } | ForEach-Object { [ordered]@{ processId=[int]$_.ProcessId; creationTimeUtc=([datetime]$_.CreationDate).ToUniversalTime().ToString('o') } })
}

if (-not (Test-Path -LiteralPath $sharedSource) -or -not (Test-Path -LiteralPath $baseRunner) -or -not (Test-Path -LiteralPath $project)) { throw 'The canonical adapter, isolated project, or base runner is missing.' }
Assert-UnderRoot $fixtureRoot (Join-Path $localRoot 'URP/Results') 'Fixture evidence directory'
if (-not (Test-Path -LiteralPath $fixtureJson) -or -not (Test-Path -LiteralPath $fixtureRunSummary) -or -not (Test-Path -LiteralPath $fixtureRunRunner)) { throw 'The passed Control Null fixture result, wrapper summary, or base runner record is missing.' }
if (-not (Test-Path -LiteralPath $cloneAsmdef)) { throw 'The isolated scene-comparison test assembly definition is missing.' }
if ((Test-Path -LiteralPath $results) -or (Test-Path -LiteralPath $projectEvidence)) { throw 'Refusing to overwrite an earlier Native probe run.' }
if ($RenderedViews -notin @(1,12)) { throw 'RenderedViews must be 1 or 12.' }
if ($RestartCycles -gt 0 -and $Mode -ne 'Auto') { throw 'History camera stop/restart cycles are approved only for the Auto clone.' }
if ($RestartCycles -gt 0 -and $RenderedViews -ne 1) { throw 'History camera stop/restart cycles are only approved for one rendered view.' }

$fixture = Get-Content -LiteralPath $fixtureJson -Raw | ConvertFrom-Json
$nullRunner = Get-Content -LiteralPath $fixtureRunSummary -Raw | ConvertFrom-Json
$nullRunnerBase = Get-Content -LiteralPath $fixtureRunRunner -Raw | ConvertFrom-Json
if ($fixture.schema -ne 'viewweave-sample-texture-capacity-fixture-v2-grid40' -or $fixture.status -ne 'success' -or $fixture.nunitOutcome -ne 'Passed' -or $fixture.graphicsDevice -ne 'Null' -or -not $fixture.sourceAssetUnchanged -or -not $fixture.openSceneFilesUnchanged) { throw 'The preserved Control Null fixture JSON does not describe the expected source-preserving success.' }
if ($nullRunner.schema -ne 'viewweave-sample-texture-capacity-runner-v1' -or $nullRunner.status -ne 'fixture-passed' -or $nullRunner.testPassGate -ne $true -or $nullRunner.exactXmlCaseGate -ne $true -or $nullRunner.noResidualGate -ne $true -or $nullRunner.editorExitCode -ne 0 -or $nullRunner.timeout -ne $false -or $nullRunner.outputDrainIncomplete -ne $false -or $nullRunner.processInventoryResidualCount -ne 0 -or $nullRunner.fixtureStatus -ne 'success') { throw 'The Control Null wrapper summary does not describe a complete exact drained pass.' }
if ($nullRunnerBase.nunit.total -ne 1 -or $nullRunnerBase.nunit.passed -ne 1 -or $nullRunnerBase.nunit.failed -ne 0 -or $nullRunnerBase.editorExitCode -ne 0 -or $nullRunnerBase.timeout -ne $false -or $nullRunnerBase.outputDrainIncomplete -ne $false -or $nullRunnerBase.processInventoryAvailableAfter -ne $true -or $nullRunnerBase.processInventoryResidualCount -ne 0 -or $nullRunnerBase.testPlatform -ne 'EditMode') { throw 'The Control Null base runner record does not support the exact drained zero-residual pass.' }
if ($fixture.sourceParticleCapacity -ne 160000 -or $fixture.fixtureParticleCapacity -ne 1600 -or $fixture.customCompilerDisabledReasonName -ne 'None') { throw 'The Control Null fixture capacities or compiler eligibility do not match the approved test scope.' }
if ($fixture.controlComparableStructureSha256 -ne $fixture.autoComparableStructureSha256 -or $fixture.controlComparableStructureSha256 -ne $fixture.customComparableStructureSha256) { throw 'The normalized Control, Auto, and Custom clone structures do not match.' }
if ($fixture.controlAssetGuid -eq $fixture.autoAssetGuid -or $fixture.controlAssetGuid -eq $fixture.customAssetGuid) { throw 'The 160000-capacity Control asset must be distinct and must not be activated.' }
$selectedClone = if ($Mode -eq 'Auto') { $fixture.autoAssetPath } else { $fixture.customAssetPath }
$selectedCloneGuid = if ($Mode -eq 'Auto') { $fixture.autoAssetGuid } else { $fixture.customAssetGuid }
$selectedCloneSha = if ($Mode -eq 'Auto') { $fixture.autoAssetSha256 } else { $fixture.customAssetSha256 }
$selectedCloneFullPath = Join-Path $project ($selectedClone.Replace('/','\'))
if (-not (Test-Path -LiteralPath $selectedCloneFullPath) -or (Get-Sha256 $selectedCloneFullPath) -ne $selectedCloneSha) { throw 'The selected low-capacity clone is missing or differs from the passed fixture result.' }
if ($Mode -eq 'Auto' -and ($fixture.autoInstancingModeName -notin @('Auto','Automatic') -or $fixture.autoInstancingCapacity -ne 64)) { throw 'The selected Auto clone settings differ from the passed fixture.' }
if ($Mode -eq 'Custom' -and ($fixture.customInstancingModeName -ne 'Custom' -or $fixture.customInstancingCapacity -ne 1)) { throw 'The selected Custom clone settings differ from the passed fixture.' }

$beforeRows = @(Get-ExactProjectRows $project)
if ($beforeRows.Count -gt 0) { throw 'An Editor already targets the isolated clone; no files were changed and no test was started.' }

$sourceHash = Get-Sha256 $sharedSource
$runnerHash = Get-Sha256 $sharedRunner
$cloneHashBefore = if (Test-Path -LiteralPath $cloneSource) { Get-Sha256 $cloneSource } else { $null }
if (Test-Path -LiteralPath $cloneMeta) {
    $metaGuidLine = (Select-String -LiteralPath $cloneMeta -Pattern '^guid: ([0-9a-f]{32})$').Matches.Groups[1].Value
    if ($metaGuidLine -ne $adapterGuid) { throw 'An existing clone adapter meta has an unexpected GUID; refusing to overwrite it.' }
    $metaHash = Get-Sha256 $cloneMeta
}

New-Item -ItemType Directory -Path $results -Force | Out-Null
New-Item -ItemType Directory -Path $projectEvidence -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $results 'asrun-source') -Force | Out-Null
Copy-Item -LiteralPath $sharedSource -Destination (Join-Path $results 'asrun-source/SampleTextureNativeProbeTests.cs')
Copy-Item -LiteralPath $sharedRunner -Destination (Join-Path $results 'asrun-source/Run-SampleTextureNativeProbe.ps1')
Copy-Item -LiteralPath $fixtureJson -Destination (Join-Path $results 'fixture-result.json')
Copy-Item -LiteralPath $fixtureRunSummary -Destination (Join-Path $results 'null-runner-summary.json')
Copy-Item -LiteralPath $fixtureRunRunner -Destination (Join-Path $results 'null-base-runner.json')

if ($cloneHashBefore -and $cloneHashBefore -ne $sourceHash) {
    Copy-Item -LiteralPath $cloneSource -Destination $retiredCloneSource
}
if (-not $cloneHashBefore -or $cloneHashBefore -ne $sourceHash) { Copy-Item -LiteralPath $sharedSource -Destination $cloneSource }
if (-not (Test-Path -LiteralPath $cloneMeta)) {
    [IO.File]::WriteAllText($cloneMeta,"fileFormatVersion: 2`nguid: $adapterGuid`n",[Text.UTF8Encoding]::new($false))
}
$cloneHashAfter = Get-Sha256 $cloneSource
if ($cloneHashAfter -ne $sourceHash) { throw 'The installed clone adapter differs from its canonical source.' }
$metaHash = Get-Sha256 $cloneMeta

$runEvidenceBefore = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File -ErrorAction SilentlyContinue | ForEach-Object FullName)
$envMap = [ordered]@{
    VW_SAMPLE_TEXTURE_NATIVE_RESULT = $projectEvidence
    VW_SAMPLE_TEXTURE_NATIVE_FIXTURE = $fixtureJson
    VW_SAMPLE_TEXTURE_NATIVE_MODE = $Mode
    VW_SAMPLE_TEXTURE_NATIVE_RESTART_CYCLES = [string]$RestartCycles
    VW_SAMPLE_TEXTURE_NATIVE_RENDERED_VIEWS = [string]$RenderedViews
}
$oldEnvironment = @{}
$invocationFailure = $null
try {
    foreach ($name in $envMap.Keys) {
        $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
        [Environment]::SetEnvironmentVariable($name,[string]$envMap[$name],'Process')
    }
    try {
        $null = & $baseRunner -Pipeline URP -Operation RunTests -TestPlatform PlayMode -TestFilter $filter -ExpectedTestCount 1 -TimeoutSeconds $TimeoutSeconds -GraphicsApi D3D12
        $copyExitCode = $LASTEXITCODE
    }
    catch {
        $invocationFailure = $_.Exception.GetType().Name
        $invocationErrorMessage = ([string]$_.Exception.Message -replace '[^\x20-\x7E]', '?')
        if ($invocationErrorMessage.Length -gt 400) { $invocationErrorMessage = $invocationErrorMessage.Substring(0,400) }
    }
}
finally {
    foreach ($name in $envMap.Keys) { [Environment]::SetEnvironmentVariable($name,$oldEnvironment[$name],'Process') }
}

$runEvidenceAfter = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File -ErrorAction SilentlyContinue | ForEach-Object FullName)
$newRecords = @($runEvidenceAfter | Where-Object { $runEvidenceBefore -notcontains $_ })
if ($newRecords.Count -eq 1) {
    $underlyingPath = $newRecords[0]
    try { $underlying = Get-Content -LiteralPath $underlyingPath -Raw | ConvertFrom-Json } catch { $invocationFailure = 'UnderlyingRunnerJsonInvalid' }
    if ($underlying) { Copy-Item -LiteralPath $underlyingPath -Destination $runnerRecordPath }
}
$rawXmlPath = if ($underlying) { [string]$underlying.rawXmlPath } else { '' }
if ($rawXmlPath -and (Test-Path -LiteralPath $rawXmlPath)) { Copy-Item -LiteralPath $rawXmlPath -Destination $xmlCopyPath }
if (Test-Path -LiteralPath (Join-Path $projectEvidence 'native-result.json')) { Copy-Item -LiteralPath (Join-Path $projectEvidence 'native-result.json') -Destination $nativeResultCopyPath }
$testEvidencePath = Join-Path $projectEvidence 'native-result.json'
if (Test-Path -LiteralPath $testEvidencePath) { try { $testEvidence = Get-Content -LiteralPath $testEvidencePath -Raw | ConvertFrom-Json } catch { $invocationFailure = 'NativeResultJsonInvalid' } }
if (Test-Path -LiteralPath $xmlCopyPath) {
    try {
        [xml]$xmlDocument = [IO.File]::ReadAllText($xmlCopyPath)
        $xmlNames = @($xmlDocument.SelectNodes('//test-case') | ForEach-Object { [string]$_.fullname })
        $nunitExact = $xmlNames.Count -eq 1 -and $xmlNames[0] -eq $filter
    }
    catch { $invocationFailure = 'NativeXmlInvalid' }
}
$testPass = $testEvidence -and $testEvidence.status -eq 'passed' -and
    $testEvidence.mode -eq $Mode -and $testEvidence.graphicsApiGate -eq $true -and $testEvidence.pipelineGate -eq $true -and
    $testEvidence.renderedViewsRequested -eq $RenderedViews -and $testEvidence.outputLayoutGate -eq $true -and
    $testEvidence.outputColumns -eq $(if($RenderedViews -eq 12){4}else{1}) -and
    $testEvidence.outputRows -eq $(if($RenderedViews -eq 12){3}else{1}) -and
    $testEvidence.outputQuiltWidth -eq $(if($RenderedViews -eq 12){512}else{128}) -and
    $testEvidence.outputQuiltHeight -eq $(if($RenderedViews -eq 12){288}else{96}) -and
    $testEvidence.quiltViewRenderMode -eq 'Full' -and
    $testEvidence.renderedViewCountAfterOff -eq $RenderedViews -and $testEvidence.renderedViewCountAfterActive -eq $RenderedViews -and
    $testEvidence.historyCameraCountAfterOff -eq $RenderedViews -and $testEvidence.historyCameraCountAfterActive -eq $RenderedViews -and
    $testEvidence.effectiveAANoneAfterOffGate -eq $true -and $testEvidence.effectiveAANoneAfterActiveGate -eq $true -and
    $testEvidence.effectiveAANoneCountAfterOff -eq $RenderedViews -and $testEvidence.effectiveAANoneCountAfterActive -eq $RenderedViews -and
    $testEvidence.twoCameraGate -eq $true -and $testEvidence.isolatedHistoryGate -eq $true -and $testEvidence.gridResolutionGate -eq $true -and $testEvidence.offscreenRevisionGate -eq $true -and
    $testEvidence.activeRevisionGate -eq $true -and $testEvidence.activeParticlesGate -eq $true -and $testEvidence.pixelDifferenceGate -eq $true -and
    $testEvidence.pixelDifferenceEachTileGate -eq $true -and @($testEvidence.changedPixelCountPerOutputTile).Count -eq $RenderedViews -and
    @($testEvidence.changedPixelCountPerOutputTile | Where-Object { $_ -le 0 }).Count -eq 0 -and
    $testEvidence.measurementComplete -eq $true -and $testEvidence.checkpoint8Reached -eq $true -and $testEvidence.checkpoint16Reached -eq $true -and
    $testEvidence.checkpoint32Reached -eq $true -and $testEvidence.rendererReadyBeforeActivation -eq $true -and
    $testEvidence.sourceCullingMaskIncludesEffect -eq $true -and $testEvidence.effectMaterialsSupported -eq $true -and
    $testEvidence.componentAllowInstancing -eq $true -and $testEvidence.componentInstancingGate -eq $true -and
    $testEvidence.componentActivationSettingsGate -eq $true -and $testEvidence.componentSettingsUnchanged -eq $true -and
    $testEvidence.restartCyclesRequested -eq $RestartCycles -and $testEvidence.historyCycleGate -eq $true -and
    @($testEvidence.historyCycles).Count -eq $RestartCycles -and
    $testEvidence.growthStopNotReached -eq $true -and $testEvidence.descriptorGate -eq $true -and $testEvidence.sourceGraphUnchanged -eq $true -and
    $testEvidence.sceneDiskUnchanged -eq $true -and $testEvidence.sceneSavedDuringRun -eq $false -and $testEvidence.noUnityErrorsDuringRun -eq $true -and
    $testEvidence.cleanupPassed -eq $true -and $testEvidence.rigAutoRenderRestored -eq $true -and $testEvidence.profileRestored -eq $true -and
    $testEvidence.cameraStateRestored -eq $true -and $testEvidence.selectedRendererStateRestored -eq $true
$nunitPass = $underlying -and $underlying.nunit.total -eq 1 -and $underlying.nunit.passed -eq 1 -and $underlying.nunit.failed -eq 0 -and
    $underlying.nunit.skipped -eq 0 -and $underlying.nunit.inconclusive -eq 0 -and $underlying.editorExitCode -eq 0 -and
    $underlying.timeout -eq $false -and $underlying.outputDrainIncomplete -eq $false -and $underlying.processInventoryAvailableAfter -eq $true -and
    $underlying.processInventoryResidualCount -eq 0 -and $underlying.testPassGate -eq $true
$sourceAfter = Get-Sha256 $sharedSource
$runnerAfter = Get-Sha256 $sharedRunner
$cloneHashAfter = if (Test-Path -LiteralPath $cloneSource) { Get-Sha256 $cloneSource } else { $null }
$finalPass = $Mode -in @('Auto','Custom') -and $newRecords.Count -eq 1 -and $nunitExact -and $nunitPass -and $testPass -and
    $sourceAfter -eq $sourceHash -and $runnerAfter -eq $runnerHash -and $cloneHashAfter -eq $sourceHash -and $invocationFailure -eq $null

$summary = [ordered]@{
    schema='viewweave-sample-texture-native-runner-v4-grid40-view-count'
    status=if($finalPass){'native-probe-passed'}else{'not-passed'}
    mode=$Mode; renderedViewsRequested=$RenderedViews; restartCyclesRequested=$RestartCycles; testPlatform='PlayMode'; graphicsApiRequested='D3D12'; testFilter=$filter; expectedTestCount=1
    outputColumns=if($testEvidence){$testEvidence.outputColumns}else{$null}; outputRows=if($testEvidence){$testEvidence.outputRows}else{$null}
    outputQuiltWidth=if($testEvidence){$testEvidence.outputQuiltWidth}else{$null}; outputQuiltHeight=if($testEvidence){$testEvidence.outputQuiltHeight}else{$null}
    quiltViewRenderMode=if($testEvidence){$testEvidence.quiltViewRenderMode}else{$null}; quiltOutputDescriptor=if($testEvidence){$testEvidence.quiltOutputDescriptor}else{$null}
    effectiveAAModesAfterOff=if($testEvidence){$testEvidence.effectiveAAModesAfterOff}else{$null}
    effectiveAAModesAfterActive=if($testEvidence){$testEvidence.effectiveAAModesAfterActive}else{$null}
    projectPath=$project; fixtureResultDirectory=$fixtureRoot; fixtureRunId=$audit.runId
    sourceAssetGuid=$fixture.sourceAssetGuid; sourceAssetSha256=$fixture.sourceAssetSha256Before
    controlAssetGuid=$fixture.controlAssetGuid; controlCapacity=$fixture.sourceParticleCapacity; controlActivated=$false
    selectedAssetPath=$selectedClone; selectedAssetGuid=$selectedCloneGuid; selectedAssetSha256=$selectedCloneSha
    canonicalSourcePath=$sharedSource; canonicalSourceSha256=$sourceHash; canonicalSourceSha256After=$sourceAfter
    cloneSourcePath=$cloneSource; cloneSourceSha256Before=$cloneHashBefore; cloneSourceSha256After=$cloneHashAfter
    retiredCloneSourcePath=if(Test-Path -LiteralPath $retiredCloneSource){$retiredCloneSource}else{$null}
    retiredCloneSourceSha256=if(Test-Path -LiteralPath $retiredCloneSource){Get-Sha256 $retiredCloneSource}else{$null}
    cloneMetaGuid=$adapterGuid; cloneMetaSha256=$metaHash
    runnerPath=$sharedRunner; runnerSha256=$runnerHash; runnerSha256After=$runnerAfter
    underlyingRunnerPath=$underlyingPath; underlyingRunId=if($underlying){$underlying.runId}else{$null}
    editorProcessId=if($underlying){$underlying.editorProcessId}else{$null}; editorExitCode=if($underlying){$underlying.editorExitCode}else{$null}
    timeout=if($underlying){$underlying.timeout}else{$null}; outputDrainIncomplete=if($underlying){$underlying.outputDrainIncomplete}else{$null}
    processInventoryAvailableAfter=if($underlying){$underlying.processInventoryAvailableAfter}else{$false}
    processInventoryResidualCount=if($underlying){$underlying.processInventoryResidualCount}else{$null}
    rawXmlPath=if(Test-Path -LiteralPath $xmlCopyPath){$xmlCopyPath}else{$null}
    rawXmlBytes=if(Test-Path -LiteralPath $xmlCopyPath){(Get-Item -LiteralPath $xmlCopyPath).Length}else{$null}
    rawXmlSha256=if(Test-Path -LiteralPath $xmlCopyPath){Get-Sha256 $xmlCopyPath}else{$null}
    xmlTestCaseFullNames=$xmlNames; exactCaseGate=[bool]$nunitExact; nunit=if($underlying){$underlying.nunit}else{$null}
    evidencePath=$projectEvidence; evidenceExists=[bool](Test-Path -LiteralPath $testEvidencePath)
    evidenceStatus=if($testEvidence){$testEvidence.status}else{'missing'}; evidenceGatesPassed=[bool]$testPass
    historyCycleGate=if($testEvidence){$testEvidence.historyCycleGate}else{$false}; historyCycles=if($testEvidence){$testEvidence.historyCycles}else{@()}
    totalMeasurementFrames=if($testEvidence){$testEvidence.totalMeasurementFrames}else{$null}
    maximumTotalMeasurementFrames=if($testEvidence){$testEvidence.maximumTotalMeasurementFrames}else{$null}
    totalMeasurementElapsedSeconds=if($testEvidence){$testEvidence.totalMeasurementElapsedSeconds}else{$null}
    maximumTotalMeasurementSeconds=if($testEvidence){$testEvidence.maximumTotalMeasurementSeconds}else{$null}
    fixtureNullSummarySha256=Get-Sha256 $fixtureRunSummary; fixtureRawStatus=$fixture.status; fixtureWrapperStatus=$nullRunner.status; fixtureBaseRunnerStatus=$nullRunnerBase.status
    wrapperInvocationErrorType=$invocationFailure; wrapperInvocationErrorMessage=$invocationErrorMessage; wrapperExitCode=$copyExitCode
    safetyStopBytes=536870912; safetyStopSemantics='observed-growth-stop-after-512MiB; not a hard allocation cap'
    editorLogIncluded=$editorLogIncluded; passGate=[bool]$finalPass
}
[IO.File]::WriteAllText($runSummaryPath,($summary | ConvertTo-Json -Depth 9),[Text.UTF8Encoding]::new($false))
Get-Content -LiteralPath $runSummaryPath -Raw | Write-Output
if (-not $finalPass) { exit 1 }
