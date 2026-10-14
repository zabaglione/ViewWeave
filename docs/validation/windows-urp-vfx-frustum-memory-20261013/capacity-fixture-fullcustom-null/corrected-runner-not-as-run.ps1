param(
    [Parameter(Mandatory=$true)][ValidateSet('URP')][string]$Pipeline
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path 'REPO_ROOT').Path
$localRoot = Join-Path $repo '.local/windows-urp-vfx-frustum-memory-20261013'
$project = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$baseRunner = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/Tools/Run-UnityIsolated.ps1'
$baseResults = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/Results'
$sourcePath = Join-Path $localRoot 'Shared/SampleTextureFullCustomCapacityFixture.cs'
$sourceMetaPath = $sourcePath + '.meta'
$cloneTestDirectory = Join-Path $project 'Assets/Editor/FrustumMetadataProbe'
$cloneSourcePath = Join-Path $cloneTestDirectory 'SampleTextureFullCustomCapacityFixture.cs'
$cloneMetaPath = $cloneSourcePath + '.meta'
$testFilter = 'ViewWeave.SceneComparison.Tests.SampleTextureFullCustomCapacityFixture.CreatesFullCapacityCustomCloneAndDescribesCompiledAsset'

if (-not (Test-Path -LiteralPath $baseRunner) -or -not (Test-Path -LiteralPath $project) -or -not (Test-Path -LiteralPath $sourcePath) -or -not (Test-Path -LiteralPath $sourceMetaPath) -or -not (Test-Path -LiteralPath $cloneTestDirectory)) {
    throw 'Required isolated URP clone, Editor test assembly, or canonical fixture source is missing.'
}
$resolvedProject = (Resolve-Path -LiteralPath $project).Path.TrimEnd('\')
$approvedProject = (Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject').TrimEnd('\')
if (-not $resolvedProject.Equals($approvedProject, [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to use an unapproved Unity project path.' }
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
try {
    $cloneProcessRows = @(Get-ExactProjectProcessRows $project)
    if ($cloneProcessRows.Count -gt 0) { throw 'An Editor already targets this exact clone; fixture source was not changed.' }
}
catch { throw 'Cannot verify the exact clone process inventory; fixture source was not changed.' }
$controlPath = Join-Path $project 'Assets/FrustumMemoryFixturesGrid40/Control/SampleTexture2D.vfx'
$controlMetaPath = $controlPath + '.meta'
if (-not (Test-Path -LiteralPath $controlPath) -or -not (Test-Path -LiteralPath $controlMetaPath)) { throw 'Saved Grid40 Control asset or metadata is missing.' }
$controlMetaText = [IO.File]::ReadAllText($controlMetaPath)
if ($controlMetaText -notmatch '(?m)^guid: 2f97a7e6edd4b5c4489e69768872259b$' -or (Get-FileHash -LiteralPath $controlPath -Algorithm SHA256).Hash -ne '55AE59AD9656256C04291B14FFAADECAF1BD622561A8E1B8BE53C66328CA6155') { throw 'Saved Grid40 Control GUID/hash differs from the approved fixture.' }
if (Test-Path -LiteralPath (Join-Path $project 'Assets/FrustumMemoryFullCustom')) { throw 'Refusing to reuse prior FullCustom fixture assets; use a fresh approved clone.' }

$sourceHash = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$sourceMetaHash = (Get-FileHash -LiteralPath $sourceMetaPath -Algorithm SHA256).Hash
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$runDir = Join-Path $localRoot "URP/Results/SampleTextureFullCustomCapacity-$stamp"
if (Test-Path -LiteralPath $runDir) { throw 'Refusing to overwrite prior fixture results.' }
New-Item -ItemType Directory -Path $runDir | Out-Null
$cloneTestResults = Join-Path $project 'TestResults'
if (-not (Test-Path -LiteralPath $cloneTestResults)) { New-Item -ItemType Directory -Path $cloneTestResults | Out-Null }
$cloneResultJson = Join-Path $cloneTestResults "fixture-result-SampleTextureFullCustomCapacity-$stamp.json"
$resultJson = Join-Path $runDir 'fixture-result.json'
$fixtureCopy = $resultJson
if (Test-Path -LiteralPath $cloneResultJson) { throw 'Refusing to overwrite prior clone fixture evidence.' }
$asRunSourceCopy = Join-Path $runDir 'as-run-SampleTextureFullCustomCapacityFixture.cs'
$asRunMetaCopy = $asRunSourceCopy + '.meta'
$priorCloneSourceCopy = Join-Path $runDir 'prior-clone-SampleTextureFullCustomCapacityFixture.cs'
$hadPriorCloneSource = Test-Path -LiteralPath $cloneSourcePath
$priorCloneMetaCopy = Join-Path $runDir 'prior-clone-SampleTextureFullCustomCapacityFixture.cs.meta'
$hadPriorCloneMeta = Test-Path -LiteralPath $cloneMetaPath
$priorCloneSourceHash = $null
$cloneAsRunSourceHash = $null
$cloneSourceRestored = $false
$cloneRestoreDeferredReason = $null
if ($hadPriorCloneSource) {
    Copy-Item -LiteralPath $cloneSourcePath -Destination $priorCloneSourceCopy
    $priorCloneSourceHash = (Get-FileHash -LiteralPath $priorCloneSourceCopy -Algorithm SHA256).Hash
}
if ($hadPriorCloneMeta) { Copy-Item -LiteralPath $cloneMetaPath -Destination $priorCloneMetaCopy }
$runnerRecord = Join-Path $runDir 'runner.json'
$xmlCopy = Join-Path $runDir 'results.xml'
$summaryPath = Join-Path $runDir 'summary.json'
$beforeRecords = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
$envName = 'VW_SAMPLE_TEXTURE_FULL_CUSTOM_RESULT'
$oldEnv = [Environment]::GetEnvironmentVariable($envName, 'Process')
$runnerOutput = $null
$underlyingRunnerPath = $null
$invocationError = $null

try {
    Copy-Item -LiteralPath $sourcePath -Destination $asRunSourceCopy
    Copy-Item -LiteralPath $sourceMetaPath -Destination $asRunMetaCopy
    Copy-Item -LiteralPath $sourcePath -Destination $cloneSourcePath -Force
    Copy-Item -LiteralPath $sourceMetaPath -Destination $cloneMetaPath -Force
    $cloneHash = (Get-FileHash -LiteralPath $cloneSourcePath -Algorithm SHA256).Hash
    $cloneAsRunSourceHash = $cloneHash
    $cloneMetaHash = (Get-FileHash -LiteralPath $cloneMetaPath -Algorithm SHA256).Hash
    if ($sourceMetaHash -ne $cloneMetaHash) { throw 'Canonical and clone metadata hashes differ.' }
    if ($sourceHash -ne $cloneHash) { throw 'Canonical and clone source hashes differ.' }
    [Environment]::SetEnvironmentVariable($envName, $cloneResultJson, 'Process')
    try {
        $runnerOutput = & $baseRunner -Pipeline URP -Operation RunTests -TestPlatform EditMode -TestFilter $testFilter -ExpectedTestCount 1 -TimeoutSeconds 900 -NoGraphics
    }
    catch { $invocationError = $_.Exception.GetType().Name }
}
finally {
    [Environment]::SetEnvironmentVariable($envName, $oldEnv, 'Process')
    $restoreSafe = $false
    try {
        $afterForRestore = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
        $newForRestore = @($afterForRestore | Where-Object { $beforeRecords -notcontains $_ })
        if ($newForRestore.Count -eq 1) {
            $restoreRecord = Get-Content -LiteralPath $newForRestore[0] -Raw | ConvertFrom-Json
            $restoreSafe = ($restoreRecord.status -eq 'preflight-process-inventory-failed' -and $null -eq $restoreRecord.editorProcessId) -or
                ($null -ne $restoreRecord.editorProcessId -and $null -ne $restoreRecord.editorExitCode -and $restoreRecord.processInventoryAvailableAfter -eq $true -and
                    $restoreRecord.processInventoryResidualCount -eq 0 -and $restoreRecord.processObjectStillRunning -eq $false -and $restoreRecord.outputDrainIncomplete -eq $false)
        }
    }
    catch { $restoreSafe = $false }
    if ($restoreSafe) {
        if ($hadPriorCloneSource -and (Test-Path -LiteralPath $priorCloneSourceCopy)) { Copy-Item -LiteralPath $priorCloneSourceCopy -Destination $cloneSourcePath -Force }
        elseif (-not $hadPriorCloneSource -and (Test-Path -LiteralPath $cloneSourcePath)) { Remove-Item -LiteralPath $cloneSourcePath }
        if ($hadPriorCloneMeta -and (Test-Path -LiteralPath $priorCloneMetaCopy)) { Copy-Item -LiteralPath $priorCloneMetaCopy -Destination $cloneMetaPath -Force }
        elseif (-not $hadPriorCloneMeta -and (Test-Path -LiteralPath $cloneMetaPath)) { Remove-Item -LiteralPath $cloneMetaPath }
        $cloneSourceRestored = $true
    }
    else { $cloneRestoreDeferredReason = 'owned Editor exit/drain/residual gate was not confirmed; as-run source remains installed in the clone' }
}

$afterRecords = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
$createdRecords = @($afterRecords | Where-Object { $beforeRecords -notcontains $_ })
if ($createdRecords.Count -eq 1) { $underlyingRunnerPath = $createdRecords[0] }
$underlying = if ($underlyingRunnerPath) { Get-Content -LiteralPath $underlyingRunnerPath -Raw | ConvertFrom-Json } else { $null }
$xmlSource = if ($underlying -and $underlying.rawXmlPath -and (Test-Path -LiteralPath $underlying.rawXmlPath)) { $underlying.rawXmlPath } else { $null }
if ($underlyingRunnerPath) { Copy-Item -LiteralPath $underlyingRunnerPath -Destination $runnerRecord }
if ($xmlSource) { Copy-Item -LiteralPath $xmlSource -Destination $xmlCopy }
$cloneSourceHashAfter = $cloneAsRunSourceHash
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
$expectedNormalizedFields = @('m_InstancingMode', 'm_InstancingCapacity')
$normalizedFieldsGate = $null -ne $fixture -and @($fixture.normalizedFields).Count -eq 2 -and (@($fixture.normalizedFields) -join '|') -ceq ($expectedNormalizedFields -join '|')
$descriptorText = if ($fixture -and $fixture.fullCustomDescriptors) { (@($fixture.fullCustomDescriptors.temporaryDescriptors) -join "`n") } else { '' }
$descriptorMappingText = if ($fixture -and $fixture.fullCustomDescriptors) { (@($fixture.fullCustomDescriptors.systemsAndMappings) -join "`n") } else { '' }
$matchingDescriptorRows = @($fixture.fullCustomDescriptors.temporaryDescriptors | Where-Object {
    $_ -match 'debugName=VFXMovecsBuffer' -and $_ -match 'target=Raw' -and $_ -match 'size=8320000' -and $_ -match 'frameCount=2'
})
$descriptorGate = $matchingDescriptorRows.Count -eq 1 -and $descriptorMappingText -match 'perCameraBuffer=True'
$fixtureGate = $null -ne $fixture -and $fixture.status -eq 'success' -and $fixture.schema -eq 'viewweave-sample-texture-full-custom-capacity-v1' -and
    $fixture.graphicsDevice -eq 'Null' -and $fixture.vfxGraphVersion -eq '17.6.0' -and
    $fixture.sourceAssetGuid -eq 'e166f36ceb1831f46bea1e17fbc67c5a' -and $fixture.sourceAssetSha256Before -eq '78D34F482963B47E6555E9218389A98A045F614F3F5B300776C78948CFE96556' -and
    $fixture.controlAssetGuid -eq '2f97a7e6edd4b5c4489e69768872259b' -and $fixture.controlAssetSha256Before -eq '55AE59AD9656256C04291B14FFAADECAF1BD622561A8E1B8BE53C66328CA6155' -and
    $fixture.sourceParticleCapacity -eq 160000 -and $fixture.controlParticleCapacity -eq 160000 -and $fixture.fullCustomParticleCapacity -eq 160000 -and
    $fixture.sourceGridResolutionX -eq 400 -and $fixture.sourceGridResolutionY -eq 400 -and $fixture.controlGridResolutionX -eq 400 -and $fixture.controlGridResolutionY -eq 400 -and
    $fixture.fullCustomGridResolutionX -eq 400 -and $fixture.fullCustomGridResolutionY -eq 400 -and
    $fixture.controlInstancingModeValue -eq 0 -and $fixture.controlInstancingCapacity -eq 64 -and
    $fixture.fullCustomInstancingModeValue -eq 1 -and $fixture.fullCustomInstancingCapacity -eq 1 -and
    $fixture.fullCustomDisabledReasonName -eq 'None' -and $fixture.fullCustomDisabledReasonValue -eq $fixture.fullCustomDisabledReasonNoneValue -and
    $fixture.controlDescriptors.compileStatus -eq 'success' -and $fixture.fullCustomDescriptors.compileStatus -eq 'success' -and
    $fixture.controlDescriptors.descriptorFieldsComplete -eq $true -and $fixture.controlDescriptors.mappingIndicesValid -eq $true -and
    $fixture.fullCustomDescriptors.descriptorFieldsComplete -eq $true -and $fixture.fullCustomDescriptors.mappingIndicesValid -eq $true -and $descriptorGate -and $normalizedFieldsGate -and
    $fixture.controlComparableStructureSha256 -eq $fixture.fullCustomComparableStructureSha256 -and
    $fixture.sourceAssetUnchanged -eq $true -and $fixture.controlAssetUnchanged -eq $true -and $fixture.openSceneFilesUnchanged -eq $true -and
    $fixture.finalSourceAndControlIntegrityGate -eq $true -and $fixture.sceneSaveCalled -eq $false -and $fixture.runtimeGraphActivation -eq $false -and $fixture.nativeMeasurement -eq $false -and
    $fixture.activeSceneCameraCountDuringProbe -eq 0 -and $fixture.activeSceneVfxCountDuringProbe -eq 0
$passed = $testGate -and $fixtureGate -and $sourceHash -eq $cloneSourceHashAfter -and $sourceHash -eq (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
$summary = [ordered]@{
    schema = 'viewweave-sample-texture-full-custom-capacity-runner-v1'
    status = if ($passed) { 'fixture-passed' } else { 'not-passed' }
    pipeline = 'URP'
    testFilter = $testFilter
    expectedTestCount = 1
    editorExitCode = if ($underlying) { $underlying.editorExitCode } else { $null }
    editorProcessId = if ($underlying) { $underlying.editorProcessId } else { $null }
    timeout = if ($underlying) { $underlying.timeout } else { $null }
    outputDrainIncomplete = if ($underlying) { $underlying.outputDrainIncomplete } else { $null }
    processInventoryResidualCount = if ($underlying) { $underlying.processInventoryResidualCount } else { $null }
    testPassGate = if ($underlying) { $underlying.testPassGate } else { $false }
    exactXmlCaseGate = [bool]$exactCaseGate
    fixtureGate = [bool]$fixtureGate
    descriptorGate = [bool]$descriptorGate
    canonicalSourceSha256 = $sourceHash
    canonicalMetaSha256 = $sourceMetaHash
    runnerSourceSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
    cloneMetaSha256 = if (Test-Path -LiteralPath $asRunMetaCopy) { (Get-FileHash -LiteralPath $asRunMetaCopy -Algorithm SHA256).Hash } else { $null }
    cloneSourceSha256 = $cloneSourceHashAfter
    fixtureStatus = if ($fixture) { $fixture.status } else { 'missing' }
    sourceAssetSha256Before = if ($fixture) { $fixture.sourceAssetSha256Before } else { $null }
    sourceAssetSha256After = if ($fixture) { $fixture.sourceAssetSha256After } else { $null }
    controlAssetGuid = if ($fixture) { $fixture.controlAssetGuid } else { $null }
    controlAssetSha256 = if ($fixture) { $fixture.controlAssetSha256Before } else { $null }
    fullCustomAssetGuid = if ($fixture) { $fixture.fullCustomAssetGuid } else { $null }
    fullCustomAssetSha256 = if ($fixture) { $fixture.fullCustomAssetSha256After } else { $null }
    capacity = if ($fixture) { $fixture.fullCustomParticleCapacity } else { $null }
    gridResolution = if ($fixture) { @($fixture.fullCustomGridResolutionX, $fixture.fullCustomGridResolutionY) } else { $null }
    instancingMode = if ($fixture) { $fixture.fullCustomInstancingMode } else { $null }
    instancingCapacity = if ($fixture) { $fixture.fullCustomInstancingCapacity } else { $null }
    disabledReason = if ($fixture) { $fixture.fullCustomDisabledReasonName } else { $null }
    controlAndFullCustomStructureMatch = if ($fixture) { $fixture.controlComparableStructureSha256 -eq $fixture.fullCustomComparableStructureSha256 } else { $false }
    descriptorCompileStatus = if ($fixture) { @($fixture.controlDescriptors.compileStatus, $fixture.fullCustomDescriptors.compileStatus) } else { $null }
    descriptorFields = if ($fixture) { @($fixture.fullCustomDescriptors.temporaryDescriptors) } else { $null }
    descriptorMappings = if ($fixture) { @($fixture.fullCustomDescriptors.systemsAndMappings) } else { $null }
    normalizedFields = if ($fixture) { @($fixture.normalizedFields) } else { $null }
    resultJson = if (Test-Path -LiteralPath $fixtureCopy) { 'fixture-result.json' } else { $null }
    asRunSource = if (Test-Path -LiteralPath $asRunSourceCopy) { 'as-run-SampleTextureFullCustomCapacityFixture.cs' } else { $null }
    cloneSourceRestored = $cloneSourceRestored
    cloneRestoreDeferredReason = $cloneRestoreDeferredReason
    copiedRunnerRecord = if (Test-Path -LiteralPath $runnerRecord) { 'runner.json' } else { $null }
    copiedXml = if (Test-Path -LiteralPath $xmlCopy) { 'results.xml' } else { $null }
    graphicsExecution = 'NoGraphics; EditMode only; no activation or native measurement.'
}
$summary | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8
Get-Content -LiteralPath $summaryPath -Raw | Write-Output
if (-not $passed) { exit 1 }
