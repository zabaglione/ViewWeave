param(
    [Parameter(Mandatory=$true)][ValidateSet('PrepareFixture','NativeProbe')][string]$Mode,
    [string]$FixtureResultJson,
    [ValidateSet(1,12)][int]$RenderedViews = 1
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path 'E:/LookingGlassSDK').Path
$localRoot = Join-Path $repo '.local/windows-urp-vfx-frustum-memory-20261013'
$project = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$baseRunner = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/Tools/Run-UnityIsolated.ps1'
$canonicalSource = Join-Path $localRoot 'Shared/CollisionAdvancedUnbatchedNativeProbeTests.cs'
$canonicalFixture = Join-Path $localRoot 'Shared/CollisionAdvancedUnbatchedFixture.cs'
$canonicalFixtureAsmdef = Join-Path $localRoot 'Shared/ViewWeave.CollisionAdvancedUnbatched.Fixture.EditorTests.asmdef'
$testDirectory = Join-Path $project 'Assets/SceneComparisonTests'
$fixtureTestDirectory = Join-Path $project 'Assets/Editor/CollisionAdvancedUnbatchedFixture'
$cloneNative = Join-Path $testDirectory 'CollisionAdvancedUnbatchedNativeProbeTests.cs'
$cloneFixture = Join-Path $fixtureTestDirectory 'CollisionAdvancedUnbatchedFixture.cs'
$cloneFixtureAsmdef = Join-Path $fixtureTestDirectory 'ViewWeave.CollisionAdvancedUnbatched.Fixture.EditorTests.asmdef'
$nativeMeta = $canonicalSource + '.meta'
$fixtureMeta = $canonicalFixture + '.meta'
$fixtureAsmdefMeta = $canonicalFixtureAsmdef + '.meta'
$cloneNativeMeta = $cloneNative + '.meta'
$cloneFixtureMeta = $cloneFixture + '.meta'
$cloneFixtureAsmdefMeta = $cloneFixtureAsmdef + '.meta'
$fixtureAssetRoot = Join-Path $project 'Assets/FrustumMemoryCollisionUnbatched'
$projectResults = Join-Path $project 'TestResults'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$runDirectory = Join-Path $localRoot "URP/Results/CollisionAdvancedUnbatched-$stamp"
$fixtureEvidenceInClone = Join-Path $projectResults "collision-advanced-fixture-$stamp.json"
$nativeEvidenceInClone = Join-Path $projectResults "collision-advanced-native-$stamp"
$testFilter = if ($Mode -eq 'PrepareFixture') {
    'ViewWeave.SceneComparison.Tests.CollisionAdvancedUnbatchedFixture.BuildsIsolatedCollisionAdvancedUnbatchedSceneAndPrefab'
} else {
    'ViewWeave.SceneComparison.Tests.CollisionAdvancedUnbatchedNativeProbeTests.VerifiesCollisionAdvancedUnbatchedNativeDrawOnNaturalAutoRender'
}
if (-not (Test-Path -LiteralPath $baseRunner) -or -not (Test-Path -LiteralPath $project) -or -not (Test-Path -LiteralPath $testDirectory)) { throw 'Required isolated URP project or test runner is missing.' }
if (-not (Test-Path -LiteralPath $canonicalSource) -or -not (Test-Path -LiteralPath $canonicalFixture) -or -not (Test-Path -LiteralPath $canonicalFixtureAsmdef) -or -not (Test-Path -LiteralPath $nativeMeta) -or -not (Test-Path -LiteralPath $fixtureMeta) -or -not (Test-Path -LiteralPath $fixtureAsmdefMeta)) { throw 'Canonical CollisionAdvanced test sources or metadata are missing.' }
if (Test-Path -LiteralPath $runDirectory) { throw 'Refusing to overwrite native probe evidence.' }
$resolvedProject = (Resolve-Path -LiteralPath $project).Path.TrimEnd('\')
$expectedProject = (Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject').TrimEnd('\')
if (-not $resolvedProject.Equals($expectedProject,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to use an unapproved Unity project.' }
function Get-ExactCloneEditorProcessCount([string]$ProjectPath) {
    $rows = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop)
    $full = [IO.Path]::GetFullPath($ProjectPath).Replace('/','\').TrimEnd('\')
    $matching = @($rows | Where-Object {
        $command = [string]$_.CommandLine
        $match = [regex]::Match($command, '(?:^|\s)-projectPath(?:\s+|=)(?:"([^"]+)"|(\S+))', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) { return $false }
        $candidate = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        try { [IO.Path]::GetFullPath($candidate).Replace('/','\').TrimEnd('\').Equals($full,[StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    return $matching.Count
}
function Test-JsonTrue($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $false }
    return ($property.Value -eq $true) -or [string]::Equals([string]$property.Value,'true',[StringComparison]::OrdinalIgnoreCase)
}
function Test-JsonFalse($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $false }
    return ($property.Value -eq $false) -or [string]::Equals([string]$property.Value,'false',[StringComparison]::OrdinalIgnoreCase)
}
function Test-TaaHistoryEvidence($Evidence, [int]$ExpectedRenderedViews) {
    $offVersions = @($Evidence.taaAccumulationVersionsAfterOff)
    $activeVersions = @($Evidence.taaAccumulationVersionsAfterActive)
    $offIds = @($Evidence.taaHistoryCameraEntityIdsAfterOff)
    $activeIds = @($Evidence.taaHistoryCameraEntityIdsAfterActive)
    if ($offVersions.Count -ne $ExpectedRenderedViews -or $activeVersions.Count -ne $ExpectedRenderedViews -or
        $offIds.Count -ne $ExpectedRenderedViews -or $activeIds.Count -ne $ExpectedRenderedViews) { return $false }
    if ($ExpectedRenderedViews -eq 12) {
        for ($index = 0; $index -lt 12; $index++) {
            if ([int]$offVersions[$index] -le 0 -or [int]$activeVersions[$index] -le [int]$offVersions[$index] -or
                [string]$offIds[$index] -eq 'null' -or [string]$offIds[$index] -ne [string]$activeIds[$index]) { return $false }
        }
        return $true
    }
    return [int]$offVersions[0] -le 0 -and [int]$activeVersions[0] -le 0
}
function Test-FixtureEvidence($Evidence) {
    if ($null -eq $Evidence -or $Evidence.schema -ne 'viewweave-collision-advanced-unbatched-fixture-v1' -or $Evidence.status -ne 'passed') { return $false }
    foreach ($name in @('sourceFilesUnchanged','noGraphicsNullDevice','editorNotPlaying','showcaseMetadataSeedApplied','showcaseMetadataMappingValidated','showcaseMetadataReferencesRestored','componentNonAllowOverridesMatch','componentAllowOverrideSaved','originalSceneVfxComponentRemoved','fixtureSceneSaved')) {
        if (-not (Test-JsonTrue $Evidence $name)) { return $false }
    }
    return $Evidence.fixtureSelectedAllowInstancing -eq 'False' -and
        $Evidence.fixtureSceneSelectedGraphComponentCount -eq '1' -and
        $Evidence.sourceGraphGuid -eq 'fdd4e278b167d3f41a9bfeabcdb3ff21' -and
        $Evidence.sourceGraphSha256After -eq 'D12815212B21A7C9F20C3DE7A635AA3800BD80DC7DC0FC6904C965F25FBBE3BD' -and
        $Evidence.sourcePrefabGuid -eq '87f78a0aa2642774ea793b42f5984c6a' -and
        $Evidence.sourcePrefabSha256After -eq 'DA7D26C4040489F13DB8276CDDCF5C0A72B5F9ABF1689452970803B8501513CD' -and
        $Evidence.sourceSceneGuid -eq 'ba07c3d71e7f23846a0a2a39ae625622' -and
        $Evidence.sourceSceneSha256After -eq 'A47FECCDA2F348A0598A8E58133ABA539C1D1F94625BCC0A069676155F2D335D' -and
        -not [string]::IsNullOrWhiteSpace([string]$Evidence.fixturePrefabGuid) -and
        -not [string]::IsNullOrWhiteSpace([string]$Evidence.fixturePrefabSha256) -and
        -not [string]::IsNullOrWhiteSpace([string]$Evidence.fixtureSceneGuid) -and
        -not [string]::IsNullOrWhiteSpace([string]$Evidence.fixtureSceneSha256)
}
function Test-NativeEvidence($Evidence, [int]$ExpectedRenderedViews) {
    if ($null -eq $Evidence -or $Evidence.schema -ne 'viewweave-collision-advanced-component-optout-native-v2' -or $Evidence.status -ne 'passed') { return $false }
    $expectedColumns = if ($ExpectedRenderedViews -eq 12) { 4 } else { 1 }
    $expectedRows = if ($ExpectedRenderedViews -eq 12) { 3 } else { 1 }
    $expectedQuiltWidth = $expectedColumns * 128
    $expectedQuiltHeight = $expectedRows * 96
    $expectedAAMode = if ($ExpectedRenderedViews -eq 12) { 'TemporalAntiAliasing' } else { 'None' }
    $expectedTaaHistoryCount = if ($ExpectedRenderedViews -eq 12) { 12 } else { 0 }
    $requiredTrue = @(
        'graphicsApiGate','pipelineGate','twoCameraGate','isolatedHistoryGate','gridResolutionGate','outputLayoutGate',
        'effectiveAAExpectedModeAfterOffGate','effectiveAAExpectedModeAfterActiveGate','taaAccumulationHistoryGateAfterOff','taaAccumulationHistoryGateAfterActive',
        'taaAccumulationHistoryAdvancedGate',
        'taaEligibleHistoryCameraGateAfterOff','taaEligibleHistoryCameraGateAfterActive','offscreenRevisionGate','activeRevisionGate',
        'activeParticlesGate','pixelDifferenceGate','pixelDifferenceEachTileGate','measurementComplete','growthStopNotReached',
        'referenceAAConfigured','referencePostProcessingConfigured','sourceAAConfigured','sourcePostProcessingConfigured','msaaDisabled',
        'renderTargetsSingleSampleAfterOffGate','renderTargetsSingleSampleAfterActiveGate',
        'descriptorGate','sourceGraphUnchanged','sourcePrefabUnchanged','selectedAssetUnchanged','sceneDiskUnchanged',
        'autoRenderErrorFree','noUnityErrorsDuringRun','cleanupPassed','historyCycleGate','rendererReadyBeforeActivation',
        'sourceCullingMaskIncludesEffect','effectMaterialsSupported','componentInstancingGate','componentActivationSettingsGate',
        'componentSettingsUnchanged','componentOptOutAppliedBeforeActivation','componentOptOutFalseAtActiveDraw',
        'componentOptOutFalseThroughCleanup','componentAllowInstancingRestored','componentSettingsUnchangedDuringRun',
        'componentOptOutNativeBatchGate','rigAutoRenderRestored','profileRestored','cameraStateRestored','selectedRendererStateRestored'
    )
    foreach ($name in $requiredTrue) { if (-not (Test-JsonTrue $Evidence $name)) { return $false } }
    return (Test-JsonFalse $Evidence 'sceneSavedDuringRun') -and
        $Evidence.mode -eq 'ComponentOptOut' -and
        $Evidence.renderedViewsRequested -eq $ExpectedRenderedViews -and
        $Evidence.outputColumns -eq $expectedColumns -and $Evidence.outputRows -eq $expectedRows -and
        $Evidence.outputQuiltWidth -eq $expectedQuiltWidth -and $Evidence.outputQuiltHeight -eq $expectedQuiltHeight -and
        $Evidence.effectiveAAExpectedMode -eq $expectedAAMode -and
        $Evidence.effectiveAAExpectedModeCountAfterOff -eq $ExpectedRenderedViews -and
        $Evidence.effectiveAAExpectedModeCountAfterActive -eq $ExpectedRenderedViews -and
        $Evidence.taaAccumulationHistoryCountAfterOff -eq $expectedTaaHistoryCount -and
        $Evidence.taaAccumulationHistoryCountAfterActive -eq $expectedTaaHistoryCount -and
        $Evidence.taaEligibleHistoryCameraCountAfterOff -eq $expectedTaaHistoryCount -and
        $Evidence.taaEligibleHistoryCameraCountAfterActive -eq $expectedTaaHistoryCount -and
        (Test-TaaHistoryEvidence $Evidence $ExpectedRenderedViews) -and
        $Evidence.historyCameraCountAfterOff -eq $ExpectedRenderedViews -and
        $Evidence.historyCameraCountAfterActive -eq $ExpectedRenderedViews -and
        $Evidence.historyCameraCountAfterCleanup -eq 0 -and
        $Evidence.activeSceneEffectCountAtCleanup -eq 0 -and
        $Evidence.selectedGraphSceneComponentCountAtStart -eq 1 -and
        -not [string]::IsNullOrWhiteSpace([string]$Evidence.fixtureResultSha256)
}
$cloneEditorCountBeforeCopy = Get-ExactCloneEditorProcessCount $project
if ($cloneEditorCountBeforeCopy -ne 0) { throw 'The approved clone Editor must be fully stopped before changing test source files.' }
if ($Mode -eq 'PrepareFixture' -and (Test-Path -LiteralPath $fixtureAssetRoot)) { throw 'Refusing to reuse the generated collision fixture assets.' }
if ($Mode -eq 'NativeProbe') {
    if ([string]::IsNullOrWhiteSpace($FixtureResultJson)) { throw 'NativeProbe requires the exact passed fixture-result JSON path.' }
    $FixtureResultJson = (Resolve-Path -LiteralPath $FixtureResultJson).Path
    $allowedTestResults = (Join-Path $project 'TestResults').TrimEnd('\') + '\'
    if (-not $FixtureResultJson.StartsWith($allowedTestResults,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture evidence must be inside the approved clone TestResults directory.' }
    $fixture = Get-Content -LiteralPath $FixtureResultJson -Raw | ConvertFrom-Json
    if (-not (Test-FixtureEvidence $fixture)) { throw 'The exact fixture evidence did not pass its required source, Null/EditMode, metadata, and serialized opt-out gates.' }
}
$sourceHash = (Get-FileHash -LiteralPath $canonicalSource -Algorithm SHA256).Hash
$runnerScriptPath = (Resolve-Path -LiteralPath $MyInvocation.MyCommand.Path).Path
$runnerScriptHash = (Get-FileHash -LiteralPath $runnerScriptPath -Algorithm SHA256).Hash
$fixtureHash = (Get-FileHash -LiteralPath $canonicalFixture -Algorithm SHA256).Hash
$fixtureAsmdefHash = (Get-FileHash -LiteralPath $canonicalFixtureAsmdef -Algorithm SHA256).Hash
$nativeMetaHash = (Get-FileHash -LiteralPath $nativeMeta -Algorithm SHA256).Hash
$fixtureMetaHash = (Get-FileHash -LiteralPath $fixtureMeta -Algorithm SHA256).Hash
$fixtureAsmdefMetaHash = (Get-FileHash -LiteralPath $fixtureAsmdefMeta -Algorithm SHA256).Hash
$existingCloneFiles = @(@($cloneNative,$cloneFixture,$cloneFixtureAsmdef,$cloneNativeMeta,$cloneFixtureMeta,$cloneFixtureAsmdefMeta) | Where-Object { Test-Path -LiteralPath $_ })
if ($existingCloneFiles.Count -gt 0) {
    foreach ($path in @($cloneNative,$cloneFixture,$cloneFixtureAsmdef,$cloneNativeMeta,$cloneFixtureMeta,$cloneFixtureAsmdefMeta)) {
        if (-not (Test-Path -LiteralPath $path)) { throw 'A partial prior test-source install exists; refusing automatic replacement.' }
    }
    if ((Get-FileHash -LiteralPath $cloneNative -Algorithm SHA256).Hash -ne $sourceHash -or
        (Get-FileHash -LiteralPath $cloneFixture -Algorithm SHA256).Hash -ne $fixtureHash -or
        (Get-FileHash -LiteralPath $cloneFixtureAsmdef -Algorithm SHA256).Hash -ne $fixtureAsmdefHash -or
        (Get-FileHash -LiteralPath $cloneNativeMeta -Algorithm SHA256).Hash -ne $nativeMetaHash -or
        (Get-FileHash -LiteralPath $cloneFixtureMeta -Algorithm SHA256).Hash -ne $fixtureMetaHash -or
        (Get-FileHash -LiteralPath $cloneFixtureAsmdefMeta -Algorithm SHA256).Hash -ne $fixtureAsmdefMetaHash) { throw 'Installed clone test sources differ from the approved canonical files.' }
} else {
    New-Item -ItemType Directory -Path $fixtureTestDirectory -Force | Out-Null
    Copy-Item -LiteralPath $canonicalSource -Destination $cloneNative
    Copy-Item -LiteralPath $canonicalFixture -Destination $cloneFixture
    Copy-Item -LiteralPath $canonicalFixtureAsmdef -Destination $cloneFixtureAsmdef
    Copy-Item -LiteralPath $nativeMeta -Destination $cloneNativeMeta
    Copy-Item -LiteralPath $fixtureMeta -Destination $cloneFixtureMeta
    Copy-Item -LiteralPath $fixtureAsmdefMeta -Destination $cloneFixtureAsmdefMeta
}
New-Item -ItemType Directory -Path $runDirectory | Out-Null
$canonicalBackup = Join-Path $runDirectory 'as-run-source'
New-Item -ItemType Directory -Path $canonicalBackup | Out-Null
Copy-Item -LiteralPath $canonicalSource -Destination (Join-Path $canonicalBackup 'CollisionAdvancedUnbatchedNativeProbeTests.cs')
Copy-Item -LiteralPath $canonicalFixture -Destination (Join-Path $canonicalBackup 'CollisionAdvancedUnbatchedFixture.cs')
Copy-Item -LiteralPath $canonicalFixtureAsmdef -Destination (Join-Path $canonicalBackup 'ViewWeave.CollisionAdvancedUnbatched.Fixture.EditorTests.asmdef')
Copy-Item -LiteralPath $nativeMeta -Destination (Join-Path $canonicalBackup 'CollisionAdvancedUnbatchedNativeProbeTests.cs.meta')
Copy-Item -LiteralPath $fixtureMeta -Destination (Join-Path $canonicalBackup 'CollisionAdvancedUnbatchedFixture.cs.meta')
Copy-Item -LiteralPath $fixtureAsmdefMeta -Destination (Join-Path $canonicalBackup 'ViewWeave.CollisionAdvancedUnbatched.Fixture.EditorTests.asmdef.meta')
$asRunRunnerPath = Join-Path $canonicalBackup 'Run-CollisionAdvancedUnbatchedNativeProbe.ps1'
Copy-Item -LiteralPath $runnerScriptPath -Destination $asRunRunnerPath
if ((Get-FileHash -LiteralPath $asRunRunnerPath -Algorithm SHA256).Hash -ne $runnerScriptHash) { throw 'The as-run PowerShell runner snapshot hash did not match its source.' }
$envFixture = 'VW_COLLISION_ADVANCED_UNBATCHED_FIXTURE_RESULT'
$envNative = 'VW_COLLISION_ADVANCED_UNBATCHED_RESULT'
$envViews = 'VW_COLLISION_ADVANCED_UNBATCHED_RENDERED_VIEWS'
$envMode = 'VW_COLLISION_ADVANCED_UNBATCHED_MODE'
$oldFixture = [Environment]::GetEnvironmentVariable($envFixture,'Process')
$oldNative = [Environment]::GetEnvironmentVariable($envNative,'Process')
$oldViews = [Environment]::GetEnvironmentVariable($envViews,'Process')
$oldMode = [Environment]::GetEnvironmentVariable($envMode,'Process')
$beforeRunnerRecords = @(Get-ChildItem -LiteralPath (Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/Results') -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
$runnerInvocation = @{
    schema = 'viewweave-collision-advanced-unbatched-native-runner-v2'
    status = 'started'
    mode = $Mode
    requestedRenderedViews = $RenderedViews
    testFilter = $testFilter
    canonicalNativeSourceSha256 = $sourceHash
    canonicalFixtureSourceSha256 = $fixtureHash
    canonicalFixtureAsmdefSha256 = $fixtureAsmdefHash
    canonicalNativeMetaSha256 = $nativeMetaHash
    canonicalFixtureMetaSha256 = $fixtureMetaHash
    runnerScriptPath = $runnerScriptPath
    runnerScriptSha256 = $runnerScriptHash
    asRunRunnerSourcePath = 'as-run-source/Run-CollisionAdvancedUnbatchedNativeProbe.ps1'
    asRunRunnerSourceSha256 = $runnerScriptHash
    fixtureEvidenceInput = if ($Mode -eq 'NativeProbe') { $FixtureResultJson } else { $null }
}
try {
    [Environment]::SetEnvironmentVariable($envViews,[string]$RenderedViews,'Process')
    if ($Mode -eq 'PrepareFixture') {
        [Environment]::SetEnvironmentVariable($envFixture,$fixtureEvidenceInClone,'Process')
        & $baseRunner -Pipeline URP -Operation RunTests -TimeoutSeconds 900 -TestPlatform EditMode -TestFilter $testFilter -ExpectedTestCount 1 -NoGraphics
    } else {
        [Environment]::SetEnvironmentVariable($envFixture,$FixtureResultJson,'Process')
        [Environment]::SetEnvironmentVariable($envNative,$nativeEvidenceInClone,'Process')
        [Environment]::SetEnvironmentVariable($envMode,'ComponentOptOut','Process')
        & $baseRunner -Pipeline URP -Operation RunTests -TimeoutSeconds 900 -TestPlatform PlayMode -TestFilter $testFilter -ExpectedTestCount 1 -GraphicsApi D3D12
    }
    $runnerInvocation.editorExitCode = $LASTEXITCODE
} catch {
    $runnerInvocation.invocationError = $_.Exception.GetType().Name
    $runnerInvocation.status = 'invocation-failed'
    throw
} finally {
    [Environment]::SetEnvironmentVariable($envFixture,$oldFixture,'Process')
    [Environment]::SetEnvironmentVariable($envNative,$oldNative,'Process')
    [Environment]::SetEnvironmentVariable($envViews,$oldViews,'Process')
    [Environment]::SetEnvironmentVariable($envMode,$oldMode,'Process')
}
$runnerDirectory = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/Results'
$afterRunnerRecords = @(Get-ChildItem -LiteralPath $runnerDirectory -Filter 'RunTests-*-runner.json' -File | ForEach-Object FullName)
$created = @($afterRunnerRecords | Where-Object { $beforeRunnerRecords -notcontains $_ })
$underlying = $null
if ($created.Count -eq 1) {
    $underlying = Get-Content -LiteralPath $created[0] -Raw | ConvertFrom-Json
    Copy-Item -LiteralPath $created[0] -Destination (Join-Path $runDirectory 'runner.json')
    if ($underlying.rawXmlPath -and (Test-Path -LiteralPath $underlying.rawXmlPath)) { Copy-Item -LiteralPath $underlying.rawXmlPath -Destination (Join-Path $runDirectory 'results.xml') }
}
if ($Mode -eq 'PrepareFixture' -and (Test-Path -LiteralPath $fixtureEvidenceInClone)) { Copy-Item -LiteralPath $fixtureEvidenceInClone -Destination (Join-Path $runDirectory 'fixture-result.json') }
if ($Mode -eq 'NativeProbe') {
    $nativeEvidenceFile = Join-Path $nativeEvidenceInClone 'native-result.json'
    if (Test-Path -LiteralPath $nativeEvidenceFile -PathType Leaf) { Copy-Item -LiteralPath $nativeEvidenceFile -Destination (Join-Path $runDirectory 'native-result.json') }
}
$runnerInvocation.status = if ($underlying -and $underlying.editorExitCode -eq 0 -and -not $underlying.timeout -and $underlying.outputDrainIncomplete -eq $false -and $underlying.processInventoryAvailableAfter -eq $true -and $underlying.processInventoryResidualCount -eq 0 -and $underlying.testPassGate) { 'runner-passed' } else { 'not-passed' }
$runnerInvocation.underlyingRunner = if ($created.Count -eq 1) { $created[0] } else { $null }
$runnerInvocation.processResidualCount = if ($underlying) { $underlying.processInventoryResidualCount } else { $null }
$runnerInvocation.outputDrainIncomplete = if ($underlying) { $underlying.outputDrainIncomplete } else { $null }
$runnerInvocation.editorExitCode = if ($underlying) { $underlying.editorExitCode } else { $null }
$runnerInvocation.fixtureResult = if (Test-Path -LiteralPath (Join-Path $runDirectory 'fixture-result.json')) { 'fixture-result.json' } else { $null }
$runnerInvocation.nativeResult = if (Test-Path -LiteralPath (Join-Path $runDirectory 'native-result.json')) { 'native-result.json' } else { $null }
$fixtureEvidenceObject = if (Test-Path -LiteralPath (Join-Path $runDirectory 'fixture-result.json')) { Get-Content -LiteralPath (Join-Path $runDirectory 'fixture-result.json') -Raw | ConvertFrom-Json } else { $null }
$nativeEvidenceObject = if (Test-Path -LiteralPath (Join-Path $runDirectory 'native-result.json') -PathType Leaf) { Get-Content -LiteralPath (Join-Path $runDirectory 'native-result.json') -Raw | ConvertFrom-Json } else { $null }
$runnerInvocation.fixtureEvidenceGate = if ($Mode -eq 'PrepareFixture') { Test-FixtureEvidence $fixtureEvidenceObject } else { $null }
$runnerInvocation.nativeEvidenceGate = if ($Mode -eq 'NativeProbe') { Test-NativeEvidence $nativeEvidenceObject $RenderedViews } else { $null }
$runnerInvocation.status = if ($runnerInvocation.status -eq 'runner-passed' -and (($Mode -eq 'PrepareFixture' -and $runnerInvocation.fixtureEvidenceGate) -or ($Mode -eq 'NativeProbe' -and $runnerInvocation.nativeEvidenceGate))) { 'runner-passed' } else { 'not-passed' }
$runnerInvocation.completedUtc = [DateTime]::UtcNow.ToString('o')
$runnerInvocation | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $runDirectory 'runner-summary.json') -Encoding ascii
Write-Output $runDirectory
