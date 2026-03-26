[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputRoot = "releases",

    [switch]$Clean,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Step([string]$name, [scriptblock]$body) {
    Write-Host "`n==> $name" -ForegroundColor Cyan
    & $body
}

function Assert-Exit([string]$label) {
    if ($LASTEXITCODE -ne 0) { throw "$label failed (exit code $LASTEXITCODE)" }
}

function Get-GradleProperty([string]$file, [string]$key) {
    $line = Get-Content $file | Where-Object { $_ -match "^\s*$([regex]::Escape($key))\s*=" } | Select-Object -First 1
    if (-not $line) { throw "Key `'$key`' not found in $file" }
    return $line.Split("=", 2)[1].Trim()
}

function Get-VsixVersion([string]$manifest) {
    $xml = [xml](Get-Content -Raw $manifest)
    $ns  = New-Object System.Xml.XmlNamespaceManager $xml.NameTable
    $ns.AddNamespace("v", "http://schemas.microsoft.com/developer/vsx-schema/2011")
    return $xml.SelectSingleNode("//v:Identity", $ns).GetAttribute("Version")
}

function Get-ChangelogSection([string]$file, [string]$ver) {
    $text    = Get-Content -Raw $file
    $escaped = [regex]::Escape($ver)
    foreach ($p in @(
        "(?ms)^##\s+\[$escaped\]\s*(?<body>.+?)(?=^##\s+\[|\z)",
        "(?ms)^##\s+\[v$escaped\]\s*(?<body>.+?)(?=^##\s+\[|\z)",
        "(?ms)^##\s+\[Unreleased\]\s*(?<body>.+?)(?=^##\s+\[|\z)"
    )) {
        $m = [regex]::Match($text, $p)
        if ($m.Success -and -not [string]::IsNullOrWhiteSpace($m.Groups["body"].Value)) {
            return $m.Groups["body"].Value.Trim()
        }
    }
    throw "No [$ver], [v$ver], or [Unreleased] section found in $file"
}

# Paths
$root        = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$vscodeDir   = Join-Path $root "src/Plugins/ef-querylens-vscode"
$riderDir    = Join-Path $root "src/Plugins/ef-querylens-rider"
$vsProj      = Join-Path $root "src/Plugins/ef-querylens-visualstudio/EFQueryLens.VisualStudio/EFQueryLens.VisualStudio.csproj"
$vsOut       = Join-Path $root "src/Plugins/ef-querylens-visualstudio/EFQueryLens.VisualStudio/bin/Release/net472"
$pkl         = Join-Path $vscodeDir "package.json"
$gradleProps = Join-Path $riderDir "gradle.properties"
$vsManifest  = Join-Path $root "src/Plugins/ef-querylens-visualstudio/EFQueryLens.VisualStudio/source.extension.vsixmanifest"
$changelog   = Join-Path $root "CHANGELOG.md"

$relVer    = Join-Path $root (Join-Path $OutputRoot $Version)
$relVscode = Join-Path $relVer "vscode"
$relRider  = Join-Path $relVer "rider"
$relVS     = Join-Path $relVer "visualstudio"
$vsixName  = "ef-querylens-vscode-$Version.vsix"

if ($Version -notmatch '^\d+\.\d+\.\d+([.\-][0-9A-Za-z]+)*$') {
    throw "Version must match semver format, e.g. 0.0.2"
}

Step "Validate plugin versions" {
    $vc = (Get-Content -Raw $pkl | ConvertFrom-Json).version
    $rv = Get-GradleProperty $gradleProps "pluginVersion"
    $vv = Get-VsixVersion $vsManifest
    $bad = @()
    if ($vc -ne $Version) { $bad += "VS Code package.json = $vc" }
    if ($rv -ne $Version) { $bad += "Rider gradle.properties = $rv" }
    if ($vv -ne $Version) { $bad += "VS vsixmanifest = $vv" }
    if ($bad.Count -gt 0) { throw "Version mismatch:`n  " + ($bad -join "`n  ") }
    Write-Host "All plugin versions match $Version" -ForegroundColor Green
}

Step "Prepare output directories" {
    if ($Clean -and (Test-Path $relVer)) { Remove-Item $relVer -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $relVscode | Out-Null
    New-Item -ItemType Directory -Force -Path $relRider  | Out-Null
    New-Item -ItemType Directory -Force -Path $relVS     | Out-Null
}

Step "Extract changelog release notes" {
    $body = Get-ChangelogSection $changelog $Version
    Set-Content -Path (Join-Path $relVer "release-notes.md") -Value "# EF QueryLens $Version`n`nSource: CHANGELOG.md`n`n$body" -Encoding UTF8
}

if (-not $SkipBuild) {
    Step "Build VS Code extension" {
        Push-Location $vscodeDir
        npm ci;                Assert-Exit "npm ci"
        npm run compile;       Assert-Exit "npm run compile"
        npm run prepare-runtime; Assert-Exit "npm run prepare-runtime"
        npx --yes "@vscode/vsce" package --pre-release -o $vsixName
        Assert-Exit "vsce package"
        Pop-Location
    }

    Step "Build Rider plugin" {
        dotnet build src/EFQueryLens.Lsp/EFQueryLens.Lsp.csproj -c Debug
        Assert-Exit "dotnet build EFQueryLens.Lsp"
        dotnet build src/EFQueryLens.Daemon/EFQueryLens.Daemon.csproj -c Debug
        Assert-Exit "dotnet build EFQueryLens.Daemon"
        Push-Location $riderDir
        $g = if ($IsWindows) { ".\gradlew.bat" } else { "./gradlew" }
        & $g buildPlugin --no-daemon -x buildSearchableOptions
        Assert-Exit "gradle buildPlugin"
        Pop-Location
    }

    Step "Build Visual Studio extension" {
        dotnet build $vsProj -c Release
        Assert-Exit "dotnet build VS extension"
    }
}
else {
    Write-Host "`n    (skipping builds)" -ForegroundColor Yellow
}

Step "Collect artifacts" {
    $vsixPath = Join-Path $vscodeDir $vsixName
    if (-not (Test-Path $vsixPath)) {
        $vsixPath = Get-ChildItem $vscodeDir -Filter "ef-querylens-vscode-*.vsix" |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1 -ExpandProperty FullName
        if (-not $vsixPath) { throw "No VS Code VSIX found in $vscodeDir" }
    }
    $riderZip = Get-ChildItem (Join-Path $riderDir "build/distributions") -Filter "*.zip" |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $riderZip) { throw "No Rider ZIP found" }
    $vsVsix = Get-ChildItem $vsOut -Filter "*.vsix" |
        Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $vsVsix) { throw "No VS VSIX found in $vsOut" }

    Copy-Item $vsixPath            $relVscode -Force
    Copy-Item $riderZip.FullName   $relRider  -Force
    Copy-Item $vsVsix.FullName     $relVS     -Force

    $all  = @()
    $all += Get-ChildItem $relVscode -File
    $all += Get-ChildItem $relRider  -File
    $all += Get-ChildItem $relVS     -File

    Write-Host "`nRelease artifacts:" -ForegroundColor Green
    $all | Select-Object @{N="File";E={$_.FullName.Replace($root + [IO.Path]::DirectorySeparatorChar,"")}},
                         @{N="MB";E={[math]::Round($_.Length/1MB,2)}} | Format-Table -AutoSize

    Write-Host "Release notes: $(Join-Path $relVer 'release-notes.md')" -ForegroundColor Green
}

Write-Host "`nDone. Release $Version packaged to releases/$Version" -ForegroundColor Green
