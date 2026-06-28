# Build + publish BOTH apps: the GUI single-file exe and the MAUI signed APK.
# Outputs: publish-single\ChessOverMesh.Gui.exe  and  publish-apk\ChessOverMesh.apk
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

& (Join-Path $root 'publish-gui.ps1')
& (Join-Path $root 'publish-maui.ps1')

Write-Host "=== Done. ===" -ForegroundColor Green
