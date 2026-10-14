param(
    [Parameter(Mandatory=$true)][string]$FixtureResultDirectory,
    [ValidateRange(60,600)][int]$TimeoutSeconds = 300,
    [ValidateSet(1,12)][int]$RenderedViews = 1
)
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path 'E:/LookingGlassSDK').Path
$localRoot = Join-Path $repo '.local/windows-urp-vfx-frustum-memory-20261013'
$project = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$projectResults = Join-Path $project 'TestResults'
$baseRunner = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/Tools/Run-UnityIsolated.ps1'
$baseResults = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/Results'
$cloneSource = Join-Path $project 'Assets/SceneComparisonTests/SampleTextureFullNativeProbeTests.cs'
$cloneMeta = $cloneSource + '.meta'
$cloneAsmdef = Join-Path $project 'Assets/SceneComparisonTests/ViewWeave.SceneComparison.Tests.asmdef'
$runnerSource = $PSCommandPath
$fixtureRoot = [IO.Path]::GetFullPath($FixtureResultDirectory)
$fixtureJson = Join-Path $fixtureRoot 'fixture-result.json'
$fixtureSummary = Join-Path $fixtureRoot 'summary.json'
$fixtureDerived = Join-Path $fixtureRoot 'derived-validation.json'
$fixtureRunner = Join-Path $fixtureRoot 'runner.json'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$viewLabel = if ($RenderedViews -eq 1) { '1view' } else { '12views' }
$runId = "SampleTextureFullNative-$viewLabel-$stamp"
$results = Join-Path $localRoot "URP/Results/$runId"
$projectEvidence = Join-Path $projectResults "SampleTextureFullNative/$runId"
$runSummaryPath = Join-Path $results 'summary.json'
$runnerRecordPath = Join-Path $results 'runner.json'
$xmlCopyPath = Join-Path $results 'results.xml'
$nativeResultCopyPath = Join-Path $results 'native-result.json'
$filter = 'ViewWeave.SceneComparison.Tests.SampleTextureFullNativeProbeTests.ComparesFullCapacitySampleTextureOnNaturalAutoRender'
$expectedMetaGuid = '60e49e35ce294e839af5e5a9f9b8a46d'
$fixture = $null
$fixtureDerivedRecord = $null
$underlying = $null
$native = $null
$invocationFailure = $null
$invocationErrorMessage = $null
$copyExitCode = $null
$newRecords = @()
$nunitExact = $false
$testPass = $false
$finalPass = $false
$editorLogIncluded = $false
$expectedColumns = if ($RenderedViews -eq 1) { 1 } else { 4 }
$expectedRows = if ($RenderedViews -eq 1) { 1 } else { 3 }
$expectedQuiltWidth = 128 * $expectedColumns
$expectedQuiltHeight = 96 * $expectedRows
$graphicsGrowthStopBytes = if ($RenderedViews -eq 1) { 536870912L } else { 2147483648L }
$privateGrowthStopBytes = if ($RenderedViews -eq 1) { 536870912L } else { 1073741824L }

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

if (-not (Test-Path -LiteralPath $baseRunner) -or -not (Test-Path -LiteralPath $project)) { throw 'The isolated project or base runner is missing.' }
Assert-UnderRoot $fixtureRoot (Join-Path $localRoot 'URP/Results') 'Full Custom Null fixture evidence directory'
if (-not (Test-Path -LiteralPath $fixtureJson) -or -not (Test-Path -LiteralPath $fixtureSummary) -or
    -not (Test-Path -LiteralPath $fixtureDerived) -or -not (Test-Path -LiteralPath $fixtureRunner)) { throw 'The Full Custom Null fixture evidence is incomplete.' }
if (-not (Test-Path -LiteralPath $cloneSource) -or -not (Test-Path -LiteralPath $cloneMeta) -or -not (Test-Path -LiteralPath $cloneAsmdef)) { throw 'The clone-only Full Native adapter is missing.' }
if ((Test-Path -LiteralPath $results) -or (Test-Path -LiteralPath $projectEvidence)) { throw 'Refusing to overwrite an earlier Full Native probe run.' }

$fixture = Get-Content -LiteralPath $fixtureJson -Raw | ConvertFrom-Json
$fixtureDerivedRecord = Get-Content -LiteralPath $fixtureDerived -Raw | ConvertFrom-Json
$fixtureRunnerRecord = Get-Content -LiteralPath $fixtureRunner -Raw | ConvertFrom-Json
$fixtureSummaryRecord = Get-Content -LiteralPath $fixtureSummary -Raw | ConvertFrom-Json
if ($fixture.schema -ne 'viewweave-sample-texture-full-custom-capacity-v1' -or $fixture.status -ne 'success' -or
    $fixture.graphicsDevice -ne 'Null' -or $fixture.sourceParticleCapacity -ne 160000 -or
    $fixture.fullCustomParticleCapacity -ne 160000 -or $fixture.fullCustomInstancingMode -ne 'Custom' -or
    $fixture.fullCustomInstancingModeValue -ne 1 -or $fixture.fullCustomInstancingCapacity -ne 1 -or
    $fixture.fullCustomDisabledReasonName -ne 'None' -or $fixture.fullCustomDisabledReasonValue -ne $fixture.fullCustomDisabledReasonNoneValue -or
    $fixture.fullCustomGridResolutionX -ne 400 -or $fixture.fullCustomGridResolutionY -ne 400 -or
    -not $fixture.sourceAssetUnchanged -or -not $fixture.controlAssetUnchanged -or -not $fixture.openSceneFilesUnchanged -or
    -not $fixture.finalSourceAndControlIntegrityGate -or $fixture.runtimeGraphActivation -or $fixture.nativeMeasurement) {
    throw 'The Full Custom Null fixture does not match the source-preserving 160000/400x400/Custom-1 scope.'
}
if ($fixtureDerivedRecord.schema -ne 'viewweave-full-custom-null-derived-validation-v1' -or
    $fixtureDerivedRecord.status -ne 'pass' -or -not $fixtureDerivedRecord.exactNUnitXmlGate -or
    -not $fixtureDerivedRecord.fixtureGate -or -not $fixtureDerivedRecord.processExitDrainResidualGate -or
    $fixtureDerivedRecord.editorExitCode -ne 0 -or $fixtureDerivedRecord.timeout -or
    $fixtureDerivedRecord.outputDrainIncomplete -or -not $fixtureDerivedRecord.processInventoryAvailableAfter -or
    $fixtureDerivedRecord.processInventoryResidualCount -ne 0) { throw 'The derived Full Custom Null validation does not prove the exact drained zero-residual pass.' }
if ($fixtureRunnerRecord.nunit.total -ne 1 -or $fixtureRunnerRecord.nunit.passed -ne 1 -or
    $fixtureRunnerRecord.nunit.failed -ne 0 -or $fixtureRunnerRecord.editorExitCode -ne 0 -or
    $fixtureRunnerRecord.timeout -or $fixtureRunnerRecord.outputDrainIncomplete -or
    -not $fixtureRunnerRecord.processInventoryAvailableAfter -or $fixtureRunnerRecord.processInventoryResidualCount -ne 0) {
    throw 'The preserved base runner record does not support the derived Full Custom Null pass.'
}
$selectedAsset = Join-Path $project ($fixture.fullCustomAssetPath.Replace('/','\'))
if (-not (Test-Path -LiteralPath $selectedAsset) -or (Get-Sha256 $selectedAsset) -ne $fixture.fullCustomAssetSha256After) { throw 'The Full Custom clone differs from its passing Null fixture.' }
$metaGuidLine = (Select-String -LiteralPath $cloneMeta -Pattern '^guid: ([0-9a-f]{32})$').Matches.Groups[1].Value
if ($metaGuidLine -ne $expectedMetaGuid) { throw 'The Full Native clone test meta GUID differs from the reviewed adapter.' }
$cloneSourceHashBefore = Get-Sha256 $cloneSource
$cloneMetaHash = Get-Sha256 $cloneMeta
$runnerHashBefore = Get-Sha256 $runnerSource
$fixtureHash = Get-Sha256 $fixtureJson
$fixtureDerivedHash = Get-Sha256 $fixtureDerived
$sourceRowsBefore = @(Get-ExactProjectRows $project)
if ($sourceRowsBefore.Count -gt 0) { throw 'An Editor already targets the isolated clone; no files were changed and no test was started.' }

New-Item -ItemType Directory -Path $results -Force | Out-Null
New-Item -ItemType Directory -Path $projectEvidence -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $results 'asrun-source') -Force | Out-Null
Copy-Item -LiteralPath $cloneSource -Destination (Join-Path $results 'asrun-source/SampleTextureFullNativeProbeTests.cs')
Copy-Item -LiteralPath $cloneMeta -Destination (Join-Path $results 'asrun-source/SampleTextureFullNativeProbeTests.cs.meta')
Copy-Item -LiteralPath $runnerSource -Destination (Join-Path $results 'asrun-source/Run-SampleTextureFullNativeProbe.ps1')
Copy-Item -LiteralPath $fixtureJson -Destination (Join-Path $results 'full-custom-null-fixture-result.json')
Copy-Item -LiteralPath $fixtureDerived -Destination (Join-Path $results 'full-custom-null-derived-validation.json')
Copy-Item -LiteralPath $fixtureSummary -Destination (Join-Path $results 'full-custom-null-raw-summary.json')
Copy-Item -LiteralPath $fixtureRunner -Destination (Join-Path $results 'full-custom-null-runner.json')

$runEvidenceBefore = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File -ErrorAction SilentlyContinue | ForEach-Object FullName)
$envMap = [ordered]@{
    VW_SAMPLE_TEXTURE_FULL_NATIVE_RESULT = $projectEvidence
    VW_SAMPLE_TEXTURE_FULL_NATIVE_FIXTURE = $fixtureJson
    VW_SAMPLE_TEXTURE_FULL_NATIVE_MODE = 'Custom'
    VW_SAMPLE_TEXTURE_FULL_NATIVE_RESTART_CYCLES = '0'
    VW_SAMPLE_TEXTURE_FULL_NATIVE_RENDERED_VIEWS = [string]$RenderedViews
}
$oldEnvironment = @{}
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
    try { $underlying = Get-Content -LiteralPath $newRecords[0] -Raw | ConvertFrom-Json } catch { $invocationFailure = 'UnderlyingRunnerJsonInvalid' }
    if ($underlying) { Copy-Item -LiteralPath $newRecords[0] -Destination $runnerRecordPath }
}
if ($underlying) {
    $rawXmlPath = [string]$underlying.rawXmlPath
    if ($rawXmlPath -and (Test-Path -LiteralPath $rawXmlPath)) { Copy-Item -LiteralPath $rawXmlPath -Destination $xmlCopyPath }
}
$nativePath = Join-Path $projectEvidence 'native-result.json'
if (Test-Path -LiteralPath $nativePath) {
    Copy-Item -LiteralPath $nativePath -Destination $nativeResultCopyPath
    try { $native = Get-Content -LiteralPath $nativePath -Raw | ConvertFrom-Json } catch { $invocationFailure = 'NativeResultJsonInvalid' }
}
if (Test-Path -LiteralPath $xmlCopyPath) {
    try {
        [xml]$xmlDocument = [IO.File]::ReadAllText($xmlCopyPath)
        $xmlNames = @($xmlDocument.SelectNodes('//test-case') | ForEach-Object { [string]$_.fullname })
        $nunitExact = $xmlNames.Count -eq 1 -and $xmlNames[0] -eq $filter
    } catch { $invocationFailure = 'NativeXmlInvalid' }
}

$assetInventoryGate = $false
$assetInventoryCapturePhases = @()
$assetInventoryFailures = @()
if ($native -and $native.checkpoints) {
    foreach ($checkpoint in $native.checkpoints) {
        if ([string]::IsNullOrWhiteSpace([string]$checkpoint.allAssetBatchInfoJson)) { continue }
        try {
            $inventory = [string]$checkpoint.allAssetBatchInfoJson | ConvertFrom-Json
            if ($inventory.sceneEffectComponentCount -gt 0 -and @($inventory.assets).Count -gt 0) {
                $assetInventoryCapturePhases += [string]$inventory.phase
                foreach ($assetRow in @($inventory.assets)) {
                    if (-not [string]::IsNullOrWhiteSpace([string]$assetRow.failure)) { $assetInventoryFailures += "$($inventory.phase):$($assetRow.assetGuid):$($assetRow.failure)" }
                }
            }
        } catch { $assetInventoryFailures += 'invalid-snapshot-json' }
    }
    $assetInventoryGate = $assetInventoryCapturePhases -contains 'vfx-off-warmed' -and
        (($assetInventoryCapturePhases -contains 'selected-graph-draw') -or ($assetInventoryCapturePhases -contains 'growth-stop'))
}

$testPass = $native -and $native.status -eq 'passed' -and $native.schema -eq 'viewweave-sample-texture-full-native-probe-v1' -and
    $native.mode -eq 'FullCustom' -and $native.graphicsApiGate -and $native.pipelineGate -and $native.twoCameraGate -and $native.isolatedHistoryGate -and
    $native.outputLayoutGate -and $native.quiltViewRenderMode -eq 'Full' -and $native.renderedViewsRequested -eq $RenderedViews -and
    $native.outputColumns -eq $expectedColumns -and $native.outputRows -eq $expectedRows -and $native.outputQuiltWidth -eq $expectedQuiltWidth -and $native.outputQuiltHeight -eq $expectedQuiltHeight -and
    $native.graphicsGrowthStopBytes -eq $graphicsGrowthStopBytes -and $native.privateGrowthStopBytes -eq $privateGrowthStopBytes -and
    $native.sourceParticleCapacity -eq 160000 -and $native.selectedParticleCapacity -eq 160000 -and
    $native.sourceGridResolutionX -eq 400 -and $native.sourceGridResolutionY -eq 400 -and
    $native.selectedGridResolutionX -eq 400 -and $native.selectedGridResolutionY -eq 400 -and
    $native.selectedAssetGuid -eq $fixture.fullCustomAssetGuid -and $native.selectedAssetSha256 -eq $fixture.fullCustomAssetSha256After -and
    $native.gridResolutionGate -and $native.historyCameraCountAfterOff -eq 1 -and $native.historyCameraCountAfterActive -eq 1 -and
    $native.renderedViewCountAfterOff -eq 1 -and $native.renderedViewCountAfterActive -eq 1 -and
    $native.effectiveAANoneCountAfterOff -eq $RenderedViews -and $native.effectiveAANoneCountAfterActive -eq $RenderedViews -and
    $native.effectiveAANoneAfterOffGate -and $native.effectiveAANoneAfterActiveGate -and
    @($native.effectiveAAModesAfterOff -split ',').Count -eq $RenderedViews -and @($native.effectiveAAModesAfterActive -split ',').Count -eq $RenderedViews -and
    $native.activeParticlesGate -and $native.pixelDifferenceEachTileGate -and @($native.changedPixelCountPerOutputTile).Count -eq $RenderedViews -and
    @($native.changedPixelCountPerOutputTile | Where-Object { $_ -le 0 }).Count -eq 0 -and
    $native.measurementComplete -and $native.checkpoint8Reached -and $native.checkpoint16Reached -and $native.checkpoint32Reached -and
    $native.growthStopNotReached -and $native.descriptorGate -and $native.sourceGraphUnchanged -and $native.selectedAssetUnchanged -and
    $native.sceneDiskUnchanged -and -not $native.sceneSavedDuringRun -and $native.noUnityErrorsDuringRun -and $native.cleanupPassed -and
    $native.historyReleased -and $native.viewTextureReleased -and $native.resolveTextureReleased -and $native.quiltTextureReleased
$nunitPass = $underlying -and $underlying.nunit.total -eq 1 -and $underlying.nunit.passed -eq 1 -and
    $underlying.nunit.failed -eq 0 -and $underlying.nunit.skipped -eq 0 -and $underlying.nunit.inconclusive -eq 0 -and
    $underlying.editorExitCode -eq 0 -and -not $underlying.timeout -and -not $underlying.outputDrainIncomplete -and
    $underlying.processInventoryAvailableAfter -and $underlying.processInventoryResidualCount -eq 0 -and $underlying.testPassGate
$sourceHashAfter = Get-Sha256 $cloneSource
$metaHashAfter = Get-Sha256 $cloneMeta
$runnerHashAfter = Get-Sha256 $runnerSource
$fixtureHashAfter = Get-Sha256 $fixtureJson
$fixtureDerivedHashAfter = Get-Sha256 $fixtureDerived
$rowsAfter = @(Get-ExactProjectRows $project)
$finalPass = $newRecords.Count -eq 1 -and $nunitExact -and $nunitPass -and $testPass -and $assetInventoryGate -and
    $sourceHashAfter -eq $cloneSourceHashBefore -and $metaHashAfter -eq $cloneMetaHash -and
    $runnerHashAfter -eq $runnerHashBefore -and $fixtureHashAfter -eq $fixtureHash -and $fixtureDerivedHashAfter -eq $fixtureDerivedHash -and $rowsAfter.Count -eq 0 -and
    $invocationFailure -eq $null

$summary = [ordered]@{
    schema='viewweave-sample-texture-full-native-runner-v1'
    status=if($finalPass){'full-native-probe-passed'}else{'not-passed'}
    mode='FullCustom'; testPlatform='PlayMode'; graphicsApiRequested='D3D12'; testFilter=$filter; expectedTestCount=1
    renderedViews=$RenderedViews; outputColumns=$expectedColumns; outputRows=$expectedRows; outputQuiltWidth=$expectedQuiltWidth; outputQuiltHeight=$expectedQuiltHeight
    graphicsGrowthStopBytes=$graphicsGrowthStopBytes; privateGrowthStopBytes=$privateGrowthStopBytes
    fixturePath=$fixtureRoot; fixtureNullStatus=$fixture.status; fixtureRawOuterRunnerStatus=$fixtureSummaryRecord.status
    fixtureDerivedStatus=$fixtureDerivedRecord.status; fixtureDerivedSha256=$fixtureDerivedHash
    fixtureSourceAssetGuid=$fixture.sourceAssetGuid; fixtureSourceAssetSha256=$fixture.sourceAssetSha256Before
    fixtureControlAssetGuid=$fixture.controlAssetGuid; fullCustomAssetPath=$fixture.fullCustomAssetPath
    fullCustomAssetGuid=$fixture.fullCustomAssetGuid; fullCustomAssetSha256=$fixture.fullCustomAssetSha256After
    fullCustomParticleCapacity=$fixture.fullCustomParticleCapacity; gridResolution=@($fixture.fullCustomGridResolutionX,$fixture.fullCustomGridResolutionY)
    instancingMode=$fixture.fullCustomInstancingMode; instancingCapacity=$fixture.fullCustomInstancingCapacity
    cloneSourcePath=$cloneSource; cloneSourceSha256=$cloneSourceHashBefore; cloneMetaGuid=$expectedMetaGuid; cloneMetaSha256=$cloneMetaHash
    runnerPath=$runnerSource; runnerSha256=$runnerHashBefore; runnerSha256After=$runnerHashAfter
    runId=$runId; projectPath=$project; evidencePath=$projectEvidence; evidenceStatus=if($native){$native.status}else{'missing'}
    evidencePassGate=[bool]$testPass; allAssetInventoryPassGate=[bool]$assetInventoryGate
    allAssetInventoryCapturePhases=@($assetInventoryCapturePhases); allAssetInventoryApiFailures=@($assetInventoryFailures)
    underlyingRunnerPath=if($newRecords.Count -eq 1){$newRecords[0]}else{$null}; editorProcessId=if($underlying){$underlying.editorProcessId}else{$null}
    editorExitCode=if($underlying){$underlying.editorExitCode}else{$null}; timeout=if($underlying){$underlying.timeout}else{$null}
    outputDrainIncomplete=if($underlying){$underlying.outputDrainIncomplete}else{$null}
    processInventoryAvailableAfter=if($underlying){$underlying.processInventoryAvailableAfter}else{$null}
    processInventoryResidualCount=if($underlying){$underlying.processInventoryResidualCount}else{$null}
    rawXmlPath=if(Test-Path -LiteralPath $xmlCopyPath){$xmlCopyPath}else{$null}
    rawXmlSha256=if(Test-Path -LiteralPath $xmlCopyPath){Get-Sha256 $xmlCopyPath}else{$null}
    nunitExactCaseGate=[bool]$nunitExact; nunit=if($underlying){$underlying.nunit}else{$null}
    graphicsPeakGrowthBytes=if($native){$native.graphicsPeakGrowthBytes}else{$null}
    privatePeakGrowthBytes=if($native){$native.privatePeakGrowthBytes}else{$null}
    measurementComplete=if($native){$native.measurementComplete}else{$false}
    nativeResultSha256=if(Test-Path -LiteralPath $nativeResultCopyPath){Get-Sha256 $nativeResultCopyPath}else{$null}
    cloneSourceSha256After=$sourceHashAfter; cloneMetaSha256After=$metaHashAfter
    fixtureJsonSha256Before=$fixtureHash; fixtureJsonSha256After=$fixtureHashAfter
    fixtureDerivedSha256Before=$fixtureDerivedHash; fixtureDerivedSha256After=$fixtureDerivedHashAfter
    cloneResidualProcessCount=$rowsAfter.Count; invocationErrorType=$invocationFailure; invocationErrorMessage=$invocationErrorMessage
    safetyStopSemantics='observed process-global graphics and process-private growth stops; post-allocation checks, not hard allocation caps'
    editorLogIncluded=$editorLogIncluded; passGate=[bool]$finalPass
}
[IO.File]::WriteAllText($runSummaryPath,($summary | ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Get-Content -LiteralPath $runSummaryPath -Raw | Write-Output
if (-not $finalPass) { exit 1 }
