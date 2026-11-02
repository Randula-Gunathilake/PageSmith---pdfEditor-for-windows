<#
.SYNOPSIS
    Builds the PageSmith installer, PageSmith-Setup-<version>-<runtime>.exe.

.DESCRIPTION
    Publishes a self-contained folder build with publish.ps1 (which runs the tests first unless
    -SkipTests), then compiles installer/PageSmith.iss with Inno Setup 6 into artifacts/installer.
    The installer runs on PCs without .NET and needs no admin rights for a per-user install.

    Install Inno Setup once with:
        winget install --id JRSoftware.InnoSetup -e --scope user

.EXAMPLE
    .\scripts\build-installer.ps1
    .\scripts\build-installer.ps1 -Runtime win-arm64 -SkipTests
#>
param(
    [ValidateSet("win-x64", "win-arm64", "win-x86")]
    [string]$Runtime = "win-x64",
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 isn't installed. Install it with: winget install --id JRSoftware.InnoSetup -e --scope user"
}

# A folder build starts faster than the single-file one and suits an installed app.
& (Join-Path $PSScriptRoot "publish.ps1") -Runtime $Runtime -Folder -SkipTests:$SkipTests

$version = ([xml](Get-Content (Join-Path $root "Directory.Build.props"))).Project.PropertyGroup.Version
$source = Join-Path $root "artifacts\publish\$Runtime"
$output = Join-Path $root "artifacts\installer"

Write-Host "Compiling the installer..." -ForegroundColor Cyan
& $iscc /Q "/DAppVersion=$version" "/DRuntime=$Runtime" "/DSourceDir=$source" "/DOutputDir=$output" (Join-Path $root "installer\PageSmith.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

Write-Host "Done. Installer: $(Join-Path $output "PageSmith-Setup-$version-$Runtime.exe")" -ForegroundColor Green
