param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$DotNet = "dotnet"
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "FisherDutyScheduler.csproj"

& $DotNet build $project -c $Configuration -p:Platform=x64 -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$package = Join-Path $PSScriptRoot "bin\x64\$Configuration\FisherDutyScheduler\latest.zip"
Write-Host "Package: $package"
