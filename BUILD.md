# Build & publish

Commands to build and package the two apps:

- **GUI** — `ChessOverMesh.Gui` (WPF, `net8.0-windows`) → a self-contained single-file `ChessOverMesh.Gui.exe`.
- **MAUI** — `ChessOverMesh.Maui` (`net9.0-android`) → a signed `ChessOverMesh.apk`.

Both reference `ChessOverMesh` / `ChessOverMesh.Core`, which compile the shared chess/mesh source and the
vendored `c-sharp-master\Meshtastic` protobuf library.

Run from the repo root in PowerShell. The `.ps1` scripts resolve paths relative to themselves, so they work from
anywhere.

## Scripts

| Script | What it does |
| --- | --- |
| `publish-all.ps1` | Build + publish both: the GUI `.exe` and the MAUI `.apk`. |
| `publish-gui.ps1` | Build + publish only the GUI single-file `.exe` → `publish-single\`. |
| `publish-maui.ps1` | Build + publish only the MAUI APK → `publish-apk\ChessOverMesh.apk`. |
| `build.ps1` | Compile-check both projects (no packaging) — fast feedback. |

```powershell
.\publish-all.ps1      # both artifacts
.\publish-gui.ps1      # GUI exe only
.\publish-maui.ps1     # MAUI apk only
.\build.ps1            # just compile (no publish)
```

## The raw commands

### GUI — self-contained single-file exe → `publish-single\ChessOverMesh.Gui.exe`

```powershell
dotnet publish "ChessOverMesh.Gui\ChessOverMesh.Gui.csproj" `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:IncludeAndroid=false `
    -o "publish-single"
```

**`-p:IncludeAndroid=false` is required.** The shared `c-sharp-master\Meshtastic\Meshtastic.csproj`
multi-targets `net7.0;net9.0-android`. A `-r win-x64 --self-contained` publish otherwise tries to restore the
Android Mono runtime pack (`Microsoft.NETCore.App.Runtime.Mono.win-x64`), which fails with `NU1102`. The flag
drops the Android target for this desktop publish (the csproj reads it via a `Condition`).

### MAUI — signed APK → `publish-apk\ChessOverMesh.apk`

```powershell
dotnet publish "ChessOverMesh.Maui\ChessOverMesh.Maui.csproj" -c Release -f net9.0-android

# The publish output is named after the app id; copy it to a stable path:
Copy-Item "ChessOverMesh.Maui\bin\Release\net9.0-android\publish\se.sa0mba.chessovermesh-Signed.apk" `
          "publish-apk\ChessOverMesh.apk" -Force
```

### Build only (compile-check, no packaging)

```powershell
dotnet build "ChessOverMesh.Gui\ChessOverMesh.Gui.csproj"  -c Release -p:IncludeAndroid=false
dotnet build "ChessOverMesh.Maui\ChessOverMesh.Maui.csproj" -c Release -f net9.0-android
```

## Notes

- `dotnet` (the .NET SDK) and the MAUI Android workload must be installed (`dotnet workload install maui-android`).
- The MAUI APK is signed with the default debug-style signing config from the project; replace it with a release
  keystore before any store distribution.
- `publish-single\` and `publish-apk\` are the output folders the team already uses for the artifacts.
