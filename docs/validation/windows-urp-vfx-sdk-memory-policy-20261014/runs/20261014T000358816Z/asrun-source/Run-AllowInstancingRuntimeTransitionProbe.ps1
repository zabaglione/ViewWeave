[CmdletBinding()]
param([int]$TimeoutSeconds = 900)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$policyRoot = 'REPO_ROOT/.local/windows-urp-vfx-sdk-memory-policy-20261014'
$cloneRoot = 'REPO_ROOT/.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$editor = 'C:/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe'
$baseRunner = 'REPO_ROOT/.local/windows-vfx-scene-comparison-20261013/Tools/Run-UnityIsolated.ps1'
$inputDirectory = 'REPO_ROOT/.local/windows-urp-vfx-frustum-memory-20261013/URP/Results/SampleTextureNative-20261013T103828702Z-Auto'
$inputNative = Join-Path $inputDirectory 'native-result.json'
$inputSummary = Join-Path $inputDirectory 'summary.json'
$assetPath = 'Assets/FrustumMemoryFixturesGrid40/Auto/SampleTexture2D.vfx'
$assetGuid = '186806741105897419284e4bc5055e91'
$assetSha = '62A767F8977E0C6125A16878F47520A59B83AF66F8F2F70B4A37BFE1913DE7BD'
$filter = 'ViewWeave.SceneComparison.Tests.AllowInstancingRuntimeTransitionProbe.MeasuresPublicAllowInstancingTransitionsWithoutReinitializingTheVfx'
$relativeTestDir = 'Assets/SceneComparisonTests/AllowInstancingRuntimeTransition'
$sourcePath = Join-Path $policyRoot 'Shared/AllowInstancingRuntimeTransitionProbe.cs'
$sourceMetaPath = $sourcePath + '.meta'
$asmdefPath = Join-Path $policyRoot 'Shared/ViewWeave.AllowInstancingRuntimeTransition.Tests.asmdef'
$asmdefMetaPath = Join-Path $policyRoot 'Shared/ViewWeave.AllowInstancingRuntimeTransition.Tests.asmdef.meta'
$runId = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$policyRun = Join-Path $policyRoot "runs/$runId"
$cloneTestDir = Join-Path $cloneRoot $relativeTestDir
$testSource = Join-Path $cloneTestDir 'AllowInstancingRuntimeTransitionProbe.cs'
$testMeta = Join-Path $cloneTestDir 'AllowInstancingRuntimeTransitionProbe.cs.meta'
$testAsmdef = Join-Path $cloneTestDir 'ViewWeave.AllowInstancingRuntimeTransition.Tests.asmdef'
$testAsmdefMeta = Join-Path $cloneTestDir 'ViewWeave.AllowInstancingRuntimeTransition.Tests.asmdef.meta'
$projectEvidenceDir = Join-Path $cloneRoot "TestResults/AllowInstancingRuntimeTransition/$runId"
$underlyingResultsRoot = Join-Path (Split-Path (Split-Path $baseRunner -Parent) -Parent) 'URP/Results'
$nativeResultPath = Join-Path $projectEvidenceDir 'native-result.json'
$xmlCopyPath = Join-Path $policyRun 'results.xml'
$runnerCopyPath = Join-Path $policyRun 'unity-runner.json'
$asRunDir = Join-Path $policyRun 'asrun-source'
$summaryPath = Join-Path $policyRun 'runner-summary.json'
$requiredNativeSchema = 'viewweave-sample-texture-native-probe-v2-grid40'
$requiredNativeCase = 'ViewWeave.SceneComparison.Tests.SampleTextureNativeProbeTests.ComparesLowCapacitySampleTextureOnNaturalAutoRender'
$caseName = 'ViewWeave.SceneComparison.Tests.AllowInstancingRuntimeTransitionProbe.MeasuresPublicAllowInstancingTransitionsWithoutReinitializingTheVfx'
$summary = [ordered]@{
    schema = 'viewweave-allow-instancing-runtime-transition-runner-v1'
    status = 'not-passed'
    runId = $runId
    testCase = $caseName
    mode = 'RuntimePublicSetterTransition'
    unityVersionExpected = '6000.6.4f1'
    graphicsApiExpected = 'Direct3D12'
    cloneProjectPath = $cloneRoot
    editorPath = $editor
    sourcePath = $sourcePath
    sourceSha256 = $null
    priorCloneSourceSha256 = $null
    priorCloneMetaSha256 = $null
    asmdefSha256 = $null
    runnerSha256 = $null
    inputNativeResultPath = $inputNative
    inputNativeResultSha256 = $null
    inputSummaryPath = $inputSummary
    inputSummarySha256 = $null
    selectedAssetPath = $assetPath
    selectedAssetGuid = $assetGuid
    selectedAssetSha256 = $assetSha
    projectEvidencePath = $projectEvidenceDir
    nativeResultPath = $nativeResultPath
    nativeResultSha256 = $null
    nativeResultStatus = $null
    behaviorClassification = $null
    exactNunitCaseGate = $false
    nativeResultGate = $false
    cloneProcessInventoryAvailableBefore = $false
    cloneProcessCountBefore = $null
    cloneProcessInventoryAvailableAfter = $false
    cloneProcessCountAfter = $null
    editorExitCode = $null
    timeout = $false
    outputDrainIncomplete = $true
    testPassGate = $false
    editorLogShared = $false
    failureType = $null
    failureStage = $null
    failureScriptLine = $null
    failureErrorId = $null
    artifactsDirectory = $policyRun
}

function Get-Sha256([string]$Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}
function Get-ExactCloneEditorRows {
    $rows = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop
    $full = [IO.Path]::GetFullPath($cloneRoot).Replace('/','\').TrimEnd('\')
    @($rows | Where-Object {
        $line = [string]$_.CommandLine
        $match = [regex]::Match($line, '(?:^|\s)-projectPath(?:\s+|=)(?:"([^"]+)"|(\S+))', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) { return $false }
        $candidate = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        try { [IO.Path]::GetFullPath($candidate).Replace('/','\').TrimEnd('\').Equals($full,[StringComparison]::OrdinalIgnoreCase) } catch { $false }
    } | ForEach-Object { [ordered]@{ processId=[int]$_.ProcessId; executablePath=[string]$_.ExecutablePath; creationTimeUtc=([datetime]$_.CreationDate).ToUniversalTime().ToString('o') } })
}
function Write-Summary {
    if (-not (Test-Path -LiteralPath $policyRun)) { New-Item -ItemType Directory -Path $policyRun | Out-Null }
    [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}

$executionStage = 'input-validation'
try {
    if ($TimeoutSeconds -lt 60 -or $TimeoutSeconds -gt 1800) { throw 'timeout-range-invalid' }
    foreach ($requiredPath in @($editor,$baseRunner,$inputNative,$inputSummary,$sourcePath,$sourceMetaPath,$asmdefPath,$asmdefMetaPath)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) { throw 'required-input-missing' }
    }
    if (Test-Path -LiteralPath $policyRun) { throw 'refusing-to-overwrite-prior-run' }
    $summary.sourceSha256 = Get-Sha256 $sourcePath
    $summary.asmdefSha256 = Get-Sha256 $asmdefPath
    $summary.runnerSha256 = Get-Sha256 $PSCommandPath
    if ((Get-Content -LiteralPath $sourcePath -Raw) -match '[^\x00-\x7F]') { throw 'source-not-ascii' }

    $native = Get-Content -LiteralPath $inputNative -Raw | ConvertFrom-Json
    $prior = Get-Content -LiteralPath $inputSummary -Raw | ConvertFrom-Json
    $summary.inputNativeResultSha256 = Get-Sha256 $inputNative
    $summary.inputSummarySha256 = Get-Sha256 $inputSummary
    $inputSha = if ([string]::IsNullOrWhiteSpace([string]$native.selectedAssetSha256)) { [string]$native.selectedAssetSha256After } else { [string]$native.selectedAssetSha256 }
    $inputXmlCase = @($prior.xmlTestCaseFullNames | Where-Object { $_ -eq $requiredNativeCase }).Count -eq 1
    $inputPass = $native.schema -eq $requiredNativeSchema -and $native.status -eq 'passed' -and $native.mode -eq 'Auto' -and
        $native.selectedAssetPath -eq $assetPath -and $native.selectedAssetGuid -eq $assetGuid -and $inputSha -eq $assetSha -and
        [int]$native.selectedParticleCapacity -eq 1600 -and [int]$native.selectedGridResolutionX -eq 40 -and [int]$native.selectedGridResolutionY -eq 40 -and
        [int]$native.changedPixelCount -gt 0 -and $prior.status -eq 'native-probe-passed' -and $prior.passGate -eq $true -and
        $prior.nunit.total -eq 1 -and $prior.nunit.passed -eq 1 -and $prior.nunit.failed -eq 0 -and $inputXmlCase
    if (-not $inputPass) { throw 'native-input-proof-mismatch' }

    $executionStage = 'clone-process-preflight'
    $before = @(Get-ExactCloneEditorRows)
    $summary.cloneProcessInventoryAvailableBefore = $true
    $summary.cloneProcessCountBefore = $before.Count
    if ($before.Count -ne 0) { throw 'clone-editor-already-running' }

    $executionStage = 'as-run-source-copy'
    New-Item -ItemType Directory -Path $asRunDir | Out-Null
    foreach ($path in @($sourcePath,$sourceMetaPath,$asmdefPath,$asmdefMetaPath,$PSCommandPath)) {
        Copy-Item -LiteralPath $path -Destination (Join-Path $asRunDir (Split-Path $path -Leaf))
    }
    $summary.asRunSourcePath = $asRunDir
    $summary.asRunSourceSha256 = $summary.sourceSha256
    $summary.asRunAsmdefSha256 = $summary.asmdefSha256
    $summary.asRunRunnerSha256 = $summary.runnerSha256
    New-Item -ItemType Directory -Path $cloneTestDir -Force | Out-Null

    $installFiles = @(
        @{ source=$sourcePath; target=$testSource },
        @{ source=$sourceMetaPath; target=$testMeta },
        @{ source=$asmdefPath; target=$testAsmdef },
        @{ source=$asmdefMetaPath; target=$testAsmdefMeta }
    )
    $cloneSourceExists = Test-Path -LiteralPath $testSource -PathType Leaf
    $cloneMetaExists = Test-Path -LiteralPath $testMeta -PathType Leaf
    if ($cloneSourceExists -ne $cloneMetaExists) { throw 'partial-clone-test-file-state' }
    $currentCloneSourceSha = if ($cloneSourceExists) { Get-Sha256 $testSource } else { $null }
    $currentCloneMetaSha = if ($cloneMetaExists) { Get-Sha256 $testMeta } else { $null }
    $canonicalSourceSha = Get-Sha256 $sourcePath
    $canonicalMetaSha = Get-Sha256 $sourceMetaPath
    $priorKnownSourceSha = '7D990D85D9CCE9D409D54DEAF21E3CF8C287D5BF21B6D94481A0F09825A6AF1D'
    $currentKnownSourceSha = '993A7FCF4755762393D5EDCA1BC2A963E7A8AD57B1650F52C11E1BF254B8C7FA'
    $priorKnownMetaSha = '3113C81771F3C92AD8478CA56113B8CAE32F13E7FD511AC77EE5AE8FE83772D4'
    $priorKnownAsmdefMetaSha = 'B232E40A93EB0D46CD6C02A8B6A1B4C1349335D516BCC02B898025A7C7D01685'
    $currentCloneHashes = @{}
    foreach ($item in $installFiles) {
        $targetFile = [string]$item.target
        $currentCloneHashes[$targetFile] = if (Test-Path -LiteralPath $targetFile -PathType Leaf) { Get-Sha256 $targetFile } else { $null }
    }
    $cloneFilesMatchCanonical = $true
    $cloneFilesAbsent = $true
    foreach ($item in $installFiles) {
        $targetHash = $currentCloneHashes[[string]$item.target]
        if ($null -ne $targetHash) { $cloneFilesAbsent = $false }
        if ($targetHash -ne (Get-Sha256 ([string]$item.source))) { $cloneFilesMatchCanonical = $false }
    }
    $knownStaleCloneState =
        (($currentCloneHashes[$testSource] -eq $priorKnownSourceSha -or $currentCloneHashes[$testSource] -eq $currentKnownSourceSha) -and
         $currentCloneHashes[$testMeta] -eq $priorKnownMetaSha -and
         $currentCloneHashes[$testAsmdef] -eq 'B0874F194DC76944CCB06C0B2DE10070DBC519EBC7B6C1D3B56A6AF39AD79655' -and
         $currentCloneHashes[$testAsmdefMeta] -eq $priorKnownAsmdefMetaSha)
    $cloneUpdateAuthorized = $false
    $authorizedPriorCloneHashes = @{}
    if (-not $cloneFilesMatchCanonical) {
        if (-not $cloneFilesAbsent) {
            if (-not $knownStaleCloneState) { throw 'clone-test-file-conflict' }
            $retiredDir = Join-Path $policyRun 'retired-clone-source'
            New-Item -ItemType Directory -Path $retiredDir | Out-Null
            foreach ($item in $installFiles) {
                $targetFile = [string]$item.target
                $sourceName = Split-Path ([string]$item.source) -Leaf
                Copy-Item -LiteralPath $targetFile -Destination (Join-Path $retiredDir $sourceName)
                $authorizedPriorCloneHashes[$targetFile] = $currentCloneHashes[$targetFile]
            }
            $summary.priorCloneSourceSha256 = $currentCloneHashes[$testSource]
            $summary.priorCloneMetaSha256 = $currentCloneHashes[$testMeta]
            $summary.priorCloneAsmdefSha256 = $currentCloneHashes[$testAsmdef]
            $summary.priorCloneAsmdefMetaSha256 = $currentCloneHashes[$testAsmdefMeta]
            $cloneUpdateAuthorized = $true
        }
    }
    $executionStage = 'clone-test-install'
    foreach ($item in $installFiles) {
        $sourceFile = [string]$item.source
        $targetFile = [string]$item.target
        if (Test-Path -LiteralPath $targetFile) {
            if ((Get-Sha256 $sourceFile) -ne (Get-Sha256 $targetFile)) {
                if (-not $cloneUpdateAuthorized -or -not $authorizedPriorCloneHashes.ContainsKey($targetFile) -or (Get-Sha256 $targetFile) -ne $authorizedPriorCloneHashes[$targetFile]) { throw 'clone-test-file-conflict' }
                Copy-Item -LiteralPath $sourceFile -Destination $targetFile -Force
            }
        } else { Copy-Item -LiteralPath $sourceFile -Destination $targetFile }
    }
    $env:VW_ALLOW_INSTANCING_INPUT_NATIVE_RESULT = $inputNative
    $env:VW_ALLOW_INSTANCING_TRANSITION_RESULT = $nativeResultPath
    $env:VW_ALLOW_INSTANCING_TRANSITION_MODE = 'RuntimePublicSetterTransition'

    $executionStage = 'underlying-runner-invocation'
    $captured = @(& { Set-StrictMode -Off; & $baseRunner -Pipeline URP -Operation RunTests -TimeoutSeconds $TimeoutSeconds -TestPlatform PlayMode -TestFilter $filter -ExpectedTestCount 1 -GraphicsApi D3D12 })
    $baseText = [string]::Join([Environment]::NewLine, [string[]]$captured)
    $base = $null
    $executionStage = 'underlying-runner-parse'
    try { $base = $baseText | ConvertFrom-Json -ErrorAction Stop } catch { throw 'underlying-runner-summary-unavailable' }
    $executionStage = 'underlying-runner-summary-consume'
    $summary.editorExitCode = $base.editorExitCode
    $summary.timeout = [bool]$base.timeout
    $summary.outputDrainIncomplete = [bool]$base.outputDrainIncomplete
    $summary.testPassGate = [bool]$base.testPassGate
    $summary.cloneProcessInventoryAvailableAfter = [bool]$base.processInventoryAvailableAfter
    $summary.cloneProcessCountAfter = $base.processInventoryResidualCount
    $summary.underlyingRunnerStatus = [string]$base.status
    $summary.rawXmlSha256 = [string]$base.rawXmlSha256
    $summary.rawXmlBytes = $base.rawXmlBytes
    if ($base.rawXmlPath -and (Test-Path -LiteralPath $base.rawXmlPath)) { Copy-Item -LiteralPath $base.rawXmlPath -Destination $xmlCopyPath }

    $runnerCandidates = @(Get-ChildItem -LiteralPath $underlyingResultsRoot -Filter 'RunTests-*-runner.json' -File -ErrorAction SilentlyContinue)
    $underlyingRecord = $null
    foreach ($candidate in $runnerCandidates) {
        try {
            $candidateRecord = Get-Content -LiteralPath $candidate.FullName -Raw | ConvertFrom-Json
            if ($candidateRecord.editorProcessId -eq $base.editorProcessId -and $candidateRecord.rawXmlSha256 -eq $base.rawXmlSha256) { $underlyingRecord = $candidate; break }
        } catch { }
    }
    if ($underlyingRecord) { Copy-Item -LiteralPath $underlyingRecord.FullName -Destination $runnerCopyPath }

    $caseGate = $false
    if ($base.rawXmlPath -and (Test-Path -LiteralPath $base.rawXmlPath)) {
        try {
            [xml]$xml = [IO.File]::ReadAllText($base.rawXmlPath)
            $cases = @($xml.SelectNodes('//test-case'))
            $caseGate = $cases.Count -eq 1 -and $cases[0].fullname -eq $caseName -and $cases[0].result -eq 'Passed' -and
                $base.nunit.total -eq 1 -and $base.nunit.passed -eq 1 -and $base.nunit.failed -eq 0 -and $base.nunit.skipped -eq 0 -and $base.nunit.inconclusive -eq 0
        } catch { $caseGate = $false }
    }
    $summary.exactNunitCaseGate = $caseGate -and $base.testPassGate -eq $true -and $base.editorExitCode -eq 0 -and
        $base.timeout -eq $false -and $base.outputDrainIncomplete -eq $false -and $base.processInventoryAvailableAfter -eq $true -and $base.processInventoryResidualCount -eq 0

    $executionStage = 'native-result-validation'
    $evidence = $null
    if (Test-Path -LiteralPath $nativeResultPath -PathType Leaf) {
        $summary.nativeResultSha256 = Get-Sha256 $nativeResultPath
        $evidence = Get-Content -LiteralPath $nativeResultPath -Raw | ConvertFrom-Json
        $summary.nativeResultStatus = [string]$evidence.status
        $summary.behaviorClassification = [string]$evidence.classification
        $requiredGates = @('runtimePlatformGate','graphicsApiGate','pipelineGate','inputProofGate','selectedAssetGate','freshSceneGate','singletonVfxGate',
            'rendererSetupGate','naturalAutoRenderGate','warmupRevisionGate','vfxOnDrawGate','pauseGate','setterFalseGate','setterTrueGate',
            'pauseRetentionGate','unpausedContinuationGate','memoryGuardNotReached','memoryCaptureComplete','noUnityErrors','noSceneSave',
            'sourceAssetUnchanged','cleanupPassed','historyReleased','quiltTextureReleased')
        $gateChecks = foreach ($gate in $requiredGates) { $null -ne $evidence.$gate -and [bool]$evidence.$gate }
        $samplesValid = @($evidence.samples).Count -gt 0 -and @($evidence.samples | Where-Object { $_.batchInfoFailure -ne '' -or $_.allowInstancing -eq 'missing' }).Count -eq 0
        $summary.nativeResultGate = $evidence.schema -eq 'viewweave-allow-instancing-runtime-transition-v1' -and $evidence.status -eq 'passed' -and
            $evidence.classification -ne 'inconclusive' -and $gateChecks -notcontains $false -and $samplesValid -and
            $evidence.inputNativeResultSha256 -eq $summary.inputNativeResultSha256 -and
            $evidence.selectedAssetGuid -eq $assetGuid -and $evidence.selectedAssetSha256Before -eq $assetSha -and $evidence.selectedAssetSha256After -eq $assetSha
    }
    $summary.naturalExitAndEvidenceGate = $summary.exactNunitCaseGate -and $summary.nativeResultGate -and $null -ne $underlyingRecord
    $summary.status = if ($summary.naturalExitAndEvidenceGate) { 'passed' } else { 'not-passed' }
    if ($summary.status -ne 'passed' -and $summary.failureType -eq $null) { $summary.failureType = 'result-gate-not-met' }
}
catch {
    $summary.failureType = $_.Exception.GetType().Name
    $summary.failureStage = $executionStage
    $summary.failureScriptLine = $_.InvocationInfo.ScriptLineNumber
    $summary.failureErrorId = [string]$_.FullyQualifiedErrorId
}
finally {
    try {
        $after = @(Get-ExactCloneEditorRows)
        $summary.cloneProcessInventoryAvailableAfter = $true
        $summary.cloneProcessCountAfter = $after.Count
    } catch {
        $summary.cloneProcessInventoryAvailableAfter = $false
        $summary.cloneProcessCountAfter = $null
    }
    if ($summary.cloneProcessCountAfter -ne 0) { $summary.status = 'not-passed' }
    if (Test-Path -LiteralPath $nativeResultPath -PathType Leaf) {
        if ($null -eq $summary.nativeResultSha256) { $summary.nativeResultSha256 = Get-Sha256 $nativeResultPath }
        try { $evidence = Get-Content -LiteralPath $nativeResultPath -Raw | ConvertFrom-Json; $summary.nativeResultStatus = [string]$evidence.status; $summary.behaviorClassification = [string]$evidence.classification } catch { }
    }
    Write-Summary
}
Get-Content -LiteralPath $summaryPath -Raw | Write-Output
