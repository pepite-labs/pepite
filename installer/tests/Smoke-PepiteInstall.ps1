<#
.SYNOPSIS
  Smoke test of a built installer (plan 13, A4: the "smoke" job of .github/workflows/app.yml), on a DISPOSABLE GitHub
  Actions runner only: it installs Pepite per user, writes the Run key and uninstalls. It refuses to run elsewhere
  (never on a development PC: there the test VM and Test-PepiteInstall.ps1 are the way).

.DESCRIPTION
  -Release: the folder of the build (the "installer" artifact): Pepite-<ver>-installation.exe, files.sha256,
  exemple-etablissement.json. Checks, one line each ("SMOKE PASS|FAIL <check> <detail>"), exit code 1 when one fails:
    1. silent per-user install (/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CURRENTUSER) exits 0;
    2. the installed files are exactly files.sha256 (same SHA-256; only Inno's unins000.* added);
    3. Pepite.exe --check-config exemple-etablissement.json -> 0, a broken JSON file -> 2;
    4. the HKCU uninstall key {999AA879-43C2-4C22-9785-691AA95A3455}_is1 exists;
    5. silent uninstall: the install folder and the uninstall key are gone;
    -SpeechBench (optional, before the uninstall): Pepite.exe --speech-bench <out> --audio null [--sentences <file>,
     e.g. development\voice-test\sentences.txt of the checkout], reported, never fatal.
  ASCII only (Windows PowerShell 5.1 and pwsh).
#>
param([Parameter(Mandatory)][string]$Release, [Parameter(Mandatory)][string]$Version, [string]$Out = '', [switch]$SpeechBench,
      [string]$Sentences = '')
$ErrorActionPreference = 'Stop'
# a GitHub-hosted runner image (ImageOS is set by the image itself, not by the workflow)
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or -not $env:ImageOS) {
  throw 'Smoke-PepiteInstall.ps1 runs only on a disposable GitHub-hosted runner (it installs Pepite and its Run key)'
}
if (-not $Out) { $Out = Join-Path $env:RUNNER_TEMP 'smoke' }
New-Item -ItemType Directory -Force $Out | Out-Null
$exe = Join-Path $Release "Pepite-$Version-installation.exe"
$app = Join-Path $env:LOCALAPPDATA 'Programs\Pepite-Prediction'
$ukey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{999AA879-43C2-4C22-9785-691AA95A3455}_is1'
$script:failed = 0
function Report([string]$check, [bool]$ok, [string]$detail) {
  Write-Host ("SMOKE {0} {1} {2}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $check, $detail)
  if (-not $ok) { $script:failed++ }
}
function Run([string]$file, [string[]]$argList) {
  (Start-Process -FilePath $file -ArgumentList $argList -Wait -PassThru -WindowStyle Hidden).ExitCode
}

# 1. silent per-user install
if (Test-Path $app) { throw "$app exists before the install: not a fresh runner" }
$code = Run $exe @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$Out\setup.log`"")
Report 'install' ($code -eq 0 -and (Test-Path (Join-Path $app 'Pepite.exe'))) "exit $code, $app"
if ($code -ne 0) { exit 1 }

# 2. installed files = files.sha256
$expected = @{}
foreach ($line in Get-Content (Join-Path $Release 'files.sha256')) {
  if ($line -match '^([0-9a-f]{64})  (.+)$') { $expected[$Matches[2]] = $Matches[1] }
}
$root = (Resolve-Path $app).Path.TrimEnd('\') + '\'
$found = @{}
Get-ChildItem $app -Recurse -File | ForEach-Object { $found[$_.FullName.Substring($root.Length)] = $_.FullName }
$missing = @($expected.Keys | Where-Object { -not $found.ContainsKey($_) })
$extra = @($found.Keys | Where-Object { -not $expected.ContainsKey($_) -and $_ -notmatch '^unins\d{3}\.(exe|dat|msg)$' })
$differ = @($expected.Keys | Where-Object { $found.ContainsKey($_) -and (Get-FileHash $found[$_] -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[$_] })
Report 'files' ($expected.Count -gt 0 -and -not $missing -and -not $extra -and -not $differ) `
  "$($expected.Count) expected, missing $($missing.Count), extra $($extra.Count), different $($differ.Count) $((@($missing) + @($extra) + @($differ) | Select-Object -First 5) -join ' ; ')"

# 3. --check-config: the example -> 0, broken JSON -> 2
$pepite = Join-Path $app 'Pepite.exe'
$code = Run $pepite @('--check-config', "`"$(Join-Path $Release 'exemple-etablissement.json')`"")
Report 'check-config-example' ($code -eq 0) "exit $code (expected 0)"
$bad = Join-Path $Out 'casse.json'
[IO.File]::WriteAllText($bad, '{ "schema": 1, ', (New-Object Text.UTF8Encoding $false))
$code = Run $pepite @('--check-config', "`"$bad`"")
Report 'check-config-broken' ($code -eq 2) "exit $code (expected 2)"

# 4. the uninstall entry
Report 'uninstall-key' (Test-Path $ukey) $ukey

# optional: the speech bench without an audio device (never fatal)
if ($SpeechBench) {
  try {
    $benchArgs = @('--speech-bench', "`"$Out\bench`"", '--audio', 'null')
    if ($Sentences) { $benchArgs += @('--sentences', "`"$((Resolve-Path $Sentences).Path)`"") }
    $code = Run $pepite $benchArgs
    Write-Host "SMOKE INFO speech-bench exit $code (not a gate; $Out\bench)"
  } catch { Write-Host "SMOKE INFO speech-bench did not run: $_" }
}

# 5. silent uninstall (the uninstaller relaunches itself from %TEMP%: wait for the folder and the key to go)
$code = Run (Join-Path $app 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$Out\uninstall.log`"")
$deadline = (Get-Date).AddSeconds(180)
while (((Test-Path $app) -or (Test-Path $ukey)) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
Report 'uninstall' ($code -eq 0 -and -not (Test-Path $app) -and -not (Test-Path $ukey)) `
  "exit $code, folder $(if (Test-Path $app) { 'still there' } else { 'gone' }), key $(if (Test-Path $ukey) { 'still there' } else { 'gone' })"

Write-Host "SMOKE $(if ($script:failed) { "FAILED ($script:failed check(s))" } else { 'OK' })"
exit $(if ($script:failed) { 1 } else { 0 })
