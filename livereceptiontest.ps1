# Does the GUI on 218 actually SHOW an incoming Robotnic message on a normal connect
# (without manually re-selecting the channel)? Sends a plain chat from 183 on Robotnic.
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
$ErrorActionPreference = 'Stop'
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
$cond = [System.Windows.Automation.Condition]::TrueCondition
$gui = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"
$con = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh\bin\Debug\net8.0\ChessOverMesh.exe"
$tag = "HELLO" + (Get-Random -Maximum 9999)
function Log($m) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }
function Main-Win([int]$procId, [int]$to = 30) {
    $pc = New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty, $procId); $d = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $d) { $e = $A::RootElement.FindFirst($T::Children, $pc); if ($e) { return $e }; Start-Sleep -Milliseconds 300 }
    throw "no main window"
}
function ById($r, $id, $to = 15) {
    $c = New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty, $id); $d = (Get-Date).AddSeconds($to)
    while ((Get-Date) -lt $d) { $e = $r.FindFirst($T::Descendants, $c); if ($e) { return $e }; Start-Sleep -Milliseconds 250 }
    return $null
}
function Invoke-El($e) { $e.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() }
function Set-Val($e, $v) { $e.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($v) }
function ComboText($combo) {
    $sel = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection()
    if ($sel.Length -gt 0) { return $sel[0].Current.Name } else { return "(none)" }
}
function ChatLines($win) { (ById $win "ChatList").FindAll($T::Children, $cond) | ForEach-Object { $_.Current.Name } }

$p = Start-Process $gui -PassThru
Start-Sleep -Seconds 3
try {
    $win = Main-Win $p.Id
    Log "Connecting GUI to 192.168.2.218 (NOT manually selecting a channel)..."
    Set-Val (ById $win "HostBox") "http://192.168.2.218"
    Invoke-El (ById $win "ConnectBtn")
    $combo = ById $win "ChannelCombo"; $d = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $d -and -not $combo.Current.IsEnabled) { Start-Sleep -Milliseconds 500 }
    if (-not $combo.Current.IsEnabled) { throw "GUI did not connect" }
    Start-Sleep -Seconds 2
    Log "GUI connected. Auto-selected channel shows: '$(ComboText $combo)'"

    Log "Sending plain chat from 183 on Robotnic (ch1): $tag"
    & $con --send http://192.168.2.183 1 "$tag" | Out-Null

    Log "Watching the GUI ChatList for the message (up to 40s)..."
    $seen = $false; $d = (Get-Date).AddSeconds(40)
    while ((Get-Date) -lt $d) { Start-Sleep -Seconds 2; if ((ChatLines $win) -like "*$tag*") { $seen = $true; break } }

    Log "================ RESULT ================"
    Log "GUI channel selection: '$(ComboText $combo)'"
    Log "GUI ChatList lines:"; ChatLines $win | ForEach-Object { Log "   chat> $_" }
    if ($seen) { Log "PASS: GUI on 218 received and displayed the Robotnic message." }
    else { Log "FAIL: GUI on 218 did NOT display the message (channel filter / key issue)." }
} finally {
    Stop-Process -Id $p.Id -ErrorAction SilentlyContinue
}
