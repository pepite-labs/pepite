using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Predict.Keyboard;

/// <summary>
/// Where Piper's audio goes (plan 08a, step 6). The timeline is in <b>source samples</b> (mono, the voice's rate) and
/// never stops while the device is open: when nothing is queued it plays silence, so a sample index is also a time.
/// <see cref="Enqueue"/> says where a chunk lands on that timeline; <see cref="PlayedSourceSamples"/> is where the
/// device is (the audio clock the highlight follows).
/// </summary>
internal interface IAudioSink : IDisposable
{
    /// <summary>Open the device if needed (idempotent); throws when it cannot. Resets the timeline to 0 on a new open.</summary>
    void EnsureOpen();
    /// <summary>Close the device (after the idle delay, or after an error); the next <see cref="EnsureOpen"/> reopens.</summary>
    void Close();
    bool IsOpen { get; }
    /// <returns>timeline index of the chunk's first sample.</returns>
    long Enqueue(float[] samples, int count);
    /// <summary>Drop everything queued at once (cancel): only the device buffer (≤ 40 ms) still plays.</summary>
    void Flush();
    long PlayedSourceSamples { get; }
    long QueuedEndSample { get; }
    float Gain { get; set; }
    /// <summary>The device stopped or the default device changed (any thread); the reason has no text.</summary>
    event Action<string>? Error;
}

/// <summary>Mono float samples on a never-ending timeline: queued chunks, else silence (thread-safe).</summary>
internal sealed class PlaybackQueue : ISampleProvider
{
    private readonly object _lock = new();
    private readonly LinkedList<(float[] Buf, int Count)> _chunks = new();
    private int _offset;            // read position in the first chunk
    private long _read, _queuedEnd; // timeline: samples handed to the device chain; end of the queued audio
    private int _silentReads;       // reads in a row that carried no audio at all

    public PlaybackQueue(int rate) => WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(rate, 1);
    public WaveFormat WaveFormat { get; }

    public long QueuedEnd { get { lock (_lock) return Math.Max(_queuedEnd, _read); } }

    /// <summary>Nothing queued and the last <see cref="IdleReads"/> reads were pure silence (the resampler's filter
    /// has flushed the end of the last utterance): the device chain may output zeros without pulling through it.</summary>
    public bool IsIdle { get { lock (_lock) return _chunks.Count == 0 && _silentReads >= IdleReads; } }
    public const int IdleReads = 4;

    /// <summary>The timeline advanced by <paramref name="samples"/> of silence the chain did not pull (idle bypass).</summary>
    public void AdvanceSilence(long samples)
    {
        lock (_lock) { _read += samples; }
    }

    public long Enqueue(float[] samples, int count)
    {
        lock (_lock)
        {
            long start = Math.Max(_queuedEnd, _read);
            if (count > 0) { _chunks.AddLast((samples, count)); _silentReads = 0; }
            _queuedEnd = start + count;
            return start;
        }
    }

    public void Flush()
    {
        lock (_lock) { _chunks.Clear(); _offset = 0; _queuedEnd = _read; }
    }

    public int Read(float[] buffer, int offset, int count)
    {
        lock (_lock)
        {
            int done = 0;
            while (done < count && _chunks.First is { } node)
            {
                var (buf, n) = node.Value;
                int take = Math.Min(count - done, n - _offset);
                Array.Copy(buf, _offset, buffer, offset + done, take);
                done += take;
                _offset += take;
                if (_offset >= n) { _chunks.RemoveFirst(); _offset = 0; }
            }
            if (done < count) Array.Clear(buffer, offset + done, count - done);
            _silentReads = done == 0 ? Math.Min(_silentReads + 1, IdleReads) : 0;
            _read += count;
            return count;
        }
    }
}

/// <summary>
/// The device chain with an idle bypass: while the <see cref="PlaybackQueue"/> is idle the device gets zeros straight
/// away (no resampling, no channel copy of silence — an open device then costs almost no CPU, plan 08a CPU gate) and the
/// queue's timeline is advanced by the source samples those output frames stand for, so positions stay exact.
/// </summary>
internal sealed class IdleBypassProvider : ISampleProvider
{
    private readonly ISampleProvider _chain;
    private readonly PlaybackQueue _queue;
    private readonly double _sourcePerFrame;
    private double _frac;

    public IdleBypassProvider(ISampleProvider chain, PlaybackQueue queue)
    {
        _chain = chain;
        _queue = queue;
        _sourcePerFrame = (double)queue.WaveFormat.SampleRate / chain.WaveFormat.SampleRate;
    }

    public WaveFormat WaveFormat => _chain.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        if (!_queue.IsIdle) return _chain.Read(buffer, offset, count);
        Array.Clear(buffer, offset, count);
        double adv = count / _chain.WaveFormat.Channels * _sourcePerFrame + _frac;
        long whole = (long)adv;
        _frac = adv - whole;
        _queue.AdvanceSilence(whole);
        return count;
    }
}

/// <summary>Copies a mono provider to every channel of the device's mix format (more than two channels).</summary>
internal sealed class FanOutSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _src;
    private readonly int _channels;
    private float[] _mono = Array.Empty<float>();

    public FanOutSampleProvider(ISampleProvider mono, int channels)
    {
        _src = mono;
        _channels = channels;
        WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(mono.WaveFormat.SampleRate, channels);
    }

    public WaveFormat WaveFormat { get; }

    public int Read(float[] buffer, int offset, int count)
    {
        int frames = count / _channels;
        if (_mono.Length < frames) _mono = new float[frames];
        int n = _src.Read(_mono, 0, frames);
        for (int f = 0; f < n; f++)
            for (int c = 0; c < _channels; c++) buffer[offset + f * _channels + c] = _mono[f];
        return n * _channels;
    }
}

/// <summary>
/// The default render endpoint in shared mode (event-driven, 40 ms): <see cref="PlaybackQueue"/> → gain → WDL resampler
/// to the mix rate → channel fan-out → <see cref="WasapiOut"/>. A default-device change or a device error raises
/// <see cref="Error"/>; the owner closes and reopens on the next utterance.
/// </summary>
internal sealed class WasapiSink : IAudioSink, IMMNotificationClient
{
    public const int LatencyMs = 40;
    private readonly int _rate;
    private readonly object _lock = new();
    private MMDeviceEnumerator? _enum;
    private WasapiOut? _out;
    private PlaybackQueue? _queue;
    private VolumeSampleProvider? _volume;
    private int _mixRate, _blockAlign;
    private float _gain = 1f;
    private bool _registered;

    public WasapiSink(int sourceRate) => _rate = sourceRate;

    public event Action<string>? Error;
    public bool IsOpen { get { lock (_lock) return _out != null; } }

    public float Gain
    {
        get => _gain;
        set { _gain = value; lock (_lock) if (_volume != null) _volume.Volume = value; }
    }

    public void EnsureOpen()
    {
        lock (_lock)
        {
            if (_out != null) return;
            _enum ??= new MMDeviceEnumerator();
            if (!_registered) { _enum.RegisterEndpointNotificationCallback(this); _registered = true; }
            var device = _enum.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = device.AudioClient.MixFormat;
            var queue = new PlaybackQueue(_rate);
            var volume = new VolumeSampleProvider(queue) { Volume = _gain };
            ISampleProvider chain = mix.SampleRate == _rate ? volume : new WdlResamplingSampleProvider(volume, mix.SampleRate);
            chain = mix.Channels switch
            {
                1 => chain,
                2 => new MonoToStereoSampleProvider(chain),
                _ => new FanOutSampleProvider(chain, mix.Channels),
            };
            var o = new WasapiOut(device, AudioClientShareMode.Shared, true, LatencyMs);
            o.PlaybackStopped += OnStopped;
            o.Init(new SampleToWaveProvider(new IdleBypassProvider(chain, queue)));
            o.Play();
            _out = o;
            _queue = queue;
            _volume = volume;
            _mixRate = mix.SampleRate;
            _blockAlign = o.OutputWaveFormat.BlockAlign;
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null) Error?.Invoke("device stopped");
    }

    public void Close()
    {
        WasapiOut? o;
        lock (_lock) { o = _out; _out = null; _queue = null; _volume = null; }
        if (o is null) return;
        o.PlaybackStopped -= OnStopped;
        try { o.Stop(); } catch (Exception) { }
        try { o.Dispose(); } catch (Exception) { }
    }

    public long Enqueue(float[] samples, int count)
    {
        lock (_lock) return _queue?.Enqueue(samples, count) ?? throw new InvalidOperationException("audio device not open");
    }

    public void Flush() { lock (_lock) _queue?.Flush(); }

    public long PlayedSourceSamples
    {
        get
        {
            WasapiOut? o; int mixRate, ba;
            lock (_lock) { o = _out; mixRate = _mixRate; ba = _blockAlign; }
            if (o is null || ba == 0) return 0;
            try { return (long)(o.GetPosition() / ba * (double)_rate / mixRate); }
            catch (Exception) { return 0; }
        }
    }

    public long QueuedEndSample { get { lock (_lock) return _queue?.QueuedEnd ?? 0; } }

    // IMMNotificationClient: only the default render device for multimedia matters
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia && IsOpen) Error?.Invoke("default device changed");
    }
    public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    public void OnDeviceAdded(string pwstrDeviceId) { }
    public void OnDeviceRemoved(string deviceId) { }
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        Close();
        try { if (_registered) _enum?.UnregisterEndpointNotificationCallback(this); } catch (Exception) { }
        _enum?.Dispose();
    }
}

/// <summary>No device (<c>--audio null</c>, tests): the timeline advances in real time as if played.</summary>
internal sealed class NullSink : IAudioSink
{
    private readonly int _rate;
    private readonly object _lock = new();
    private Stopwatch? _clock;
    private long _queuedEnd;

    public NullSink(int rate) => _rate = rate;
    public event Action<string>? Error { add { } remove { } }
    public bool IsOpen { get { lock (_lock) return _clock != null; } }
    public float Gain { get; set; } = 1f;
    public void EnsureOpen() { lock (_lock) if (_clock is null) { _clock = Stopwatch.StartNew(); _queuedEnd = 0; } }
    public void Close() { lock (_lock) _clock = null; }

    public long PlayedSourceSamples { get { lock (_lock) return _clock is null ? 0 : (long)(_clock.Elapsed.TotalSeconds * _rate); } }

    public long Enqueue(float[] samples, int count)
    {
        lock (_lock)
        {
            long played = _clock is null ? 0 : (long)(_clock.Elapsed.TotalSeconds * _rate);
            long start = Math.Max(_queuedEnd, played);
            _queuedEnd = start + count;
            return start;
        }
    }

    public void Flush() { lock (_lock) _queuedEnd = _clock is null ? 0 : (long)(_clock.Elapsed.TotalSeconds * _rate); }
    public long QueuedEndSample { get { lock (_lock) return _queuedEnd; } }
    public void Dispose() => Close();
}

/// <summary>
/// The one audio sink of the Piper engine (plan 08b): all voices share it, so they share one timeline and one clock.
/// It is created on first use and recreated only when a voice with another sample rate speaks (the timeline is in
/// that rate's samples). Not thread-safe: the caller locks.
/// </summary>
internal sealed class SinkSlot : IDisposable
{
    private readonly Func<int, IAudioSink> _factory;
    private readonly Action<IAudioSink> _setup;

    /// <param name="setup">wires a new sink (gain, error event) before it is used.</param>
    public SinkSlot(Func<int, IAudioSink> factory, Action<IAudioSink> setup)
    {
        _factory = factory;
        _setup = setup;
    }

    public IAudioSink? Current { get; private set; }
    public int Rate { get; private set; }

    /// <summary>The sink for <paramref name="rate"/>: the current one, or a new one (the old one disposed).</summary>
    public IAudioSink Get(int rate)
    {
        if (Current != null && Rate == rate) return Current;
        Current?.Dispose();
        Current = null;
        var s = _factory(rate);
        _setup(s);
        Current = s;
        Rate = rate;
        return s;
    }

    public void Dispose()
    {
        Current?.Dispose();
        Current = null;
    }
}
