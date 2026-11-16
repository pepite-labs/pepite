using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plans 08a step 9, 08b step 4: engine choice (D24 default, SAPI interim, hold, fallback, several Piper
/// voices, residency) with fake engines.</summary>
public class EngineRouterTests
{
    private sealed class FakeSapi : ISynth
    {
        public bool HasVoices = true;
        public readonly List<string> Spoken = new();
        public readonly List<string?> Configured = new();
        public int Cancels;
        public IReadOnlyList<VoiceEntry> Voices() => HasVoices
            ? new[] { new VoiceEntry("sapi:Microsoft Paul", "Microsoft Paul", "en-US", "Male", "sapi"),
                      new VoiceEntry("sapi:Microsoft Hortense Desktop", "Microsoft Hortense Desktop", "fr-FR", "Female", "sapi") }
            : Array.Empty<VoiceEntry>();
        public string Configure(SpeechSettings s) { Configured.Add(s.Voice); return "sapi " + s.Voice; }
        public void SpeakAsync(string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null) { Spoken.Add(text); done(SpeechOutcome.Done, null); }
        public void CancelAll() => Cancels++;
        public void Dispose() { }
    }

    private sealed class FakePiper : IPiperEngine
    {
        private readonly string[] _ids;
        public readonly Dictionary<string, PiperState> States = new();
        public event Action<string, PiperState>? StateChanged;
        public Action<string, Action<SpeechOutcome, string?>, Action<SpeechProgress>?>? DeviceFallback { get; set; }
        public string? Resident { get; set; }
        public readonly List<string> LoadOrder = new();
        public int Loads => LoadOrder.Count;
        public readonly List<string> Spoken = new();
        public readonly List<(string Voice, string Text)> SpokenBy = new();
        public readonly List<bool> WordFlags = new();
        public readonly List<string?> Configured = new();

        public FakePiper(params string[] ids)
        {
            _ids = ids.Length == 0 ? new[] { "piper:siwis" } : ids;
            foreach (var i in _ids) States[i] = PiperState.NotLoaded;
        }

        /// <summary>siwis' state (the plan 08a tests).</summary>
        public PiperState State => States["piper:siwis"];
        public PiperState StateOf(string voiceId) => States.TryGetValue(voiceId, out var s) ? s : PiperState.Failed;
        public IReadOnlyList<VoiceEntry> Voices() => _ids.Select(i => new VoiceEntry(i, i.Substring(6), "fr-FR", "x", "piper")).ToList();
        public string Configure(SpeechSettings s) { Configured.Add(s.Voice); return s.Voice ?? "?"; }
        public void StartLoad(string voiceId) { LoadOrder.Add(voiceId); States[voiceId] = PiperState.Loading; }
        public void Finish(PiperState s) => Finish("piper:siwis", s);
        public void Finish(string voiceId, PiperState s) { States[voiceId] = s; StateChanged?.Invoke(voiceId, s); }
        /// <summary>The next Speak finds the voice unloaded (idle unload race): refused, state NotLoaded.</summary>
        public bool UnloadBeforeNextSpeak;
        public bool Speak(string voiceId, string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null, bool word = false)
        {
            if (UnloadBeforeNextSpeak) { UnloadBeforeNextSpeak = false; States[voiceId] = PiperState.NotLoaded; return false; }
            Spoken.Add(text);
            SpokenBy.Add((voiceId, text));
            WordFlags.Add(word);
            done(SpeechOutcome.Done, null);
            return true;
        }
        public void SpeakAsync(string text, Action<SpeechOutcome, string?> done, Action<SpeechProgress>? progress = null) => throw new InvalidOperationException("the router names the voice");
        public void CancelAll() { }
        public void Dispose() { }
    }

    private static (EngineRouter R, FakeSapi S, FakePiper? P, List<string> Log) Make(bool piper = true, bool sapiVoices = true)
    {
        var s = new FakeSapi { HasVoices = sapiVoices };
        var p = piper ? new FakePiper() : null;
        var log = new List<string>();
        return (new EngineRouter(s, p, log.Add, a => a()), s, p, log);
    }

    /// <summary>What the speech worker does for one item: the kind, then the item's settings, then speak.</summary>
    private static List<SpeechOutcome> Item(EngineRouter r, SpeechSettings s, string text, SpeechKind kind)
    {
        var o = new List<SpeechOutcome>();
        r.NextKind(kind);
        r.Configure(s);
        r.SpeakAsync(text, (x, _) => o.Add(x));
        return o;
    }

    private static (EngineRouter R, FakeSapi S, FakePiper P, List<string> Log) MakeTwo(params string[] ids)
    {
        var s = new FakeSapi();
        var p = new FakePiper(ids.Length == 0 ? new[] { "piper:siwis", "piper:pierre" } : ids);
        var log = new List<string>();
        return (new EngineRouter(s, p, log.Add, a => a()), s, p, log);
    }

    private static List<SpeechOutcome> Say(EngineRouter r, string text, SpeechKind kind = SpeechKind.Word)
    {
        var o = new List<SpeechOutcome>();
        r.NextKind(kind);
        r.SpeakAsync(text, (x, _) => o.Add(x));
        return o;
    }

    [Fact]
    public void Default_Is_Siwis_When_Installed_Else_First_French_Sapi()
    {
        var (r, s, p, _) = Make();
        Assert.Equal("piper:siwis", r.DefaultVoice);
        Assert.Equal("piper:siwis", r.Configure(new SpeechSettings()));
        Assert.Equal(new[] { "piper:siwis", "sapi:Microsoft Paul", "sapi:Microsoft Hortense Desktop" }, r.Voices().Select(v => v.Id));
        var (r2, s2, _, _) = Make(piper: false);
        Assert.Equal("sapi:Microsoft Hortense Desktop", r2.DefaultVoice);
        r2.Configure(new SpeechSettings());
        Assert.Equal(new string?[] { null }, s2.Configured);
    }

    [Fact]
    public void Loading_Speaks_With_Sapi_And_Logs_Once()
    {
        var (r, s, p, log) = Make();
        r.Configure(new SpeechSettings());
        r.AllowLoad();
        Assert.Equal(1, p!.Loads);
        Say(r, "un"); Say(r, "deux");
        Assert.Equal(new[] { "un", "deux" }, s.Spoken);
        Assert.Single(log, l => l == "speech: using SAPI until piper:siwis is ready");
        p.Finish(PiperState.Ready);
        Say(r, "trois");
        Assert.Equal(new[] { "trois" }, p.Spoken);
    }

    [Fact]
    public void No_Load_Before_The_App_Allows_It()
    {
        var (r, _, p, _) = Make();
        r.Configure(new SpeechSettings());
        Assert.Equal(0, p!.Loads);
        r.AllowLoad();
        Assert.Equal(1, p.Loads);
        r.Configure(new SpeechSettings(Rate: 3));
        Assert.Equal(1, p.Loads);
    }

    [Fact]
    public void Without_Sapi_Voices_The_Utterance_Waits_For_Piper()
    {
        var (r, s, p, _) = Make(sapiVoices: false);
        r.Configure(new SpeechSettings());
        r.AllowLoad();
        var o = Say(r, "maison");
        Assert.Empty(o);
        Assert.Empty(s.Spoken);
        p!.Finish(PiperState.Ready);
        Assert.Equal(new[] { "maison" }, p.Spoken);
        Assert.Equal(new[] { SpeechOutcome.Done }, o);
    }

    [Fact]
    public void Preview_Of_The_Piper_Voice_Waits_And_Cancel_Releases_It()
    {
        var (r, s, p, _) = Make();
        r.Configure(new SpeechSettings(Voice: "piper:siwis"));
        r.AllowLoad();
        var o = Say(r, "Bonjour", SpeechKind.Preview);
        Assert.Empty(o);
        r.CancelAll();
        Assert.Equal(new[] { SpeechOutcome.Cancelled }, o);
        p!.Finish(PiperState.Ready);
        Assert.Empty(p.Spoken);
        Assert.Empty(s.Spoken);
    }

    [Fact]
    public void Failed_Load_Goes_To_Sapi()
    {
        var (r, s, p, _) = Make();
        r.Configure(new SpeechSettings());
        r.AllowLoad();
        var held = Say(r, "Bonjour", SpeechKind.Preview);
        p!.Finish(PiperState.Failed);
        Assert.Equal(new[] { "Bonjour" }, s.Spoken);
        Assert.Equal(new[] { SpeechOutcome.Done }, held);
        Say(r, "après");
        Assert.Equal(2, s.Spoken.Count);
        Assert.Empty(p.Spoken);
    }

    [Fact]
    public void Explicit_Sapi_Voice_Never_Loads_Piper_And_Strips_The_Prefix()
    {
        var (r, s, p, _) = Make();
        r.AllowLoad();
        r.Configure(new SpeechSettings(Voice: "sapi:Microsoft Hortense Desktop"));
        Assert.Equal(0, p!.Loads);
        Assert.Equal("Microsoft Hortense Desktop", s.Configured.Last());
        Say(r, "chat");
        Assert.Equal(new[] { "chat" }, s.Spoken);
        r.Configure(new SpeechSettings(Voice: "piper:siwis"));   // selecting siwis starts the load
        Assert.Equal(1, p.Loads);
    }

    [Fact]
    public void Missing_Piper_Voice_Falls_Back_To_Sapi_Default()
    {
        var (r, s, p, log) = Make(piper: false);
        r.Configure(new SpeechSettings(Voice: "piper:siwis"));
        Assert.Null(s.Configured.Last());                   // the SAPI default (first fr-FR voice)
        Assert.Contains("speech: voice 'piper:siwis' not installed, using the default voice", log);
        Say(r, "chat");
        Assert.Single(s.Spoken);
    }

    [Theory]
    [InlineData("piper:..\\..\\x")]
    [InlineData("piper:C:\\models\\x.onnx")]
    [InlineData("piper:/etc/x")]
    public void A_Voice_Id_Is_Only_Looked_Up_In_The_Installed_Catalogue(string voice)
    {
        // a settings layer (an imported *.pepite file among others) cannot point the synthesizer at a model path: an id
        // that is not an installed pack falls back to the default voice; no load is started for it
        var (r, s, p, log) = Make();
        Assert.Equal("piper:siwis", r.Configure(new SpeechSettings(Voice: voice)));
        Assert.Contains(log, l => l.Contains("not installed, using the default voice"));
        r.AllowLoad();
        Assert.Equal(new[] { "piper:siwis" }, p!.LoadOrder);
    }

    [Fact]
    public void Missing_Sapi_Voice_Means_The_Default_Siwis()
    {
        var (r, s, p, log) = Make();
        Assert.Equal("piper:siwis", r.Configure(new SpeechSettings(Voice: "sapi:Microsoft Julie")));
        Assert.Contains(log, l => l.Contains("'sapi:Microsoft Julie' not installed"));
        r.AllowLoad();
        Assert.Equal(1, p!.Loads);
        r.Configure(new SpeechSettings(Voice: "sapi:Microsoft Paul"));   // installed: kept, Piper not used
        Assert.Equal("Microsoft Paul", s.Configured.Last());
    }

    [Fact]
    public void Cancel_Is_Not_Lost_When_Piper_Hands_The_Utterance_To_Sapi()
    {
        var s = new FakeSapi();
        var p = new FakePiper();
        var posted = new List<Action>();
        var r = new EngineRouter(s, p, null, posted.Add);   // posts run when the test says (speech thread later)
        r.Configure(new SpeechSettings());
        p.Finish(PiperState.Ready);
        posted.Clear();
        var o = new List<SpeechOutcome>();
        r.SpeakAsync("bonjour", (x, _) => o.Add(x));
        Assert.Equal(new[] { "bonjour" }, p.Spoken);
        o.Clear();
        // Piper could not open the device: it hands the utterance back (posted), then a cancel arrives first
        p.DeviceFallback!("bonjour", (x, _) => o.Add(x), null);
        r.CancelAll();
        foreach (var a in posted) a();
        Assert.Equal(new[] { SpeechOutcome.Cancelled }, o);
        Assert.Empty(s.Spoken);
        // without a cancel in between, SAPI speaks it
        posted.Clear(); o.Clear();
        r.SpeakAsync("salut", (x, _) => o.Add(x));
        p.DeviceFallback!("salut", (x, _) => o.Add(x), null);
        foreach (var a in posted) a();
        Assert.Equal(new[] { "salut" }, s.Spoken);
    }

    [Fact]
    public void Device_Fallback_Hands_The_Utterance_To_Sapi()
    {
        var (r, s, p, log) = Make();
        var o = new List<SpeechOutcome>();
        p!.DeviceFallback!("bonjour", (x, _) => o.Add(x), null);
        Assert.Equal(new[] { "bonjour" }, s.Spoken);
        Assert.Equal(new[] { SpeechOutcome.Done }, o);
        Assert.Contains(log, l => l.Contains("utterance given to SAPI"));
    }

    // ---------------------------------------------------------------- plan 08b: several Piper voices

    [Fact]
    public void Default_Is_Siwis_With_Pierre_Installed_And_Never_Pierre_Alone()
    {
        var (r, s, p, _) = MakeTwo();
        Assert.Equal("piper:siwis", r.DefaultVoice);
        Assert.Equal(new[] { "piper:siwis", "piper:pierre", "sapi:Microsoft Paul", "sapi:Microsoft Hortense Desktop" }, r.Voices().Select(v => v.Id));
        Assert.Equal("piper:siwis", r.Configure(new SpeechSettings()));
        Assert.Equal("piper:siwis", p.Resident);
        // pierre only: D24 literally, the first fr-FR SAPI voice
        var (r2, s2, p2, _) = MakeTwo("piper:pierre");
        Assert.Equal("sapi:Microsoft Hortense Desktop", r2.DefaultVoice);
        r2.Configure(new SpeechSettings());
        Assert.Equal(new string?[] { null }, s2.Configured);
        Assert.Null(p2.Resident);
        r2.AllowLoad();
        Assert.Equal(0, p2.Loads);
        // none: SAPI
        var (r3, _, _, _) = Make(piper: false);
        Assert.Equal("sapi:Microsoft Hortense Desktop", r3.DefaultVoice);
    }

    [Fact]
    public void Each_Piper_Voice_Is_Routed_By_Its_Own_State()
    {
        var (r, s, p, log) = MakeTwo();
        r.Configure(new SpeechSettings(Voice: "piper:pierre"));
        r.AllowLoad();
        Assert.Equal(new[] { "piper:pierre" }, p.LoadOrder);   // only the configured voice loads
        Assert.Equal("piper:pierre", p.Resident);
        Say(r, "un");
        Assert.Equal(new[] { "un" }, s.Spoken);
        Assert.Single(log, l => l == "speech: using SAPI until piper:pierre is ready");
        p.Finish("piper:pierre", PiperState.Ready);
        Say(r, "deux");
        Assert.Equal(new[] { ("piper:pierre", "deux") }, p.SpokenBy);
        Assert.Equal("piper:pierre", p.Configured.Last());
    }

    [Fact]
    public void Preview_Of_Pierre_While_Siwis_Is_Configured_Holds_Then_Pierre_And_The_Next_Echo_Is_Siwis()
    {
        var (r, s, p, log) = MakeTwo();
        var saved = new SpeechSettings(Voice: "piper:siwis");
        r.Configure(saved);
        r.AllowLoad();
        p.Finish("piper:siwis", PiperState.Ready);
        var held = Item(r, new SpeechSettings(Voice: "piper:pierre"), "Bonjour", SpeechKind.Preview);
        Assert.Empty(held);
        Assert.Contains("speech: waiting for the Piper voice to load", log);
        Assert.Equal(new[] { "piper:siwis", "piper:pierre" }, p.LoadOrder);
        Assert.Equal("piper:siwis", p.Resident);              // a preview never changes the configured voice
        p.Finish("piper:siwis", PiperState.Ready);              // another voice's event: still held
        Assert.Empty(held);
        p.Finish("piper:pierre", PiperState.Ready);
        Assert.Equal(new[] { SpeechOutcome.Done }, held);
        Item(r, saved, "chat", SpeechKind.Word);               // the worker applies the saved settings again
        Assert.Equal(new[] { ("piper:pierre", "Bonjour"), ("piper:siwis", "chat") }, p.SpokenBy);
        Assert.Equal(2, p.Loads);                               // no reload of siwis
        Assert.Empty(s.Spoken);
        Assert.Equal("piper:siwis", p.Resident);
    }

    [Fact]
    public void Saving_Another_Voice_Makes_It_Resident_And_A_Sapi_Voice_Clears_It()
    {
        var (r, _, p, _) = MakeTwo();
        r.Configure(new SpeechSettings());
        Assert.Equal("piper:siwis", p.Resident);
        r.Configure(new SpeechSettings(Voice: "piper:pierre"));      // Enregistrer (no item kind: Manual)
        Assert.Equal("piper:pierre", p.Resident);
        Item(r, new SpeechSettings(Voice: "sapi:Microsoft Paul"), "x", SpeechKind.Preview);
        Assert.Equal("piper:pierre", p.Resident);                    // preview of a SAPI voice: unchanged
        r.Configure(new SpeechSettings(Voice: "sapi:Microsoft Paul"));
        Assert.Null(p.Resident);
    }

    [Fact]
    public void Failed_Pierre_Load_Leaves_Siwis_Working()
    {
        var (r, s, p, _) = MakeTwo();
        var saved = new SpeechSettings(Voice: "piper:siwis");
        r.Configure(saved);
        r.AllowLoad();
        p.Finish("piper:siwis", PiperState.Ready);
        var held = Item(r, new SpeechSettings(Voice: "piper:pierre"), "Bonjour", SpeechKind.Preview);
        p.Finish("piper:pierre", PiperState.Failed);
        Assert.Equal(new[] { "Bonjour" }, s.Spoken);                 // the held preview goes to SAPI
        Assert.Equal(new[] { SpeechOutcome.Done }, held);
        Item(r, saved, "chat", SpeechKind.Word);
        Assert.Equal(new[] { ("piper:siwis", "chat") }, p.SpokenBy);
    }

    [Fact]
    public void Voice_Unloaded_Between_Check_And_Speak_Is_Loaded_Again_Not_An_Error()
    {
        var (r, s, p, log) = MakeTwo();
        var saved = new SpeechSettings(Voice: "piper:siwis");
        r.Configure(saved);
        r.AllowLoad();
        p.Finish("piper:siwis", PiperState.Ready);
        p.Finish("piper:pierre", PiperState.Ready);
        // a preview of pierre: unloaded (idle) just as it is spoken -> held, reloaded, then spoken
        p.UnloadBeforeNextSpeak = true;
        var o = Item(r, new SpeechSettings(Voice: "piper:pierre"), "Bonjour", SpeechKind.Preview);
        Assert.Empty(o);
        Assert.Contains("speech: piper:pierre was unloaded meanwhile, loading it again", log);
        Assert.Equal(PiperState.Loading, p.StateOf("piper:pierre"));
        p.Finish("piper:pierre", PiperState.Ready);
        Assert.Equal(new[] { SpeechOutcome.Done }, o);
        Assert.Equal(("piper:pierre", "Bonjour"), p.SpokenBy.Last());
        // a word echo in the same race: SAPI meanwhile (interim), never an error
        p.UnloadBeforeNextSpeak = true;
        var w = Item(r, saved, "chat", SpeechKind.Word);
        Assert.Equal(new[] { SpeechOutcome.Done }, w);
        Assert.Equal(new[] { "chat" }, s.Spoken);
        Assert.Equal(PiperState.Loading, p.StateOf("piper:siwis"));
    }

    // ---------------------------------------------------------------- 0.6.0 owner bug: make-before-break, preload

    /// <summary>siwis configured and Ready, the app started.</summary>
    private static (EngineRouter R, FakeSapi S, FakePiper P, List<string> Log) SiwisReady()
    {
        var t = MakeTwo();
        t.R.Configure(new SpeechSettings(Voice: "piper:siwis"));
        t.R.AllowLoad();
        t.P.Finish("piper:siwis", PiperState.Ready);
        return t;
    }

    [Fact]
    public void Switching_Voice_Keeps_The_Old_Piper_Voice_Speaking_Until_The_New_One_Is_Ready()
    {
        var (r, s, p, log) = SiwisReady();
        var pierre = new SpeechSettings(Voice: "piper:pierre");
        r.Configure(pierre);                                         // Enregistrer with pierre
        Assert.Equal("piper:pierre", p.Resident);
        Assert.Equal(new[] { "piper:siwis", "piper:pierre" }, p.LoadOrder);
        Item(r, pierre, "les amis", SpeechKind.Word);
        Item(r, pierre, "un enfant", SpeechKind.Word);
        Assert.Empty(s.Spoken);                                      // never Hortense
        Assert.Equal(new[] { ("piper:siwis", "les amis"), ("piper:siwis", "un enfant") }, p.SpokenBy);
        Assert.Single(log, l => l == "speech: using piper:siwis until piper:pierre is ready");
        Assert.DoesNotContain(log, l => l.Contains("using SAPI"));
        p.Finish("piper:pierre", PiperState.Ready);
        Item(r, pierre, "chat", SpeechKind.Word);
        Assert.Equal(("piper:pierre", "chat"), p.SpokenBy.Last());
        // a later switch back to siwis, now unloaded by the residency: SAPI interim is not needed while pierre is Ready
        p.States["piper:siwis"] = PiperState.NotLoaded;
        var siwis = new SpeechSettings(Voice: "piper:siwis");
        r.Configure(siwis);
        Item(r, siwis, "chien", SpeechKind.Word);
        Assert.Equal(("piper:pierre", "chien"), p.SpokenBy.Last());
        Assert.Empty(s.Spoken);
    }

    [Fact]
    public void A_Preview_Of_The_New_Voice_Still_Waits_For_It()
    {
        var (r, _, p, _) = SiwisReady();
        var pierre = new SpeechSettings(Voice: "piper:pierre");
        r.Configure(pierre);
        var held = Item(r, pierre, "Bonjour", SpeechKind.Preview);   // « Écouter » must be pierre, not the bridge
        Assert.Empty(held);
        Assert.Empty(p.SpokenBy);
        p.Finish("piper:pierre", PiperState.Ready);
        Assert.Equal(new[] { ("piper:pierre", "Bonjour") }, p.SpokenBy);
    }

    [Fact]
    public void A_Failed_Load_Of_The_New_Voice_Keeps_The_Old_One()
    {
        var (r, s, p, log) = SiwisReady();
        var pierre = new SpeechSettings(Voice: "piper:pierre");
        r.Configure(pierre);
        p.Finish("piper:pierre", PiperState.Failed);
        Assert.Contains("speech: piper:pierre could not be loaded, keeping piper:siwis", log);
        Item(r, pierre, "chat", SpeechKind.Word);
        Assert.Equal(new[] { ("piper:siwis", "chat") }, p.SpokenBy);
        Assert.Empty(s.Spoken);
    }

    [Fact]
    public void Sapi_Is_The_Interim_Only_When_No_Piper_Voice_Is_Loaded()
    {
        // siwis not Ready yet (start-up) and pierre saved: nothing to bridge with
        var (r, s, p, log) = MakeTwo();
        r.Configure(new SpeechSettings(Voice: "piper:siwis"));
        r.AllowLoad();
        var pierre = new SpeechSettings(Voice: "piper:pierre");
        r.Configure(pierre);
        Item(r, pierre, "un", SpeechKind.Word);
        Assert.Equal(new[] { "un" }, s.Spoken);
        Assert.Contains("speech: using SAPI until piper:pierre is ready", log);
        // the bridge itself unloaded meanwhile: SAPI, not an error
        var (r2, s2, p2, _) = SiwisReady();
        r2.Configure(pierre);
        p2.States["piper:siwis"] = PiperState.NotLoaded;
        Assert.Equal(new[] { SpeechOutcome.Done }, Item(r2, pierre, "deux", SpeechKind.Word));
        Assert.Equal(new[] { "deux" }, s2.Spoken);
    }

    [Fact]
    public void Switching_To_A_Sapi_Voice_Does_Not_Bridge()
    {
        var (r, s, p, _) = SiwisReady();
        var paul = new SpeechSettings(Voice: "sapi:Microsoft Paul");
        r.Configure(paul);
        Assert.Null(p.Resident);
        Item(r, paul, "chat", SpeechKind.Word);
        Assert.Equal(new[] { "chat" }, s.Spoken);
        Assert.Empty(p.SpokenBy);
    }

    [Fact]
    public void Selecting_A_Piper_Voice_Preloads_It_Without_Changing_The_Configured_Voice()
    {
        var (r, _, p, log) = SiwisReady();
        r.Preload("piper:pierre");
        Assert.Equal(new[] { "piper:siwis", "piper:pierre" }, p.LoadOrder);
        Assert.Equal("piper:siwis", p.Resident);                    // preview-only until saved
        Assert.Contains("speech: preloading piper:pierre (selected in Réglages)", log);
        r.Preload("piper:pierre");                                   // already loading: once
        r.Preload("piper:siwis");                                    // already Ready
        r.Preload("piper:absent");                                   // not installed
        Assert.Equal(2, p.Loads);
        p.Finish("piper:pierre", PiperState.Ready);
        // Enregistrer finds it Ready: no bridge, no interim
        var pierre = new SpeechSettings(Voice: "piper:pierre");
        r.Configure(pierre);
        Item(r, pierre, "chat", SpeechKind.Word);
        Assert.Equal(new[] { ("piper:pierre", "chat") }, p.SpokenBy);
        Assert.DoesNotContain(log, l => l.StartsWith("speech: using"));
        // Annuler instead: siwis stays configured and keeps speaking
        var (r2, _, p2, _) = SiwisReady();
        r2.Preload("piper:pierre");
        p2.Finish("piper:pierre", PiperState.Ready);
        Item(r2, new SpeechSettings(Voice: "piper:siwis"), "chien", SpeechKind.Word);
        Assert.Equal(new[] { ("piper:siwis", "chien") }, p2.SpokenBy);
        Assert.Equal("piper:siwis", p2.Resident);
    }

    [Fact]
    public void Single_Words_Are_Flagged_For_The_Word_Scales_And_Cache()
    {
        var (r, _, p, _) = SiwisReady();
        var siwis = new SpeechSettings(Voice: "piper:siwis");
        foreach (var k in new[] { SpeechKind.Word, SpeechKind.Inserted, SpeechKind.Manual, SpeechKind.Sentence, SpeechKind.Reread, SpeechKind.Reading, SpeechKind.Preview })
            Item(r, siwis, "x", k);
        Assert.Equal(new[] { true, true, true, false, false, false, false }, p.WordFlags);
    }

    [Fact]
    public void No_Preload_Before_The_App_Allows_Loads()
    {
        var (r, _, p, _) = MakeTwo();
        r.Configure(new SpeechSettings(Voice: "piper:siwis"));
        r.Preload("piper:pierre");
        Assert.Equal(0, p.Loads);
    }

    [Fact]
    public void Missing_Saved_Pierre_Means_Siwis()
    {
        var (r, _, p, log) = MakeTwo("piper:siwis");
        Assert.Equal("piper:siwis", r.Configure(new SpeechSettings(Voice: "piper:pierre")));
        Assert.Contains("speech: voice 'piper:pierre' not installed, using the default voice", log);
        Assert.Equal("piper:siwis", p.Resident);
    }
}

/// <summary>Plan 08a: voice pack cards (A11: listed by size, verified by SHA-256 at load).</summary>
public class PiperVoicePackTests
{
    private static string MakePack(string root, byte[] model, bool badSize = false, string? sha = null)
    {
        var dir = Path.Combine(root, "siwis");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "v.onnx"), model);
        File.WriteAllText(Path.Combine(dir, "v.onnx.json"), "{}");
        string H(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();
        var card = $@"{{""id"":""siwis"",""name"":""S"",""gender"":""female"",""culture"":""fr-FR"",""sampleRate"":22050,
            ""model"":""v.onnx"",""config"":""v.onnx.json"",
            ""sizes"":{{""v.onnx"":{model.Length + (badSize ? 1 : 0)},""v.onnx.json"":2}},
            ""sha256"":{{""v.onnx"":""{sha ?? H(model)}"",""v.onnx.json"":""{H(Encoding.UTF8.GetBytes("{}"))}""}},
            ""attribution"":[""a"",""b""]}}";
        File.WriteAllText(Path.Combine(dir, "voice.json"), card);
        return dir;
    }

    private static string Root() => Path.Combine(Path.GetTempPath(), "kbvoices-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Card_Parses_And_Verifies()
    {
        var root = Root();
        try
        {
            MakePack(root, new byte[] { 1, 2, 3 });
            var p = PiperVoicePack.Scan(root, null).Single();
            Assert.Equal("piper:siwis", p.VoiceId);
            Assert.Equal(new[] { "a", "b" }, p.Attribution);
            p.Verify();
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Size_Mismatch_Is_Not_Listed()
    {
        var root = Root();
        try
        {
            MakePack(root, new byte[] { 1, 2, 3 }, badSize: true);
            var log = new List<string>();
            Assert.Empty(PiperVoicePack.Scan(root, log.Add));
            Assert.Contains(log, l => l.Contains("left out (size of v.onnx)"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Sha_Mismatch_Fails_Verify()
    {
        var root = Root();
        try
        {
            MakePack(root, new byte[] { 1, 2, 3 }, sha: new string('0', 64));
            var p = PiperVoicePack.Scan(root, null).Single();
            var ex = Assert.Throws<InvalidDataException>(() => p.Verify());
            Assert.StartsWith("sha256 mismatch", ex.Message);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Path_In_A_Card_Is_Rejected()
    {
        Assert.Throws<FormatException>(() => PiperVoicePack.Parse("x", @"{""id"":""a"",""culture"":""fr-FR"",""gender"":""f"",""model"":""..\\x.onnx"",""sizes"":{},""sha256"":{}}"));
    }

    [Fact]
    public void Voice_Config_Parses_The_Siwis_Fields()
    {
        var json = $@"{{""audio"":{{""sample_rate"":22050}},""espeak"":{{""voice"":""fr""}},""inference"":{{""noise_scale"":0.5,""length_scale"":1.0,""noise_w"":0.333}},
            ""hop_length"":256,""num_speakers"":1,""phoneme_id_map"":{{""_"":[0],""^"":[1],""$"":[2]}},""vowel_clusters"":[[""a"",""ɪ""]]}}";
        var c = PiperVoiceConfig.Parse(json);
        Assert.Equal("fr", c.EspeakVoice);
        Assert.Equal(0.333f, c.NoiseW);
        Assert.Equal(256, c.HopLength);
        Assert.Equal("aɪ", string.Concat(c.VowelClusters.Single()));
    }
}
