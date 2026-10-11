# Builds and runs one device-backed Windows Sample Player case.
# Example:
# .\Run-WindowsSamplePlayerValidation.ps1 -Pipeline urp -GraphicsApi d3d12 `
#   -Development false -EvidenceRoot E:\LookingGlassSDK\.local\validation\urp-d3d12-release `
#   -ExpectedDllSha256 CEE089B1779752DAB4FFABDFFC44B879A0E4D09414C9A85F7590C8D9A9DDCF4D
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('urp', 'hdrp')]
    [string]$Pipeline,
    [Parameter(Mandatory = $true)]
    [ValidateSet('d3d11', 'd3d12')]
    [string]$GraphicsApi,
    [Parameter(Mandatory = $true)]
    [ValidateSet('true', 'false')]
    [string]$Development,
    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Fa-f0-9]{64}$')]
    [string]$ExpectedDllSha256,
    [int]$ExpectedDllBytes = 302080,
    [int]$PlayerTimeoutSeconds = 180,
    [string]$ExistingBuildRoot
)

$ErrorActionPreference = 'Stop'
$repo = 'E:\LookingGlassSDK'
$project = Join-Path $repo 'Tests\Pipelines\Unity'
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$editor = 'C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe'
$cliPlayerName = 'SamplePlayerValidation.exe'
$packageDll = Join-Path $repo 'Packages\com.zabaglione.lookingglass.viewweave.bridge\Plugins\x86_64\lookingglass_bridge_shim.dll'
$runtimeDll = 'C:\Program Files\Looking Glass\Looking Glass Bridge 2.6.3\bridge_inproc.dll'
$expectedDllHash = $ExpectedDllSha256.ToUpperInvariant()
$isDevelopment = [string]::Equals($Development, 'true', [StringComparison]::OrdinalIgnoreCase)
$buildKind = if ($isDevelopment) { 'Development' } else { 'Release' }
$pipelineName = $Pipeline.ToLowerInvariant()
$apiName = $GraphicsApi.ToUpperInvariant()
$builderMethod = if ($pipelineName -eq 'hdrp') { 'ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildHdrp' } else { 'ViewWeave.SamplePlayerValidation.Editor.SamplePlayerValidationBuilder.BuildUrp' }
$generatedAssets = Join-Path $project 'Assets\SamplePlayerValidationGenerated'
$generatedMeta = Join-Path $project 'Assets\SamplePlayerValidationGenerated.meta'
$settingsRoot = Join-Path $project 'ProjectSettings'
$pipelineGlobalSettingsPaths = @(
    'Assets/UniversalRenderPipelineGlobalSettings.asset',
    'Assets/UniversalRenderPipelineGlobalSettings.asset.meta',
    'Assets/HDRPDefaultResources/HDRenderPipelineGlobalSettings.asset',
    'Assets/HDRPDefaultResources/HDRenderPipelineGlobalSettings.asset.meta',
    'Assets/HDRPDefaultResources/DefaultSettingsVolumeProfile.asset',
    'Assets/HDRPDefaultResources/DefaultSettingsVolumeProfile.asset.meta'
)
$baselineRoot = Join-Path $evidence 'baseline'
$settingsBackup = Join-Path $baselineRoot 'ProjectSettings'
$assetsBackup = Join-Path $baselineRoot 'SamplePlayerValidationGenerated'
$pipelineSettingsBackup = Join-Path $baselineRoot 'PipelineGlobalSettings'
$runEvidence = Join-Path $evidence 'player-evidence'
$buildRoot = if ([string]::IsNullOrWhiteSpace($ExistingBuildRoot)) { Join-Path $evidence 'Build' } else { [IO.Path]::GetFullPath($ExistingBuildRoot) }
$playerPath = Join-Path $buildRoot $cliPlayerName
$settingsBefore = @()
$pipelineSettingsBefore = @()
$assetsBefore = @()
$generatedMetaBefore = $null
$buildProcess = $null
$playerProcess = $null
$buildResult = $null
$playerResult = $null
$snapshotReady = $false
$ownsEvidenceRoot = $false

function Write-AsciiJson([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 10) + "`r`n", [Text.Encoding]::ASCII)
}

function Hash-File([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Get-AsciiTextFromString([string]$Text) {
    return [regex]::Replace($Text, '[^\x00-\x7F]', '?')
}

function Assert-Within([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escaped approved root.' }
}

function Copy-TreeSnapshot([string]$Source, [string]$Destination) {
    $records = @()
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return $records }
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point found in protected tree.' }
        $relative = [IO.Path]::GetRelativePath($Source, $file.FullName)
        $copy = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $copy) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $copy -Force
        $records += [pscustomobject]@{ path = $relative.Replace('\','/'); sha256 = (Hash-File $file.FullName); bytes = $file.Length }
    }
    return $records
}

function Restore-TreeSnapshot([string]$Target, [string]$Backup, [object[]]$Records) {
    $targetExists = Test-Path -LiteralPath $Target -PathType Container
    if (-not $targetExists -and $Records.Count -eq 0) { return }
    if (-not $targetExists) { New-Item -ItemType Directory -Path $Target -Force | Out-Null }
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $Records) { [void]$allowed.Add($record.path.Replace('/','\')) }
    foreach ($file in @(Get-ChildItem -LiteralPath $Target -Recurse -File -Force)) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point found during tree restore.' }
        $relative = [IO.Path]::GetRelativePath($Target, $file.FullName)
        if (-not $allowed.Contains($relative)) {
            Assert-Within $file.FullName $Target
            Remove-Item -LiteralPath $file.FullName -Force
        }
    }
    foreach ($record in $Records) {
        $destination = Join-Path $Target ($record.path.Replace('/','\'))
        $source = Join-Path $Backup ($record.path.Replace('/','\'))
        Assert-Within $destination $Target
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw 'Protected snapshot file is missing.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Force
    }
    $dirs = @(Get-ChildItem -LiteralPath $Target -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending)
    foreach ($dir in $dirs) {
        if (@(Get-ChildItem -LiteralPath $dir.FullName -Force).Count -eq 0) {
            Assert-Within $dir.FullName $Target
            Remove-Item -LiteralPath $dir.FullName -Force
        }
    }
}

function Get-AsciiText([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    $text = [IO.File]::ReadAllText($Path)
    return [regex]::Replace($text, '[^\x00-\x7F]', '?')
}

try {
if ($PlayerTimeoutSeconds -lt 1) { throw 'PlayerTimeoutSeconds must be positive.' }
Assert-Within $evidence (Join-Path $repo '.local')
foreach ($ownedOutput in @($baselineRoot, $runEvidence, (Join-Path $evidence 'build-summary.json'), (Join-Path $evidence 'player-summary.json'), (Join-Path $evidence 'cleanup-summary.json'))) {
    if (Test-Path -LiteralPath $ownedOutput) { throw 'A prior run output exists; use a fresh evidence directory.' }
}
if ([string]::IsNullOrWhiteSpace($ExistingBuildRoot) -and (Test-Path -LiteralPath $buildRoot)) { throw 'A prior build output exists; use a fresh evidence directory.' }
if (-not [string]::IsNullOrWhiteSpace($ExistingBuildRoot)) {
    Assert-Within $buildRoot (Join-Path $repo '.local')
    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot $cliPlayerName) -PathType Leaf)) { throw 'Existing Player build is missing.' }
}
foreach ($path in @($project, $editor, $packageDll, $runtimeDll)) {
    if (-not (Test-Path -LiteralPath $path)) { throw 'Required project, Unity, package DLL, or Bridge runtime path is missing.' }
}
if ((Hash-File $packageDll) -ne $expectedDllHash -or (Get-Item -LiteralPath $packageDll).Length -ne $ExpectedDllBytes) { throw 'Package DLL identity mismatch.' }
$exactEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.ExecutablePath -ieq $editor })
if ($exactEditors.Count -ne 0) { throw 'The expected Unity Editor is already running.' }
$otherPlayers = @(Get-CimInstance Win32_Process | Where-Object { $_.Name -eq $cliPlayerName })
if ($otherPlayers.Count -ne 0) { throw 'Another SamplePlayerValidation process is already running.' }

New-Item -ItemType Directory -Path $evidence,$baselineRoot,$settingsBackup,$assetsBackup,$pipelineSettingsBackup,$runEvidence -Force | Out-Null
if ([string]::IsNullOrWhiteSpace($ExistingBuildRoot)) { New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null }
$ownsEvidenceRoot = $true
$settingsBefore = Copy-TreeSnapshot $settingsRoot $settingsBackup
foreach ($relativePath in $pipelineGlobalSettingsPaths) {
    $fullPath = Join-Path $project ($relativePath.Replace('/','\'))
    $exists = Test-Path -LiteralPath $fullPath -PathType Leaf
    $record = [pscustomobject]@{ path=$relativePath; exists=$exists; sha256=$null; bytes=0 }
    if ($exists) {
        $record.sha256 = Hash-File $fullPath
        $record.bytes = (Get-Item -LiteralPath $fullPath).Length
        $backupPath = Join-Path $pipelineSettingsBackup ($relativePath.Replace('/','\'))
        New-Item -ItemType Directory -Path (Split-Path -Parent $backupPath) -Force | Out-Null
        Copy-Item -LiteralPath $fullPath -Destination $backupPath -Force
    }
    $pipelineSettingsBefore += $record
}
$assetsWasPresent = Test-Path -LiteralPath $generatedAssets -PathType Container
$assetsBefore = Copy-TreeSnapshot $generatedAssets $assetsBackup
$generatedMetaBefore = if (Test-Path -LiteralPath $generatedMeta -PathType Leaf) { [pscustomobject]@{ exists=$true; sha256=(Hash-File $generatedMeta); bytes=(Get-Item -LiteralPath $generatedMeta).Length; backup=(Join-Path $baselineRoot 'SamplePlayerValidationGenerated.meta') } } else { [pscustomobject]@{ exists=$false; sha256=$null; bytes=0; backup=$null } }
if ($generatedMetaBefore.exists) { Copy-Item -LiteralPath $generatedMeta -Destination $generatedMetaBefore.backup -Force }
$snapshotReady = $true
$settingsHashesBefore = @($settingsBefore | ForEach-Object { [pscustomobject]@{ path=$_.path; sha256=$_.sha256 } })
$sourceHashes = @()
foreach ($source in @(
    'Assets/SamplePlayerValidation/Editor/SamplePlayerValidationBuilder.cs',
    'Assets/SamplePlayerValidation/SamplePlayerValidationRunner.cs',
    'Assets/SamplePlayerValidation/SamplePlayerValidation.Runtime.asmdef',
    'Assets/SamplePlayerValidation/Editor/SamplePlayerValidation.Editor.asmdef'
)) {
    $full = Join-Path $project ($source.Replace('/','\'))
    $sourceHashes += [pscustomobject]@{ path=$source; sha256=(Hash-File $full) }
}
$startedUtc = [DateTime]::UtcNow
$buildLog = Join-Path $evidence 'unity-build.log'
$buildOut = Join-Path $evidence 'unity-build.stdout.txt'
$buildErr = Join-Path $evidence 'unity-build.stderr.txt'
$buildArgs = @('-batchmode','-quit','-projectPath',$project,'-executeMethod',$builderMethod,'-buildOutput',$playerPath,'-buildGraphicsApi',$GraphicsApi.ToLowerInvariant(),'-buildDevelopment',$Development.ToLowerInvariant(),'-logFile',$buildLog)
$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $editor
$psi.WorkingDirectory = $project
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
foreach ($arg in $buildArgs) { $psi.ArgumentList.Add($arg) }
if ([string]::IsNullOrWhiteSpace($ExistingBuildRoot)) {
    $buildProcess = [Diagnostics.Process]::new()
    $buildProcess.StartInfo = $psi
    if (-not $buildProcess.Start()) { throw 'Unity build Editor did not start.' }
    $buildStdout = $buildProcess.StandardOutput.ReadToEndAsync()
    $buildStderr = $buildProcess.StandardError.ReadToEndAsync()
    $buildStarted = [DateTime]::UtcNow
    if (-not $buildProcess.WaitForExit(1200000)) { throw 'Unity Player build exceeded its 1200-second deadline; its process was left running.' }
    $buildProcess.WaitForExit()
    $buildExit = $buildProcess.ExitCode
    $buildStdout.Wait(); $buildStderr.Wait()
    [IO.File]::WriteAllText($buildOut,(Get-AsciiTextFromString ([string]$buildStdout.Result)),[Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($buildErr,(Get-AsciiTextFromString ([string]$buildStderr.Result)),[Text.Encoding]::ASCII)
    if (Test-Path -LiteralPath $buildLog -PathType Leaf) {
        $buildLogText = Get-AsciiText $buildLog
        [IO.File]::WriteAllText($buildLog, $buildLogText, [Text.Encoding]::ASCII)
    }
    $buildResult = [ordered]@{ editorPid=$buildProcess.Id; startedUtc=$buildStarted.ToString('o'); endedUtc=[DateTime]::UtcNow.ToString('o'); naturalExit=$true; exitCode=$buildExit; buildLog=$buildLog; stdout=$buildOut; stderr=$buildErr; pipeline=$pipelineName; graphicsApi=$apiName; development=$isDevelopment; arguments=@('-batchmode','-quit','-executeMethod',$builderMethod,'-buildGraphicsApi',$GraphicsApi.ToLowerInvariant(),'-buildDevelopment',$Development.ToLowerInvariant()); playerPath=$playerPath; playerExists=(Test-Path -LiteralPath $playerPath -PathType Leaf); playerExeSha256=(Hash-File $playerPath); sourceHashes=$sourceHashes }
    Write-AsciiJson (Join-Path $evidence 'build-summary.json') $buildResult
    if ($buildExit -ne 0 -or -not (Test-Path -LiteralPath $playerPath -PathType Leaf)) { throw 'Sample Player build failed.' }
} else {
    $buildReferencePath = Join-Path (Split-Path -Parent $ExistingBuildRoot) 'build-summary.json'
    if (-not (Test-Path -LiteralPath $buildReferencePath -PathType Leaf)) { throw 'Existing build reference summary is missing.' }
    $buildReference = Get-Content -Raw -LiteralPath $buildReferencePath | ConvertFrom-Json
    if (-not $buildReference.naturalExit -or $buildReference.exitCode -ne 0 -or $buildReference.pipeline -ne $pipelineName -or $buildReference.graphicsApi -ne $apiName -or [bool]$buildReference.development -ne $isDevelopment) { throw 'Existing build reference does not match the requested Player configuration.' }
    $sourceHashMismatch = @($buildReference.sourceHashes | Where-Object {
        $prior = $_
        $current = @($sourceHashes | Where-Object { $_.path -eq $prior.path } | Select-Object -First 1)
        $current.Count -ne 1 -or $current[0].sha256 -ne $prior.sha256
    }).Count -ne 0
    if ($sourceHashMismatch) { throw 'Existing build source hashes differ from the current source files.' }
    if ((Hash-File $playerPath) -ne $buildReference.playerExeSha256 -and $buildReference.playerExeSha256) { throw 'Existing Player executable hash differs from its build reference.' }
    Write-AsciiJson (Join-Path $evidence 'build-reference.json') ([ordered]@{ sourceSummary=$buildReferencePath; sourceSummarySha256=(Hash-File $buildReferencePath); reusedBuildRoot=$buildRoot; playerPath=$playerPath; playerSha256=(Hash-File $playerPath); nativeDllPath=(Join-Path $buildRoot 'SamplePlayerValidation_Data\Plugins\x86_64\lookingglass_bridge_shim.dll'); nativeDllSha256=(Hash-File (Join-Path $buildRoot 'SamplePlayerValidation_Data\Plugins\x86_64\lookingglass_bridge_shim.dll')); requestedPipeline=$pipelineName; requestedApi=$apiName; requestedDevelopment=$isDevelopment; currentSourceHashes=$sourceHashes })
}

$builtDll = Join-Path $buildRoot 'SamplePlayerValidation_Data\Plugins\x86_64\lookingglass_bridge_shim.dll'
if (-not (Test-Path -LiteralPath $builtDll -PathType Leaf)) { throw 'Built Player native DLL is missing.' }
$builtDllHash = Hash-File $builtDll
if ($builtDllHash -ne $expectedDllHash) { throw 'Built Player native DLL hash differs from package DLL.' }
$playerLog = Join-Path $evidence 'player.log'
$nativeTrace = Join-Path $evidence 'native-trace.log'
$playerStdoutPath = Join-Path $evidence 'player.stdout.txt'
$playerStderrPath = Join-Path $evidence 'player.stderr.txt'
$playerArgs = @('--quality','preview','--evidence-dir',$runEvidence,'-logFile',$playerLog)
$playerInfo = [Diagnostics.ProcessStartInfo]::new()
$playerInfo.FileName = $playerPath
$playerInfo.WorkingDirectory = $buildRoot
$playerInfo.UseShellExecute = $false
$playerInfo.CreateNoWindow = $true
$playerInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$playerInfo.RedirectStandardOutput = $true
$playerInfo.RedirectStandardError = $true
foreach ($arg in $playerArgs) { $playerInfo.ArgumentList.Add($arg) }
$playerInfo.EnvironmentVariables['LKG_BRIDGE_PATH'] = $runtimeDll
$playerInfo.EnvironmentVariables['LKG_NATIVE_TRACE'] = $nativeTrace
$playerInfo.EnvironmentVariables['VIEWWEAVE_EDITOR_PREVIEW_SUBPIXEL_MODE'] = 'Automatic'
$playerInfo.EnvironmentVariables['TEMP'] = Join-Path $evidence 'temp-player'
$playerInfo.EnvironmentVariables['TMP'] = Join-Path $evidence 'temp-player'
New-Item -ItemType Directory -Path $playerInfo.EnvironmentVariables['TEMP'] -Force | Out-Null
$playerProcess = [Diagnostics.Process]::new()
$playerProcess.StartInfo = $playerInfo
$playerStart = [DateTime]::UtcNow
if (-not $playerProcess.Start()) { throw 'Sample Player process did not start.' }
$playerStdout = $playerProcess.StandardOutput.ReadToEndAsync()
$playerStderr = $playerProcess.StandardError.ReadToEndAsync()
$moduleSamples = 0
$moduleFailures = 0
$moduleEvidence = $null
$deadline = [DateTime]::UtcNow.AddSeconds($PlayerTimeoutSeconds)
$deadlineReached = $false
while (-not $playerProcess.HasExited -and [DateTime]::UtcNow -lt $deadline) {
    $moduleSamples++
    try {
        $matches = @()
        foreach ($module in (Get-Process -Id $playerProcess.Id -Module -ErrorAction Stop)) {
            if ($module.ModuleName -ieq 'lookingglass_bridge_shim.dll') {
                $matches += [pscustomobject]@{ path=[IO.Path]::GetFullPath($module.FileName); sha256=(Hash-File $module.FileName) }
            }
        }
        if ($matches.Count -gt 0 -and $null -eq $moduleEvidence) {
            $moduleEvidence = [ordered]@{ pid=$playerProcess.Id; modules=$matches; expectedPlayerPath=[IO.Path]::GetFullPath($builtDll); expectedSha256=$expectedDllHash; pathMatches=($matches.Count -eq 1 -and [string]::Equals($matches[0].path,[IO.Path]::GetFullPath($builtDll),[StringComparison]::OrdinalIgnoreCase)); hashMatches=($matches.Count -eq 1 -and $matches[0].sha256 -eq $expectedDllHash); sampleAttempts=$moduleSamples }
            Write-AsciiJson (Join-Path $evidence 'loaded-modules.json') $moduleEvidence

        }
    } catch { $moduleFailures++ }
    Start-Sleep -Milliseconds 100
    $playerProcess.Refresh()
}
$playerProcess.Refresh()
$hasExitedAtLoopEnd = $playerProcess.HasExited
$deadlineReached = -not $hasExitedAtLoopEnd -and [DateTime]::UtcNow -ge $deadline
$graceWaitMilliseconds = 10000
$playerNaturalExit = $playerProcess.WaitForExit($graceWaitMilliseconds)
$playerExit = if ($playerNaturalExit) { $playerProcess.ExitCode } else { $null }
if ($playerNaturalExit) {
    $playerStdout.Wait(); $playerStderr.Wait()
    [IO.File]::WriteAllText($playerStdoutPath,(Get-AsciiTextFromString ([string]$playerStdout.Result)),[Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($playerStderrPath,(Get-AsciiTextFromString ([string]$playerStderr.Result)),[Text.Encoding]::ASCII)
    if (Test-Path -LiteralPath $playerLog -PathType Leaf) {
        $playerLogText = Get-AsciiText $playerLog
        [IO.File]::WriteAllText($playerLog, $playerLogText, [Text.Encoding]::ASCII)
    }
} else {
    [IO.File]::WriteAllText($playerStdoutPath,'Player stdout unavailable before natural exit.' + "`r`n",[Text.Encoding]::ASCII)
    [IO.File]::WriteAllText($playerStderrPath,'Player stderr unavailable before natural exit.' + "`r`n",[Text.Encoding]::ASCII)
}
$playerEnd = [DateTime]::UtcNow
$playerSummaryPath = Join-Path $runEvidence 'summary.json'
$runnerSummary = if (Test-Path -LiteralPath $playerSummaryPath -PathType Leaf) { Get-Content -Raw -LiteralPath $playerSummaryPath | ConvertFrom-Json } else { $null }
$traceText = if (Test-Path -LiteralPath $nativeTrace -PathType Leaf) { Get-Content -Raw -LiteralPath $nativeTrace } else { '' }
$nativePids = @([regex]::Matches($traceText,'(?m)\bpid=(\d+)') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Unique)
$nativeGates = [ordered]@{
    tracePresent=(Test-Path -LiteralPath $nativeTrace)
    loadOk=$traceText.Contains('load_end result=0')
    initializeOk=$traceText.Contains('initialize_end result=0')
    displayOne=[bool]($traceText -match 'stage=display-count countResult=0 count=1')
    pendingZero=[bool]($traceText -match 'pendingTokens=0')
    uninitializeOk=[bool]($traceText -match 'event=uninitialize_bridge end result=1')
    tracePids=$nativePids
    pidMatches=($nativePids.Count -gt 0 -and $nativePids.Count -eq 1 -and $nativePids[0] -eq $playerProcess.Id)
}
$events = @()
try {
    foreach ($event in Get-WinEvent -FilterHashtable @{LogName='Application';StartTime=$playerStart.ToLocalTime();EndTime=$playerEnd.ToLocalTime();Id=1000,1001} -ErrorAction SilentlyContinue) {
        $message = [string]$event.Message
        if ($message -match [regex]::Escape($cliPlayerName) -or $message -match ('PID:\s*' + $playerProcess.Id)) {
            $events += [pscustomobject]@{ timeUtc=$event.TimeCreated.ToUniversalTime().ToString('o'); id=$event.Id; provider=$event.ProviderName; messageCaptured=$false }
        }
    }
} catch {}
$playerResult = [ordered]@{
    startedUtc=$playerStart.ToString('o'); endedUtc=$playerEnd.ToString('o'); pid=$playerProcess.Id
    playerTimeoutSeconds=$PlayerTimeoutSeconds; deadlineReached=$deadlineReached; hasExitedAtLoopEnd=$hasExitedAtLoopEnd; postLoopGraceWaitMilliseconds=$graceWaitMilliseconds
    naturalExit=$playerNaturalExit; exitCode=$playerExit; expectedBuildKind=$buildKind; expectedDevelopment=$isDevelopment
    pipeline=$pipelineName; quality='preview'; requestedApi=$apiName; playerEvidence=$playerSummaryPath; playerLog=$playerLog
    stdout=$playerStdoutPath; stderr=$playerStderrPath; nativeTrace=$nativeTrace
    playerNativeDll=$builtDll; playerNativeDllSha256=$builtDllHash; moduleEvidence=$moduleEvidence
    moduleSampleAttempts=$moduleSamples; moduleEnumerationFailures=$moduleFailures
    native=$nativeGates; applicationEvents1000_1001=$events; runner=$runnerSummary
}
Write-AsciiJson (Join-Path $evidence 'player-summary.json') $playerResult

if ($null -eq $runnerSummary -or -not $playerNaturalExit -or $deadlineReached -or $playerExit -ne 0 -or -not $runnerSummary.ok -or $runnerSummary.isEditor -or [bool]$runnerSummary.development -ne $isDevelopment -or -not $runnerSummary.graphicsMultiThreaded -or $runnerSummary.graphicsDeviceType -ne $(if ($GraphicsApi -eq 'd3d12') { 'Direct3D12' } else { 'Direct3D11' }) -or $runnerSummary.pipeline -ne $pipelineName -or $runnerSummary.quality -ne 'preview' -or [uint32]$runnerSummary.completed -lt 120 -or -not $runnerSummary.before.allTilesNonBlank -or -not $runnerSummary.after.allTilesNonBlank -or -not $runnerSummary.before.colorEvidence -or -not $runnerSummary.after.colorEvidence -or -not $runnerSummary.viewDifference -or -not $runnerSummary.movementChanged -or -not $runnerSummary.cleanupOk -or $runnerSummary.cleanupPending -ne 0) { throw 'Player runtime evidence gate failed.' }
if ($null -eq $moduleEvidence -or -not $moduleEvidence.pathMatches -or -not $moduleEvidence.hashMatches) { throw 'Loaded Player module path/hash gate failed.' }
if (-not $nativeGates.tracePresent -or -not $nativeGates.loadOk -or -not $nativeGates.initializeOk -or -not $nativeGates.displayOne -or -not $nativeGates.pendingZero -or -not $nativeGates.uninitializeOk -or -not $nativeGates.pidMatches) { throw 'Native trace lifecycle/PID gate failed.' }
if ($events.Count -ne 0) { throw 'Application crash events were observed for the Player.' }

}
finally {
    $unityStillRunning = $false
    if ($null -ne $buildProcess -and -not $buildProcess.HasExited) { $unityStillRunning = $true }
    $playerStillRunning = $false
    if ($null -ne $playerProcess -and -not $playerProcess.HasExited) { $playerStillRunning = $true }
    $ownedProcessStillRunning = $unityStillRunning -or $playerStillRunning
    if ($snapshotReady -and -not $ownedProcessStillRunning) {
        Restore-TreeSnapshot $generatedAssets $assetsBackup $assetsBefore
        if ($generatedMetaBefore.exists) { Copy-Item -LiteralPath $generatedMetaBefore.backup -Destination $generatedMeta -Force }
        elseif (Test-Path -LiteralPath $generatedMeta -PathType Leaf) { Assert-Within $generatedMeta (Join-Path $project 'Assets'); Remove-Item -LiteralPath $generatedMeta -Force }
        Restore-TreeSnapshot $settingsRoot $settingsBackup $settingsBefore
        foreach ($record in $pipelineSettingsBefore) {
            $destination = Join-Path $project ($record.path.Replace('/','\'))
            if ($record.exists) {
                $backupPath = Join-Path $pipelineSettingsBackup ($record.path.Replace('/','\'))
                if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) { throw 'Pipeline global settings baseline file is missing.' }
                Assert-Within $destination (Join-Path $project 'Assets')
                Copy-Item -LiteralPath $backupPath -Destination $destination -Force
            } elseif (Test-Path -LiteralPath $destination -PathType Leaf) {
                Assert-Within $destination (Join-Path $project 'Assets')
                Remove-Item -LiteralPath $destination -Force
            }
        }
    }
    $settingsAfter = @()
    $settingsDifferences = @()
    if ($snapshotReady) {
        $settingsManifestAfter = Copy-TreeSnapshot $settingsRoot (Join-Path $settingsBackup 'after-audit')
        $settingsDifferences = @(Compare-Object -ReferenceObject @($settingsBefore | Select-Object path,sha256,bytes) -DifferenceObject @($settingsManifestAfter | Select-Object path,sha256,bytes) -Property path,sha256,bytes)
        foreach ($record in $settingsBefore) {
            $current = @($settingsManifestAfter | Where-Object { $_.path -eq $record.path }) | Select-Object -First 1
            $settingsAfter += [pscustomobject]@{ path=$record.path; matchesBaseline=($null -ne $current -and $current.sha256 -eq $record.sha256 -and $current.bytes -eq $record.bytes) }
        }
    }
    $assetAfter = @()
    if ($snapshotReady) { $assetAfter = Copy-TreeSnapshot $generatedAssets (Join-Path $baselineRoot 'GeneratedAssetsAfterAudit') }
    $pipelineSettingsAfter = @()
    foreach ($record in $pipelineSettingsBefore) {
        $path = Join-Path $project ($record.path.Replace('/','\'))
        $exists = Test-Path -LiteralPath $path -PathType Leaf
        $matchesBaseline = if ($record.exists) { $exists -and (Hash-File $path) -eq $record.sha256 } else { -not $exists }
        $pipelineSettingsAfter += [pscustomobject]@{ path=$record.path; existedAtStart=$record.exists; matchesBaseline=$matchesBaseline }
    }
    if ($snapshotReady -and -not $assetsWasPresent -and (Test-Path -LiteralPath $generatedAssets -PathType Container) -and @(Get-ChildItem -LiteralPath $generatedAssets -Force).Count -eq 0) {
        Assert-Within $generatedAssets (Join-Path $project 'Assets')
        Remove-Item -LiteralPath $generatedAssets -Force
    }
    $assetDifferences = @()
    if ($snapshotReady) { $assetDifferences = @(Compare-Object -ReferenceObject @($assetsBefore | Select-Object path,sha256,bytes) -DifferenceObject @($assetAfter | Select-Object path,sha256,bytes) -Property path,sha256,bytes) }
    $generatedFolderMatchesBaseline = if ($assetsWasPresent) { Test-Path -LiteralPath $generatedAssets -PathType Container } else { -not (Test-Path -LiteralPath $generatedAssets -PathType Container) }
    $generatedMetaMatchesBaseline = if ($snapshotReady -and $generatedMetaBefore.exists) { (Test-Path -LiteralPath $generatedMeta) -and (Hash-File $generatedMeta) -eq $generatedMetaBefore.sha256 } elseif ($snapshotReady) { -not (Test-Path -LiteralPath $generatedMeta) } else { $false }
    $settingsMatch = $snapshotReady -and $settingsDifferences.Count -eq 0 -and @($settingsAfter | Where-Object { -not $_.matchesBaseline }).Count -eq 0
    $assetsMatch = $snapshotReady -and $generatedFolderMatchesBaseline -and $generatedMetaMatchesBaseline -and $assetDifferences.Count -eq 0
    $pipelineSettingsMatch = $snapshotReady -and @($pipelineSettingsAfter | Where-Object { -not $_.matchesBaseline }).Count -eq 0
    $cleanupOk = $snapshotReady -and -not $ownedProcessStillRunning -and $settingsMatch -and $assetsMatch -and $pipelineSettingsMatch
    $cleanup = [ordered]@{ completedUtc=[DateTime]::UtcNow.ToString('o'); evidenceRootOwned=$ownsEvidenceRoot; snapshotReady=$snapshotReady; unityEditorStillRunning=$unityStillRunning; playerStillRunning=$playerStillRunning; generatedFolderExistedAtStart=$assetsWasPresent; generatedFolderMatchesBaseline=$generatedFolderMatchesBaseline; generatedAssetFilesAtStart=$assetsBefore.Count; generatedAssetFilesAfter=$assetAfter.Count; generatedMetaMatchesBaseline=$generatedMetaMatchesBaseline; generatedAssetDifferences=@($assetDifferences | ForEach-Object { [ordered]@{ side=$_.SideIndicator; path=$_.path; sha256=$_.sha256; bytes=$_.bytes } }); projectSettingsFilesAtStart=$settingsBefore.Count; projectSettingsMatchesBaseline=$settingsMatch; projectSettingsDifferences=@($settingsDifferences | ForEach-Object { [ordered]@{ side=$_.SideIndicator; path=$_.path; sha256=$_.sha256; bytes=$_.bytes } }); generatedAssetsMatchBaseline=$assetsMatch; pipelineGlobalSettingsMatchesBaseline=$pipelineSettingsMatch; pipelineGlobalSettings=$pipelineSettingsAfter; settings=$settingsAfter; assetsBefore=$assetsBefore; assetsAfter=$assetAfter; cleanupOk=$cleanupOk }
    if ($ownsEvidenceRoot) { Write-AsciiJson (Join-Path $evidence 'cleanup-summary.json') $cleanup }
    if (-not $cleanupOk) { throw 'Player runner cleanup gate failed; inspect cleanup-summary.json.' }
}
