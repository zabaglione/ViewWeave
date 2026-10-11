param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Urp', 'Hdrp')]
    [string]$Kind
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$editor = 'C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe'
$project = if ($Kind -eq 'Urp') { 'E:\URPSample_6.6.41f1' } else { 'E:\My project (34)' }
$pipeline = if ($Kind -eq 'Urp') { 'URP' } else { 'HDRP' }
$package = if ($Kind -eq 'Urp') { 'com.zabaglione.lookingglass.viewweave.urp' } else { 'com.zabaglione.lookingglass.viewweave.hdrp' }
$sampleName = if ($Kind -eq 'Urp') { 'URP Quick Start' } else { 'HDRP Quick Start' }
$packageDisplayName = if ($Kind -eq 'Urp') { 'ViewWeave for Looking Glass - URP (Unofficial)' } else { 'ViewWeave for Looking Glass - HDRP (Unofficial)' }
$sourceSample = if ($Kind -eq 'Urp') { 'Distribution\urp\Samples~\QuickStartURP' } else { 'Distribution\hdrp\Samples~\QuickStartHDRP' }
$distributionPackage = if ($Kind -eq 'Urp') { 'Distribution\urp' } else { 'Distribution\hdrp' }
$expectedPackageRoot = Join-Path $repo $distributionPackage
$evidenceRoot = Join-Path $repo '.local\windows-environment-output-validation-20261011'
$runName = 'quickstart-' + $Kind.ToLowerInvariant() + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$run = Join-Path $evidenceRoot $runName
$sourceHelper = Join-Path $PSScriptRoot 'WindowsQuickStartValidation\QuickStartValidationRunner.cs'
$helperName = 'ViewWeaveQuickStartValidation_20261011.cs'
$helper = Join-Path $project ('Assets\Editor\' + $helperName)
$helperMeta = $helper + '.meta'
$packageVersion = ((Get-Content -LiteralPath (Join-Path $repo (Join-Path $distributionPackage 'package.json')) -Raw) | ConvertFrom-Json).version
$expectedImport = Join-Path $project ('Assets\Samples\' + $packageDisplayName + '\' + $packageVersion + '\' + $sampleName)
$expectedImportMeta = $expectedImport + '.meta'
$packageSampleRoot = Join-Path $project ('Assets\Samples\' + $packageDisplayName)
$versionSampleRoot = Join-Path $packageSampleRoot $packageVersion
$scene = Join-Path $expectedImport 'QuickStart.unity'
$log = Join-Path $run 'editor.log'
$baselineRoot = Join-Path $run 'baseline'

function Get-TreeManifest([string]$Root, [string[]]$RelativeRoots) {
    $entries = [System.Collections.Generic.List[object]]::new()
    foreach ($relativeRoot in $RelativeRoots) {
        $absoluteRoot = Join-Path $Root $relativeRoot
        if (-not (Test-Path -LiteralPath $absoluteRoot)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $absoluteRoot -Recurse -File -Force) {
            $relative = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            $entries.Add([pscustomobject]@{ path = $relative; bytes = $file.Length; sha256 = $hash })
        }
    }
    return @($entries | Sort-Object path)
}

function Assert-OwnedPath([string]$Path, [string]$Boundary) {
    $full = [IO.Path]::GetFullPath($Path)
    $base = [IO.Path]::GetFullPath($Boundary).TrimEnd('\') + '\'
    if (-not $full.StartsWith($base, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cleanup path escaped its owned project boundary.'
    }
    return $full
}

function Read-TextWithRetry([string]$Path, [int]$Attempts = 20) {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try { return [IO.File]::ReadAllText($Path) }
        catch [IO.IOException] {
            if ($attempt -eq $Attempts) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
    throw 'Editor log could not be read after bounded retry.'
}

if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) { throw 'Unity Editor 6000.6.4f1 was not found.' }
if (-not (Test-Path -LiteralPath $project -PathType Container)) { throw 'Target sample project was not found.' }
if (-not (Test-Path -LiteralPath $sourceHelper -PathType Leaf)) { throw 'Quick Start validation helper source was not found.' }
if (-not (Test-Path -LiteralPath (Join-Path $repo $sourceSample) -PathType Container)) { throw 'Expected Quick Start package sample source was not found.' }
if (-not (Test-Path -LiteralPath (Join-Path $project 'Assets\Editor') -PathType Container)) { throw 'Sample Editor folder was not found.' }
if (Test-Path -LiteralPath $helper) { throw 'Temporary Editor helper already exists; refusing to overwrite it.' }
if (Test-Path -LiteralPath $helperMeta) { throw 'Temporary Editor helper meta already exists; refusing to overwrite it.' }
if ((Test-Path -LiteralPath $expectedImport) -or (Test-Path -LiteralPath $expectedImportMeta)) { throw 'Quick Start sample import target or folder metadata already exists; refusing to overwrite user content.' }
if (-not (Test-Path -LiteralPath (Join-Path $project 'Packages\manifest.json'))) { throw 'Sample project manifest was not found.' }
if (-not (Test-Path -LiteralPath (Join-Path $project 'Packages\packages-lock.json'))) { throw 'Sample project package lock was not found.' }

$editorProcesses = @(Get-CimInstance Win32_Process | Where-Object {
    $_.ExecutablePath -and [StringComparer]::OrdinalIgnoreCase.Equals($_.ExecutablePath, $editor)
})
if ($editorProcesses.Count -gt 0) { throw 'Unity Editor 6000.6.4f1 is already running; refusing to attach or interfere.' }

New-Item -ItemType Directory -Path $run -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $baselineRoot 'assets') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $baselineRoot 'project-settings') -Force | Out-Null
$manifest = Get-TreeManifest $project @('Assets', 'ProjectSettings')
$samplesRootMeta = Join-Path $project 'Assets\Samples.meta'
$samplesRootMetaExisted = [bool]($manifest | Where-Object { $_.path -eq 'Assets/Samples.meta' })
[IO.File]::WriteAllText((Join-Path $baselineRoot 'assets-projectsettings.json'), ($manifest | ConvertTo-Json -Depth 4), [Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $project 'Assets') -Destination (Join-Path $baselineRoot 'assets') -Recurse
Copy-Item -LiteralPath (Join-Path $project 'ProjectSettings') -Destination (Join-Path $baselineRoot 'project-settings') -Recurse
$assetsBackupRoot = Join-Path $baselineRoot 'assets\Assets'
$projectSettingsBackup = Join-Path $baselineRoot 'project-settings\ProjectSettings'
$sampleSourceManifest = Get-TreeManifest (Join-Path $repo $sourceSample) @('.')
[IO.File]::WriteAllText((Join-Path $run 'sample-source-baseline.json'), ($sampleSourceManifest | ConvertTo-Json -Depth 4), [Text.Encoding]::ASCII)
$manifestPathBefore = (Get-FileHash -LiteralPath (Join-Path $project 'Packages\manifest.json') -Algorithm SHA256).Hash
$lockPathBefore = (Get-FileHash -LiteralPath (Join-Path $project 'Packages\packages-lock.json') -Algorithm SHA256).Hash
$geometryStore = Join-Path $project 'UserSettings\ViewWeaveDisplayGeometry.asset'
$geometryStoreMeta = $geometryStore + '.meta'
$geometryStoreExisted = Test-Path -LiteralPath $geometryStore
$geometryStoreMetaExisted = Test-Path -LiteralPath $geometryStoreMeta
if ($geometryStoreExisted) { Copy-Item -LiteralPath $geometryStore -Destination (Join-Path $baselineRoot 'ViewWeaveDisplayGeometry.asset') }
if ($geometryStoreMetaExisted) { Copy-Item -LiteralPath $geometryStoreMeta -Destination (Join-Path $baselineRoot 'ViewWeaveDisplayGeometry.asset.meta') }
$geometryStoreHashBefore = if ($geometryStoreExisted) { (Get-FileHash -LiteralPath $geometryStore -Algorithm SHA256).Hash } else { $null }
$geometryStoreMetaHashBefore = if ($geometryStoreMetaExisted) { (Get-FileHash -LiteralPath $geometryStoreMeta -Algorithm SHA256).Hash } else { $null }
$packageSampleRootExisted = Test-Path -LiteralPath $packageSampleRoot
$packageSampleRootMetaExisted = Test-Path -LiteralPath ($packageSampleRoot + '.meta')
$versionSampleRootExisted = Test-Path -LiteralPath $versionSampleRoot
$versionSampleRootMetaExisted = Test-Path -LiteralPath ($versionSampleRoot + '.meta')
$originalTraceEnv = $env:LKG_NATIVE_TRACE
$helperHash = (Get-FileHash -LiteralPath $sourceHelper -Algorithm SHA256).Hash
$started = [DateTime]::UtcNow
$process = $null
$launchPid = $null
$exitCode = $null
$cleanupCompleted = $false

try {
    Copy-Item -LiteralPath $sourceHelper -Destination $helper
    $env:VW_QUICKSTART_PACKAGE = $package
    $env:VW_QUICKSTART_SAMPLE = $sampleName
    $env:VW_QUICKSTART_PIPELINE = $pipeline
    $env:VW_QUICKSTART_EXPECTED_IMPORT = $expectedImport
    $env:VW_QUICKSTART_EXPECTED_PACKAGE_ROOT = $expectedPackageRoot
    $tracePath = Join-Path $run 'native-trace.log'
    $env:LKG_NATIVE_TRACE = $tracePath

    $quotedProject = '"' + $project + '"'
    $arguments = @('-projectPath', $quotedProject, '-batchmode', '-force-d3d12', '-executeMethod', 'ViewWeave.WindowsQuickStartValidation.QuickStartValidationRunner.Start', '-logFile', $log)
    $process = Start-Process -FilePath $editor -ArgumentList $arguments -PassThru -WindowStyle Hidden
    $launchPid = $process.Id
    $finished = $process.WaitForExit(1200000)
    if (-not $finished) {
        [IO.File]::WriteAllText((Join-Path $run 'runner-status.txt'), 'Editor timeout; process left running for inspection.' + [Environment]::NewLine, [Text.Encoding]::ASCII)
        throw 'Unity Editor exceeded the 1200 second deadline; process left running for inspection.'
    }
    $process.WaitForExit()
    $exitCode = $process.ExitCode
    $editorLogText = if (Test-Path -LiteralPath $log) { Read-TextWithRetry $log } else { '' }
    $nonAscii = [regex]::Matches($editorLogText, '[^\x00-\x7F]').Count
    if ($nonAscii -gt 0) {
        [IO.File]::WriteAllText($log, [regex]::Replace($editorLogText, '[^\x00-\x7F]', '?'), [Text.Encoding]::ASCII)
        $editorLogText = [regex]::Replace($editorLogText, '[^\x00-\x7F]', '?')
    }

    $pass = $editorLogText.Contains('VW_QUICKSTART_PASS pipeline=' + $pipeline)
    $failed = $editorLogText.Contains('VW_QUICKSTART_FAILED')
    $api = [regex]::Match($editorLogText, '(?m)^\s*Version:\s*(Direct3D 12 \[level [^\]]+\])').Groups[1].Value.Trim()
    $gpu = [regex]::Match($editorLogText, '(?m)^\s*Renderer:\s*(NVIDIA GeForce RTX 3090 Ti[^\r\n]*)').Groups[1].Value.Trim()
    $sourceRecords = @(Select-String -LiteralPath $log -Pattern '^VW_QUICKSTART_SOURCE ' -ErrorAction SilentlyContinue).Count
    $nativeModule = [regex]::Match($editorLogText, '(?m)^VW_QUICKSTART_NATIVE_MODULE .*').Value
    $traceText = if (Test-Path -LiteralPath $tracePath) { [IO.File]::ReadAllText($tracePath) } else { '' }
    $modulePid = [regex]::Match($nativeModule, '\bpid=(\d+)').Groups[1].Value
    $shutdownMatches = [regex]::Matches($traceText, '(?m)^shutdown pid=(\d+) tid=(\d+) tick_ms=\d+ session_id=\d+ event=uninitialize_bridge end result=1(?:\s|$)')
    $shutdownPids = @($shutdownMatches | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $shutdownThreads = @($shutdownMatches | ForEach-Object { $_.Groups[2].Value } | Select-Object -Unique)
    $displayCountThreads = @([regex]::Matches($traceText, '(?m)^worker tid=(\d+) stage=display-count countResult=0 count=1') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $nativeSessionPids = @([regex]::Matches($traceText, '(?m)^native_session pid=(\d+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $nativeEvents = [ordered]@{
        tracePresent = (Test-Path -LiteralPath $tracePath)
        loadSucceeded = $traceText.Contains('load_end result=0')
        initializeSucceeded = $traceText.Contains('initialize_end result=0')
        oneDisplayReturned = [bool]($traceText -match 'stage=display-count countResult=0 count=1')
        uninitializeSucceeded = [bool]($traceText -match 'event=uninitialize_bridge end result=1')
        modulePid = $modulePid
        modulePidMatchesEditor = ($modulePid -eq [string]$launchPid)
        successfulShutdownPids = $shutdownPids
        successfulShutdownThreads = $shutdownThreads
        shutdownPidMatchesModule = ($shutdownPids -contains $modulePid)
        displayCountThreads = $displayCountThreads
        displayThreadMatchesShutdown = [bool](@($displayCountThreads | Where-Object { $shutdownThreads -contains $_ }).Count -gt 0)
        nativeSessionPids = $nativeSessionPids
        nativeSessionPidMatchesEditor = ($nativeSessionPids -contains [string]$launchPid)
        workerMetadataRequests = @([regex]::Matches($traceText, '(?m)^worker tid=\d+ stage=display-count countResult=0 count=1')).Count
        metadataRequestTraceObserved = [bool]($traceText -match '(?m)^worker tid=\d+ stage=display-count countResult=0 count=1')
        successfulBridgeInitializations = @([regex]::Matches($traceText, '(?m)^initialize_end result=0 ')).Count
        successfulBridgeUninitializations = @([regex]::Matches($traceText, '(?m)event=uninitialize_bridge end result=1')).Count
        pendingZero = [bool]($nativeModule -match 'pending=0(?:\s|$)')
        rawTraceSha256 = if (Test-Path -LiteralPath $tracePath) { (Get-FileHash -LiteralPath $tracePath -Algorithm SHA256).Hash } else { $null }
    }
    $events = @(Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $started.ToLocalTime(); Id = 1000, 1001 } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match ('Unity\.exe|PID:\s*' + $launchPid) }).Count
    $summary = [ordered]@{
        schema = 1; kind = $Kind.ToLowerInvariant(); pipeline = $pipeline; project = $project; sample = $sampleName
        unityVersion = '6000.6.4f1'; api = $api; gpu = $gpu; processId = $launchPid
        startedUtc = $started.ToString('o'); endedUtc = [DateTime]::UtcNow.ToString('o')
        naturalExit = $true; exitCode = $exitCode; passMarker = $pass; failureMarker = $failed
        helperSha256 = $helperHash; sourceRecords = $sourceRecords
        package = $package; packageUri = if ($Kind -eq 'Urp') { 'file:E:/LookingGlassSDK/Distribution/urp' } else { 'file:E:/LookingGlassSDK/Distribution/hdrp' }
        sourceSample = (Join-Path $repo $sourceSample); importedScene = $scene
        nativeModuleMarker = $nativeModule; nativeTracePresent = (Test-Path -LiteralPath $tracePath); nativeEvents = $nativeEvents
        nativeInitialization = if ($nativeEvents.initializeSucceeded) { 'Observed successful metadata initialization.' } else { 'Not observed as successful; inspect safe event flags.' }
        applicationCrashEvents1000_1001 = $events; editorLog = $log; nativeTrace = $tracePath
        logNonAsciiSanitized = $nonAscii; manifestSha256Before = $manifestPathBefore; packageLockSha256Before = $lockPathBefore
    }
    [IO.File]::WriteAllText((Join-Path $run 'process-summary.json'), ($summary | ConvertTo-Json -Depth 5), [Text.Encoding]::ASCII)
    if ($api -notmatch '^Direct3D 12 \[level ' -or $gpu -notmatch '^NVIDIA GeForce RTX 3090 Ti' -or $events -ne 0)
        { throw 'Quick Start Editor validation did not run on the required D3D12/GPU environment or emitted a crash event.' }
    if (-not $nativeEvents.tracePresent -or -not $nativeEvents.loadSucceeded -or -not $nativeEvents.initializeSucceeded -or
        -not $nativeEvents.uninitializeSucceeded -or
        -not $nativeEvents.modulePidMatchesEditor -or -not $nativeEvents.shutdownPidMatchesModule -or
        -not $nativeEvents.pendingZero)
        { throw 'Native module lifecycle/PID/pending gates did not pass.' }
    if ($exitCode -ne 0 -or -not $pass -or $failed) { throw 'Quick Start Editor validation did not pass all process and render gates.' }
}
catch {
    $message = [regex]::Replace($_.Exception.GetBaseException().Message, '[^\x00-\x7F]', '?')
    try { [IO.File]::WriteAllText((Join-Path $run 'runner-error.txt'), ('Runner failed: ' + $message + [Environment]::NewLine), [Text.Encoding]::ASCII) } catch {}
    throw
}
finally {
    $stillRunning = $false
    if ($process -ne $null) {
        try { $stillRunning = -not $process.HasExited } catch { $stillRunning = $true }
    }
    if (-not $stillRunning) {
        $assetsBoundary = Join-Path $project 'Assets'
        if (Test-Path -LiteralPath $helper) {
            $safeHelper = Assert-OwnedPath $helper (Join-Path $project 'Assets\Editor')
            Remove-Item -LiteralPath $safeHelper -Force
        }
        if (Test-Path -LiteralPath $helperMeta) {
            $safeMeta = Assert-OwnedPath $helperMeta (Join-Path $project 'Assets\Editor')
            Remove-Item -LiteralPath $safeMeta -Force
        }
        if (Test-Path -LiteralPath $expectedImport) {
            $safeImport = Assert-OwnedPath $expectedImport (Join-Path $project 'Assets\Samples')
            if (-not $safeImport.StartsWith((Join-Path $project 'Assets\Samples\'), [StringComparison]::OrdinalIgnoreCase)) {
                throw 'Quick Start import cleanup escaped the expected Assets/Samples boundary.'
            }
            Remove-Item -LiteralPath $safeImport -Recurse -Force
        }
        if (Test-Path -LiteralPath $expectedImportMeta) {
            Remove-Item -LiteralPath (Assert-OwnedPath $expectedImportMeta (Join-Path $project 'Assets\Samples')) -Force
        }
        if (-not $versionSampleRootExisted -and (Test-Path -LiteralPath $versionSampleRoot) -and
            @(Get-ChildItem -LiteralPath $versionSampleRoot -Force).Count -eq 0) {
            $safeVersion = Assert-OwnedPath $versionSampleRoot (Join-Path $project 'Assets\Samples')
            Remove-Item -LiteralPath $safeVersion -Force
        }
        if (-not $versionSampleRootMetaExisted -and (Test-Path -LiteralPath ($versionSampleRoot + '.meta'))) {
            Remove-Item -LiteralPath (Assert-OwnedPath ($versionSampleRoot + '.meta') (Join-Path $project 'Assets\Samples')) -Force
        }
        if (-not $packageSampleRootExisted -and (Test-Path -LiteralPath $packageSampleRoot) -and
            @(Get-ChildItem -LiteralPath $packageSampleRoot -Force).Count -eq 0) {
            $safePackageRoot = Assert-OwnedPath $packageSampleRoot (Join-Path $project 'Assets\Samples')
            Remove-Item -LiteralPath $safePackageRoot -Force
        }
        if (-not $packageSampleRootMetaExisted -and (Test-Path -LiteralPath ($packageSampleRoot + '.meta'))) {
            Remove-Item -LiteralPath (Assert-OwnedPath ($packageSampleRoot + '.meta') (Join-Path $project 'Assets\Samples')) -Force
        }
        if (-not $samplesRootMetaExisted -and (Test-Path -LiteralPath $samplesRootMeta)) {
            Remove-Item -LiteralPath (Assert-OwnedPath $samplesRootMeta (Join-Path $project 'Assets')) -Force
        }

        $assetsRoot = Join-Path $project 'Assets'
        $baselineAssetManifest = @($manifest | Where-Object { $_.path -like 'Assets/*' })
        $currentAssetManifest = Get-TreeManifest $project @('Assets')
        $baselineAssetMap = @{}; foreach ($entry in $baselineAssetManifest) { $baselineAssetMap[$entry.path] = $entry }
        $currentAssetMap = @{}; foreach ($entry in $currentAssetManifest) { $currentAssetMap[$entry.path] = $entry }
        $unexpectedAddedAssets = @($currentAssetManifest | Where-Object { -not $baselineAssetMap.ContainsKey($_.path) })
        foreach ($entry in $baselineAssetManifest) {
            $relative = $entry.path.Substring('Assets/'.Length).Replace('/', '\')
            $target = Assert-OwnedPath (Join-Path $assetsRoot $relative) $assetsRoot
            $backup = Assert-OwnedPath (Join-Path $assetsBackupRoot $relative) $assetsBackupRoot
            if (-not $currentAssetMap.ContainsKey($entry.path) -or $currentAssetMap[$entry.path].sha256 -ne $entry.sha256) {
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                Copy-Item -LiteralPath $backup -Destination $target -Force
            }
        }
        if ($unexpectedAddedAssets.Count -gt 0) {
            $unexpectedPaths = @($unexpectedAddedAssets | ForEach-Object { $_.path })
            [IO.File]::WriteAllText((Join-Path $run 'unexpected-added-assets.txt'), ($unexpectedPaths -join [Environment]::NewLine) + [Environment]::NewLine, [Text.Encoding]::ASCII)
        }

        $projectSettingsRoot = Join-Path $project 'ProjectSettings'
        $baselineSettingsManifest = @($manifest | Where-Object { $_.path -like 'ProjectSettings/*' })
        $currentSettingsManifest = Get-TreeManifest $project @('ProjectSettings')
        $baselineMap = @{}; foreach ($entry in $baselineSettingsManifest) { $baselineMap[$entry.path] = $entry }
        $currentMap = @{}; foreach ($entry in $currentSettingsManifest) { $currentMap[$entry.path] = $entry }
        foreach ($path in $baselineMap.Keys) {
            $relative = $path.Substring('ProjectSettings/'.Length).Replace('/', '\')
            $target = Assert-OwnedPath (Join-Path $projectSettingsRoot $relative) $projectSettingsRoot
            $backup = Join-Path $projectSettingsBackup $relative
            if (-not $currentMap.ContainsKey($path) -or $currentMap[$path].sha256 -ne $baselineMap[$path].sha256) {
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                Copy-Item -LiteralPath $backup -Destination $target -Force
            }
        }
        foreach ($entry in $currentSettingsManifest) {
            if (-not $baselineMap.ContainsKey($entry.path)) {
                $relative = $entry.path.Substring('ProjectSettings/'.Length).Replace('/', '\')
                $target = Assert-OwnedPath (Join-Path $projectSettingsRoot $relative) $projectSettingsRoot
                Remove-Item -LiteralPath $target -Force
            }
        }

        if ($geometryStoreExisted) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $geometryStore) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $baselineRoot 'ViewWeaveDisplayGeometry.asset') -Destination $geometryStore -Force
        } elseif (Test-Path -LiteralPath $geometryStore) { Remove-Item -LiteralPath $geometryStore -Force }
        if ($geometryStoreMetaExisted) {
            New-Item -ItemType Directory -Path (Split-Path -Parent $geometryStoreMeta) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $baselineRoot 'ViewWeaveDisplayGeometry.asset.meta') -Destination $geometryStoreMeta -Force
        } elseif (Test-Path -LiteralPath $geometryStoreMeta) { Remove-Item -LiteralPath $geometryStoreMeta -Force }

        $finalManifest = Get-TreeManifest $project @('Assets', 'ProjectSettings')
        $expectedManifest = @($manifest)
        $differences = Compare-Object -ReferenceObject $expectedManifest -DifferenceObject $finalManifest -Property path, bytes, sha256
        $geometryStoreRestored = if ($geometryStoreExisted) { (Test-Path -LiteralPath $geometryStore) -and (Get-FileHash -LiteralPath $geometryStore -Algorithm SHA256).Hash -eq $geometryStoreHashBefore } else { -not (Test-Path -LiteralPath $geometryStore) }
        $geometryMetaRestored = if ($geometryStoreMetaExisted) { (Test-Path -LiteralPath $geometryStoreMeta) -and (Get-FileHash -LiteralPath $geometryStoreMeta -Algorithm SHA256).Hash -eq $geometryStoreMetaHashBefore } else { -not (Test-Path -LiteralPath $geometryStoreMeta) }
        $baselineResult = [ordered]@{ expectedAssetProjectSettingFiles = $expectedManifest.Count; finalAssetProjectSettingFiles = $finalManifest.Count; differences = @($differences | ForEach-Object { [ordered]@{ side = $_.SideIndicator; path = $_.path; bytes = $_.bytes; sha256 = $_.sha256 } }); restoredProjectSettings = $true; geometryStoreRestored = $geometryStoreRestored; geometryStoreMetaRestored = $geometryMetaRestored; samplesRootMetaRestored = ($samplesRootMetaExisted -or -not (Test-Path -LiteralPath $samplesRootMeta)); ownedHelperRemoved = -not (Test-Path -LiteralPath $helper) -and -not (Test-Path -LiteralPath $helperMeta); importedSampleRemoved = -not (Test-Path -LiteralPath $expectedImport) -and -not (Test-Path -LiteralPath $expectedImportMeta); launchedEditorPid = $launchPid; editorNaturalExitCode = $exitCode; launchedEditorPidAbsent = -not $stillRunning }
        [IO.File]::WriteAllText((Join-Path $run 'cleanup-audit.json'), ($baselineResult | ConvertTo-Json -Depth 6), [Text.Encoding]::ASCII)
        $cleanupCompleted = $baselineResult.ownedHelperRemoved -and $baselineResult.importedSampleRemoved -and $baselineResult.samplesRootMetaRestored -and $baselineResult.geometryStoreRestored -and $baselineResult.geometryStoreMetaRestored -and $unexpectedAddedAssets.Count -eq 0 -and $differences.Count -eq 0
        if (-not $cleanupCompleted) { throw 'Quick Start cleanup/baseline gate failed; see cleanup-audit.json.' }
        $manifestAfter = (Get-FileHash -LiteralPath (Join-Path $project 'Packages\manifest.json') -Algorithm SHA256).Hash
        $lockAfter = (Get-FileHash -LiteralPath (Join-Path $project 'Packages\packages-lock.json') -Algorithm SHA256).Hash
        if ($manifestAfter -ne $manifestPathBefore -or $lockAfter -ne $lockPathBefore) { throw 'Project manifest or package lock changed during sample validation.' }
    }
    Remove-Item Env:VW_QUICKSTART_PACKAGE -ErrorAction SilentlyContinue
    Remove-Item Env:VW_QUICKSTART_SAMPLE -ErrorAction SilentlyContinue
    Remove-Item Env:VW_QUICKSTART_PIPELINE -ErrorAction SilentlyContinue
    Remove-Item Env:VW_QUICKSTART_EXPECTED_IMPORT -ErrorAction SilentlyContinue
    Remove-Item Env:VW_QUICKSTART_EXPECTED_PACKAGE_ROOT -ErrorAction SilentlyContinue
    if ($null -eq $originalTraceEnv) { Remove-Item Env:LKG_NATIVE_TRACE -ErrorAction SilentlyContinue } else { $env:LKG_NATIVE_TRACE = $originalTraceEnv }
}

if (-not $cleanupCompleted) { throw 'Cleanup gate did not complete.' }
Write-Output ('QUICKSTART_VALIDATION_PASS kind=' + $Kind.ToLowerInvariant() + ' project=' + $project + ' evidence=' + $run)
