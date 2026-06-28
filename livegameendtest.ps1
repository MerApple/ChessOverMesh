# Live test: moving in a game the opponent no longer has running.
# Creator (White) + joiner (Black) start a game. The JOINER disconnects+reconnects, which
# wipes its game state (no longer 'playing') but keeps it reachable on the mesh. The CREATOR,
# still in the game, makes a move (e2-e4). The joiner must reply ENDED, and the creator must
# then end its game.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$ErrorActionPreference = 'Stop'
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]::TrueCondition
$exe = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"
$gid = "e" + ((Get-Random -Maximum 0xFFFF).ToString("x4"))
function Log($m) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }

function Main-Win([int]$procId, [int]$to = 30) {
    $pc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $procId); $d = (Get-Date).AddSeconds($to)
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
function Wait-BtnLabel($win, $id, $label, $to = 40) { $d = (Get-Date).AddSeconds($to); while ((Get-Date) -lt $d) { $b = ById $win $id 3; if ($b -and $b.Current.Name -eq $label) { return $true }; Start-Sleep -Milliseconds 400 }; return $false }
function SysLines($win) { (ById $win "SystemList").FindAll($T::Children, $cond) | ForEach-Object { $_.Current.Name } }
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

# =====================================================================
Log "Game='$gid'"
$pc = Start-Process $exe -PassThru
$pj = Start-Process $exe -PassThru
Start-Sleep -Seconds 3
try {
    $winC = Connect $pc.Id "http://192.168.2.183" "CREATOR"
    $winJ = Connect $pj.Id "http://192.168.2.218" "JOINER"

    # --- Creator hosts White; joiner joins Black ---
    Log "CREATOR: create fresh White game '$gid'"
    Invoke-El (ById $winC "StartBtn")
    $cw = DescWin $winC "Create game" 20; if (-not $cw) { throw "Create dialog missing" }
    Set-Val (FirstType $cw ([System.Windows.Automation.ControlType]::Edit)) $gid
    Start-Sleep -Milliseconds 300
    Sel-Combo (FirstType $cw ([System.Windows.Automation.ControlType]::ComboBox)) "White"
    Start-Sleep -Milliseconds 300
    Invoke-El (ByName $cw "Create new game")
    Start-Sleep -Seconds 2

    Log "JOINER: waiting for announcement of '$gid'"
    $heard = $false; $d = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $d) { if ((SysLines $winJ) -like "*started game '$gid'*") { $heard = $true; break }; Start-Sleep -Seconds 3 }
    if (-not $heard) { throw "JOINER never heard the game" }
    Log "JOINER: joining"
    Invoke-El (ById $winJ "JoinBtn")
    $jw = DescWin $winJ "Join a game" 20; if (-not $jw) { throw "Join dialog missing" }
    Start-Sleep -Milliseconds 500
    $list = FirstType $jw ([System.Windows.Automation.ControlType]::List)
    $li = $list.FindAll($T::Children, $cond); $pick = $null
    foreach ($x in $li) { if ($x.Current.Name -like "*$gid*") { $pick = $x; break } }
    if (-not $pick -and $li.Count -gt 0) { $pick = $li[0] }
    if (-not $pick) { throw "game not listed" }
    $pick.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep -Milliseconds 300
    Invoke-El (ByName $jw "Join selected")

    Log "Waiting for JOINER to receive board..."
    $d = (Get-Date).AddSeconds(55); $ready = $false
    while ((Get-Date) -lt $d) { Start-Sleep -Seconds 2; if ((SysLines $winJ) -like "*board received from host*") { $ready = $true; break } }
    if (-not $ready) { throw "JOINER never entered the game" }
    Start-Sleep -Seconds 3   # let the creator process the JOIN/be solidly in-game
    Log "CREATOR status: $(Txt (ById $winC 'StatusText'))"

    # --- JOINER leaves the game by disconnect+reconnect (no longer running the game) ---
    Log "JOINER: disconnecting (wipes its game)"
    Invoke-El (ById $winJ "ConnectBtn")
    if (-not (Wait-BtnLabel $winJ "ConnectBtn" "Connect" 20)) { throw "JOINER did not disconnect" }
    Start-Sleep -Seconds 1
    Log "JOINER: reconnecting (stays reachable, but not in the game)"
    Invoke-El (ById $winJ "ConnectBtn")
    if (-not (Wait-BtnLabel $winJ "ConnectBtn" "Disconnect" 40)) { throw "JOINER did not reconnect" }
    Start-Sleep -Seconds 3

    # --- CREATOR (still playing, White to move) makes e2-e4 -> sq12 then sq28 ---
    Log "CREATOR: making move e2-e4 (sq12 -> sq28)"
    Invoke-El (ById $winC "sq12"); Start-Sleep -Milliseconds 600
    Invoke-El (ById $winC "sq28"); Start-Sleep -Milliseconds 600
    Log "CREATOR status after move: $(Txt (ById $winC 'StatusText'))"

    # --- CREATOR should receive ENDED and end the game ---
    Log "CREATOR: waiting for the opponent's ENDED reply..."
    $ended = $false; $d = (Get-Date).AddSeconds(75)
    while ((Get-Date) -lt $d) {
        Start-Sleep -Seconds 2
        if ((SysLines $winC) -like "*has already ended on their side*") { $ended = $true; break }
        if ((Txt (ById $winC 'StatusText')) -like "*Opponent has ended*") { $ended = $true; break }
    }
    $popup = ($null -ne (DescWin $winC "Game over" 5))

    Log "================ RESULT ================"
    Log "CREATOR status: $(Txt (ById $winC 'StatusText'))"
    Log "CREATOR system messages:"; SysLines $winC | ForEach-Object { Log "   sys> $_" }
    Log "checks: endedNoticeReceived=$ended  creatorGameOverPopup=$popup"
    if ($ended -and $popup) {
        Log "PASS: moving into a game the opponent no longer runs returned ENDED and ended the game here."
    } else {
        Log "FAIL: see checks above."
    }
} finally {
    Log "Cleanup."
    Stop-Process -Id $pc.Id -ErrorAction SilentlyContinue
    Stop-Process -Id $pj.Id -ErrorAction SilentlyContinue
}
