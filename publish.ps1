<#
.SYNOPSIS
    Publishes the distributable artifacts for ChessOverMesh:
      * the desktop GUI as a single-file, self-contained Windows .exe
      * the MAUI phone app as a signed Android .apk

.DESCRIPTION
    Both outputs land in .\artifacts. Run from anywhere — paths are anchored to the
    script's own folder (the repo root). For a quick compile-only check use build.ps1.

    Notes baked in from getting these to publish cleanly:
      * The GUI references the Meshtastic library, which multi-targets net7.0;net9.0-android.
        A RID-specific desktop publish must pass IncludeAndroid=false, otherwise it tries to
        restore Android/Mono runtime packs that don't exist for win-x64.
      * The single-file exe is self-contained (runs without a .NET runtime installed) and is
        therefore large (~150 MB). It self-extracts native libs on first launch.
      * The APK is signed with the Android debug/default key — fine for sideloading/testing.
        For Play Store distribution, pass a real keystore via the -KeyStore params below.

.EXAMPLE
    .\publish.ps1
    Publishes both the exe and the apk in Release.

.EXAMPLE
    .\publish.ps1 -SkipApk
    Publishes only the Windows single-file exe.

.EXAMPLE
    .\publish.ps1 -KeyStore C:\keys\release.keystore -KeyAlias chess -StorePass *** -KeyPass ***
    Publishes the apk signed with a real release keystore.
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Rid           = 'win-x64',
    [switch]$SkipGui,
    [switch]$SkipApk,
    [switch]$Clean,

    # Optional real APK signing (all four required together). Omit to use the debug/default key, OR let them be
    # auto-loaded from a signing.properties file (see -SigningProperties) so the release key stays off the command line.
    [string]$KeyStore,
    [string]$KeyAlias,
    [string]$StorePass,
    [string]$KeyPass,

    # A key=value file (keystore/alias/storepass/keypass) holding the release signing config, kept OUTSIDE the repo.
    # Used automatically when the four -Key* params aren't all given. Default: ~\keys\chessovermesh\signing.properties.
    [string]$SigningProperties = (Join-Path $env:USERPROFILE 'keys\chessovermesh\signing.properties')
)

$ErrorActionPreference = 'Stop'
$root      = $PSScriptRoot
$guiProj   = Join-Path $root 'ChessOverMesh.Gui\ChessOverMesh.Gui.csproj'
$mauiProj  = Join-Path $root 'ChessOverMesh.Maui\ChessOverMesh.Maui.csproj'
$guiOut    = Join-Path $root 'publish-single'
$apkOut    = Join-Path $root 'publish-apk'

# If the release key wasn't passed explicitly, load it from the signing.properties file when present.
if (-not ($KeyStore -and $KeyAlias -and $StorePass -and $KeyPass) -and (Test-Path $SigningProperties)) {
    $cfg = @{}
    Get-Content $SigningProperties | Where-Object { $_ -match '^\s*[^#].*=' } | ForEach-Object {
        $k, $v = $_ -split '=', 2; $cfg[$k.Trim()] = $v.Trim()
    }
    if (-not $KeyStore)  { $KeyStore  = $cfg['keystore'] }
    if (-not $KeyAlias)  { $KeyAlias  = $cfg['alias'] }
    if (-not $StorePass) { $StorePass = $cfg['storepass'] }
    if (-not $KeyPass)   { $KeyPass   = $cfg['keypass'] }
    if ($KeyStore -and $KeyAlias -and $StorePass -and $KeyPass) {
        Write-Host "Using release signing config from $SigningProperties" -ForegroundColor DarkGray
    }
}

function Invoke-Step {
    param([string]$Title, [string[]]$DotnetArgs)
    Write-Host "`n=== $Title ===" -ForegroundColor Cyan
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "$Title failed (exit $LASTEXITCODE)." }
}

if ($Clean) {
    # Only clean a folder we're about to rebuild, so -Clean with -SkipGui/-SkipApk doesn't wipe the other artifact.
    $toClean = @()
    if (-not $SkipGui) { $toClean += $guiOut }
    if (-not $SkipApk) { $toClean += $apkOut }
    foreach ($dir in $toClean) {
        if (Test-Path $dir) { Write-Host "Cleaning $dir" -ForegroundColor DarkGray; Remove-Item $dir -Recurse -Force }
    }
}

# ---- Desktop GUI: single-file, self-contained .exe ----
if (-not $SkipGui) {
    Invoke-Step 'Publishing Windows GUI (single-file exe)' @(
        'publish', $guiProj,
        '-c', $Configuration,
        '-r', $Rid,
        '--self-contained', 'true',
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:IncludeAndroid=false',
        '-o', $guiOut,
        '--nologo'
    )
}

# ---- MAUI phone app: signed .apk ----
if (-not $SkipApk) {
    $apkArgs = @(
        'publish', $mauiProj,
        '-c', $Configuration,
        '-f', 'net9.0-android',
        '-p:AndroidPackageFormat=apk',
        '-o', $apkOut,
        '--nologo'
    )
    if ($KeyStore -and $KeyAlias -and $StorePass -and $KeyPass) {
        $apkArgs += @(
            '-p:AndroidKeyStore=true',
            "-p:AndroidSigningKeyStore=$KeyStore",
            "-p:AndroidSigningKeyAlias=$KeyAlias",
            "-p:AndroidSigningStorePass=$StorePass",
            "-p:AndroidSigningKeyPass=$KeyPass"
        )
        Write-Host 'Signing APK with the supplied release keystore.' -ForegroundColor DarkGray
    }
    Invoke-Step 'Publishing Android APK' $apkArgs

    # The publish names the APK after the app id (e.g. se.sa0mba.chessovermesh-Signed.apk). Copy the signed one to
    # a stable filename and drop the long app-id-named files, so the deliverable is just chessovermesh.apk.
    $signed = Get-ChildItem $apkOut -Filter '*-Signed.apk' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($signed) {
        Copy-Item $signed.FullName (Join-Path $apkOut 'chessovermesh.apk') -Force
        Get-ChildItem $apkOut -Filter 'se.*.apk' -ErrorAction SilentlyContinue | Remove-Item -Force
        Write-Host 'APK -> chessovermesh.apk' -ForegroundColor DarkGray
    }
}

# ---- Summary ----
Write-Host "`n=== Done - artifacts ===" -ForegroundColor Green
@($guiOut, $apkOut) | Where-Object { Test-Path $_ } |
    Get-ChildItem -Recurse -Include *.exe, *.apk -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike 'createdump*' } |
    Select-Object @{n = 'File'; e = { $_.FullName.Substring($root.Length + 1) } },
                  @{n = 'MB';   e = { [math]::Round($_.Length / 1MB, 1) } },
                  LastWriteTime |
    Format-Table -AutoSize

Write-Host "Install publish-apk\chessovermesh.apk on the phone; run publish-single\ChessOverMesh.Gui.exe on Windows." -ForegroundColor DarkGray
