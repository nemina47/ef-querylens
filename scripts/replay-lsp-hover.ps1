param(
    [string]$ServerDll = "src/EFQueryLens.Lsp/bin/Debug/net10.0/EFQueryLens.Lsp.dll",
    [string]$Workspace = "",
    [string]$FilePath = "",
    [int]$Line = -1,
    [int]$Character = -1,
    [string]$CasesFile = "",
    [switch]$DebugServer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-AbsolutePath([string]$PathValue) {
    if ([string]::IsNullOrWhiteSpace($PathValue)) { return "" }
    return [System.IO.Path]::GetFullPath($PathValue)
}

$serverAbs = Resolve-AbsolutePath $ServerDll
if (-not (Test-Path $serverAbs)) {
    throw "Server DLL not found: $serverAbs"
}

$workspaceAbs = if ([string]::IsNullOrWhiteSpace($Workspace)) {
    Resolve-AbsolutePath (Get-Location).Path
} else {
    Resolve-AbsolutePath $Workspace
}

$argsList = @("`"$serverAbs`"", "--hover-replay")

if (-not [string]::IsNullOrWhiteSpace($CasesFile)) {
    $casesAbs = Resolve-AbsolutePath $CasesFile
    if (-not (Test-Path $casesAbs)) {
        throw "Cases file not found: $casesAbs"
    }
    $argsList += @("--cases-file", "`"$casesAbs`"")
} elseif (-not [string]::IsNullOrWhiteSpace($FilePath) -and $Line -ge 0 -and $Character -ge 0) {
    $fileAbs = Resolve-AbsolutePath $FilePath
    if (-not (Test-Path $fileAbs)) {
        throw "File not found: $fileAbs"
    }
    $argsList += @("--file", "`"$fileAbs`"", "--line", "$Line", "--char", "$Character")
} else {
    throw "Provide either -CasesFile or all of -FilePath, -Line, -Character."
}

Write-Host "Running hover replay via LSP executable"
Write-Host "Server: $serverAbs"
Write-Host "Workspace: $workspaceAbs"

$env:QUERYLENS_WORKSPACE = $workspaceAbs
$env:QUERYLENS_CLIENT = "replay-script"
$env:QUERYLENS_DEBUG = if ($DebugServer) { "true" } else { "false" }

$command = "dotnet " + ($argsList -join " ")
Write-Host "Command: $command"

Invoke-Expression $command
