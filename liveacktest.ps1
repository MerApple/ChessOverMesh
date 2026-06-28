# Live regression test for: "no CREATEACK after a previous game ended".
# Root cause was a MODAL game-over MessageBox blocking the opponent's mesh poll.
# Scenario: creator+joiner play game A; creator RESIGNS (both get a game-over popup);
# WITHOUT dismissing the joiner's popup, the creator creates game B. The joiner must
# still acknowledge game B (proving the now-MODELESS popup no longer blocks the poll).

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
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
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]::TrueCondition
$exe = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"
$idA = "a" + ((Get-Random -Maximum 0xFFFF).ToString("x4"))
$idB = "b" + ((Get-Random -Maximum 0xFFFF).ToString("x4"))
function Log($m) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }

# Rooted EnumWindows callback (keep alive so GC can't collect it mid-enumeration).
$script:hwndCb = [Win32+EnumProc]{
    param($h, $l)
    $pp = 0; [void][Win32]::GetWindowThreadProcessId($h, [ref]$pp)
    if ($pp -eq $script:tp -and [Win32]::IsWindowVisible($h)) {
        $sb = New-Object System.Text.StringBuilder 512
        [void][Win32]::GetWindowText($h, $sb, $sb.Capacity)
        [void]$script:hits.Add($sb.ToString())
    }
    return $true
}
function Get-Titles([int]$procId) {
    $script:hits = New-Object System.Collections.ArrayList; $script:tp = $procId
    [void][Win32]::EnumWindows($script:hwndCb, [IntPtr]::Zero); [System.GC]::KeepAlive($script:hwndCb)
    return @($script:hits)
}
function Wait-Title([int]$procId, [string]$like, [int]$to = 20) {
    $d = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $d) { if ((Get-Titles $procId) -like "*$like*") { return $true }; Start-Sleep -Milliseconds 400 }
    return $false
}
function Foreground([int]$procId, [string]$like) {
    $script:hits = New-Object System.Collections.ArrayList; $script:tp = $procId
    [void][Win32]::EnumWindows($script:hwndCb, [IntPtr]::Zero); [System.GC]::KeepAlive($script:hwndCb)
    # find the hwnd matching the title
    $script:fh = [IntPtr]::Zero; $script:fl = $like
    $cb2 = [Win32+EnumProc]{
        param($h, $l)
        $pp = 0; [void][Win32]::GetWindowThreadProcessId($h, [ref]$pp)
        if ($pp -eq $script:tp -and [Win32]::IsWindowVisible($h)) {
            $sb = New-Object System.Text.StringBuilder 512; [void][Win32]::GetWindowText($h, $sb, $sb.Capacity)
            if ($sb.ToString() -like "*$($script:fl)*") { $script:fh = $h }
        }
        return $true
    }
    [void][Win32]::EnumWindows($cb2, [IntPtr]::Zero); [System.GC]::KeepAlive($cb2)
    if ($script:fh -ne [IntPtr]::Zero) { [void][Win32]::ShowWindow($script:fh, 9); [void][Win32]::SetForegroundWindow($script:fh); Start-Sleep -Milliseconds 350; return $true }
    return $false
}

function Main-Win([int]$procId, [int]$to = 30) {
    $pc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $procId)
    $d = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $d) { $e = $A::RootElement.FindFirst($T::Children, $pc); if ($e) { return $e }; Start-Sleep -Milliseconds 300 }
    throw "no main window for $procId"
}
function ById($r, $id, $to = 15) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id); $d = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $d) { $e = $r.FindFirst($T::Descendants, $c); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
    return $null
}
function ByName($r, $n, $to = 15) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $n); $d = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $d) { $e = $r.FindFirst($T::Descendants, $c); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
    return $null
}
function DescWin($main, $title, $to = 20) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty, $title); $d = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $d) { $e = $main.FindFirst($T::Descendants, $c); if ($e) { return $e }; Start-Sleep -Milliseconds 300 }
    return $null
}
function FirstType($r, $ct) { $r.FindFirst($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, $ct))) }
function Invoke-El($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Val($e, $v) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Txt($e) { $e.Current.Name }
function Wait-Enabled($e, $to = 20) { $d = (Get-Date).AddSeconds($to); while ((Get-Date) -lt $d) { if ($e.Current.IsEnabled) { return $true }; Start-Sleep -Milliseconds 300 }; return $false }
function Sel-Combo($combo, $itemText) {
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand(); Start-Sleep -Milliseconds 400
    $items = $combo.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $t = $null; foreach ($it in $items) { if ($it.Current.Name -like "*$itemText*") { $t = $it; break } }
    if (-not $t -and $items.Count -gt 0) { $t = $items[$items.Count - 1] }
    if ($t) { $t.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select() }
    try { $ec.Collapse() } catch {}
}

function Connect([int]$procId, [string]$url, [string]$label) {
    Log "${label}: connect $url"
    $win = Main-Win $procId
    Set-Val (ById $win "HostBox") $url
    Invoke-El (ById $win "ConnectBtn")
    $combo = ById $win "ChannelCombo"; $d = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $d -and -not $combo.Current.IsEnabled) { Start-Sleep -Milliseconds 500 }
    if (-not $combo.Current.IsEnabled) { throw "${label}: not connected" }
    Sel-Combo $combo "Robotnic"
    $key = ById $win "KeyBox"; try { $key.SetFocus() } catch {}; Start-Sleep -Milliseconds 300
    [System.Windows.Forms.SendKeys]::SendWait("^a{DEL}"); Start-Sleep -Milliseconds 400
    Log "${label}: connected, Robotnic selected"
    return $win
}

# Create a FRESH game (no file): Create -> set id -> 'Create new game'.
function Create-Fresh([int]$procId, $win, [string]$gid, [string]$label) {
    Log "${label}: creating fresh game '$gid'"
    $btn = ById $win "StartBtn"
    if (-not (Wait-Enabled $btn 20)) { throw "${label}: Create not enabled" }
    Invoke-El $btn
    $cw = DescWin $win "Create game" 20
    if (-not $cw) { throw "${label}: Create dialog missing" }
    Set-Val (FirstType $cw ([System.Windows.Automation.ControlType]::Edit)) $gid
    Start-Sleep -Milliseconds 300
    Invoke-El (ByName $cw "Create new game")
    Start-Sleep -Seconds 2
}

# =====================================================================
Log "Game A='$idA'  Game B='$idB'"
Log "Launching two instances..."
$pc = Start-Process $exe -PassThru
$pj = Start-Process $exe -PassThru
Start-Sleep -Seconds 3

try {
    $winC = Connect $pc.Id "http://192.168.2.183" "CREATOR"
    $winJ = Connect $pj.Id "http://192.168.2.218" "JOINER"

    # --- Game A: create + join so both are playing ---
    Create-Fresh $pc.Id $winC $idA "CREATOR"
    Log "CREATOR after create A: $(Txt (ById $winC 'StatusText'))"

    Log "JOINER: waiting for game A announcement..."
    $sysJ = ById $winJ "SystemList"
    if (-not (Wait-Title $pj.Id "Chess over Meshtastic" 2)) {}   # noop, ensure function warm
    $heard = $false; $d = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $d) {
        $items = $sysJ.FindAll($T::Children, $cond)
        foreach ($it in $items) { if ($it.Current.Name -like "*started game '$idA'*") { $heard = $true; break } }
        if ($heard) { break }; Start-Sleep -Seconds 3
    }
    if (-not $heard) { throw "JOINER never heard game A" }
    Log "JOINER: heard A; opening Join dialog"
    Invoke-El (ById $winJ "JoinBtn")
    $jw = DescWin $winJ "Join a game" 20
    if (-not $jw) { throw "JOINER: Join dialog missing" }
    Start-Sleep -Milliseconds 500
    $list = FirstType $jw ([System.Windows.Automation.ControlType]::List)
    $li = $list.FindAll($T::Children, $cond); $pick = $null
    foreach ($x in $li) { if ($x.Current.Name -like "*$idA*") { $pick = $x; break } }
    if (-not $pick -and $li.Count -gt 0) { $pick = $li[0] }
    if (-not $pick) { throw "JOINER: game A not listed" }
    $pick.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
    Invoke-El (ByName $jw "Join selected")

    Log "Waiting for both to be in game A..."
    $d = (Get-Date).AddSeconds(55); $inGame = $false
    while ((Get-Date) -lt $d) {
        Start-Sleep -Seconds 2
        $sc = Txt (ById $winC "StatusText"); $sj = Txt (ById $winJ "StatusText")
        if (($sc -like "*Game on*" -or $sc -like "*Your move*" -or $sc -like "*Waiting for*") -and ($sj -like "*you are*" -or $sj -like "*Your move*" -or $sj -like "*Waiting for*")) { $inGame = $true; break }
    }
    Log "CREATOR status: $(Txt (ById $winC 'StatusText'))"
    Log "JOINER  status: $(Txt (ById $winJ 'StatusText'))"
    if (-not $inGame) { Log "WARN: could not confirm both in game A via status; continuing" }

    # --- CREATOR resigns game A -> both get a (modeless) game-over popup ---
    Log "CREATOR: resigning game A"
    Invoke-El (ById $winC "ResignBtn")
    if (Wait-Title $pc.Id "Resign" 8) { Foreground $pc.Id "Resign" | Out-Null; Start-Sleep -Milliseconds 400; [System.Windows.Forms.SendKeys]::SendWait("{ENTER}") }
    Log "CREATOR: waiting for JOINER to reach game over (popup)..."
    $joinerOver = Wait-Title $pj.Id "Game over" 60
    Log "JOINER 'Game over' popup present: $joinerOver"
    if (-not $joinerOver) {
        Log "JOINER system messages:"; $sysJ.FindAll($T::Children, $cond) | ForEach-Object { Log ("   sys> " + $_.Current.Name) }
        throw "JOINER never ended game A"
    }
    Log "JOINER popup LEFT OPEN deliberately (this is the regression condition)."

    # --- CREATOR creates game B while JOINER's game-over popup is still open ---
    Create-Fresh $pc.Id $winC $idB "CREATOR"
    Log "CREATOR: waiting for game B to be ACKNOWLEDGED by joiner (whose popup is open)..."
    $acked = $false; $d = (Get-Date).AddSeconds(70)
    while ((Get-Date) -lt $d) {
        Start-Sleep -Seconds 2
        $sc = Txt (ById $winC "StatusText")
        if ($sc -like "*acknowledged*") { $acked = $true; break }
    }
    $stillOpen = (Get-Titles $pj.Id) -like "*Game over*"

    Log "================ RESULT ================"
    Log "CREATOR final status: $(Txt (ById $winC 'StatusText'))"
    Log "JOINER 'Game over' popup still open during ack: $([bool]$stillOpen)"
    Log "CREATOR game B acknowledged: $acked"
    if ($acked) {
        Log "PASS: opponent acknowledged the new game even with its game-over popup open."
    } else {
        Log "FAIL: new game B was not acknowledged."
        Log "JOINER system messages:"; $sysJ.FindAll($T::Children, $cond) | ForEach-Object { Log ("   sys> " + $_.Current.Name) }
    }
} finally {
    Log "Cleanup."
    Get-Job | Remove-Job -Force -ErrorAction SilentlyContinue
    Stop-Process -Id $pc.Id -ErrorAction SilentlyContinue
    Stop-Process -Id $pj.Id -ErrorAction SilentlyContinue
}
