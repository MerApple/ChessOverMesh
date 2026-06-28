# ChessOverMesh — build, publish & release

Chess + chat over a Meshtastic mesh. Multi-project .NET solution:

| Project | Target | Output |
|---|---|---|
| `ChessOverMesh.Gui` | `net8.0-windows` (WPF) | Windows desktop app |
| `ChessOverMesh.Maui` | `net9.0-android` | Android app |
| `Meshtastic.Proxy` | `net8.0` (console) | Share one radio with several apps |
| `ChessOverMesh` / `.Core` | shared mesh + chess logic | referenced by the above |
| `c-sharp-master/Meshtastic` | vendored protobuf lib, multi-targets `net7.0;net9.0-android` | — |

## Versioning — one source of truth
`Directory.Build.props` holds the version for every project:
- `<AppVersion>` — display/semver (e.g. `1.0.8`); used in-app, as the GitHub release **tag** (`vX.Y.Z`), and the APK display version.
- `<AppVersionCode>` — Android `versionCode` (integer); **must increase every release** or Android refuses to install the update.

Don't hand-edit these for a release — `release.ps1` rewrites them.

## Publish the artifacts — `publish.ps1`
Builds the distributables into fixed folders at the repo root:

```powershell
.\publish.ps1                 # all three: GUI exe + APK + Proxy exe
.\publish.ps1 -SkipApk        # only the Windows exes
.\publish.ps1 -SkipProxy      # GUI exe + APK (proxy unchanged)
.\publish.ps1 -Clean          # wipe-then-rebuild (only the folders being rebuilt)
```

| Artifact | Folder | File |
|---|---|---|
| Desktop GUI | `publish-single\` | `ChessOverMesh.Gui.exe` (~150 MB, self-contained single file) |
| Android app | `publish-apk\` | `chessovermesh.apk` (release-signed) |
| Proxy | `publish-proxy\` | `Meshtastic.Proxy.exe` (self-contained single file) |

Key facts baked into the script:
- The Windows exes publish **self-contained** with `-p:IncludeAndroid=false`. That flag is required: the GUI references the Meshtastic lib which multi-targets Android, and a `win-x64` publish otherwise tries to restore Android/Mono runtime packs that don't exist for that RID.
- The APK is signed with the **release keystore**, auto-loaded from `~\keys\chessovermesh\signing.properties` (a `keystore=/alias=/storepass=/keypass=` file kept **outside** the repo). The publish names the APK after the app id, then the script copies the `*-Signed.apk` to the stable `chessovermesh.apk`.
- App id is `se.sa0mba.chessovermesh`.

## Cut a release (bump + commit + push + upload to GitHub) — `release.ps1`
One command does everything: bump the version, build all three artifacts, commit, rebase, push, and create the GitHub release with the **APK + GUI exe + Proxy exe** attached.

```powershell
.\release.ps1 -Version 1.0.8                               # auto-increments versionCode; auto-generates notes
.\release.ps1 -Version 1.0.8 -NotesFile notes.md           # custom notes from a file (preferred)
.\release.ps1 -Version 1.0.8 -Notes "One-line summary."    # custom notes inline
.\release.ps1 -Version 1.0.8 -DryRun                        # bump + build only; no commit/push/release
```

What it does, in order:
1. Rewrites `<AppVersion>`/`<AppVersionCode>` in `Directory.Build.props` (versionCode = current + 1 unless `-VersionCode` given).
2. Runs `publish.ps1`; aborts if any of the three artifacts is missing.
3. `git add -A` → `commit -m "Release X.Y.Z"` → `fetch` → `rebase origin/main` → `push origin main`.
4. `gh release create vX.Y.Z <apk> <exe> <proxyExe> --title "ChessOverMesh vX.Y.Z"` with the notes.
5. Prints the release URL + asset list.

Prerequisites: `git` and `gh` on PATH, `gh` logged in, and the signing.properties keystore present.

Repo: **MerApple/ChessOverMesh** (public). Releases: https://github.com/MerApple/ChessOverMesh/releases

### Writing release notes
Drop a markdown file (e.g. in the scratchpad) and pass it via `-NotesFile`. Lead with the three downloads (apk / GUI exe / proxy exe), then a short "Changes since vPREV" list, and the reminder that a debug-signed older install must be uninstalled first. Convert the work since the last tag into user-facing bullets.

### PowerShell 5.1 gotchas the scripts already handle
- Native git stderr (e.g. the `LF -> CRLF` warning) becomes a *terminating* error under `$ErrorActionPreference='Stop'`. `Invoke-Git` flips it to `Continue` around the call and checks the exit code instead.
- The git wrapper is named **`Invoke-Git`**, not `Git` — PowerShell is case-insensitive, so a function `Git` calling `git` recurses into itself.
- It uses the automatic `$args` so git flags (`-A`, `-q`, `-m`) aren't bound to the function's own parameters.

When running these from a tool, prepend the machine+user PATH so `dotnet`/`git`/`gh` resolve:
`$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`
