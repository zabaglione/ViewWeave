param(
    [Parameter(Mandatory=$true)][ValidateSet('RotationAngle','AngularVelocity','OrientAdvanced')][string]$Graph,
    [int]$TimeoutSeconds = 180
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path 'REPO_ROOT').Path
$localRoot = Join-Path $repo '.local/windows-urp-vfx-frustum-memory-20261013'
$project = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/UnityProject'
$baseRunner = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/Tools/Run-UnityIsolated.ps1'
$baseResults = Join-Path $repo '.local/windows-vfx-scene-comparison-20261013/URP/Results'
$sharedSource = Join-Path $localRoot 'Shared/FrustumMemoryProbeTests.cs'
$cloneSource = Join-Path $project 'Assets/SceneComparisonTests/FrustumMemoryProbeTests.cs'
$cloneMeta = $cloneSource + '.meta'
if (-not (Test-Path -LiteralPath $project) -or -not (Test-Path -LiteralPath $baseRunner)) { throw 'The prepared isolated URP project or safe runner is missing.' }
if (-not (Test-Path -LiteralPath $sharedSource) -or -not (Test-Path -LiteralPath $cloneSource) -or -not (Test-Path -LiteralPath $cloneMeta)) { throw 'The canonical source, clone source, or clone metadata is missing.' }
$sharedHash = (Get-FileHash -LiteralPath $sharedSource -Algorithm SHA256).Hash
$cloneHash = (Get-FileHash -LiteralPath $cloneSource -Algorithm SHA256).Hash
if ($sharedHash -ne $cloneHash) { throw 'Canonical and clone probe source hashes differ.' }
$sourceBytes = (Get-Item -LiteralPath $sharedSource).Length
$filter = "ViewWeave.SceneComparison.Tests.FrustumMemoryProbeTests.$($Graph)OriginalSceneOneViewFrustumLifecycle"
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$results = Join-Path $localRoot "URP/Results/$stamp-$($Graph)"
$projectResults = Join-Path $project "TestResults/FrustumMemory/$stamp-$($Graph)"
New-Item -ItemType Directory -Force -Path $results | Out-Null
$runnerRecord = Join-Path $results 'runner.json'
$xmlCopy = Join-Path $results 'results.xml'
$summaryPath = Join-Path $results 'summary.json'
$sourceSnapshot = Join-Path $results 'asrun-source.cs'
$cloneSourceSnapshot = Join-Path $results 'asrun-clone-source.cs'
$cloneMetaSnapshot = Join-Path $results 'asrun-clone-source.meta'
$runnerSnapshot = Join-Path $results 'asrun-runner.ps1'
$resultJson = Join-Path $projectResults 'result.json'
$runnerSourceHash = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
Copy-Item -LiteralPath $sharedSource -Destination $sourceSnapshot
Copy-Item -LiteralPath $cloneSource -Destination $cloneSourceSnapshot
Copy-Item -LiteralPath $cloneMeta -Destination $cloneMetaSnapshot
Copy-Item -LiteralPath $PSCommandPath -Destination $runnerSnapshot
$envName = 'VW_FRUSTUM_MEMORY_EVIDENCE_DIR'
$oldEnv = [Environment]::GetEnvironmentVariable($envName,'Process')
$beforeRecords = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File -ErrorAction SilentlyContinue | ForEach-Object FullName)
$underlying = $null
$underlyingPath = $null
$invocationError = $null
try {
    [Environment]::SetEnvironmentVariable($envName,$projectResults,'Process')
    try {
        $null = & $baseRunner -Pipeline URP -Operation RunTests -TestPlatform PlayMode -TestFilter $filter -ExpectedTestCount 1 -TimeoutSeconds $TimeoutSeconds -GraphicsApi D3D12
    }
    catch { $invocationError = $_.Exception.GetType().Name }
}
finally {
    [Environment]::SetEnvironmentVariable($envName,$oldEnv,'Process')
}
$afterRecords = @(Get-ChildItem -LiteralPath $baseResults -Filter 'RunTests-*-runner.json' -File -ErrorAction SilentlyContinue | ForEach-Object FullName)
$created = @($afterRecords | Where-Object { $beforeRecords -notcontains $_ })
if ($created.Count -eq 1) {
    $underlyingPath = $created[0]
    try { $underlying = Get-Content -LiteralPath $underlyingPath -Raw | ConvertFrom-Json } catch { $invocationError = 'UnderlyingRunnerJsonInvalid' }
    if ($underlying) { Copy-Item -LiteralPath $underlyingPath -Destination $runnerRecord }
}
if ($underlying -and $underlying.rawXmlPath -and (Test-Path -LiteralPath $underlying.rawXmlPath)) { Copy-Item -LiteralPath $underlying.rawXmlPath -Destination $xmlCopy }
$xmlTestNames = @()
$xmlParseError = $null
if (Test-Path -LiteralPath $xmlCopy) {
    try {
        [xml]$xmlDocument = [IO.File]::ReadAllText($xmlCopy)
        $xmlTestNames = @($xmlDocument.SelectNodes('//test-case') | ForEach-Object { [string]$_.fullname })
    }
    catch { $xmlParseError = $_.Exception.GetType().Name }
}
$exactCase = $null -ne $underlying -and $null -ne $underlying.nunit -and $underlying.nunit.total -eq 1 -and $xmlTestNames.Count -eq 1 -and $xmlTestNames[0] -eq $filter
$drainVerified = $null -ne $underlying -and $null -ne $underlying.PSObject.Properties['outputDrainIncomplete'] -and $underlying.outputDrainIncomplete -eq $false
$sharedHashAfter = (Get-FileHash -LiteralPath $sharedSource -Algorithm SHA256).Hash
$cloneHashAfter = (Get-FileHash -LiteralPath $cloneSource -Algorithm SHA256).Hash
$runnerHashAfter = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash
$pass = $exactCase -and $drainVerified -and $underlying.editorExitCode -eq 0 -and -not $underlying.timeout -and $underlying.testPassGate -and $underlying.processInventoryAvailableAfter -and @($underlying.matchingAfter).Count -eq 0 -and $sharedHashAfter -eq $sharedHash -and $cloneHashAfter -eq $cloneHash -and $runnerHashAfter -eq $runnerSourceHash
$resultSummary = $null
if (Test-Path -LiteralPath $resultJson) { try { $resultSummary = Get-Content -LiteralPath $resultJson -Raw | ConvertFrom-Json } catch { $invocationError = 'ProbeResultJsonInvalid' } }
$summary = [ordered]@{
    schema='viewweave-frustum-memory-runner-v1'
    status=if($pass){'probe-passed'}else{'not-passed'}
    graph=$Graph; testFilter=$filter; expectedTestCount=1; testPlatform='PlayMode'; graphicsApiRequested='D3D12'
    projectPath=$project; canonicalSourcePath=$sharedSource; canonicalSourceSha256=$sharedHash; cloneSourceSha256=$cloneHash; sourceBytes=$sourceBytes
    canonicalSourceSha256After=$sharedHashAfter; cloneSourceSha256After=$cloneHashAfter; runnerSha256=$runnerSourceHash; runnerSha256After=$runnerHashAfter
    sourceSnapshot='asrun-source.cs'; cloneSourceSnapshot='asrun-clone-source.cs'; cloneMetaSnapshot='asrun-clone-source.meta'; runnerSnapshot='asrun-runner.ps1'
    wrapperStartedUtc=$stamp; invocationErrorType=$invocationError
    underlyingRunnerPath=$underlyingPath; editorProcessId=if($underlying){$underlying.editorProcessId}else{$null}; editorStartUtc=if($underlying){$underlying.startedUtc}else{$null}
    editorExitCode=if($underlying){$underlying.editorExitCode}else{$null}; timeout=if($underlying){$underlying.timeout}else{$null}
    outputDrainIncomplete=if($underlying){$underlying.outputDrainIncomplete}else{$null}; residualCount=if($underlying){$underlying.processInventoryResidualCount}else{$null}
    xmlPath=if(Test-Path -LiteralPath $xmlCopy){$xmlCopy}else{$null}; xmlBytes=if(Test-Path -LiteralPath $xmlCopy){(Get-Item -LiteralPath $xmlCopy).Length}else{$null}
    xmlSha256=if(Test-Path -LiteralPath $xmlCopy){(Get-FileHash -LiteralPath $xmlCopy -Algorithm SHA256).Hash}else{$null}; xmlParseErrorType=$xmlParseError
    nunit=if($underlying){$underlying.nunit}else{$null}; xmlTestCaseFullNames=$xmlTestNames; exactCaseGate=[bool]$exactCase; drainGate=[bool]$drainVerified
    underlyingTestPassGate=if($underlying){$underlying.testPassGate}else{$false}; evidenceDirectory=$projectResults; evidenceResultStatus=if($resultSummary){$resultSummary.status}else{'missing'}
    evidenceResultPath=if(Test-Path -LiteralPath $resultJson){$resultJson}else{$null}; editorLogIncluded=$false; passGate=[bool]$pass
}
[IO.File]::WriteAllText($summaryPath,($summary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
Get-Content -LiteralPath $summaryPath -Raw | Write-Output
if (-not $pass) { exit 1 }
