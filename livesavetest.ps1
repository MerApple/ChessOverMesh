# Live test: Save when it is NOT your move.
# Fresh game => White (creator) to move, so the JOINER (Black) clicking Save is the
# "not your move" case. Expect:
#   - JOINER's game ends immediately (game-over popup), copy saved.
#   - JOINER system log clearly shows the save request was SENT, then ACKED.
#   - CREATOR gets the "Game ended" prompt and (choosing 'Save a copy') ends + acks.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$ErrorActionPreference = 'Stop'
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]::TrueCondition
$exe = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"
$gid = "s" + ((Get-Random -Maximum 0xFFFF).ToString("x4"))
$saveName = "sv" + ((Get-Random -Maximum 0xFFFF).ToString("x4"))
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
Log "Game='$gid'  saveName='$saveName'"
$pc = Start-Process $exe -PassThru
$pj = Start-Process $exe -PassThru
Start-Sleep -Seconds 3
try {
    $winC = Connect $pc.Id "http://192.168.2.183" "CREATOR"
    $winJ = Connect $pj.Id "http://192.168.2.218" "JOINER"

    # --- Creator hosts a fresh game (White); joiner joins (Black, NOT their move) ---
    Log "CREATOR: create fresh game '$gid'"
    Invoke-El (ById $winC "StartBtn")
    $cw = DescWin $winC "Create game" 20; if (-not $cw) { throw "Create dialog missing" }
    Set-Val (FirstType $cw ([System.Windows.Automation.ControlType]::Edit)) $gid
    Start-Sleep -Milliseconds 300
    # Force White so the joiner is Black and it is NOT the joiner's move.
    $roleCombo = (FirstType $cw ([System.Windows.Automation.ControlType]::ComboBox))
    Sel-Combo $roleCombo "White"
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

    Log "Waiting for JOINER to receive the board (be in game)..."
    $d = (Get-Date).AddSeconds(55); $ready = $false
    while ((Get-Date) -lt $d) { Start-Sleep -Seconds 2; if ((SysLines $winJ) -like "*board received from host*") { $ready = $true; break } }
    if (-not $ready) { throw "JOINER never entered the game" }
    Log "JOINER status: $(Txt (ById $winJ 'StatusText'))   (should be 'Waiting for White' = NOT joiner's move)"

    # --- JOINER (Black) saves while it is NOT their move ---
    Log "JOINER: clicking Save (not their move)"
    if (-not (Wait-Enabled (ById $winJ "SaveBtn") 10)) { throw "Save button not enabled" }
    Invoke-El (ById $winJ "SaveBtn")
    $sd = DescWin $winJ "Save game" 15; if (-not $sd) { throw "Save filename dialog missing" }
    Set-Val (FirstType $sd ([System.Windows.Automation.ControlType]::Edit)) $saveName
    Start-Sleep -Milliseconds 300
    Invoke-El (ByName $sd "OK")
    Start-Sleep -Seconds 2

    $joinerOver = ($null -ne (DescWin $winJ "Game over" 8))
    Log "JOINER game-over popup present (ended immediately): $joinerOver"
    Log "JOINER status: $(Txt (ById $winJ 'StatusText'))"

    # --- CREATOR gets the prompt; choose 'Save a copy' ---
    Log "CREATOR: waiting for 'Game ended' save prompt..."
    $prompt = DescWin $winC "Game ended" 60
    if (-not $prompt) { Log "CREATOR system msgs:"; SysLines $winC | ForEach-Object { Log "   sys> $_" }; throw "CREATOR never got the save prompt" }
    Log "CREATOR: got prompt; clicking 'Save a copy'"
    Invoke-El (ByName $prompt "Save a copy")

    # --- JOINER should see the request ACKED (green) ---
    Log "JOINER: waiting for the save request to be acknowledged..."
    $acked = $false; $d = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $d) { Start-Sleep -Seconds 2; if ((SysLines $winJ) -like "*opponent saved their copy*") { $acked = $true; break } }

    Log "================ RESULT ================"
    Log "JOINER status: $(Txt (ById $winJ 'StatusText'))"
    Log "JOINER system messages:"; SysLines $winJ | ForEach-Object { Log "   sys> $_" }
    $sent = [bool]((SysLines $winJ) -like "*Save request '$saveName' sent*")
    Log "checks: endedImmediately=$joinerOver  requestSentLogged=$sent  ackLogged=$acked"
    if ($joinerOver -and $sent -and $acked) {
        Log "PASS: save when not your move ended the game immediately, logged the request, and showed it acked."
    } else {
        Log "FAIL: see checks above."
    }
} finally {
    Log "Cleanup."
    Stop-Process -Id $pc.Id -ErrorAction SilentlyContinue
    Stop-Process -Id $pj.Id -ErrorAction SilentlyContinue
}
