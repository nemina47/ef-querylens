[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"

Set-Location (Resolve-Path (Join-Path $PSScriptRoot ".."))

Write-Host "Restoring local .NET tools if a manifest is present..."
if (Test-Path ".config/dotnet-tools.json") {
    dotnet tool restore
}
else {
    Write-Warning "No local tool manifest found. Install Husky with 'dotnet tool install --local Husky' and rerun this script."
}

Write-Host "Installing git hooks with Husky..."
dotnet tool run husky install

