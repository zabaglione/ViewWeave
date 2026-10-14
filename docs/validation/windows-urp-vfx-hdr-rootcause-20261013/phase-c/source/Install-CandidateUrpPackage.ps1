param([int]$TimeoutSeconds = 900)
$ErrorActionPreference = 'Stop'
$base = 'E:/LookingGlassSDK/.local/windows-vfx-scene-comparison-20261013'
$project = Join-Path $base 'URP/UnityProject'
$editor = 'C:/Program Files/Unity/Hub/Editor/6000.6.4f1/Editor/Unity.exe'
$candidate = Join-Path $base 'CandidatePackages/com.zabaglione.lookingglass.viewweave.urp'
$candidateSource = Join-Path $candidate 'Runtime/UrpViewHistoryRenderer.cs'
$results = Join-Path $base 'URP/CandidatePackageResolution'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ')
$resultPath = Join-Path $results "candidate-urp-$stamp.json"
$logPath = Join-Path $results "candidate-urp-$stamp-editor.log"
$recordPath = Join-Path $results "candidate-urp-$stamp-runner.json"
$packageUri = 'file:' + $candidate.Replace('\','/')
if (-not (Test-Path -LiteralPath $editor) -or -not (Test-Path -LiteralPath $candidateSource)) { throw 'Unity Editor or candidate package source is missing.' }
foreach ($path in @($resultPath,$logPath,$recordPath)) { if (Test-Path -LiteralPath $path) { throw 'Refusing to overwrite a package-resolution artifact.' } }
$candidateHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $candidateSource).Hash

function Get-ExactProjectProcessRows([string]$projectPath) {
    $rows = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop
    $full = [IO.Path]::GetFullPath($projectPath).Replace('/','\').TrimEnd('\')
    @($rows | Where-Object {
        $command = [string]$_.CommandLine
        $match = [regex]::Match($command, '(?:^|\s)-projectPath(?:\s+|=)(?:"([^"]+)"|(\S+))', [Text.RegularExpressions.RegexOptions]::IgnoreCase)
        if (-not $match.Success) { return $false }
        $candidateArg = if ($match.Groups[1].Success) { $match.Groups[1].Value } else { $match.Groups[2].Value }
        try { [IO.Path]::GetFullPath($candidateArg).Replace('/','\').TrimEnd('\').Equals($full,[StringComparison]::OrdinalIgnoreCase) } catch { $false }
    } | ForEach-Object { [ordered]@{ processId=[int]$_.ProcessId; executablePath=$_.ExecutablePath; creationTimeUtc=([datetime]$_.CreationDate).ToUniversalTime().ToString('o') } })
}

$before = @()
try { $before = Get-ExactProjectProcessRows $project }
catch {
    $summary = [ordered]@{ status='blocked-process-inventory-unavailable'; projectPath=$project; candidatePackagePath=$candidate; candidateRuntimeSourceSha256=$candidateHash; started=$false; errorType=$_.Exception.GetType().Name }
    [IO.File]::WriteAllText($recordPath,($summary | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    Get-Content -LiteralPath $recordPath -Raw | Write-Output
    exit 2
}
if ($before.Count -gt 0) {
    $summary = [ordered]@{ status='blocked-exact-project-editor-already-running'; projectPath=$project; matchingBefore=$before; candidatePackagePath=$candidate; candidateRuntimeSourceSha256=$candidateHash; started=$false }
    [IO.File]::WriteAllText($recordPath,($summary | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    Get-Content -LiteralPath $recordPath -Raw | Write-Output
    exit 2
}

$psi = [Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $editor
$psi.WorkingDirectory = $project
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
foreach ($arg in @('-batchmode','-projectPath',$project,'-executeMethod','ViewWeave.SceneComparison.InstallCandidateUrpPackage.Run','-logFile',$logPath)) { [void]$psi.ArgumentList.Add($arg) }
$psi.Environment['VW_URP_HDR_CANDIDATE_PATH'] = $candidate
$psi.Environment['VW_URP_HDR_CANDIDATE_URI'] = $packageUri
$psi.Environment['VW_URP_HDR_CANDIDATE_RESULT'] = $resultPath
$process = [Diagnostics.Process]::new()
$process.StartInfo = $psi
$stdoutDrain = $null
$stderrDrain = $null
$started = $false
$timedOut = $false
$exitCode = $null
$editorPid = $null
$startUtc = $null
$drainIncomplete = $false
$killAttempted = $false
$killErrorType = $null
$startErrorType = $null
try {
    $started = $process.Start()
    if (-not $started) { throw 'Editor process start returned false.' }
    $editorPid = $process.Id
    $startUtc = $process.StartTime.ToUniversalTime().ToString('o')
    $stdoutDrain = $process.StandardOutput.BaseStream.CopyToAsync([IO.Stream]::Null)
    $stderrDrain = $process.StandardError.BaseStream.CopyToAsync([IO.Stream]::Null)
    if ($process.WaitForExit([Math]::Max(1,$TimeoutSeconds) * 1000)) { $exitCode = $process.ExitCode }
    else {
        $timedOut = $true
        $killAttempted = $true
        try {
            if (-not $process.HasExited) { $process.Kill($true) }
            if ($process.WaitForExit(10000)) { $exitCode = $process.ExitCode }
        } catch { $killErrorType = $_.Exception.GetType().Name }
    }
} catch { $startErrorType = $_.Exception.GetType().Name }
finally {
    if ($started -and $process.HasExited) {
        try { $drainIncomplete = -not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdoutDrain,$stderrDrain),10000) } catch { $drainIncomplete = $true }
    } elseif ($started) { $drainIncomplete = $true }
    $after = @()
    $afterAvailable = $true
    try { $after = Get-ExactProjectProcessRows $project } catch { $afterAvailable = $false }
    $stillRunning = $false
    if ($started) { try { $stillRunning = -not $process.HasExited } catch { $stillRunning = $true } }
    $resolution = $null
    if (Test-Path -LiteralPath $resultPath) {
        try { $resolution = Get-Content -LiteralPath $resultPath -Raw | ConvertFrom-Json } catch { $resolution = $null }
    }
    $resolvedMatch = $false
    if ($resolution -and $resolution.resolvedPath) {
        try { $resolvedMatch = [IO.Path]::GetFullPath([string]$resolution.resolvedPath).TrimEnd('\').Equals([IO.Path]::GetFullPath($candidate).TrimEnd('\'),[StringComparison]::OrdinalIgnoreCase) } catch { $resolvedMatch = $false }
    }
    $pass = $started -and -not $timedOut -and $null -ne $exitCode -and $exitCode -eq 0 -and -not $drainIncomplete -and $afterAvailable -and $after.Count -eq 0 -and
        $resolution -and $resolution.status -eq 'candidate-package-loaded' -and [int]$resolution.exitCode -eq 0 -and $resolvedMatch
    $summary = [ordered]@{
        status=if($pass){'candidate-urp-package-loaded'}elseif($timedOut){'timeout-result-incomplete'}else{'not-passed'}
        projectPath=$project; unityExecutable=$editor; candidatePackagePath=$candidate; candidatePackageUri=$packageUri; candidateRuntimeSourceSha256=$candidateHash
        startedUtc=$startUtc; endedUtc=[DateTime]::UtcNow.ToString('o'); editorProcessId=$editorPid; editorExitCode=$exitCode; timeout=$timedOut; timeoutSeconds=$TimeoutSeconds
        timeoutKillAttempted=$killAttempted; timeoutKillErrorType=$killErrorType; startErrorType=$startErrorType
        stdoutAndStderrDrained=$started -and -not $drainIncomplete; processInventoryAvailableBefore=$true; matchingBefore=$before
        processInventoryAvailableAfter=$afterAvailable; matchingAfter=$after; processObjectStillRunning=$stillRunning
        packageResolutionResultPath=$resultPath; packageResolutionResult=$resolution; resolvedPathMatchesCandidate=$resolvedMatch
        editorLogPath=$logPath; editorLogShared=$false; passGate=$pass
    }
    [IO.File]::WriteAllText($recordPath,($summary | ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    $process.Dispose()
}
Get-Content -LiteralPath $recordPath -Raw | Write-Output
if (-not $pass) { exit 1 }
