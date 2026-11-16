<#
.SYNOPSIS
  SAPI 5 baseline (Microsoft Hortense Desktop, System.Speech as in Predict.Keyboard): WAVs, latency, SAPI phonemes
  and word timings, in the same layout as the Python engine scripts (plan 05, D25).
.DESCRIPTION
  Output is 16 kHz PCM, Hortense's native rate: System.Speech computes event AudioPositions in the voice's native
  format, so any other output rate would scale every word / phoneme time (x1.378 at 22.05 kHz, measured).
  first_audio_ms = time from SpeakAsync to the first PCM bytes the voice writes to the output stream (SAPI streams,
  so this is the real start-of-audio latency); word timings come from SpeakProgress (AudioPosition), phonemes from
  PhonemeReached (SAPI French phone ids mapped to IPA). -Profile 2core pins the process to logical CPUs 0 and 2, like common.py.
.EXAMPLE
  powershell -NoProfile -File synth_sapi.ps1 -Root <bulk-data folder> -Profile normal
#>
param(
    [string]$Root = $env:VOICE_LAB_ROOT,
    [string]$Sentences = (Join-Path $PSScriptRoot '..\..\development\voice-test\sentences.txt'),
    [ValidateSet('normal', '2core')][string]$Profile = 'normal',
    [int]$Reps = 3,
    [int]$WordReps = 10,
    [string]$Voice = 'Microsoft Hortense Desktop',
    [string]$Only = '',
    # Hortense's native format. System.Speech reports SpeakProgress / PhonemeReached AudioPosition in the voice's
    # native format: with another output rate the positions are scaled by native/output (x1.378 at 22.05 kHz).
    [int]$SampleRate = 16000
)
$ErrorActionPreference = 'Stop'
if (-not $Root) { throw 'Give -Root <bulk-data folder> or set VOICE_LAB_ROOT.' }
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
if ($Profile -eq '2core') { [System.Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity = [IntPtr]5 }

Add-Type -AssemblyName System.Speech
Add-Type -ReferencedAssemblies System.Speech -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Threading;

public sealed class TimedStream : Stream
{
    public readonly MemoryStream Data = new MemoryStream();
    public long FirstTicks = -1;
    public override void Write(byte[] buffer, int offset, int count)
    {
        if (count > 0 && FirstTicks < 0) FirstTicks = Stopwatch.GetTimestamp();
        Data.Write(buffer, offset, count);
    }
    public override bool CanRead { get { return false; } }
    public override bool CanSeek { get { return false; } }
    public override bool CanWrite { get { return true; } }
    public override long Length { get { return Data.Length; } }
    public override long Position { get { return Data.Length; } set { } }
    public override void Flush() { }
    public override int Read(byte[] b, int o, int c) { throw new NotSupportedException(); }
    public override long Seek(long o, SeekOrigin s) { throw new NotSupportedException(); }
    public override void SetLength(long v) { }
}

public sealed class SapiLab
{
    public static int Rate = 16000;
    private readonly SpeechSynthesizer _s = new SpeechSynthesizer();
    private readonly ManualResetEvent _done = new ManualResetEvent(false);
    public readonly List<string> Words = new List<string>();   // "charPos|len|startMs"
    public readonly List<string> Phonemes = new List<string>(); // "phoneme|startMs|durMs"

    // SAPI French phone set (LCID 1036) id -> IPA. Ids and SAPI symbols read with SAPI.SpPhoneConverter.IdToPhone:
    // 1 '-' 2 '!' 3 '&' 4 ',' 5 '.' 6 '?' 7 '_' 8 '1' (stress) 9 '~' (nasal) 10 aa 11 a 12 oh 13 ax 14 b 15 d 16 eh
    // 17 ey 18 f 19 g 20 hy 21 uy 22 iy 23 k 24 l 25 m 26 n 27 ng 28 nj 29 oe 30 eu 31 ow 32 p 33 r 34 s 35 sh
    // 36 t 37 uw 38 v 39 w 40 y 41 z 42 zh.
    private static readonly string[] Ipa = { "", "", "", "", ",", ".", "?", " ", "\u02c8", "\u0303",
        "\u0251", "a", "\u0254", "\u0259", "b", "d", "\u025b", "e", "f", "\u0261", "\u0265", "y", "i", "k", "l", "m", "n",
        "\u014b", "\u0272", "\u0153", "\u00f8", "o", "p", "\u0281", "s", "\u0283", "t", "u", "v", "w", "j", "z", "\u0292" };
    private static readonly IFormatProvider Inv = System.Globalization.CultureInfo.InvariantCulture;

    private static string ToIpa(string sapi)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in sapi) sb.Append(c < Ipa.Length ? Ipa[c] : "?");
        return sb.ToString();
    }

    public SapiLab(string voice)
    {
        _s.SelectVoice(voice);
        _s.SpeakCompleted += (o, e) => _done.Set();
        _s.SpeakProgress += (o, e) => { lock (Words) Words.Add(e.CharacterPosition + "|" + e.CharacterCount + "|" + e.AudioPosition.TotalMilliseconds.ToString(Inv)); };
        _s.PhonemeReached += (o, e) => { lock (Phonemes) Phonemes.Add(ToIpa(e.Phoneme) + "|" + e.AudioPosition.TotalMilliseconds.ToString(Inv) + "|" + e.Duration.TotalMilliseconds.ToString(Inv)); };
    }

    /// <summary>Speaks one text; returns { firstMs, totalMs } and the PCM bytes (16-bit mono, at Rate: 16 kHz by default).</summary>
    public double[] Speak(string text, out byte[] pcm)
    {
        lock (Words) Words.Clear();
        lock (Phonemes) Phonemes.Clear();
        var st = new TimedStream();
        _s.SetOutputToAudioStream(st, new SpeechAudioFormatInfo(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        _done.Reset();
        long t0 = Stopwatch.GetTimestamp();
        _s.SpeakAsync(text);
        _done.WaitOne();
        long t1 = Stopwatch.GetTimestamp();
        _s.SetOutputToNull();
        pcm = st.Data.ToArray();
        double f = 1000.0 / Stopwatch.Frequency;
        return new double[] { st.FirstTicks < 0 ? double.NaN : (st.FirstTicks - t0) * f, (t1 - t0) * f };
    }

    public static void WriteWav(string path, byte[] pcm)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write(36 + pcm.Length);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write(pcm.Length); w.Write(pcm);
        }
    }
}
'@

[SapiLab]::Rate = $SampleRate
$proc = [System.Diagnostics.Process]::GetCurrentProcess()
$engine = 'sapi-hortense'
$out = Join-Path $Root "out\$Profile\$engine"
New-Item -ItemType Directory -Force $out | Out-Null
$items = Get-Content -Encoding UTF8 $Sentences | Where-Object { $_ -and -not $_.StartsWith('#') } | ForEach-Object {
    $p = $_ -split "`t"; [pscustomobject]@{ id = $p[0]; category = $p[1]; text = $p[2] } }
if ($Only) { $keep = $Only -split ','; $items = $items | Where-Object { $keep -contains $_.id } }

$proc.Refresh(); $rssStart = $proc.WorkingSet64
$sw = [System.Diagnostics.Stopwatch]::StartNew()
$lab = New-Object SapiLab $Voice
$pcm = $null; [void]$lab.Speak('Bonjour.', [ref]$pcm)   # load completes on first use: measured as load
$loadMs = $sw.Elapsed.TotalMilliseconds
$proc.Refresh(); $rssLoaded = $proc.WorkingSet64
[void]$lab.Speak('Bonjour.', [ref]$pcm)   # warm-up

$rows = New-Object System.Collections.Generic.List[object]
$phon = [ordered]@{}; $tim = [ordered]@{}
foreach ($it in $items) {
    $n = if ($it.category -eq 'word') { $WordReps } else { $Reps }
    for ($rep = 0; $rep -lt $n; $rep++) {
        $r = $lab.Speak($it.text, [ref]$pcm)
        $audioS = $pcm.Length / 2.0 / [SapiLab]::Rate
        $rows.Add([pscustomobject]@{ engine = $engine; profile = $Profile; id = $it.id; category = $it.category; rep = $rep
                load_ms = [math]::Round($loadMs, 1); first_audio_ms = [math]::Round($r[0], 1); total_ms = [math]::Round($r[1], 1)
                audio_s = [math]::Round($audioS, 3); rtf = [math]::Round($r[1] / 1000 / $audioS, 4) })
        if ($rep -gt 0) { continue }
        [SapiLab]::WriteWav((Join-Path $out "$($it.id).wav"), $pcm)
        $phon[$it.id] = (($lab.Phonemes | ForEach-Object { ($_ -split '\|')[0] }) -join '')
        $words = foreach ($w in $lab.Words) {
            $p = $w -split '\|'; $pos = [int]$p[0]; $len = [int]$p[1]
            [ordered]@{ text = $it.text.Substring($pos, [math]::Min($len, $it.text.Length - $pos)); char = $pos; start = [math]::Round([double]$p[2] / 1000, 3) }
        }
        $phs = foreach ($x in $lab.Phonemes) { $p = $x -split '\|'; ,@($p[0], [math]::Round([double]$p[1] / 1000, 3), [math]::Round(([double]$p[1] + [double]$p[2]) / 1000, 3)) }
        $tim[$it.id] = [ordered]@{ words = @($words); phonemes = @($phs) }
    }
}
$rows | Export-Csv -NoTypeInformation -Encoding UTF8 (Join-Path $out 'latency.csv')
$utf8 = New-Object System.Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $out 'phonemes.json'), ($phon | ConvertTo-Json -Depth 4), $utf8)
[IO.File]::WriteAllText((Join-Path $out 'timings.json'), ($tim | ConvertTo-Json -Depth 6), $utf8)
$proc.Refresh()
# Voice data of Hortense (registry VoicePath / LangDataPath of TTS_MS_FR-FR_HORTENSE_11.0): M1036Hortense.* + MSTTSLocFrFR.dat
$engineDir = Join-Path $env:windir 'Speech_OneCore\Engines\TTS\fr-FR'
$voiceFiles = @(Get-ChildItem -File $engineDir -ErrorAction SilentlyContinue | Where-Object { $_.Name -like 'M1036Hortense*' -or $_.Name -eq 'MSTTSLocFrFR.dat' })
$diskMb = if ($voiceFiles.Count) { [math]::Round((($voiceFiles | Measure-Object Length -Sum).Sum) / 1MB, 1) } else { $null }
$run = [ordered]@{ rss_start_mb = [math]::Round($rssStart / 1MB, 1); rss_loaded_mb = [math]::Round($rssLoaded / 1MB, 1)
    rss_end_mb = [math]::Round($proc.WorkingSet64 / 1MB, 1); peak_wset_mb = [math]::Round($proc.PeakWorkingSet64 / 1MB, 1)
    load_ms = $loadMs; sample_rate = [SapiLab]::Rate; threads = 'SAPI'; model = $Voice; model_mb = $diskMb; engine_dir = $engineDir }
[IO.File]::WriteAllText((Join-Path $out 'run.json'), ($run | ConvertTo-Json), $utf8)
$w = @($rows | Where-Object category -eq 'word' | ForEach-Object first_audio_ms | Sort-Object)
$s = @($rows | Where-Object category -ne 'word' | ForEach-Object first_audio_ms | Sort-Object)
$p50 = { param($v) if ($v.Count) { $v[[int][math]::Floor(($v.Count - 1) / 2)] } else { 'n/a' } }
"$engine $Profile : word first p50=$(& $p50 $w) ; sentence first p50=$(& $p50 $s) ; load=$([math]::Round($loadMs)) ms"
