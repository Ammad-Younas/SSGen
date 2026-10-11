<#
.SYNOPSIS
  Downloads third-party binaries that are too large or not appropriate for git into third_party/.
  Run once after cloning (the build copies them next to the app):  powershell -File tools/fetch-deps.ps1

  - scrcpy-server v5.0.1 (Apache-2.0): pushed to the device for the live preview. Version must match ScrcpyServer.Version.
  - FFmpeg 9.0 LGPL *shared* build (BtbN): only avcodec/avutil/swscale DLLs, loaded dynamically for H.264 decoding.
#>
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = Join-Path $PSScriptRoot '..\third_party' | Resolve-Path -ErrorAction SilentlyContinue
if (-not $root) { $root = New-Item -ItemType Directory -Force (Join-Path $PSScriptRoot '..\third_party') }
$root = "$root"

# --- scrcpy-server -----------------------------------------------------------
$scrcpyVersion = '5.0.1'
$scrcpyDir = Join-Path $root 'scrcpy'
$scrcpyFile = Join-Path $scrcpyDir "scrcpy-server-v$scrcpyVersion"
if (-not (Test-Path $scrcpyFile)) {
    New-Item -ItemType Directory -Force $scrcpyDir | Out-Null
    Write-Host "Downloading scrcpy-server v$scrcpyVersion"
    Invoke-WebRequest "https://github.com/Genymobile/scrcpy/releases/download/v$scrcpyVersion/scrcpy-server-v$scrcpyVersion" -OutFile $scrcpyFile
    Invoke-WebRequest "https://raw.githubusercontent.com/Genymobile/scrcpy/v$scrcpyVersion/LICENSE" -OutFile (Join-Path $scrcpyDir 'LICENSE')
}

# --- FFmpeg (LGPL shared) ----------------------------------------------------
# Must match the FFmpeg.AutoGen major version in Directory.Packages.props (FFmpeg 9.0 -> avcodec-63).
$ffDir = Join-Path $root 'ffmpeg'
$required = @('avcodec-63.dll', 'avutil-61.dll', 'swscale-10.dll', 'swresample-7.dll')
$missing = $required | Where-Object { -not (Test-Path (Join-Path $ffDir $_)) }
if ($missing) {
    $candidates = @(
        'https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-09-14-16/ffmpeg-n9.0.2-24-gfd5d616c29-win64-lgpl-shared-9.0.zip',
        'https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip'
    )
    $tmp = Join-Path ([IO.Path]::GetTempPath()) ("ffmpeg-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $zip = Join-Path $tmp 'ffmpeg.zip'
    $ok = $false
    foreach ($url in $candidates) {
        try { Write-Host "Downloading $url"; Invoke-WebRequest $url -OutFile $zip; $ok = $true; break } catch { Write-Warning "Failed: $($_.Exception.Message)" }
    }
    if (-not $ok) { throw 'Could not download FFmpeg. Check your connection or update the URLs in tools/fetch-deps.ps1.' }
    Expand-Archive $zip -DestinationPath $tmp -Force
    $bin = Get-ChildItem $tmp -Recurse -Directory -Filter bin | Select-Object -First 1
    New-Item -ItemType Directory -Force $ffDir | Out-Null
    foreach ($dll in $required) {
        $src = Join-Path $bin.FullName $dll
        if (-not (Test-Path $src)) { throw "The FFmpeg build doesn't contain $dll; the major version changed. Update FFmpeg.AutoGen and this script together." }
        Copy-Item $src $ffDir -Force
    }
    $license = Get-ChildItem $tmp -Recurse -Filter 'LICENSE*' | Select-Object -First 1
    if ($license) { Copy-Item $license.FullName (Join-Path $ffDir 'LICENSE.txt') -Force }
    Get-ChildItem $tmp -Recurse -Directory | Where-Object Name -match '^ffmpeg-n' | Select-Object -First 1 | ForEach-Object { Set-Content -Encoding utf8 (Join-Path $ffDir 'BUILD.txt') "$($_.Name)`nDownloaded $(Get-Date -Format o)" }
    Remove-Item $tmp -Recurse -Force
}

# --- Sample APK for integration tests (test data only: never committed or shipped) ---
# Privacy Friendly Notes 2.2.2 from F-Droid (GPL-3.0, https://f-droid.org/packages/org.secuso.privacyfriendlynotes/)
$samples = Join-Path $root 'samples'
$sampleApk = Join-Path $samples 'privacyfriendlynotes-105.apk'
if (-not (Test-Path $sampleApk)) {
    New-Item -ItemType Directory -Force $samples | Out-Null
    Write-Host 'Downloading sample APK (Privacy Friendly Notes)'
    Invoke-WebRequest 'https://f-droid.org/repo/org.secuso.privacyfriendlynotes_105.apk' -OutFile $sampleApk
}

# --- Fonts for rendered screenshots (SIL Open Font License 1.1; shipped with the app) ---
# Static weights from the Google Fonts repository. File names must stay Family-Weight.ttf (see SSGen.Scene.Fonts).
$fontDir = Join-Path $root 'fonts'
$fontFiles = @(
    @{ Url = 'https://raw.githubusercontent.com/google/fonts/main/ofl/poppins/Poppins-Regular.ttf'; Name = 'Poppins-Regular.ttf' },
    @{ Url = 'https://raw.githubusercontent.com/google/fonts/main/ofl/poppins/Poppins-SemiBold.ttf'; Name = 'Poppins-SemiBold.ttf' },
    @{ Url = 'https://raw.githubusercontent.com/google/fonts/main/ofl/poppins/Poppins-Bold.ttf'; Name = 'Poppins-Bold.ttf' },
    @{ Url = 'https://raw.githubusercontent.com/google/fonts/main/ofl/poppins/Poppins-ExtraBold.ttf'; Name = 'Poppins-ExtraBold.ttf' },
    @{ Url = 'https://raw.githubusercontent.com/google/fonts/main/ofl/poppins/OFL.txt'; Name = 'Poppins-OFL.txt' },
    @{ Url = 'https://raw.githubusercontent.com/google/fonts/main/ofl/dmserifdisplay/DMSerifDisplay-Regular.ttf'; Name = 'DMSerifDisplay-Regular.ttf' },
    @{ Url = 'https://raw.githubusercontent.com/google/fonts/main/ofl/dmserifdisplay/OFL.txt'; Name = 'DMSerifDisplay-OFL.txt' }
)
New-Item -ItemType Directory -Force $fontDir | Out-Null
foreach ($font in $fontFiles) {
    $target = Join-Path $fontDir $font.Name
    if (-not (Test-Path $target)) { Write-Host "Downloading $($font.Name)"; Invoke-WebRequest $font.Url -OutFile $target }
}

Get-ChildItem $root -Recurse -File | ForEach-Object { "{0,10:N0}  {1}" -f $_.Length, $_.FullName.Substring($root.Length + 1) }
Write-Host 'third_party ready.'
