using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Scheduling of caret reads with a fake reader (no UIA, no window).</summary>
public class ReadQueueTests
{
    private static readonly IntPtr Fg = (IntPtr)42;
    private static ReadResult Ok(string prefix) =>
        new(ReadStatus.Ok, "fake", ContextExtractor.Extract(prefix, prefix.Length, false, false), 0);

    private sealed class Harness : IDisposable
    {
        public readonly ConcurrentQueue<(ReadRequest Q, ReadResult R)> Done = new();
        public readonly ConcurrentQueue<string> Log = new();
        public readonly SemaphoreSlim Completed = new(0);
        public int Reads;
        public long CurrentVersion = 1;
        public Func<int, ReadResult> Read = _ => Ok("mai");
        public readonly ReadQueue Q;
        public Harness(int stuckMs = 2000, Action<int>? warmUp = null)
        {
            Q = new ReadQueue(v => v == Interlocked.Read(ref CurrentVersion),
                              (q, r) => { Done.Enqueue((q, r)); Completed.Release(); },
                              _ => Read(Interlocked.Increment(ref Reads)),
                              warmUp, m => Log.Enqueue(m), stuckMs);
        }
        public bool WaitCompleted(int ms = 30000) => Completed.Wait(ms);
        public void Dispose() => Q.Dispose();
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void LatestRequestWins()
    {
        using var h = new Harness();
        // a delay far longer than the three calls take, even on a busy runner: no read is due before the last request
        h.Q.Request(1, Fg, 3000, "nav");
        h.Q.Request(1, Fg, 3000, "nav");
        h.Q.Request(1, Fg, 3000, "click");
        Assert.True(h.WaitCompleted());
        Thread.Sleep(400);
        Assert.Single(h.Done);
        Assert.Equal(h.Q.LatestId, h.Done.Single().Q.Id);
        Assert.Equal("click", h.Done.Single().Q.Reason);
        Assert.Equal(1, h.Reads);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void VersionChanged_StaleWithoutReading()
    {
        using var h = new Harness();
        Interlocked.Exchange(ref h.CurrentVersion, 2);   // the user typed after the jump: the request carries version 1
        h.Q.Request(1, Fg, 50, "click");                 // (set first, so the read can never be due before the change)
        Assert.True(h.WaitCompleted());
        Assert.Equal(ReadStatus.Stale, h.Done.Single().R.Status);
        Assert.Equal(0, h.Reads);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void SlowRead_IsTimeout()
    {
        using var h = new Harness { Read = _ => { Thread.Sleep(KeyboardPolicy.ReadTimeoutMs + 60); return Ok("mai"); } };
        h.Q.Request(1, Fg, 0, "nav");
        Assert.True(h.WaitCompleted());
        Assert.Equal(ReadStatus.Timeout, h.Done.Single().R.Status);
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void ThrowingRead_IsUnavailableAndLogged()
    {
        using var h = new Harness { Read = _ => throw new InvalidOperationException("boom") };
        h.Q.Request(1, Fg, 0, "nav");
        Assert.True(h.WaitCompleted());
        Assert.Equal(ReadStatus.Unavailable, h.Done.Single().R.Status);
        Assert.Contains(h.Log, l => l.Contains("InvalidOperationException"));
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void StuckWorker_IsReplaced_AndNeverDeliversItsLateResult()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        using var h = new Harness(stuckMs: 200)
        {
            Read = n => { if (n == 1) { entered.Set(); release.Wait(60000); } return Ok(n == 1 ? "vieux" : "neuf"); }
        };
        try
        {
            h.Q.Request(1, Fg, 0, "first");
            Assert.True(entered.Wait(30000));       // the worker may start late under the suite's load: count from the read
            Thread.Sleep(450);                     // worker 0 is blocked in read #1 for > stuckMs (busy is set before the read)
            h.Q.Request(1, Fg, 0, "second");       // triggers the replacement
            Assert.True(h.WaitCompleted());
            var (q, r) = h.Done.Single();
            Assert.Equal("second", q.Reason);
            Assert.Equal("neuf", r.Ctx.Prefix);
            Assert.True(SpinWait.SpinUntil(() => h.Log.Any(l => l.StartsWith("reader stuck")), 30000));   // logged on the pool

            release.Set();                         // the old generation wakes up…
            Thread.Sleep(400);
            Assert.Single(h.Done);                 // …and delivers nothing
        }
        finally { release.Set(); }
    }

    [Trait("Category", "Timing")]
    [Fact]
    public void StuckWarmUp_IsReplaced_AndReplacementSkipsIt()
    {
        var release = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var gens = new ConcurrentQueue<int>();
        using var h = new Harness(stuckMs: 200, warmUp: g => { gens.Enqueue(g); if (g == 0) { entered.Set(); release.Wait(60000); } });
        try
        {
            Assert.True(entered.Wait(30000));       // counted from the warm-up's start, not the harness's
            Thread.Sleep(450);                     // generation 0 hangs in its warm-up (busy is set before it)
            h.Q.Request(1, Fg, 0, "click");
            Assert.True(h.WaitCompleted());        // generation 1 starts from the thread pool: 30 s bound
            Assert.Equal("click", h.Done.Single().Q.Reason);
            Assert.Contains(1, gens);
            release.Set();
            Thread.Sleep(300);
            Assert.Single(h.Done);
        }
        finally { release.Set(); }
    }
}
