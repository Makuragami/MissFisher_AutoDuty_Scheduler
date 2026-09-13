param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$DotNet = "dotnet",
    [string]$DeployPath = "",
    [switch]$OpenOutput
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "FisherDutyScheduler.csproj"

& $DotNet build $project -c $Configuration -p:Platform=x64 -warnaserror
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}

$package = Join-Path $PSScriptRoot "bin\x64\$Configuration\FisherDutyScheduler\latest.zip"
$rootPackage = Join-Path $PSScriptRoot "latest.zip"
Copy-Item -LiteralPath $package -Destination $rootPackage -Force
Write-Host "Package: $rootPackage"

# A dev-plugin path can be supplied once and is remembered locally for later builds.
$pathFile = Join-Path $PSScriptRoot ".deploy-path"
if ([string]::IsNullOrWhiteSpace($DeployPath) -and (Test-Path -LiteralPath $pathFile)) {
    $DeployPath = (Get-Content -LiteralPath $pathFile -Raw).Trim()
}

if (-not [string]::IsNullOrWhiteSpace($DeployPath)) {
    $DeployPath = [Environment]::ExpandEnvironmentVariables($DeployPath)
    if (-not (Test-Path -LiteralPath $DeployPath)) {
        New-Item -ItemType Directory -Path $DeployPath -Force | Out-Null
    }
    Set-Content -LiteralPath $pathFile -Value $DeployPath -NoNewline

    $outputDir = Join-Path $PSScriptRoot "bin\x64\$Configuration"
    Copy-Item -LiteralPath (Join-Path $outputDir "FisherDutyScheduler.dll") -Destination $DeployPath -Force
    Copy-Item -LiteralPath (Join-Path $outputDir "FisherDutyScheduler.json") -Destination $DeployPath -Force
    Write-Host "Deployed to: $DeployPath"
}

if ($OpenOutput) {
    Start-Process explorer.exe -ArgumentList "/select,`"$rootPackage`""
}
