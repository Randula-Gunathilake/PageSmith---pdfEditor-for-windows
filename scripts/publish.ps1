<#
.SYNOPSIS
    Builds a release of PageSmith that runs on a PC without .NET installed.

.DESCRIPTION
    Runs the tests, then publishes src/PageSmith.App as a self-contained build for the chosen
    Windows runtime into artifacts/publish/<runtime>. By default the result is a single
    PageSmith.exe; pass -Folder for a regular folder layout (faster start-up, more files).

.EXAMPLE
    .\scripts\publish.ps1
    .\scripts\publish.ps1 -Runtime win-arm64 -Zip
    .\scripts\publish.ps1 -Folder -SkipTests
#>
param(
    [ValidateSet("win-x64", "win-arm64", "win-x86")]
    [string]$Runtime = "win-x64",
    [switch]$Folder,
    [switch]$SkipTests,
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "artifacts\publish\$Runtime"

if (-not $SkipTests) {
    Write-Host "Running tests..." -ForegroundColor Cyan
    dotnet test (Join-Path $root "tests\PageSmith.Tests") -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests failed; nothing was published." }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
$publishArgs = @(
    "publish", (Join-Path $root "src\PageSmith.App"),
    "-c", "Release",
    "-r", $Runtime,
    "--self-contained", "true",
    "-o", $out,
    "--nologo"
)
if (-not $Folder) {
    $publishArgs += @("-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true")
}

Write-Host "Publishing $Runtime to $out ..." -ForegroundColor Cyan
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

# Debug symbols and XML docs are not needed to run the app.
Get-ChildItem $out -Include *.pdb, *.xml -Recurse | Remove-Item -Force

if ($Zip) {
    $version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
    $zipPath = Join-Path $root "artifacts\PageSmith-$version-$Runtime.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zipPath
    Write-Host "Zipped to $zipPath" -ForegroundColor Green
}

Write-Host "Done. Run $(Join-Path $out 'PageSmith.exe')" -ForegroundColor Green
