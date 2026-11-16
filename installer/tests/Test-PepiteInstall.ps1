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
# installed files against files.sha256 (the full component set): missing / different files
function TreeCheck([string]$dir, [string]$list, [string[]]$skip = @()) {
  $bad = @()
  foreach ($l in Get-Content $list) {
    $h, $rel = $l -split '  ', 2
    if ($skip | Where-Object { $rel -like $_ }) { continue }
    $p = Join-Path $dir $rel
    if (-not (Test-Path $p)) { $bad += "missing $rel" } elseif ((Get-FileHash $p -Algorithm SHA256).Hash.ToLower() -ne $h) { $bad += "differs $rel" }
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
