Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms
Add-Type @"
using System; using System.Text; using System.Runtime.InteropServices;
public class W32 {
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
}
"@
$ErrorActionPreference = 'Stop'
$exe = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"

function Log($m) { Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date), $m) }
function MainWin($procId) {
    $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    for ($i=0; $i -lt 40; $i++) {
        $el = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children, $pidCond)
        if ($el) { return $el }; Start-Sleep -Milliseconds 300
    }
    throw "no main window"
}
function ById($root,$id) {
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,$id)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
}
function DumpWins($procId,$tag) {
    $pidCond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty, $procId)
    $wins = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $pidCond)
    Log "$tag : $($wins.Count) top-level window(s):"
    foreach ($w in $wins) { Log ("     win> '" + $w.Current.Name + "'  class=" + $w.Current.ClassName) }
}

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 3
$win = MainWin $p.Id
ById $win "HostBox" | ForEach-Object { $_.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("http://192.168.2.183") }
(ById $win "ConnectBtn").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$combo = ById $win "ChannelCombo"
for ($i=0;$i -lt 60 -and -not $combo.Current.IsEnabled;$i++){ Start-Sleep -Milliseconds 500 }
Log "combo enabled = $($combo.Current.IsEnabled)"
# select Robotnic
$ec = $combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern); $ec.Expand(); Start-Sleep -Milliseconds 500
$items = $combo.FindAll([System.Windows.Automation.TreeScope]::Descendants,(New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
foreach ($it in $items){ if ($it.Current.Name -like "*Robotnic*"){ $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); break } }
try { $ec.Collapse() } catch {}
Start-Sleep -Milliseconds 800
$start = ById $win "StartBtn"
Log "StartBtn enabled = $($start.Current.IsEnabled)"

function DumpW32($procId,$tag) {
    $script:w32hits = New-Object System.Collections.ArrayList
    $script:w32pid = $procId
    $cb = [W32+EnumProc]{
        param($h,$l)
        $pp=0; [void][W32]::GetWindowThreadProcessId($h,[ref]$pp)
        if ($pp -eq $script:w32pid -and [W32]::IsWindowVisible($h)) {
            $sb=New-Object System.Text.StringBuilder 512; [void][W32]::GetWindowText($h,$sb,$sb.Capacity)
            [void]$script:w32hits.Add($sb.ToString())
        }
        return $true
    }
    [void][W32]::EnumWindows($cb,[IntPtr]::Zero)
    Log "$tag : Win32 visible windows: $($script:w32hits.Count)"
    foreach ($t in $script:w32hits) { Log ("     w32> '" + $t + "'") }
}
function DumpDesc($win,$tag) {
    $wc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty,[System.Windows.Automation.ControlType]::Window)
    $ws = $win.FindAll([System.Windows.Automation.TreeScope]::Descendants,$wc)
    Log "$tag : descendant Window elements: $($ws.Count)"
    foreach ($w in $ws) { Log ("     desc-win> '" + $w.Current.Name + "'") }
}

DumpWins $p.Id "BEFORE click"
DumpW32 $p.Id "BEFORE click"
Log "Attempt 1: UIA InvokePattern.Invoke on StartBtn from a background job"
$job = Start-Job -ScriptBlock {
    param($procId)
    Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes
    $pc = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ProcessIdProperty,$procId)
    $w = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Children,$pc)
    $c = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::AutomationIdProperty,"StartBtn")
    $b = $w.FindFirst([System.Windows.Automation.TreeScope]::Descendants,$c)
    try { $b.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke(); "invoked ok" }
    catch { "INVOKE ERROR: " + $_.Exception.Message }
} -ArgumentList $p.Id
Start-Sleep -Seconds 5
Receive-Job $job | ForEach-Object { Log ("   job result: " + $_) }
Remove-Job $job -Force -ErrorAction SilentlyContinue
DumpWins $p.Id "AFTER job-invoke (UIA root children)"
DumpW32 $p.Id "AFTER job-invoke (Win32)"
DumpDesc $win "AFTER job-invoke (descendant windows of main)"

Log "Attempt 2: keyboard (SetFocus StartBtn + Space)"
$start.SetFocus(); Start-Sleep -Milliseconds 400
[System.Windows.Forms.SendKeys]::SendWait(" ")
Start-Sleep -Seconds 3
DumpW32 $p.Id "AFTER keyboard (Win32)"

Stop-Process -Id $p.Id -ErrorAction SilentlyContinue
