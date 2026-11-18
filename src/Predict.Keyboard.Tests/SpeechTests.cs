// xUnit1031: blocking on a task (Wait / Result) is deliberate here: the test checks, synchronously, the state that the
// app's background write, load or worker leaves behind (no xUnit sync context, so no deadlock)
#pragma warning disable xUnit1031
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Speech echo (what is said while typing), queue policy, the speech thread and the password guard.</summary>
public class SpeechTests
{
    private static List<EchoEvent> Feed(SpeechEcho e, string text)
    {
        var l = new List<EchoEvent>();
        foreach (var c in text) if (e.Type(c) is { } ev) l.Add(ev);
        return l;
    }

    private static SpeechEcho Words() => new() { SpeakWords = true };
    private static string[] Texts(IEnumerable<EchoEvent> l) => l.Select(x => x.Text).ToArray();

    [Fact] public void Words_After_Spaces() => Assert.Equal(new[] { "le", "chat", "dort" }, Texts(Feed(Words(), "le chat dort ")));
    [Fact] public void Apostrophe_Joins_A_Chunk() => Assert.Equal(new[] { "l'arbre" }, Texts(Feed(Words(), "l'arbre ")));
    [Fact] public void Typographic_Apostrophe_And_Hyphen() => Assert.Equal(new[] { "l’eau", "peut-être" }, Texts(Feed(Words(), "l’eau peut-être,")));
    [Fact] public void Punctuation_Flushes() => Assert.Equal(new[] { "oui", "non", "si" }, Texts(Feed(Words(), "oui;non:si (")));

    [Fact]
    public void Sentence_End_Speaks_The_Sentence_Not_Its_Last_Word()
    {
        var e = new SpeechEcho { SpeakWords = true, SpeakSentences = true };
        var l = Feed(e, "il fait beau.");
        Assert.Equal(new[] { "il", "fait" }, Texts(l.Where(x => x.Kind == EchoKind.Word)));
        Assert.Equal(new EchoEvent(EchoKind.Sentence, "il fait beau"), l.Last());
        var only = Feed(new SpeechEcho { SpeakSentences = true }, "il fait beau. Bonjour !");
        Assert.Equal(new[] { new EchoEvent(EchoKind.Sentence, "il fait beau"), new EchoEvent(EchoKind.Sentence, "Bonjour") }, only);
    }

    [Fact]
    public void Sentence_End_Without_Sentences_Speaks_The_Last_Word()
    {
        Assert.Equal(new[] { "il", "fait", "beau" }, Texts(Feed(Words(), "il fait beau.")));
    }

    [Fact]
    public void Enter_Flushes_The_Word_Only()
    {
        var e = new SpeechEcho { SpeakWords = true, SpeakSentences = true };
        Assert.Equal(new[] { new EchoEvent(EchoKind.Word, "bonjour") }, Feed(e, "bonjour\n"));
        var s = new SpeechEcho { SpeakSentences = true };
        Assert.Empty(Feed(s, "bonjour\n"));
        Assert.Equal("bonjour toi", Feed(s, "toi.").Single().Text);   // Enter is not a sentence end
    }

    [Fact]
    public void Backspace_Over_A_Space_Then_Retype_Gives_The_Right_Chunk()
    {
        var e = Words();
        Assert.Equal(new[] { "le", "chat" }, Texts(Feed(e, "le chat ")));
        e.Backspace();   // the space: « chat » comes back
        e.Backspace();   // t
        Assert.Equal(new[] { "chas" }, Texts(Feed(e, "s ")));
        for (int i = 0; i < 5; i++) e.Backspace();   // the space after « chas », then its 4 letters
        Assert.Equal(new[] { "chien" }, Texts(Feed(e, "chien ")));
    }

    [Fact]
    public void Reset_Mid_Sentence_Speaks_Only_What_Follows()
    {
        var e = new SpeechEcho { SpeakSentences = true };
        Feed(e, "le chat ");
        e.Reset();
        Assert.Equal("dort bien", Feed(e, "dort bien.").Single().Text);
    }

    [Fact]
    public void Insertion_Then_Space_Does_Not_Repeat()
    {
        var e = Words();
        Feed(e, "la mai");
        Assert.Null(e.Inserted("maison", spaced: false, spoken: true));
        Assert.Empty(Feed(e, " "));
        Assert.Null(e.Inserted("chat", spaced: true, spoken: true));   // our own space: no event, no repeat
        Assert.Equal(new[] { "dort" }, Texts(Feed(e, "dort ")));
    }

    [Fact]
    public void Insertion_Then_Letters_Speaks_The_New_Word()
    {
        var e = Words();
        e.Inserted("maison", spaced: false, spoken: true);
        Assert.Equal(new[] { "maisons" }, Texts(Feed(e, "s ")));
        // auto-space removed with Backspace, then « s »: the mark comes back with the chunk, then clears
        var f = Words();
        f.Inserted("maison", spaced: true, spoken: true);
        f.Backspace();
        Assert.Equal(new[] { "maisons" }, Texts(Feed(f, "s ")));
        // inserted without the inserted echo: the word echo says it at its space
        Assert.Equal("maison", Words().Inserted("maison", spaced: true, spoken: false)!.Text);
    }

    [Fact]
    public void Sentence_Contains_Inserted_Words_And_Seeded_Prefix()
    {
        var e = new SpeechEcho { SpeakSentences = true };
        Feed(e, "la mai");
        e.Inserted("maison", true, true);
        Assert.Equal("la maison est belle", Feed(e, "est belle.").Single().Text);
        e.Seed("cha");
        Assert.Equal("chat", Feed(e, "t!").Single().Text);
    }

    [Fact]
    public void Flags_Off_No_Events()
    {
        var e = new SpeechEcho();
        Assert.Empty(Feed(e, "le chat dort. Il fait beau!\n"));
        Assert.Null(e.Inserted("x", true, false));
    }

    [Fact]
    public void Sentence_Is_Capped()
    {
        var e = new SpeechEcho { SpeakSentences = true };
        var t = string.Concat(Enumerable.Range(0, 70).Select(i => "m" + i + " "));
        var s = Feed(e, t + ".").Single().Text.Split(' ');
        Assert.Equal(SpeechEcho.MaxSentenceWords, s.Length);
        Assert.Equal("m10", s[0]);
    }

    // ------------------------------------------------------------------ queue policy

    private static SpeechItem I(string t, SpeechKind k) => new(t, k);

    [Fact]
    public void Interrupting_Kinds_Clear_Pending()
    {
        foreach (var k in new[] { SpeechKind.Manual, SpeechKind.Preview, SpeechKind.Inserted })
        {
            var q = new SpeechQueue();
            Assert.False(q.Enqueue(I("a", SpeechKind.Word)));
            Assert.False(q.Enqueue(I("b", SpeechKind.Sentence)));
            Assert.True(q.Enqueue(I("c", k)));
            Assert.Equal(new[] { "c" }, q.Pending.Select(x => x.Text));
        }
    }

    [Fact]
    public void Words_Are_Capped_At_Two_Pending()
    {
        var q = new SpeechQueue();
        foreach (var w in new[] { "a", "b", "c", "d" }) q.Enqueue(I(w, SpeechKind.Word));
        Assert.Equal(new[] { "c", "d" }, q.Pending.Select(x => x.Text));
        Assert.Equal("c", q.Next()!.Text);
        Assert.Equal("d", q.Next()!.Text);
        Assert.Null(q.Next());
    }

    [Fact]
    public void Sentence_Drops_Pending_Words()
    {
        var q = new SpeechQueue();
        q.Enqueue(I("a", SpeechKind.Word));
        q.Enqueue(I("s1", SpeechKind.Sentence));
        q.Enqueue(I("b", SpeechKind.Word));
        q.Enqueue(I("s2", SpeechKind.Sentence));
        Assert.Equal(new[] { "s1", "s2" }, q.Pending.Select(x => x.Text));
    }

    // ------------------------------------------------------------------ speech thread

    private sealed class FakeSynth : ISynth
    {
        public int CreateDelayMs, SpeakMs = 50;
        public readonly ConcurrentQueue<string> Spoken = new();
        public readonly ManualResetEventSlim Started = new();
        private Action<SpeechOutcome, string?>? _done;
        private CancellationTokenSource? _cts;
        public int Cancels;
        public Action<SpeechProgress>? LastProgress;
        public IReadOnlyList<VoiceEntry> Voices() { Thread.Sleep(CreateDelayMs); return new[] { new VoiceEntry("sapi:Fake", "Fake", "fr-FR", "Female", VoiceId.Sapi) }; }
        public readonly ConcurrentQueue<string?> Configured = new();
        public string Configure(SpeechSettings s) { Configured.Enqueue(s.Voice); return "Fake"; }
        public void SpeakAsync(string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null)
        {
            Spoken.Enqueue(text);
            LastProgress = progress;
            _done = done;
            var cts = _cts = new CancellationTokenSource();
            Started.Set();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                bool cancelled = cts.Token.WaitHandle.WaitOne(SpeakMs);
                done(cancelled ? SpeechOutcome.Cancelled : SpeechOutcome.Done, null);
            });
        }
        public void CancelAll() { Interlocked.Increment(ref Cancels); _cts?.Cancel(); }
        public void Dispose() { }
    }

    private static bool WaitFor(Func<bool> cond, int ms = 4000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms) { if (cond()) return true; Thread.Sleep(5); }
        return cond();
    }

    /// <summary>Median of 9 batches of 100 calls, per call (robust against a descheduled batch on a busy runner).</summary>
    private static double MedianPerCallMs(Action call)
    {
        call();   // JIT
        var batches = new List<double>();
        for (int b = 0; b < 9; b++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 100; i++) call();
            batches.Add(sw.Elapsed.TotalMilliseconds / 100);
        }
        batches.Sort();
        return batches[4];
    }

    [Fact]
    public void Hook_Side_Calls_Never_Wait_For_A_Blocked_Synthesizer_Or_Probe()
    {
        var fake = new FakeSynth { CreateDelayMs = 2000 };
        using var w = new SpeechWorker(() => fake, new SpeechSettings(), null);
        var block = new ManualResetEventSlim();
        using var g = new EchoGuard(_ => { block.Wait(5000); return ProbeStatus.NotPassword; }, () => (IntPtr)1, i => w.Say(i), null);
        var item = new SpeechItem("mot", SpeechKind.Word);
        double say = MedianPerCallMs(() => w.Say("mot", SpeechKind.Word));
        double cancel = MedianPerCallMs(w.CancelAll);
        double submit = MedianPerCallMs(() => g.Submit(item, (IntPtr)1));
        double clear = MedianPerCallMs(g.Clear);
        block.Set();
        Assert.True(say < 0.1 && cancel < 0.1 && submit < 0.1 && clear < 0.1,
                    $"per call: Say {say:0.0000} CancelAll {cancel:0.0000} Submit {submit:0.0000} Clear {clear:0.0000} ms");
    }

    [Fact]
    public void Guard_Never_Forwards_An_Item_Cleared_While_Probing_And_Reports_Passwords()
    {
        var fg = (IntPtr)5;
        var inProbe = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var status = ProbeStatus.NotPassword;
        var forwarded = new ConcurrentQueue<string>();
        var passwords = new ConcurrentQueue<IntPtr>();
        var log = new ConcurrentQueue<string>();
        using var g = new EchoGuard(_ => { inProbe.Set(); release.Wait(5000); return status; }, () => fg,
                                    i => forwarded.Enqueue(i.Text), log.Enqueue, passwords.Enqueue);
        g.Submit(new SpeechItem("avant", SpeechKind.Word), fg);
        Assert.True(inProbe.Wait(4000));
        g.Clear();                       // pause while the item is being probed
        release.Set();
        Assert.True(WaitFor(() => log.Any(l => l.StartsWith("echo dropped (cancelled)"))));
        Assert.Empty(forwarded);
        status = ProbeStatus.Password;
        g.Submit(new SpeechItem("secret", SpeechKind.Word), fg);
        Assert.True(WaitFor(() => passwords.Contains(fg)));
        Assert.Empty(forwarded);
    }

    [Fact]
    public void Abandoned_Guard_Threads_Are_Capped()
    {
        var fg = (IntPtr)1;
        var hang = new ManualResetEventSlim();
        var log = new ConcurrentQueue<string>();
        int calls = 0;
        long lastStart = 0;
        using var g = new EchoGuard(_ =>
                                    {
                                        Interlocked.Exchange(ref lastStart, Environment.TickCount64);
                                        Interlocked.Increment(ref calls);
                                        hang.Wait(60000);
                                        return ProbeStatus.NotPassword;
                                    },
                                    () => fg, _ => { }, log.Enqueue);
        try
        {
            // each worker generation takes its item and hangs; the next Submit sees it stuck and starts a new one
            for (int round = 0; round <= ProbeWorker<object>.MaxAbandoned; round++)
            {
                g.Submit(new SpeechItem("m" + round, SpeechKind.Word), fg);
                int expected = round + 1;
                Assert.True(WaitFor(() => Volatile.Read(ref calls) == expected, 30000), $"round {round}: {calls} calls");
                WaitUntilStuck(ref lastStart);
            }
            // every replacement used up: the next item is refused at once (on the caller's thread)
            g.Submit(new SpeechItem("m" + (ProbeWorker<object>.MaxAbandoned + 1), SpeechKind.Word), fg);
            Assert.Equal(ProbeWorker<object>.MaxAbandoned, log.Count(l => l.Contains("new thread")));
            Assert.Contains(log, l => l == "echo dropped (guard stuck)");
            Assert.Equal(ProbeWorker<object>.MaxAbandoned + 1, Volatile.Read(ref calls));
        }
        finally { hang.Set(); }
    }

    /// <summary>Waits until the probe that started last has been busy for longer than <see cref="EchoGuard.StuckMs"/>:
    /// the worker marks itself busy before it calls the probe, so the guard then sees it as stuck. Measured from the
    /// probe's own start, not from the test's Submit, so a slow thread start on a busy runner cannot shorten it.</summary>
    private static void WaitUntilStuck(ref long lastStart)
    {
        long start = Interlocked.Read(ref lastStart);
        Assert.True(start != 0, "the probe never started");
        while (Environment.TickCount64 - start <= EchoGuard.StuckMs + 100) Thread.Sleep(20);
    }

    [Fact]
    public void Field_Probe_Latest_Request_Wins()
    {
        var results = new ConcurrentQueue<(long, ProbeStatus)>();
        using var p = new FieldProbe(fg => fg == (IntPtr)7 ? ProbeStatus.Password : ProbeStatus.NotPassword,
                                     (id, fg, st, ms) => results.Enqueue((id, st)), null);
        long a = p.Request((IntPtr)7);
        Assert.True(a > 0);
        Assert.True(WaitFor(() => results.Any(r => r.Item1 == a)));
        Assert.Equal(ProbeStatus.Password, results.Single(r => r.Item1 == a).Item2);
        long b = p.Request((IntPtr)8);
        Assert.True(WaitFor(() => results.Any(r => r.Item1 == b)));
        Assert.Equal(ProbeStatus.NotPassword, results.Single(r => r.Item1 == b).Item2);
    }

    [Fact]
    public void Say_Returns_At_Once_Even_With_A_Blocking_Synthesizer()
    {
        var fake = new FakeSynth { CreateDelayMs = 2000 };   // creation + voice enumeration hang 2 s
        using var w = new SpeechWorker(() => fake, new SpeechSettings(), null);
        double perCall = MedianPerCallMs(() => w.Say("mot", SpeechKind.Word));
        Assert.True(perCall < 0.1, $"Say took {perCall:0.0000} ms per call");
        Assert.False(w.Voices.IsCompleted);
        Assert.True(WaitFor(() => w.Voices.IsCompleted));
    }

    [Fact]
    public void After_A_Preview_The_Configured_Settings_Are_Applied_Again_When_Idle()
    {
        // plan 08b: the engine must learn the configured voice back (its residency) without waiting for an utterance
        var fake = new FakeSynth();
        using var w = new SpeechWorker(() => fake, new SpeechSettings(Voice: "piper:siwis"), null);
        w.Preview("Bonjour", new SpeechSettings(Voice: "piper:pierre"));
        Assert.True(WaitFor(() => fake.Configured.Count >= 3));
        Assert.Equal(new string?[] { "piper:siwis", "piper:pierre", "piper:siwis" }, fake.Configured.ToArray());
        // a Configure that arrives while an item speaks is applied once it ends
        fake.SpeakMs = 500;
        w.Say("une phrase", SpeechKind.Sentence);
        Assert.True(fake.Started.Wait(4000));
        w.Configure(new SpeechSettings(Voice: "piper:pierre"));
        Assert.True(WaitFor(() => fake.Configured.Count >= 4));
        Assert.Equal("piper:pierre", fake.Configured.Last());
    }

    [Fact]
    public void Inserted_Interrupts_And_CancelAll_Is_Honoured()
    {
        var fake = new FakeSynth { SpeakMs = 3000 };
        var log = new ConcurrentQueue<string>();
        using var w = new SpeechWorker(() => fake, new SpeechSettings(), log.Enqueue);
        w.Say("une longue phrase", SpeechKind.Sentence);
        Assert.True(fake.Started.Wait(4000));
        w.Say("maison", SpeechKind.Inserted);
        Assert.True(WaitFor(() => fake.Spoken.Contains("maison")));
        Assert.Contains(log, l => l.StartsWith("speech cancelled"));
        w.CancelAll();
        Assert.True(WaitFor(() => log.Count(l => l.StartsWith("speech cancelled")) == 2, 1000));   // well under the 3 s utterance
        w.Say("a", SpeechKind.Word);
        Assert.True(WaitFor(() => fake.Spoken.Contains("a")));
        Assert.Contains(log, l => l == "speak Inserted 'maison'");
    }

    [Fact]
    public void Speech_Unavailable_Is_Logged_And_Harmless()
    {
        var log = new ConcurrentQueue<string>();
        using var w = new SpeechWorker(() => throw new InvalidOperationException("no audio"), new SpeechSettings(), log.Enqueue);
        Assert.True(WaitFor(() => w.Voices.IsCompleted));
        Assert.Empty(w.Voices.Result);
        w.Say("x", SpeechKind.Manual);
        Assert.True(WaitFor(() => log.Any(l => l.StartsWith("speech unavailable"))));
    }

    // ------------------------------------------------------------------ echo guard

    [Fact]
    public void Guard_Drops_Password_And_Slow_Probes_Without_Logging_The_Text()
    {
        var fg = (IntPtr)42;
        var status = ProbeStatus.Password;
        int delay = 0;
        var forwarded = new ConcurrentQueue<string>();
        var log = new ConcurrentQueue<string>();
        using var g = new EchoGuard(_ => { Thread.Sleep(delay); return status; }, () => fg, i => forwarded.Enqueue(i.Text), log.Enqueue);
        g.Submit(new SpeechItem("secret", SpeechKind.Word), fg);
        Assert.True(WaitFor(() => log.Any(l => l.StartsWith("echo dropped (password)"))));
        status = ProbeStatus.Unavailable;   // 0.8.3 (owner): a field that cannot be classified stays silent
        g.Submit(new SpeechItem("inconnu", SpeechKind.Word), fg);
        Assert.True(WaitFor(() => log.Any(l => l.StartsWith("echo dropped (field not proven)"))));
        status = ProbeStatus.NotPassword;   // a plain field: spoken
        g.Submit(new SpeechItem("bonjour", SpeechKind.Word), fg);
        Assert.True(WaitFor(() => forwarded.Contains("bonjour")));
        status = ProbeStatus.NotPassword; delay = 400;
        g.Submit(new SpeechItem("lent", SpeechKind.Word), fg);
        Assert.True(WaitFor(() => log.Any(l => l.Contains("> 300"))));
        delay = 0;
        g.Submit(new SpeechItem("ailleurs", SpeechKind.Word), (IntPtr)7);   // foreground changed since the key
        Assert.True(WaitFor(() => log.Any(l => l.Contains("foreground changed"))));
        Assert.DoesNotContain(log, l => l.Contains("secret") || l.Contains("lent") || l.Contains("ailleurs") || l.Contains("inconnu"));
        Assert.Equal(new[] { "bonjour" }, forwarded.ToArray());
    }

    [Fact]
    public void Stuck_Guard_Is_Replaced()
    {
        var fg = (IntPtr)1;
        var hang = new ManualResetEventSlim();
        var forwarded = new ConcurrentQueue<string>();
        var log = new ConcurrentQueue<string>();
        int calls = 0;
        long lastStart = 0;
        var blockedDone = new ManualResetEventSlim();
        using var g = new EchoGuard(_ =>
                                    {
                                        Interlocked.Exchange(ref lastStart, Environment.TickCount64);
                                        if (Interlocked.Increment(ref calls) == 1) hang.Wait(60000);
                                        return ProbeStatus.NotPassword;
                                    },
                                    () => fg, i => forwarded.Enqueue(i.Text), log.Enqueue);
        try
        {
            g.Submit(new SpeechItem("bloque", SpeechKind.Word, Done: _ => blockedDone.Set()), fg);
            Assert.True(WaitFor(() => Volatile.Read(ref calls) == 1, 30000));
            WaitUntilStuck(ref lastStart);
            g.Submit(new SpeechItem("suivant", SpeechKind.Word), fg);
            Assert.True(WaitFor(() => forwarded.Contains("suivant"), 30000));
            hang.Set();
            // the abandoned guard comes back, sees it no longer owns the queue and drops its item (Done = Cancelled)
            Assert.True(blockedDone.Wait(30000), "the abandoned item was never settled");
            Assert.DoesNotContain("bloque", forwarded);   // the abandoned guard never delivers
            Assert.Contains(log, l => l.Contains("new thread"));
        }
        finally { hang.Set(); }
    }
}
