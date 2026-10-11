param(
    [Parameter(Mandatory = $true)]
    [string]$InputDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OraclePath,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$InputDirectory = (Resolve-Path -LiteralPath $InputDirectory).Path
$OraclePath = (Resolve-Path -LiteralPath $OraclePath).Path
$sourceMetadataPath = Join-Path $InputDirectory 'producer-orientation.json'
if (-not (Test-Path -LiteralPath $sourceMetadataPath -PathType Leaf)) {
    throw "Producer metadata not found: $sourceMetadataPath"
}
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $PSScriptRoot (Join-Path 'quilt-orientation-results' (Split-Path $InputDirectory -Leaf))
} elseif (-not [IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory = Join-Path (Get-Location).Path $OutputDirectory
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$sourceMetadata = Get-Content -LiteralPath $sourceMetadataPath -Raw | ConvertFrom-Json
$runSummaryPath = Join-Path $InputDirectory 'run-summary.json'
if (-not (Test-Path -LiteralPath $runSummaryPath -PathType Leaf)) {
    throw "Unity run summary not found: $runSummaryPath"
}
$runSummary = Get-Content -LiteralPath $runSummaryPath -Raw | ConvertFrom-Json
if (-not $runSummary.passGate -or $runSummary.cliExitCode -ne 0 -or
    -not $runSummary.editorNaturalExitObserved -or $runSummary.mode -ne 'PlayMode' -or
    $runSummary.testCounts.total -ne 1 -or $runSummary.testCounts.passed -ne 1 -or
    $runSummary.testCounts.failed -ne 0 -or $runSummary.testCounts.skipped -ne 0 -or
    @($runSummary.settingsDiff).Count -ne 0) {
    throw "Unity producer run did not pass the required XML, natural-exit, and settings gates: $runSummaryPath"
}
$testXmlPath = $runSummary.testXml
if (-not (Test-Path -LiteralPath $testXmlPath -PathType Leaf)) {
    throw "Unity test XML not found: $testXmlPath"
}
$testXmlHash = (Get-FileHash -LiteralPath $testXmlPath -Algorithm SHA256).Hash
$unityInstallRoot = Split-Path (Split-Path $runSummary.editorProcessesObserved[0].executablePath -Parent) -Parent
$width = [int]$sourceMetadata.width
$height = [int]$sourceMetadata.height
$columns = [int]$sourceMetadata.columns
$rows = [int]$sourceMetadata.rows
$supportedProducerApis = @('Direct3D11', 'Direct3D12')
$summaryApi = if ($sourceMetadata.graphicsApi -eq 'Direct3D11') { 'D3D11' } else { 'D3D12' }
if ($supportedProducerApis -notcontains $sourceMetadata.graphicsApi -or $width -le 0 -or $height -le 0 -or
    $summaryApi -ne $runSummary.graphicsApiRequested -or
    $columns -le 0 -or $rows -le 0 -or $width % $columns -ne 0 -or $height % $rows -ne 0) {
    throw 'Producer metadata must describe a valid D3D11 quilt and complete tile grid'
}
$expectedBytes = [int64]$width * $height * 4
$sourceMetadataHash = (Get-FileHash -LiteralPath $sourceMetadataPath -Algorithm SHA256).Hash
$results = @()
$outputPaths = @{}
$candidateFiles = @($sourceMetadata.files | Where-Object { $_.name -match '^transfer-.*\.rgba8$' } | ForEach-Object { $_.name })
if ($candidateFiles -notcontains 'transfer-current-flip.rgba8' -or
    $candidateFiles -notcontains 'transfer-no-flip.rgba8') {
    throw 'Producer metadata must include current-flip and no-flip transfer candidates'
}

foreach ($candidateFile in $candidateFiles) {
    $candidate = [IO.Path]::GetFileNameWithoutExtension($candidateFile)
    $inputPath = Join-Path $InputDirectory $candidateFile
    if (-not (Test-Path -LiteralPath $inputPath -PathType Leaf)) {
        throw "Candidate input not found: $inputPath"
    }
    $fileRecord = @($sourceMetadata.files | Where-Object { $_.name -eq $candidateFile }) | Select-Object -First 1
    if (-not $fileRecord) {
        throw "Producer metadata has no hash record for $candidate.rgba8"
    }
    $inputHash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash
    if ($inputHash -ne $fileRecord.sha256) {
        throw "Candidate SHA-256 does not match producer metadata: $inputPath"
    }
    $inputBytes = [IO.File]::ReadAllBytes($inputPath)
    if ($inputBytes.LongLength -ne $expectedBytes) {
        throw "Candidate has $($inputBytes.LongLength) bytes; expected ${expectedBytes}: $inputPath"
    }
    $hasVisibleRgb = $false
    for ($index = 0; $index -lt $inputBytes.Length; $index += 4) {
        if ($inputBytes[$index] -ne 0 -or $inputBytes[$index + 1] -ne 0 -or $inputBytes[$index + 2] -ne 0) {
            $hasVisibleRgb = $true
            break
        }
    }
    if (-not $hasVisibleRgb) {
        throw "Candidate input is all black RGB and cannot diagnose orientation: $inputPath"
    }

    foreach ($mode in 0, 1, 2) {
        $outputPath = Join-Path $OutputDirectory "$candidate.mode-$mode.native.rgba8"
        $outputMetadataPath = Join-Path $OutputDirectory "$candidate.mode-$mode.native.json"
        & $OraclePath $inputPath $outputPath $outputMetadataPath $width $height $columns $rows $mode
        if ($LASTEXITCODE -ne 0) {
            throw "Native oracle failed for $candidate mode $mode with exit code $LASTEXITCODE"
        }
        $outputBytes = [IO.File]::ReadAllBytes($outputPath)
        if ($outputBytes.Length -ne (9 * 7 * 4)) {
            throw "Native oracle output size is invalid: $outputPath"
        }
        $topRed = 0; $topBlue = 0; $bottomRed = 0; $bottomBlue = 0
        $greenValues = @{}
        for ($y = 0; $y -lt 7; $y++) {
            for ($x = 0; $x -lt 9; $x++) {
                $index = ($y * 9 + $x) * 4
                $greenValues[[string]$outputBytes[$index + 1]] = $true
                if ($y -lt 3) {
                    $topRed += $outputBytes[$index]
                    $topBlue += $outputBytes[$index + 2]
                } elseif ($y -gt 3) {
                    $bottomRed += $outputBytes[$index]
                    $bottomBlue += $outputBytes[$index + 2]
                }
            }
        }
        $topRedMean = [Math]::Round($topRed / 27)
        $topBlueMean = [Math]::Round($topBlue / 27)
        $bottomRedMean = [Math]::Round($bottomRed / 27)
        $bottomBlueMean = [Math]::Round($bottomBlue / 27)
        $orientation = if ($topRedMean -gt $topBlueMean -and $bottomBlueMean -gt $bottomRedMean) {
            'top-red-bottom-blue'
        } elseif ($topBlueMean -gt $topRedMean -and $bottomRedMean -gt $bottomBlueMean) {
            'top-blue-bottom-red'
        } else {
            'ambiguous'
        }
        $outputPaths["$candidate/$mode"] = $outputPath
        $results += [PSCustomObject]@{
            candidate = $candidate
            mode = $mode
            modeName = @('Automatic', 'Classic RGB', 'Calibrated Cells')[$mode]
            orientation = $orientation
            topRedMean = $topRedMean
            topBlueMean = $topBlueMean
            bottomRedMean = $bottomRedMean
            bottomBlueMean = $bottomBlueMean
            uniqueGreenChannels = $greenValues.Count
            inputSha256 = $inputHash
            outputSha256 = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash
            outputMetadata = [IO.Path]::GetFileName($outputMetadataPath)
        }
        Write-Output "ORACLE: candidate=$candidate mode=$mode orientation=$orientation topRB=$topRedMean,$topBlueMean bottomRB=$bottomRedMean,$bottomBlueMean uniqueGreen=$($greenValues.Count)"
    }
}

foreach ($mode in 0, 1, 2) {
    $currentBytes = [IO.File]::ReadAllBytes($outputPaths["transfer-current-flip/$mode"])
    $noFlipBytes = [IO.File]::ReadAllBytes($outputPaths["transfer-no-flip/$mode"])
    $differentChannels = 0
    for ($index = 0; $index -lt $currentBytes.Length; $index++) {
        if ($currentBytes[$index] -ne $noFlipBytes[$index]) { $differentChannels++ }
    }
    Write-Output "COMPARE: current-flip vs no-flip mode=$mode differingChannels=$differentChannels of $($currentBytes.Length)"
    if ($outputPaths.ContainsKey("transfer-production-copy/$mode")) {
        $productionBytes = [IO.File]::ReadAllBytes($outputPaths["transfer-production-copy/$mode"])
        $differentProductionChannels = 0
        for ($index = 0; $index -lt $productionBytes.Length; $index++) {
            if ($productionBytes[$index] -ne $noFlipBytes[$index]) { $differentProductionChannels++ }
        }
        Write-Output "COMPARE: production-copy vs no-flip mode=$mode differingChannels=$differentProductionChannels of $($productionBytes.Length)"
    }
}

$resultDocument = [PSCustomObject]@{
    schemaVersion = 1
    graphicsApi = $sourceMetadata.graphicsApi
    unityEditorVersion = [IO.Path]::GetFileName($unityInstallRoot)
    unityTestFilter = $runSummary.testFilter
    unityTestCounts = $runSummary.testCounts
    unityRunSummarySha256 = (Get-FileHash -LiteralPath $runSummaryPath -Algorithm SHA256).Hash
    unityTestXmlSha256 = $testXmlHash
    renderPipeline = $sourceMetadata.renderPipeline
    graphicsUvStartsAtTop = $sourceMetadata.graphicsUvStartsAtTop
    inputRowOrder = $sourceMetadata.readbackRowOrder
    inputWidth = $width
    inputHeight = $height
    columns = $columns
    rows = $rows
    viewCount = $sourceMetadata.viewCount
    renderCallbackCount = $sourceMetadata.renderCallbackCount
    requestedMsaaSamples = $sourceMetadata.requestedMsaaSamples
    effectiveViewAntiAliasing = $sourceMetadata.effectiveViewAntiAliasing
    resolveTextureCreated = $sourceMetadata.resolveTextureCreated
    producerMetadataSha256 = $sourceMetadataHash
    producerSourceFiles = $runSummary.sourceFiles
    nativeOutputGeometry = '9x7 offscreen test fixture; synthetic calibration, not calibrated panel geometry'
    results = $results
}
$resultDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'native-oracle-results.json') -Encoding utf8
Write-Output "PASS: $($sourceMetadata.graphicsApi) producer candidates were rendered in all three Native modes"
