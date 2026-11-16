<#
.SYNOPSIS
  Plan 05 voice lab (D20, D25): runs every step in order, one CPU-heavy process at a time.
.DESCRIPTION
  Steps (pick with -Steps): setup (venvs), fetch (models + SHA-256 check), patch (Piper alignment output),
  normal / 2core (synthesis + latency per engine and profile), transcribe (Whisper), analyse (tables), blind (page).
  Bulk data (venvs, models, WAVs, outputs) lives under -Root (default: the VOICE_LAB_ROOT environment variable),
  never in the repository.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File run_all.ps1 -Root D:\bulk\voice-lab
  powershell -NoProfile -ExecutionPolicy Bypass -File run_all.ps1 -Steps normal,2core -Engines piper
#>
param(
    [string]$Root = $env:VOICE_LAB_ROOT,
    [string[]]$Steps = @('setup', 'fetch', 'patch', 'normal', '2core', 'transcribe', 'analyse', 'blind'),
    [string[]]$Engines = @('sapi', 'piper', 'kokoro', 'melo'),
    [string]$Py312 = 'C:\Program Files\Python312\python.exe',
    [string]$Py310 = "$env:LOCALAPPDATA\Programs\Python\Python310\python.exe"
)
$ErrorActionPreference = 'Stop'
if (-not $Root) { throw 'Give -Root <bulk-data folder> or set VOICE_LAB_ROOT.' }
$Steps = $Steps -split ','       # "-File run_all.ps1 -Steps a,b" passes one string
$Engines = $Engines -split ','
$here = $PSScriptRoot
$venv = Join-Path $Root 'venvs'
function Py([string]$name) { Join-Path $venv "$name\Scripts\python.exe" }
function Run([string]$exe, [string[]]$argv) {
    Write-Host ">> $exe $($argv -join ' ')"
    $ErrorActionPreference = 'Continue'   # Windows PowerShell 5.1 turns native stderr (warnings) into errors
    & $exe @argv
    if ($LASTEXITCODE -ne 0) { throw "failed ($LASTEXITCODE): $exe $($argv -join ' ')" }
}
$env:PYTHONIOENCODING = 'utf-8'   # -I ignores PYTHON* variables; -X utf8 below does the same job
$env:HF_HOME = Join-Path $Root 'cache\hf'

if ($Steps -contains 'setup') {
    foreach ($v in @(@('piper', $Py312), @('kokoro', $Py312), @('whisper', $Py312), @('melo', $Py310))) {
        $name = $v[0]
        if (-not (Test-Path (Py $name))) { Run $v[1] @('-m', 'venv', (Join-Path $venv $name)) }
        Run (Py $name) @('-I', '-m', 'pip', 'install', '--upgrade', 'pip')
        if ($name -eq 'melo') {
            Run (Py $name) @('-I', '-m', 'pip', 'install', 'torch==2.5.1', 'torchaudio==2.5.1', '--index-url', 'https://download.pytorch.org/whl/cpu')
        }
        Run (Py $name) @('-I', '-m', 'pip', 'install', '-r', (Join-Path $here "requirements-$name.txt"))
    }
}
if ($Steps -contains 'fetch') { Run (Py 'piper') @('-I', (Join-Path $here 'fetch_models.py'), $Root) }
if ($Steps -contains 'patch') {
    $dst = Join-Path $Root 'models\piper-aligned'
    New-Item -ItemType Directory -Force $dst | Out-Null
    foreach ($v in 'siwis', 'tom', 'upmc') {
        Copy-Item (Join-Path $Root "models\piper\fr_FR-$v-medium.onnx.json") $dst -Force
        Run (Py 'piper') @('-I', '-m', 'piper.patch_voice_with_alignment', (Join-Path $Root "models\piper\fr_FR-$v-medium.onnx"),
            '--output', (Join-Path $dst "fr_FR-$v-medium.onnx"))
    }
}
foreach ($prof in 'normal', '2core') {
    if ($Steps -notcontains $prof) { continue }
    # Timed runs: sentences x 3 (2 on 2core) and words x 10; the engines slower than real time (Kokoro int8, MeloTTS)
    # get sentences x 1 and words x 5 so a pass stays under ~15 min.
    $sentReps = if ($prof -eq 'normal') { 3 } else { 2 }
    $common = @('--root', $Root, '--profile', $prof, '--reps', $sentReps, '--word-reps', 10)
    $slow = @('--root', $Root, '--profile', $prof, '--reps', 1, '--word-reps', 5)
    if ($Engines -contains 'sapi') {
        Run 'powershell' @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $here 'synth_sapi.ps1'), '-Root', $Root,
            '-Profile', $prof, '-Reps', $sentReps, '-WordReps', 10)
    }
    if ($Engines -contains 'piper') {
        foreach ($a in @(@('--voice', 'siwis'), @('--voice', 'tom'), @('--voice', 'upmc', '--speaker', 'jessica'), @('--voice', 'upmc', '--speaker', 'pierre'))) {
            Run (Py 'piper') (@('-X', 'utf8', '-I', (Join-Path $here 'synth_piper.py')) + $a + $common)
        }
    }
    if ($Engines -contains 'kokoro') {
        Run (Py 'kokoro') (@('-X', 'utf8', '-I', (Join-Path $here 'synth_kokoro.py'), '--variant', 'fp32') + $common)
        Run (Py 'kokoro') (@('-X', 'utf8', '-I', (Join-Path $here 'synth_kokoro.py'), '--variant', 'int8') + $slow)
    }
    if ($Engines -contains 'melo') { Run (Py 'melo') (@('-X', 'utf8', '-I', (Join-Path $here 'synth_melo.py')) + $slow) }
}
if ($Steps -contains 'transcribe') { Run (Py 'whisper') @('-X', 'utf8', '-I', (Join-Path $here 'transcribe.py'), '--root', $Root) }
if ($Steps -contains 'analyse') { Run (Py 'whisper') @('-X', 'utf8', '-I', (Join-Path $here 'analyse.py'), '--root', $Root) }
if ($Steps -contains 'blind') { Run (Py 'whisper') @('-X', 'utf8', '-I', (Join-Path $here 'make_blind_page.py'), '--root', $Root) }
