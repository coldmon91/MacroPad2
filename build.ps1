#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the RSoft.MacroPad solution (RSoft.MacroPad, RSoft.MacroPad.BLL, RSoft.MacroPad.Cli).

.PARAMETER Configuration
    Build configuration: Debug or Release. Defaults to Release.

.PARAMETER Clean
    Run 'dotnet clean' before building.

.EXAMPLE
    ./build.ps1
    ./build.ps1 -Configuration Debug
    ./build.ps1 -Clean
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$solutionPath = Join-Path $PSScriptRoot "src\RSoft.MacroPad.sln"

if (-not (Test-Path $solutionPath)) {
    throw "Solution file not found: $solutionPath"
}

if ($Clean) {
    Write-Host "==> Cleaning ($Configuration)..." -ForegroundColor Cyan
    dotnet clean $solutionPath -c $Configuration
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "==> Restoring packages..." -ForegroundColor Cyan
dotnet restore $solutionPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> Building ($Configuration)..." -ForegroundColor Cyan
dotnet build $solutionPath -c $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "==> Build succeeded ($Configuration)." -ForegroundColor Green
