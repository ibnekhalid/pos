# Builds the Restaurant POS desktop installer: publishes the unpackaged MAUI app, then compiles
# installer\Restaurant.Desktop.iss with Inno Setup 6. Run on Windows from the repo root:
#
#   .\installer\build-installer.ps1                  # version from ApplicationDisplayVersion in the .csproj
#   .\installer\build-installer.ps1 -SkipPublish     # re-package the last publish
#   .\installer\build-installer.ps1 -PublishOnly     # just the folder (no Inno Setup needed)
#   .\installer\build-installer.ps1 -Version 0.4     # a release version (the apps pipeline sets it)
#
# Output: installer\output\Restaurant-POS-Setup-<version>.exe (+ its SHA-256). Needs the .NET 10 SDK with
# the maui-windows workload, and Inno Setup 6 (winget install JRSoftware.InnoSetup) unless -PublishOnly.
#
# The publish is self-contained (.NET and the Windows App SDK ship inside the app folder), so a counter PC
# needs nothing installed except the WebView2 runtime. Bump ApplicationDisplayVersion and
# ApplicationVersion in src\clients\Restaurant.Desktop\Restaurant.Desktop.csproj before each release.
param(
    [string]$Version,
    [switch]$SkipPublish,
    [switch]$PublishOnly,
    [string]$IsccPath
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Root "src\clients\Restaurant.Desktop\Restaurant.Desktop.csproj"
$PublishDir = Join-Path $PSScriptRoot "publish"
$Script = Join-Path $PSScriptRoot "Restaurant.Desktop.iss"
$Framework = "net10.0-windows10.0.19041.0"

if (-not $Version) {
    [xml]$Csproj = Get-Content -Raw $Project
    $Version = ($Csproj.Project.PropertyGroup | ForEach-Object { $_.ApplicationDisplayVersion } | Where-Object { $_ } | Select-Object -First 1)
    if (-not $Version) { throw "ApplicationDisplayVersion not found in $Project" }
}
Write-Host "=== Restaurant POS desktop $Version ==="

if (-not $SkipPublish) {
    Write-Host "=== Publish ($Framework, Release, win-x64, self-contained) ==="
    if (Test-Path $PublishDir) { Remove-Item -Recurse -Force $PublishDir }
    # Release builds call https://api.restaurant.dsnoopdoc.com (MauiProgram.ProductionApi).
    & dotnet publish $Project -f $Framework -c Release -r win-x64 --self-contained true `
        -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true "-p:ApplicationDisplayVersion=$Version" -o $PublishDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
}

$Exe = Join-Path $PublishDir "Restaurant.Desktop.exe"
if (-not (Test-Path $Exe)) { throw "$Exe not found - run without -SkipPublish" }
$Size = "{0:N0} MB" -f ((Get-ChildItem -Recurse -File $PublishDir | Measure-Object Length -Sum).Sum / 1MB)
Write-Host "Published to $PublishDir ($Size)"
if ($PublishOnly) { return }

if (-not $IsccPath) {
    $Candidates = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) }
    $IsccPath = $Candidates | Select-Object -First 1
}
if (-not $IsccPath) {
    throw "Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) or pass -IsccPath."
}

$Bootstrapper = Join-Path $PSScriptRoot "redist\MicrosoftEdgeWebview2Setup.exe"
if (Test-Path $Bootstrapper) {
    Write-Host "Bundling the WebView2 bootstrapper (installed only where missing)"
} else {
    Write-Host "No redist\MicrosoftEdgeWebview2Setup.exe: the installer only warns when WebView2 is missing"
}

Write-Host "=== Inno Setup ($IsccPath) ==="
& $IsccPath "/DAppVersion=$Version" "/DPublishDir=$PublishDir" $Script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit $LASTEXITCODE)" }

$Setup = Join-Path $PSScriptRoot "output\Restaurant-POS-Setup-$Version.exe"
$Hash = (Get-FileHash -Algorithm SHA256 $Setup).Hash.ToLowerInvariant()
"$Hash  Restaurant-POS-Setup-$Version.exe" | Set-Content -Encoding ascii "$Setup.sha256"
Write-Host ""
Write-Host "Installer: $Setup"
Write-Host "SHA-256:   $Hash"
Write-Host "Release:   scp it to /root/downloads/restaurant/ on the VM, then set RP_DESKTOP_LATEST_VERSION=$Version"
Write-Host "           and RP_DESKTOP_DOWNLOAD_URL=https://restaurant.dsnoopdoc.com/downloads/Restaurant-POS-Setup-$Version.exe"
Write-Host "           and re-run the deploy (docs/DEPLOYMENT.md, 'Desktop app releases')."
