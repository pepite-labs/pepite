using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace Predict.Keyboard.Tests;

/// <summary>LibreOffice phase 1, S2: the IA2 reader core over a fake backend (no COM, no window).</summary>
public class LibreOfficeTextTests
{
    internal static readonly IntPtr Fg = (IntPtr)0x1000, Fg2 = (IntPtr)0x2000, Para = (IntPtr)0x1010;
    private const int Focused = Ia2.STATE_SYSTEM_FOCUSED;

    internal sealed class FakePara : ILoParagraph
    {
        private readonly FakeBackend _b;
        public FakePara(FakeBackend b, string content = "la maison ", int caret = -2)
        {
            _b = b;
            Content = content;
            Caret = caret == -2 ? content.Length : caret;
        }
        public int Role { get; set; } = Ia2.ROLE_PARAGRAPH;
        public int StateValue = Focused;
        public string Content;
        public int Caret;
        public (int S, int E)? Sel;
        /// <summary>Phase 2: several ranges, as reported (a backward one reversed); overrides <see cref="Sel"/>.</summary>
        public List<(int S, int E)>? Sels;
        /// <summary>Phase 2: the document this paragraph belongs to and its index there (<see cref="Neighbour"/>).</summary>
        public FakeDoc? Doc;
        public int Index;
        public int Calls, TextCalls;
        public (int S, int E) LastText;
        public bool Disposed;
        public int DropOneChar;               // > 0: get_text returns one character less (offset mismatch)
        public Dictionary<string, int> Hr = new();
        public Action? DuringText;
        public double Cost;                   // ms added to the backend clock per call
        public string? SleepOn;               // a real sleep of SleepMs in this call (a busy LibreOffice)
        public int SleepMs;

        private int Enter(string name)
        {
            Calls++;
            _b.Now += Cost;
            if (name == SleepOn && SleepMs > 0) System.Threading.Thread.Sleep(SleepMs);
            Assert.False(Disposed, $"{name} on a disposed paragraph");
            return Hr.TryGetValue(name, out int hr) ? hr : Ia2.S_OK;
        }

        public int State(out int state) { state = StateValue; return Enter("State"); }
        public int CaretOffset(out int offset) { offset = Caret; return Enter("Caret"); }
        public int SelectionCount(out int count) { count = Sels?.Count ?? (Sel is null ? 0 : 1); return Enter("NSel"); }
        public int Selection(int index, out int start, out int end)
        {
            (start, end) = Sels is { } l ? l[index] : Sel ?? (0, 0);
            return Enter("Sel");
        }
        public int Neighbour(bool next, out ILoParagraph? paragraph)
        {
            paragraph = null;
            int hr = Enter("Neighbour");
            if (hr < 0 || Doc is null) return hr < 0 ? hr : Ia2.S_FALSE;
            _b.Now += Doc.HopCost;
            int i = Index + (next ? 1 : -1);
            if (i < 0 || i >= Doc.Count) return Ia2.S_FALSE;
            paragraph = Doc.View(i);   // a new object per call, as a COM relation target is
            return Ia2.S_OK;
        }
        public int CharacterCount(out int count) { count = Content.Length; return Enter("NChars"); }
        public int Text(int start, int end, out string? text)
        {
            TextCalls++;
            LastText = (start, end);
            int hr = Enter("Text");
            DuringText?.Invoke();
            text = Content.Substring(start, end - start - (DropOneChar > 0 && end > start ? 1 : 0));
            return hr;
        }
        public int CharacterExtents(int offset, out int x, out int y, out int width, out int height)
        {
            (x, y, width, height) = (100 + 10 * offset, 200, 10, 20);
            return Enter("Extents");
        }
        public int Location(out int x, out int y, out int width, out int height)
        {
            (x, y, width, height) = (100, 300, 500, 24);
            return Enter("Location");
        }
        public void Dispose() { Assert.False(Disposed, "disposed twice"); Disposed = true; }
    }

    /// <summary>Phase 2: a fake Writer document — paragraphs, a selection from (paragraph, offset) to (paragraph, offset),
    /// reported the way the VM probe showed it: every paragraph its own range, an empty paragraph inside [0,0], the cursor's
    /// paragraph reversed for a backward selection, an empty mark at the cursor's paragraph edge.</summary>
    internal sealed class FakeDoc
    {
        private readonly FakeBackend _b;
        public readonly List<string> Texts;
        public readonly List<List<(int S, int E)>> Ranges = new();
        public readonly Dictionary<int, int> States = new(), Roles = new();
        public readonly Dictionary<int, int> NeighbourHr = new();
        public readonly List<FakePara> Views = new();
        /// <summary>Backend clock time added by each <see cref="FakePara.Neighbour"/> call only (a slow relation hop).</summary>
        public double HopCost;
        /// <summary>Backend clock time added by each text call only (a slow get_text).</summary>
        public double TextCost;
        public int Count => Texts.Count;

        public FakeDoc(FakeBackend b, params string[] texts)
        {
            _b = b;
            Texts = texts.ToList();
            foreach (var _ in texts) Ranges.Add(new List<(int, int)>());
        }

        /// <summary>Selects (p0, o0) → (p1, o1) in document order; <paramref name="backward"/> = the cursor is at the start.</summary>
        public FakeDoc Select(int p0, int o0, int p1, int o1, bool backward = false)
        {
            foreach (var r in Ranges) r.Clear();
            for (int i = p0; i <= p1; i++)
            {
                int s = i == p0 ? o0 : 0, e = i == p1 ? o1 : Texts[i].Length;
                bool cursor = backward ? i == p0 : i == p1;
                Ranges[i].Add(cursor && backward ? (e, s) : (s, e));
            }
            return this;
        }

        public FakePara View(int i)
        {
            var p = new FakePara(_b, Texts[i], -1)
            {
                Doc = this, Index = i, Sels = new List<(int, int)>(Ranges[i]),
                DuringText = TextCost > 0 ? () => _b.Now += TextCost : null,
                StateValue = States.TryGetValue(i, out int st) ? st : (Ranges[i].Count > 0 ? Ia2.STATE_SYSTEM_SELECTED : 0),
            };
            if (Roles.TryGetValue(i, out int role)) p.Role = role;
            if (NeighbourHr.TryGetValue(i, out int hr)) p.Hr["Neighbour"] = hr;
            _b.Made.Add(p);
            Views.Add(p);
            return p;
        }

        /// <summary>The focused paragraph (the cursor's), as the focus event names it.</summary>
        public FakePara Focus(int i, int caret)
        {
            var p = View(i);
            p.Caret = caret;
            p.StateValue |= Focused;
            return p;
        }
    }

    internal sealed class FakeBackend : ILoBackend
    {
        public uint Pid = 7;
        public IntPtr ForegroundWindow = Fg;
        public double Now = 1000;
        public (int L, int T, int R, int B) Rect = (0, 0, 2000, 1500);
        public Func<FakePara?> OnEvent, OnWalk;
        public int EventHr, WalkHr;
        public int EventCalls, WalkCalls, PidCalls, VersionCalls;
        public readonly List<FakePara> Made = new();

        public FakeBackend()
        {
            OnEvent = () => New();
            OnWalk = () => New();
        }

        public FakePara New(string content = "la maison ", int caret = -2)
        {
            var p = new FakePara(this, content, caret);
            Made.Add(p);
            return p;
        }

        /// <summary>COM-side calls (resolves + paragraph calls); the pid / clock / rect lookups are Win32 and free.</summary>
        public int ComCalls => EventCalls + WalkCalls + Made.Sum(p => p.Calls);

        public uint PidOf(IntPtr fg) { PidCalls++; return Pid; }
        public IntPtr Foreground() => ForegroundWindow;
        public bool WindowRect(IntPtr fg, out int left, out int top, out int right, out int bottom)
        {
            (left, top, right, bottom) = Rect;
            return true;
        }
        public string? ProductVersion(uint pid) { VersionCalls++; return "26.8.3.1"; }
        public double NowMs() => Now;

        public int FromEvent(IntPtr hwnd, int objectId, int childId, out ILoParagraph? paragraph)
        {
            EventCalls++;
            Now += 1;
            paragraph = EventHr < 0 ? null : OnEvent();
            return EventHr;
        }

        public int FromFocusWalk(IntPtr fg, out ILoParagraph? paragraph)
        {
            WalkCalls++;
            Now += 1;
            paragraph = WalkHr < 0 ? null : OnWalk();
            return WalkHr;
        }
    }

    private readonly FakeBackend _b = new();
    private readonly ConcurrentQueue<string> _log = new();
    private readonly List<IntPtr> _prefetch = new();
    private readonly LibreOfficeText _lo;
    private readonly List<Action> _mta = new();   // releases handed to an MTA thread by Dispose (run by the test)

    public LibreOfficeTextTests()
    {
        _lo = new LibreOfficeText(_b, enabled: true, m => _log.Enqueue(m), w => _prefetch.Add(w), onMta: a => _mta.Add(a));
    }

    private void Focus(IntPtr root = default) => _lo.NoteFocus(root == default ? Fg : root, Para, -4, 0);

    // ---- step order -------------------------------------------------------------------------------------------

    [Fact]
    public void Password_First_No_Text_Call()
    {
        var p = _b.New();
        p.StateValue = Focused | Ia2.STATE_SYSTEM_PROTECTED;
        _b.OnEvent = () => p;
        Focus();
        var r = _lo.Read(Fg);
        Assert.Equal(ReadStatus.Password, r.Status);
        Assert.Equal("ia2", r.Source);
        Assert.Equal(1, p.Calls);          // accState only
        Assert.Equal(0, p.TextCalls);
        Assert.Equal(ReadStatus.Password, _lo.ReadBefore(Fg, 4).Status);
        Assert.Equal(0, p.TextCalls);
    }

    [Fact]
    public void Unfocused_Object_Gets_One_Walk_Then_Unavailable()
    {
        var a = _b.New(); a.StateValue = 0;
        var w = _b.New(); w.StateValue = 0;
        _b.OnEvent = () => a;
        _b.OnWalk = () => w;
        Focus();
        var r = _lo.Read(Fg);
        Assert.Equal(ReadStatus.Unavailable, r.Status);
        Assert.Equal(1, _b.EventCalls);
        Assert.Equal(1, _b.WalkCalls);
        Assert.Equal(0, a.TextCalls + w.TextCalls);
        Assert.True(a.Disposed && w.Disposed);   // stale objects are not kept
    }

    [Fact]
    public void Unfocused_Event_Object_Walk_Finds_The_Focused_One()
    {
        var a = _b.New(); a.StateValue = 0;
        _b.OnEvent = () => a;
        Focus();
        var r = _lo.Read(Fg);
        Assert.Equal(ReadStatus.Ok, r.Status);
        Assert.Equal(1, _b.WalkCalls);
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.Equal(1, _b.WalkCalls);    // the walked object is cached for this seq
        Assert.Equal(1, _b.EventCalls);
    }

    [Fact]
    public void No_Focus_Slot_Resolves_By_The_Walk()
    {
        var r = _lo.Read(Fg);
        Assert.Equal(ReadStatus.Ok, r.Status);
        Assert.Equal(0, _b.EventCalls);
        Assert.Equal(1, _b.WalkCalls);
    }

    [Fact]
    public void Failed_Walk_Is_Unavailable_And_Keeps_Ia2_On()
    {
        _b.WalkHr = Ia2.E_FAIL;   // e.g. accFocus lost behind an infobar
        for (int i = 0; i < 10; i++) Assert.Equal(("ia2", ReadStatus.Unavailable), (_lo.Read(Fg).Source, _lo.Read(Fg).Status));
        Assert.False(_lo.IsOff(Fg));
        Assert.False(_lo.IsLive(Fg));
    }

    [Theory]
    [InlineData(Ia2.ROLE_PARAGRAPH, true)]
    [InlineData(Ia2.ROLE_HEADING, true)]
    [InlineData(Ia2.ROLE_EDITBAR, false)]
    [InlineData(0, false)]
    public void Role_Filter(int role, bool ok)
    {
        var expected = ok ? ReadStatus.Ok : ReadStatus.Unavailable;
        var p = _b.New();
        p.Role = role;
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal(expected, _lo.Read(Fg).Status);
        if (expected != ReadStatus.Ok) Assert.Equal(0, p.TextCalls);
    }

    [Fact]
    public void Caret_Minus_One_Is_Unavailable()
    {
        var p = _b.New("abc", caret: -1);
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal(ReadStatus.Unavailable, _lo.Read(Fg).Status);
        Assert.Equal(0, p.TextCalls);
        Assert.False(_lo.IsLive(Fg));
    }

    [Fact]
    public void Non_Empty_Selection_Is_Selection_Without_Text()
    {
        var p = _b.New();
        p.Sel = (3, 9);
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal(ReadStatus.Selection, _lo.Read(Fg).Status);
        Assert.Equal(0, p.TextCalls);
        Assert.True(_lo.IsLive(Fg));
        p.Sel = (5, 5);   // a collapsed selection is the caret
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
    }

    // ---- the window ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1000, 0)]
    [InlineData(1000, 1000)]
    [InlineData(1000, 700)]
    [InlineData(1000, 300)]
    [InlineData(40, 20)]
    public void Window_Clamps_And_Matches_ContextExtractor(int n, int c)
    {
        string content = string.Concat(Enumerable.Repeat("abc ", n / 4 + 1)).Substring(0, n);
        var p = _b.New(content, c);
        _b.OnEvent = () => p;
        Focus();
        var r = _lo.Read(Fg);
        Assert.Equal(ReadStatus.Ok, r.Status);
        int lo = Math.Max(0, c - 600), hi = Math.Min(n, c + 300);
        if (hi > lo) Assert.Equal((lo, hi), p.LastText);
        else Assert.Equal(0, p.TextCalls);
        string text = content.Substring(lo, hi - lo);
        var want = ContextExtractor.Extract(text, c - lo, lo > 0, hi < n);
        Assert.Equal(want.Valid, r.Ctx.Valid);
        Assert.Equal(want.Prefix, r.Ctx.Prefix);
        Assert.Equal(want.Suffix, r.Ctx.Suffix);
        Assert.Equal(want.NextChar, r.Ctx.NextChar);
        Assert.Equal(want.Context, r.Ctx.Context);
        Assert.Equal(want.Separators, r.Ctx.Separators);
        Assert.Equal(want.AtSentenceStart, r.Ctx.AtSentenceStart);
        Assert.NotNull(r.Window);
        Assert.Equal(text, r.Window!.Text);
        Assert.Equal(c - lo, r.Window.Caret);
        Assert.Equal(lo > 0, r.Window.TruncStart);
        Assert.Equal(hi < n, r.Window.TruncEnd);
        Assert.Equal(-1, r.Window.Base);
    }

    [Fact]
    public void Caret_Inside_A_Word_Gives_Prefix_And_Suffix()
    {
        var p = _b.New("la maison est", caret: 6);
        _b.OnEvent = () => p;
        Focus();
        var r = _lo.Read(Fg);
        Assert.Equal(ReadStatus.Ok, r.Status);
        Assert.Equal("mai", r.Ctx.Prefix);
        Assert.Equal("son", r.Ctx.Suffix);
    }

    [Fact]
    public void Text_Length_Mismatch_Is_Unavailable()
    {
        var p = _b.New("la maison ");
        p.DropOneChar = 1;
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal(ReadStatus.Unavailable, _lo.Read(Fg).Status);
        Assert.Contains("ia2: offset mismatch", _log);
    }

    [Fact]
    public void Foreground_Changed_During_Read_Is_Stale()
    {
        var p = _b.New();
        p.DuringText = () => _b.ForegroundWindow = Fg2;
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal(ReadStatus.Stale, _lo.Read(Fg).Status);
    }

    // ---- HRESULT classes, breaker -----------------------------------------------------------------------------

    [Theory]
    [InlineData(Ia2.RPC_E_CALL_REJECTED)]
    [InlineData(Ia2.CO_E_OBJNOTCONNECTED)]
    [InlineData(Ia2.RPC_E_DISCONNECTED)]
    [InlineData(Ia2.E_FAIL)]
    public void Normal_Failure_Drops_The_Cache_Not_The_Breaker(int hr)
    {
        _b.OnEvent = () => { var q = _b.New(); q.Hr["Caret"] = hr; return q; };
        Focus();
        for (int i = 0; i < 6; i++) Assert.Equal(ReadStatus.Unavailable, _lo.Read(Fg).Status);
        Assert.Equal(6, _b.EventCalls);      // each failure dropped the cached object: re-resolved every time
        Assert.All(_b.Made, q => Assert.True(q.Disposed));
        _b.OnEvent = () => _b.New();
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.DoesNotContain(_log, l => l.Contains("off"));
    }

    [Fact]
    public void Five_Structural_Failures_Switch_The_Pid_Off()
    {
        _b.EventHr = Ia2.E_NOINTERFACE;
        Focus();
        for (int i = 0; i < 4; i++) Assert.Equal("ia2", _lo.Read(Fg).Source);
        Assert.False(_lo.IsOff(Fg));
        Assert.Equal(ReadStatus.Unavailable, _lo.Read(Fg).Status);
        Assert.True(_lo.IsOff(Fg));
        Assert.Contains(_log, l => l.StartsWith("ia2: off for pid 7 (5 structural", StringComparison.Ordinal));
        int calls = _b.EventCalls;
        var r = _lo.Read(Fg);
        Assert.Equal(("skip", ReadStatus.Unavailable), (r.Source, r.Status));
        Assert.Equal(calls, _b.EventCalls);
    }

    [Fact]
    public void Structural_Run_Is_Reset_By_A_Success()
    {
        Focus();
        for (int round = 0; round < 3; round++)
        {
            _b.EventHr = Ia2.E_NOINTERFACE;
            for (int i = 0; i < 4; i++) _lo.Read(Fg);
            _b.EventHr = 0;
            Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
            Focus();   // a new seq: the next read resolves again
        }
        Assert.False(_lo.IsOff(Fg));
    }

    [Fact]
    public void Proxy_Not_Registered_Switches_Off_Globally()
    {
        _b.EventHr = Ia2.REGDB_E_IIDNOTREG;
        Focus();
        _lo.Read(Fg);
        Assert.Contains(_log, l => l.StartsWith("ia2: off (proxy not registered", StringComparison.Ordinal));
        _b.Pid = 8;      // another LibreOffice: still off
        _b.EventHr = 0;
        Assert.Equal("skip", _lo.Read(Fg).Source);
        Assert.True(_lo.IsOff(Fg));
    }

    [Fact]
    public void Server_Died_Forgets_The_Pid()
    {
        var p = _b.New();
        p.Hr["State"] = Ia2.RPC_E_SERVER_DIED_DNE;
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal(ReadStatus.Unavailable, _lo.Read(Fg).Status);
        Assert.Contains(_log, l => l.StartsWith("ia2: pid 7 forgotten", StringComparison.Ordinal));
        Assert.True(p.Disposed);
        _b.Pid = 9;      // LibreOffice restarted
        _b.OnEvent = () => _b.New();
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.Equal(2, _b.VersionCalls);
        Assert.Contains("ia2: LibreOffice 26.8.3.1 pid 9", _log);
    }

    [Fact]
    public void A_Stuck_Call_Switches_Off()
    {
        var p = _b.New();
        p.DuringText = () => _b.Now += 2500;
        _b.OnEvent = () => p;
        Focus();
        _lo.Read(Fg);
        Assert.True(_lo.IsOff(Fg));
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 10 s", _log);   // S2b: timed
        Assert.Equal("skip", _lo.Read(Fg).Source);
        Assert.False(_lo.IsLive(Fg));
        Assert.True(p.Disposed);
    }

    [Fact]
    public void A_Call_Still_In_Flight_Past_The_Stuck_Limit_Switches_Off_The_Next_Caller()
    {
        var p = _b.New();
        ReadResult inner = default;
        p.DuringText = () =>
        {
            p.DuringText = null;
            _b.Now += 2500;                 // the outer read is still inside get_text
            inner = _lo.Read(Fg);           // another worker arrives
        };
        _b.OnEvent = () => p;
        Focus();
        _lo.Read(Fg);
        Assert.Equal("skip", inner.Source);
        Assert.True(_lo.IsOff(Fg));
    }

    [Fact]
    public void Three_Timeouts_In_A_Row_Switch_Off_A_Fast_Read_Resets()
    {
        var p = _b.New();
        _b.OnEvent = () => p;
        Focus();
        _lo.Read(Fg);
        _b.Now += LibreOfficeText.PidGraceMs;   // past the start grace
        p.Cost = 40;     // 6 calls ≈ 240 ms > 150
        _lo.Read(Fg); _lo.Read(Fg);
        p.Cost = 0;
        _lo.Read(Fg);    // within budget: the run restarts
        p.Cost = 40;
        _lo.Read(Fg); _lo.Read(Fg);
        Assert.False(_lo.IsOff(Fg));
        _lo.Read(Fg);
        Assert.True(_lo.IsOff(Fg));
        Assert.Contains("ia2: off for pid 7 (3 timeouts), retry in 10 s", _log);   // S2b: timed
    }

    /// <summary>VM run 2026-11-27: the bridge's slow first answers (300–420 ms, three at once) switched IA2 off for the
    /// session. Slow calls in the pid's first seconds are not counted; overlapping slow calls count once.</summary>
    [Fact]
    public void Slow_Start_And_Overlapping_Slow_Calls_Do_Not_Switch_Off()
    {
        var p = _b.New();
        _b.OnEvent = () => p;
        Focus();
        p.Cost = 40;
        for (int i = 0; i < 5; i++) _lo.Read(Fg);              // slow, but within the start grace
        Assert.False(_lo.IsOff(Fg));
        Assert.Contains(_log, l => l.StartsWith("ia2: slow call at start", StringComparison.Ordinal));
        _b.Now += LibreOfficeText.PidGraceMs;
        // three slow calls in flight together (each starts while the others run): one episode
        int depth = 0;
        p.DuringText = () => { if (depth++ < 2) _lo.Read(Fg); };
        _lo.Read(Fg);
        p.DuringText = null;
        Assert.False(_lo.IsOff(Fg));
        _lo.Read(Fg); _lo.Read(Fg);                              // two more sequential slow calls: 3 in a row
        Assert.True(_lo.IsOff(Fg));
    }

    [Fact]
    public void A_New_Pid_Starts_Afresh()
    {
        _b.EventHr = Ia2.E_NOINTERFACE;
        Focus();
        for (int i = 0; i < 5; i++) _lo.Read(Fg);
        Assert.True(_lo.IsOff(Fg));
        _b.Pid = 8;
        _b.EventHr = 0;
        Assert.False(_lo.IsOff(Fg));
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.True(_lo.IsLive(Fg));
    }

    // ---- cache, leases, focus seq -------------------------------------------------------------------------------

    [Fact]
    public void NoteFocus_Seq_Invalidates_The_Cache()
    {
        Focus();
        _lo.Read(Fg);
        _lo.Read(Fg);
        Assert.Equal(1, _b.EventCalls);
        var first = _b.Made[0];
        long seq = _lo.FocusSeq;
        Focus();
        Assert.Equal(seq + 1, _lo.FocusSeq);
        Assert.False(first.Disposed);       // the hook never releases: the next worker call does
        _lo.Read(Fg);
        Assert.Equal(2, _b.EventCalls);
        Assert.True(first.Disposed);
        Assert.Equal(new[] { Fg, Fg }, _prefetch);
    }

    [Fact]
    public void Release_During_Use_Frees_After_The_Last_Holder()
    {
        var a = _b.New("la maison ");
        var b = _b.New("le chat ");
        _b.OnEvent = () => a;
        Focus();
        ReadResult inner = default;
        a.DuringText = () =>
        {
            a.DuringText = null;
            Focus();                        // a new paragraph while the outer read uses « a »
            _b.OnEvent = () => b;
            inner = _lo.Read(Fg);           // supersedes « a » in the cache
            Assert.False(a.Disposed);       // the outer read still holds it
        };
        var outer = _lo.Read(Fg);
        Assert.Equal(ReadStatus.Ok, outer.Status);
        Assert.Equal("la maison ", outer.Window!.Text);
        Assert.Equal("le chat ", inner.Window!.Text);
        Assert.True(a.Disposed);            // released by the last holder
        Assert.False(b.Disposed);           // cached
        _lo.Dispose();
        Assert.False(b.Disposed);           // never released on the disposing (UI, STA) thread …
        Assert.Single(_mta);
        _mta[0]();                          // … but handed to an MTA thread
        Assert.True(b.Disposed);
    }

    [Fact]
    public void Prefetch_Resolves_Without_Text_And_Warms_The_Read()
    {
        Focus();
        Assert.Equal(new[] { Fg }, _prefetch);
        _lo.Prefetch(Fg);
        Assert.Equal(1, _b.EventCalls);
        Assert.Equal(0, _b.Made[0].Calls);
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.Equal(1, _b.EventCalls);
        Assert.Contains("ia2: LibreOffice 26.8.3.1 pid 7", _log);
        Assert.Equal(1, _b.VersionCalls);
    }

    // ---- password probe -------------------------------------------------------------------------------------------

    [Fact]
    public void Probe_Cache_Hit_Costs_No_Com_Call()
    {
        Focus();
        Assert.Equal(ProbeStatus.NotPassword, _lo.ProbePassword(Fg));
        int calls = _b.ComCalls;
        for (int i = 0; i < 50; i++) Assert.Equal(ProbeStatus.NotPassword, _lo.ProbePassword(Fg));
        Assert.Equal(calls, _b.ComCalls);
        Assert.True(_lo.IsLive(Fg));
    }

    [Fact]
    public void Probe_Recomputes_On_New_Seq_Fg_Or_Pid()
    {
        Focus();
        _lo.ProbePassword(Fg);
        int calls = _b.ComCalls;
        Focus();                                           // new seq
        _lo.ProbePassword(Fg);
        Assert.True(_b.ComCalls > calls);
        calls = _b.ComCalls;
        Assert.Null(_lo.ProbePassword(Fg2));               // no slot for that window: today's UIA probe
        Assert.Equal(calls, _b.ComCalls);
        Focus(Fg2);
        _b.ForegroundWindow = Fg2;
        Assert.Equal(ProbeStatus.NotPassword, _lo.ProbePassword(Fg2));
        Assert.True(_b.ComCalls > calls);
        calls = _b.ComCalls;
        _b.Pid = 8;                                        // another soffice behind the same handle
        Assert.Equal(ProbeStatus.NotPassword, _lo.ProbePassword(Fg2));
        Assert.True(_b.ComCalls > calls);
    }

    [Fact]
    public void Probe_Protected_Is_Password_NonParagraph_Unavailable()
    {
        var p = _b.New();
        p.StateValue = Ia2.STATE_SYSTEM_PROTECTED | Focused;
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal(ProbeStatus.Password, _lo.ProbePassword(Fg));
        Assert.Equal(0, p.TextCalls);

        var q = _b.New();
        q.Role = 0x402;   // a dialog's field, a toolbar: not text we follow
        _b.OnEvent = () => q;
        Focus();
        Assert.Equal(ProbeStatus.Unavailable, _lo.ProbePassword(Fg));
        int calls = _b.ComCalls;
        Assert.Equal(ProbeStatus.Unavailable, _lo.ProbePassword(Fg));   // cached too: silence costs nothing
        Assert.Equal(calls, _b.ComCalls);
    }

    [Fact]
    public void Probe_Without_Slot_Is_Null_And_Calls_Nothing()
    {
        Assert.Null(_lo.ProbePassword(Fg));
        Assert.Equal(0, _b.ComCalls);
    }

    // ---- caret point ---------------------------------------------------------------------------------------------

    [Fact]
    public void CaretPoint_Inside_At_End_And_Empty_Paragraph()
    {
        var p = _b.New("abcdef", caret: 2);
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal((120, 220), _lo.CaretPoint(Fg));     // extents(2): x 120, y 200 + h 20
        p.Caret = 6;
        Assert.Equal((160, 220), _lo.CaretPoint(Fg));     // extents(5) right edge: 150 + 10
        p.Content = ""; p.Caret = 0;
        Assert.Equal((100, 324), _lo.CaretPoint(Fg));     // accLocation: left, top + height
        Assert.Equal(0, p.TextCalls);
    }

    [Fact]
    public void CaretPoint_Outside_The_Window_Is_Null()
    {
        Focus();
        _b.Rect = (500, 500, 900, 900);
        Assert.Null(_lo.CaretPoint(Fg));
        Assert.Null(_lo.CachedCaretPoint(Fg));
    }

    [Fact]
    public void CaretPoint_Fallback_Cache_Lasts_One_Second_And_One_Seq()
    {
        var p = _b.New("abcdef", caret: 2);
        _b.OnEvent = () => p;
        Focus();
        var pt = _lo.CaretPoint(Fg);
        Assert.Equal(pt, _lo.CachedCaretPoint(Fg));
        Assert.Null(_lo.CachedCaretPoint(Fg2));
        _b.Now += 1001;
        Assert.Null(_lo.CachedCaretPoint(Fg));
        _lo.CaretPoint(Fg);
        Focus();
        Assert.Null(_lo.CachedCaretPoint(Fg));
    }

    [Fact]
    public void CaretPointBounded_Fast_Is_The_Worker_Answer()
    {
        var p = _b.New("abcdef", caret: 2);
        _b.OnEvent = () => p;
        Focus();
        Assert.Equal((120, 220, LibreOfficeText.CaretFrom.Fresh), _lo.CaretPointBounded(Fg, 2000));
        Assert.Equal((120, 220, LibreOfficeText.CaretFrom.Recent), _lo.CaretPointBounded(Fg, 2000));   // < 100 ms: throttled
        int calls = _b.ComCalls;
        _b.Now += LibreOfficeText.CaretThrottleMs + 1;
        Assert.Equal((120, 220, LibreOfficeText.CaretFrom.Fresh), _lo.CaretPointBounded(Fg, 2000));
        Assert.True(_b.ComCalls > calls);
        _lo.Dispose();
    }

    [Fact]
    public void A_Successful_Prefetch_Resets_The_Structural_Run()
    {
        Focus();
        for (int round = 0; round < 3; round++)
        {
            _b.EventHr = Ia2.E_NOINTERFACE;
            for (int i = 0; i < 4; i++) _lo.Read(Fg);
            _b.EventHr = 0;
            Focus();
            _lo.Prefetch(Fg);                                 // resolved: the run is over (no read needed)
            Focus();
        }
        Assert.False(_lo.IsOff(Fg));
    }

    /// <summary>Review S3–S7: a focus object never found gives no IA2 answer after ProbeGiveUp tries (the UIA probe takes
    /// over, so a misbehaving window does not drop every echo for good); a new focus object is asked again.</summary>
    [Fact]
    public void Probe_Gives_Up_On_A_Focus_Object_Never_Found()
    {
        _b.OnEvent = () => { var q = _b.New(); q.StateValue = 0; return q; };
        _b.OnWalk = () => { var q = _b.New(); q.StateValue = 0; return q; };
        Focus();
        for (int i = 1; i < LibreOfficeText.ProbeGiveUp; i++) Assert.Equal(ProbeStatus.Unavailable, _lo.ProbePassword(Fg));
        Assert.Null(_lo.ProbePassword(Fg));                   // the last try: no answer
        int calls = _b.ComCalls;
        Assert.Null(_lo.ProbePassword(Fg));                   // then no COM call at all for this focus object
        Assert.Equal(calls, _b.ComCalls);
        Assert.Single(_log, l => l.Contains("no focused object after"));
        _b.OnEvent = () => _b.New();
        Focus();                                               // another focus object
        Assert.Equal(ProbeStatus.NotPassword, _lo.ProbePassword(Fg));
    }

    /// <summary>VM L11: LibreOffice's dialogs (SALSUBFRAME, title not « LibreOffice Writer ») get their focus objects
    /// recorded for the password probe, without a prefetch; Calc / Impress / Draw do not.</summary>
    [Fact]
    public void Dialogs_Are_Recorded_For_The_Probe_Without_Prefetch()
    {
        Assert.True(HostApps.IsFrameClass("SALFRAME"));
        Assert.True(HostApps.IsFrameClass("SALSUBFRAME"));
        Assert.False(HostApps.IsFrameClass("Notepad"));
        Assert.False(HostApps.IsFrameClass(null));
        Assert.True(HostApps.Ia2Focus(HostApps.ModuleOf("soffice.bin", "Saisir le mot de passe")));
        Assert.True(HostApps.Ia2Focus(HostApps.ModuleOf("soffice.bin", "Sans nom 1 — LibreOffice Writer")));
        Assert.False(HostApps.Ia2Focus(HostApps.ModuleOf("soffice.bin", "Sans nom 1 — LibreOffice Calc")));
        Assert.False(HostApps.Ia2Focus(HostApps.ModuleOf("soffice.bin", "Sans nom 1 — LibreOffice Impress")));
        _lo.NoteFocus(Fg, Para, -4, 0, prefetch: false);
        Assert.Empty(_prefetch);
        var p = _b.New(); p.Role = 0x2A; p.StateValue = Ia2.STATE_SYSTEM_PROTECTED | Focused;   // an edit with PROTECTED
        _b.OnEvent = () => p;
        Assert.Equal(ProbeStatus.Password, _lo.ProbePassword(Fg));
        Assert.Equal(0, p.TextCalls);
    }

    /// <summary>VM L11: in a LibreOffice dialog whose focused field cannot be found (no focus event of its own, the walk
    /// finds nothing) the answer is Password — never cached, never given up — so the typed password is never logged.</summary>
    [Fact]
    public void Dialog_With_No_Focused_Object_Is_Password()
    {
        _b.OnEvent = () => { var q = _b.New(); q.StateValue = 0; return q; };
        _b.OnWalk = () => { var q = _b.New(); q.StateValue = 0; return q; };
        _lo.NoteFocus(Fg, Para, -4, 0, prefetch: false);
        for (int i = 0; i < LibreOfficeText.ProbeGiveUp + 2; i++) Assert.Equal(ProbeStatus.Password, _lo.ProbePassword(Fg, dialog: true));
        Assert.False(_lo.IsLive(Fg));                          // IA2 did not answer: not « live »
        Assert.DoesNotContain(_log, l => l.Contains("no focused object after"));
        _b.OnEvent = () => { var q = _b.New(); q.Role = 0x2B; return q; };   // a focus event names a button
        _lo.NoteFocus(Fg, Para, -4, 0, prefetch: false);
        Assert.Equal(ProbeStatus.Unavailable, _lo.ProbePassword(Fg, dialog: true));
    }

    [Fact]
    public void Probe_Cached_Answer_Is_Logged_Once()
    {
        Focus();
        for (int i = 0; i < 5; i++) Assert.Equal(ProbeStatus.NotPassword, _lo.ProbePassword(Fg));
        Assert.Single(_log, l => l.StartsWith("probe ia2 cached", StringComparison.Ordinal));
    }

    [Fact]
    public void Probe_Does_Not_Cache_When_Nothing_Focused_Was_Found()
    {
        _b.OnEvent = () => { var q = _b.New(); q.StateValue = 0; return q; };
        _b.OnWalk = () => { var q = _b.New(); q.StateValue = 0; return q; };
        Focus();
        Assert.Equal(ProbeStatus.Unavailable, _lo.ProbePassword(Fg));
        int calls = _b.ComCalls;
        _b.OnEvent = () => _b.New();                          // the focus settled on the paragraph
        Assert.Equal(ProbeStatus.NotPassword, _lo.ProbePassword(Fg));
        Assert.True(_b.ComCalls > calls);
    }

    // ---- read-back, --no-ia2 ---------------------------------------------------------------------------------------

    [Fact]
    public void ReadBefore_Four()
    {
        var p = _b.New("Bonjour !", caret: 9);
        _b.OnEvent = () => p;
        Focus();
        var r = _lo.ReadBefore(Fg, 4);
        Assert.Equal(ReadStatus.Ok, r.Status);
        Assert.Equal("ur !", r.Before);
        Assert.Equal((5, 9), p.LastText);
        p.Caret = 2;
        Assert.Equal("Bo", _lo.ReadBefore(Fg, 4).Before);
        p.Caret = 0;
        Assert.Equal("", _lo.ReadBefore(Fg, 4).Before);
        p.Caret = -1;
        Assert.Equal(ReadStatus.Unavailable, _lo.ReadBefore(Fg, 4).Status);
    }

    [Fact]
    public void No_Ia2_Never_Calls_The_Backend()
    {
        var lo = new LibreOfficeText(_b, enabled: false, m => _log.Enqueue(m), w => _prefetch.Add(w));
        lo.NoteFocus(Fg, Para, -4, 0);
        Assert.Equal(("skip", ReadStatus.Unavailable), (lo.Read(Fg).Source, lo.Read(Fg).Status));
        Assert.Equal(ReadStatus.Unavailable, lo.ReadBefore(Fg, 4).Status);
        Assert.Null(lo.CaretPoint(Fg));
        Assert.Null(lo.CachedCaretPoint(Fg));
        Assert.Null(lo.ProbePassword(Fg));
        Assert.Equal(("skip", ReadStatus.Unavailable), (lo.ReadSelection(Fg).Source, lo.ReadSelection(Fg).Status));   // phase 2
        lo.Prefetch(Fg);
        Assert.False(lo.IsLive(Fg));
        Assert.True(lo.IsOff(Fg));
        Assert.Empty(_prefetch);
        Assert.Equal(0, _b.ComCalls + _b.PidCalls + _b.VersionCalls);
        Assert.Empty(_log);
    }

    [Fact]
    public void Log_Never_Carries_Text()
    {
        const string secret = "Secret4821";
        var p = _b.New($"mot {secret} ");
        p.Cost = 10;   // slow enough for the phases line
        _b.OnEvent = () => p;
        Focus();
        _lo.Read(Fg);
        _lo.ReadBefore(Fg, 4);
        p.DropOneChar = 1;
        _lo.Read(Fg);
        Assert.Contains(_log, l => l.StartsWith("ia2 read phases:", StringComparison.Ordinal));
        Assert.DoesNotContain(_log, l => l.Contains(secret, StringComparison.Ordinal) || l.Contains("mot", StringComparison.Ordinal));
    }

    // ---- settle policy ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(' ', true)]
    [InlineData('\r', true)]
    [InlineData('\t', true)]
    [InlineData(' ', true)]
    [InlineData('.', true)]
    [InlineData(',', true)]
    [InlineData('!', true)]
    [InlineData('?', true)]
    [InlineData(';', true)]
    [InlineData(':', true)]
    [InlineData('«', true)]
    [InlineData('\'', false)]
    [InlineData('’', false)]
    [InlineData('-', false)]
    [InlineData('a', false)]
    [InlineData('é', false)]
    [InlineData('1', false)]
    public void Settle_Separator_Set(char c, bool expected) => Assert.Equal(expected, SettlePolicy.IsSeparator(c));

    /// <summary>A settle read agrees with the keys up to LibreOffice's capital at a sentence start and its ’; any other
    /// change (a letter, a length, a word after the caret) re-seeds.</summary>
    [Theory]
    [InlineData("", "", "jtaime", "", "jtaime", true)]          // nothing changed
    [InlineData("", "", "Jtaime", "", "jtaime", true)]          // AutoCorrect: capital at the sentence start
    [InlineData("", "", "mon cousin", "", "Mon cousin", true)]
    [InlineData("", "", "j’ai", "", "j'ai", true)]              // AutoCorrect: typographic apostrophe
    [InlineData("", "", "très", "", "tres", false)]             // AutoCorrect replaced a word: seed
    [InlineData("", "", "il y a", "", "ilya", false)]           // another length / word count
    [InlineData("ma", "", "la", "ma", "la", true)]
    [InlineData("ma", "", "la", "mai", "la", false)]
    [InlineData("", "son", "la", "", "la", false)]              // a word after the caret
    public void Settle_Agreement(string docPrefix, string docSuffix, string docCtx, string cur, string ctx, bool expected)
    {
        static string[] W(string s) => s.Length == 0 ? Array.Empty<string>() : s.Split(' ');
        Assert.Equal(expected, SettlePolicy.Agrees(docPrefix, docSuffix, W(docCtx), cur, W(ctx)));
    }

    [Theory]
    [InlineData(true, false, false, false, true)]
    [InlineData(false, false, false, false, false)]   // not live
    [InlineData(true, true, false, false, false)]     // our auto-space is armed
    [InlineData(true, false, true, false, false)]     // a D22 read-back is pending
    [InlineData(true, false, false, true, false)]     // a ≈ / negation target is armed
    public void Settle_Guards(bool live, bool autoSpace, bool readBack, bool correction, bool expected)
    {
        Assert.Equal(expected, SettlePolicy.ShouldAsk(live, autoSpace, readBack, correction));
        Assert.Equal(expected, SettlePolicy.ShouldAsk(' ', live, autoSpace, readBack, correction));
        Assert.False(SettlePolicy.ShouldAsk('a', live, autoSpace, readBack, correction));
    }

    // ---- phase 2, S2: ReadSelection over a fake document ---------------------------------------------------------------

    private static readonly string[] Three =
    {
        "Le chat dort dans la maison. Il fait beau aujourd'hui.",
        "La petite fille joue dans le jardin avec son chien.",
        "Les oiseaux chantent dans les arbres du parc.",
    };

    /// <summary>The document's focused paragraph (the cursor's), named by the focus event.</summary>
    private FakePara FocusOn(FakeDoc d, int i, int caret)
    {
        var f = d.Focus(i, caret);
        _b.OnEvent = () => f;
        Focus();
        return f;
    }

    private int TextCallsOf(FakeDoc d) => d.Views.Sum(v => v.TextCalls);

    [Fact]
    public void Selection_In_One_Paragraph_Has_Its_D26_Context()
    {
        var d = new FakeDoc(_b, Three).Select(0, 5, 0, 16);   // « Le ch[at dort dan]s la maison »
        FocusOn(d, 0, 16);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal((ReadStatus.Ok, "ia2", false), (r.Status, r.Source, r.Partial));
        Assert.Equal("at dort dan", r.Selected);
        Assert.Equal("Le ch", r.Before);
        Assert.Equal("s la maison. Il fait beau aujourd'hui.", r.After);
        Assert.Contains(_log, l => l.StartsWith("selection ia2: 1 paragraphs, 0 hops", StringComparison.Ordinal) && l.EndsWith("complete", StringComparison.Ordinal));
        Assert.True(_lo.IsLive(Fg));
    }

    [Fact]
    public void Context_Is_Clamped_To_48_Characters_In_Its_Own_Paragraph()
    {
        string t = new string('a', 100) + " milieu " + new string('b', 100);
        var d = new FakeDoc(_b, "avant", t, "après").Select(1, 101, 1, 107);
        FocusOn(d, 1, 107);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal("milieu", r.Selected);
        Assert.Equal(SelectionExtend.Context, r.Before!.Length);
        Assert.Equal(SelectionExtend.Context, r.After!.Length);
        Assert.EndsWith("a ", r.Before);
        Assert.StartsWith(" b", r.After);
    }

    [Fact]
    public void Three_Paragraphs_Forward_And_Backward_Read_The_Same_Text()
    {
        const string expected = "chat dort dans la maison. Il fait beau aujourd'hui.\nLa petite fille joue dans le jardin avec son chien.\nLes oiseaux";
        var fwd = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        FocusOn(fwd, 2, 11);   // a forward drag: the cursor in the last paragraph
        var a = _lo.ReadSelection(Fg);
        var back = new FakeDoc(_b, Three).Select(0, 3, 2, 11, backward: true);
        FocusOn(back, 0, 3);   // a backward drag / Shift+Up: the cursor in the first one, its range reversed ([54,3])
        var b = _lo.ReadSelection(Fg);
        Assert.Equal(expected, a.Selected);
        Assert.Equal(expected, b.Selected);
        Assert.Equal(("Le ", " chantent dans les arbres du parc."), (a.Before, a.After));
        Assert.Equal((a.Before, a.After), (b.Before, b.After));
        Assert.False(a.Partial || b.Partial);
    }

    [Fact]
    public void An_Empty_Paragraph_Inside_Is_A_Blank_Line_Not_A_Gap()
    {
        var d = new FakeDoc(_b, "Les oiseaux chantent.", "", "Après l'école.").Select(0, 4, 2, 5);
        FocusOn(d, 2, 5);
        Assert.Equal("oiseaux chantent.\n\nAprès", _lo.ReadSelection(Fg).Selected);
        // an empty paragraph outside the selection reports no range: the walk stops there
        var e = new FakeDoc(_b, "", "Les oiseaux chantent.", "").Select(1, 0, 1, 21);
        FocusOn(e, 1, 21);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal("Les oiseaux chantent.", r.Selected);
        Assert.Equal(("", ""), (r.Before, r.After));
    }

    [Fact]
    public void A_Selection_Starting_At_Offset_Zero_Stops_At_The_Previous_Paragraph()
    {
        var d = new FakeDoc(_b, Three).Select(1, 0, 2, 3);
        FocusOn(d, 2, 3);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal("La petite fille joue dans le jardin avec son chien.\nLes", r.Selected);
        Assert.Equal("", r.Before);
        Assert.Contains(_log, l => l.StartsWith("selection ia2: 2 paragraphs, 2 hops", StringComparison.Ordinal));
        Assert.Equal(0, d.Views.Where(v => v.Index == 0).Sum(v => v.TextCalls));   // never read outside the selection
    }

    [Fact]
    public void Ctrl_A_Over_30_Paragraphs_Reads_Them_In_Order()
    {
        var texts = Enumerable.Range(1, 30).Select(i => $"Paragraphe {i}.").ToArray();
        var d = new FakeDoc(_b, texts).Select(0, 0, 29, texts[29].Length);
        FocusOn(d, 29, texts[29].Length);   // Ctrl+A: the cursor at the end
        var r = _lo.ReadSelection(Fg);
        Assert.Equal(string.Join("\n", texts), r.Selected);
        Assert.False(r.Partial);
        // 29 hops back, one more that finds no previous paragraph, one forward that finds no next one
        Assert.Contains(_log, l => l.StartsWith("selection ia2: 30 paragraphs, 31 hops", StringComparison.Ordinal));
    }

    [Fact]
    public void The_Hop_Limit_Gives_A_Partial_Read()
    {
        // the cursor at the start: the start is read, the end not reached → Ok, Partial (its own message)
        var texts = Enumerable.Range(0, LibreOfficeText.SelectionMaxHops + 50).Select(i => "mot").ToArray();
        var d = new FakeDoc(_b, texts).Select(0, 0, texts.Length - 1, 3, backward: true);
        FocusOn(d, 0, 0);
        var r = _lo.ReadSelection(Fg, maxChars: 1_000_000);
        Assert.Equal(ReadStatus.Ok, r.Status);
        Assert.True(r.Partial);
        Assert.Equal(LibreOfficeText.SelectionMaxHops, r.Selected!.Split('\n').Length);   // the focused one + 399 forward (one hop went to finding no previous paragraph)
        Assert.Contains(_log, l => l.EndsWith("partial(hops)", StringComparison.Ordinal));
    }

    /// <summary>Review S3–S5 (plan LS8): the walk back never reached the selection's start → Unavailable (the copy when
    /// allowed, else nothing), never only the end of a selection shown as if it were all of it.</summary>
    [Fact]
    public void The_Start_Not_Reached_Is_Unavailable()
    {
        var texts = Enumerable.Range(0, LibreOfficeText.SelectionMaxHops + 50).Select(i => "mot").ToArray();
        var d = new FakeDoc(_b, texts).Select(0, 0, texts.Length - 1, 3);
        FocusOn(d, texts.Length - 1, 3);
        Assert.Equal(ReadStatus.Unavailable, _lo.ReadSelection(Fg, maxChars: 1_000_000).Status);
        Assert.Contains(_log, l => l.EndsWith("start not reached (hops)", StringComparison.Ordinal));
        Assert.Equal(0, d.Views.Sum(v => v.TextCalls));
    }

    [Fact]
    public void The_Time_Budget_Gives_A_Partial_Read_And_Is_Not_A_Timeout()
    {
        var texts = Enumerable.Range(0, 200).Select(i => "mot").ToArray();
        var d = new FakeDoc(_b, texts).Select(0, 0, 199, 3, backward: true);
        d.HopCost = 10;   // 10 ms per hop: the 700 ms budget ends the walk after ≈ 70 hops
        FocusOn(d, 0, 0);
        _lo.Read(Fg);                                   // the pid's first call: the start grace is behind us after this
        _b.Now += LibreOfficeText.PidGraceMs;
        for (int i = 0; i < 4; i++)
        {
            var r = _lo.ReadSelection(Fg);
            Assert.True(r.Partial);
            Assert.Contains(_log, l => l.EndsWith("partial(time)", StringComparison.Ordinal));
        }
        // four walks of ≈ 700–710 ms, past the 150 ms read budget but within the walk's: never a timeout
        Assert.False(_lo.IsOff(Fg));
        Assert.DoesNotContain(_log, l => l.StartsWith("ia2: off", StringComparison.Ordinal));
    }

    [Fact]
    public void Past_The_Cap_The_Text_Is_Cut_Without_After_And_The_Walk_Stops()
    {
        var texts = Enumerable.Range(0, 100).Select(i => new string((char)('a' + i % 26), 499)).ToArray();
        var d = new FakeDoc(_b, texts).Select(0, 0, 99, 499, backward: true);
        FocusOn(d, 0, 0);   // the cursor at the start: the walk goes forward and stops once the cap is reached
        var r = _lo.ReadSelection(Fg);
        // cut at the last word boundary before the cap (here a line break: no spaces inside these paragraphs), Cut set
        Assert.True(r.Cut);
        Assert.InRange(r.Selected!.Length, LibreOfficeText.SelectionMaxChars - 500, LibreOfficeText.SelectionMaxChars);
        Assert.Contains(r.Selected!.Split('\n')[^1], texts);
        Assert.Equal("", r.After);
        Assert.False(r.Partial);
        Assert.Contains(_log, l => l.EndsWith("complete (cut)", StringComparison.Ordinal));
        Assert.True(d.Views.Count < 60, $"{d.Views.Count} paragraphs visited");   // 20 000 / 500 = 40, not the 100
        // Ctrl+A with the cursor at the end: the text still starts at the document's start (never only its end)
        var e = new FakeDoc(_b, texts).Select(0, 0, 99, 499);
        FocusOn(e, 99, 499);
        var all = _lo.ReadSelection(Fg);
        Assert.StartsWith(texts[0], all.Selected);
        Assert.True(all.Cut);
        Assert.InRange(all.Selected!.Length, LibreOfficeText.SelectionMaxChars - 500, LibreOfficeText.SelectionMaxChars);
        Assert.Equal("", all.After);
    }

    [Fact]
    public void A_Protected_Neighbour_Makes_The_Whole_Read_Password_With_No_Text_Call()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        d.States[1] = Ia2.STATE_SYSTEM_SELECTED | Ia2.STATE_SYSTEM_PROTECTED;
        FocusOn(d, 2, 11);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal(ReadStatus.Password, r.Status);
        Assert.Null(r.Selected);
        Assert.Equal(0, TextCallsOf(d));
    }

    [Fact]
    public void A_Non_Text_Neighbour_Gives_A_Partial_Read()
    {
        // the cursor at the start (a backward selection): the end is beyond a selected non-text object → Partial
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11, backward: true);
        d.Roles[1] = Ia2.ROLE_EDITBAR;
        FocusOn(d, 0, 3);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal((ReadStatus.Ok, true, Three[0].Substring(3)), (r.Status, r.Partial, r.Selected));
        Assert.Contains(_log, l => l.EndsWith("partial(role 0x409)", StringComparison.Ordinal));
        // the cursor at the end: the start is beyond it → Unavailable, never the tail alone
        var e = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        e.Roles[1] = Ia2.ROLE_EDITBAR;
        FocusOn(e, 2, 11);
        Assert.Equal(ReadStatus.Unavailable, _lo.ReadSelection(Fg).Status);
    }

    [Fact]
    public void A_Length_Mismatch_Is_Unavailable()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        var f = FocusOn(d, 2, 11);
        f.DropOneChar = 1;
        Assert.Equal(ReadStatus.Unavailable, _lo.ReadSelection(Fg).Status);
        Assert.Contains("ia2: offset mismatch (selection)", _log);
        // a range outside the paragraph: nothing trusted either, and no text call
        var e = new FakeDoc(_b, Three);
        e.Ranges[2].Add((0, 999));
        FocusOn(e, 2, 0);
        Assert.Equal(ReadStatus.Unavailable, _lo.ReadSelection(Fg).Status);
        Assert.Equal(0, TextCallsOf(e));
    }

    [Fact]
    public void A_Foreground_Change_During_The_Read_Is_Stale()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        var f = FocusOn(d, 2, 11);
        f.DuringText = () => _b.ForegroundWindow = Fg2;
        Assert.Equal(ReadStatus.Stale, _lo.ReadSelection(Fg).Status);
    }

    [Fact]
    public void Neighbours_Are_Disposed_The_Focused_Entry_Stays_Cached()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        var f = FocusOn(d, 2, 11);
        _lo.ReadSelection(Fg);
        Assert.All(d.Views.Where(v => !ReferenceEquals(v, f)), v => Assert.True(v.Disposed));
        Assert.False(f.Disposed);
        int resolves = _b.EventCalls;
        _lo.ReadSelection(Fg);
        Assert.Equal(resolves, _b.EventCalls);   // the focused paragraph came from the cache
    }

    [Fact]
    public void No_Selection_Is_Ok_And_Empty_Without_A_Hop()
    {
        var d = new FakeDoc(_b, Three);
        FocusOn(d, 1, 4);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal((ReadStatus.Ok, ""), (r.Status, r.Selected));
        Assert.Single(d.Views);   // no neighbour was asked for
    }

    [Fact]
    public void Edge_Mark_Reads_The_Previous_Paragraph_And_The_Caret_Read_Says_Selection()
    {
        // Shift+Down from the start of paragraph 0 (VM P0): paragraph 1 holds the cursor and an empty mark [0,0]
        var d = new FakeDoc(_b, Three).Select(0, 0, 1, 0);
        FocusOn(d, 1, 0);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal(Three[0], r.Selected);   // no trailing line break for the empty mark
        Assert.Equal(ReadStatus.Selection, _lo.Read(Fg).Status);
        // the mirror case: the cursor at the end of paragraph 1 (mark [n,n]), the selection in the next paragraph
        var e = new FakeDoc(_b, Three);
        e.Ranges[1].Add((Three[1].Length, Three[1].Length));
        e.Ranges[2].Add((0, 11));
        FocusOn(e, 1, Three[1].Length);
        Assert.Equal("Les oiseaux", _lo.ReadSelection(Fg).Selected);
        Assert.Equal(ReadStatus.Selection, _lo.Read(Fg).Status);
    }

    [Fact]
    public void Edge_Mark_Without_A_Neighbour_Selection_Is_No_Selection()
    {
        var d = new FakeDoc(_b, Three);
        d.Ranges[1].Add((0, 0));
        FocusOn(d, 1, 0);
        Assert.Equal("", _lo.ReadSelection(Fg).Selected);
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        // a plain caret (no mark) never hops: the caret read stays as cheap as phase 1
        var e = new FakeDoc(_b, Three);
        FocusOn(e, 1, 0);
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.Single(e.Views);
    }

    [Fact]
    public void Table_Block_Caret_Minus_One_Is_A_Selection_For_The_Caret_Read()
    {
        // VM P0: a block of cells gives the focused cell caret -1 with its whole range
        var d = new FakeDoc(_b, "case un", "case deux", "case trois", "case quatre").Select(0, 0, 3, 11);
        FocusOn(d, 3, -1);
        Assert.Equal(ReadStatus.Selection, _lo.Read(Fg).Status);
        Assert.Equal("case un\ncase deux\ncase trois\ncase quatre", _lo.ReadSelection(Fg).Selected);
    }

    [Fact]
    public void Two_Ranges_In_One_Paragraph_Are_Not_Glued()
    {
        var d = new FakeDoc(_b, "Le chat dort ici.");
        d.Ranges[0].Add((3, 7));
        d.Ranges[0].Add((8, 12));
        FocusOn(d, 0, 12);
        Assert.Equal("chat\ndort", _lo.ReadSelection(Fg).Selected);
    }

    [Fact]
    public void A_Com_Error_Mid_Walk_Is_Partial_A_Dead_Server_Is_Unavailable()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11, backward: true);
        d.NeighbourHr[1] = Ia2.E_FAIL;
        FocusOn(d, 0, 3);
        var r = _lo.ReadSelection(Fg);
        Assert.True(r.Partial);
        Assert.Equal(Three[0].Substring(3) + "\nLa petite fille joue dans le jardin avec son chien.", r.Selected);
        var b = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        b.NeighbourHr[1] = Ia2.E_FAIL;
        FocusOn(b, 2, 11);
        Assert.Equal(ReadStatus.Unavailable, _lo.ReadSelection(Fg).Status);   // the start not reached
        var e = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        e.NeighbourHr[2] = Ia2.RPC_E_SERVER_DIED;
        FocusOn(e, 2, 11);
        Assert.Equal(ReadStatus.Unavailable, _lo.ReadSelection(Fg).Status);
        Assert.Contains(_log, l => l.StartsWith("ia2: pid 7 forgotten", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Stuck_Walk_Still_Trips_The_Breaker()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        var f = FocusOn(d, 2, 11);
        f.DuringText = () => _b.Now += 2500;
        _lo.ReadSelection(Fg);
        Assert.True(_lo.IsOff(Fg));
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 10 s", _log);
        Assert.Equal("skip", _lo.ReadSelection(Fg).Source);
    }

    [Fact]
    public void Selection_Log_Never_Carries_Text()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        FocusOn(d, 2, 11);
        _lo.ReadSelection(Fg);
        Assert.DoesNotContain(_log, l => l.Contains("chat", StringComparison.Ordinal) || l.Contains("oiseaux", StringComparison.Ordinal));
    }

    [Fact]
    public void A_Cap_At_A_Paragraph_End_Keeps_Its_Last_Word()
    {
        var d = new FakeDoc(_b, "abcd efgh", "abcd efgh", "abcd efgh", "abcd efgh").Select(0, 0, 3, 9, backward: true);
        FocusOn(d, 0, 0);
        var r = _lo.ReadSelection(Fg, maxChars: 19);   // exactly two paragraphs and their line break
        Assert.True(r.Cut);
        Assert.Equal("abcd efgh\nabcd efgh", r.Selected);
        var e = new FakeDoc(_b, "abcd efgh", "abcd efgh").Select(0, 0, 1, 9, backward: true);
        FocusOn(e, 0, 0);
        Assert.Equal("abcd efgh\nabcd", _lo.ReadSelection(Fg, maxChars: 17).Selected);   // inside a word: back to a whole one
    }

    [Fact]
    public void A_Long_Selection_Read_Neither_Counts_Nor_Ends_The_Timeout_Run()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        var f = FocusOn(d, 2, 11);
        _lo.Read(Fg);
        _b.Now += LibreOfficeText.PidGraceMs;
        f.Cost = 40;                                   // caret reads of ≈ 160–200 ms: timeouts
        _lo.Read(Fg); _lo.Read(Fg);
        f.Cost = 0;
        d.HopCost = 150;                               // a 300 ms walk: within its allowance, slower than a caret read
        Assert.Equal(ReadStatus.Ok, _lo.ReadSelection(Fg).Status);
        Assert.False(_lo.IsOff(Fg));
        f.Cost = 40;
        _lo.Read(Fg);                                  // the third caret-read timeout in a row: the walk did not reset the run
        Assert.True(_lo.IsOff(Fg));
        Assert.Contains("ia2: off for pid 7 (3 timeouts), retry in 10 s", _log);
    }

    /// <summary>VM LS8: Ctrl+A on a long document — no focus event, the cursor's paragraph off-screen; the event object and
    /// the walk give a paragraph that is selected but no longer focused. The selection read starts from it; the caret read
    /// says « a selection » and never reads text from it.</summary>
    [Fact]
    public void A_Selected_Unfocused_Paragraph_Starts_The_Walk_Never_Caret_Text()
    {
        var texts = Enumerable.Range(1, 6).Select(i => $"Paragraphe {i}.").ToArray();
        var d = new FakeDoc(_b, texts).Select(0, 0, 5, texts[5].Length);
        _b.OnEvent = () => d.View(1);   // SELECTED, not FOCUSED
        _b.OnWalk = () => d.View(1);
        Focus();
        var r = _lo.ReadSelection(Fg);
        Assert.Equal((ReadStatus.Ok, false), (r.Status, r.Partial));
        Assert.Equal(string.Join("\n", texts), r.Selected);
        Assert.Equal(ReadStatus.Selection, _lo.Read(Fg).Status);
        // collapsed: the unfocused paragraph has no selection any more → Unavailable, still no text call
        var e = new FakeDoc(_b, texts);
        e.States[1] = 0;
        _b.OnEvent = () => e.View(1);
        _b.OnWalk = () => e.View(1);
        Focus();
        Assert.Equal(ReadStatus.Unavailable, _lo.Read(Fg).Status);
        Assert.Equal(0, e.Views.Sum(v => v.TextCalls));
        // selected but not focused and no range of its own: Unavailable, never caret text
        var f = new FakeDoc(_b, texts);
        f.States[1] = Ia2.STATE_SYSTEM_SELECTED;
        _b.OnEvent = () => f.View(1);
        _b.OnWalk = () => f.View(1);
        Focus();
        Assert.Equal(ReadStatus.Unavailable, _lo.Read(Fg).Status);
        Assert.Equal(0, f.Views.Sum(v => v.TextCalls));
    }

    /// <summary>Review S3–S5: a selected, unfocused object is accepted only from the accFocus walk, never the event slot's
    /// (possibly stale, cached) object: the event object is dropped and the walk's focused paragraph is used.</summary>
    [Fact]
    public void A_Selected_Unfocused_Event_Object_Is_Not_Used_The_Walk_Decides()
    {
        var texts = Enumerable.Range(1, 4).Select(i => $"Paragraphe {i}.").ToArray();
        var d = new FakeDoc(_b, texts).Select(0, 0, 3, texts[3].Length);
        var ev = d.View(1);                       // SELECTED, not FOCUSED (the event slot)
        var walked = d.Focus(3, texts[3].Length); // the walk finds the focused one
        _b.OnEvent = () => ev;
        _b.OnWalk = () => walked;
        Focus();
        var r = _lo.ReadSelection(Fg);
        Assert.Equal(string.Join("\n", texts), r.Selected);
        Assert.Equal((1, 1), (_b.EventCalls, _b.WalkCalls));
        Assert.True(ev.Disposed);                 // dropped, not used
        Assert.Equal(0, ev.TextCalls);
    }

    [Fact]
    public void A_Short_Selection_Is_Not_Cut()
    {
        var d = new FakeDoc(_b, Three).Select(0, 3, 2, 11);
        FocusOn(d, 2, 11);
        Assert.False(_lo.ReadSelection(Fg).Cut);
    }

    [Fact]
    public void The_Cap_Never_Splits_A_Surrogate_Pair()
    {
        string emojis = string.Concat(Enumerable.Repeat("\U0001F600", 5));   // 10 UTF-16 units, no white space
        var d = new FakeDoc(_b, emojis).Select(0, 0, 0, 10);
        FocusOn(d, 0, 10);
        var r = _lo.ReadSelection(Fg, maxChars: 5);   // the cap falls inside the third emoji
        Assert.True(r.Cut);
        Assert.Equal("\U0001F600\U0001F600", r.Selected);
    }

    [Fact]
    public void The_Budget_Is_Checked_In_The_Text_Pass_And_Is_Not_A_Timeout()
    {
        // review S2: the walk ends within budget (≈ 620 ms), then slow text calls (60 ms each) would run past it
        var texts = Enumerable.Range(1, 30).Select(i => $"Paragraphe {i}.").ToArray();
        var d = new FakeDoc(_b, texts).Select(0, 0, 29, texts[29].Length);
        d.HopCost = 20;
        d.TextCost = 60;
        FocusOn(d, 29, texts[29].Length);
        _lo.Read(Fg);                                   // the pid's first call: past the start grace afterwards
        _b.Now += LibreOfficeText.PidGraceMs;
        for (int i = 0; i < 4; i++)
        {
            _log.Clear();
            var r = _lo.ReadSelection(Fg);
            Assert.Equal(ReadStatus.Ok, r.Status);
            Assert.True(r.Partial && r.Cut);
            Assert.StartsWith("Paragraphe 1.", r.Selected);
            Assert.True(r.Selected!.Split('\n').Length < 30);
            Assert.Contains(_log, l => l.EndsWith("partial(time)", StringComparison.Ordinal));
        }
        Assert.False(_lo.IsOff(Fg));
        Assert.DoesNotContain(_log, l => l.StartsWith("ia2: off", StringComparison.Ordinal));
    }

    [Fact]
    public void Edge_Rule_On_An_Empty_Focused_Paragraph()
    {
        // Shift+Down onto an empty paragraph: the cursor's paragraph is empty, its mark [0,0]; the text is before it
        var d = new FakeDoc(_b, "Le chat dort.", "", "Il fait beau.").Select(0, 0, 1, 0);
        FocusOn(d, 1, 0);
        Assert.Equal(ReadStatus.Selection, _lo.Read(Fg).Status);
        Assert.StartsWith("Le chat dort.", _lo.ReadSelection(Fg).Selected);
        // Shift+Up onto it: the text is after it
        var e = new FakeDoc(_b, "Le chat dort.", "", "Il fait beau.").Select(1, 0, 2, 13, backward: true);
        FocusOn(e, 1, 0);
        Assert.Equal(ReadStatus.Selection, _lo.Read(Fg).Status);
        Assert.EndsWith("Il fait beau.", _lo.ReadSelection(Fg).Selected);
        // an empty paragraph with no mark: a plain caret, no hop
        var f = new FakeDoc(_b, "Le chat dort.", "", "Il fait beau.");
        FocusOn(f, 1, 0);
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.Single(f.Views);
    }

    [Fact]
    public void A_Non_Text_Neighbour_Outside_The_Selection_Ends_It_Completely()
    {
        // a whole paragraph selected (Home, Shift+End) between two non-text objects (a frame, a table) not selected
        var d = new FakeDoc(_b, "x", Three[1], "y").Select(1, 0, 1, Three[1].Length);
        d.Roles[0] = 0x429;
        d.Roles[2] = 0x429;
        FocusOn(d, 1, Three[1].Length);
        var r = _lo.ReadSelection(Fg);
        Assert.Equal((Three[1], false), (r.Selected, r.Partial));
        Assert.Contains(_log, l => l.EndsWith("complete", StringComparison.Ordinal));
    }

    [Fact]
    public void Episodes_Count_Again_After_Ten_Minutes_On()
    {
        Slow(2500);
        _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 10 s", _log);
        Fast();
        _b.Now += 10_000;
        Assert.Equal("ia2", _lo.Read(Fg).Source);               // on again
        _b.Now += 5 * 60_000;                                    // 5 minutes later: within the window
        Slow(2500);
        _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 20 s", _log);
        Fast();
        _b.Now += 20_000;
        Assert.Equal("ia2", _lo.Read(Fg).Source);
        _b.Now += LibreOfficeText.EpisodeWindowMs;               // 10 minutes on without an episode
        _log.Clear();
        Slow(2500);
        _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 10 s", _log);   // counted from 1 again
    }

    [Fact]
    public void Failed_Trials_Long_After_The_Last_On_Still_End_Permanent()
    {
        Fast();
        Assert.Equal("ia2", _lo.Read(Fg).Source);
        _b.Now += 2 * LibreOfficeText.EpisodeWindowMs;          // a long calm session, then LibreOffice stalls for good
        Slow(2500);
        _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 10 s", _log);
        _b.Now += 10_000; _lo.Read(Fg);                          // failed trials: never a « calm spell »
        _b.Now += 20_000; _lo.Read(Fg);
        _b.Now += 40_000; _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck)", _log);
        Fast();
        _b.Now += 10_000_000;
        Assert.Equal("skip", _lo.Read(Fg).Source);
    }

    [Fact]
    public void Four_Episodes_Within_The_Window_Are_Permanent_Even_With_Recoveries()
    {
        for (int ep = 1; ep <= 3; ep++)
        {
            Slow(2500);
            _lo.Read(Fg);
            Assert.Contains($"ia2: off for pid 7 (stuck), retry in {10 << (ep - 1)} s", _log);
            Fast();
            _b.Now += LibreOfficeText.CooldownMs << (ep - 1);
            Assert.Equal("ia2", _lo.Read(Fg).Source);           // recovers each time
            _b.Now += 60_000;
        }
        Slow(2500);
        _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck)", _log);
        Fast();
        _b.Now += 10_000_000;
        Assert.Equal("skip", _lo.Read(Fg).Source);
    }

    // ---- phase 2, S2b: the half-open breaker -----------------------------------------------------------------------

    /// <summary>Every resolve gives a new paragraph whose text call takes <paramref name="ms"/> on the backend clock (an
    /// off switch disposes the cached one: the trial resolves afresh).</summary>
    private void Slow(double ms)
    {
        _b.OnEvent = () =>
        {
            var q = _b.New();
            q.DuringText = () => _b.Now += ms;
            return q;
        };
        Focus();
    }

    /// <summary>A fast paragraph, resolved again (a new one) once the breaker has disposed it.</summary>
    private FakePara Fast(Action<FakePara>? setup = null)
    {
        var p = _b.New();
        setup?.Invoke(p);
        _b.OnEvent = () =>
        {
            if (!p.Disposed) return p;
            var q = _b.New();
            setup?.Invoke(q);
            return q;
        };
        Focus();
        return p;
    }

    [Fact]
    public void Stuck_Then_Cooldown_Then_One_Trial_Switches_On_Again()
    {
        Slow(2500);
        _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 10 s", _log);
        Assert.False(_lo.IsLive(Fg));
        var p = Fast();
        int before = _b.ComCalls;
        _b.Now += 9_000;
        Assert.Equal("skip", _lo.Read(Fg).Source);            // still in the cooldown: keys only, no COM call
        Assert.Null(_lo.ProbePassword(Fg));                     // the UIA probe takes over, as today
        Assert.Equal(before, _b.ComCalls);
        _b.Now += 1_500;
        var r = _lo.Read(Fg);                                   // the trial
        Assert.Equal((ReadStatus.Ok, "ia2"), (r.Status, r.Source));
        Assert.False(_lo.IsOff(Fg));
        Assert.Single(_log, l => l.StartsWith("ia2: on again for pid 7 after 1", StringComparison.Ordinal));   // 10–11 s
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.True(_lo.IsLive(Fg));
        Assert.Single(_log, l => l.StartsWith("ia2: on again", StringComparison.Ordinal));
    }

    [Fact]
    public void Exactly_One_Trial_While_It_Runs()
    {
        Slow(2500);
        _lo.Read(Fg);
        _b.Now += LibreOfficeText.CooldownMs;
        ReadResult inner = default;
        Fast(q => q.DuringText = () => { q.DuringText = null; inner = _lo.Read(Fg); });   // another worker while the trial runs
        Assert.Equal("ia2", _lo.Read(Fg).Source);
        Assert.Equal("skip", inner.Source);
        Assert.False(_lo.IsOff(Fg));
    }

    [Fact]
    public void Failed_Trials_Double_The_Cooldown_Then_Off_For_Good()
    {
        Slow(2500);
        _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 10 s", _log);
        _b.Now += 10_000;
        _lo.Read(Fg);                                           // trial, stuck again
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 20 s", _log);
        _b.Now += 19_000;
        Assert.Equal("skip", _lo.Read(Fg).Source);
        _b.Now += 1_000;
        _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (stuck), retry in 40 s", _log);
        _b.Now += 40_000;
        _lo.Read(Fg);                                           // the 4th episode
        Assert.Contains("ia2: off for pid 7 (stuck)", _log);
        Fast();
        _b.Now += 10_000_000;
        Assert.Equal("skip", _lo.Read(Fg).Source);
        Assert.True(_lo.IsOff(Fg));
    }

    [Fact]
    public void A_Slow_Trial_Is_The_Next_Episode()
    {
        Slow(2500);
        _lo.Read(Fg);
        _b.Now += 10_000;
        Slow(400);                                              // answers, but past the read budget
        _lo.Read(Fg);
        Assert.Contains(_log, l => l.StartsWith("ia2: off for pid 7 (slow trial, 40", StringComparison.Ordinal) && l.EndsWith("retry in 20 s", StringComparison.Ordinal));
        Assert.True(_lo.IsOff(Fg));
    }

    [Fact]
    public void No_Trial_While_A_Stuck_Call_Is_Still_In_Flight()
    {
        var p = _b.New();
        ReadResult second = default, third = default;
        p.DuringText = () =>
        {
            p.DuringText = null;
            _b.Now += 2500;                 // the outer read is stuck inside get_text
            second = _lo.Read(Fg);          // switches off (stuck in flight)
            _b.Now += 60_000;               // long past the cooldown, the outer call still has not returned
            third = _lo.Read(Fg);
        };
        _b.OnEvent = () => p;
        Focus();
        _lo.Read(Fg);
        Assert.Equal(("skip", "skip"), (second.Source, third.Source));
        Assert.True(_lo.IsOff(Fg));
        Fast();
        Assert.Equal("ia2", _lo.Read(Fg).Source);              // the stuck call is back: now the trial
        Assert.False(_lo.IsOff(Fg));
    }

    [Fact]
    public void Three_Timeouts_Are_Timed_Off_Too_Structural_Stays_Permanent()
    {
        var p = Fast();
        _lo.Read(Fg);
        _b.Now += LibreOfficeText.PidGraceMs;
        p.Cost = 40;
        _lo.Read(Fg); _lo.Read(Fg); _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 7 (3 timeouts), retry in 10 s", _log);
        p.Cost = 0;
        _b.Now += 10_000;
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.False(_lo.IsOff(Fg));

        _b.Pid = 8;                                             // structural: no usable IA2 in that process
        _b.EventHr = Ia2.E_NOINTERFACE;
        for (int i = 0; i < 5; i++) _lo.Read(Fg);
        Assert.Contains("ia2: off for pid 8 (5 structural failures)", _log);
        _b.EventHr = 0;
        _b.Now += 10_000_000;
        Assert.Equal("skip", _lo.Read(Fg).Source);
    }

    [Fact]
    public void Timed_Off_Clears_The_Caches_And_A_New_Pid_Starts_Afresh()
    {
        var p = Fast();
        Assert.NotNull(_lo.CaretPoint(Fg));
        Assert.NotNull(_lo.CachedCaretPoint(Fg));
        p.DuringText = () => _b.Now += 2500;
        _lo.Read(Fg);
        Assert.Null(_lo.CachedCaretPoint(Fg));
        Assert.False(_lo.IsLive(Fg));
        Assert.True(p.Disposed);
        _b.Pid = 9;                                             // a new LibreOffice process: no cooldown
        Fast();
        Assert.Equal(ReadStatus.Ok, _lo.Read(Fg).Status);
        Assert.False(_lo.IsOff(Fg));
    }

    /// <summary>The bounded caret wait, in the timing collection (runs alone: wall-clock bounds). The strict 20 ms is
    /// judged from the VM log (caret(ia2-late) timings); here the first call must never take the backend's 200 ms.</summary>
    [Collection(TimingCollection.Name)]
    public sealed class CaretTiming
    {
        [Trait("Category", "Timing")]
        [Fact]
        public void CaretPointBounded_Slow_LibreOffice_Never_Holds_The_Caller()
        {
            var b = new FakeBackend();
            var lo = new LibreOfficeText(b, true, null, onMta: a => a());
            var p = b.New("abcdef", caret: 2);
            b.OnEvent = () => p;
            lo.NoteFocus(Fg, Para, -4, 0);
            Assert.Equal((120, 220), lo.CaretPoint(Fg));            // the 1 s fallback cache
            p.SleepOn = "Extents"; p.SleepMs = 200;                  // LibreOffice busy from now on
            b.Now += LibreOfficeText.CaretThrottleMs + 1;            // past the throttle: a lookup is asked
            var sw = Stopwatch.StartNew();
            var late = lo.CaretPointBounded(Fg);
            long first = sw.ElapsedMilliseconds;
            Assert.Equal((120, 220, LibreOfficeText.CaretFrom.Late), late);   // the cached point, marked late
            sw.Restart();
            var busy = lo.CaretPointBounded(Fg);                    // the worker still busy: no second call queued, no wait
            long second = sw.ElapsedMilliseconds;
            Assert.Equal((120, 220, LibreOfficeText.CaretFrom.Late), busy);
            // never the backend's 200 ms (usually 20 ms + 1-2 ms; a loaded machine can add scheduling quanta)
            Assert.True(first <= 100, $"waited {first} ms");
            Assert.True(second <= 20, $"waited {second} ms while the worker was busy");
            b.Now += 1001;                                           // cache too old: nothing (the caller uses the mouse)
            Assert.Null(lo.CaretPointBounded(Fg, 0));
            lo.Dispose();
        }
    }

    /// <summary>Hook-path cost: a focus note (with its prefetch request into a real <see cref="ReadQueue"/>) and the
    /// settle decision stay far below the 5 µs mean of the plan.</summary>
    [Collection(TimingCollection.Name)]
    public sealed class Timing
    {
        private readonly ITestOutputHelper _out;
        public Timing(ITestOutputHelper output) => _out = output;

        [Trait("Category", "Timing")]
        [Fact]
        public void NoteFocus_And_Settle_Decision_Under_Five_Microseconds()
        {
            var b = new FakeBackend();
            using var queue = new ReadQueue(_ => true, (_, _) => { }, (Func<IntPtr, ReadResult>)(_ => default), null, null, 2000,
                                            KeyboardPolicy.ReadTimeoutMs, "lo-prefetch-test");
            var lo = new LibreOfficeText(b, true, null, w => queue.Request(0, w, 10_000, "prefetch"));
            for (int i = 0; i < 1000; i++) { lo.NoteFocus(Fg, Para, -4, 0); _ = SettlePolicy.ShouldAsk(' ', lo.IsLive(Fg), false, false, false); }
            const int n = 10_000;
            var sw = Stopwatch.StartNew();
            int asked = 0;
            for (int i = 0; i < n; i++)
            {
                lo.NoteFocus(Fg, Para, -4, 0);
                if (SettlePolicy.ShouldAsk(' ', !lo.IsLive(Fg2), false, false, false)) asked++;
            }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000 / n;
            _out.WriteLine($"NoteFocus + prefetch Request + settle decision: {us:0.00} µs mean over {n}");
            Assert.Equal(n, asked);
            Assert.True(us < 5, $"{us:0.00} µs");
            Assert.Equal(0, b.ComCalls);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();

        /// <summary>Review S3–S7: the hook's focus path tests LibreOffice's frame class first (one GetClassName), never a
        /// process lookup for another app's window; the image-name lookup (cold, a cache miss) is measured once.</summary>
        [Trait("Category", "Timing")]
        [Fact]
        public void Focus_Path_Frame_Test_Under_Five_Microseconds()
        {
            var h = GetDesktopWindow();
            Assert.False(HostApps.IsFrame(h));
            Assert.False(HostApps.IsFrame(IntPtr.Zero));
            var b = new FakeBackend();
            var lo = new LibreOfficeText(b, true, null);
            for (int i = 0; i < 1000; i++) { _ = HostApps.IsFrame(h); lo.NoteFocus(Fg, Para, -4, 0); }
            const int n = 10_000;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) { if (HostApps.IsFrame(h)) lo.NoteFocus(Fg, Para, -4, 0); else lo.NoteFocus(Fg2, Para, -4, 0); }
            sw.Stop();
            double us = sw.Elapsed.TotalMilliseconds * 1000 / n;
            var cold = Stopwatch.StartNew();
            var name = HostApps.ProcessNameOf(Process.GetCurrentProcess().MainWindowHandle != IntPtr.Zero ? Process.GetCurrentProcess().MainWindowHandle : h);
            double coldMs = cold.Elapsed.TotalMilliseconds;
            _out.WriteLine($"frame class test + NoteFocus: {us:0.00} µs mean over {n}; ProcessNameOf cold {coldMs:0.000} ms ('{name}')");
            Assert.True(us < 5, $"{us:0.00} µs");
            Assert.Equal(0, b.ComCalls);
        }
    }
}
