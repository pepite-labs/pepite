# Host test in Microsoft Word: caret text reading (plan 01, WH1-WH7) and punctuation / spacing (plan 04,
# WP2 WP4 WP4b WP5 WP4en WP4fr2 WP8: -Suite plan04), re-read of edits (plan 06, WR1-WR10: -Suite plan06, ~3 min) and
# reading of the selected text (plan 07, WS1-WS9: -Suite plan07, ~1.5 min; speech plays through the speakers).
# Owner-approved exception: Word only. Do not touch the keyboard/mouse during the run (~2-3 min each suite).
# Plan 06 switches once to this script's own console window and back to Word (WR5, the « window switch » case).
# Plan 04 writes %APPDATA%\PredictKeyboard\settings.json (still the settings folder in 0.6.1) for its two phases (text.punctuationSpace french, then
# none) and restores the user's original file (or removes it) at the end; a copy is kept in the output folder.
# The app is started with --no-promote (no tray registry change).
# Safety: every input batch first checks that Word's window is in front (one SetForegroundWindow retry,
# else the scenario aborts); injected input carries dwExtraInfo = 0; no Word option is changed; the test
# document is closed unsaved; Word is quit only if this script started it; the app (Pepite.exe since 0.6.1,
# PredictKeyboard.exe before; both accepted, started with --no-welcome) is stopped with
# --quit (and --exit-after 300 as a backstop), never killed.
# A framework-dependent build needs the Microsoft.WindowsDesktop.App 10.x runtime installed on the host (otherwise it
# only shows "You must install .NET"): use a SELF-CONTAINED publish, which carries it, and pass the DB explicitly.
# Usage:  dotnet publish src/Predict.Keyboard -c Release -r win-x64 --self-contained true -o <work>\host-keyboard
#         powershell -ExecutionPolicy Bypass -File src\Predict.Keyboard.Tests\host\WordHostTest.ps1 -Exe <work>\host-keyboard\Pepite.exe
#                    [-Suite plan01|plan04|plan06|plan07|all] [-Only WP4en,WP4fr2] [-OutDir <dir>]
#         (<work> = a bulk-data folder off the system drive; or set PREDICT_WORK_DIR and omit -Exe / -OutDir)
# Plan 07 drags / double-clicks in the document at points Word itself gives (ActiveWindow.GetPoint), clicks our bar's
# buttons found by UI Automation in our process, and checks before every click / drag that no other window covers the point.
param(
  [ValidateSet('plan01', 'plan04', 'plan06', 'plan07', 'all')][string]$Suite = 'plan01',
  # run only these scenarios of the suite (e.g. -Only WP4en,WP4fr2); empty = all
  [string[]]$Only = @(),
  # default: <PREDICT_WORK_DIR>\host-keyboard\Pepite.exe, else PredictKeyboard.exe (builds up to 0.6.0)
  [string]$Exe = '',
  [string]$Db = (Join-Path $PSScriptRoot '..\..\Data.Build\out\prediction.sqlite'),
  [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
# -Only accepts "WP4en,WP4fr2" as one string too (powershell -File passes it unsplit)
$Only = @($Only -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$KnownScenarios = @('WH1', 'WH2', 'WH5', 'WH2-end', 'WH2b', 'WH3', 'WH4', 'WH6', 'WH7', 'WP2', 'WP4', 'WP4b', 'WP5', 'WP4en', 'WP4fr2', 'WP8',
                    'WR1', 'WR2', 'WR3', 'WR4', 'WR5', 'WR6', 'WR7', 'WR8', 'WR9', 'WR10',
                    'WS1', 'WS2', 'WS3', 'WS4', 'WS5', 'WS6', 'WS7', 'WS8', 'WS9')
$unknown = @($Only | Where-Object { $KnownScenarios -notcontains $_ })
if ($unknown.Count -gt 0) { throw "unknown scenario(s) in -Only: $($unknown -join ', ') (known: $($KnownScenarios -join ', '))" }
$script:ranScenarios = 0
if (-not $Exe) {
  if (-not $env:PREDICT_WORK_DIR) { throw "pass -Exe <publish>\Pepite.exe (or set PREDICT_WORK_DIR)" }
  $Exe = @('Pepite.exe', 'PredictKeyboard.exe' | ForEach-Object { Join-Path $env:PREDICT_WORK_DIR "host-keyboard\$_" } | Where-Object { Test-Path $_ })[0]
}
if (-not $OutDir) {
  if (-not $env:PREDICT_WORK_DIR) { throw "pass -OutDir <dir> (or set PREDICT_WORK_DIR)" }
  $OutDir = Join-Path $env:PREDICT_WORK_DIR ("word-host\" + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
}
$Exe = (Resolve-Path $Exe).Path
$KbName = [IO.Path]::GetFileNameWithoutExtension($Exe)
$Db = (Resolve-Path $Db).Path

Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WH {
  [StructLayout(LayoutKind.Sequential)] public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }
  [StructLayout(LayoutKind.Explicit)] public struct U { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public U u; }
  [DllImport("user32.dll", SetLastError=true)] static extern uint SendInput(uint n, INPUT[] i, int sz);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr c);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
  public static IntPtr RootAt(int x, int y) { return GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), 2); }
  [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] static extern uint GetPid(IntPtr h, out uint pid);
  public static uint PidOf(IntPtr h) { uint p; GetPid(h, out p); return p; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, IntPtr pid);
  [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
  [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
  [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
  // The foreground lock refuses SetForegroundWindow from a background process; attaching to the
  // foreground thread's input queue for the call is the documented way around it (no input sent).
  public static bool Activate(IntPtr h) {
    uint fgT = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
    bool att = fgT != 0 && fgT != me && AttachThreadInput(me, fgT, true);
    BringWindowToTop(h); bool ok = SetForegroundWindow(h);
    if (att) AttachThreadInput(me, fgT, false);
    return ok;
  }
  static int Sz { get { return Marshal.SizeOf(typeof(INPUT)); } }
  static INPUT K(ushort vk, ushort scan, uint fl) { var i = new INPUT { type = 1 }; i.u.ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = fl, dwExtraInfo = UIntPtr.Zero }; return i; }
  static bool Ext(ushort vk) { return (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E; }
  public static uint Key(ushort vk, bool ctrl) {
    uint e = Ext(vk) ? 1u : 0u;
    var l = new System.Collections.Generic.List<INPUT>();
    if (ctrl) l.Add(K(0x11, 0, 0));
    l.Add(K(vk, 0, e)); l.Add(K(vk, 0, e | 2));
    if (ctrl) l.Add(K(0x11, 0, 2));
    return SendInput((uint)l.Count, l.ToArray(), Sz);
  }
  // a real layout key with Shift held (AZERTY: Shift + VK_OEM_COMMA = '?')
  public static uint ShiftKey(ushort vk) {
    var l = new INPUT[] { K(0x10, 0, 0), K(vk, 0, 0), K(vk, 0, 2), K(0x10, 0, 2) };
    return SendInput(4, l, Sz);
  }
  [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
  public static uint Char(char c) { return SendInput(2, new INPUT[] { K(0, c, 4), K(0, c, 6) }, Sz); }
  // plan 07: Shift + an arrow key (extended key: the real arrow, not the keypad)
  public static uint ShiftArrow(ushort vk) {
    var l = new INPUT[] { K(0x10, 0, 0), K(vk, 0, 1), K(vk, 0, 3), K(0x10, 0, 2) };
    return SendInput(4, l, Sz);
  }
  // plan 07: left button down / up at the current cursor position (drags)
  public static uint Button(bool down) {
    var a = new INPUT[1]; a[0].type = 0; a[0].u.mi = new MOUSEINPUT { dwFlags = down ? 2u : 4u };
    return SendInput(1, a, Sz);
  }
  public static uint Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(60);
    var a = new INPUT[2]; a[0].type = 0; a[0].u.mi = new MOUSEINPUT { dwFlags = 2 }; a[1].type = 0; a[1].u.mi = new MOUSEINPUT { dwFlags = 4 };
    return SendInput(2, a, Sz);
  }
}
"@
[WH]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null

$log = Join-Path $env:TEMP 'predict-keyboard.log'
$results = [ordered]@{}
$notes = New-Object System.Collections.ArrayList
function Note($s) { [void]$notes.Add($s); Write-Host $s }
function LogLines { if (Test-Path $log) { @(Get-Content $log -Encoding UTF8) } else { @() } }
$global:mark = 0
function Mark { $global:mark = (LogLines).Count }
function Since { LogLines | Select-Object -Skip $global:mark | Where-Object { $_ -notmatch '  key vk=' } }

# Word is activated once at setup. If anything else comes to the front during the run (e.g. the user
# starts working in another app), abort: never take focus back from the user, never type elsewhere.
function Guard {
  if ([WH]::GetForegroundWindow() -ne $script:wordHwnd) { throw "SAFETY: Word is no longer in the foreground - input aborted" }
}
function TypeText([string]$s) { Guard; foreach ($c in $s.ToCharArray()) { Guard; [WH]::Char($c) | Out-Null; Start-Sleep -Milliseconds 70 } }
function Key([int]$vk, [switch]$Ctrl) { Guard; [WH]::Key([uint16]$vk, [bool]$Ctrl) | Out-Null; Start-Sleep -Milliseconds 150 }
# F1 only when the latest state of the app has a non-empty prediction (otherwise F1 reaches Word = Help).
function F1 {
  $last = LogLines | Select-Object -Skip $script:startMark | Where-Object { $_ -match "typed='|read \w+ src=|insert '" } | Select-Object -Last 1
  if ($last -notmatch "typed='.*-> \S") { throw "no prediction shown for the current state - F1 not pressed (last: $last)" }
  Key 0x70; Start-Sleep -Milliseconds 600
}
function Text { $script:d.Content.Text.TrimEnd("`r") }
function Caret { $script:w.Selection.Start }
function OraclePrefix { $t = $script:d.Range(0, (Caret)).Text; if ($t -match '(?i)([a-zà-ÿœ]+)$') { $matches[1] } else { '' } }
function ClickIdx([int]$i) {
  $r = $script:d.Range($i, $i)
  try { $script:w.ActiveWindow.ScrollIntoView($r, $true) } catch { }
  Start-Sleep -Milliseconds 150
  $l = 0; $t = 0; $wd = 0; $h = 0
  $script:w.ActiveWindow.GetPoint([ref]$l, [ref]$t, [ref]$wd, [ref]$h, $r)   # screen px of the character, from Word itself
  $x = $l + 1; $y = $t + [int]($h / 2)
  $wr = New-Object WH+RECT; [void][WH]::GetWindowRect($script:wordHwnd, [ref]$wr)
  if ($x -lt $wr.L -or $x -ge $wr.R -or $y -lt $wr.T -or $y -ge $wr.B) { throw "SAFETY: point $x,$y is outside Word's window [$($wr.L),$($wr.T),$($wr.R),$($wr.B)] - click aborted" }
  Guard
  $root = [WH]::RootAt($x, $y)
  if ($root -ne $script:wordHwnd) {
    # something on top of the text: our suggestion bar (topmost) or a Word popup. Esc hides either (in Word, Esc on a
    # collapsed caret changes nothing), once; anything else (another app) aborts
    $owner = [WH]::PidOf($root)
    $ours = $script:p -and -not $script:p.HasExited -and $owner -eq [uint32]$script:p.Id
    $words = $owner -eq [WH]::PidOf($script:wordHwnd)
    if ($ours -or $words) {
      Note "  harness: point $x,$y covered by $(if ($ours) { 'our bar' } else { 'a Word popup' }) (root $root) - Esc"
      Key 0x1B; Start-Sleep -Milliseconds 400
      $root = [WH]::RootAt($x, $y)
    }
    if ($root -ne $script:wordHwnd) { throw "SAFETY: point $x,$y is not over Word (root $root, pid $([WH]::PidOf($root))) - click aborted" }
  }
  [WH]::Click($x, $y) | Out-Null
  Start-Sleep -Milliseconds 400
  $s = Caret
  if ([math]::Abs($s - $i) -gt 1) { Note "  harness: click idx $i landed at $s" }
  elseif ($s -ne $i) { Note "  harness: click idx $i landed at $s (±1)" }
  return $s
}
function Prefill([string]$s) { $script:d.Content.Text = $s; Start-Sleep -Milliseconds 300 }
# ---- plan 04 helpers
function Cps([string]$t) { ($t.ToCharArray() | ForEach-Object { if ([int]$_ -lt 33 -or [int]$_ -gt 126) { 'U+{0:X4}' -f [int]$_ } else { [string]$_ } }) -join ' ' }
# press the F-key of $word (case-insensitive: the bar may capitalise it) in the latest suggestion line since Mark
# (never F-keys blindly: F1 = Word help). Returns the word as shown, i.e. as inserted.
function PickWord([string]$word) {
  $l = Since | Where-Object { $_ -match "typed=.* -> (.*)  \(\d+ ms\)$" } | Select-Object -Last 1
  if ($l -notmatch "typed=.* -> (.*)  \(\d+ ms\)$") { throw "no suggestion line - '$word' not picked" }
  $ws = @($matches[1] -split ', ')
  $i = -1; for ($k = 0; $k -lt $ws.Count; $k++) { if ($ws[$k] -ieq $word) { $i = $k; break } }
  if ($i -lt 0) { throw "'$word' not in the bar ($($matches[1]))" }
  Key (0x70 + $i); Start-Sleep -Milliseconds 700
  $ws[$i]
}
# U+0020, U+00A0, U+202F, U+2009... (any Unicode space separator)
function IsSpaceLike([char]$c) { [Globalization.CharUnicodeInfo]::GetUnicodeCategory($c) -eq [Globalization.UnicodeCategory]::SpaceSeparator }
# two consecutive space-like code points anywhere (mixed U+202F + U+00A0 included)
function HasDoubleSpace([string]$t) { for ($i = 1; $i -lt $t.Length; $i++) { if ((IsSpaceLike $t[$i - 1]) -and (IsSpaceLike $t[$i])) { return $true } }; $false }
# exact document text, by code point; flags double spaces. -Wait: the read-back runs ~60 ms after the key.
function CheckText($name, [string]$expected, $extra = '', [int]$Wait = 1000) {
  Start-Sleep -Milliseconds $Wait
  $t = Text
  $dbl = HasDoubleSpace $t
  Pass $name ($t -ceq $expected -and -not $dbl) "text [$t] cps [$(Cps $t)] expected [$(Cps $expected)] double-space=$dbl $extra"
}
# exactly one space-like char before the final punctuation, whichever (Word's or ours)
function CheckOneSpaceBefore($name, [string]$stem, [char]$punct, $extra = '') {
  Start-Sleep -Milliseconds 1000
  $t = Text
  $ok = $t.Length -eq $stem.Length + 2 -and $t.StartsWith($stem) -and (IsSpaceLike $t[$t.Length - 2]) -and $t[$t.Length - 1] -eq $punct -and -not (HasDoubleSpace $t)
  Pass $name $ok "text [$t] cps [$(Cps $t)] (one space-like before '$punct' expected) $extra"
}
# French text (proofing language of THIS test document only: wdFrench 1036), caret at its end
function AtEndOf([string]$s) { Prefill $s; $script:d.Content.LanguageID = 1036; ClickIdx $s.Length | Out-Null; Start-Sleep -Milliseconds 400; Mark }
$SettingsPath = Join-Path $env:APPDATA 'PredictKeyboard\settings.json'
function WriteSettings([string]$mode) {
  New-Item -ItemType Directory -Force (Split-Path $SettingsPath) | Out-Null
  [IO.File]::WriteAllText($SettingsPath, '{"schema":1,"text":{"punctuationSpace":"' + $mode + '"}}')
}
function StartKb([switch]$Setup) {
  Mark; $script:startMark = $global:mark
  $script:p = Start-Process $Exe -ArgumentList ('"' + $Db + '"'), '--log', '--no-promote', '--no-welcome', '--exit-after', '300' -PassThru
  $deadline = (Get-Date).AddSeconds(30)
  while ((Get-Date) -lt $deadline -and -not $script:p.HasExited -and -not ((Since) -match 'reader warm-up')) { Start-Sleep -Milliseconds 500 }
  $boot = Since
  Add-Content $runLog $boot
  if (-not ($boot -match 'hooks installed') -or -not ($boot -match 'reader warm-up')) { throw "$KbName did not start" }
  Note ("settings: " + (($boot | Where-Object { $_ -match 'settings: words' }) -replace '^.*settings: ', ''))
  # at setup Word is brought to the front once; on a restart mid-run focus is only checked, never taken back
  if ($Setup) { [WH]::Activate($script:wordHwnd) | Out-Null } else { Guard }
  Start-Sleep -Milliseconds 800
}
function StopKb {
  if ($script:p -and -not $script:p.HasExited) { $q = Start-Process $Exe -ArgumentList '--quit' -PassThru; [void]$q.WaitForExit(10000) }
  if ($script:p -and -not $script:p.WaitForExit(5000)) { throw "$KbName did not exit on --quit" }
}

function ReadLine([string]$reason) { Since | Where-Object { $_ -match "read $reason " } | Select-Object -Last 1 }
function Pass($name, [bool]$ok, $detail) { $results[$name] = $(if ($ok) { 'PASS' } else { 'FAIL' }) + " - $detail"; Write-Host "$name $($results[$name])" }
function Scenario($name, [scriptblock]$body) {
  if ($Only.Count -gt 0 -and $Only -notcontains $name) { return }
  $script:ranScenarios++
  Mark
  try { & $body } catch { Pass $name $false ("aborted: " + $_.Exception.Message) }
  $lines = Since
  Add-Content $script:runLog "===== $name"; Add-Content $script:runLog $lines
  if ($lines -match 'hook error|predict error|reader error') { Pass $name $false "error line in log" }
  if ($p.HasExited) { Pass $name $false "$KbName exited" }
}

New-Item -ItemType Directory -Force $OutDir | Out-Null
$runLog = Join-Path $OutDir 'predict-keyboard.log.part'
if (Get-Process Pepite, PredictKeyboard -EA SilentlyContinue) { throw "Pepite / PredictKeyboard already running on the host - stop it first" }

$wordPidsBefore = @(Get-Process WINWORD -EA SilentlyContinue | ForEach-Object Id)
$w = $null; $d = $null; $p = $null; $startMark = 0; $createdWord = $false
try {
  $w = New-Object -ComObject Word.Application
  $createdWord = @(Get-Process WINWORD -EA SilentlyContinue | Where-Object { $wordPidsBefore -notcontains $_.Id }).Count -gt 0
  Note "Word: $(if ($createdWord) { 'new instance started by the test' } else { 'attached to an existing instance (will not be quit)' })"
  $w.Visible = $true
  $d = $w.Documents.Add()
  $w.Activate()
  $wordHwnd = [IntPtr]$w.ActiveWindow.Hwnd
  [WH]::Activate($wordHwnd) | Out-Null; Start-Sleep -Milliseconds 500

  if ($Suite -ne 'plan01') {
    # back up the user's settings file before any write (restored in finally)
    $script:settingsHad = Test-Path $SettingsPath
    if ($script:settingsHad) { Copy-Item $SettingsPath (Join-Path $OutDir 'settings.json.orig') -Force }
    $script:settingsTouched = $true
    WriteSettings 'french'
  }
  StartKb -Setup

  if ($Suite -in 'plan01', 'all') {
  Scenario 'WH1' {
    Prefill ''; ClickIdx 0 | Out-Null; Key 0x1B   # Esc: keys only from here
    Mark; TypeText 'il fait '; Start-Sleep -Milliseconds 700
    $l = Since; $pred = $l | Where-Object { $_ -match "typed='' ctx=\[il fait\] src=keys" } | Select-Object -Last 1
    $W = if ($pred -match '-> ([^,]+),') { $matches[1].Trim() } else { '' }
    F1; $t = Text
    Pass 'WH1' ([bool]$pred -and $W -and $t -match "(?i)il fait $([regex]::Escape($W)) $") "next-word '$W', text [$t]"
  }
  Scenario 'WH2' {
    Prefill 'la maison est belle'; ClickIdx 6 | Out-Null; Start-Sleep -Milliseconds 300
    $r = ReadLine 'click'; F1; $t = Text; $ins = Since | Where-Object { $_ -match 'insert' } | Select-Object -Last 1
    Pass 'WH2' ($r -match "status=Ok .*prefix='mai' suffix='son' next=␠ ctx=\[la\]" -and $t -match '(?i)^la \w+ est belle$' -and $ins -match 'space=False') "$r | [$t] | $ins"
    $script:wh2 = $t
  }
  Scenario 'WH5' {
    Key 0x5A -Ctrl; Start-Sleep -Milliseconds 500
    $r = ReadLine 'ctrl'; $o = OraclePrefix
    Pass 'WH5' ($r -match "prefix='$o'" -and $r -match 'status=Ok') "after undo text [$(Text)] caret $(Caret) oracle prefix '$o' | $r"
  }
  Scenario 'WH2-end' {
    Prefill 'la maison est belle'; ClickIdx 9 | Out-Null; Start-Sleep -Milliseconds 300
    $r = ReadLine 'click'; F1; $t = Text
    Pass 'WH2-end' ($r -match "prefix='maison' suffix='' next=␠" -and $t -match '(?i)^la \w+ est belle$') "$r | [$t]"
  }
  Scenario 'WH2b' {
    Prefill 'la maison'; ClickIdx 6 | Out-Null; Start-Sleep -Milliseconds 300
    $r = ReadLine 'click'; F1; $t = Text
    # Word's DocumentRange excludes the final paragraph mark: after clamping, the document end reads as
    # end of text (next=EOT -> "la W "); next=\r (no space) is accepted too.
    $ok = ($r -match "prefix='mai' suffix='son' next=EOT" -and $t -match '(?i)^la \w+ $') -or
          ($r -match "prefix='mai' suffix='son' next=\\r" -and $t -match '(?i)^la \w+$')
    Pass 'WH2b' $ok "$r | [$t]"
  }
  Scenario 'WH3' {
    Prefill 'je ne sais'; ClickIdx 10 | Out-Null; Start-Sleep -Milliseconds 300
    Mark; Key 0x23; Key 0x25; Key 0x27; Start-Sleep -Milliseconds 500
    $r = ReadLine 'nav'
    TypeText ' '; Start-Sleep -Milliseconds 600
    $l = Since | Where-Object { $_ -match "typed='' ctx=\[je ne sais\]" } | Select-Object -Last 1
    TypeText 'p'; Start-Sleep -Milliseconds 600
    $pl = Since | Where-Object { $_ -match "typed='p'" } | Select-Object -Last 1
    $W = if ($pl -match '-> ([^,]+),') { $matches[1].Trim() } else { '' }
    F1; $t = Text
    Pass 'WH3' ($r -match "src=uia status=Ok .*prefix='sais' .*ctx=\[je ne\]" -and $l -match '\bpas\b' -and $t -match "(?i)^je ne sais $([regex]::Escape($W)) $") "$r | next: $l | [$t]"
  }
  Scenario 'WH4' {
    Prefill 'la maison bleue'; ClickIdx 15 | Out-Null; Start-Sleep -Milliseconds 300
    Mark; Key 0x08 -Ctrl; Start-Sleep -Milliseconds 600
    $r = ReadLine 'ctrl'; $t = $script:d.Content.Text.TrimEnd("`r")
    Pass 'WH4' ($t -eq 'la maison ' -and $r -match "prefix='' .*ctx=\[la maison\] sep=1") "[$t] | $r"
  }
  Scenario 'WH6' {
    Prefill ("un deux trois`rquatre"); ClickIdx 13 | Out-Null; Start-Sleep -Milliseconds 300
    $r = ReadLine 'click'
    $ok1 = $r -match "prefix='trois' .*ctx=\[un deux\]"
    $end = $script:d.Content.End - 1
    $tb = $script:d.Tables.Add($script:d.Range($end, $end), 1, 1)
    $tb.Cell(1, 1).Range.Text = 'le chat'
    $ci = $tb.Cell(1, 1).Range.End - 1
    Mark; ClickIdx $ci | Out-Null; Start-Sleep -Milliseconds 400
    $r2 = ReadLine 'click'
    Pass 'WH6' ($ok1 -and -not $p.HasExited) "$r | table: $r2"
  }
  Scenario 'WH7' {
    Prefill ''; ClickIdx 0 | Out-Null; Start-Sleep -Milliseconds 300
    Mark; TypeText 'la mai'; Start-Sleep -Milliseconds 700
    $t = Text; $pl = Since | Where-Object { $_ -match "typed='mai'" } | Select-Object -Last 1
    Pass 'WH7' ($t -match '(?i)la mai$' -and [bool]$pl) "[$t] | $pl"
  }
  }

  if ($Suite -in 'plan06', 'all') {
    # Plan 06 (D23): re-read of the modified word / sentence when the caret leaves it. What is said is read from the
    # --log trace ("speak Reread '...'"); default speech settings (word / sentence echo off, re-read on).
    # Paragraphs are separated by `r (Word's paragraph mark, one character in Range indices).
    function Rereads { @(Since | Where-Object { $_ -match "speak Reread '(.*)'$" } | ForEach-Object { $_ -replace "^.*speak Reread '(.*)'$", '$1' }) }
    function RereadLines { @(Since | Where-Object { $_ -match 'reread |speak (Reread|Inserted)' }) }
    function FrenchDoc([string]$s) { Prefill $s; $script:d.Content.LanguageID = 1036 }
    function ExpectRereads($name, [string[]]$exp, $extra = '') {
      Start-Sleep -Milliseconds 1500
      $got = @(Rereads)
      Pass $name (($got -join ' | ') -ceq ($exp -join ' | ')) "said [$($got -join ' | ')] expected [$($exp -join ' | ')] text [$(Text)] $extra | $((RereadLines) -join ' / ')"
    }
    $L2 = "`rIl pleut."
    # one click before the scenarios: the first UIA read after a start is cold (Word's provider, COM interop); its time
    # is reported by WR10 as « cold first read » and kept out of the 150 ms budget, which applies to reads in use
    FrenchDoc "Echauffement du lecteur."; $script:warmMark = (LogLines).Count; ClickIdx 5 | Out-Null; Start-Sleep -Milliseconds 900
    $script:coldRead = [string](LogLines | Select-Object -Skip $script:warmMark | Where-Object { $_ -match ' read click src=' } | Select-Object -First 1)
    $script:afterWarm = (LogLines).Count
    Scenario 'WR1' {   # a letter added inside a word, click in the same sentence -> the word
      FrenchDoc ("Le cht dort bien." + $L2); ClickIdx 5 | Out-Null; Start-Sleep -Milliseconds 500; Mark
      TypeText 'a'; Start-Sleep -Milliseconds 400; ClickIdx 14 | Out-Null
      ExpectRereads 'WR1' @('chat')
    }
    Scenario 'WR2' {   # a word typed in the middle, click in the next paragraph -> the sentence only
      FrenchDoc ("Le chat dort." + $L2); ClickIdx 3 | Out-Null; Start-Sleep -Milliseconds 500; Mark
      TypeText 'gros '; Start-Sleep -Milliseconds 400; ClickIdx 24 | Out-Null
      ExpectRereads 'WR2' @('Le gros chat dort.')
    }
    Scenario 'WR3' {   # arrows inside the word -> nothing; into the next word -> the word; Down -> the sentence
      FrenchDoc ("Le cht dort bien." + $L2); ClickIdx 5 | Out-Null; Start-Sleep -Milliseconds 500; Mark
      TypeText 'a'; Start-Sleep -Milliseconds 400
      foreach ($k in 0x25, 0x27, 0x27, 0x27) { Key $k; Start-Sleep -Milliseconds 600 }
      Key 0x28; Start-Sleep -Milliseconds 600
      ExpectRereads 'WR3' @('chat', 'Le chat dort bien.') ("same-word skips: " + @(Since | Where-Object { $_ -match 'skip\(same word\)' }).Count)
    }
    Scenario 'WR4' {   # Backspace + End -> the word; Delete + Ctrl+End -> the sentence
      FrenchDoc ("Le chaat dort." + $L2); ClickIdx 6 | Out-Null; Start-Sleep -Milliseconds 500; Mark
      Key 0x08; Start-Sleep -Milliseconds 400; Key 0x23
      ExpectRereads 'WR4a' @('chat')
      FrenchDoc ("Le xchat dort." + $L2); ClickIdx 3 | Out-Null; Start-Sleep -Milliseconds 500; Mark
      Key 0x2E; Start-Sleep -Milliseconds 600; Key 0x23 -Ctrl
      ExpectRereads 'WR4b' @('Le chat dort.')
    }
    Scenario 'WR5' {   # a suggestion inserted in the middle, then a window switch -> the sentence; the word said once
      FrenchDoc ("Le chat dort." + $L2); ClickIdx 3 | Out-Null; Start-Sleep -Milliseconds 500; Mark
      TypeText 'pe'; Start-Sleep -Milliseconds 700; $wd = PickWord 'petit'; Start-Sleep -Milliseconds 600
      $first = (Text) -split "`r" | Select-Object -First 1
      # window switch without input: a small window of this script comes to the front, then Word again (both by this
      # script; no console needed: the owner's run used a non-console host)
      Add-Type -AssemblyName System.Windows.Forms
      $f = New-Object System.Windows.Forms.Form
      try {
        $f.Text = 'WR5 - fenetre de test'; $f.Width = 360; $f.Height = 140; $f.StartPosition = 'Manual'; $f.Left = 40; $f.Top = 40
        $f.ShowInTaskbar = $false
        $f.Show(); [System.Windows.Forms.Application]::DoEvents()
        [WH]::Activate($f.Handle) | Out-Null
        for ($k = 0; $k -lt 18; $k++) { [System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 100 }
        $switched = [WH]::GetForegroundWindow() -eq $f.Handle
        $said = @(Rereads); $ins = @(Since | Where-Object { $_ -match "speak Inserted '" }).Count
      }
      finally {
        [WH]::Activate($script:wordHwnd) | Out-Null
        $f.Close(); $f.Dispose(); [System.Windows.Forms.Application]::DoEvents()
      }
      Start-Sleep -Milliseconds 800; Guard
      Pass 'WR5' ($switched -and ($said -join ' | ') -ceq $first -and $ins -eq 1) "switched=$switched said [$($said -join ' | ')] expected [$first]; inserted '$wd' spoken $ins time(s)"
    }
    Scenario 'WR6' {   # Word's own no-break space before '!' does not turn a move in the same sentence into a leave
      FrenchDoc ("Il fait beau`rFin."); ClickIdx 8 | Out-Null; Start-Sleep -Milliseconds 500; Mark
      TypeText 'tres '; Start-Sleep -Milliseconds 400; Key 0x23; Start-Sleep -Milliseconds 800
      TypeText '!'; Start-Sleep -Milliseconds 800                     # AutoCorrect (French) may add U+00A0 before '!'
      $t1 = Text
      ClickIdx 4 | Out-Null; Start-Sleep -Milliseconds 1200           # inside 'fait': same sentence
      $same = [string](Since | Where-Object { $_ -match 'reread .*landing=' } | Select-Object -Last 1)
      # what is said comes from the document (AutoCorrect « tres » -> « très »), never the typed form
      $doc = (Text) -replace [char]0x00A0, ' '
      $bad = @(Rereads | Where-Object { ($doc -replace '\s+', ' ') -notlike ('*' + ($_ -replace '\s+', ' ') + '*') })
      Pass 'WR6' ([bool]($same -match 'landing=Same') -and $bad.Count -eq 0) "text [$(Cps $t1)] last landing: $same | said not in the document: [$($bad -join ' | ')] | $((RereadLines) -join ' / ')"
    }
    Scenario 'WR7' {   # an edit inside a table cell, click outside the table -> the cell's sentence only
      FrenchDoc "Avant.`r"
      $end = $script:d.Content.End - 1
      $tb = $script:d.Tables.Add($script:d.Range($end, $end), 1, 2)
      $tb.Cell(1, 1).Range.Text = 'le cht dort'
      $tb.Cell(1, 2).Range.Text = 'autre cellule'
      $ci = $tb.Cell(1, 1).Range.Start + 5
      ClickIdx $ci | Out-Null; Start-Sleep -Milliseconds 500; Mark
      TypeText 'a'; Start-Sleep -Milliseconds 400
      ClickIdx 2 | Out-Null                                            # in 'Avant.'
      ExpectRereads 'WR7' @('le chat dort')
    }
    Scenario 'WR8' {   # AutoCorrect rewrites a word typed in the middle (ca -> ça): informational, the mirror does not see it
      FrenchDoc ("Il dit que tout va bien." + $L2); ClickIdx 11 | Out-Null; Start-Sleep -Milliseconds 500; Mark
      TypeText 'ca '; Start-Sleep -Milliseconds 600
      $t1 = (Text) -split "`r" | Select-Object -First 1
      ClickIdx ($t1.Length + 4) | Out-Null; Start-Sleep -Milliseconds 1500
      $said = @(Rereads)
      # the sentence said is the document's, not the typed form. Word's AutoCorrect « ca » -> « ça » does not always
      # apply: without it the case only shows that the document's text is said (INFO)
      $auto = $t1 -match [char]0x00E7
      Pass 'WR8' ($said.Count -eq 1 -and $said[0] -ceq $t1) "$(if (-not $auto) { 'INFO (Word did not AutoCorrect ca) ' })document [$t1] said [$($said -join ' | ')]"
    }
    Scenario 'WR9' {   # a sentence longer than the read window -> the modified word only (partial)
      $long = ('le chat dort ' * 55) + 'bien.'
      FrenchDoc ($long + $L2); ClickIdx 354 | Out-Null; Start-Sleep -Milliseconds 500; Mark   # 13 * 27 + 3: before 'chat'
      TypeText 'x'; Start-Sleep -Milliseconds 400
      ClickIdx ($long.Length + 5) | Out-Null
      ExpectRereads 'WR9' @('xchat') ("(partial: " + @(Since | Where-Object { $_ -match 'partial' }).Count + ")")
    }
    Scenario 'WR10' {  # caret reads in Word with the widened window stay within the 150 ms budget
      # budget: KeyboardPolicy.ReadTimeoutMs = 150 ms, for every read after the suite's warm-up click
      $ms = @(LogLines | Select-Object -Skip $script:afterWarm | Where-Object { $_ -match ' read (click|nav|focus|del) src=uia status=\w+ (\d+) ms' } |
              ForEach-Object { [int]($_ -replace '.* status=\w+ (\d+) ms.*', '$1') })
      Note "cold first read (warm-up click, not in the budget): $($script:coldRead)"
      $max = ($ms | Measure-Object -Maximum).Maximum
      # edit-probe load (one UIA probe per <= 150 ms while typing): count, outcome and probe-only latency, for the owner run
      $ep = @(LogLines | Select-Object -Skip $script:startMark | Where-Object { $_ -match 'reread: edit probe (\w+) (\d+) ms \(debounce (\d+) ms\)' } |
              ForEach-Object { if ($_ -match 'edit probe (\w+) (\d+) ms \(debounce (\d+) ms\)') { [pscustomobject]@{ St = $matches[1]; Ms = [int]$matches[2]; Wait = [int]$matches[3] } } })
      $epMs = @($ep | ForEach-Object Ms); $epMax = ($epMs | Measure-Object -Maximum).Maximum
      $epAvg = if ($epMs.Count) { [math]::Round(($epMs | Measure-Object -Average).Average, 1) } else { 0 }
      $epNot = @($ep | Where-Object { $_.St -ne 'NotPassword' }).Count
      $notProven = @(LogLines | Select-Object -Skip $script:startMark | Where-Object { $_ -match 'skip\(field not proven\)' }).Count
      Note "edit probes (plan 06): n=$($ep.Count) mean=$epAvg ms max=$epMax ms not-NotPassword=$epNot field-not-proven skips=$notProven all=[$($epMs -join ',')]"
      Pass 'WR10' ($ms.Count -gt 0 -and $max -le 150) "uia reads after warm-up n=$($ms.Count) max=$max ms all=[$($ms -join ',')] | cold first read: $($script:coldRead -replace '^\S+ ', '') | edit probes n=$($ep.Count) mean=$epAvg max=$epMax ms, not-NotPassword=$epNot, field-not-proven skips=$notProven"
    }
  }

  if ($Suite -in 'plan07', 'all') {
    # Plan 07 (D21/D24): the bar's reading mode for a selection in Word. What is shown and spoken is read from the --log
    # trace ("reading shown", "speak Reading", "reading progress"); our bar's buttons and tiles are found by UI Automation
    # (AutomationId) in our process. Default reading settings (on, Ctrl+C fallback off): the settings file written above.
    Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
    $AE = [Windows.Automation.AutomationElement]; $TS = [Windows.Automation.TreeScope]
    function Lines([string]$rx) { @(Since | Where-Object { $_ -match $rx }) }
    function LastShown { [string](@(Lines 'reading shown') | Select-Object -Last 1) }
    function WaitLine([string]$rx, [int]$ms = 4000) {
      $end = (Get-Date).AddMilliseconds($ms)
      while ((Get-Date) -lt $end) { if (@(Lines $rx).Count -gt 0) { return $true }; Start-Sleep -Milliseconds 100 }
      $false
    }
    function FrenchDoc7([string]$s) { Prefill $s; $script:d.Content.LanguageID = 1036 }
    # screen px of character $i, from Word's own layout (no scrolling here: the caller scrolled the start into view)
    function PointOf([int]$i) {
      $r = $script:d.Range($i, $i)
      $l = 0; $t = 0; $wd = 0; $h = 0
      $script:w.ActiveWindow.GetPoint([ref]$l, [ref]$t, [ref]$wd, [ref]$h, $r)
      @(($l + 1), ($t + [int]($h / 2)))
    }
    # the point must be over Word's own window (nothing on top: our bar, a Word popup, another app)
    function Uncovered([int]$x, [int]$y, [string]$what) {
      $wr = New-Object WH+RECT; [void][WH]::GetWindowRect($script:wordHwnd, [ref]$wr)
      if ($x -lt $wr.L -or $x -ge $wr.R -or $y -lt $wr.T -or $y -ge $wr.B) { throw "SAFETY: $what point $x,$y is outside Word's window - input aborted" }
      $root = [WH]::RootAt($x, $y)
      if ($root -ne $script:wordHwnd) { throw "SAFETY: $what point $x,$y is covered (root $root, pid $([WH]::PidOf($root))) - input aborted" }
    }
    # Esc (leaves the reading mode, closes Word's mini toolbar) and a click at the start: a clean state for the scenario
    function Reset7 { Guard; Key 0x1B; Start-Sleep -Milliseconds 300; ClickIdx 0 | Out-Null; Start-Sleep -Milliseconds 600 }
    function DragIdx([int]$a, [int]$b) {
      try { $script:w.ActiveWindow.ScrollIntoView($script:d.Range($a, $a), $true) } catch { }
      Start-Sleep -Milliseconds 200
      $p = @(PointOf $a); $q = @(PointOf $b)
      Uncovered $p[0] $p[1] 'drag start'; Uncovered $q[0] $q[1] 'drag end'
      Guard
      [void][WH]::SetCursorPos($p[0], $p[1]); Start-Sleep -Milliseconds 80
      [void][WH]::Button($true)
      try {
        Start-Sleep -Milliseconds 120
        for ($k = 1; $k -le 10; $k++) {
          [void][WH]::SetCursorPos([int]($p[0] + ($q[0] - $p[0]) * $k / 10), [int]($p[1] + ($q[1] - $p[1]) * $k / 10)); Start-Sleep -Milliseconds 30
        }
      }
      finally { [void][WH]::Button($false) }
      Start-Sleep -Milliseconds 200
      "drag $a..$b -> selection [$($script:w.Selection.Start),$($script:w.Selection.End)]"
    }
    function DblIdx([int]$i) {
      $p = @(PointOf $i); Uncovered $p[0] $p[1] 'double click'; Guard
      [void][WH]::Click($p[0], $p[1]); Start-Sleep -Milliseconds 90; [void][WH]::Click($p[0], $p[1]); Start-Sleep -Milliseconds 300
      "double click $i -> selection [$($script:w.Selection.Start),$($script:w.Selection.End)]"
    }
    function KbEl([string]$aid) {
      $c = New-Object Windows.Automation.PropertyCondition($AE::ProcessIdProperty, [int]$script:p.Id)
      foreach ($win in @($AE::RootElement.FindAll($TS::Children, $c))) {
        if ($win.Current.AutomationId -eq $aid) { return $win }
        $e = $win.FindFirst($TS::Descendants, (New-Object Windows.Automation.PropertyCondition($AE::AutomationIdProperty, $aid)))
        if ($e) { return $e }
      }
      $null
    }
    # click one of our bar's controls: it must be visible and on top at its centre (our process owns the point)
    function ClickOurs([string]$aid) {
      $e = KbEl $aid; if (-not $e) { throw "no element '$aid' in our windows" }
      $r = $e.Current.BoundingRectangle; $x = [int]($r.X + $r.Width / 2); $y = [int]($r.Y + $r.Height / 2)
      $root = [WH]::RootAt($x, $y)
      if ([WH]::PidOf($root) -ne [uint32]$script:p.Id) { throw "SAFETY: '$aid' at $x,$y is covered (root $root, pid $([WH]::PidOf($root))) - click aborted" }
      Guard   # our bar never takes the focus: Word must still be in front
      [void][WH]::Click($x, $y); Start-Sleep -Milliseconds 400
      "clicked $aid at $x,$y"
    }
    # a key only while our reading bar is shown (else Space would replace the selection in the document)
    function ReadKey([int]$vk) {
      if (-not (KbEl 'ReadPlay')) { throw "the reading bar is not shown - key 0x$('{0:X2}' -f $vk) not pressed" }
      Key $vk
    }
    # highlighted word = spoken word, event -> highlight delay, wall / audio clock per utterance (from the progress lines)
    function SyncStats {
      $l = @(Lines "reading progress id=(\d+) pos=\d+ len=\d+ audio=(\d+) ms wall=(\d+) ms engine='(.*)' tile=-?\d+ '(.*)' highlight \+([0-9.]+) ms$")
      if ($l.Count -eq 0) { return @{ N = 0; Ok = 0; Text = 'no progress lines' } }
      $n = 0; $ok = 0; $lags = New-Object System.Collections.ArrayList; $first = @{}; $last = @{}
      foreach ($x in $l) {
        if ($x -notmatch "reading progress id=(\d+) pos=\d+ len=\d+ audio=(\d+) ms wall=(\d+) ms engine='(.*)' tile=-?\d+ '(.*)' highlight \+([0-9.]+) ms$") { continue }
        $id = $matches[1]; $au = [double]$matches[2]; $wa = [double]$matches[3]
        $eng = ($matches[4] -replace '[^\p{L}\p{N}]', '').ToLowerInvariant(); $til = ($matches[5] -replace '[^\p{L}\p{N}]', '').ToLowerInvariant()
        $n++
        if ($til -ne '' -and ($til -eq $eng -or $til.Contains($eng) -or $eng.Contains($til))) { $ok++ }
        [void]$lags.Add([double]$matches[6])
        if (-not $first.ContainsKey($id)) { $first[$id] = @($au, $wa) }; $last[$id] = @($au, $wa)
      }
      $sorted = @($lags | Sort-Object)
      $ratios = @(foreach ($k in $first.Keys) { $da = $last[$k][0] - $first[$k][0]; if ($da -gt 300) { [math]::Round(($last[$k][1] - $first[$k][1]) / $da, 3) } })
      @{ N = $n; Ok = $ok; Text = "events=$n highlighted=spoken $ok/$n, event->highlight median $($sorted[[int]($sorted.Count / 2)]) ms max $($sorted[-1]) ms, wall/audio [$($ratios -join ', ')]" }
    }
    $P50 = 'Le petit chat mange une pomme dans la cuisine avec sa maman. Il fait beau aujourd' + [char]0x2019 + 'hui et les enfants jouent dans le jardin. Le chien dort devant la porte de la maison. Nous allons manger une tarte aux fraises ce soir avec nos amis du village.'
    $P3 = "Le chat dort sur le lit. Il fait beau dehors.`rLes oiseaux chantent dans le grand arbre."

    Scenario 'WS1' {   # S3: drag-select a ~50-word paragraph -> the reading bar, <= 250 ms after the button-up
      FrenchDoc7 ($P50 + "`rFin."); Reset7; Mark
      $dr = DragIdx 0 $P50.Length
      [void](WaitLine 'reading shown' 4000); Start-Sleep -Milliseconds 500
      $s = LastShown
      $ms = if ($s -match ' (\d+) ms after the selection gesture') { [int]$matches[1] } else { -1 }
      Pass 'WS1' ($s -match 'src=uia' -and $s -match '(4\d|5\d) words' -and $ms -ge 0 -and $ms -le 250) "$dr | $s"
    }
    Scenario 'WS2' {   # S4: double click on a word -> one tile
      FrenchDoc7 $P3; Reset7; Mark
      $dc = DblIdx 4
      [void](WaitLine 'reading shown' 3000); Start-Sleep -Milliseconds 300
      $s = LastShown
      Pass 'WS2' ($s -match ' 1 words, ') "$dc | $s"
    }
    Scenario 'WS3' {   # S5: Shift+Right x 8 -> one selection read after the keys stop
      FrenchDoc7 $P3; Reset7; Mark
      for ($k = 0; $k -lt 8; $k++) { Guard; [void][WH]::ShiftArrow(0x27); Start-Sleep -Milliseconds 60 }
      [void](WaitLine 'reading shown' 3000); Start-Sleep -Milliseconds 300
      $reads = @(Lines 'selection read shift').Count
      $s = LastShown
      Pass 'WS3' ($s -match ' 2 words, ' -and $reads -le 2) "selection reads=$reads (debounced) | $s"
    }
    Scenario 'WS4' {   # S6: Ctrl+A on a long document -> 1000 words, the message, lazy tiles
      $long = (@(1..110 | ForEach-Object { "Le petit chat mange une pomme rouge dans la grande cuisine $_." }) -join ' ')
      FrenchDoc7 $long; Reset7; Mark
      Key 0x41 -Ctrl
      [void](WaitLine 'reading view bar: \d+ tiles' 8000); Start-Sleep -Milliseconds 500
      $s = LastShown; $v = @(Lines 'reading view bar') -join ' | '
      $msg = KbEl 'ReadTooLong'
      $ok = $s -match '1000 words \(truncated at 1000\)' -and $msg -and -not $msg.Current.IsOffscreen -and $v -match 'max batch ([0-9.]+) ms' -and [double]$matches[1] -le 50
      Pass 'WS4' $ok "$s | $v | message shown=$([bool]$msg)"
    }
    Scenario 'WS5' {   # S7: Space = play, F9 = pause (never typed into the document), resume, then to the end
      FrenchDoc7 $P3; Reset7; Mark
      Key 0x41 -Ctrl
      if (-not (WaitLine 'reading shown' 3000)) { throw 'the reading bar did not appear' }
      Start-Sleep -Milliseconds 400
      $t0 = Text
      ReadKey 0x20; Start-Sleep -Milliseconds 2200
      ReadKey 0x78; Start-Sleep -Milliseconds 700
      $paused = @(Lines 'play/pause \(F9\) -> Paused').Count
      ReadKey 0x20
      # the end of the reading: a « state Ready » logged AFTER the resume (the one logged when the bar appeared does not count)
      $resumeAt = (LogLines).Count
      $end = (Get-Date).AddSeconds(20)
      while ((Get-Date) -lt $end -and -not @(LogLines | Select-Object -Skip $resumeAt | Where-Object { $_ -match 'reading: state Ready' }).Count) { Start-Sleep -Milliseconds 200 }
      Start-Sleep -Milliseconds 500
      $t1 = Text
      $utt = @(Lines "speak Reading '")
      $sy = SyncStats
      $ok = $paused -eq 1 -and $t1 -ceq $t0 -and $utt.Count -ge 3 -and $sy.N -gt 0 -and $sy.Ok * 100 -ge $sy.N * 95
      Pass 'WS5' $ok "document unchanged=$($t1 -ceq $t0) paused by F9=$paused utterances=$($utt.Count) | $($sy.Text)"
    }
    Scenario 'WS6' {   # S8: click a tile -> that word is said; the next Play starts at its sentence
      FrenchDoc7 $P3; Reset7; Mark
      Key 0x41 -Ctrl
      if (-not (WaitLine 'reading shown' 3000)) { throw 'the reading bar did not appear' }
      Start-Sleep -Milliseconds 400
      $c = ClickOurs 'Tile9'                                  # « beau » (sentence 2)
      Start-Sleep -Milliseconds 900
      $word = @(Lines "speak Manual '")
      ReadKey 0x20; Start-Sleep -Milliseconds 1200
      $next = [string](@(Lines "speak Reading '") | Select-Object -Last 1)
      ReadKey 0x20
      Pass 'WS6' ($word.Count -eq 1 -and $word[0] -match "'beau'" -and $next -match "'Il fait beau dehors\.'") "$c | word [$($word -join ' | ')] | next play [$next]"
    }
    Scenario 'WS7' {   # S10: Esc (not shown again for the same selection), a click (selection gone), typing
      FrenchDoc7 $P3; Reset7; Mark
      Key 0x41 -Ctrl; [void](WaitLine 'reading shown' 3000); Start-Sleep -Milliseconds 300
      Key 0x1B; Start-Sleep -Milliseconds 700
      Key 0x41 -Ctrl; Start-Sleep -Milliseconds 1200                                   # same selection: dismissed
      $a = @(Lines 'reading shown').Count -eq 1 -and @(Lines 'reading: exit \(Esc\)').Count -eq 1 -and @(Lines 'dismissed selection').Count -ge 1
      ClickIdx 8 | Out-Null; Start-Sleep -Milliseconds 900                             # selection gone
      $dc = DblIdx 4; [void](WaitLine 'reading shown: 1 words' 3000); Start-Sleep -Milliseconds 300
      TypeText 'x'; Start-Sleep -Milliseconds 900
      $b = @(Lines 'reading: exit \(typing\)').Count -eq 1 -and -not (KbEl 'ReadPlay')
      Pass 'WS7' ($a -and $b) "Esc + dismissed=$a typing exit=$b text [$((Text) -split "`r" | Select-Object -First 1)] | $(@(Lines 'reading: (exit|state)|dismissed|reading shown') -join ' / ')"
    }
    Scenario 'WS8' {   # S9: « grande fenêtre »: opens, follows the reading, Esc closes it, Word gets the focus back
      FrenchDoc7 ($P50 + "`rFin."); Reset7; Mark
      Key 0x41 -Ctrl
      if (-not (WaitLine 'reading shown' 3000)) { throw 'the reading bar did not appear' }
      Start-Sleep -Milliseconds 400
      $c = ClickOurs 'ReadLarge'; Start-Sleep -Milliseconds 1500
      $lw = KbEl 'ReadingWindow'
      $opened = [bool]$lw
      $fgOurs = [WH]::PidOf([WH]::GetForegroundWindow()) -eq [uint32]$script:p.Id
      if ($opened -and $fgOurs) {
        [void][WH]::Key(0x20, $false); Start-Sleep -Milliseconds 2000                  # Space in our window: play
        [void][WH]::Key(0x78, $false); Start-Sleep -Milliseconds 500                   # F9: pause
        [void][WH]::Key(0x1B, $false); Start-Sleep -Milliseconds 900                   # Esc: close
      }
      $closed = -not (KbEl 'ReadingWindow')
      if (-not $closed -and $lw) { try { $lw.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close() } catch { } }
      [void][WH]::Activate($script:wordHwnd); Start-Sleep -Milliseconds 800; Guard
      $played = @(Lines 'play/pause \(large window key\) -> Playing').Count
      Pass 'WS8' ($opened -and $fgOurs -and $closed -and $played -ge 1) "$c | opened=$opened in front=$fgOurs played=$played closed by Esc=$closed | $(@(Lines 'reading: (large|play)') -join ' / ')"
    }
    Scenario 'WS9' {   # D26: words cut at both ends are completed, the sentence end « ! » is added (on the last tile); the
                       # selection in Word is not changed
      $t9 = 'Il fait beau. Le chat dort bien ! Fin.'
      FrenchDoc7 $t9; Reset7; Mark
      $a = $t9.IndexOf('chat') + 2; $b = $t9.IndexOf('bien') + 2      # « at dort bi »
      $dr = DragIdx $a $b
      $sel0 = "$($script:w.Selection.Start),$($script:w.Selection.End)"
      [void](WaitLine 'reading shown' 3000); Start-Sleep -Milliseconds 400
      $s = LastShown
      $tile = KbEl 'Tile2'
      $tileName = if ($tile) { $tile.Current.Name } else { '' }
      ReadKey 0x20; [void](WaitLine "speak Reading '" 3000); Start-Sleep -Milliseconds 2500
      $said = [string](@(Lines "speak Reading '") | Select-Object -Last 1)
      ReadKey 0x20
      $sel1 = "$($script:w.Selection.Start),$($script:w.Selection.End)"
      # Word may put its own no-break space before « ! » (AutoCorrect on Content.Text: usually not): any space-like char
      $okSaid = ($said -replace [char]0x00A0, ' ' -replace [char]0x202F, ' ') -match "'chat dort bien !'"
      $okTile = ($tileName -replace [char]0x00A0, ' ' -replace [char]0x202F, ' ') -eq 'bien !'
      Pass 'WS9' ($s -match ' 3 words, 3 tiles' -and $okSaid -and $okTile -and $sel0 -eq $sel1) "$dr | last tile [$tileName] said [$said] selection before/after $sel0 / $sel1 | $s | $(@(Lines 'completed \(D26\)') -join ' / ')"
    }
  }

  if ($Suite -in 'plan04', 'all') {
    # Each scenario starts after 'Il dit ' (mid-sentence: Word's own capitalisation does not interfere).
    # Adaptive Word rule: in a French paragraph Word's AutoCorrect (option on by default) adds U+00A0 before ; : ! ?
    # and we add nothing; where Word adds none (English paragraph, or the option off) we add our U+202F.
    $nnbsp = [string][char]0x202F
    $nbsp = [string][char]0x00A0
    function Checks { Since | Where-Object { $_ -match 'auto-space|punct check' } }
    Scenario 'WP2' {
      AtEndOf 'Il dit '; TypeText 'la mai'; Start-Sleep -Milliseconds 700; PickWord 'maison' | Out-Null
      TypeText ','; CheckText 'WP2' 'Il dit la maison,' (Since | Where-Object { $_ -match 'auto-space' })
    }
    Scenario 'WP4' {
      AtEndOf 'Il dit '; TypeText 'bonj'; Start-Sleep -Milliseconds 700; PickWord 'bonjour' | Out-Null
      TypeText '!'; CheckText 'WP4' ('Il dit bonjour' + $nbsp + '!') (Checks)
    }
    Scenario 'WP4b' {
      $hkl = [int64][WH]::GetKeyboardLayout(0) -band 0xFFFF
      if ($hkl -ne 0x040C -and $hkl -ne 0x080C) { Pass 'WP4b' $false "skipped: keyboard layout 0x$('{0:X4}' -f $hkl) is not AZERTY (fr-FR / fr-BE)"; return }
      AtEndOf 'Il dit '; TypeText 'bonj'; Start-Sleep -Milliseconds 700; PickWord 'bonjour' | Out-Null
      Guard; [WH]::ShiftKey(0xBC) | Out-Null; Start-Sleep -Milliseconds 300   # real AZERTY key: Shift + , = ?
      CheckText 'WP4b' ('Il dit bonjour' + $nbsp + '?') (Checks)
    }
    Scenario 'WP5' {
      AtEndOf 'Il dit '; TypeText 'la mai'; Start-Sleep -Milliseconds 700; PickWord 'maison' | Out-Null
      TypeText ' '; CheckText 'WP5a' 'Il dit la maison ' (Since | Where-Object { $_ -match 'auto-space' })
      TypeText ','; CheckText 'WP5b' 'Il dit la maison,'
    }
    # English paragraph (proofing language set on THIS test document only, via COM; no Word option touched):
    # Word's French AutoCorrect does not apply -> the read-back finds no space -> we insert U+202F.
    # (first run: the bar offered 'Bonjour' with ctx=[] start, i.e. the click read saw no word before the caret; the
    # click read and the caret are now recorded, and the expected text uses the word as actually inserted)
    Scenario 'WP4en' {
      Prefill 'he said '; $script:d.Content.LanguageID = 1033   # wdEnglishUS, lower case: mid-sentence
      Mark; $at = ClickIdx 8; Start-Sleep -Milliseconds 400
      $rd = ReadLine 'click'; $txt0 = Text
      Mark
      TypeText 'bonj'; Start-Sleep -Milliseconds 700; $wd = PickWord 'bonjour'
      TypeText '!'; CheckText 'WP4en' ('he said ' + $wd + $nnbsp + '!') ("lang=$($script:d.Content.LanguageID) caret=$at before-typing=[$txt0] read: $rd | " + (Checks))
    }
    # back to French in the same window: this window is now remembered as "adds no space", so we put ours at once,
    # Word adds its own too -> the read-back removes one: exactly one space must remain
    Scenario 'WP4fr2' {
      Prefill 'Il dit '; $script:d.Content.LanguageID = 1036   # wdFrench
      ClickIdx 7 | Out-Null; Start-Sleep -Milliseconds 400; Mark
      TypeText 'bonj'; Start-Sleep -Milliseconds 700; $wd = PickWord 'bonjour'
      TypeText '!'; CheckOneSpaceBefore 'WP4fr2' ('Il dit ' + $wd) '!' (Checks)
    }
    # phase 2: setting "none" (written before a restart, as an administrator / the settings window would)
    if ($Only.Count -eq 0 -or $Only -contains 'WP8') { StopKb; WriteSettings 'none'; StartKb }
    Scenario 'WP8' {
      AtEndOf 'Il dit '; TypeText 'bonj'; Start-Sleep -Milliseconds 700; PickWord 'bonjour' | Out-Null
      # « Aucune » removes only OUR space; Word's own AutoCorrect still adds U+00A0 (a Word option we never change)
      TypeText '!'; CheckText 'WP8' ('Il dit bonjour' + $nbsp + '!') ("(U+00A0 = Word's AutoCorrect, not us) " + (Checks))
    }
  }
}
catch { Note "SETUP/RUN ERROR: $($_.Exception.Message)" }
finally {
  if ($p) {
    if (-not $p.HasExited) { $q = Start-Process $Exe -ArgumentList '--quit' -PassThru; [void]$q.WaitForExit(10000) }
    if (-not $p.WaitForExit(5000)) { Note "$KbName did not exit on --quit (exit-after 300 s will stop it)" }
  }
  if ($script:settingsTouched) {
    try {
      $orig = Join-Path $OutDir 'settings.json.orig'
      if ($script:settingsHad) { Copy-Item $orig $SettingsPath -Force; Note "settings.json restored" }
      else { Remove-Item $SettingsPath -Force -EA SilentlyContinue; Note "settings.json removed (there was none before)" }
    } catch { Note "RESTORE settings.json FAILED: $_ (original copy: $orig)" }
  }
  if ($d) { try { $d.Saved = $true; $d.Close([ref]0) } catch { Note "close doc: $_" } }
  if ($w -and $createdWord) { try { $w.Quit([ref]0) } catch { Note "quit Word: $_" } }
  foreach ($o in @($d, $w)) { if ($o) { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($o) } }
  $all = LogLines | Select-Object -Skip $startMark
  $all | Set-Content (Join-Path $OutDir 'predict-keyboard.log.txt') -Encoding UTF8
  $reads = $all | Where-Object { $_ -match ' read \w+ src=\w+ status=\w+ (\d+) ms' } | ForEach-Object { [int]($_ -replace '.* status=\w+ (\d+) ms.*', '$1') }
  $summary = @("Word host test $(Get-Date -Format o)", "exe: $Exe") + ($results.GetEnumerator() | ForEach-Object { "$($_.Key): $($_.Value)" }) +
             @("read ms: $($reads -join ', ')", "$KbName exited: $(if ($p) { $p.HasExited } else { 'n/a' })") + $notes
  if ($script:ranScenarios -eq 0) { $summary += 'FAIL - no scenario ran (check -Suite / -Only)' }
  $summary | Set-Content (Join-Path $OutDir 'summary.txt') -Encoding UTF8
  $summary
}
