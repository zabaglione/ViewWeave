param(
    [Parameter(Mandatory=$true)][ValidateSet('URP')][string]$Pipeline
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path 'REPO_ROOT').Path
$localRoot = Join-Path $repo '.local/windows-urp-vfx-frustum-memory-20261013'
$project = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$baseRunner = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/Tools/Run-UnityIsolated.ps1'
$baseResults = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/Results'
$sourcePath = Join-Path $localRoot 'Shared/SampleTextureCapacityFixture.cs'
$cloneTestDirectory = Join-Path $project 'Assets/Editor/FrustumMetadataProbe'
$cloneSourcePath = Join-Path $cloneTestDirectory 'SampleTextureCapacityFixture.cs'
$testFilter = 'ViewWeave.SceneComparison.Tests.SampleTextureCapacityFixture.CreatesAutoAndCustomCapacityOnlyClonesAndDescribesCompiledAssets'

if (-not (Test-Path -LiteralPath $baseRunner) -or -not (Test-Path -LiteralPath $project) -or -not (Test-Path -LiteralPath $sourcePath) -or -not (Test-Path -LiteralPath $cloneTestDirectory)) {
    throw 'Required isolated URP clone, Editor test assembly, or canonical fixture source is missing.'
}
$resolvedProject = (Resolve-Path -LiteralPath $project).Path.TrimEnd('\')
$approvedProject = (Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject').TrimEnd('\')
if (-not $resolvedProject.Equals($approvedProject, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to use an unapproved Unity project path.' }
if (Test-Path -LiteralPath $cloneSourcePath) { throw 'Refusing to overwrite an existing clone test source.' }
if (Test-Path -LiteralPath ($cloneSourcePath + '.meta')) { throw 'Refusing to overwrite an existing clone test metadata file.' }
if (Test-Path -LiteralPath (Join-Path $project 'Assets/FrustumMemoryFixtures')) { throw 'Refusing to reuse prior fixture assets; use a fresh approved clone.' }

$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$runDir = Join-Path $localRoot "URP/Results/SampleTextureCapacity-$stamp"
if (Test-Path -LiteralPath $runDir) { throw 'Refusing to overwrite prior fixture results.' }
New-Item -ItemType Directory -Path $runDir | Out-Null
$cloneTestResults = Join-Path $project 'TestResults'
if (-not (Test-Path -LiteralPath $cloneTestResults)) { New-Item -ItemType Directory -Path $cloneTestResults | Out-Null }
$cloneResultJson = Join-Path $cloneTestResults "fixture-result-SampleTextureCapacity-$stamp.json"
$resultJson = Join-Path $runDir 'fixture-result.json'
$fixtureCopy = $resultJson
if (Test-Path -LiteralPath $cloneResultJson) { throw 'Refusing to overwrite prior clone fixture evidence.' }
$runnerRecord = Join-Path $runDir 'runner.json'
$xmlCopy = Join-Path $runDir 'results.xml'
$summaryPath = Join-Path $runDir 'summary.json'
$beforeRecords = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
$envName = 'VW_SAMPLE_TEXTURE_CAPACITY_RESULT'
$oldEnv = [Environment]::GetEnvironmentVariable($envName, 'Process')
$runnerOutput = $null
$underlyingRunnerPath = $null
$invocationError = $null

try {
    Copy-Item -LiteralPath $sourcePath -Destination $cloneSourcePath
    $cloneHash = (Get-FileHash -LiteralPath $cloneSourcePath -Algorithm SHA256).Hash
    if ($sourceHash -ne $cloneHash) { throw 'Canonical and clone source hashes differ.' }
    [Environment]::SetEnvironmentVariable($envName, $cloneResultJson, 'Process')
    try {
        $runnerOutput = & $baseRunner -Pipeline URP -Operation RunTests -TestPlatform EditMode -TestFilter $testFilter -ExpectedTestCount 1 -TimeoutSeconds 900 -NoGraphics
    }
    catch { $invocationError = $_.Exception.GetType().Name }
}
finally {
    [Environment]::SetEnvironmentVariable($envName, $oldEnv, 'Process')
}

$afterRecords = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
$createdRecords = @($afterRecords | Where-Object { $beforeRecords -notcontains $_ })
if ($createdRecords.Count -eq 1) { $underlyingRunnerPath = $createdRecords[0] }
$underlying = if ($underlyingRunnerPath) { Get-Content -LiteralPath $underlyingRunnerPath -Raw | ConvertFrom-Json } else { $null }
$xmlSource = if ($underlying -and $underlying.rawXmlPath -and (Test-Path -LiteralPath $underlying.rawXmlPath)) { $underlying.rawXmlPath } else { $null }
if ($underlyingRunnerPath) { Copy-Item -LiteralPath $underlyingRunnerPath -Destination $runnerRecord }
if ($xmlSource) { Copy-Item -LiteralPath $xmlSource -Destination $xmlCopy }
$cloneSourceHashAfter = if (Test-Path -LiteralPath $cloneSourcePath) { (Get-FileHash -LiteralPath $cloneSourcePath -Algorithm SHA256).Hash } else { $null }
if ((Test-Path -LiteralPath $cloneResultJson) -and -not (Test-Path -LiteralPath $fixtureCopy)) { Copy-Item -LiteralPath $cloneResultJson -Destination $fixtureCopy }
$xmlCaseNames = @()
if (Test-Path -LiteralPath $xmlCopy) {
    [xml]$xmlDocument = Get-Content -LiteralPath $xmlCopy -Raw
    $xmlCaseNames = @($xmlDocument.SelectNodes('//test-case') | ForEach-Object { [string]$_.fullname })
}
$exactCaseGate = $xmlCaseNames.Count -eq 1 -and $xmlCaseNames[0] -eq $testFilter
$drainGate = $null -ne $underlying -and $null -ne $underlying.PSObject.Properties['outputDrainIncomplete'] -and $underlying.outputDrainIncomplete -eq $false
$noResidualGate = $null -ne $underlying -and $underlying.processInventoryAvailableAfter -eq $true -and $underlying.processInventoryResidualCount -eq 0
$testGate = $null -ne $underlying -and $underlying.editorExitCode -eq 0 -and -not $underlying.timeout -and $drainGate -and $noResidualGate -and $underlying.testPassGate -and $exactCaseGate
$fixture = if (Test-Path -LiteralPath $fixtureCopy) { Get-Content -LiteralPath $fixtureCopy -Raw | ConvertFrom-Json } else { $null }
$fixtureGate = $null -ne $fixture -and $fixture.status -eq 'success' -and $fixture.graphicsDevice -eq 'Null' -and
    $fixture.customCompilerDisabledReasonName -eq 'None' -and $fixture.sourceAssetUnchanged -and $fixture.openSceneFilesUnchanged -and
    $fixture.activeSceneCameraCount -eq 0 -and $fixture.activeSceneVfxCount -eq 0 -and
    $fixture.controlInstancingModeValue -eq $fixture.originalInstancingModeValue -and
    $fixture.controlInstancingCapacity -eq $fixture.originalInstancingCapacity -and
    $fixture.controlDescriptors.compileStatus -eq 'success' -and $fixture.autoDescriptors.compileStatus -eq 'success' -and $fixture.customDescriptors.compileStatus -eq 'success' -and
    -not [string]::IsNullOrWhiteSpace($fixture.controlComparableStructureSha256) -and
    $fixture.controlComparableStructureSha256 -eq $fixture.autoComparableStructureSha256 -and
    $fixture.controlComparableStructureSha256 -eq $fixture.customComparableStructureSha256
$passed = $testGate -and $fixtureGate -and $sourceHash -eq $cloneSourceHashAfter -and $sourceHash -eq (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash

$summary = [ordered]@{
    schema = 'viewweave-sample-texture-capacity-runner-v1'
    status = if ($passed) { 'fixture-passed' } else { 'not-passed' }
    pipeline = 'URP'
    testFilter = $testFilter
    expectedTestCount = 1
    editorExitCode = if ($underlying) { $underlying.editorExitCode } else { $null }
    editorProcessId = if ($underlying) { $underlying.editorProcessId } else { $null }
    timeout = if ($underlying) { $underlying.timeout } else { $null }
    outputDrainIncomplete = if ($underlying) { $underlying.outputDrainIncomplete } else { $null }
    processInventoryResidualCount = if ($underlying) { $underlying.processInventoryResidualCount } else { $null }
    noResidualGate = [bool]$noResidualGate
    testPassGate = if ($underlying) { $underlying.testPassGate } else { $false }
    exactXmlCaseGate = [bool]$exactCaseGate
    cloneSourceSha256 = $cloneSourceHashAfter
    canonicalSourceSha256 = $sourceHash
    fixtureStatus = if ($fixture) { $fixture.status } else { 'missing' }
    controlGraphSha256 = if ($fixture) { $fixture.controlAssetSha256 } else { $null }
    autoGraphSha256 = if ($fixture) { $fixture.autoAssetSha256 } else { $null }
    customGraphSha256 = if ($fixture) { $fixture.customAssetSha256 } else { $null }
    controlDescriptorCompileStatus = if ($fixture) { $fixture.controlDescriptors.compileStatus } else { $null }
    controlSettings = if ($fixture) { [ordered]@{ particleCapacity = $fixture.sourceParticleCapacity; instancingMode = $fixture.controlInstancingModeValue; instancingCapacity = $fixture.controlInstancingCapacity } } else { $null }
    comparableStructureHashesMatch = if ($fixture) { $fixture.controlComparableStructureSha256 -eq $fixture.autoComparableStructureSha256 -and $fixture.controlComparableStructureSha256 -eq $fixture.customComparableStructureSha256 } else { $false }
    controlAutoCustomDescriptorsCompile = if ($fixture) { $fixture.controlDescriptors.compileStatus -eq 'success' -and $fixture.autoDescriptors.compileStatus -eq 'success' -and $fixture.customDescriptors.compileStatus -eq 'success' } else { $false }
    customCompilerDisabledReason = if ($fixture) { $fixture.customCompilerDisabledReasonName } else { $null }
    fixtureResultJson = if (Test-Path -LiteralPath $fixtureCopy) { 'fixture-result.json' } else { $null }
    copiedRunnerRecord = if (Test-Path -LiteralPath $runnerRecord) { 'runner.json' } else { $null }
    copiedXml = if (Test-Path -LiteralPath $xmlCopy) { 'results.xml' } else { $null }
    invocationErrorType = $invocationError
    graphicsExecution = 'NoGraphics; EditMode only; no Play Mode, camera rendering, or device output.'
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Get-Content -LiteralPath $summaryPath -Raw | Write-Output
if (-not $passed) { exit 1 }
