param(
    [string]$BuildDirectory = ".local/dx12-followup/warp/build",
    [string]$EvidencePath = ".local/dx12-followup/warp/evidence/warp.json"
)

$ErrorActionPreference = "Stop"
$env:VSLANG = "1033"
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "../../..")).Path
$buildPath = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $BuildDirectory))
$evidenceFile = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $EvidencePath))
$allowedRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot ".local/dx12-followup/warp"))
if (-not $buildPath.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
    -not $evidenceFile.StartsWith($allowedRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Build and evidence paths must stay under .local/dx12-followup/warp"
}

New-Item -ItemType Directory -Force -Path $buildPath | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $evidenceFile) | Out-Null
cmake -S (Join-Path $repoRoot "Tests/Native/D3D12WarpInterop") -B $buildPath -G "Visual Studio 17 2022" -A x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
cmake --build $buildPath --config Release
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$probeExe = Join-Path $buildPath "Release/d3d12_warp_interop_probe.exe"
$output = & $probeExe
$exitCode = $LASTEXITCODE
if ($output -notmatch '^\{"verdict":"(PASS|UNSUPPORTED|FAIL)"') {
    throw "Probe did not produce the expected JSON result"
}
$result = ConvertFrom-Json -InputObject $output
$sourceFile = Join-Path $PSScriptRoot "d3d12_warp_interop_probe.cpp"
$scriptFile = $MyInvocation.MyCommand.Path
$result | Add-Member -NotePropertyName run_utc -NotePropertyValue ([DateTime]::UtcNow.ToString("o"))
$result | Add-Member -NotePropertyName source_sha256 -NotePropertyValue (Get-FileHash -Algorithm SHA256 -LiteralPath $sourceFile).Hash
$result | Add-Member -NotePropertyName script_sha256 -NotePropertyValue (Get-FileHash -Algorithm SHA256 -LiteralPath $scriptFile).Hash
$result | Add-Member -NotePropertyName executable_sha256 -NotePropertyValue (Get-FileHash -Algorithm SHA256 -LiteralPath $probeExe).Hash
$evidence = ConvertTo-Json -InputObject $result -Compress
[System.IO.File]::WriteAllText($evidenceFile, ($evidence + "`n"), [System.Text.Encoding]::ASCII)
Write-Output $output
exit $exitCode
