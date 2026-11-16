using System;
using System.Diagnostics;
using System.Threading;

namespace Predict.Keyboard;

internal enum ReadStatus { Ok, Password, Selection, Unavailable, Timeout, Stale }

/// <param name="Version">tracker version when the jump happened: the result applies only if still equal.</param>
/// <param name="DueTick">Environment.TickCount64 at which to read (lets the app process the key/click first).</param>
/// <param name="RequestedTick">Environment.TickCount64 of the request (plan 07: « reading bar shown N ms after the selection »).</param>
/// <param name="Clipboard">plan 07 selection reads: this request may use the simulated Ctrl+C fallback.</param>
/// <param name="Trigger">plan 07 selection reads: TickCount64 of the gesture that led to it.</param>
internal readonly record struct ReadRequest(long Id, long Version, IntPtr Fg, long DueTick, string Reason, long RequestedTick = 0,
                                            bool Clipboard = false, long Trigger = 0);

/// <param name="Source">"win32", "uia" or "none".</param>
/// <param name="Before">only for the punctuation read-back: the raw characters right before the caret.</param>
/// <param name="Window">the text read around the caret (widened to the sentence bounds), for the D23 edit session.</param>
/// <param name="Selected">plan 07, selection reads only: the text to read ("" = no selection) — after D26, the selection
/// completed to whole words plus the sentence end that follows; <see cref="Before"/> / <see cref="After"/> hold the context
/// read around the selection, <see cref="TailAt"/> where the added sentence end starts (-1 = none); <see cref="Cut"/> =
/// the selection was longer than the reader's character limit (cut at a word boundary).</param>
internal readonly record struct ReadResult(ReadStatus Status, string Source, TextContext Ctx, long Ms, string? Before = null,
                                           TextWindow? Window = null, string? Selected = null, string? After = null, int TailAt = -1,
                                           bool Cut = false);

/// <summary>
/// Scheduling of caret reads, independent of how a read is done (so it is unit-tested with a fake reader):
/// one dedicated MTA worker thread, one pending slot where the latest request wins (debounces
/// auto-repeat), a version check before reading (a request overtaken by typing is <c>Stale</c> without
/// reading), a time budget after it (<c>Timeout</c>). A worker busy for more than <c>stuckMs</c> — in a
/// read or in its warm-up — is abandoned when the next request arrives: a new generation takes over and
/// the old one exits without ever delivering its result.
/// </summary>
internal sealed class ReadQueue : IDisposable
{
    private readonly Func<long, bool> _isCurrent;
    private readonly Action<ReadRequest, ReadResult> _completed;
    private readonly Func<ReadRequest, ReadResult> _read;
    private readonly Action<int>? _warmUp;
    private readonly Action<string>? _log;
    private readonly int _stuckMs, _timeoutMs;
    private readonly string _name;
    private readonly object _lock = new();
    private readonly AutoResetEvent _signal = new(false);
    private ReadRequest? _pending;          // guarded by _lock
    private long _busySince;                // guarded by _lock; 0 = idle
    private int _generation;                // guarded by _lock
    private long _nextId;
    private long _latestId;
    private volatile bool _disposed;

    /// <param name="read">performs one read for the foreground window (worker thread).</param>
    /// <param name="warmUp">called once at the start of each worker, with its generation (counted as busy).</param>
    /// <param name="timeoutMs">a read longer than this is reported as Timeout (caret reads: <see cref="KeyboardPolicy.ReadTimeoutMs"/>).</param>
    public ReadQueue(Func<long, bool> isCurrent, Action<ReadRequest, ReadResult> completed, Func<IntPtr, ReadResult> read,
                     Action<int>? warmUp, Action<string>? log, int stuckMs, int timeoutMs = KeyboardPolicy.ReadTimeoutMs,
                     string name = "caret-text-reader")
        : this(isCurrent, completed, q => read(q.Fg), warmUp, log, stuckMs, timeoutMs, name) { }

    /// <param name="read">performs one read for a request (its flags travel with it: latest request, latest flags).</param>
    public ReadQueue(Func<long, bool> isCurrent, Action<ReadRequest, ReadResult> completed, Func<ReadRequest, ReadResult> read,
                     Action<int>? warmUp, Action<string>? log, int stuckMs, int timeoutMs, string name)
    {
        _timeoutMs = timeoutMs;
        _name = name;
        _isCurrent = isCurrent;
        _completed = completed;
        _read = read;
        _warmUp = warmUp;
        _log = log;
        _stuckMs = stuckMs;
        StartWorker(0);
    }

    /// <summary>Id of the most recent request.</summary>
    public long LatestId => Interlocked.Read(ref _latestId);

    /// <summary>A longer « stuck » limit while the worker runs a known long operation (plan 07: the Ctrl+C fallback); 0 = none.</summary>
    public volatile int StuckMsOverride;

    /// <summary>Hook-thread safe: a tiny lock and an event set, no I/O, never waits.</summary>
    public void Request(long version, IntPtr fg, int delayMs, string reason, bool clipboard = false, long trigger = 0)
    {
        if (_disposed) return;
        long now = Environment.TickCount64;
        long id = Interlocked.Increment(ref _nextId);
        Interlocked.Exchange(ref _latestId, id);
        int replaceGen = -1;
        long stuckFor = 0;
        lock (_lock)
        {
            _pending = new ReadRequest(id, version, fg, now + Math.Max(0, delayMs), reason, now, clipboard, trigger);
            if (_busySince != 0 && now - _busySince > Math.Max(_stuckMs, StuckMsOverride))
            {
                stuckFor = now - _busySince;
                replaceGen = ++_generation;    // the old worker exits when (if) it comes back
                _busySince = 0;
            }
        }
        if (replaceGen >= 0)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ =>
            {
                _log?.Invoke($"reader stuck {stuckFor} ms, replaced");
                StartWorker(replaceGen);
            }, null);
        }
        _signal.Set();
    }

    private void StartWorker(int gen)
    {
        var t = new Thread(() => Loop(gen)) { IsBackground = true, Name = _name };
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.MTA);   // UIA client: MTA
        t.Start();
    }

    private void Loop(int gen)
    {
        if (_warmUp != null)
        {
            lock (_lock) { if (gen != _generation) return; _busySince = Environment.TickCount64; }
            try { _warmUp(gen); } catch (Exception ex) { _log?.Invoke($"reader warm-up error: {ex.GetType().Name}"); }
            lock (_lock) { if (gen != _generation) return; _busySince = 0; }
        }
        while (!_disposed)
        {
            ReadRequest req;
            lock (_lock)
            {
                if (gen != _generation) return;
                req = _pending ?? default;
            }
            if (req.Id == 0) { _signal.WaitOne(); continue; }
            long wait = req.DueTick - Environment.TickCount64;
            if (wait > 0) { _signal.WaitOne((int)wait); continue; }   // re-check: a newer request may have replaced it

            lock (_lock)
            {
                if (gen != _generation) return;
                if (_pending is null || _pending.Value.Id != req.Id) continue;
                _pending = null;
                _busySince = Environment.TickCount64;
            }

            ReadResult r;
            if (!_isCurrent(req.Version))
                r = new ReadResult(ReadStatus.Stale, "none", TextContext.Invalid, 0);
            else
            {
                var sw = Stopwatch.StartNew();
                try { r = _read(req); }
                catch (Exception ex)
                {
                    r = new ReadResult(ReadStatus.Unavailable, "none", TextContext.Invalid, 0);
                    _log?.Invoke($"reader error: {ex.GetType().Name}: {ex.Message}");
                }
                long ms = sw.ElapsedMilliseconds;
                r = r with { Ms = ms };
                if (ms > _timeoutMs && r.Status != ReadStatus.Password) r = r with { Status = ReadStatus.Timeout };
            }

            lock (_lock)
            {
                if (gen != _generation) return;   // replaced while stuck: never deliver a late result
                _busySince = 0;
            }
            try { _completed(req, r); }
            catch (Exception ex) { _log?.Invoke($"reader callback error: {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _disposed = true;
        lock (_lock) _generation++;
        _signal.Set();
    }
}
