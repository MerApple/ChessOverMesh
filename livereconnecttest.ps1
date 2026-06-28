# Live regression test for: "no CREATEACK after disconnect/reconnect".
# Root cause: on reconnect a fresh mesh client defaults to ChannelIndex 0; because ChannelItem
# is a record (value equality) the ComboBox preserves its selection across the ItemsSource swap,
# so setting SelectedIndex is a no-op, SelectionChanged never fires, and the channel is never
# pushed onto the new client -> all traffic on the actual play channel is dropped by Dispatch.
# Fix: ConnectAsync explicitly calls ApplySelectedChannel() after PopulateChannels.
#
# Scenario: both connect on Robotnic; the JOINER (acker) disconnects + reconnects WITHOUT
# re-touching the channel combo; the CREATOR then creates a game. The joiner must still ack.

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$ErrorActionPreference = 'Stop'
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]::TrueCondition
$exe = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"
$idA = "r" + ((Get-Random -Maximum 0xFFFF).ToString("x4"))
function Log($m) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }

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
function Wait-BtnLabel($win, $id, $label, $to = 40) {
    $d = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $d) { $b = ById $win $id 3; if ($b -and $b.Current.Name -eq $label) { return $true }; Start-Sleep -Milliseconds 400 }
    return $false
}
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
function Create-Fresh($win, [string]$gid, [string]$label) {
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
Log "Game A='$idA'"
$pc = Start-Process $exe -PassThru
$pj = Start-Process $exe -PassThru
Start-Sleep -Seconds 3
try {
    $winC = Connect $pc.Id "http://192.168.2.183" "CREATOR"
    $winJ = Connect $pj.Id "http://192.168.2.218" "JOINER"

    # --- JOINER disconnects then reconnects, WITHOUT re-selecting the channel ---
    Log "JOINER: clicking Disconnect"
    Invoke-El (ById $winJ "ConnectBtn")          # connected -> Disconnect
    if (-not (Wait-BtnLabel $winJ "ConnectBtn" "Connect" 20)) { throw "JOINER: did not disconnect" }
    Log "JOINER: disconnected. Reconnecting (NOT touching the channel combo)..."
    Start-Sleep -Seconds 1
    Invoke-El (ById $winJ "ConnectBtn")          # disconnected -> Connect (cached)
    if (-not (Wait-BtnLabel $winJ "ConnectBtn" "Disconnect" 40)) { throw "JOINER: did not reconnect" }
    Log "JOINER: reconnected (channel combo NOT re-selected by the test)."
    Start-Sleep -Seconds 2

    # --- CREATOR creates a game on Robotnic; reconnected JOINER must ack it ---
    Create-Fresh $winC $idA "CREATOR"
    Log "CREATOR: waiting for ack from the reconnected joiner..."
    $acked = $false; $d = (Get-Date).AddSeconds(70)
    while ((Get-Date) -lt $d) {
        Start-Sleep -Seconds 2
        $sc = Txt (ById $winC "StatusText")
        if ($sc -like "*acknowledged*") { $acked = $true; break }
    }
    Log "================ RESULT ================"
    Log "CREATOR final status: $(Txt (ById $winC 'StatusText'))"
    Log "JOINER  final status: $(Txt (ById $winJ 'StatusText'))"
    Log "JOINER system messages:"
    (ById $winJ "SystemList").FindAll($T::Children, $cond) | ForEach-Object { Log ("   sys> " + $_.Current.Name) }
    if ($acked) {
        Log "PASS: reconnected opponent acknowledged the new game (channel correctly re-applied)."
    } else {
        Log "FAIL: no ack after reconnect - channel not re-applied?"
    }
} finally {
    Log "Cleanup."
    Stop-Process -Id $pc.Id -ErrorAction SilentlyContinue
    Stop-Process -Id $pj.Id -ErrorAction SilentlyContinue
}
