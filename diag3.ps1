Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
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
$A=[System.Windows.Automation.AutomationElement]; $T=[System.Windows.Automation.TreeScope]
function Log($m){ Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date),$m) }
function MainWin($procId){ $pc=New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty,$procId); for($i=0;$i -lt 40;$i++){ $e=$A::RootElement.FindFirst($T::Children,$pc); if($e){return $e}; Start-Sleep -Milliseconds 300 }; throw "no win" }
function ById($r,$id){ $r.FindFirst($T::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty,$id))) }
function ByName($r,$n){ $r.FindFirst($T::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,$n))) }
$script:cb=[W32+EnumProc]{ param($h,$l) $pp=0;[void][W32]::GetWindowThreadProcessId($h,[ref]$pp); if($pp -eq $script:tp -and [W32]::IsWindowVisible($h)){ $sb=New-Object System.Text.StringBuilder 512;[void][W32]::GetWindowText($h,$sb,$sb.Capacity); [void]$script:hits.Add($sb.ToString()) } return $true }
function AllTitles($procId){ $script:hits=New-Object System.Collections.ArrayList; $script:tp=$procId; [void][W32]::EnumWindows($script:cb,[IntPtr]::Zero); [System.GC]::KeepAlive($script:cb); return @($script:hits) }

$p = Start-Process $exe -PassThru
Start-Sleep -Seconds 3
$win = MainWin $p.Id
(ById $win "HostBox").GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("http://192.168.2.183")
(ById $win "ConnectBtn").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
$combo = ById $win "ChannelCombo"; for($i=0;$i -lt 60 -and -not $combo.Current.IsEnabled;$i++){Start-Sleep -Milliseconds 500}
$ec=$combo.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern);$ec.Expand();Start-Sleep -Milliseconds 500
$its=$combo.FindAll($T::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,[System.Windows.Automation.ControlType]::ListItem)))
foreach($it in $its){ if($it.Current.Name -like "*Robotnic*"){ $it.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select(); break } }
try{$ec.Collapse()}catch{}
Start-Sleep -Milliseconds 800

Log "Direct-invoke StartBtn"
$sw = [System.Diagnostics.Stopwatch]::StartNew()
(ById $win "StartBtn").GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Log "  StartBtn.Invoke returned after $($sw.ElapsedMilliseconds) ms"
Start-Sleep -Milliseconds 800
Log "Titles after StartBtn: $((AllTitles $p.Id) -join ' | ')"

# Create dialog: find it as descendant of main, set id, find loadBtn
$cwName = New-Object System.Windows.Automation.PropertyCondition($A::NameProperty,"Create game")
$cw = $win.FindFirst($T::Descendants,$cwName)
Log "Create dialog descendant found: $($cw -ne $null)"
$idBox = $cw.FindFirst($T::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)))
$idBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("diag3")
$loadBtn = ByName $cw "Load existing game"
Log "loadBtn found: $($loadBtn -ne $null)"

Log "Direct-invoke loadBtn"
$sw.Restart()
$loadBtn.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
Log "  loadBtn.Invoke returned after $($sw.ElapsedMilliseconds) ms"

# Poll window titles every second for 40s to see when the file dialog appears.
for($s=1; $s -le 40; $s++){
  Start-Sleep -Seconds 1
  $titles = AllTitles $p.Id
  Log ("  t+${s}s: " + ($titles -join ' | '))
  if ($titles -like "*saved game*") { Log "  -> file dialog detected at t+${s}s"; break }
}
Stop-Process -Id $p.Id -ErrorAction SilentlyContinue
