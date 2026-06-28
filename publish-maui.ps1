# Build + publish the MAUI Android app and copy the signed APK to a stable path.
# Output: publish-apk\ChessOverMesh.apk
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "=== Publishing MAUI (net9.0-android) ===" -ForegroundColor Cyan
dotnet publish (Join-Path $root 'ChessOverMesh.Maui\ChessOverMesh.Maui.csproj') -c Release -f net9.0-android

# The publish names the APK after the app id (se.sa0mba.chessovermesh); copy it to a stable filename.
$apk = Join-Path $root 'ChessOverMesh.Maui\bin\Release\net9.0-android\publish\se.sa0mba.chessovermesh-Signed.apk'
$dst = Join-Path $root 'publish-apk'
New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item $apk (Join-Path $dst 'ChessOverMesh.apk') -Force

Get-Item (Join-Path $dst 'ChessOverMesh.apk') |
    Select-Object FullName, @{N='SizeMB';E={[math]::Round($_.Length/1MB,1)}}, LastWriteTime
