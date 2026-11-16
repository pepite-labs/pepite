using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

internal enum BarSize { S, M, L, XL }
internal enum BarPosition { Caret, Top, Bottom, Free }
internal enum KeyMode { F1F8, Off }
internal enum BarFont { SegoeUI, OpenDyslexic, Lexend, Andika }
internal enum LetterSpacing { Normal, Wider, Widest }
/// <summary>Space put before <c>; : ! ?</c> typed right after an inserted word (D22): French typography with a
/// narrow no-break space (default), none, or — hidden, JSON only — a plain no-break space for apps or fonts that
/// draw the narrow one badly.</summary>
internal enum PunctuationSpacing { French, None, FrenchNbsp }

/// <param name="Rate">SAPI rate −10..10.</param>
/// <param name="Volume">SAPI volume 0..100.</param>
/// <param name="SpeakWords">D7, owner 2026-11-15 (0.8.3): the word echo after a separator, on by default (an explicit false in
/// a profile or the machine file stays false).</param>
/// <param name="SpeakSentences">D7, owner 2026-11-15 (0.8.3): the sentence echo at . ! ?, on by default.</param>
/// <param name="RereadEdits">D23: re-read the modified word / sentence when the caret leaves it (on by default).</param>
internal sealed record SpeechSettings(bool SpeakInserted = true, bool SpeakWords = true, bool SpeakSentences = true,
                                      string? Voice = null, int Rate = 0, int Volume = 100, bool RereadEdits = true);

/// <summary>Selection reading (plan 07, D21/D24): the reading mode on (default), the simulated Ctrl+C fallback (off by
/// default), pictures in the reading tiles (null = follow <c>words.pictures</c>), and the « grande fenêtre » place and size
/// (physical px; null until the user moved or resized it).</summary>
internal sealed record ReadingSettings(bool Enabled = true, bool ClipboardFallback = false, bool? Pictures = null,
                                       int? WinX = null, int? WinY = null, int? WinW = null, int? WinH = null)
{
    public const int MinWindow = 200;
    public bool HasWindow => WinX.HasValue && WinY.HasValue && WinW.HasValue && WinH.HasValue;
}

/// <summary>PBKDF2 hash of the adult PIN (designed in iteration 3, used from iteration 6).</summary>
internal sealed record PinHash(string Alg, int Iterations, string Salt, string Hash);

/// <summary>Adult lock of « Réglages » (D8). Child-proofing, not security: read and kept, ignored this iteration.</summary>
internal sealed record LockSettings(bool Enabled = false, PinHash? Pin = null);

/// <summary>
/// Every user setting of the app (D7–D9, D16). Immutable; <see cref="Validated"/> clamps out-of-range values.
/// The JSON shape (<see cref="ToJson"/> / <see cref="FromJson"/>) groups them as <c>words</c>, <c>bar</c>,
/// <c>text</c>, <c>speech</c>, <c>reading</c>, <c>start</c>, <c>lock</c>; enum values are short strings so that an admin can write the machine file by hand.
/// Parsing is tolerant: a missing, mistyped or unknown value falls back to the default of that field only.
/// </summary>
internal sealed record AppSettings(
    int WordCount = 8, BarSize Size = BarSize.M, bool Pictures = true, KeyMode Keys = KeyMode.F1F8,
    BarFont Font = BarFont.SegoeUI, LetterSpacing Spacing = LetterSpacing.Normal,
    BarPosition Position = BarPosition.Caret, int? FreeX = null, int? FreeY = null,
    SpeechSettings? Speech = null, LockSettings? Lock = null, PunctuationSpacing Punctuation = PunctuationSpacing.French,
    ReadingSettings? Reading = null, bool Welcome = true, bool SpellingIndicator = true, bool NamePictures = true)
{
    public const int Schema = 1;
    public const int MinWords = 1, MaxWords = 8;

    public SpeechSettings Speech { get; init; } = Speech ?? new SpeechSettings();
    public LockSettings Lock { get; init; } = Lock ?? new LockSettings();
    public ReadingSettings Reading { get; init; } = Reading ?? new ReadingSettings();

    /// <summary>Pictures above the reading tiles: the reading setting when set, else the bar's.</summary>
    public bool ReadingPictures => Reading.Pictures ?? Pictures;

    public AppSettings Validated() => this with
    {
        WordCount = Math.Clamp(WordCount, MinWords, MaxWords),
        Size = Enum.IsDefined(Size) ? Size : BarSize.M,
        Keys = Enum.IsDefined(Keys) ? Keys : KeyMode.F1F8,
        Font = Enum.IsDefined(Font) ? Font : BarFont.SegoeUI,
        Spacing = Enum.IsDefined(Spacing) ? Spacing : LetterSpacing.Normal,
        Position = Enum.IsDefined(Position) ? Position : BarPosition.Caret,
        Punctuation = Enum.IsDefined(Punctuation) ? Punctuation : PunctuationSpacing.French,
        FreeX = FreeX.HasValue && FreeY.HasValue ? FreeX : null,
        FreeY = FreeX.HasValue && FreeY.HasValue ? FreeY : null,
        Speech = Speech with
        {
            Rate = Math.Clamp(Speech.Rate, -10, 10),
            Volume = Math.Clamp(Speech.Volume, 0, 100),
            Voice = VoiceId.Normalize(Speech.Voice),   // plan 08a: engine:name (a legacy bare SAPI name → sapi:name)
        },
        Lock = Lock.Pin is { } p && (p.Iterations <= 0 || string.IsNullOrEmpty(p.Salt) || string.IsNullOrEmpty(p.Hash))
             ? Lock with { Pin = null } : Lock,
        Reading = Reading.HasWindow && Reading.WinW >= ReadingSettings.MinWindow && Reading.WinH >= ReadingSettings.MinWindow
                  ? Reading : Reading with { WinX = null, WinY = null, WinW = null, WinH = null },
    };

    // ------------------------------------------------------------------ JSON names
    public static readonly IReadOnlyDictionary<BarSize, string> SizeNames =
        new Dictionary<BarSize, string> { [BarSize.S] = "S", [BarSize.M] = "M", [BarSize.L] = "L", [BarSize.XL] = "XL" };
    public static readonly IReadOnlyDictionary<KeyMode, string> KeyNames =
        new Dictionary<KeyMode, string> { [KeyMode.F1F8] = "F1-F8", [KeyMode.Off] = "off" };
    public static readonly IReadOnlyDictionary<BarFont, string> FontNames = new Dictionary<BarFont, string>
    { [BarFont.SegoeUI] = "segoe-ui", [BarFont.OpenDyslexic] = "opendyslexic", [BarFont.Lexend] = "lexend", [BarFont.Andika] = "andika" };
    public static readonly IReadOnlyDictionary<LetterSpacing, string> SpacingNames = new Dictionary<LetterSpacing, string>
    { [LetterSpacing.Normal] = "normal", [LetterSpacing.Wider] = "wider", [LetterSpacing.Widest] = "widest" };
    public static readonly IReadOnlyDictionary<BarPosition, string> PositionNames = new Dictionary<BarPosition, string>
    { [BarPosition.Caret] = "caret", [BarPosition.Top] = "top", [BarPosition.Bottom] = "bottom", [BarPosition.Free] = "free" };
    public static readonly IReadOnlyDictionary<PunctuationSpacing, string> PunctuationNames = new Dictionary<PunctuationSpacing, string>
    { [PunctuationSpacing.French] = "french", [PunctuationSpacing.None] = "none", [PunctuationSpacing.FrenchNbsp] = "french-nbsp" };

    /// <summary>The settings as the <c>words / bar / text / speech / lock</c> part of settings.json (no schema, no state).</summary>
    public JsonObject ToJson()
    {
        var bar = new JsonObject { ["position"] = PositionNames[Position] };
        if (FreeX is int x && FreeY is int y) bar["free"] = new JsonObject { ["x"] = x, ["y"] = y };
        return new JsonObject
        {
            ["words"] = new JsonObject
            {
                ["count"] = WordCount, ["size"] = SizeNames[Size], ["pictures"] = Pictures, ["keys"] = KeyNames[Keys],
                ["font"] = FontNames[Font], ["spacing"] = SpacingNames[Spacing],
                // plan 10B (D12, Q7): the round marker ✓ ≈ ? left of the words; on by default, per profile, lockable
                ["spellingIndicator"] = SpellingIndicator,
                // plan 10C: flags and the teacher's pictures above proper names; on by default, per profile, lockable
                ["namePictures"] = NamePictures,
            },
            ["bar"] = bar,
            ["text"] = new JsonObject { ["punctuationSpace"] = PunctuationNames[Punctuation] },
            ["speech"] = new JsonObject
            {
                ["speakInserted"] = Speech.SpeakInserted, ["speakWords"] = Speech.SpeakWords,
                ["speakSentences"] = Speech.SpeakSentences, ["voice"] = Speech.Voice, ["rate"] = Speech.Rate,
                ["volume"] = Speech.Volume, ["rereadEdits"] = Speech.RereadEdits,
            },
            ["reading"] = new JsonObject
            {
                ["enabled"] = Reading.Enabled, ["clipboardFallback"] = Reading.ClipboardFallback, ["pictures"] = Reading.Pictures,
                ["window"] = Reading.HasWindow
                    ? new JsonObject { ["x"] = Reading.WinX, ["y"] = Reading.WinY, ["w"] = Reading.WinW, ["h"] = Reading.WinH }
                    : null,
            },
            // the welcome window at a manual start (shown once: turned off when shown, owner Q3 of the embedded-help plan)
            ["start"] = new JsonObject { ["welcome"] = Welcome },
            ["lock"] = new JsonObject
            {
                ["enabled"] = Lock.Enabled,
                ["pin"] = Lock.Pin is { } p
                    ? new JsonObject { ["alg"] = p.Alg, ["iterations"] = p.Iterations, ["salt"] = p.Salt, ["hash"] = p.Hash }
                    : null,
            },
        };
    }

    /// <summary>Tolerant parse of a (merged) settings object, then <see cref="Validated"/>.</summary>
    public static AppSettings FromJson(JsonObject? o)
    {
        var d = new AppSettings();
        var sd = d.Speech;
        var w = o?["words"] as JsonObject;
        var b = o?["bar"] as JsonObject;
        var s = o?["speech"] as JsonObject;
        var t = o?["text"] as JsonObject;
        var l = o?["lock"] as JsonObject;
        var rd = o?["reading"] as JsonObject;
        var st = o?["start"] as JsonObject;
        var rw = rd?["window"] as JsonObject;
        var free = b?["free"] as JsonObject;
        PinHash? pin = null;
        if (l?["pin"] is JsonObject p && Str(p["alg"]) is { } alg && Int(p["iterations"]) is int it &&
            Str(p["salt"]) is { } salt && Str(p["hash"]) is { } hash)
            pin = new PinHash(alg, it, salt, hash);
        return new AppSettings(
            WordCount: Int(w?["count"]) ?? d.WordCount,
            Size: ParseEnum(w?["size"], SizeNames, d.Size),
            Pictures: Bool(w?["pictures"]) ?? d.Pictures,
            Keys: ParseEnum(w?["keys"], KeyNames, d.Keys),
            Font: ParseEnum(w?["font"], FontNames, d.Font),
            Spacing: ParseEnum(w?["spacing"], SpacingNames, d.Spacing),
            Position: ParseEnum(b?["position"], PositionNames, d.Position),
            FreeX: Int(free?["x"]), FreeY: Int(free?["y"]),
            Speech: new SpeechSettings(
                Bool(s?["speakInserted"]) ?? sd.SpeakInserted, Bool(s?["speakWords"]) ?? sd.SpeakWords,
                Bool(s?["speakSentences"]) ?? sd.SpeakSentences, Str(s?["voice"]), Int(s?["rate"]) ?? sd.Rate,
                Int(s?["volume"]) ?? sd.Volume, Bool(s?["rereadEdits"]) ?? sd.RereadEdits),
            Lock: new LockSettings(Bool(l?["enabled"]) ?? false, pin),
            Punctuation: ParseEnum(t?["punctuationSpace"], PunctuationNames, d.Punctuation),
            Reading: new ReadingSettings(Bool(rd?["enabled"]) ?? true, Bool(rd?["clipboardFallback"]) ?? false, Bool(rd?["pictures"]),
                                         Int(rw?["x"]), Int(rw?["y"]), Int(rw?["w"]), Int(rw?["h"])),
            Welcome: Bool(st?["welcome"]) ?? d.Welcome,
            SpellingIndicator: Bool(w?["spellingIndicator"]) ?? d.SpellingIndicator,
            NamePictures: Bool(w?["namePictures"]) ?? d.NamePictures).Validated();
    }

    private static T ParseEnum<T>(JsonNode? n, IReadOnlyDictionary<T, string> names, T def) where T : struct
    {
        var v = Str(n);
        if (v is null) return def;
        foreach (var kv in names) if (string.Equals(kv.Value, v, StringComparison.OrdinalIgnoreCase)) return kv.Key;
        return def;
    }

    private static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    private static int? Int(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d) && !double.IsNaN(d)) return (int)Math.Clamp(Math.Round(d), int.MinValue, int.MaxValue);
        return null;
    }
}

/// <summary>Deep merge and path helpers on <see cref="JsonObject"/> (pure).</summary>
internal static class Json
{
    /// <summary>Copy every value of <paramref name="src"/> into <paramref name="dst"/>: objects merge recursively,
    /// anything else (including null and arrays) replaces.</summary>
    public static JsonObject MergeInto(JsonObject dst, JsonObject? src)
    {
        if (src is null) return dst;
        foreach (var (k, v) in src.ToList())
        {
            if (v is JsonObject so && dst[k] is JsonObject dobj) MergeInto(dobj, so);
            else dst[k] = Copy(v);
        }
        return dst;
    }

    public static JsonObject Clone(JsonObject? o) => Copy(o) as JsonObject ?? new JsonObject();

    /// <summary>Deep copy (JsonNode.DeepClone is .NET 8+; a node can have only one parent).</summary>
    public static JsonNode? Copy(JsonNode? n) => n is null ? null : JsonNode.Parse(n.ToJsonString());

    /// <summary>Dotted paths of every leaf (non-object value) of <paramref name="o"/>, e.g. <c>words.size</c>.</summary>
    public static IEnumerable<string> LeafPaths(JsonObject? o, string prefix = "")
    {
        if (o is null) yield break;
        foreach (var (k, v) in o)
        {
            var p = prefix.Length == 0 ? k : prefix + "." + k;
            if (v is JsonObject c) { foreach (var q in LeafPaths(c, p)) yield return q; }
            else yield return p;
        }
    }

    /// <summary>Remove the leaves at <paramref name="paths"/> (and objects left empty by that).</summary>
    public static JsonObject Without(JsonObject o, IEnumerable<string> paths)
    {
        var r = Clone(o);
        foreach (var path in paths)
        {
            var parts = path.Split('.');
            var stack = new List<JsonObject> { r };
            JsonObject cur = r;
            bool found = true;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (cur[parts[i]] is not JsonObject next) { found = false; break; }
                cur = next;
                stack.Add(cur);
            }
            if (!found) continue;
            cur.Remove(parts[^1]);
            for (int i = stack.Count - 1; i > 0; i--)
                if (stack[i].Count == 0) stack[i - 1].Remove(parts[i - 1]);
        }
        return r;
    }

    /// <summary>The node at a dotted path (found = the path exists, the node may be a JSON null).</summary>
    public static (bool Found, JsonNode? Node) Get(JsonObject o, string path)
    {
        var parts = path.Split('.');
        JsonObject cur = o;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next) return (false, null);
            cur = next;
        }
        return cur.ContainsKey(parts[^1]) ? (true, cur[parts[^1]]) : (false, null);
    }

    /// <summary>Set a copy of <paramref name="value"/> at a dotted path, creating the objects on the way.</summary>
    public static void Set(JsonObject o, string path, JsonNode? value)
    {
        var parts = path.Split('.');
        JsonObject cur = o;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (cur[parts[i]] is not JsonObject next) { next = new JsonObject(); cur[parts[i]] = next; }
            cur = next;
        }
        cur[parts[^1]] = Copy(value);
    }

    public static bool Same(JsonNode? a, JsonNode? b) => (a?.ToJsonString() ?? "null") == (b?.ToJsonString() ?? "null");

    /// <summary>Is <paramref name="path"/> locked, i.e. equal to or inside one of the locked leaf paths' objects
    /// (<c>bar.free</c> is locked when <c>bar.free.x</c> is).</summary>
    public static bool IsLocked(ISet<string> locked, string path) =>
        locked.Contains(path) || locked.Any(l => l.StartsWith(path + ".", StringComparison.Ordinal));
}
