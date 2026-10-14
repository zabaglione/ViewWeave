param(
    [Parameter(Mandatory=$true)][ValidateSet('Green','Coin')][string]$Probe,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [int]$TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
$base = 'E:/LookingGlassSDK/.local/windows-vfx-scene-comparison-20261013'
$project = Join-Path $base 'URP/UnityProject'
$editor = 'C:/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe'
$source = 'E:/LookingGlassSDK/.local/windows-urp-vfx-hdr-fix-20261013/DeviceHelpers/MainSdkHdrDeviceProbeTests.cs'
$cloneSource = Join-Path $project 'Assets/SceneComparisonTests/MainSdkHdrDeviceProbeTests.cs'
$results = Join-Path $base 'URP/ResultsDevice'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$filter = if ($Probe -eq 'Green') {
    'ViewWeave.SceneComparison.Tests.MainSdkHdrDeviceProbeTests.ObserveGreenVfxOnMainSdkAndPhysicalDisplay'
} else {
    'ViewWeave.SceneComparison.Tests.MainSdkHdrDeviceProbeTests.ObserveCoinVfxOnMainSdkAndPhysicalDisplay'
}
$xml = Join-Path $results "main-device-green-$stamp.xml"
$editorLog = Join-Path $results "main-device-green-$stamp-editor.log"
$record = Join-Path $results "main-device-green-$stamp-runner.json"
$evidence = [IO.Path]::GetFullPath($EvidenceDirectory)
$testResultsRoot = [IO.Path]::GetFullPath((Join-Path $project 'TestResults')).TrimEnd('\') + '\'

if (-not (Test-Path -LiteralPath $editor)) { throw 'Configured Unity Editor executable is missing.' }
if (-not (Test-Path -LiteralPath (Join-Path $project 'Packages/manifest.json'))) { throw 'Isolated project manifest is missing.' }
if (-not (Test-Path -LiteralPath $source) -or -not (Test-Path -LiteralPath $cloneSource)) { throw 'Device probe source is missing.' }
$sourceHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $source).Hash
$cloneHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $cloneSource).Hash
if ($sourceHash -ne 'AFC521F615489468D1F351565366935B37835B061B953DC6F9DBEC6303DE2B7F' -or $cloneHash -ne $sourceHash) { throw 'Device probe source is not the reviewed canonical source.' }
if (-not $evidence.StartsWith($testResultsRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence directory must remain under the isolated project TestResults folder.' }
foreach ($path in @($evidence,$xml,$editorLog,$record)) { if (Test-Path -LiteralPath $path) { throw 'Refusing to overwrite an existing device probe artifact.' } }

function Get-ExactProjectProcessRows([string]$projectPath) {
    $rows = Get-CimInstance -ClassName Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop
    $full = [IO.Path]::GetFullPath($projectPath).Replace('/','\').TrimEnd('\')
    @($rows | Where-Object {
        $command = [string]$_.CommandLine
        $match = [regex]::Match($command, '(?:^|\s)-projectPath(?:\s+|=)(?:"([^"]+)"|(\S+))', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) { return $false }
        $candidate = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        try { [IO.Path]::GetFullPath($candidate).Replace('/','\').TrimEnd('\').Equals($full,[StringComparison]::OrdinalIgnoreCase) } catch { $false }
    } | ForEach-Object { [ordered]@{ processId=[int]$_.ProcessId; executablePath=[string]$_.ExecutablePath; creationTimeUtc=([datetime]$_.CreationDate).ToUniversalTime().ToString('o') } })
}

$before = @()
try { $before = Get-ExactProjectProcessRows $project }
catch { throw 'Cannot verify exact-project Unity process inventory; no Editor was started.' }
if ($before.Count -gt 0) { throw 'An Editor already targets this exact isolated project; no new Editor was started.' }

$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $editor
$psi.WorkingDirectory = $project
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.RedirectStandardInput = $false
foreach ($arg in @('-batchmode','-projectPath',$project,'-runTests','-testPlatform','PlayMode','-testFilter',$filter,'-testResults',$xml,'-force-d3d12','-logFile',$editorLog)) { [void]$psi.ArgumentList.Add($arg) }
$psi.Environment['VW_MAIN_HDR_DEVICE_TEST'] = '1'
$psi.Environment['VW_MAIN_HDR_DEVICE_EVIDENCE_DIR'] = $evidence

$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi
$stdoutDrain = $null
$stderrDrain = $null
$started = $false
$timedOut = $false
$exitCode = $null
$processId = $null
$startUtc = $null
$startErrorType = $null
$timeoutCleanupAttempted = $false
$timeoutCleanupSucceeded = $false
$timeoutCleanupErrorType = $null
try {
    $started = $process.Start()
    if (-not $started) { throw 'Editor process start returned false.' }
    $processId = $process.Id
    $startUtc = $process.StartTime.ToUniversalTime().ToString('o')
    $stdoutDrain = $process.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
    $stderrDrain = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
    if ($process.WaitForExit([Math]::Max(1,$TimeoutSeconds) * 1000)) { $exitCode = $process.ExitCode }
    else {
        $timedOut = $true
        $timeoutCleanupAttempted = $true
        try {
            $identityRows = Get-ExactProjectProcessRows $project
            $exactOwned = @($identityRows | Where-Object {
                $_.processId -eq $processId -and
                [string]::Equals([string]$_.executablePath,$editor,[StringComparison]::OrdinalIgnoreCase) -and
                [Math]::Abs((([datetime]$_.creationTimeUtc).ToUniversalTime() - ([datetime]$startUtc).ToUniversalTime()).TotalSeconds) -lt 2
            }).Count -eq 1
            if ($exactOwned) {
                $process.Kill($true)
                $timeoutCleanupSucceeded = $process.WaitForExit(10000)
                if ($timeoutCleanupSucceeded) { $exitCode = $process.ExitCode }
            }
        } catch { $timeoutCleanupErrorType = $_.Exception.GetType().Name }
    }
} catch { $startErrorType = $_.Exception.GetType().Name }
finally {
    $endedUtc = [DateTime]::UtcNow.ToString('o')
    $drainIncomplete = $false
    if ($started -and $process.HasExited) {
        try { $drainIncomplete = -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutDrain,$stderrDrain),10000) } catch { $drainIncomplete = $true }
    } elseif ($started) { $drainIncomplete = $true }
    $after = @()
    $afterAvailable = $true
    try { $after = Get-ExactProjectProcessRows $project } catch { $afterAvailable = $false }
    $stillRunning = $false
    if ($started) { try { $stillRunning = -not $process.HasExited } catch { $stillRunning = $true } }
    $nunit = $null
    $caseNames = @()
    $xmlBytes = $null
    $xmlHash = $null
    $xmlError = $null
    if (Test-Path -LiteralPath $xml) {
        $xmlBytes = (Get-Item -LiteralPath $xml).Length
        $xmlHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $xml).Hash
        try {
            [xml]$doc = [IO.File]::ReadAllText($xml)
            $run = $doc.SelectSingleNode('//test-run')
            if ($run) {
                $caseNames = @($doc.SelectNodes('//test-case') | ForEach-Object { [string]$_.fullname })
                $nunit = [ordered]@{ total=[int]$run.total; passed=[int]$run.passed; failed=[int]$run.failed; skipped=[int]$run.skipped; inconclusive=[int]$run.inconclusive; result=[string]$run.result; testCaseFullNames=$caseNames }
            } else { $xmlError = 'MissingTestRunRoot' }
        } catch { $xmlError = $_.Exception.GetType().Name }
    }
    $evidenceFiles = @()
    if (Test-Path -LiteralPath $evidence) {
        $evidenceFiles = @(Get-ChildItem -LiteralPath $evidence -File | ForEach-Object { [ordered]@{ name=$_.Name; bytes=$_.Length; sha256=(Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash } })
    }
    $exactCaseGate = $null -ne $nunit -and $nunit.total -eq 1 -and $nunit.passed -eq 1 -and $nunit.failed -eq 0 -and $nunit.skipped -eq 0 -and $nunit.inconclusive -eq 0 -and $nunit.result -eq 'Passed' -and $caseNames.Count -eq 1 -and $caseNames[0] -eq $filter
    $pass = $started -and -not $timedOut -and $null -ne $exitCode -and $exitCode -eq 0 -and -not $drainIncomplete -and $afterAvailable -and $after.Count -eq 0 -and -not $stillRunning -and $exactCaseGate
    $summary = [ordered]@{
        status=if($pass){'nunit-and-process-gates-passed'}elseif($timedOut){'timeout-or-optical-response-incomplete'}else{'not-passed'}
        probe=$Probe; testFilter=$filter; testPlatform='PlayMode'; expectedTestCount=1; graphicsApiRequested='D3D12'; projectPath=$project; unityExecutable=$editor
        canonicalSourcePath=$source; canonicalSourceSha256=$sourceHash; cloneSourcePath=$cloneSource; cloneSourceSha256=$cloneHash
        startedUtc=$startUtc; endedUtc=$endedUtc; editorProcessId=$processId; editorExitCode=$exitCode; timeout=$timedOut; timeoutSeconds=$TimeoutSeconds
        timeoutCleanupAttempted=$timeoutCleanupAttempted; timeoutCleanupSucceeded=$timeoutCleanupSucceeded; timeoutCleanupErrorType=$timeoutCleanupErrorType; startErrorType=$startErrorType
        stdoutAndStderrDrained=$started -and -not $drainIncomplete; processInventoryAvailableBefore=$true; matchingBefore=$before; processInventoryAvailableAfter=$afterAvailable; matchingAfter=$after; processObjectStillRunning=$stillRunning
        xmlPath=$xml; xmlBytes=$xmlBytes; xmlSha256=$xmlHash; xmlParseErrorType=$xmlError; nunit=$nunit; exactTestCaseGate=$exactCaseGate
        editorLogPath=$editorLog; editorLogShared=$false; evidenceDirectory=$evidence; evidenceFiles=$evidenceFiles; stageReadyExists=(Test-Path -LiteralPath (Join-Path $evidence 'stageReady.json')); deviceResponseExists=(Test-Path -LiteralPath (Join-Path $evidence 'device-response.json')); boundedRunGate=$pass
    }
    [IO.File]::WriteAllText($record,($summary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $process.Dispose()
}
Get-Content -LiteralPath $record -Raw | Write-Output
if (-not $pass) { exit 1 }
