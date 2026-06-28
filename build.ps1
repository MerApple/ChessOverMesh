# Compile-check both projects (no packaging) — fast feedback that everything builds.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "=== Building GUI ===" -ForegroundColor Cyan
dotnet build (Join-Path $root 'ChessOverMesh.Gui\ChessOverMesh.Gui.csproj') -c Release -p:IncludeAndroid=false

Write-Host "=== Building MAUI (net9.0-android) ===" -ForegroundColor Cyan
dotnet build (Join-Path $root 'ChessOverMesh.Maui\ChessOverMesh.Maui.csproj') -c Release -f net9.0-android

Write-Host "=== Build OK. ===" -ForegroundColor Green
