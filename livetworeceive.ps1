# Replicate the user's setup: TWO GUI instances open at once (one->218, one->183).
# Send a chat from the 218 instance; confirm the 183 instance receives it.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$ErrorActionPreference = 'Stop'
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]::TrueCondition
$gui = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"
$tag = "CHAT" + (Get-Random -Maximum 9999)
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
function Invoke-El($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Val($e, $v) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function Wait-Enabled($e, $to = 30) { $d = (Get-Date).AddSeconds($to); while ((Get-Date) -lt $d) { if ($e.Current.IsEnabled) { return $true }; Start-Sleep -Milliseconds 300 }; return $false }
function ComboText($combo) { $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection(); if ($sel.Length -gt 0) { return $sel[0].Current.Name } else { return "(none)" } }
function ChatLines($win) { (ById $win "ChatList").FindAll($T::Children, $cond) | ForEach-Object { $_.Current.Name } }
function Sel-Combo($combo, $itemText) {
    $ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand(); Start-Sleep -Milliseconds 400
    $items = $combo.FindAll($T::Descendants, (New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem)))
    $t = $null; foreach ($it in $items) { if ($it.Current.Name -like "*$itemText*") { $t = $it; break } }
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
    Log "${label}: connected; channel='$(ComboText $combo)'"
    return $win
}

$pa = Start-Process $gui -PassThru   # -> 218 (sender)
$pb = Start-Process $gui -PassThru   # -> 183 (receiver under test)
Start-Sleep -Seconds 3
try {
    $winA = Connect $pa.Id "http://192.168.2.218" "GUI-218"
    $winB = Connect $pb.Id "http://192.168.2.183" "GUI-183"
    Start-Sleep -Seconds 2

    Log "GUI-218: sending chat '$tag'"
    Set-Val (ById $winA "ChatBox") $tag
    Start-Sleep -Milliseconds 300
    if (-not (Wait-Enabled (ById $winA "ChatSendBtn") 15)) { throw "GUI-218 Send not enabled" }
    Invoke-El (ById $winA "ChatSendBtn")

    Log "GUI-183: watching ChatList for '$tag' (both instances open the whole time)..."
    $seen = $false; $d = (Get-Date).AddSeconds(45)
    while ((Get-Date) -lt $d) { Start-Sleep -Seconds 2; if ((ChatLines $winB) -like "*$tag*") { $seen = $true; break } }

    Log "================ RESULT ================"
    Log "GUI-183 channel: '$(ComboText (ById $winB 'ChannelCombo'))'"
    Log "GUI-183 ChatList:"; ChatLines $winB | ForEach-Object { Log "   chat> $_" }
    if ($seen) { Log "PASS: 183 received the chat with BOTH instances open -> two instances are not the cause." }
    else { Log "FAIL: 183 did NOT receive the chat while both instances were open." }
} finally {
    Stop-Process -Id $pa.Id -ErrorAction SilentlyContinue
    Stop-Process -Id $pb.Id -ErrorAction SilentlyContinue
}
