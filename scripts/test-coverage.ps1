param(
    [ValidateSet("all", "code", "plugins")]
    [string]$Scope = "all",

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",

    [switch]$SkipRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$coverageRoot = Join-Path $repoRoot "coverage-output"
$runId = Get-Date -Format "yyyyMMdd-HHmmss"
$runRoot = Join-Path $coverageRoot "run-$runId"
$toolPath = Join-Path $runRoot ".tools"
$reportGeneratorExe = Join-Path $toolPath "reportgenerator.exe"
$dotnetCoverageExe = Join-Path $toolPath "dotnet-coverage.exe"

$codeResultsDir = Join-Path $runRoot "code"
$pluginResultsDir = Join-Path $runRoot "plugins"

$vscodeDir = Join-Path $repoRoot "src/Plugins/ef-querylens-vscode"
$riderDir = Join-Path $repoRoot "src/Plugins/ef-querylens-rider"
$vsPluginTestsProject = Join-Path $repoRoot "tests/EFQueryLens.VisualStudio.Tests/EFQueryLens.VisualStudio.Tests.csproj"
$coreTestsProject = Join-Path $repoRoot "tests/EFQueryLens.Core.Tests/EFQueryLens.Core.Tests.csproj"

function Invoke-Step {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][scriptblock]$Script
    )

    Write-Host ""
    Write-Host "==> $Name" -ForegroundColor Cyan
    & $Script
}

function Ensure-ReportGenerator {
    if (Test-Path $reportGeneratorExe) {
        return
    }

    New-Item -ItemType Directory -Path $toolPath -Force | Out-Null

    Invoke-Step "Installing ReportGenerator tool" {
        dotnet tool install dotnet-reportgenerator-globaltool --tool-path $toolPath --version 5.*
    }
}

function Ensure-DotnetCoverage {
    if (Test-Path $dotnetCoverageExe) {
        return
    }

    New-Item -ItemType Directory -Path $toolPath -Force | Out-Null

    Invoke-Step "Installing dotnet-coverage tool" {
        dotnet tool install dotnet-coverage --tool-path $toolPath --version 18.*
    }
}

function New-CleanDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (Test-Path $Path) {
        Remove-Item -Path $Path -Recurse -Force
    }

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
}

function Get-FilesOrFail {
    param(
        [Parameter(Mandatory = $true)][string]$Root,
        [Parameter(Mandatory = $true)][string]$Filter,
        [Parameter(Mandatory = $true)][string]$ErrorMessage
    )

    $files = @(Get-ChildItem -Path $Root -Recurse -File -Filter $Filter -ErrorAction SilentlyContinue)
    if (-not $files -or $files.Count -eq 0) {
        throw $ErrorMessage
    }

    return $files
}

function Write-CoverageSummary {
    param(
        [Parameter(Mandatory = $true)][string]$Title,
        [Parameter(Mandatory = $true)][string[]]$ReportPaths,
        [Parameter(Mandatory = $true)][string]$SummaryDirectory
    )

    New-CleanDirectory -Path $SummaryDirectory
    Ensure-ReportGenerator

    $reportArg = "-reports:{0}" -f (($ReportPaths | ForEach-Object { $_.Replace('\\', '/') }) -join ";")
    $targetArg = "-targetdir:{0}" -f $SummaryDirectory
    $typeArg = "-reporttypes:TextSummary;Html"

    Invoke-Step "Generating $Title coverage summary" {
        & $reportGeneratorExe $reportArg $targetArg $typeArg
    }

    $summaryFile = Join-Path $SummaryDirectory "Summary.txt"
    if (-not (Test-Path $summaryFile)) {
        throw "ReportGenerator did not create Summary.txt for $Title."
    }

    Write-Host ""
    Write-Host "---- $Title Coverage Summary ----" -ForegroundColor Green
    Get-Content -Path $summaryFile
    Write-Host "--------------------------------" -ForegroundColor Green
}

Push-Location $repoRoot
try {
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

    if (-not $SkipRestore) {
        Invoke-Step "Restoring .NET solution" {
            dotnet restore EFQueryLens.slnx
        }

        Invoke-Step "Installing VS Code plugin dependencies" {
            npm ci --prefix src/Plugins/ef-querylens-vscode
        }
    }

    if ($Scope -in @("all", "code")) {
        New-CleanDirectory -Path $codeResultsDir

        Invoke-Step "Running code/backend tests with Coverlet" {
            dotnet test $coreTestsProject `
                -c $Configuration `
                --collect:"XPlat Code Coverage" `
                --results-directory $codeResultsDir `
                --logger "console;verbosity=minimal"
        }

        $codeCoverageFiles = Get-FilesOrFail -Root $codeResultsDir -Filter "coverage.cobertura.xml" -ErrorMessage "No code coverage file generated for backend tests."
        $codeReportPaths = $codeCoverageFiles | ForEach-Object { $_.FullName }

        Write-CoverageSummary `
            -Title "Code/Backend" `
            -ReportPaths $codeReportPaths `
            -SummaryDirectory (Join-Path $codeResultsDir "report")
    }

    if ($Scope -in @("all", "plugins")) {
        New-CleanDirectory -Path $pluginResultsDir

        $pluginCoverageInputs = @()

        Invoke-Step "Running VS Code plugin tests with Vitest coverage" {
            npm run test:coverage --prefix src/Plugins/ef-querylens-vscode
        }

        $vscodeCoverageXml = Join-Path $vscodeDir "coverage/cobertura-coverage.xml"
        if (-not (Test-Path $vscodeCoverageXml)) {
            throw "VS Code plugin coverage report not found at $vscodeCoverageXml"
        }
        $pluginCoverageInputs += $vscodeCoverageXml

        $riderCoverageXml = Join-Path $riderDir "build/reports/jacoco/test/jacocoTestReport.xml"
        Invoke-Step "Running Rider plugin tests with JaCoCo report" {
            Push-Location $riderDir
            try {
                .\gradlew.bat test jacocoTestReport --no-daemon
            }
            finally {
                Pop-Location
            }
        }

        if (-not (Test-Path $riderCoverageXml)) {
            throw "Rider plugin JaCoCo report not found at $riderCoverageXml"
        }
        $pluginCoverageInputs += $riderCoverageXml

        $vsCoverageDir = Join-Path $pluginResultsDir "visualstudio"
        New-CleanDirectory -Path $vsCoverageDir

        Invoke-Step "Running Visual Studio plugin tests with Microsoft Code Coverage" {
            dotnet test $vsPluginTestsProject `
                -c $Configuration `
                --collect:"Code Coverage" `
                --results-directory $vsCoverageDir `
                --logger "console;verbosity=minimal"
        }

        Ensure-DotnetCoverage

        $vsCoverageFiles = Get-FilesOrFail -Root $vsCoverageDir -Filter "*.coverage" -ErrorMessage "No Visual Studio plugin .coverage file generated."
        $vsCobertura = Join-Path $vsCoverageDir "visualstudio-coverage.cobertura.xml"
        $vsCoverageInputs = @($vsCoverageFiles | ForEach-Object { $_.FullName })

        Invoke-Step "Converting Visual Studio plugin coverage to Cobertura" {
            & $dotnetCoverageExe merge -o $vsCobertura -f cobertura @vsCoverageInputs
        }

        if (-not (Test-Path $vsCobertura)) {
            throw "Visual Studio plugin Cobertura conversion failed at $vsCobertura"
        }

        $pluginCoverageInputs += $vsCobertura

        Write-CoverageSummary `
            -Title "Plugins" `
            -ReportPaths $pluginCoverageInputs `
            -SummaryDirectory (Join-Path $pluginResultsDir "report")
    }

    Write-Host ""
    Write-Host "Coverage artifacts written to: $runRoot" -ForegroundColor Yellow
}
finally {
    Pop-Location
}
