# Builds the Windows installer (unsigned) with Velopack.
#   powershell -File tools/make-installer.ps1 -Version 0.8.0
# Output: artifacts/releases/SSGen-win-Setup.exe (plus the packages Velopack uses for updates).
# Needs: tools/fetch-deps.ps1 run once (scrcpy-server, FFmpeg DLLs, fonts are bundled from third_party/).
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$publish = Join-Path $root "artifacts\publish"
$releases = Join-Path $root "artifacts\releases"
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force $releases | Out-Null

foreach ($required in "third_party\scrcpy", "third_party\ffmpeg", "third_party\fonts") {
    if (-not (Test-Path (Join-Path $root $required))) {
        throw "Missing $required. Run: powershell -File tools/fetch-deps.ps1"
    }
}

Write-Host "Publishing SSGen $Version ($Configuration, win-x64, framework-dependent)..."
# Framework-dependent: the installer installs the .NET 10 Desktop Runtime if it's missing (Velopack --framework).
dotnet publish src/SSGen.App -c $Configuration -r win-x64 --self-contained false -p:Version=$Version -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Packing the installer..."
dotnet tool restore
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }
$env:DOTNET_ROLL_FORWARD = "Major"   # vpk targets an older .NET; let it run on .NET 10
dotnet vpk pack `
    --packId SSGen `
    --packVersion $Version `
    --packDir $publish `
    --mainExe SSGen.exe `
    --packTitle SSGen `
    --packAuthors SSGen `
    --icon (Join-Path $root "src\SSGen.App\Assets\ssgen.ico") `
    --framework net10.0-x64-desktop `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw "vpk pack failed" }

Get-ChildItem $releases | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
Write-Host "Installer: $(Join-Path $releases 'SSGen-win-Setup.exe') (unsigned: Windows SmartScreen will warn on first run)."
