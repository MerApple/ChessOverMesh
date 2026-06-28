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
$exe  = "C:\Users\MerApple\source\repos\MeshTastic\ChessOverMesh.Gui\bin\Debug\net8.0-windows\ChessOverMesh.Gui.exe"
$save = "C:\Users\MerApple\AppData\Local\ChessOverMesh\saves\selftest.json"
$A = [System.Windows.Automation.AutomationElement]
$T = [System.Windows.Automation.TreeScope]
function Log($m){ Write-Host ("[{0:HH:mm:ss}] {1}" -f (Get-Date),$m) }
function MainWin($procId){ $pc=New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty,$procId); for($i=0;$i -lt 40;$i++){ $e=$A::RootElement.FindFirst($T::Children,$pc); if($e){return $e}; Start-Sleep -Milliseconds 300 }; throw "no win" }
function ById($r,$id){ $c=New-Object System.Windows.Automation.PropertyCondition($A::AutomationIdProperty,$id); $r.FindFirst($T::Descendants,$c) }
function Hwnds($procId,$like){ $script:hh=New-Object System.Collections.ArrayList; $script:hp=$procId; $script:hl=$like; $cb=[W32+EnumProc]{ param($h,$l) $pp=0;[void][W32]::GetWindowThreadProcessId($h,[ref]$pp); if($pp -eq $script:hp -and [W32]::IsWindowVisible($h)){ $sb=New-Object System.Text.StringBuilder 512;[void][W32]::GetWindowText($h,$sb,$sb.Capacity); if($sb.ToString() -like "*$($script:hl)*"){[void]$script:hh.Add($h)} } return $true }; [void][W32]::EnumWindows($cb,[IntPtr]::Zero); return @($script:hh) }
function WinByHwnd($procId,$like,$to=20){ $d=(Get-Date).AddSeconds($to); while((Get-Date) -lt $d){ $h=Hwnds $procId $like|Select-Object -First 1; if($h){return $A::FromHandle($h)}; Start-Sleep -Milliseconds 300 }; return $null }
function InvokeJob($procId,$prop,$val){ Start-Job -ScriptBlock { param($procId,$prop,$val); Add-Type -AssemblyName UIAutomationClient; Add-Type -AssemblyName UIAutomationTypes; $A=[System.Windows.Automation.AutomationElement]; $T=[System.Windows.Automation.TreeScope]; $pc=New-Object System.Windows.Automation.PropertyCondition($A::ProcessIdProperty,$procId); $w=$null; for($i=0;$i -lt 40;$i++){ $w=$A::RootElement.FindFirst($T::Children,$pc); if($w){break}; Start-Sleep -Milliseconds 300 }; $p=$A::$prop; $c=New-Object System.Windows.Automation.PropertyCondition($p,$val); $el=$null; for($i=0;$i -lt 30;$i++){ $el=$w.FindFirst($T::Descendants,$c); if($el){break}; Start-Sleep -Milliseconds 300 }; $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke() } -ArgumentList $procId,$prop,$val }

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
Log "opening Create"
InvokeJob $p.Id "AutomationIdProperty" "StartBtn" | Out-Null
$cw = WinByHwnd $p.Id "Create game" 20
Log "Create open: $($cw -ne $null)"
$idBox = $cw.FindFirst($T::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,[System.Windows.Automation.ControlType]::Edit)))
$idBox.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("diagid")
InvokeJob $p.Id "NameProperty" "Load existing game" | Out-Null
$fw = WinByHwnd $p.Id "Load a saved game" 20
Log "File dialog open: $($fw -ne $null)"
Log "==== file dialog Edit/Combo/Button controls ===="
foreach($ct in @([System.Windows.Automation.ControlType]::Edit,[System.Windows.Automation.ControlType]::ComboBox,[System.Windows.Automation.ControlType]::Button,[System.Windows.Automation.ControlType]::SplitButton)){
  $els = $fw.FindAll($T::Descendants,(New-Object System.Windows.Automation.PropertyCondition($A::ControlTypeProperty,$ct)))
  foreach($e in $els){ Log ("  " + $ct.ProgrammaticName + " id='" + $e.Current.AutomationId + "' name='" + $e.Current.Name + "' enabled=" + $e.Current.IsEnabled) }
}
Get-Job | Remove-Job -Force -ErrorAction SilentlyContinue
Stop-Process -Id $p.Id -ErrorAction SilentlyContinue
