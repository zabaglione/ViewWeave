[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$EvidenceRoot,
    [int]$PlayerTimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..\..')).Path
$project = Join-Path $repo 'Tests\Pipelines\Unity'
$editor = 'C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe'
$evidence = [IO.Path]::GetFullPath($EvidenceRoot)
$runEvidence = Join-Path $evidence 'player-evidence'
$baselineRoot = Join-Path $evidence 'baseline'
$projectSettings = Join-Path $project 'ProjectSettings'
$userSettings = Join-Path $project 'UserSettings'
$geometryCache = Join-Path $userSettings 'ViewWeaveDisplayGeometry.asset'
$geometryCacheMeta = $geometryCache + '.meta'
$projectSettingsBackup = Join-Path $baselineRoot 'ProjectSettings'
$generatedRoot = Join-Path $project 'Assets\WindowsRenderOnlyPlayerValidationGenerated'
$generatedRootMeta = $generatedRoot + '.meta'
$generatedBackup = Join-Path $baselineRoot 'GeneratedAssets'
$generatedMetaBackup = Join-Path $baselineRoot 'WindowsRenderOnlyPlayerValidationGenerated.meta'
$geometryCacheBackup = Join-Path $baselineRoot 'UserSettings\ViewWeaveDisplayGeometry.asset'
$geometryCacheMetaBackup = Join-Path $baselineRoot 'UserSettings\ViewWeaveDisplayGeometry.asset.meta'
$editorHelper = Join-Path $project 'Assets\Editor\WindowsRenderOnlyPlayerValidationBuilder.cs'
$editorHelperMeta = $editorHelper + '.meta'
$runtimeRoot = Join-Path $project 'Assets\WindowsRenderOnlyPlayerValidationRuntime'
$runtimeHelper = Join-Path $runtimeRoot 'WindowsRenderOnlyPlayerValidationRunner.cs'
$runtimeRootMeta = $runtimeRoot + '.meta'
$playerName = 'WindowsRenderOnlyPlayerValidation.exe'
$buildRoot = Join-Path $evidence 'Build'
$playerPath = Join-Path $buildRoot $playerName
$buildLog = Join-Path $evidence 'unity-build.log'
$playerLog = Join-Path $evidence 'player.log'
$nativeTrace = Join-Path $evidence 'native-trace.log'
$builderSource = Join-Path $PSScriptRoot 'WindowsRenderOnlyPlayerValidation\WindowsRenderOnlyPlayerValidationBuilder.cs'
$runtimeSource = Join-Path $PSScriptRoot 'WindowsRenderOnlyPlayerValidation\WindowsRenderOnlyPlayerValidationRunner.cs'
$globalAssets = @(
    'Assets/HDRPDefaultResources/HDRenderPipelineGlobalSettings.asset',
    'Assets/HDRPDefaultResources/HDRenderPipelineGlobalSettings.asset.meta',
    'Assets/HDRPDefaultResources/DefaultSettingsVolumeProfile.asset',
    'Assets/HDRPDefaultResources/DefaultSettingsVolumeProfile.asset.meta'
)
$builderMethod = 'ViewWeave.WindowsRenderOnlyPlayerValidation.Editor.WindowsRenderOnlyPlayerValidationBuilder.BuildHdrpD3D12Release'
$manifestBefore = @()
$settingsBefore = @()
$pipelineBefore = @()
$editorProcess = $null
$playerProcess = $null
$snapshotReady = $false
$evidenceOwned = $false
$buildExitCode = $null
$playerExitCode = $null
$playerNaturalExit = $false
$processPreflightMethod = 'CIM'
$playerTimedOut = $false
$playerDeadlineReached = $false
$generatedExisted = $false
$generatedBefore = @()
$generatedMetaExisted = $false
$geometryCacheExisted = $false
$geometryCacheMetaExisted = $false
$geometryCacheHashBefore = $null
$geometryCacheMetaHashBefore = $null
$manifestShaBefore = $null
$lockShaBefore = $null

function Assert-Within([string]$Path, [string]$Root) {
    $full = [IO.Path]::GetFullPath($Path)
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    if (-not $full.StartsWith($rootFull, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escaped its approved root.' }
    return $full
}

function Hash-File([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

function Get-RelativePath([string]$Root, [string]$Path) {
    $rootPrefix = [IO.Path]::GetFullPath($Root).TrimEnd('\') + '\'
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path is outside its protected root.' }
    return $fullPath.Substring($rootPrefix.Length)
}

function Quote-ProcessArgument([string]$Value) {
    if ($null -eq $Value) { $Value = '' }
    $builder = [Text.StringBuilder]::new()
    [void]$builder.Append('"')
    $slashes = 0
    for ($index = 0; $index -lt $Value.Length; $index++) {
        $character = $Value[$index]
        if ($character -eq '\') { $slashes++; continue }
        if ($character -eq '"') {
            [void]$builder.Append(('\' * (2 * $slashes + 1)))
            [void]$builder.Append('"')
        } else {
            if ($slashes -gt 0) { [void]$builder.Append(('\' * $slashes)) }
            [void]$builder.Append($character)
        }
        $slashes = 0
    }
    if ($slashes -gt 0) { [void]$builder.Append(('\' * (2 * $slashes))) }
    [void]$builder.Append('"')
    return $builder.ToString()
}

function Get-TreeManifest([string]$Root, [string[]]$RelativeRoots) {
    $records = [Collections.Generic.List[object]]::new()
    foreach ($relativeRoot in $RelativeRoots) {
        $absoluteRoot = Join-Path $Root $relativeRoot
        if (-not (Test-Path -LiteralPath $absoluteRoot)) { continue }
        foreach ($file in Get-ChildItem -LiteralPath $absoluteRoot -Recurse -File -Force) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point found in protected tree.' }
            $records.Add([pscustomobject]@{
                path = (Get-RelativePath $Root $file.FullName).Replace('\', '/')
                bytes = $file.Length
                sha256 = Hash-File $file.FullName
            })
        }
    }
    return @($records | Sort-Object path)
}

function Copy-TreeSnapshot([string]$Source, [string]$Destination) {
    $records = @()
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) { return $records }
    foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point found in protected tree.' }
        $relative = Get-RelativePath $Source $file.FullName
        $copy = Join-Path $Destination $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $copy) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $copy -Force
        $records += [pscustomobject]@{ path = $relative.Replace('\', '/'); bytes = $file.Length; sha256 = Hash-File $file.FullName }
    }
    return $records
}

function Restore-TreeSnapshot([string]$Target, [string]$Backup, [object[]]$Records, [string]$Boundary) {
    if (-not (Test-Path -LiteralPath $Target -PathType Container) -and $Records.Count -eq 0) { return }
    if (-not (Test-Path -LiteralPath $Target -PathType Container)) { New-Item -ItemType Directory -Path $Target -Force | Out-Null }
    $allowed = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $Records) { [void]$allowed.Add($record.path.Replace('/', '\')) }
    foreach ($file in @(Get-ChildItem -LiteralPath $Target -Recurse -File -Force)) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Reparse point found during protected tree restore.' }
        $relative = Get-RelativePath $Target $file.FullName
        if (-not $allowed.Contains($relative)) { Remove-Item -LiteralPath (Assert-Within $file.FullName $Boundary) -Force }
    }
    foreach ($record in $Records) {
        $destination = Join-Path $Target ($record.path.Replace('/', '\'))
        $backupFile = Join-Path $Backup ($record.path.Replace('/', '\'))
        if (-not (Test-Path -LiteralPath $backupFile -PathType Leaf)) { throw 'A protected baseline file is missing.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $backupFile -Destination (Assert-Within $destination $Boundary) -Force
    }
    foreach ($directory in @(Get-ChildItem -LiteralPath $Target -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending)) {
        if (@(Get-ChildItem -LiteralPath $directory.FullName -Force).Count -eq 0) {
            Remove-Item -LiteralPath (Assert-Within $directory.FullName $Boundary) -Force
        }
    }
}

function Write-AsciiJson([string]$Path, [object]$Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 12) + "`r`n", [Text.Encoding]::ASCII)
}

function Read-TextWithRetry([string]$Path, [int]$Attempts = 20) {
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try { return [IO.File]::ReadAllText($Path) }
        catch [IO.IOException] {
            if ($attempt -eq $Attempts) { throw }
            Start-Sleep -Milliseconds 500
        }
    }
    throw 'Unity log could not be read after bounded retry.'
}

function Get-AsciiText([string]$Path) {
    $value = Read-TextWithRetry $Path
    return [regex]::Replace($value, '[^\x00-\x7F]', '?')
}

function Write-RunnerError([string]$Message) {
    $safe = [regex]::Replace($Message, '[^\x00-\x7F]', '?')
    try { [IO.File]::WriteAllText((Join-Path $evidence 'runner-error.txt'), ('Runner failed: ' + $safe + "`r`n"), [Text.Encoding]::ASCII) } catch {}
}

function Get-ApplicationCrashEvents([datetime]$StartUtc, [datetime]$EndUtc, [string]$ProcessName, [int]$ProcessId) {
    $events = @()
    try {
        foreach ($event in Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $StartUtc.ToLocalTime(); EndTime = $EndUtc.ToLocalTime(); Id = 1000, 1001 } -ErrorAction SilentlyContinue) {
            $message = [regex]::Replace([string]$event.Message, '[^\x00-\x7F]', '?')
            if ($message -match [regex]::Escape($ProcessName) -or $message -match ('PID:\s*' + $ProcessId)) {
                $events += [pscustomobject]@{
                    timeUtc = $event.TimeCreated.ToUniversalTime().ToString('o')
                    recordId = $event.RecordId
                    id = $event.Id
                    provider = [regex]::Replace([string]$event.ProviderName, '[^\x00-\x7F]', '?')
                    message = $message
                }
            }
        }
    } catch {}
    return $events
}

try {
    if ($PlayerTimeoutSeconds -lt 1) { throw 'PlayerTimeoutSeconds must be positive.' }
    Assert-Within $evidence (Join-Path $repo '.local') | Out-Null
    if (Test-Path -LiteralPath $evidence) { throw 'EvidenceRoot already exists; use a fresh directory.' }
    foreach ($required in @($project, $editor, $builderSource, $runtimeSource, (Join-Path $project 'Assets\Editor'))) {
        if (-not (Test-Path -LiteralPath $required)) { throw 'Required project, editor, source, or Assets/Editor path is missing.' }
    }
    if ((Test-Path -LiteralPath $editorHelper) -or (Test-Path -LiteralPath $editorHelperMeta)) { throw 'Temporary Editor helper exists; refusing to overwrite it.' }
    if (Test-Path -LiteralPath $runtimeRoot) { throw 'Temporary runtime helper folder exists; refusing to overwrite it.' }
    if (Test-Path -LiteralPath $runtimeRootMeta) { throw 'Temporary runtime helper metadata exists; refusing to overwrite it.' }
    if (Test-Path -LiteralPath $playerPath) { throw 'Player build output already exists.' }

    try {
        $unityProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object {
            $_.ExecutablePath -and [StringComparer]::OrdinalIgnoreCase.Equals($_.ExecutablePath, $editor)
        })
        $playerProcesses = @(Get-CimInstance Win32_Process -ErrorAction Stop | Where-Object { $_.Name -eq $playerName })
    } catch {
        $processPreflightMethod = 'Get-Process-name-fallback'
        $runningProcesses = @(Get-Process -ErrorAction Stop)
        $unityProcesses = @($runningProcesses | Where-Object { $_.ProcessName -match '^Unity($|Editor$)' })
        $playerBaseName = [IO.Path]::GetFileNameWithoutExtension($playerName)
        $playerProcesses = @($runningProcesses | Where-Object { [StringComparer]::OrdinalIgnoreCase.Equals($_.ProcessName, $playerBaseName) })
    }
    if ($unityProcesses.Count -gt 0) { throw 'Unity Editor is already running; refusing to interfere.' }
    if ($playerProcesses.Count -gt 0) { throw 'A render-only validation Player is already running.' }

    New-Item -ItemType Directory -Path $evidence, $baselineRoot, $projectSettingsBackup, $generatedBackup, $runEvidence, $buildRoot -Force | Out-Null
    $evidenceOwned = $true
    $crashPreflightEndUtc = [DateTime]::UtcNow
    $crashPreflightStartUtc = $crashPreflightEndUtc.AddMinutes(-15)
    $crashPreflightEvents = @(Get-ApplicationCrashEvents $crashPreflightStartUtc $crashPreflightEndUtc 'Unity.exe' 0)
    Write-AsciiJson (Join-Path $evidence 'editor-crash-events-preflight.json') ([ordered]@{
        queriedUtc = $crashPreflightEndUtc.ToString('o')
        startUtc = $crashPreflightStartUtc.ToString('o')
        endUtc = $crashPreflightEndUtc.ToString('o')
        log = 'Application'; eventIds = @(1000, 1001); processName = 'Unity.exe'; processId = 'not started'; matchingEvents = $crashPreflightEvents
    })
    $manifestBefore = Get-TreeManifest $project @('Assets', 'ProjectSettings')
    Write-AsciiJson (Join-Path $baselineRoot 'assets-projectsettings-manifest.json') $manifestBefore
    $settingsBefore = Copy-TreeSnapshot $projectSettings $projectSettingsBackup
    $generatedExisted = Test-Path -LiteralPath $generatedRoot -PathType Container
    $generatedBefore = Copy-TreeSnapshot $generatedRoot $generatedBackup
    $generatedMetaExisted = Test-Path -LiteralPath $generatedRootMeta -PathType Leaf
    if ($generatedMetaExisted) { Copy-Item -LiteralPath $generatedRootMeta -Destination $generatedMetaBackup -Force }
    $geometryCacheExisted = Test-Path -LiteralPath $geometryCache -PathType Leaf
    if ($geometryCacheExisted) {
        $geometryCacheHashBefore = Hash-File $geometryCache
        New-Item -ItemType Directory -Path (Split-Path -Parent $geometryCacheBackup) -Force | Out-Null
        Copy-Item -LiteralPath $geometryCache -Destination $geometryCacheBackup -Force
    }
    $geometryCacheMetaExisted = Test-Path -LiteralPath $geometryCacheMeta -PathType Leaf
    if ($geometryCacheMetaExisted) {
        $geometryCacheMetaHashBefore = Hash-File $geometryCacheMeta
        New-Item -ItemType Directory -Path (Split-Path -Parent $geometryCacheMetaBackup) -Force | Out-Null
        Copy-Item -LiteralPath $geometryCacheMeta -Destination $geometryCacheMetaBackup -Force
    }

    $pipelineBefore = @()
    foreach ($relative in $globalAssets) {
        $path = Join-Path $project ($relative.Replace('/', '\'))
        $exists = Test-Path -LiteralPath $path -PathType Leaf
        $record = [ordered]@{ path = $relative; exists = $exists; bytes = 0; sha256 = $null }
        if ($exists) {
            $record.bytes = (Get-Item -LiteralPath $path).Length
            $record.sha256 = Hash-File $path
            $backup = Join-Path $baselineRoot ($relative.Replace('/', '\'))
            New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
            Copy-Item -LiteralPath $path -Destination $backup -Force
        }
        $pipelineBefore += [pscustomobject]$record
    }
    $pipelineMetadata = @{}
    foreach ($relative in $globalAssets) { $pipelineMetadata[$relative] = $true }
    $manifestShaBefore = Hash-File (Join-Path $project 'Packages\manifest.json')
    $lockShaBefore = Hash-File (Join-Path $project 'Packages\packages-lock.json')
    $sourceHashes = @(
        [pscustomobject]@{ path = 'builder'; sha256 = Hash-File $builderSource },
        [pscustomobject]@{ path = 'runtime'; sha256 = Hash-File $runtimeSource },
        [pscustomobject]@{ path = 'runner'; sha256 = Hash-File $PSCommandPath }
    )
    $productHeadSha = (& git -C $repo rev-parse HEAD | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $productHeadSha -notmatch '^[0-9a-fA-F]{40}$') { throw 'Could not record the product HEAD fingerprint.' }
    $packageManifest = Get-Content -LiteralPath (Join-Path $project 'Packages\manifest.json') -Raw | ConvertFrom-Json
    $packageSourceRecords = [Collections.Generic.List[object]]::new()
    foreach ($packageName in @(
        'com.zabaglione.lookingglass.viewweave.bridge',
        'com.zabaglione.lookingglass.viewweave.core',
        'com.zabaglione.lookingglass.viewweave.hdrp',
        'com.zabaglione.lookingglass.viewweave.urp'
    )) {
        $dependency = $packageManifest.dependencies.PSObject.Properties[$packageName].Value
        if (-not ($dependency -is [string]) -or -not $dependency.StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A required ViewWeave package does not use a local file dependency.'
        }
        $packagePath = [IO.Path]::GetFullPath((Join-Path (Join-Path $project 'Packages') $dependency.Substring(5).Replace('/', '\')))
        if (-not (Test-Path -LiteralPath $packagePath -PathType Container)) { throw 'A resolved local ViewWeave package path is missing.' }
        foreach ($record in (Get-TreeManifest $packagePath @(''))) {
            $packageSourceRecords.Add([pscustomobject]@{ package = $packageName; path = $record.path; bytes = $record.bytes; sha256 = $record.sha256 })
        }
    }
    $packageSourceRecords = @($packageSourceRecords | Sort-Object package, path)
    $inputManifest = [ordered]@{ processPreflightMethod = $processPreflightMethod; sources = $sourceHashes; localPackages = $packageSourceRecords }
    $inputJson = ConvertTo-Json -InputObject $inputManifest -Depth 8 -Compress
    $inputHashAlgorithm = [Security.Cryptography.SHA256]::Create()
    try { $inputsSha256 = ([BitConverter]::ToString($inputHashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($inputJson)))).Replace('-', '') }
    finally { $inputHashAlgorithm.Dispose() }
    Write-AsciiJson (Join-Path $evidence 'inputs-manifest.json') ([ordered]@{ inputsSha256 = $inputsSha256; productHeadSha = $productHeadSha; sourceManifest = $inputManifest })
    $snapshotReady = $true

    Copy-Item -LiteralPath $builderSource -Destination $editorHelper
    New-Item -ItemType Directory -Path $runtimeRoot -Force | Out-Null
    Copy-Item -LiteralPath $runtimeSource -Destination $runtimeHelper

    $editorArgs = @(
        '-batchmode', '-quit', '-force-d3d12', '-projectPath', $project,
        '-executeMethod', $builderMethod,
        '-buildOutput', $playerPath,
        '-buildGraphicsApi', 'd3d12',
        '-logFile', $buildLog
    )
    $editorInfo = [Diagnostics.ProcessStartInfo]::new()
    $editorInfo.FileName = $editor
    $editorInfo.WorkingDirectory = $project
    $editorInfo.UseShellExecute = $false
    $editorInfo.CreateNoWindow = $true
    $editorInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $editorInfo.RedirectStandardOutput = $true
    $editorInfo.RedirectStandardError = $true
    $editorInfo.EnvironmentVariables['LKG_NATIVE_TRACE'] = ''
    $editorInfo.Arguments = (@($editorArgs | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' ')
    $buildStartedUtc = [DateTime]::UtcNow
    $editorProcess = [Diagnostics.Process]::new()
    $editorProcess.StartInfo = $editorInfo
    if (-not $editorProcess.Start()) { throw 'Unity Editor did not start the HDRP Player build.' }
    $editorStdout = $editorProcess.StandardOutput.ReadToEndAsync()
    $editorStderr = $editorProcess.StandardError.ReadToEndAsync()
    if (-not $editorProcess.WaitForExit(1200000)) {
        try { $editorProcess.Kill($true) } catch { try { $editorProcess.Kill() } catch {} }
        [void]$editorProcess.WaitForExit(10000)
        throw 'Unity HDRP Player build exceeded its 1200-second deadline.'
    }
    $editorProcess.WaitForExit()
    $buildExitCode = $editorProcess.ExitCode
    $buildEndedUtc = [DateTime]::UtcNow
    $editorCrashEvents = @(Get-ApplicationCrashEvents $buildStartedUtc $buildEndedUtc 'Unity.exe' $editorProcess.Id)
    Write-AsciiJson (Join-Path $evidence 'editor-crash-events.json') ([ordered]@{
        startUtc = $buildStartedUtc.ToString('o')
        endUtc = $buildEndedUtc.ToString('o')
        log = 'Application'; eventIds = @(1000, 1001); processName = 'Unity.exe'; processId = $editorProcess.Id; matchingEvents = $editorCrashEvents
    })
    $editorStdout.Wait(); $editorStderr.Wait()
    [IO.File]::WriteAllText((Join-Path $evidence 'unity-build.stdout.txt'), [regex]::Replace([string]$editorStdout.Result, '[^\x00-\x7F]', '?'), [Text.Encoding]::ASCII)
    [IO.File]::WriteAllText((Join-Path $evidence 'unity-build.stderr.txt'), [regex]::Replace([string]$editorStderr.Result, '[^\x00-\x7F]', '?'), [Text.Encoding]::ASCII)
    $buildLogText = if (Test-Path -LiteralPath $buildLog) { Get-AsciiText $buildLog } else { '' }
    if (Test-Path -LiteralPath $buildLog) { [IO.File]::WriteAllText($buildLog, $buildLogText, [Text.Encoding]::ASCII) }
    $buildSummary = [ordered]@{
        startedUtc = $buildStartedUtc.ToString('o'); endedUtc = $buildEndedUtc.ToString('o')
        editorPid = $editorProcess.Id; naturalExit = $true; exitCode = $buildExitCode
        processPreflightMethod = $processPreflightMethod
        pipeline = 'hdrp'; graphicsApi = 'Direct3D12'; buildKind = 'Release'; development = $false
        playerPath = $playerPath; playerExists = Test-Path -LiteralPath $playerPath -PathType Leaf
        builderMethod = $builderMethod; sourceHashes = $sourceHashes; inputsSha256 = $inputsSha256; productHeadSha = $productHeadSha; buildLog = $buildLog
    }
    Write-AsciiJson (Join-Path $evidence 'build-summary.json') $buildSummary
    if ($buildExitCode -ne 0 -or -not (Test-Path -LiteralPath $playerPath -PathType Leaf)) { throw 'HDRP D3D12 Release Player build failed.' }
    if ($buildLogText -notmatch 'WINDOWS_RENDER_ONLY_PLAYER_BUILD_PASS api=Direct3D12 development=false pipeline=HDRP') { throw 'Unity build log has no fixture success marker.' }

    $playerArgs = @('--evidence-dir', $runEvidence, '-logFile', $playerLog)
    $playerInfo = [Diagnostics.ProcessStartInfo]::new()
    $playerInfo.FileName = $playerPath
    $playerInfo.WorkingDirectory = $buildRoot
    $playerInfo.UseShellExecute = $false
    $playerInfo.CreateNoWindow = $true
    $playerInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $playerInfo.RedirectStandardOutput = $true
    $playerInfo.RedirectStandardError = $true
    $playerInfo.Arguments = (@($playerArgs | ForEach-Object { Quote-ProcessArgument ([string]$_) }) -join ' ')
    $playerInfo.EnvironmentVariables['LKG_NATIVE_TRACE'] = $nativeTrace
    $playerStartUtc = [DateTime]::UtcNow
    $playerProcess = [Diagnostics.Process]::new()
    $playerProcess.StartInfo = $playerInfo
    if (-not $playerProcess.Start()) { throw 'HDRP render-only Player did not start.' }
    $playerStdout = $playerProcess.StandardOutput.ReadToEndAsync()
    $playerStderr = $playerProcess.StandardError.ReadToEndAsync()

    $moduleObservations = @()
    $moduleObservationFailures = 0
    $deadline = [DateTime]::UtcNow.AddSeconds($PlayerTimeoutSeconds)
    while (-not $playerProcess.HasExited -and [DateTime]::UtcNow -lt $deadline) {
        try {
            foreach ($module in (Get-Process -Id $playerProcess.Id -Module -ErrorAction Stop)) {
                if ($module.ModuleName -match 'lookingglass_bridge_shim|bridge_inproc') {
                    $path = [IO.Path]::GetFullPath($module.FileName)
                    $observation = [pscustomobject]@{ name = $module.ModuleName; path = $path; sha256 = Hash-File $path }
                    if (-not ($moduleObservations | Where-Object { $_.path -eq $observation.path -and $_.sha256 -eq $observation.sha256 })) {
                        $moduleObservations += $observation
                    }
                }
            }
        } catch { $moduleObservationFailures++ }
        Start-Sleep -Milliseconds 100
        $playerProcess.Refresh()
    }
    $playerDeadlineReached = [DateTime]::UtcNow -ge $deadline -and -not $playerProcess.HasExited
    $playerExited = $playerProcess.WaitForExit(10000)
    $playerTimedOut = $playerDeadlineReached -or -not $playerExited
    if (-not $playerExited) {
        try { $playerProcess.Kill($true) } catch { try { $playerProcess.Kill() } catch {} }
        $playerExited = $playerProcess.WaitForExit(10000)
    }
    $playerNaturalExit = $playerExited -and -not $playerTimedOut
    if ($playerExited) {
        $playerProcess.WaitForExit()
        $playerExitCode = $playerProcess.ExitCode
        $playerStdout.Wait(); $playerStderr.Wait()
        [IO.File]::WriteAllText((Join-Path $evidence 'player.stdout.txt'), [regex]::Replace([string]$playerStdout.Result, '[^\x00-\x7F]', '?'), [Text.Encoding]::ASCII)
        [IO.File]::WriteAllText((Join-Path $evidence 'player.stderr.txt'), [regex]::Replace([string]$playerStderr.Result, '[^\x00-\x7F]', '?'), [Text.Encoding]::ASCII)
    }
    $playerEndUtc = [DateTime]::UtcNow
    $playerLogText = if (Test-Path -LiteralPath $playerLog) { Get-AsciiText $playerLog } else { '' }
    if (Test-Path -LiteralPath $playerLog) { [IO.File]::WriteAllText($playerLog, $playerLogText, [Text.Encoding]::ASCII) }
    $nativeTracePresent = Test-Path -LiteralPath $nativeTrace -PathType Leaf
    $nativeTraceText = if ($nativeTracePresent) { Get-AsciiText $nativeTrace } else { '' }
    if ($nativeTracePresent) { [IO.File]::WriteAllText($nativeTrace, $nativeTraceText, [Text.Encoding]::ASCII) }
    $nativeInitializeMatches = [regex]::Matches($nativeTraceText, '(?m)^initialize_end result=0(?:\s|$)')
    $nativeShutdownMatches = [regex]::Matches($nativeTraceText, '(?m)^shutdown pid=(\d+) tid=(\d+) tick_ms=\d+ session_id=\d+ event=uninitialize_bridge end result=1(?:\s|$)')
    $nativeTracePids = @([regex]::Matches($nativeTraceText, '(?m)^(?:native_session|shutdown) pid=(\d+)') | ForEach-Object { $_.Groups[1].Value } | Select-Object -Unique)
    $nativeEvents = [ordered]@{
        traceConfigured = $true; tracePresent = $nativeTracePresent
        loadSucceeded = $nativeTraceText.Contains('load_end result=0')
        successfulInitializations = $nativeInitializeMatches.Count
        successfulUninitializations = $nativeShutdownMatches.Count
        initializationStatus = if ($nativeInitializeMatches.Count -gt 0) { 'observed' } elseif ($nativeTracePresent) { 'not_observed_in_trace' } else { 'trace_file_not_created' }
        traceProcessPid = $playerProcess.Id; traceEventPids = $nativeTracePids
        tracePidsMatchPlayer = [bool](@($nativeTracePids | Where-Object { $_ -ne [string]$playerProcess.Id }).Count -eq 0)
        tracePath = $nativeTrace
        traceSha256 = if ($nativeTracePresent) { Hash-File $nativeTrace } else { $null }
    }
    $runtimeSummaryPath = Join-Path $runEvidence 'summary.json'
    $runtimeSummary = if (Test-Path -LiteralPath $runtimeSummaryPath) { Get-Content -LiteralPath $runtimeSummaryPath -Raw | ConvertFrom-Json } else { $null }
    $crashEvents = @(Get-ApplicationCrashEvents $playerStartUtc $playerEndUtc $playerName $playerProcess.Id)
    $privateViewRecords = if ($null -ne $runtimeSummary) { @($runtimeSummary.viewCallbackRecords | Where-Object { $_.logicalView -ge 0 }) } else { @() }
    $validPrivateViews = @($privateViewRecords | Where-Object {
        $_.captureSucceeded -and $_.cameraTargetWidth -gt 0 -and $_.cameraTargetHeight -gt 0 -and $_.preCopyImagePath
    } | Select-Object -ExpandProperty logicalView -Unique)
    $validEffectiveEv4Bloom0Views = @($privateViewRecords | Where-Object {
        $_.captureSucceeded -and $_.effectiveExposure -match '^True:Fixed:4(?:\.0+)?:0(?:\.0+)?$' -and
        $_.effectiveBloom -match '^True:0(?:\.0+)?$'
    } | Select-Object -ExpandProperty logicalView -Unique)
    $validEffectiveVolumeControlViews = @($privateViewRecords | Where-Object {
        $_.captureSucceeded -and $_.hdrpVolumeStackValid -and $_.effectiveVolumeControlsMatch -and
        $_.effectiveVisualEnvironment -eq 'True:0' -and $_.effectiveCloudType -eq 'True:0' -and
        $_.effectiveIndirectLighting -match '^True:0(?:\.0+)?$' -and $_.effectiveTonemapping -eq 'True:Neutral'
    } | Select-Object -ExpandProperty logicalView -Unique)
    $viewDiagnosticGate = $validPrivateViews.Count -eq 4 -and $validEffectiveEv4Bloom0Views.Count -eq 4 -and
        $validEffectiveVolumeControlViews.Count -eq 4
    $requiredLightStages = @(
        'startup', 'before-direct-render', 'after-direct-render',
        'before-quilt-readiness', 'after-quilt-readiness',
        'before-quilt-before', 'after-quilt-before', 'after-quilt-before-capture',
        'before-quilt-after', 'after-quilt-after', 'after-movement-capture'
    )
    $lightStageRecords = if ($null -ne $runtimeSummary) { @($runtimeSummary.fixtureLightStages) } else { @() }
    $validLightStages = @($lightStageRecords | Where-Object {
        $_.matchesExpected -and $_.lightFound -and $_.additionalDataPresent -and $_.lightType -eq 'Directional' -and
        $_.lightUnit -eq 'Lux' -and [Math]::Abs([double]$_.intensity - 10.0) -lt 0.0001
    } | Select-Object -ExpandProperty stage -Unique)
    $lightStateGate = $validLightStages.Count -eq $requiredLightStages.Count -and
        @($requiredLightStages | Where-Object { $validLightStages -notcontains $_ }).Count -eq 0
    $runtimeGate = $null -ne $runtimeSummary -and $runtimeSummary.ok -and -not $runtimeSummary.isEditor -and
        -not $runtimeSummary.development -and $runtimeSummary.graphicsDeviceType -eq 'Direct3D12' -and
        $runtimeSummary.pipelineAsset -eq 'UnityEngine.Rendering.HighDefinition.HDRenderPipelineAsset' -and
        $runtimeSummary.requestedViews -eq 4 -and $runtimeSummary.requestedColumns -eq 2 -and $runtimeSummary.requestedRows -eq 2 -and
        $runtimeSummary.before.allTilesNonBlank -and $runtimeSummary.before.allTilesHaveRedAndGreen -and $runtimeSummary.before.allViewsDiffer -and
        $runtimeSummary.after.allTilesNonBlank -and $runtimeSummary.after.allTilesHaveRedAndGreen -and $runtimeSummary.after.allViewsDiffer -and
        $runtimeSummary.changedPixels -ge 64 -and $runtimeSummary.settingsRetained -and $runtimeSummary.exposureRetained -and
        $runtimeSummary.fixedExposureMode -eq 'Fixed' -and [Math]::Abs([double]$runtimeSummary.fixedExposureEv - 4.0) -lt 0.0001 -and
        $runtimeSummary.manualExposureRetained -and $runtimeSummary.manualExposureMode -eq 'ManualFixed' -and
        [Math]::Abs([double]$runtimeSummary.manualExposureEv - 4.0) -lt 0.0001 -and $viewDiagnosticGate -and $lightStateGate -and $runtimeSummary.readbackReady -and
        $runtimeSummary.readinessAttempts -gt 0 -and $runtimeSummary.readinessElapsedMilliseconds -ge 0 -and
        $runtimeSummary.readinessElapsedMilliseconds -le 15000
    $playerSummary = [ordered]@{
        startedUtc = $playerStartUtc.ToString('o'); endedUtc = $playerEndUtc.ToString('o'); pid = $playerProcess.Id
        naturalExit = $playerNaturalExit; exitCode = $playerExitCode; pipeline = 'hdrp'; graphicsApi = 'Direct3D12'; buildKind = 'Release'
        timeoutSeconds = $PlayerTimeoutSeconds; deadlineReached = $playerDeadlineReached; timedOut = $playerTimedOut
        runtimeSummaryPath = $runtimeSummaryPath; runtimeSummary = $runtimeSummary; runtimeGatesPassed = [bool]$runtimeGate
        fixtureLightStageGate = [bool]$lightStateGate; fixtureLightRequiredStages = $requiredLightStages
        fixtureLightObservedStages = $validLightStages
        effectiveVolumeControlViews = $validEffectiveVolumeControlViews
        applicationCrashEvents1000_1001 = $crashEvents; log = $playerLog
        nativeEvents = $nativeEvents
        nativeTraceReason = 'The fixture has no Bridge output or session; trace is observational and no native event is required for this render-only gate.'
        loadedBridgeModules = $moduleObservations; moduleObservationFailures = $moduleObservationFailures
    }
    Write-AsciiJson (Join-Path $evidence 'player-summary.json') $playerSummary
    if (-not $playerNaturalExit -or $playerExitCode -ne 0) { throw 'HDRP render-only Player did not exit naturally with code 0.' }
    if (-not $runtimeGate) { throw 'HDRP render-only runtime gates failed; inspect player-evidence/summary.json.' }
    if ($crashEvents.Count -ne 0) { throw 'Application crash events were recorded for the HDRP Player.' }
}
catch {
    if ($evidenceOwned) { Write-RunnerError $_.Exception.GetBaseException().Message }
    throw
}
finally {
    $editorStillRunning = $false
    if ($null -ne $editorProcess) { try { $editorStillRunning = -not $editorProcess.HasExited } catch { $editorStillRunning = $true } }
    $playerStillRunning = $false
    if ($null -ne $playerProcess) { try { $playerStillRunning = -not $playerProcess.HasExited } catch { $playerStillRunning = $true } }
    $processStillRunning = $editorStillRunning -or $playerStillRunning
    if ($snapshotReady -and -not $processStillRunning) {
        if (Test-Path -LiteralPath $editorHelper) { Remove-Item -LiteralPath (Assert-Within $editorHelper (Join-Path $project 'Assets\Editor')) -Force }
        if (Test-Path -LiteralPath $editorHelperMeta) { Remove-Item -LiteralPath (Assert-Within $editorHelperMeta (Join-Path $project 'Assets\Editor')) -Force }
        if (Test-Path -LiteralPath $runtimeRoot) { Remove-Item -LiteralPath (Assert-Within $runtimeRoot (Join-Path $project 'Assets')) -Recurse -Force }
        if (Test-Path -LiteralPath $runtimeRootMeta) { Remove-Item -LiteralPath (Assert-Within $runtimeRootMeta (Join-Path $project 'Assets')) -Force }
        if ($generatedExisted) {
            Restore-TreeSnapshot $generatedRoot $generatedBackup $generatedBefore (Join-Path $project 'Assets')
        } elseif (Test-Path -LiteralPath $generatedRoot) {
            Remove-Item -LiteralPath (Assert-Within $generatedRoot (Join-Path $project 'Assets')) -Recurse -Force
        }
        if ($generatedMetaExisted) { Copy-Item -LiteralPath $generatedMetaBackup -Destination $generatedRootMeta -Force }
        elseif (Test-Path -LiteralPath $generatedRootMeta) { Remove-Item -LiteralPath (Assert-Within $generatedRootMeta (Join-Path $project 'Assets')) -Force }
        if ($geometryCacheExisted) {
            New-Item -ItemType Directory -Path $userSettings -Force | Out-Null
            Copy-Item -LiteralPath $geometryCacheBackup -Destination $geometryCache -Force
        } elseif (Test-Path -LiteralPath $geometryCache -PathType Leaf) {
            Remove-Item -LiteralPath (Assert-Within $geometryCache $userSettings) -Force
        }
        if ($geometryCacheMetaExisted) {
            New-Item -ItemType Directory -Path $userSettings -Force | Out-Null
            Copy-Item -LiteralPath $geometryCacheMetaBackup -Destination $geometryCacheMeta -Force
        } elseif (Test-Path -LiteralPath $geometryCacheMeta -PathType Leaf) {
            Remove-Item -LiteralPath (Assert-Within $geometryCacheMeta $userSettings) -Force
        }
        Restore-TreeSnapshot $projectSettings $projectSettingsBackup $settingsBefore $project
        foreach ($record in $pipelineBefore) {
            $path = Join-Path $project ($record.path.Replace('/', '\'))
            $backup = Join-Path $baselineRoot ($record.path.Replace('/', '\'))
            if ($record.exists) {
                New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
                Copy-Item -LiteralPath $backup -Destination $path -Force
            } elseif (Test-Path -LiteralPath $path -PathType Leaf) {
                Remove-Item -LiteralPath (Assert-Within $path (Join-Path $project 'Assets')) -Force
            }
        }
    }

    $manifestAfter = if ($snapshotReady) { Get-TreeManifest $project @('Assets', 'ProjectSettings') } else { @() }
    $differences = if ($snapshotReady) { @(Compare-Object -ReferenceObject @($manifestBefore) -DifferenceObject @($manifestAfter) -Property path, bytes, sha256) } else { @() }
    $manifestPathAfter = if (Test-Path -LiteralPath (Join-Path $project 'Packages\manifest.json')) { Hash-File (Join-Path $project 'Packages\manifest.json') } else { $null }
    $lockPathAfter = if (Test-Path -LiteralPath (Join-Path $project 'Packages\packages-lock.json')) { Hash-File (Join-Path $project 'Packages\packages-lock.json') } else { $null }
    $geometryCacheHashAfter = if (Test-Path -LiteralPath $geometryCache -PathType Leaf) { Hash-File $geometryCache } else { $null }
    $geometryCacheMetaHashAfter = if (Test-Path -LiteralPath $geometryCacheMeta -PathType Leaf) { Hash-File $geometryCacheMeta } else { $null }
    $geometryCacheUnchanged = ($geometryCacheExisted -eq (Test-Path -LiteralPath $geometryCache -PathType Leaf)) -and
        $geometryCacheHashBefore -eq $geometryCacheHashAfter -and
        $geometryCacheMetaExisted -eq (Test-Path -LiteralPath $geometryCacheMeta -PathType Leaf) -and
        $geometryCacheMetaHashBefore -eq $geometryCacheMetaHashAfter
    $cleanupOk = $snapshotReady -and -not $processStillRunning -and $differences.Count -eq 0 -and
        $manifestPathAfter -eq $manifestShaBefore -and $lockPathAfter -eq $lockShaBefore -and $geometryCacheUnchanged -and
        -not (Test-Path -LiteralPath $editorHelper) -and -not (Test-Path -LiteralPath $editorHelperMeta) -and
        -not (Test-Path -LiteralPath $runtimeRoot) -and -not (Test-Path -LiteralPath $runtimeRootMeta)
    $cleanup = [ordered]@{
        completedUtc = [DateTime]::UtcNow.ToString('o'); snapshotReady = $snapshotReady
        editorStillRunning = $editorStillRunning; playerStillRunning = $playerStillRunning
        generatedAssetsExistedAtStart = $generatedExisted; generatedMetaExistedAtStart = $generatedMetaExisted
        projectSettingsFilesAtStart = $settingsBefore.Count; pipelineGlobalSettings = $pipelineBefore
        finalProjectFiles = $manifestAfter.Count; differences = @($differences | ForEach-Object { [ordered]@{ side = $_.SideIndicator; path = $_.path; bytes = $_.bytes; sha256 = $_.sha256 } })
        manifestUnchanged = ($manifestPathAfter -eq $manifestShaBefore); packageLockUnchanged = ($lockPathAfter -eq $lockShaBefore)
        geometryCacheExistedAtStart = $geometryCacheExisted; geometryCacheHashBefore = $geometryCacheHashBefore; geometryCacheHashAfter = $geometryCacheHashAfter
        geometryCacheMetaExistedAtStart = $geometryCacheMetaExisted; geometryCacheMetaHashBefore = $geometryCacheMetaHashBefore; geometryCacheMetaHashAfter = $geometryCacheMetaHashAfter
        geometryCacheUnchanged = $geometryCacheUnchanged
        editorHelperRemoved = -not (Test-Path -LiteralPath $editorHelper) -and -not (Test-Path -LiteralPath $editorHelperMeta)
        runtimeHelperRemoved = -not (Test-Path -LiteralPath $runtimeRoot) -and -not (Test-Path -LiteralPath $runtimeRootMeta)
        naturalEditorExitCode = if ($null -ne $editorProcess -and $editorProcess.HasExited) { $editorProcess.ExitCode } else { $null }
        playerNaturalExitCode = $playerExitCode; cleanupOk = $cleanupOk
    }
    if ($evidenceOwned) { Write-AsciiJson (Join-Path $evidence 'cleanup-summary.json') $cleanup }
    if (-not $cleanupOk) { throw 'Render-only Player cleanup/baseline gate failed; inspect cleanup-summary.json.' }
}

Write-Output ('WINDOWS_RENDER_ONLY_PLAYER_PASS evidence=' + $evidence + ' player=' + $playerName)
