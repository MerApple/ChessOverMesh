<#
.SYNOPSIS
    Cuts a new release: bumps the version, builds the signed APK + the Windows exe, pushes the commit, and
    creates a GitHub release with both files attached.

.DESCRIPTION
    One source of truth for the version is Directory.Build.props (AppVersion + AppVersionCode). This script
    rewrites those, runs publish.ps1, commits + pushes, and runs `gh release create vX.Y.Z` with the apk + exe.

    Prerequisites: git and gh on PATH (gh logged in), and the release keystore at
    ~\keys\chessovermesh\signing.properties (used automatically by publish.ps1).

.EXAMPLE
    .\release.ps1 -Version 1.0.3
    Bumps to 1.0.3, auto-increments the Android versionCode, builds, pushes, and publishes the release with
    auto-generated notes.

.EXAMPLE
    .\release.ps1 -Version 1.1.0 -Notes "Adds the spectator mode and fixes the reconnect loop."
    Same, with custom release notes.

.EXAMPLE
    .\release.ps1 -Version 1.0.3 -DryRun
    Bumps Directory.Build.props and builds both artifacts, but does NOT commit/push/release. Revert the version
    bump with `git checkout Directory.Build.props` if you don't proceed.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,   # display/semver version, e.g. 1.0.3
    [int]$VersionCode = 0,                     # Android versionCode; 0 = auto (current + 1)
    [string]$Notes,                            # release notes text; omit to auto-generate from commits
    [string]$NotesFile,                        # ...or a notes file (takes precedence over -Notes)
    [switch]$DryRun                            # build + bump locally, but don't commit/push/release
)

$ErrorActionPreference = 'Stop'
$root      = $PSScriptRoot
$propsPath = Join-Path $root 'Directory.Build.props'
$apk       = Join-Path $root 'publish-apk\chessovermesh.apk'
$exe       = Join-Path $root 'publish-single\ChessOverMesh.Gui.exe'
$tag       = "v$Version"

function Fail($msg) { throw $msg }
function Git { param([Parameter(ValueFromRemainingArguments)]$a) & git -C $root @a; if ($LASTEXITCODE -ne 0) { Fail "git $($a -join ' ') failed (exit $LASTEXITCODE)." } }

# ---- Validate ----
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "Version must be MAJOR.MINOR.PATCH (e.g. 1.0.3), got '$Version'." }
if (-not (Test-Path $propsPath)) { Fail "Directory.Build.props not found at $propsPath." }
$props = Get-Content $propsPath -Raw
$curCode = if ($props -match '<AppVersionCode>(\d+)</AppVersionCode>') { [int]$Matches[1] } else { 0 }
if ($VersionCode -le 0) { $VersionCode = $curCode + 1 }
if ($VersionCode -le $curCode) { Fail "VersionCode ($VersionCode) must be greater than the current ($curCode)." }
if (Git tag -l $tag) { Fail "Tag $tag already exists." }

Write-Host "=== Release $tag  (versionCode $curCode -> $VersionCode) ===" -ForegroundColor Cyan

# ---- Bump Directory.Build.props ----
$props = $props -replace '<AppVersion>[^<]*</AppVersion>', "<AppVersion>$Version</AppVersion>"
$props = $props -replace '<AppVersionCode>\d+</AppVersionCode>', "<AppVersionCode>$VersionCode</AppVersionCode>"
Set-Content -Path $propsPath -Value $props -NoNewline -Encoding UTF8
Write-Host "Bumped Directory.Build.props to $Version / $VersionCode" -ForegroundColor DarkGray

# ---- Build both artifacts (publish.ps1 throws on failure) ----
& (Join-Path $root 'publish.ps1')
if (-not (Test-Path $apk)) { Fail "APK not produced: $apk" }
if (-not (Test-Path $exe)) { Fail "EXE not produced: $exe" }

if ($DryRun) {
    Write-Host "DryRun: built $tag but did NOT commit/push/release. Revert with: git checkout Directory.Build.props" -ForegroundColor Yellow
    return
}

# ---- Commit + push (rebase in case the remote moved) ----
Git add -A
Git commit -q -m "Release $Version"
Git fetch origin -q
Git rebase origin/main
Git push -q origin main

# ---- Create the GitHub release with both assets ----
$notesArgs = if ($NotesFile) { @('--notes-file', $NotesFile) }
             elseif ($Notes)  { $tmp = Join-Path $env:TEMP "release-$tag.md"; Set-Content -Path $tmp -Value $Notes -Encoding UTF8; @('--notes-file', $tmp) }
             else             { @('--generate-notes') }

& gh release create $tag $apk $exe --title "ChessOverMesh $tag" @notesArgs
if ($LASTEXITCODE -ne 0) { Fail "gh release create failed (exit $LASTEXITCODE)." }

Write-Host "`n=== Released $tag ===" -ForegroundColor Green
& gh release view $tag --json url,assets --jq '.url, (.assets[].name)'
