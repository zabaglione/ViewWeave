param(
    [Parameter(Mandatory=$true)][ValidateSet('URP')][string]$Pipeline
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path 'REPO_ROOT').Path
$localRoot = Join-Path $repo '.local/windows-urp-vfx-frustum-memory-20261013'
$project = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$baseRunner = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/Tools/Run-UnityIsolated.ps1'
$baseResults = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/Results'
$preflightPath = Join-Path $baseResults 'scene-preflight-20261012T223138298Z.json'
$descriptorPath = Join-Path $baseResults 'vfx-descriptor-inventory-20261013T001646852Z.json'
$scene = 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/URP_VFX_Learning_Templates.unity'
$preparedScene = 'Assets/SceneComparison/URP_SelectedPilot.unity'
$testFilter = 'ViewWeave.SceneComparison.Tests.FrustumSceneMetadataProbeTests.CapturesSanitizedSceneInstancesAndJoinsExistingGraphDescriptors'
if (-not (Test-Path -LiteralPath $baseRunner) -or -not (Test-Path -LiteralPath $project) -or -not (Test-Path -LiteralPath $preflightPath) -or -not (Test-Path -LiteralPath $descriptorPath)) { throw 'Required isolated project or source inventory is missing.' }
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$runDir = Join-Path $localRoot "URP/Results/$stamp"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null
$resultJson = Join-Path $runDir 'instance-metadata.json'
$runnerRecord = Join-Path $runDir 'runner.json'
$xmlCopy = Join-Path $runDir 'results.xml'
$summaryPath = Join-Path $runDir 'summary.json'
$sourcePath = Join-Path $localRoot 'Shared/FrustumSceneMetadataProbe.cs'
$cloneSourcePath = Join-Path $project 'Assets/Editor/FrustumMetadataProbe/FrustumSceneMetadataProbe.cs'
$cloneMetaPath = $cloneSourcePath + '.meta'
if (-not (Test-Path -LiteralPath $sourcePath) -or -not (Test-Path -LiteralPath $cloneSourcePath) -or -not (Test-Path -LiteralPath $cloneMetaPath)) { throw 'Canonical or clone probe source is missing.' }
$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$cloneHash = (Get-FileHash -LiteralPath $cloneSourcePath -Algorithm SHA256).Hash
if ($sourceHash -ne $cloneHash) { throw 'Canonical and clone source hashes differ.' }
$beforeRecords = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
$envNames = @('VW_FRUSTUM_RESULT','VW_FRUSTUM_PREPARED_SCENE','VW_FRUSTUM_SOURCE_SCENE','VW_FRUSTUM_PREFLIGHT_JSON','VW_FRUSTUM_DESCRIPTOR_JSON')
$oldEnv = @{}
foreach ($name in $envNames) { $oldEnv[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$invocationError = $null
$runnerOutput = $null
$recordData = $null
try {
    $env:VW_FRUSTUM_RESULT = $resultJson
    $env:VW_FRUSTUM_PREPARED_SCENE = $preparedScene
    $env:VW_FRUSTUM_SOURCE_SCENE = $scene
    $env:VW_FRUSTUM_PREFLIGHT_JSON = $preflightPath
    $env:VW_FRUSTUM_DESCRIPTOR_JSON = $descriptorPath
    try {
        $runnerOutput = & $baseRunner -Pipeline URP -Operation RunTests -TestPlatform EditMode -TestFilter $testFilter -ExpectedTestCount 1 -TimeoutSeconds 900 -NoGraphics
        $text = @($runnerOutput | ForEach-Object { [string]$_ }) -join "`n"
        $recordData = $text | ConvertFrom-Json -ErrorAction Stop
    }
    catch { $invocationError = $_.Exception.GetType().Name }
}
finally {
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $oldEnv[$name], 'Process') }
}
$afterRecords = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
$createdRecords = @($afterRecords | Where-Object { $beforeRecords -notcontains $_ })
$underlyingRunnerPath = if ($createdRecords.Count -eq 1) { $createdRecords[0] } else { $null }
$underlying = if ($underlyingRunnerPath) { Get-Content -LiteralPath $underlyingRunnerPath -Raw | ConvertFrom-Json } else { $null }
$xmlSource = if ($underlying -and $underlying.rawXmlPath -and (Test-Path -LiteralPath $underlying.rawXmlPath)) { $underlying.rawXmlPath } else { $null }
if ($underlyingRunnerPath) { Copy-Item -LiteralPath $underlyingRunnerPath -Destination $runnerRecord }
if ($xmlSource) { Copy-Item -LiteralPath $xmlSource -Destination $xmlCopy }
$instanceHash = if (Test-Path -LiteralPath $resultJson) { (Get-FileHash -LiteralPath $resultJson -Algorithm SHA256).Hash } else { $null }
$instanceData = if (Test-Path -LiteralPath $resultJson) { Get-Content -LiteralPath $resultJson -Raw | ConvertFrom-Json } else { $null }
$testNames = @()
if ($null -ne $underlying -and $null -ne $underlying.nunit -and $null -ne $underlying.nunit.testCaseFullNames) { $testNames = @($underlying.nunit.testCaseFullNames) }


$exactCase = $false
if ($testNames.Count -eq 1) { $exactCase = $testNames[0] -eq $testFilter }
$drainVerified = $null -ne $underlying -and $null -ne $underlying.PSObject.Properties['outputDrainIncomplete'] -and $underlying.outputDrainIncomplete -eq $false
$passGate = $null -ne $underlying -and $underlying.editorExitCode -eq 0 -and -not $underlying.timeout -and $drainVerified -and
    $underlying.processInventoryAvailableAfter -and @($underlying.matchingAfter).Count -eq 0 -and $underlying.testPassGate -and $exactCase -and
    $null -ne $instanceData -and $instanceData.status -eq 'success' -and $instanceData.sceneIsolationGate -and $instanceData.descriptorInventoryGate -and
    $instanceData.instanceGuidJoinGate -and $instanceData.noMutationGate -and -not $instanceData.wholeSceneReferenceGate -and
    $instanceData.knownEyesEntryVerified -and $instanceData.knownEyesInactiveUnresolved -and $instanceData.priorSnapshotMatchesKnownEyesUnresolvedState
$summary = [ordered]@{
    schema = 'viewweave-frustum-metadata-runner-v1'
    status = if ($passGate) { 'metadata-probe-passed' } else { 'not-passed' }
    pipeline = 'URP'
    testFilter = $testFilter
    expectedTestCount = 1
    editorExitCode = if ($underlying) { $underlying.editorExitCode } else { $null }
    editorProcessId = if ($underlying) { $underlying.editorProcessId } else { $null }
    timeout = if ($underlying) { $underlying.timeout } else { $null }
    outputDrainIncomplete = if ($underlying) { $underlying.outputDrainIncomplete } else { $null }
    processResidualCount = if ($underlying) { $underlying.processInventoryResidualCount } else { $null }
    nunit = if ($underlying) { $underlying.nunit } else { $null }
    exactTestCaseGate = [bool]$exactCase
    runnerPassGate = if ($underlying) { $underlying.testPassGate } else { $false }
    metadataStatus = if ($instanceData) { $instanceData.status } else { 'missing' }
    sceneIsolationGate = if ($instanceData) { $instanceData.sceneIsolationGate } else { $false }
    descriptorInventoryGate = if ($instanceData) { $instanceData.descriptorInventoryGate } else { $false }
    instanceGuidJoinGate = if ($instanceData) { $instanceData.instanceGuidJoinGate } else { $false }
    noMutationGate = if ($instanceData) { $instanceData.noMutationGate } else { $false }
    wholeSceneReferenceGate = if ($instanceData) { $instanceData.wholeSceneReferenceGate } else { $null }
    priorSnapshotMatchesKnownEyesUnresolvedState = if ($instanceData) { $instanceData.priorSnapshotMatchesKnownEyesUnresolvedState } else { $false }
    knownEyesEntryVerified = if ($instanceData) { $instanceData.knownEyesEntryVerified } else { $false }
    knownEyesInactiveUnresolved = if ($instanceData) { $instanceData.knownEyesInactiveUnresolved } else { $false }
    canonicalSourceSha256 = $sourceHash
    cloneSourceSha256 = $cloneHash
    priorSnapshotSha256 = (Get-FileHash -LiteralPath $preflightPath -Algorithm SHA256).Hash
    descriptorInventorySha256 = (Get-FileHash -LiteralPath $descriptorPath -Algorithm SHA256).Hash
    metadataJson = 'instance-metadata.json'
    metadataJsonSha256 = $instanceHash
    copiedRunnerRecord = if (Test-Path -LiteralPath $runnerRecord) { 'runner.json' } else { $null }
    copiedXml = if (Test-Path -LiteralPath $xmlCopy) { 'results.xml' } else { $null }
    invocationErrorType = $invocationError
    editorLogIncluded = $false
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Get-Content -LiteralPath $summaryPath -Raw | Write-Output
if (-not $passGate) { exit 1 }
