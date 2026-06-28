# Live two-device test: creator (192.168.2.183) loads a save file and hosts;
# joiner (192.168.2.218) joins with NO file and must receive the board over the air.
# Decisive assertion: the loaded save is "black to move" at ply 3, so a correctly
# transferred position makes the BLACK joiner immediately on-move -- impossible in a
# fresh game (white moves first).

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@

$ErrorActionPreference = 'Stop'
$AID  = [System.Windows.Automation.AutomationElement]
$cond = [System.Windows.Automation.Condition]::TrueCondition
$Walker = [System.Windows.Automation.TreeWalker]::ControlViewWalker

$exe   = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"
$save  = "C:\Users\MerApple\AppData\Local\ChessOverMesh\saves\selftest.json"
$gameId = "j" + ((Get-Random -Maximum 0xFFFF).ToString("x4"))   # unique id, avoids stale mesh traffic

function Log($m) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }

# ---- Win32 window helpers -------------------------------------------------
# The EnumWindows callback delegate must be kept rooted for the whole call, or the GC can
# collect it mid-enumeration and EnumWindows stops early (missing windows intermittently).
$script:hwndCb = [Win32+EnumProc]{
    param($h, $l)
    $pid2 = 0; [void][Win32]::GetWindowThreadProcessId($h, [ref]$pid2)
    if ($pid2 -eq $script:hwTargetPid -and [Win32]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 512
        [void][Win32]::GetWindowText($h, $sb, $sb.Capacity)
        $t = $sb.ToString()
        if ($t -like "*$($script:hwTitleLike)*") {
            [void]$script:hwndHits.Add([pscustomobject]@{ H = $h; T = $t })
        }
    }
    return $true
}
function Get-Hwnds([int]$procId, [string]$titleLike) {
    $script:hwndHits = New-Object System.Collections.ArrayList
    $script:hwTargetPid = $procId
    $script:hwTitleLike = $titleLike
    [void][Win32]::EnumWindows($script:hwndCb, [IntPtr]::Zero)
    [System.GC]::KeepAlive($script:hwndCb)
    return @($script:hwndHits)
}

function Foreground([int]$procId, [string]$titleLike) {
    $w = Get-Hwnds $procId $titleLike | Select-Object -First 1
    if ($w) { [void][Win32]::ShowWindow($w.H, 9); [void][Win32]::SetForegroundWindow($w.H); Start-Sleep -Milliseconds 350; return $true }
    return $false
}

function Wait-Window([int]$procId, [string]$titleLike, [int]$timeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        if ((Get-Hwnds $procId $titleLike).Count -gt 0) { return $true }
        Start-Sleep -Milliseconds 400
    }
    return $false
}

# ---- UIA helpers ----------------------------------------------------------
function Get-MainWindow([int]$procId, [int]$timeoutSec = 30) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    while ((Get-Date) -lt $deadline) {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 400
    }
    throw "main window for pid $procId not found"
}

function Find-ById($root, [string]$id, [int]$timeoutSec = 15) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Find-ByName($root, [string]$name, [int]$timeoutSec = 15) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Window-ByTitle([int]$procId, [string]$titleLike, [int]$timeoutSec = 20) {
    # Returns the AutomationElement for a top-level window of the process whose title matches.
    $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
        foreach ($w in $wins) { if ($w.Current.Name -like "*$titleLike*") { return $w } }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Invoke-El($el) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $p.Invoke()
}

function Wait-Enabled($el, [int]$timeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) { if ($el.Current.IsEnabled) { return $true }; Start-Sleep -Milliseconds 300 }
    return $false
}

# Click a control by focusing it and pressing Space — does NOT block on the modal it opens
# (unlike UIA InvokePattern.Invoke, which blocks while a modal dialog is shown).
function Click-Key($el) {
    $el.SetFocus(); Start-Sleep -Milliseconds 250
    [System.Windows.Forms.SendKeys]::SendWait(" ")
}

function Set-Value($el, [string]$v) {
    $p = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $p.SetValue($v)
}

function Get-Text($el) {
    return $el.Current.Name
}

function Select-Combo($combo, [string]$itemText) {
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $ec.Expand(); Start-Sleep -Milliseconds 400
    $items = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants,
             (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $target = $null
    foreach ($it in $items) { if ($it.Current.Name -like "*$itemText*") { $target = $it; break } }
    if (-not $target -and $items.Count -gt 0) { $target = $items[$items.Count - 1] }  # fallback: last
    if ($target) {
        $sip = $target.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $sip.Select()
    }
    try { $ec.Collapse() } catch {}
    if ($target) { return $target.Current.Name } else { return "<none>" }
}

# Invoke a modal-opening control from a background job. UIA Invoke is cross-process and
# foreground-independent, but blocks while the modal it opens is shown — hence the job.
# $byProp is "AutomationIdProperty" or "NameProperty".
function Invoke-InJob([int]$procId, [string]$byProp, [string]$value) {
    return Start-Job -ScriptBlock {
        param($procId, $byProp, $value)
        Add-Type -AssemblyName UIAutomationClient
        Add-Type -AssemblyName UIAutomationTypes
        $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $win = $null
        for ($i = 0; $i -lt 40; $i++) {
            $win = $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
            if ($win) { break }; Start-Sleep -Milliseconds 300
        }
        $prop = [System.Windows.Automation.AutomationElement]::$byProp
        $c = New-Object System.Windows.Automation.PropertyCondition($prop, $value)
        $el = $null
        for ($i = 0; $i -lt 30; $i++) {
            $el = $win.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
            if ($el) { break }; Start-Sleep -Milliseconds 300
        }
        $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    } -ArgumentList $procId, $byProp, $value
}

# Get the UIA element for a window by its HWND (reliable for owned/modal dialogs that
# don't appear as direct children of the UIA root). Polls via Win32 EnumWindows.
function Window-ByHwnd([int]$procId, [string]$titleLike, [int]$timeoutSec = 20) {
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $w = Get-Hwnds $procId $titleLike | Select-Object -First 1
        if ($w) { return [System.Windows.Automation.AutomationElement]::FromHandle($w.H) }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function First-ByType($root, [System.Windows.Automation.ControlType]$ct) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ct)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
}

# Find an owned dialog as a DESCENDANT of the main window (WPF owned windows appear under
# the owner in the UIA tree; resolving them this way — rather than via FromHandle — yields
# elements whose InvokePattern actually registers).
function Find-DescWindow($mainWin, [string]$title, [int]$timeoutSec = 20) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $title)
    $deadline = (Get-Date).AddSeconds($timeoutSec)
    while ((Get-Date) -lt $deadline) {
        $el = $mainWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $c)
        if ($el) { return $el }
        Start-Sleep -Milliseconds 300
    }
    return $null
}

function Connect-Instance([int]$procId, [string]$hostUrl, [string]$label) {
    Log "${label}: getting main window"
    $win = Get-MainWindow $procId
    Log "${label}: setting host = $hostUrl"
    $hostBox = Find-ById $win "HostBox"
    Set-Value $hostBox $hostUrl
    Log "${label}: clicking Connect"
    $connect = Find-ById $win "ConnectBtn"
    Invoke-El $connect
    # Wait for the channel combo to become enabled (connection succeeded + channels loaded).
    $combo = Find-ById $win "ChannelCombo"
    $deadline = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $deadline -and -not $combo.Current.IsEnabled) { Start-Sleep -Milliseconds 500 }
    if (-not $combo.Current.IsEnabled) { throw "${label}: channel combo never enabled (connect failed?)" }
    Log "${label}: connected; selecting Robotnic channel"
    $chosen = Select-Combo $combo "Robotnic"   # UIA, foreground-independent
    Log "${label}: channel = $chosen"
    Start-Sleep -Milliseconds 600
    # Clear the AES key box so both instances match (empty = no app-layer encryption).
    # SetFocus targets THIS instance's box (the two main windows share a title, so a
    # title-based foreground would be ambiguous).
    $key = Find-ById $win "KeyBox"
    try { $key.SetFocus() } catch {}
    Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait("^a{DEL}")
    Start-Sleep -Milliseconds 400
    return $win
}

# ===========================================================================
Log "Game id for this run: '$gameId'"
Log "Launching two app instances..."
$pCreator = Start-Process $exe -PassThru
$pJoiner  = Start-Process $exe -PassThru
Start-Sleep -Seconds 3

try {
    $winC = Connect-Instance $pCreator.Id "http://192.168.2.183" "CREATOR"
    $winJ = Connect-Instance $pJoiner.Id  "http://192.168.2.218" "JOINER"

    # ---- CREATOR: Create -> Load existing game -> pick selftest.json ----
    # WPF's InvokePattern.Invoke returns immediately (it does NOT block on the modal it
    # opens), so we invoke directly from the main runspace. Cross-process UIA also means
    # field-setting is foreground-independent. Only the Win32 file dialog needs keyboard.
    Log "CREATOR: opening Create dialog"
    $startBtn = Find-ById $winC "StartBtn"
    if (-not (Wait-Enabled $startBtn 20)) { throw "CREATOR: Create button never enabled" }
    Invoke-El $startBtn                                          # opens 'Create game' modal (non-blocking)
    $createWin = Find-DescWindow $winC "Create game" 20
    if (-not $createWin) { throw "CREATOR: Create dialog never appeared" }
    Log "CREATOR: Create dialog open; setting game id = $gameId"
    $idBox = First-ByType $createWin ([System.Windows.Automation.ControlType]::Edit)
    Set-Value $idBox $gameId
    Start-Sleep -Milliseconds 300
    Log "CREATOR: clicking 'Load existing game'"
    $loadBtn = Find-ByName $createWin "Load existing game"
    Invoke-El $loadBtn                                          # opens file-open modal (non-blocking)

    $fileOpen = $false
    for ($s = 1; $s -le 30; $s++) {
        Start-Sleep -Seconds 1
        $titles = (Get-Hwnds $pCreator.Id "") | ForEach-Object { $_.T }
        if ($titles -like "*saved game*") { Log "CREATOR: file dialog detected at t+${s}s"; $fileOpen = $true; break }
        if ($s -le 5 -or $s % 5 -eq 0) { Log ("CREATOR: t+${s}s windows: " + ($titles -join ' | ')) }
    }
    if (-not $fileOpen) { throw "CREATOR: file-open dialog never appeared" }
    Log "CREATOR: file dialog open; typing filename = $save"
    # The Vista file dialog's filename field/Open button aren't reachable via UIA, but the
    # dialog has a UNIQUE title and grabs foreground on open -> drive it by keyboard.
    # The filename edit is focused by default.
    Foreground $pCreator.Id "Load a saved game" | Out-Null
    Start-Sleep -Milliseconds 700
    [System.Windows.Forms.SendKeys]::SendWait("^a")
    Start-Sleep -Milliseconds 200
    [System.Windows.Forms.SendKeys]::SendWait($save)
    Start-Sleep -Milliseconds 400
    [System.Windows.Forms.SendKeys]::SendWait("{ENTER}")
    Start-Sleep -Seconds 3
    Get-Job | Remove-Job -Force -ErrorAction SilentlyContinue

    $statusC = Get-Text (Find-ById $winC "StatusText")
    Log "CREATOR status after hosting: $statusC"

    # ---- Wait for the JOINER to hear the announcement (Robotnic is slow) ----
    Log "JOINER: waiting for the game announcement to arrive..."
    $sysJ = Find-ById $winJ "SystemList"
    $heard = $false
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        $items = $sysJ.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
        foreach ($it in $items) { if ($it.Current.Name -like "*$gameId*started*" -or $it.Current.Name -like "*started game '$gameId'*") { $heard = $true; break } }
        if ($heard) { break }
        Start-Sleep -Seconds 3
    }
    if (-not $heard) {
        Log "JOINER: announcement not seen in system list within timeout; dumping system messages:"
        $sysJ.FindAll([System.Windows.Automation.TreeScope]::Children, $cond) | ForEach-Object { Log ("   sys> " + $_.Current.Name) }
        throw "JOINER never heard the game announcement"
    }
    Log "JOINER: announcement heard. Opening Join dialog."

    # ---- JOINER: Join... -> select the game -> Join selected (no file) ----
    $joinOpenBtn = Find-ById $winJ "JoinBtn"
    if (-not (Wait-Enabled $joinOpenBtn 20)) { throw "JOINER: Join button never enabled" }
    Invoke-El $joinOpenBtn                          # opens 'Join a game' modal (non-blocking)
    $joinWin = Find-DescWindow $winJ "Join a game" 20
    if (-not $joinWin) { throw "JOINER: Join dialog never appeared" }
    Start-Sleep -Milliseconds 500
    # Find the ListBox and select the entry for our game.
    $list = $joinWin.FindFirst([System.Windows.Automation.TreeScope]::Descendants,
            (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::List)))
    $listItems = $list.FindAll([System.Windows.Automation.TreeScope]::Children, $cond)
    $picked = $null
    foreach ($li in $listItems) { if ($li.Current.Name -like "*$gameId*") { $picked = $li; break } }
    if (-not $picked -and $listItems.Count -gt 0) { $picked = $listItems[0] }
    if (-not $picked) { throw "JOINER: no game in the Join list" }
    Log ("JOINER: selecting list entry: " + $picked.Current.Name)
    $picked.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    Start-Sleep -Milliseconds 300
    $joinSel = Find-ByName $joinWin "Join selected"
    Invoke-El $joinSel   # closes dialog + sends JOIN (non-modal handler, won't block)

    # ---- Wait for the board to arrive and the joiner to enter the game ----
    # The decisive signal is the joiner becoming on-move as Black ("Your move - Black"),
    # which is only possible if the black-to-move position transferred. The transient
    # "waiting for the host to send the board" status must NOT satisfy the wait.
    # Robotnic is slow: JOIN auto-retransmits at +20s and the host re-sends the board on
    # each JOIN it hears, so allow ~50s.
    Log "JOINER: waiting for BOARD from host (the board comes over the air)..."
    $statusJ = ""
    $deadline = (Get-Date).AddSeconds(55)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 2
        $statusJ = Get-Text (Find-ById $winJ "StatusText")
        if ($statusJ -like "*Your move*" -or $statusJ -like "*your turn*") { Log "JOINER: entered game -> $statusJ"; break }
        if ($statusJ -like "*Join failed*" -or $statusJ -like "*did not respond*") { Log "JOINER: join failed -> $statusJ"; break }
    }
    Start-Sleep -Seconds 2
    $statusJ = Get-Text (Find-ById $winJ "StatusText")
    $statusC = Get-Text (Find-ById $winC "StatusText")

    $sysLines = (Find-ById $winJ "SystemList").FindAll([System.Windows.Automation.TreeScope]::Children, $cond) | ForEach-Object { $_.Current.Name }

    Log "================ RESULT ================"
    Log "CREATOR final status: $statusC"
    Log "JOINER  final status: $statusJ"
    Log "JOINER system messages:"
    $sysLines | ForEach-Object { Log ("   sys> " + $_) }

    # Decisive checks (the joiner's turn-status is overwritten by the join confirmation, so we
    # verify the position transfer via the combination below):
    #  1. joiner joined as BLACK with NO file, board received over the air
    #  2. creator (WHITE) is "Waiting for Black" -> it is Black's move
    # The saved position is black-to-move at ply 3. If the FEN had not transferred, a fresh
    # game would be White-to-move and the creator would read "Your move - White" instead.
    $joinerIsBlack    = ($statusJ -like "*you are Black*")
    $boardReceived    = [bool]($sysLines | Where-Object { $_ -like "*board received from host*" })
    $blackToMove      = ($statusC -like "*Waiting for Black*")
    Log "checks: joinerIsBlack=$joinerIsBlack boardReceived=$boardReceived creatorWaitingForBlack=$blackToMove"
    if ($joinerIsBlack -and $boardReceived -and $blackToMove) {
        Log "PASS: joiner received the loaded black-to-move position over the air with no file; creator is waiting for Black."
    } else {
        Log "FAIL: position transfer not confirmed. See statuses above."
    }
} finally {
    Log "Cleaning up: closing both app instances."
    Get-Job | Remove-Job -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $pCreator.Id -ErrorAction SilentlyContinue
    Stop-Process -Id $pJoiner.Id -ErrorAction SilentlyContinue
}
