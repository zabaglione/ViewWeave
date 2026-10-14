param(
    [Parameter(Mandatory=$true)][ValidateSet('URP','HDRP')][string]$Pipeline,
    [Parameter(Mandatory=$true)][ValidateSet('InstallPackages','Preflight','SelectedPrepare','DescriptorInventory','RunTests')][string]$Operation,
    [int]$TimeoutSeconds = 900,
    [string]$TestPlatform = 'PlayMode',
    [string]$TestFilter = '',
    [int]$ExpectedTestCount = 0,
    [ValidateSet('D3D11','D3D12')][string]$GraphicsApi,
    [switch]$NoGraphics
)
$ErrorActionPreference = 'Stop'
$base = 'E:/LookingGlassSDK/.local/windows-vfx-scene-comparison-20261013'
$editor = 'C:/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe'
$project = Join-Path $base "$Pipeline/UnityProject"
$results = Join-Path $base "$Pipeline/Results"
New-Item -ItemType Directory -Path $results -Force | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$log = Join-Path $results "$Operation-$stamp-editor.log"
$record = Join-Path $results "$Operation-$stamp-runner.json"
$preflight = if ($Operation -eq 'Preflight') { Join-Path $results "scene-preflight-$stamp.json" } else { $null }
$selectedPreparation = if ($Operation -eq 'SelectedPrepare') { Join-Path $results "selected-pilot-preparation-$stamp.json" } else { $null }
$descriptorInventory = if ($Operation -eq 'DescriptorInventory') { Join-Path $results "vfx-descriptor-inventory-$stamp.json" } else { $null }
$descriptorInventorySource = if ($Operation -eq 'DescriptorInventory') { Join-Path $project 'Assets/Editor/VfxDescriptorInventory.cs' } else { $null }
$descriptorInventorySourceHash = if ($descriptorInventorySource -and (Test-Path -LiteralPath $descriptorInventorySource)) { (Get-FileHash -Algorithm SHA256 -LiteralPath $descriptorInventorySource).Hash } else { $null }
$descriptorPreflightSnapshot = if ($Operation -eq 'DescriptorInventory' -and $Pipeline -eq 'URP') { Join-Path $results 'scene-preflight-20261012T223138298Z.json' } elseif ($Operation -eq 'DescriptorInventory') { Join-Path $results 'scene-preflight-20261012T231937635Z.json' } else { $null }
$packageResolution = if ($Operation -eq 'InstallPackages') { Join-Path $project "TestResults/package-resolution-$stamp.json" } else { $null }
$xmlPath = if ($Operation -eq 'RunTests') { Join-Path $results "tests-$stamp.xml" } else { $null }
$scene = if ($Pipeline -eq 'URP') { 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/URP_VFX_Learning_Templates.unity' } else { 'Assets/Samples/Visual Effect Graph/17.6.0/Learning Templates/Scenes/HDRP_VFX_Learning_Templates.unity' }
$preparedScene = if ($Pipeline -eq 'URP') { 'Assets/SceneComparison/URP_SelectedPilot.unity' } else { 'Assets/SceneComparison/HDRP_SelectedPilot.unity' }
if (-not (Test-Path -LiteralPath $editor)) { throw 'Configured Unity Editor executable is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $project 'Packages/manifest.json'))) { throw 'Isolated project manifest is missing.' }
if ($Operation -eq 'RunTests' -and ([string]::IsNullOrWhiteSpace($TestFilter) -or $ExpectedTestCount -lt 1)) { throw 'RunTests requires a filter and a positive expected test count.' }
if ($Operation -ne 'RunTests' -and $GraphicsApi) { throw 'GraphicsApi is only valid for RunTests.' }
if ($Operation -eq 'InstallPackages' -or $Operation -eq 'Preflight' -or $Operation -eq 'SelectedPrepare' -or $Operation -eq 'DescriptorInventory') { $NoGraphics = $true }
if ($NoGraphics -and $GraphicsApi) { throw 'NoGraphics and GraphicsApi cannot be combined.' }
if (Test-Path -LiteralPath $log) { throw 'Refusing to overwrite existing log.' }
if (Test-Path -LiteralPath $record) { throw 'Refusing to overwrite existing run record.' }
if ($preflight -and (Test-Path -LiteralPath $preflight)) { throw 'Refusing to overwrite existing preflight result.' }
if ($selectedPreparation -and (Test-Path -LiteralPath $selectedPreparation)) { throw 'Refusing to overwrite existing selected preparation result.' }
if ($descriptorInventory -and (Test-Path -LiteralPath $descriptorInventory)) { throw 'Refusing to overwrite existing descriptor inventory result.' }
if ($Operation -eq 'DescriptorInventory' -and -not (Test-Path -LiteralPath $descriptorPreflightSnapshot)) { throw 'Prior scene preflight snapshot missing.' }
if ($packageResolution -and (Test-Path -LiteralPath $packageResolution)) { throw 'Refusing to overwrite existing package resolution result.' }
if ($xmlPath -and (Test-Path -LiteralPath $xmlPath)) { throw 'Refusing to overwrite existing test XML.' }
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
$startRows = @()
try { $startRows = Get-ExactProjectProcessRows $project }
catch {
    $fail = [ordered]@{ status='preflight-process-inventory-failed'; pipeline=$Pipeline; operation=$Operation; projectPath=$project; errorType=$_.Exception.GetType().Name; startedUtc=$null; endedUtc=[DateTime]::UtcNow.ToString('o'); editorProcessId=$null; editorExitCode=$null; timeout=$false; processInventoryAvailable=$false; matchingBefore=@(); matchingAfter=@(); editorLogPath=$log; editorLogShared=$false; resultJsonPath=$preflight; packageResolutionPath=$packageResolution; rawXmlPath=$xmlPath }
    [IO.File]::WriteAllText($record,($fail | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false)); throw 'Cannot verify isolated-project Unity process inventory; no Editor was started.'
}
if ($startRows.Count -gt 0) { throw 'An Editor already targets this exact isolated project; no new Editor was started.' }
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $editor
$psi.WorkingDirectory = $project
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.RedirectStandardInput = $false
foreach ($arg in @('-batchmode','-projectPath',$project,'-logFile',$log)) { [void]$psi.ArgumentList.Add($arg) }
if ($NoGraphics) { [void]$psi.ArgumentList.Add('-nographics') }
if ($Operation -eq 'InstallPackages') {
    [void]$psi.ArgumentList.Add('-executeMethod'); [void]$psi.ArgumentList.Add('ViewWeave.SceneComparison.InstallScenePackages.Run')
    $psi.Environment['VW_SCENE_PIPELINE'] = $Pipeline
    $psi.Environment['VW_PACKAGE_RESOLUTION_RESULT'] = $packageResolution
}
elseif ($Operation -eq 'Preflight') {
    [void]$psi.ArgumentList.Add('-executeMethod'); [void]$psi.ArgumentList.Add('ViewWeave.SceneComparison.SceneIsolationPreflight.Run')
    $psi.Environment['VW_SCENE_PREFLIGHT_RESULT'] = $preflight
    $psi.Environment['VW_SCENE_PREFLIGHT_SCENE'] = $scene
    $psi.Environment['VW_SCENE_PREFLIGHT_NOGRAPHICS'] = '1'
    $psi.Environment['VW_SCENE_PREFLIGHT_EXPECTED_PIPELINE'] = $Pipeline
}
elseif ($Operation -eq 'SelectedPrepare') {
    [void]$psi.ArgumentList.Add('-executeMethod'); [void]$psi.ArgumentList.Add('ViewWeave.SceneComparison.SelectedPilotPreparation.Run')
    $psi.Environment['VW_SELECTED_PILOT_SOURCE_SCENE'] = $scene
    $psi.Environment['VW_SELECTED_PILOT_PREPARED_SCENE'] = $preparedScene
    $psi.Environment['VW_SELECTED_PILOT_EXPECTED_PIPELINE'] = $Pipeline
    $psi.Environment['VW_SELECTED_PILOT_RESULT'] = $selectedPreparation
}
elseif ($Operation -eq 'DescriptorInventory') {
    [void]$psi.ArgumentList.Add('-executeMethod'); [void]$psi.ArgumentList.Add('ViewWeave.SceneComparison.VfxDescriptorInventory.Run')
    $psi.Environment['VW_DESCRIPTOR_SCENE'] = $scene
    $psi.Environment['VW_DESCRIPTOR_EXPECTED_PIPELINE'] = $Pipeline
    $psi.Environment['VW_DESCRIPTOR_NOGRAPHICS'] = '1'
    $psi.Environment['VW_DESCRIPTOR_RESULT'] = $descriptorInventory
    $psi.Environment['VW_DESCRIPTOR_PREFLIGHT_SNAPSHOT'] = $descriptorPreflightSnapshot
}
else {
    [void]$psi.ArgumentList.Add('-runTests')
    [void]$psi.ArgumentList.Add('-testPlatform'); [void]$psi.ArgumentList.Add($TestPlatform)
    [void]$psi.ArgumentList.Add('-testFilter'); [void]$psi.ArgumentList.Add($TestFilter)
    [void]$psi.ArgumentList.Add('-testResults'); [void]$psi.ArgumentList.Add($xmlPath)
    if ($GraphicsApi) { [void]$psi.ArgumentList.Add("-force-$($GraphicsApi.ToLowerInvariant())") }
}
$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi
$stdoutDrain = $null
$stderrDrain = $null
$started = [DateTime]::UtcNow
$startedOk = $false
$timedOut = $false
$exitCode = $null
$processId = $null
$startUtc = $null
$startErrorType = $null
try {
    $startedOk = $process.Start()
    if (-not $startedOk) { throw 'Editor process start returned false.' }
    $processId = $process.Id
    $startUtc = $process.StartTime.ToUniversalTime().ToString('o')
    $stdoutDrain = $process.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
    $stderrDrain = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
    $completed = $process.WaitForExit([Math]::Max(1,$TimeoutSeconds) * 1000)
    if ($completed) { $exitCode = $process.ExitCode } else { $timedOut = $true }
} catch { $startErrorType = $_.Exception.GetType().Name }
finally {
    $ended = [DateTime]::UtcNow
    $outputDrainIncomplete = $false
    if ($startedOk -and $process.HasExited) {
        try { $outputDrainIncomplete = -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutDrain,$stderrDrain),10000) } catch { $outputDrainIncomplete = $true }
    } elseif ($startedOk) { $outputDrainIncomplete = $true }
    $afterRows = @()
    $inventoryAvailable = $true
    try { $afterRows = Get-ExactProjectProcessRows $project } catch { $inventoryAvailable = $false }
    $stillRunningOwned = $false
    if ($startedOk) { try { $stillRunningOwned = -not $process.HasExited } catch { $stillRunningOwned = $true } }
    $xmlBytes = $null; $xmlHash = $null; $nunit = $null; $xmlParseError = $null
    if ($xmlPath -and (Test-Path -LiteralPath $xmlPath)) {
        $xmlFile=Get-Item -LiteralPath $xmlPath; $xmlBytes=$xmlFile.Length; $xmlHash=(Get-FileHash -Algorithm SHA256 -LiteralPath $xmlPath).Hash
        try {
            [xml]$xmlDoc = [IO.File]::ReadAllText($xmlPath)
            $testRun = $xmlDoc.SelectSingleNode('//test-run')
            if ($testRun) { $nunit=[ordered]@{total=[int]$testRun.total; passed=[int]$testRun.passed; failed=[int]$testRun.failed; skipped=[int]$testRun.skipped; inconclusive=[int]$testRun.inconclusive; result=$testRun.result} }
        } catch { $xmlParseError=$_.Exception.GetType().Name }
    }
    $preflightData = $null; $packageData = $null; $inventoryData = $null; $resultParseError = $null
    if ($preflight -and (Test-Path -LiteralPath $preflight)) { try { $preflightData = Get-Content -LiteralPath $preflight -Raw | ConvertFrom-Json } catch { $resultParseError = $_.Exception.GetType().Name } }
    $selectedData = $null
    if ($selectedPreparation -and (Test-Path -LiteralPath $selectedPreparation)) { try { $selectedData = Get-Content -LiteralPath $selectedPreparation -Raw | ConvertFrom-Json } catch { $resultParseError = $_.Exception.GetType().Name } }
    if ($descriptorInventory -and (Test-Path -LiteralPath $descriptorInventory)) { try { $inventoryData = Get-Content -LiteralPath $descriptorInventory -Raw | ConvertFrom-Json } catch { $resultParseError = $_.Exception.GetType().Name } }
    $selectedSourceHash = $null
    if ($Operation -eq 'SelectedPrepare') { $selectedSourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $project 'Assets/Editor/SelectedPilotPreparation.cs')).Hash }
    $descriptorInventorySourceHashAfter = if ($descriptorInventorySource -and (Test-Path -LiteralPath $descriptorInventorySource)) { (Get-FileHash -Algorithm SHA256 -LiteralPath $descriptorInventorySource).Hash } else { $null }
    if ($packageResolution -and (Test-Path -LiteralPath $packageResolution)) { try { $packageData = Get-Content -LiteralPath $packageResolution -Raw | ConvertFrom-Json } catch { $resultParseError = $_.Exception.GetType().Name } }
    $preflightPassGate = $false
    if ($Operation -eq 'Preflight' -and $preflightData -and $preflightData.effects -and $null -ne $preflightData.editorErrorCount) {
        $effectsValid = @($preflightData.effects | Where-Object { $_.graphInventoryStatus -ne 'success' -or @($_.graphOutputs).Count -eq 0 -or @($_.graphOutputs | Where-Object { $_.capacity -eq 'unavailable' }).Count -gt 0 }).Count -eq 0
        $expectedName = if ($Pipeline -eq 'URP') { 'UniversalRenderPipelineAsset' } else { 'HDRenderPipelineAsset' }
        $preflightPassGate = $preflightData.status -eq 'success' -and $preflightData.pipeline -like "*$expectedName*" -and $preflightData.missingScriptComponentCount -eq 0 -and $preflightData.visualEffectComponentCount -gt 0 -and $preflightData.editorErrorCount -eq 0 -and $effectsValid -and $preflightData.sceneSaved -and $inventoryAvailable -and $afterRows.Count -eq 0 -and -not $stillRunningOwned -and -not $timedOut -and -not $outputDrainIncomplete -and $exitCode -eq 0
    }
    $packageResolutionPassGate = $false
    if ($Operation -eq 'InstallPackages' -and $packageData -and $packageData.status -like 'success*' -and $packageData.pipeline -eq $Pipeline) {
        $requiredPackages = if ($Pipeline -eq 'URP') { @('com.unity.ugui','com.unity.inputsystem','com.unity.render-pipelines.universal','com.unity.visualeffectgraph') } else { @('com.unity.ugui','com.unity.inputsystem','com.unity.render-pipelines.high-definition','com.unity.visualeffectgraph','com.zabaglione.lookingglass.viewweave.hdrp') }
        $packageNames = @($packageData.packages | ForEach-Object { $_.name })
        $packageResolutionPassGate = @($requiredPackages | Where-Object { $packageNames -notcontains $_ }).Count -eq 0 -and $inventoryAvailable -and $afterRows.Count -eq 0 -and -not $stillRunningOwned -and -not $timedOut -and -not $outputDrainIncomplete -and $exitCode -eq 0
    }
    $selectedPreparationPassGate = $false
    if ($Operation -eq 'SelectedPrepare' -and $selectedData) {
        $expectedAssetType = if ($Pipeline -eq 'URP') { 'UniversalRenderPipelineAsset' } else { 'HDRenderPipelineAsset' }
        $selectedPipelineValid = $selectedData.expectedPipeline -eq $Pipeline -and $selectedData.graphicsPipeline -like "*$expectedAssetType*" -and $selectedData.vfxBinderType -match $Pipeline
        $cameraValid = if ($Pipeline -eq 'URP') { $selectedData.referenceCameraEnabled -eq $true } else { $selectedData.referenceCameraPath -eq '' -and $selectedData.sampleDriverCameraEnabled -eq $true }
        $selectedPreparationPassGate = $selectedData.selectedPilotSanitizationGate -eq $true -and $selectedData.wholeSceneReferenceGate -eq $false -and $selectedData.noGraphicsDevice -eq $true -and $selectedPipelineValid -and $cameraValid -and $selectedData.selectedEffectMatches -eq 1 -and $selectedData.selectedAssetGuid -eq 'e075786f47306614894aed1c188c9ce3' -and $selectedData.selectedCapacity -eq '16' -and $selectedData.missingScriptCount -eq 0 -and $selectedData.activeEffectObjectCountAfter -eq 0 -and $selectedData.sceneSaved -eq $true -and $inventoryAvailable -and $afterRows.Count -eq 0 -and -not $stillRunningOwned -and -not $timedOut -and -not $outputDrainIncomplete -and $exitCode -eq 0
    }
    $descriptorInventoryPassGate = $false
    if ($Operation -eq 'DescriptorInventory' -and $inventoryData -and $inventoryData.graphs) {
        $graphs = @($inventoryData.graphs)
        $allGraphRecordsValid = $graphs.Count -eq 32 -and @($graphs | Where-Object { $_.compileStatus -ne 'success' -or $_.descriptorFieldsComplete -ne $true -or $_.mappingIndicesValid -ne $true -or $_.sourceSha256Before -ne $_.sourceSha256After }).Count -eq 0
        $expectedPipelineName = if ($Pipeline -eq 'URP') { 'UniversalRenderPipelineAsset' } else { 'HDRenderPipelineAsset' }
        $descriptorInventoryPassGate = $inventoryData.status -eq 'success' -and $inventoryData.expectedPipeline -eq $Pipeline -and $inventoryData.graphicsPipeline -like "*$expectedPipelineName*" -and
            $inventoryData.vfxBinderType -match $Pipeline -and $inventoryData.graphicsDevice -eq 'Null' -and $inventoryData.noGraphics -eq $true -and $inventoryData.isolationGate -eq $true -and
            $inventoryData.allSampleScriptsStopped -eq $true -and $inventoryData.missingScriptComponentCount -eq 0 -and $inventoryData.knownUnresolvedEyesPrefabGuidVerified -eq $true -and $inventoryData.knownUnresolvedEyesWasInactive -eq $true -and
            $inventoryData.priorPreflightEyesEntryFound -eq $true -and $inventoryData.priorPreflightEyesWasInactive -eq $true -and $inventoryData.priorPreflightSnapshotSha256 -match '^[A-F0-9]{64}$' -and
            $inventoryData.allSceneCamerasDisabled -eq $true -and $inventoryData.wholeSceneReferenceGate -eq $false -and $inventoryData.effectComponentCount -eq 33 -and $inventoryData.unresolvedEffectAssetCount -eq 1 -and
            $inventoryData.uniqueGraphCount -eq 32 -and $allGraphRecordsValid -and $inventoryData.sceneSaved -eq $false -and $inventoryAvailable -and $afterRows.Count -eq 0 -and
            -not $stillRunningOwned -and -not $timedOut -and -not $outputDrainIncomplete -and $exitCode -eq 0
    }
    $status = if (-not $startedOk) { 'start-failed' } elseif ($timedOut) { 'timeout-process-left-running-or-unknown' } elseif ($exitCode -eq 0) { 'process-exit-zero' } elseif ($null -ne $exitCode) { 'process-nonzero-exit' } else { 'process-result-unknown' }
    $testPassGate = $false
    if ($Operation -eq 'RunTests' -and $null -ne $nunit -and $inventoryAvailable -and $afterRows.Count -eq 0 -and -not $stillRunningOwned -and -not $timedOut -and -not $outputDrainIncomplete -and $exitCode -eq 0) {
        $testPassGate = $nunit.total -eq $ExpectedTestCount -and $nunit.passed -eq $nunit.total -and $nunit.failed -eq 0 -and $nunit.skipped -eq 0 -and $nunit.inconclusive -eq 0
    }
    $summary = [ordered]@{
        status=$status; pipeline=$Pipeline; operation=$Operation; projectPath=$project; unityExecutable=$editor
        noGraphics=[bool]$NoGraphics; standardOutputRedirected=$true; standardErrorRedirected=$true; asyncOutputCallbacksUsed=$false; outputDrainIncomplete=$outputDrainIncomplete
        startedUtc=if($startUtc){$startUtc}else{$started.ToString('o')}; endedUtc=$ended.ToString('o'); editorProcessId=$processId
        editorExitCode=$exitCode; timeout=$timedOut; timeoutSeconds=$TimeoutSeconds; processObjectStillRunning=$stillRunningOwned
        processInventoryAvailableBefore=$true; matchingBefore=$startRows; processInventoryAvailableAfter=$inventoryAvailable; matchingAfter=$afterRows
        processInventoryResidualCount=if($inventoryAvailable){$afterRows.Count}else{$null}; resultJsonPath=$preflight
        resultJsonExists=if($preflight){Test-Path -LiteralPath $preflight}else{$false}; preflightStatus=if($preflightData){$preflightData.status}else{$null}
        preflightPipeline=if($preflightData){$preflightData.pipeline}else{$null}; preflightMissingScriptCount=if($preflightData){$preflightData.missingScriptComponentCount}else{$null}
        preflightVfxCount=if($preflightData){$preflightData.visualEffectComponentCount}else{$null}; preflightEditorErrorCategories=if($preflightData){$preflightData.editorErrorCategories}else{@()}
        preflightPassGate=$preflightPassGate; packageResolutionPath=$packageResolution; packageResolutionStatus=if($packageData){$packageData.status}else{$null}; packageResolutionPassGate=$packageResolutionPassGate; resolvedPackages=if($packageData){$packageData.packages}else{@()}; operationResultParseErrorType=$resultParseError
        selectedPreparationPath=$selectedPreparation; selectedPreparationStatus=if($selectedData){$selectedData.status}else{$null}; selectedPreparationPassGate=$selectedPreparationPassGate; selectedPreparationSourceSha256=$selectedSourceHash; wholeSceneReferenceGate=if($selectedData){$selectedData.wholeSceneReferenceGate}else{$null}; selectedPreparationErrors=if($selectedData){$selectedData.errors}else{@()}
        descriptorInventoryPath=$descriptorInventory; descriptorInventoryStatus=if($inventoryData){$inventoryData.status}else{$null}; descriptorInventoryGraphCount=if($inventoryData){$inventoryData.uniqueGraphCount}else{$null}; descriptorInventoryWholeSceneReferenceGate=if($inventoryData){$inventoryData.wholeSceneReferenceGate}else{$null}; descriptorInventoryPassGate=$descriptorInventoryPassGate; descriptorInventoryPriorPreflightSnapshotPath=$descriptorPreflightSnapshot; descriptorInventoryPriorPreflightSnapshotSha256=if($inventoryData){$inventoryData.priorPreflightSnapshotSha256}else{$null}
        descriptorInventorySourcePath=$descriptorInventorySource; descriptorInventorySourceSha256Before=$descriptorInventorySourceHash; descriptorInventorySourceSha256After=$descriptorInventorySourceHashAfter; descriptorInventorySourceUnchanged=($descriptorInventorySourceHash -ne $null -and $descriptorInventorySourceHash -eq $descriptorInventorySourceHashAfter)
        testPlatform=if($Operation -eq 'RunTests'){$TestPlatform}else{$null}
        testFilter=if($Operation -eq 'RunTests'){$TestFilter}else{$null}; expectedTestCount=if($Operation -eq 'RunTests'){$ExpectedTestCount}else{$null}; rawXmlPath=$xmlPath; rawXmlBytes=$xmlBytes; rawXmlSha256=$xmlHash; nunit=$nunit; xmlParseErrorType=$xmlParseError
        testPassGate=$testPassGate; editorLogPath=$log; editorLogShared=$false; startupExceptionType=$startErrorType
    }
    [IO.File]::WriteAllText($record,($summary|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $process.Dispose()
}
Get-Content -LiteralPath $record -Raw | Write-Output
