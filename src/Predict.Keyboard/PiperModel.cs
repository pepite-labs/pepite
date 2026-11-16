using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace Predict.Keyboard;

/// <summary>
/// One Piper voice model in ONNX Runtime (plan 08a, step 5; A8): CPU, fp32, 2 intra-op threads, 1 inter-op, sequential,
/// no spinning (an idle app must not burn a core), telemetry off. Inputs <c>input</c> int64[1,N], <c>input_lengths</c>
/// int64[1], <c>scales</c> float[3] = noise_scale, length_scale, noise_w; outputs <c>output</c> (audio) and
/// <c>/Ceil_output_0</c> (the alignment the patched model exposes: frames per id, × hop length = samples). The audio is
/// peak-normalised and clipped as Piper does by default (<c>normalize_audio=True</c>). Plan 08b: a multi-speaker model
/// (upmc) gets <c>sid</c> int64[1] as piper 1.8.0 does (<c>voice.py</c> 535-544), always the pack's own speaker.
/// </summary>
internal sealed class PiperModel : IPiperModel
{
    public const string AlignmentOutput = "/Ceil_output_0";
    private static bool _telemetryOff;

    /// <summary>Bench A/B only (<c>--ort-spinning on</c>): let ORT's worker threads spin between runs. The app keeps it
    /// off (A8): spinning threads burn CPU while the voice is idle.</summary>
#pragma warning disable CS0649   // set by SpeechBench, which the test project does not compile
    internal static bool AllowSpinning;
#pragma warning restore CS0649
    private readonly InferenceSession _session;
    private readonly PiperVoiceConfig _cfg;
    private readonly long? _sid;

    /// <param name="sid">the speaker id (<see cref="PiperVoicePack.SpeakerId"/>): required when the model has more than one
    /// speaker, null otherwise.</param>
    /// <param name="arena">ORT's CPU memory arena (plan 08b): off for a voice loaded for a preview only, so its memory
    /// goes back to the system when it is unloaded (switch gate).</param>
    public PiperModel(string modelPath, PiperVoiceConfig cfg, long? sid = null, bool arena = true, int threads = 2)
    {
        _cfg = cfg;
        _sid = sid;
        if (cfg.NumSpeakers > 1 && sid is null) throw new FormatException("multi-speaker model: no speaker id");
        if (cfg.NumSpeakers <= 1 && sid is not null) throw new FormatException("speaker id on a single-speaker model");
        if (!_telemetryOff) { OrtEnv.Instance().DisableTelemetryEvents(); _telemetryOff = true; }
        using var so = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            // every sentence has another length: a memory pattern is never reused, and the CPU arena keeps the largest
            // sentence's buffers for the whole session (peak working set gate, plan 08a)
            EnableMemoryPattern = false,
            EnableCpuMemArena = arena,
        };
        so.AddSessionConfigEntry("session.intra_op.allow_spinning", AllowSpinning ? "1" : "0");
        so.AddSessionConfigEntry("session.inter_op.allow_spinning", AllowSpinning ? "1" : "0");
        _session = new InferenceSession(modelPath, so);
        if (!_session.OutputMetadata.ContainsKey(AlignmentOutput))
            throw new InvalidOperationException("the model has no alignment output (not patched)");
        if (sid is not null && !_session.InputMetadata.ContainsKey("sid"))
        {
            _session.Dispose();
            throw new FormatException("the model has no sid input");
        }
    }

    /// <summary>Synthesise one sentence. <paramref name="run"/> may be terminated from another thread (cancel): the call
    /// then throws <see cref="OnnxRuntimeException"/>.</summary>
    /// <returns>normalised audio and samples per id (same length as <paramref name="ids"/>).</returns>
    public (float[] Audio, long[] SamplesPerId) Run(long[] ids, float lengthScale, RunOptions run) =>
        Run(ids, _cfg.NoiseScale, lengthScale, _cfg.NoiseW, run);

    /// <summary>Same with explicit noise scales (bench <c>--variant</c>, plan 08b step 7).</summary>
    public (float[] Audio, long[] SamplesPerId) Run(long[] ids, float noiseScale, float lengthScale, float noiseW, RunOptions run)
    {
        using var input = OrtValue.CreateTensorValueFromMemory(ids, new long[] { 1, ids.Length });
        using var lengths = OrtValue.CreateTensorValueFromMemory(new long[] { ids.Length }, new long[] { 1 });
        using var scales = OrtValue.CreateTensorValueFromMemory(new[] { noiseScale, lengthScale, noiseW }, new long[] { 3 });
        var inputs = new Dictionary<string, OrtValue> { ["input"] = input, ["input_lengths"] = lengths, ["scales"] = scales };
        using var sid = _sid is { } id ? OrtValue.CreateTensorValueFromMemory(new[] { id }, new long[] { 1 }) : null;
        if (sid != null) inputs["sid"] = sid;
        using var outputs = _session.Run(run, inputs, new[] { "output", AlignmentOutput });
        var audio = outputs[0].GetTensorDataAsSpan<float>().ToArray();
        var ceil = outputs[1].GetTensorDataAsSpan<float>();
        var samples = new long[ceil.Length];
        for (int i = 0; i < ceil.Length; i++) samples[i] = (long)ceil[i] * _cfg.HopLength;
        Normalize(audio);
        return (audio, samples);
    }

    /// <summary>Piper's normalize_audio: divide by the peak (silence stays silence), then clip to [−1, 1].</summary>
    public static void Normalize(float[] a)
    {
        float max = 0;
        foreach (var x in a) max = Math.Max(max, Math.Abs(x));
        if (max < 1e-8f) { Array.Clear(a); return; }
        for (int i = 0; i < a.Length; i++) a[i] = Math.Clamp(a[i] / max, -1f, 1f);
    }

    public void Dispose() => _session.Dispose();
}

/// <summary>What <see cref="PiperSynth"/> needs from a voice model (a fake in the tests).</summary>
internal interface IPiperModel : IDisposable
{
    /// <returns>normalised audio and samples per id (same length as <paramref name="ids"/>).</returns>
    (float[] Audio, long[] SamplesPerId) Run(long[] ids, float noiseScale, float lengthScale, float noiseW, RunOptions run);
}
