# Build + publish the WPF GUI as a self-contained, single-file Windows exe.
# Output: publish-single\ChessOverMesh.Gui.exe
#
# -p:IncludeAndroid=false is REQUIRED: the shared Meshtastic.csproj multi-targets net9.0-android, which would
# otherwise make this win-x64 self-contained publish try to restore the Android Mono runtime pack (NU1102).
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "=== Publishing GUI -> publish-single\ChessOverMesh.Gui.exe ===" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'ChessOverMesh.Gui\ChessOverMesh.Gui.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAndroid=false `
    -o (Join-Path $root 'publish-single')

Get-Item (Join-Path $root 'publish-single\ChessOverMesh.Gui.exe') |
    Select-Object FullName, @{N='SizeMB';E={[math]::Round($_.Length/1MB,1)}}, LastWriteTime
