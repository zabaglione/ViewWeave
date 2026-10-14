[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9-]+$')]
    [string]$RunName,
    [Parameter(Mandatory = $true)]
    [string]$Filter,
    [ValidateSet('d3d11', 'd3d12')]
    [string]$GraphicsApi = 'd3d11',
    [ValidateSet('BuiltIn', 'URP', 'HDRP')]
    [string]$Pipeline = 'URP',
    [ValidateSet(1, 4)]
    [int]$MsaaSamples = 1,
    [string]$EvidenceRoot = '',
    [string]$UnityCliPath = (Join-Path $env:LOCALAPPDATA 'Unity\bin\unity.exe'),
    [string]$UnityEditorPath = 'C:\Program Files\Unity\Hub\Editor\6000.6.4f1\Editor\Unity.exe'
)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\..\..'))
$project = Join-Path $root 'Tests\Pipelines\Unity'
if (!$EvidenceRoot) {
    $EvidenceRoot = Join-Path $root ('.local\windows-quilt-orientation-' + [DateTime]::Now.ToString('yyyyMMdd'))
}
$evidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
$run = Join-Path $evidenceRoot $RunName
$cli = [IO.Path]::GetFullPath($UnityCliPath)
$editor = [IO.Path]::GetFullPath($UnityEditorPath)
$settings = @(
    'Packages/packages-lock.json',
    'ProjectSettings/GraphicsSettings.asset',
    'ProjectSettings/QualitySettings.asset',
    'ProjectSettings/EditorSettings.asset',
    'ProjectSettings/ProjectAuditorSettings.asset'
)

function Write-Ascii([string]$path, [string]$value) {
    [IO.File]::WriteAllText($path, $value, [Text.Encoding]::ASCII)
}

function Write-AsciiJson([string]$path, [object]$value) {
    Write-Ascii $path ((ConvertTo-Json -InputObject $value -Depth 10) + "`r`n")
}

function Get-Hash([string]$path) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    return (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-EditorProcesses {
    $expected = [IO.Path]::GetFullPath($editor).TrimEnd('\')
    return @(Get-CimInstance Win32_Process | Where-Object {
        $_.ExecutablePath -and [string]::Equals([IO.Path]::GetFullPath($_.ExecutablePath).TrimEnd('\'), $expected, [StringComparison]::OrdinalIgnoreCase)
    })
}

if (Test-Path -LiteralPath $run) { throw 'Evidence directory already exists.' }
foreach ($path in @($root, $project, $cli, $editor)) {
    if (!(Test-Path -LiteralPath $path)) { throw 'Required path is missing.' }
}
$existingEditors = @(Get-EditorProcesses)
if ($existingEditors.Count -ne 0) { throw 'A Unity Editor is already running; no existing Editor was touched.' }

New-Item -ItemType Directory -Path $run -Force | Out-Null
$temp = Join-Path $run 'temp'
$crash = Join-Path $run 'crash-reports'
New-Item -ItemType Directory -Path $temp -Force | Out-Null
New-Item -ItemType Directory -Path $crash -Force | Out-Null

$baseline = @()
foreach ($relative in $settings) {
    $path = Join-Path $project ($relative -replace '/', '\')
    $entry = [ordered]@{ path = $relative; exists = (Test-Path -LiteralPath $path -PathType Leaf); sha256 = $null }
    if ($entry.exists) {
        $entry.sha256 = Get-Hash $path
        $copy = Join-Path $run ('baseline\' + ($relative -replace '/', '\'))
        New-Item -ItemType Directory -Path (Split-Path -Parent $copy) -Force | Out-Null
        Copy-Item -LiteralPath $path -Destination $copy
    }
    $baseline += [pscustomobject]$entry
}

$assetRoot = Join-Path $project 'Assets\QuiltOrientationValidation'
$sourceFiles = @(
    (Join-Path $assetRoot 'QuiltOrientationDiagnosticTests.cs'),
    (Join-Path $assetRoot 'QuiltOrientationDiagnostic.shader'),
    (Join-Path $assetRoot 'ViewWeave.QuiltOrientationValidation.Tests.asmdef')
)
$sourceHashes = @()
foreach ($path in $sourceFiles) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'Orientation test source is missing.' }
    $sourceHashes += [pscustomobject]@{ path = $path.Substring($root.Length + 1).Replace('\', '/'); sha256 = Get-Hash $path }
}
$baselinePids = @(Get-CimInstance Win32_Process | ForEach-Object { [int]$_.ProcessId } | Sort-Object -Unique)
Write-AsciiJson (Join-Path $run 'baseline.json') ([ordered]@{
    capturedUtc = [DateTime]::UtcNow.ToString('o')
    project = $project
    editorPath = $editor
    preexistingEditorCount = 0
    baselinePids = $baselinePids
    settings = $baseline
    sourceFiles = $sourceHashes
})

$originalTemp = $env:TEMP
$originalTmp = $env:TMP
$originalEvidence = $env:LKG_ORIENTATION_EVIDENCE_DIR
$originalMsaa = $env:LKG_ORIENTATION_MSAA
$env:TEMP = $temp
$env:TMP = $temp
$env:LKG_ORIENTATION_EVIDENCE_DIR = $run
$env:LKG_ORIENTATION_MSAA = [string]$MsaaSamples
$xml = Join-Path $run 'test-results.xml'
$editorLog = Join-Path $run 'unity-editor.log'
$cliOut = Join-Path $run 'unity-cli.stdout.log'
$cliErr = Join-Path $run 'unity-cli.stderr.log'
$crashArg = '-crash-report-folder "' + $crash + '"'
$unityArguments = @(
    'test', ('"' + $project + '"'),
    '--editor-version', '6000.6.4f1',
    '--editor-path', ('"' + $editor + '"'),
    '--mode', 'PlayMode',
    '--filter', ('"' + $Filter + '"'),
    '--output', ('"' + $xml + '"'),
    '--timeout', '600',
    '--', ('-force-' + $GraphicsApi), '-logFile', ('"' + $editorLog + '"'), $crashArg
)
$argumentString = $unityArguments -join ' '
Write-Ascii (Join-Path $run 'command.txt') ('unity ' + $argumentString + "`r`n")

$info = [Diagnostics.ProcessStartInfo]::new()
$info.FileName = $cli
$info.Arguments = $argumentString
$info.WorkingDirectory = $project
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$process = [Diagnostics.Process]::new()
$process.StartInfo = $info
$startUtc = [DateTime]::UtcNow
$started = $process.Start()
if (!$started) { throw 'Unity CLI failed to start.' }
$outTask = $process.StandardOutput.ReadToEndAsync()
$errTask = $process.StandardError.ReadToEndAsync()
$ownedEditors = @{}
$deadline = [DateTime]::UtcNow.AddMinutes(12)
while (!$process.HasExited -and [DateTime]::UtcNow -lt $deadline) {
    foreach ($candidate in (Get-EditorProcesses)) {
        if ($baselinePids -contains [int]$candidate.ProcessId) { continue }
        $command = [string]$candidate.CommandLine
        $quotedProject = [Regex]::Escape($project)
        if ($command -match ('(?i)-projectPath\s+"?' + $quotedProject + '"?(?:\s|$)')) {
            $ownedEditors[[int]$candidate.ProcessId] = [ordered]@{
                pid = [int]$candidate.ProcessId
                executablePath = [IO.Path]::GetFullPath($candidate.ExecutablePath)
                parentPid = [int]$candidate.ParentProcessId
                startedUtc = [string]$candidate.CreationDate
            }
        }
    }
    Start-Sleep -Milliseconds 500
    $process.Refresh()
}
if (!$process.HasExited) {
    Write-Ascii $cliOut ([string]$outTask.Result)
    Write-Ascii $cliErr ([string]$errTask.Result)
    throw 'Unity CLI exceeded the 12 minute bound. The owned process was not force-stopped.'
}
$process.Refresh()
Write-Ascii $cliOut ([string]$outTask.Result)
Write-Ascii $cliErr ([string]$errTask.Result)
$exitCode = $process.ExitCode

$counts = $null
if (Test-Path -LiteralPath $xml -PathType Leaf) {
    [xml]$xmlDoc = Get-Content -LiteralPath $xml -Raw
    $testRun = $xmlDoc.'test-run'
    $counts = [ordered]@{ total = [int]$testRun.total; passed = [int]$testRun.passed; failed = [int]$testRun.failed; skipped = [int]$testRun.skipped }
}

$settingsDiff = @()
foreach ($entry in $baseline) {
    $path = Join-Path $project ($entry.path -replace '/', '\')
    $existsNow = Test-Path -LiteralPath $path -PathType Leaf
    $hashNow = if ($existsNow) { Get-Hash $path } else { $null }
    if ($existsNow -ne $entry.exists -or $hashNow -ne $entry.sha256) {
        $settingsDiff += [ordered]@{ path = $entry.path; beforeExists = $entry.exists; beforeSha256 = $entry.sha256; afterExists = $existsNow; afterSha256 = $hashNow }
    }
}

$remainingOwnedEditors = @()
foreach ($candidate in (Get-EditorProcesses)) {
    if ($ownedEditors.ContainsKey([int]$candidate.ProcessId)) { $remainingOwnedEditors += [int]$candidate.ProcessId }
}
$editorNaturalExit = ($ownedEditors.Count -gt 0 -and $remainingOwnedEditors.Count -eq 0)
$record = [ordered]@{
    startedUtc = $startUtc.ToString('o')
    completedUtc = [DateTime]::UtcNow.ToString('o')
    cliPid = $process.Id
    cliExitCode = $exitCode
    editorProcessesObserved = @($ownedEditors.Values)
    editorNaturalExitObserved = $editorNaturalExit
    mode = 'PlayMode'
    graphicsApiRequested = $GraphicsApi.ToUpperInvariant()
    requestedMsaaSamples = $MsaaSamples
    pipelineUnderTest = $Pipeline
    testFilter = $Filter
    testCounts = $counts
    testXml = $xml
    editorLog = $editorLog
    settingsDiff = $settingsDiff
    sourceFiles = $sourceHashes
    evidenceFiles = @('source-quilt.rgba8', 'transfer-current-flip.rgba8', 'transfer-no-flip.rgba8', 'transfer-production-copy.rgba8', 'producer-orientation.json')
    rawCommandLineCaptured = $false
    passGate = ($exitCode -eq 0 -and $null -ne $counts -and $counts.total -eq 1 -and $counts.passed -eq 1 -and $counts.failed -eq 0 -and $counts.skipped -eq 0 -and $settingsDiff.Count -eq 0 -and $editorNaturalExit)
}
Write-AsciiJson (Join-Path $run 'run-summary.json') $record

$env:TEMP = $originalTemp
$env:TMP = $originalTmp
if ($null -eq $originalEvidence) { Remove-Item Env:\LKG_ORIENTATION_EVIDENCE_DIR -ErrorAction SilentlyContinue }
else { $env:LKG_ORIENTATION_EVIDENCE_DIR = $originalEvidence }
if ($null -eq $originalMsaa) { Remove-Item Env:\LKG_ORIENTATION_MSAA -ErrorAction SilentlyContinue }
else { $env:LKG_ORIENTATION_MSAA = $originalMsaa }

if ($settingsDiff.Count -ne 0) { throw 'Unity changed a protected setting. Baseline copies are available for scoped review.' }
if (!$record.passGate) { throw 'The producer orientation diagnostic did not meet its pass gate.' }
Write-Output ('RUN_COMPLETE run=' + $RunName + ' cliExit=' + $exitCode + ' tests=' + $counts.total + ' passed=' + $counts.passed + ' failed=' + $counts.failed + ' skipped=' + $counts.skipped)
