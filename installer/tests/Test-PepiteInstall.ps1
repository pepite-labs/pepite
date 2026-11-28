# Pepite installer test scenarios (plan 12 step 9), run INSIDE the Windows 11 test VM, never on a development PC.
# Dot-source it in an interactive PowerShell of the logged-on user, then call one scenario at a time:
#   . .\Test-PepiteInstall.ps1 -Setup C:\tests\inst -Out C:\tests\out ; T4
# -Setup: the folder with Pepite-<ver>-installation.exe, Pepite-<ver>-test-installation.exe, exemple-etablissement.json,
#         a .pepite file (classe.pepite) and mauvais.json (an invalid machine file).
# Each scenario prints "<id> PASS|FAIL <detail>" lines and saves screenshots (<id>-NN-<what>.png) in -Out.
# Scenarios that need an administrator (T3 as SYSTEM, T9 all-users, T10 all-users side) are driven from an elevated
# session by the host script; the functions here take the role of the interactive user (UI, non-elevated runs).
# ASCII only (Windows PowerShell 5.1 reads BOM-less files as ANSI): accented UI text is matched with '.' wildcards.
param([Parameter(Mandatory)][string]$Setup, [Parameter(Mandatory)][string]$Out)
$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force $Out | Out-Null
$global:SETUP = $Setup; $global:OUT12 = $Out
Add-Type -AssemblyName System.Windows.Forms, System.Drawing, UIAutomationClient, UIAutomationTypes
if (-not ([System.Management.Automation.PSTypeName]'PI12a').Type) {
Add-Type @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class PI12a {
  [StructLayout(LayoutKind.Sequential)] struct KI { public ushort vk, scan; public uint flags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit, Size = 40)] struct IN { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KI ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, IN[] i, int cb);
  public static void Char(char c) { var a = new IN[2]; a[0].type = 1; a[0].ki.scan = c; a[0].ki.flags = 4; a[1] = a[0]; a[1].ki.flags = 6; SendInput(2, a, Marshal.SizeOf(typeof(IN))); }
  public static void Key(ushort vk) { var a = new IN[2]; a[0].type = 1; a[0].ki.vk = vk; a[1] = a[0]; a[1].ki.flags = 2; SendInput(2, a, Marshal.SizeOf(typeof(IN))); }
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  public static void Click(int x, int y) { SetCursorPos(x, y); System.Threading.Thread.Sleep(80); mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(4, 0, 0, 0, UIntPtr.Zero); }
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, uint pid);
  [DllImport("advapi32.dll")] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
  [DllImport("advapi32.dll")] static extern bool GetTokenInformation(IntPtr t, int c, out int v, int l, out int r);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  // "yes" / "no" / "denied" (a non-elevated caller cannot open an elevated process's token: denied means elevated)
  public static string Elevated(uint pid) {
    var p = OpenProcess(0x1000, false, pid); if (p == IntPtr.Zero) return "noprocess";
    IntPtr t; if (!OpenProcessToken(p, 8, out t)) { CloseHandle(p); return "denied"; }
    int v, r; bool ok = GetTokenInformation(t, 20, out v, 4, out r); CloseHandle(t); CloseHandle(p);
    return !ok ? "error" : v != 0 ? "yes" : "no";
  }
}
"@
}
$global:AE = [Windows.Automation.AutomationElement]; $global:TS = [Windows.Automation.TreeScope]
function U([string]$s) { [regex]::Unescape($s) }
function Verd([string]$id, [bool]$ok, [string]$detail) { "$id $(if ($ok) { 'PASS' } else { 'FAIL' }) $detail" }

# ---------------------------------------------------------------- paths
function InstPublic { (Get-ChildItem $SETUP -Filter 'Pepite-*-installation.exe' | Where-Object { $_.Name -notmatch '-test-' } | Select-Object -First 1).FullName }
function InstTest { (Get-ChildItem $SETUP -Filter 'Pepite-*-test-installation.exe' | Select-Object -First 1).FullName }
$global:PU = Join-Path $env:LOCALAPPDATA 'Programs\Pepite-Prediction'
$global:PM = 'C:\Program Files\Pepite-Prediction'
$global:UKEY = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{999AA879-43C2-4C22-9785-691AA95A3455}_is1'
$global:RUNKEY = 'Software\Microsoft\Windows\CurrentVersion\Run'
$global:KLOG12 = Join-Path $env:TEMP 'predict-keyboard.log'
$global:DATA = Join-Path $env:APPDATA 'Pepite-Prediction'

# ---------------------------------------------------------------- screenshots
function Shot([string]$name) {
  $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
  $bmp = New-Object Drawing.Bitmap $b.Width, $b.Height
  $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
  $bmp.Save("$OUT12\$name.png", [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose(); "shot $name"
}

# ---------------------------------------------------------------- windows
# visible top-level windows: "pid|process|title"
function TopWins {
  $list = New-Object System.Collections.ArrayList
  $cb = [PI12a+EnumProc]{ param($h, $l)
    if ([PI12a]::IsWindowVisible($h)) {
      $sb = New-Object Text.StringBuilder 256; [PI12a]::GetWindowText($h, $sb, 256) | Out-Null
      $p = 0; [PI12a]::GetWindowThreadProcessId($h, [ref]$p) | Out-Null
      if ($sb.Length -gt 0) { [void]$list.Add("$p|$((Get-Process -Id $p -EA 0).ProcessName)|$sb") }
    }; return $true }
  [PI12a]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
  $list
}
# the setup / uninstall windows (Inno Setup: Pepite-<ver>-installation.tmp, _iuXXXX.tmp)
function SetupWins { $AE::RootElement.FindAll($TS::Children, [Windows.Automation.Condition]::TrueCondition) | Where-Object {
    $p = Get-Process -Id $_.Current.ProcessId -EA 0; $p -and $p.ProcessName -match '(?i)installation|^_iu|unins' } }
function SetupText { (SetupWins | ForEach-Object { $w = $_; @($w.Current.Name) + @($w.FindAll($TS::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) }) -join ' | ' }
# activate the first button / command link / radio button whose name matches (setup windows, or our process with
# -Pepite): Invoke, else Select, else a mouse click on its centre (the task-dialog command links of the install-mode
# choice are Panes)
function Btn([string]$rx, [int]$sec = 30, [switch]$Pepite) {
  $types = @([Windows.Automation.ControlType]::Button, [Windows.Automation.ControlType]::Pane, [Windows.Automation.ControlType]::RadioButton)
  for ($i = 0; $i -lt $sec * 4; $i++) {
    $wins = if ($Pepite) { PepWins } else { SetupWins }
    foreach ($w in $wins) {
      foreach ($b in $w.FindAll($TS::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
        if ($types -notcontains $b.Current.ControlType) { continue }
        if ($b.Current.Name -match $rx -and $b.Current.IsEnabled) {
          $n = $b.Current.Name; $how = 'invoke'
          try { $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
          catch {
            try { $b.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); $how = 'select' }
            catch { $r = $b.Current.BoundingRectangle; [PI12a]::Click([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)); $how = 'click' }
          }
          Start-Sleep -Milliseconds 700; return "button '$n' ($how)"
        }
      }
    }
    Start-Sleep -Milliseconds 250
  }
  "NO BUTTON $rx"
}
function SetupGone([int]$sec = 300) { for ($i = 0; $i -lt $sec * 2; $i++) { if (-not (Get-Process | Where-Object { $_.ProcessName -match '(?i)installation|^_iu|unins' })) { return $true }; Start-Sleep -Milliseconds 500 }; $false }

# ---------------------------------------------------------------- Pepite
function PepProcs { @(Get-Process Pepite -EA 0) }
function PepWins { $ids = (PepProcs).Id; $AE::RootElement.FindAll($TS::Children, [Windows.Automation.Condition]::TrueCondition) | Where-Object { $ids -contains $_.Current.ProcessId } }
function PepWin([string]$aid, [int]$sec = 15) {
  for ($i = 0; $i -lt $sec * 4; $i++) {
    foreach ($w in PepWins) {
      if ($w.Current.AutomationId -eq $aid) { return $w }
      $c = $w.FindFirst($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $aid)))
      if ($c -and $c.Current.ControlType -eq [Windows.Automation.ControlType]::Window) { return $c }
    }
    Start-Sleep -Milliseconds 250
  }
  $null
}
function El($w, [string]$aid) { $w.FindFirst($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $aid))) }
function Inv($w, [string]$aid, [int]$ms = 500) { (El $w $aid).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds $ms }
function AllText($w) { (@($w.Current.Name) + @($w.FindAll($TS::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name })) -join ' | ' }
function PepStart([string]$exe, [string]$extra = '--log --no-welcome --audio null') {
  $m = LogMark
  Start-Process $exe -ArgumentList ($extra -split ' ' | Where-Object { $_ }) | Out-Null
  WaitLog 'start: (Manual|Autostart), feedback' $m 120 | Out-Null
  Start-Sleep 2
  $m
}
function PepQuit([string]$exe) { if (PepProcs) { Start-Process $exe -ArgumentList '--quit' -Wait; for ($i = 0; $i -lt 40 -and (PepProcs); $i++) { Start-Sleep -Milliseconds 250 } } }
function PepElev { @(PepProcs | ForEach-Object { [PI12a]::Elevated([uint32]$_.Id) }) -join ',' }
function PepCmd { @(Get-CimInstance Win32_Process -Filter "Name='Pepite.exe'" | ForEach-Object { $_.CommandLine }) -join ' || ' }
function LogMark { if (Test-Path $KLOG12) { @(Get-Content $KLOG12 -Encoding UTF8).Count } else { 0 } }
function LogSince([int]$m) { @(Get-Content $KLOG12 -Encoding UTF8 -EA 0 | Select-Object -Skip $m) }
function Lines([int]$m, [string]$rx) { @(LogSince $m | Where-Object { $_ -match $rx }) }
function WaitLog([string]$rx, [int]$m, [int]$sec = 40) { for ($i = 0; $i -lt $sec * 4; $i++) { if (Lines $m $rx) { return $true }; Start-Sleep -Milliseconds 250 }; $false }

# ---------------------------------------------------------------- Notepad (classic, C:\Windows\notepad.exe)
function NpWin { (Get-Process notepad -EA 0 | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1) }
function NpType([string]$text) {
  if (-not (NpWin)) { Start-Process C:\Windows\notepad.exe; Start-Sleep 2 }
  $np = NpWin; [PI12a]::SetForegroundWindow($np.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 500
  foreach ($c in $text.ToCharArray()) { [PI12a]::Char($c); Start-Sleep -Milliseconds 120 }
  Start-Sleep -Milliseconds 1500
}
function BarShown { [bool](PepWins | Where-Object { $_.Current.Name -match '^P.pite$|Pr.diction' -and -not $_.Current.IsOffscreen }) }

# ---------------------------------------------------------------- registry and files
function RegVal([string]$root, [string]$key, [string]$name) { try { (Get-ItemProperty -Path "${root}:\$key" -Name $name -EA Stop).$name } catch { $null } }
function RunValue([string]$root) { RegVal $root $RUNKEY 'Pepite-Prediction' }
function Installed([string]$root) { Test-Path "${root}:\$UKEY" }
# installed files against files.sha256 (the full component set): missing / different files, and extra files (a file
# of an older version that the upgrade left behind, e.g. the .NET 6 runtime files after 0.10.2; Inno's unins000.* and
# the $skip patterns excepted)
function TreeCheck([string]$dir, [string]$list, [string[]]$skip = @()) {
  $bad = @(); $expected = @{}
  foreach ($l in Get-Content $list) {
    $h, $rel = $l -split '  ', 2
    $expected[$rel] = 1
    if ($skip | Where-Object { $rel -like $_ }) { continue }
    $p = Join-Path $dir $rel
    if (-not (Test-Path $p)) { $bad += "missing $rel" } elseif ((Get-FileHash $p -Algorithm SHA256).Hash.ToLower() -ne $h) { $bad += "differs $rel" }
  }
  $root = (Resolve-Path $dir).Path.TrimEnd('\') + '\'
  foreach ($f in Get-ChildItem $dir -Recurse -File) {
    $rel = $f.FullName.Substring($root.Length)
    if ($expected.ContainsKey($rel) -or $rel -match '^unins\d{3}\.(exe|dat|msg)$' -or ($skip | Where-Object { $rel -like $_ })) { continue }
    $bad += "extra $rel"
  }
  $bad
}
# run a setup / uninstaller and watch for any visible window it opens (/VERYSILENT must show none)
function RunWatched([string]$exe, [string[]]$args12) {
  $p = Start-Process $exe -ArgumentList $args12 -PassThru
  $seen = @{}
  while (-not $p.HasExited) {
    foreach ($w in TopWins) { $f = $w -split '\|', 3; if ($f[1] -match '(?i)installation|^_iu|unins') { $seen[$f[2]] = 1 } }
    Start-Sleep -Milliseconds 200
  }
  [pscustomobject]@{ Exit = $p.ExitCode; Windows = ($seen.Keys -join ' / ') }
}

# ================================================================ scenarios (interactive user)

# T1 interactive per-user install with the defaults; " Lancer Pepite " -> welcome window, not elevated; bar in Notepad
function T1 {
  $before = @(Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Filter '*.lnk' -EA 0).Count
  Start-Process (InstPublic)
  $steps = @()
  Start-Sleep 4; Shot 'T1-01-privileges' | Out-Null
  $steps += Btn 'seulement pour moi' 180
  Start-Sleep 2; Shot 'T1-02-info' | Out-Null
  $steps += Btn '^Suivant'                       # information (avant-installation.txt)
  $steps += Btn '^Suivant'                       # components
  Start-Sleep 1; Shot 'T1-03-tasks' | Out-Null
  $tasksText = SetupText
  $steps += Btn '^Suivant'                       # tasks
  Start-Sleep 1; Shot 'T1-04-ready' | Out-Null
  $steps += Btn '^Installer'
  $steps += Btn '^Terminer' 600
  $gone = SetupGone 60
  Start-Sleep 25
  $welcome = PepWin 'WelcomeWindow' 60
  Shot 'T1-05-welcome-after-launch' | Out-Null
  $elev = PepElev; $cmd = PepCmd
  if ($welcome) { Inv $welcome 'Ok' 800 }
  NpType 'la mais'; $bar = BarShown; Shot 'T1-06-bar-in-notepad' | Out-Null
  [PI12a]::Key(0x1B)
  $lnk = Test-Path "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\P*pite.lnk"
  @(
    Verd 'T1a per-user folder' (Test-Path "$PU\Pepite.exe") $PU
    Verd 'T1b HKCU Run value exact' ((RunValue 'HKCU') -eq "`"$PU\Pepite.exe`" --autostart") "'$(RunValue 'HKCU')'"
    Verd 'T1c Start-menu shortcut' $lnk "lnk before $before"
    Verd 'T1d uninstall entry in HKCU only' ((Installed 'HKCU') -and -not (Installed 'HKLM')) ''
    Verd 'T1e Lancer Pepite: welcome window' ([bool]$welcome) "cmd '$cmd'"
    Verd 'T1f Pepite not elevated' ($elev -eq 'no') $elev
    Verd 'T1g bar in Notepad' $bar ''
    "T1 steps: $($steps -join ' ; ') | setup gone $gone | tasks page: $tasksText"
  )
}

# start the installed copy with --log: the start line (elevated, install kind) and the database path
function StartLine([string]$exe) {
  PepQuit $exe; Start-Sleep 1
  $m = PepStart $exe
  $l = @(Lines $m '(start: elevated|engine loaded|machine settings)')
  $l
}

# T4 silent per-user install: exit 0, no window
function T4 {
  $r = RunWatched (InstPublic) @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$OUT12\T4-setup.log`"")
  Start-Sleep 2
  @(
    Verd 'T4a silent per-user: exit 0' ($r.Exit -eq 0) "exit $($r.Exit)"
    Verd 'T4b no window' (-not $r.Windows) "'$($r.Windows)'"
    Verd 'T4c installed per-user, Run value, no Pepite started' ((Test-Path "$PU\Pepite.exe") -and (RunValue 'HKCU') -and -not (PepProcs)) "procs $((PepProcs).Count)"
  )
}

# T11 .pepite double-click: management off -> message; on -> PIN, then the import dialog with the file; running / not running
function DoubleClick([string]$file) { Start-Process $file }   # ShellExecute = the association, as Explorer's double-click
function T11Off([string]$exe) {
  $file = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'classe.pepite'
  Copy-Item (Join-Path $SETUP 'classe.pepite') $file -Force
  $m = StartLineMark $exe
  DoubleClick $file
  $mb = $null; for ($i = 0; $i -lt 60 -and -not $mb; $i++) { $mb = PepWins | Where-Object { $_.Current.Name -match 'Importer des profils' } | Select-Object -First 1; Start-Sleep -Milliseconds 250 }
  $txt = if ($mb) { AllText $mb } else { '' }
  Shot 'T11-01-management-off-message' | Out-Null
  if ($mb) { Btn '^OK$' 5 -Pepite | Out-Null }
  $db = @(Lines $m 'engine loaded') | Select-Object -First 1
  @(
    Verd 'T11a management off: the activate message' ($txt -match 'activez d.abord la gestion enseignant') "'$txt'"
    Verd 'T11b database path never replaced' ($db -match [regex]::Escape("$(Split-Path $exe)\prediction.sqlite")) "'$db'"
    "T11 log: $((Lines $m '^\S+ (import|second start)') -join ' | ')"
  )
}
function StartLineMark([string]$exe) { PepQuit $exe; Start-Sleep 1; PepStart $exe }
# management on through Reglages (second start -> " deja demarre " -> Reglages -> Activer la gestion enseignant)
function MgmtOn12([string]$exe, [string]$pin = '480917') {
  Start-Process $exe | Out-Null
  $w = PepWin 'AlreadyRunningWindow' 20; if (-not $w) { $w = PepWin 'WelcomeWindow' 3 }; if (-not $w) { return 'no deja-demarre window' }
  Inv $w 'OpenSettings' 1500
  $sw = PepWin 'SettingsWindow' 20; if (-not $sw) { return 'no settings window' }
  Inv $sw 'PupilsButton' 1500
  $pd = PepWin 'PinDialog' 20; if (-not $pd) { return 'no pin dialog' }
  foreach ($c in $pin.ToCharArray()) { Inv $pd ("PinKey" + $c) 150 }
  (El $pd 'PinConfirmBox').SetFocus(); Start-Sleep -Milliseconds 300
  foreach ($c in $pin.ToCharArray()) { Inv $pd ("PinKey" + $c) 150 }
  Inv $pd 'PinOk' 1500
  $rw = PepWin 'RecoveryWindow' 30; if ($rw) { Inv $rw 'RecoveryDone' 1000 }
  $sw = PepWin 'SettingsWindow' 5; if ($sw) { Inv $sw 'Cancel' 800 }
  if ((Get-Content (Join-Path $DATA 'settings.json') -Raw -EA 0) -match '"managementEnabled":\s*true') { 'management on' } else { 'management NOT on' }
}
function PinAnswer12([string]$pin = '480917') {
  $pd = PepWin 'PinDialog' 20; if (-not $pd) { return 'no pin dialog' }
  foreach ($c in $pin.ToCharArray()) { Inv $pd ("PinKey" + $c) 150 }
  Inv $pd 'PinOk' 2500; 'pin answered'
}
function ImportSeen([string]$tag) {
  $d = PepWin 'ImportDialog' 60
  $t = if ($d) { AllText $d } else { '' }
  Shot "T11-$tag-import-dialog" | Out-Null
  if ($d) { Inv $d 'ImportCancel' 800 }
  $tw = PepWin 'TeacherWindow' 3; if ($tw) { Inv $tw 'TeacherClose' 800 }
  [pscustomobject]@{ Seen = [bool]$d; Text = $t }
}
function T11On([string]$exe, [switch]$RunningOnly) {
  $file = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'classe.pepite'
  $mg = MgmtOn12 $exe
  # running: a second copy writes ouvrir.txt and sets the Open event; the PIN gate (or its grace), then the dialog
  $m = LogMark
  DoubleClick $file
  Start-Sleep 3
  $pin1 = if (PepWin 'PinDialog' 8) { Shot 'T11-02-pin-asked' | Out-Null; PinAnswer12 } else { 'no PIN (grace after creating it)' }
  $a = ImportSeen '03-running'
  $logA = @(Lines $m '^\S+ (import|second start)') -join ' | '
  $left = Test-Path (Join-Path $env:LOCALAPPDATA 'Pepite-Prediction\ouvrir.txt')
  if ($RunningOnly) {
    return @(
      "T11 management: $mg"
      Verd 'T11c running: PIN then the import dialog with the file' ($a.Seen -and $a.Text -match 'classe\.pepite') "$pin1 | '$($a.Text.Substring(0, [math]::Min(160, $a.Text.Length)))' | log $logA"
      Verd 'T11d ouvrir.txt taken (deleted)' (-not $left) ''
    )
  }
  # not running: Explorer starts Pepite --import <file> (no --log: checked through the windows)
  PepQuit $exe; Start-Sleep 2
  DoubleClick $file
  $pd = PepWin 'PinDialog' 90
  Shot 'T11-04-not-running-pin' | Out-Null
  $pin2 = if ($pd) { PinAnswer12 } else { 'no PIN dialog' }
  $b = ImportSeen '05-not-running'
  $cmd = PepCmd
  @(
    "T11 management: $mg"
    Verd 'T11c running: PIN then the import dialog with the file' ($a.Seen -and $a.Text -match 'classe\.pepite') "$pin1 | '$($a.Text.Substring(0, [math]::Min(160, $a.Text.Length)))' | log $logA"
    Verd 'T11d ouvrir.txt taken (deleted)' (-not $left) ''
    Verd 'T11e not running: Pepite starts, PIN, the import dialog with the file' ($b.Seen -and $b.Text -match 'classe\.pepite') "$pin2 | cmd '$cmd'"
  )
}

# T7 upgrade while Pepite runs (rebuild = the -test build), untick images and start-with-Windows; then the choices kept
function T7 {
  $exe = "$PU\Pepite.exe"
  $m = StartLineMark $exe
  $r = RunWatched (InstTest) @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/COMPONENTS="voix\siwis"', '/TASKS="pepite"', "/LOG=`"$OUT12\T7-setup.log`"")
  $quit = @(Lines $m 'quit requested').Count
  $pictos = Test-Path "$PU\pictos.sqlite"; $run = RunValue 'HKCU'; $procs = (PepProcs).Count
  $r2 = RunWatched (InstPublic) @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$OUT12\T7-setup2.log`"")
  @(
    Verd 'T7a upgrade with Pepite running: exit 0, Pepite closed by the Quit event' ($r.Exit -eq 0 -and $quit -ge 1 -and $procs -eq 0) "exit $($r.Exit), quit lines $quit, procs after $procs"
    Verd 'T7b images unticked: pictos.sqlite gone' (-not $pictos) ''
    Verd 'T7c start with Windows unticked: Run value gone' (-not $run) "'$run'"
    Verd 'T7d next upgrade without switches keeps the choices' ($r2.Exit -eq 0 -and -not (Test-Path "$PU\pictos.sqlite") -and -not (RunValue 'HKCU') -and (Test-Path "$PU\voices\siwis")) "exit $($r2.Exit)"
    "T7 setup log: $((Select-String -Path "$OUT12\T7-setup.log" -Pattern 'Pepite:' | ForEach-Object Line) -join ' | ')"
  )
}

# T15 upgrade from the previous release (0.10.2: from 0.10.1 = .NET 6 to .NET 10), silent, per-user, all components:
# the installed tree is exactly files.sha256 of the new build (in -Setup), no file of the old version left behind.
# $OldSetup: the previous public installer (kept OUT of -Setup, so InstPublic still picks the new one).
function T15([Parameter(Mandatory)][string]$OldSetup) {
  $r1 = RunWatched $OldSetup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$OUT12\T15-old-setup.log`"")
  $r2 = RunWatched (InstPublic) @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$OUT12\T15-setup.log`"")
  PepQuit "$PU\Pepite.exe"
  $bad = @(TreeCheck $PU (Join-Path $SETUP 'files.sha256'))
  $extra = @($bad | Where-Object { $_ -like 'extra *' })
  @(
    Verd 'T15a old then new install: exit 0, 0' ($r1.Exit -eq 0 -and $r2.Exit -eq 0) "exit $($r1.Exit), $($r2.Exit)"
    Verd 'T15b no file of the old version left (extra)' ($extra.Count -eq 0) "$($extra.Count): $(($extra | Select-Object -First 8) -join ' ; ')"
    Verd 'T15c installed files = files.sha256' ($bad.Count -eq 0) "$($bad.Count): $(($bad | Select-Object -First 8) -join ' ; ')"
  )
}

# T8 interactive uninstall (per-user): Non keeps the data, Oui deletes it
function Uninstall12([string]$answer, [string]$tag) {
  $u = Get-ChildItem $PU -Filter 'unins*.exe' | Select-Object -First 1
  Start-Process $u.FullName
  $s1 = Btn '^Oui$' 30                        # " Voulez-vous vraiment desinstaller... "
  Start-Sleep 4
  $q = SetupText
  Shot "T8-$tag-data-question" | Out-Null
  $s2 = Btn "^$answer$" 60
  Start-Sleep 3
  $s3 = Btn '^OK$' 60
  SetupGone 60 | Out-Null
  [pscustomobject]@{ Steps = "$s1 ; $s2 ; $s3"; Question = $q }
}
function T8 {
  New-Item -ItemType Directory -Force $DATA | Out-Null
  if (-not (Test-Path "$DATA\settings.json")) { '{}' | Set-Content "$DATA\settings.json" }
  $a = Uninstall12 'Non' '01-non'
  $keptA = Test-Path "$DATA\settings.json"; $appA = Test-Path $PU
  $r = RunWatched (InstPublic) @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER')
  $b = Uninstall12 'Oui' '02-oui'
  $goneB = -not (Test-Path $DATA) -and -not (Test-Path (Join-Path $env:LOCALAPPDATA 'Pepite-Prediction'))
  @(
    Verd 'T8a the question, default Non: data kept' ($keptA -and $a.Question -match 'Supprimer aussi les r.glages') "'$($a.Question.Substring(0, [math]::Min(200, $a.Question.Length)))' | $($a.Steps)"
    Verd 'T8b no leftover in {app}' (-not $appA) "$PU exists: $appA"
    Verd 'T8c reinstall, Oui: this account''s data deleted' ($r.Exit -eq 0 -and $goneB) "$($b.Steps)"
  )
}

# T6 upgrade from a portable copy that runs (0.8.3 in C:\kb083): closed via the Quit event, profiles and words intact
function TreeHash([string]$dir) { if (-not (Test-Path $dir)) { return '' }; (Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -ne 'index.json' } | Sort-Object FullName | ForEach-Object { (Get-FileHash $_.FullName).Hash }) -join '' | ForEach-Object { $_.GetHashCode() } }
function T6 {
  $prof = Join-Path $DATA 'profiles'
  $nBefore = @(Get-ChildItem $prof -Directory -EA 0).Count; $hBefore = TreeHash $prof
  $m = LogMark
  Start-Process C:\kb083\Pepite.exe -ArgumentList '--log', '--no-welcome', '--audio', 'null'
  WaitLog 'start: (Manual|Autostart), feedback' $m 120 | Out-Null; Start-Sleep 3
  $r = RunWatched (InstPublic) @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$OUT12\T6-setup.log`"")
  $quit = @(Lines $m 'quit requested').Count; $procs = (PepProcs).Count
  $m2 = PepStart "$PU\Pepite.exe"
  $nAfter = @(Get-ChildItem $prof -Directory -EA 0).Count; $hAfter = TreeHash $prof
  @(
    Verd 'T6a 0.8.3 portable running: closed by the Quit event, install exit 0' ($r.Exit -eq 0 -and $quit -ge 1 -and $procs -eq 0) "exit $($r.Exit), quit $quit"
    Verd 'T6b profiles and words intact' ($nBefore -ge 2 -and $nAfter -eq $nBefore -and $hAfter -eq $hBefore) "profiles $nBefore -> $nAfter"
    "T6 0.10.0 start: $((Lines $m2 '(start: elevated|profiles: management)') -join ' | ')"
  )
}
# T6b from 0.6.1: the legacy %APPDATA%\PredictKeyboard settings are migrated at the first start
function T6Legacy {
  PepQuit "$PU\Pepite.exe"
  Remove-Item $DATA -Recurse -Force -EA 0
  $old = Join-Path $env:APPDATA 'PredictKeyboard'
  New-Item -ItemType Directory -Force $old | Out-Null
  [IO.File]::WriteAllText((Join-Path $old 'settings.json'), '{"schema":1,"words":{"count":5,"size":"L","font":"lexend"},"start":{"welcome":false}}')
  $m = PepStart "$PU\Pepite.exe"
  $mig = @(Lines $m '(migrat|legacy|PredictKeyboard)')
  @( Verd 'T6c from 0.6.1: legacy settings migrated' ($mig.Count -ge 1 -and (Test-Path (Join-Path $DATA 'profiles'))) ($mig -join ' | ') )
}

# T5 after a logon (start with Windows): Pepite runs with --autostart, not elevated, no welcome window
function T5([string]$tag) {
  $t0 = Get-Date
  $p = $null; for ($i = 0; $i -lt 120 -and -not $p; $i++) { $p = PepProcs | Select-Object -First 1; if (-not $p) { Start-Sleep -Milliseconds 500 } }
  Start-Sleep 8
  $welcome = PepWin 'WelcomeWindow' 2
  Shot "T5-$tag-after-logon" | Out-Null
  $up = if ($p) { [int]((Get-Date) - $p.StartTime).TotalSeconds } else { -1 }
  $boot = [int]((Get-Date) - (Get-CimInstance Win32_LogonSession -Filter 'LogonType=2' | Sort-Object StartTime -Descending | Select-Object -First 1).StartTime).TotalSeconds
  @(
    Verd "T5-$tag Pepite running after logon, with --autostart" ([bool]$p -and (PepCmd) -match '--autostart') "cmd '$(PepCmd)'; process up $up s, logon session $boot s"
    Verd "T5-$tag not elevated, no welcome window" ((PepElev) -eq 'no' -and -not $welcome) "elevated $(PepElev)"
  )
}

# T2 interactive all-users install from a standard session. Two halves: T2Start in the user's (non-elevated) session
# chooses "pour tous les utilisateurs"; Windows elevates the wizard; T2Wizard drives the elevated wizard (it must run
# elevated itself: a non-elevated process cannot click an elevated window) with the example machine file.
function T2Start {
  Start-Process (InstPublic)
  Start-Sleep 4; Shot 'T2-01-privileges' | Out-Null
  Btn 'pour tous les utilisateurs' 180
}
function T2Wizard([string]$config = (Join-Path $SETUP 'exemple-etablissement.json')) {
  $s = @()
  $s += Btn '^Suivant' 90                        # information
  $s += Btn '^Suivant'                           # components
  $s += Btn '^Suivant'                           # tasks
  Start-Sleep 2
  $edit = $null
  foreach ($w in SetupWins) { $edit = $w.FindFirst($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::ControlTypeProperty, [Windows.Automation.ControlType]::Edit))); if ($edit) { break } }
  if ($edit) { $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($config); Start-Sleep 1 }
  Shot 'T2-02-config-page' | Out-Null
  $page = SetupText
  $s += Btn '^Suivant'                           # configuration page
  Start-Sleep 1; Shot 'T2-03-ready' | Out-Null
  $s += Btn '^Installer'
  $s += Btn '^Terminer' 600
  "T2 wizard: $($s -join ' ; ') | config edit found: $([bool]$edit) | page: $($page.Substring(0, [math]::Min(300, $page.Length)))"
}

# ================================================================ plan installer-upgrade (U rows)
# The old setup ($OldSetup, the public 0.10.2) is kept OUT of -Setup; InstTest is the new -test build.
$global:BACKUPS = Join-Path $env:LOCALAPPDATA 'Pepite-Prediction\sauvegardes'
function NewestSetupLog { Get-ChildItem $env:TEMP -Filter 'Setup Log *.txt' -EA 0 | Sort-Object LastWriteTime -Descending | Select-Object -First 1 }
function SetupLogLines([string]$rx, [string]$path = '') { if (-not $path) { $l = NewestSetupLog; if (-not $l) { return @() }; $path = $l.FullName }; @(Select-String -Path $path -Pattern $rx | ForEach-Object Line) }
# file -> SHA-256 of the data folder (settings.json and .tmp left out: settings.json gets state.lastVersion)
function DataHashes {
  $h = @{}
  if (Test-Path $DATA) {
    $root = (Resolve-Path $DATA).Path.TrimEnd('\') + '\'
    foreach ($f in Get-ChildItem $DATA -Recurse -File) {
      $rel = $f.FullName.Substring($root.Length)
      if ($rel -eq 'settings.json' -or $rel -like '*.tmp') { continue }
      $h[$rel] = (Get-FileHash $f.FullName).Hash
    }
  }
  $h
}
function SameHashes($a, $b) { if ($a.Count -ne $b.Count) { return $false }; foreach ($k in $a.Keys) { if ($b[$k] -ne $a[$k]) { return $false } }; $true }
function DiffHashes($a, $b) { @($a.Keys + $b.Keys | Sort-Object -Unique | Where-Object { $a[$_] -ne $b[$_] }) -join ', ' }
function VersionZips { @(Get-ChildItem $BACKUPS -Filter 'profils-*-mise-a-jour.zip' -EA 0) }
function ZipEntries([string]$zip) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $z = [IO.Compression.ZipFile]::OpenRead($zip)
  try { @($z.Entries | ForEach-Object FullName) } finally { $z.Dispose() }
}
function ActiveProfile { try { (Get-Content (Join-Path $DATA 'profiles\index.json') -Raw | ConvertFrom-Json).active } catch { $null } }
function LastVersion { try { (Get-Content (Join-Path $DATA 'settings.json') -Raw | ConvertFrom-Json).state.lastVersion } catch { $null } }
# the value of the first Edit / Document of the setup windows (the ready page's memo)
function SetupMemo {
  foreach ($w in SetupWins) {
    foreach ($e in $w.FindAll($TS::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
      $t = $e.Current.ControlType
      if ($t -eq [Windows.Automation.ControlType]::Edit -or $t -eq [Windows.Automation.ControlType]::Document) {
        try { return $e.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value } catch { }
        try { return $e.GetCurrentPattern([Windows.Automation.TextPattern]::Pattern).DocumentRange.GetText(4000) } catch { }
      }
    }
  }
  ''
}
# tick a check box of the setup windows by name (Toggle pattern)
function Tick([string]$rx) {
  foreach ($w in SetupWins) {
    foreach ($e in $w.FindAll($TS::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
      if ($e.Current.Name -match $rx) {
        # Inno's TNewCheckBox: a CheckBox with Toggle, or a plain control: then a click on its box (left edge)
        try { $e.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); $how = 'toggle' }
        catch { $r = $e.Current.BoundingRectangle; [PI12a]::Click([int]($r.X + 8), [int]($r.Y + $r.Height / 2)); $how = 'click' }
        Start-Sleep -Milliseconds 500
        return "ticked '$($e.Current.Name)' ($how, $($e.Current.ControlType.ProgrammaticName))"
      }
    }
  }
  "NO CHECKBOX $rx"
}
function WaitSetupText([string]$rx, [int]$sec = 600) { for ($i = 0; $i -lt $sec * 2; $i++) { $t = SetupText; if ($t -match $rx) { return $t }; Start-Sleep -Milliseconds 500 }; '' }
function WaitPep([int]$sec = 30) { for ($i = 0; $i -lt $sec * 2; $i++) { if (PepProcs) { return $true }; Start-Sleep -Milliseconds 500 }; $false }
function Cut([string]$s, [int]$n = 300) { if (-not $s) { return '' }; $s.Substring(0, [math]::Min($n, $s.Length)) }

# before U1 / U8: the old version per-user, silent; management on (PIN -> teacher.json, " Classe "), one word in
# " Mes mots " of the active profile; Pepite left running
function U1Prep([Parameter(Mandatory)][string]$OldSetup, [string]$dir = '') {
  $a = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$OUT12\U1-old-setup.log`"")
  if ($dir) { $a += "/DIR=`"$dir`"" }
  $r = RunWatched $OldSetup $a
  $exe = if ($dir) { "$dir\Pepite.exe" } else { "$PU\Pepite.exe" }
  PepStart $exe | Out-Null
  $mg = MgmtOn12 $exe
  PepQuit $exe; Start-Sleep 1
  $act = ActiveProfile
  if ($act) {
    $mots = '{"schema":1,"listes":[{"id":"mes-mots","nom":"Mes mots","active":true,"type":"perso","mots":[{"w":"Zorglub","src":"manuel","ajoute":"2026-11-27"}]}]}'
    [IO.File]::WriteAllText((Join-Path $DATA "profiles\$act\mots.json"), $mots)
  }
  PepStart $exe | Out-Null
  "U1Prep: old setup exit $($r.Exit) | $mg | active $act | profiles $(@(Get-ChildItem (Join-Path $DATA 'profiles') -Directory -EA 0).Count) | running $((PepProcs).Count)"
}

# U1 interactive upgrade, Pepite running: the upgrade page, " Mettre a jour ", " Terminer " (2 clicks)
function U1([string]$ver = '0.11.0', [string]$dir = '', [string]$tag = 'U1') {
  $app = if ($dir) { $dir } else { $PU }
  $hBefore = DataHashes; $zBefore = (VersionZips).Count; $act = ActiveProfile
  $runBefore = RunValue 'HKCU'; $procsBefore = (PepProcs).Count
  $m = LogMark
  Start-Process (InstTest)
  WaitSetupText 'Mise . jour de P.pite' 120 | Out-Null
  Start-Sleep 1; Shot "$tag-01-upgrade-page" | Out-Null
  $first = SetupText
  $clicks = @()
  $clicks += Btn '^Mettre . jour$' 30
  $fin = WaitSetupText 'Terminer' 600
  Start-Sleep 1; Shot "$tag-02-finished" | Out-Null
  $clicks += Btn '^Terminer' 30
  $gone = SetupGone 60
  $up = WaitPep 60; Start-Sleep 8
  # the version backup runs during the start-up (before the profiles load): wait for it, up to 60 s
  for ($i = 0; $i -lt 120 -and (VersionZips).Count -le $zBefore; $i++) { Start-Sleep -Milliseconds 500 }
  Shot "$tag-03-after-terminer" | Out-Null
  $elev = PepElev; $cmd = PepCmd
  $hAfter = DataHashes; $z = VersionZips
  $entries = if ($z.Count) { ZipEntries ($z | Sort-Object Name | Select-Object -Last 1).FullName } else { @() }
  $slog = SetupLogLines '(upgrade:|repair:|install:|downgrade|Pepite:|restart:)'
  $vEsc = [regex]::Escape($ver)
  @(
    Verd "$tag-a first page = upgrade page (no privileges / folder page)" ($first -match 'Mise . jour de P.pite' -and $first -notmatch 'seulement pour moi|Dossier de destination') "'$(Cut $first 400)'"
    Verd "$tag-b subtitle names both versions" ($first -match "mis . jour en $vEsc") ''
    Verd "$tag-c folder, components, tasks shown" ($first -match [regex]::Escape("Dossier : $app") -and $first -match 'Composants : P.pite, images des mots' -and $first -match 'T.ches : d.marrer avec Windows, ouvrir les fichiers \.pepite') ''
    Verd "$tag-d 2 clicks" ($clicks.Count -eq 2 -and -not ($clicks -match 'NO BUTTON')) ($clicks -join ' ; ')
    Verd "$tag-e Pepite closed without a question (quit requested)" ($procsBefore -eq 0 -or @(Lines $m 'quit requested').Count -ge 1) "running before $procsBefore"
    Verd "$tag-f setup log upgrade line" (@($slog -match "upgrade: .* -> $vEsc").Count -ge 1) ($slog -join ' | ')
    Verd "$tag-g finished page: mis a jour" ($fin -match "mis . jour en $vEsc" -and $fin -match 'Mise . jour termin.e') "'$(Cut $fin)'"
    Verd "$tag-h Terminer starts Pepite, not elevated" ($up -and $elev -eq 'no') "elev $elev cmd '$cmd'"
    Verd "$tag-i same active profile" ((ActiveProfile) -eq $act) "$act -> $(ActiveProfile)"
    Verd "$tag-j data hash-identical except settings.json" (SameHashes $hBefore $hAfter) "$($hBefore.Count) files; differ: $(DiffHashes $hBefore $hAfter)"
    Verd "$tag-k one version zip with application/settings.json" ($z.Count -eq $zBefore + 1 -and $entries -contains 'application/settings.json' -and @($entries -match 'profile\.json$').Count -ge 2) "$($z.Count) zips; entries: $($entries -join ', ')"
    Verd "$tag-l state.lastVersion" ((LastVersion) -eq $ver) "'$(LastVersion)'"
    Verd "$tag-m Run value unchanged" ((RunValue 'HKCU') -eq $runBefore) "'$(RunValue 'HKCU')'"
    "$tag setup gone $gone"
  )
}

# the version backup's duration: lastVersion removed, a --log start makes the backup again
function U1Time([string]$exe = "$PU\Pepite.exe") {
  PepQuit $exe; Start-Sleep 1
  $p = Join-Path $DATA 'settings.json'
  $j = Get-Content $p -Raw | ConvertFrom-Json
  $j.state.PSObject.Properties.Remove('lastVersion')
  [IO.File]::WriteAllText($p, ($j | ConvertTo-Json -Depth 20))
  $m = PepStart $exe
  "U1 timing: $((Lines $m 'version: ') -join ' | ')"
}

# U1-b the same version again, " Modifier les choix ": Suivant, the previous ticks, the ready page names the repair; Annuler -> 2
function U1b([string]$ver = '0.11.0') {
  $p = Start-Process (InstTest) -PassThru
  $page = WaitSetupText 'Mise . jour de P.pite' 120
  Shot 'U1b-01-repair-page' | Out-Null
  $s = @()
  $s += Tick 'Modifier les choix'
  Start-Sleep 1; Shot 'U1b-02-ticked' | Out-Null
  $next = SetupText
  $s += Btn '^Suivant' 30
  Start-Sleep 1; Shot 'U1b-03-components' | Out-Null; $comp = SetupText
  $s += Btn '^Suivant' 30
  Start-Sleep 1; Shot 'U1b-04-tasks' | Out-Null; $tasks = SetupText
  $s += Btn '^Suivant' 30
  Start-Sleep 1; Shot 'U1b-05-ready' | Out-Null; $memo = SetupMemo
  $s += Btn '^Annuler' 30
  $s += Btn '^Oui$' 30
  $p.WaitForExit(60000) | Out-Null
  $vEsc = [regex]::Escape($ver)
  @(
    Verd 'U1b-a repair subtitle' ($page -match 'il sera r.install. \(r.paration\)') "'$(Cut $page)'"
    Verd 'U1b-b ticked: button Suivant' ($next -match '\| Suivant \| Annuler$') "'$(Cut $next 80)...'"
    Verd 'U1b-c components page (ticks: see the screenshot, the list items are not in UIA)' ($comp -cmatch 'Composants . installer') ''
    Verd 'U1b-d tasks page' ($tasks -cmatch 'T.ches suppl.mentaires') ''
    Verd 'U1b-e ready page: Mise a jour 0.11.0 -> 0.11.0' ($memo -match "Mise . jour : P.pite $vEsc . $vEsc") "'$(Cut $memo 200)'"
    Verd 'U1b-f Cancel -> exit 2' ($p.ExitCode -eq 2) "exit $($p.ExitCode); $($s -join ' ; ')"
  )
}

# U1-c silent upgrade with Pepite running: restarted with --autostart, not elevated; U1-d the same with /NOLANCER
function U1c([switch]$NoLancer) {
  $exe = "$PU\Pepite.exe"
  # a fully started copy (its single-instance mutex exists only once the start-up is done): quit, then start with --log
  PepQuit $exe; Start-Sleep 1; PepStart $exe | Out-Null
  $tag = if ($NoLancer) { 'U1d' } else { 'U1c' }
  $a = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$OUT12\$tag-setup.log`"")
  if ($NoLancer) { $a += '/NOLANCER' }
  $r = RunWatched (InstTest) $a
  $up = WaitPep 15; Start-Sleep 3
  Shot "$tag-01-after-silent-upgrade" | Out-Null
  $slog = SetupLogLines '(restart:|Pepite:)' "$OUT12\$tag-setup.log"
  if ($NoLancer) {
    @( Verd 'U1d /NOLANCER: exit 0, Pepite not running' ($r.Exit -eq 0 -and -not $up -and @($slog -match 'restart: skipped \(/NOLANCER\)').Count -ge 1) "exit $($r.Exit) | $($slog -join ' | ')" )
  } else {
    @( Verd 'U1c silent: exit 0, Pepite back with --autostart, not elevated' ($r.Exit -eq 0 -and $up -and (PepCmd) -match '--autostart' -and (PepElev) -eq 'no' -and @($slog -match 'restart: started').Count -ge 1) "exit $($r.Exit) | cmd '$(PepCmd)' | elev $(PepElev) | $($slog -join ' | ')" )
  }
}

# U6 downgrade: a) the old setup over 0.11.0 (no guard before 0.11.0); b) / c) a fake newer DisplayVersion
function U6a([Parameter(Mandatory)][string]$OldSetup) {
  $n = @(Get-ChildItem (Join-Path $DATA 'profiles') -Directory -EA 0).Count
  $r = RunWatched $OldSetup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$OUT12\U6a-setup.log`"")
  PepQuit "$PU\Pepite.exe"
  $m = PepStart "$PU\Pepite.exe"
  $l = @(Lines $m 'profiles: loaded') | Select-Object -First 1
  @( Verd 'U6a old setup accepted (exit 0), old Pepite reads every profile' ($r.Exit -eq 0 -and $l -match "loaded $n in" -and $l -match ', 0 unreadable') "exit $($r.Exit) | '$l'" )
}
function U6Fake([string]$v) { Set-ItemProperty -Path "HKCU:\$UKEY" -Name DisplayVersion -Value $v }
function U6b {
  $exe = "$PU\Pepite.exe"
  if (-not (PepProcs)) { PepStart $exe | Out-Null }
  $tree = TreeHash $PU; $run = RunValue 'HKCU'
  U6Fake '0.11.9'
  $p = Start-Process (InstTest) -PassThru; $null = $p.Handle   # the handle now, or ExitCode stays empty
  $box = WaitSetupText 'plus r.cente' 120
  Start-Sleep 1; Shot 'U6b-01-refusal' | Out-Null
  # the message box: Enter on it (a UIA click on its OK button may land on another window)
  $w = Get-Process | Where-Object { $_.ProcessName -match 'installation\.tmp$' -and $_.MainWindowHandle -ne 0 } | Select-Object -First 1
  if ($w) { [PI12a]::SetForegroundWindow($w.MainWindowHandle) | Out-Null; Start-Sleep -Milliseconds 500; [PI12a]::Key(0x0D) }
  $ok = "enter on '$($w.MainWindowTitle)'"
  $p.WaitForExit(60000) | Out-Null
  $dv = RegVal 'HKCU' $UKEY 'DisplayVersion'
  @( Verd 'U6b refusal box with 0.11.9 and 0.11.0; OK -> exit 1; nothing changed; Pepite still running' ($box -match '0\.11\.9' -and $box -match '0\.11\.0' -and $p.ExitCode -eq 1 -and (TreeHash $PU) -eq $tree -and $dv -eq '0.11.9' -and (RunValue 'HKCU') -eq $run -and (PepProcs)) "exit $($p.ExitCode) | $ok | '$(Cut $box 400)'" )
}
function U6c {
  $r = RunWatched (InstTest) @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/CURRENTUSER', "/LOG=`"$OUT12\U6c-setup.log`"")
  $l = SetupLogLines 'downgrade refused' "$OUT12\U6c-setup.log"
  U6Fake '0.11.0'
  @( Verd 'U6c silent: exit 1, no window, log line' ($r.Exit -eq 1 -and -not $r.Windows -and @($l -match 'downgrade refused: installed 0\.11\.9, this setup 0\.11\.0').Count -ge 1) "exit $($r.Exit) | windows '$($r.Windows)' | $($l -join ' | ')" )
}

# U8: the old version in C:\Outils\Pepite (U1Prep -dir), then U1 -dir; the shortcut and the Run value point there
function U8Check([string]$dir = 'C:\Outils\Pepite') {
  $lnk = Get-ChildItem "$env:APPDATA\Microsoft\Windows\Start Menu\Programs" -Filter 'P*pite.lnk' -EA 0 | Select-Object -First 1
  $target = if ($lnk) { (New-Object -ComObject WScript.Shell).CreateShortcut($lnk.FullName).TargetPath } else { '' }
  @( Verd 'U8 installed in the custom folder (Run value, shortcut), no default folder' ((Test-Path "$dir\Pepite.exe") -and (RunValue 'HKCU') -eq "`"$dir\Pepite.exe`" --autostart" -and $target -eq "$dir\Pepite.exe" -and -not (Test-Path $PU)) "run '$(RunValue 'HKCU')' | lnk '$target'" )
}

# U2 (all users, as SYSTEM: the host runs both setups). In the pupil's session: before, after the upgrade, after a new logon
function U2Before { "U2 before: running $((PepProcs).Count) cmd '$(PepCmd)' | zips $((VersionZips).Count)" }
function U2After { Start-Sleep 20; @( Verd 'U2a pupil copy closed and not restarted' (-not (PepProcs)) "procs $((PepProcs).Count)" ) }
function U2Logon {
  $p = $null; for ($i = 0; $i -lt 120 -and -not $p; $i++) { $p = PepProcs | Select-Object -First 1; if (-not $p) { Start-Sleep -Milliseconds 500 } }
  Start-Sleep 10
  Shot 'U2-01-after-logon' | Out-Null
  $ver = if ($p) { (Get-Item $p.Path).VersionInfo.ProductVersion } else { '' }
  @( Verd 'U2b next logon: new version with --autostart, one version zip' ([bool]$p -and (PepCmd) -match '--autostart' -and $ver -like '0.11.0*' -and (VersionZips).Count -eq 1) "ver '$ver' cmd '$(PepCmd)' zips $((VersionZips).Count) lastVersion '$(LastVersion)'" )
}
