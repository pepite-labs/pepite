using System;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>
/// A voice setting as <c>engine:name</c> (plan 08a): <c>sapi:Microsoft Hortense Desktop</c>, <c>piper:siwis</c>. The
/// engine prefix is case-insensitive. Up to 0.4.0 <c>speech.voice</c> held a bare SAPI name: any string without a
/// known prefix (including one with other colons) is such a legacy name and means <c>sapi:&lt;string&gt;</c>. Pure.
/// </summary>
internal readonly record struct VoiceId(string Engine, string Name)
{
    public const string Sapi = "sapi", Piper = "piper";

    public bool IsPiper => Engine == Piper;
    public bool IsSapi => Engine == Sapi;

    public override string ToString() => Engine + ":" + Name;

    /// <summary>null for null / blank (= the default voice).</summary>
    public static VoiceId? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        int c = s.IndexOf(':');
        if (c > 0)
        {
            var e = s.Substring(0, c);
            var n = s.Substring(c + 1);
            if (n.Length > 0)
            {
                if (e.Equals(Sapi, StringComparison.OrdinalIgnoreCase)) return new VoiceId(Sapi, n);
                if (e.Equals(Piper, StringComparison.OrdinalIgnoreCase)) return new VoiceId(Piper, n);
            }
        }
        return new VoiceId(Sapi, s);
    }

    /// <summary>The canonical form of a setting value (legacy SAPI name → <c>sapi:…</c>); null stays null.</summary>
    public static string? Normalize(string? s) => Parse(s)?.ToString();

    /// <summary>Rewrite <c>speech.voice</c> of a settings JSON object (a user file or a machine layer part) to its
    /// canonical form, in place. Returns the new value when it changed, else null.</summary>
    public static string? MigrateJson(JsonObject? root)
    {
        if (root?["speech"] is not JsonObject sp || sp["voice"] is not JsonValue v || !v.TryGetValue<string>(out var old)) return null;
        var now = Normalize(old);
        if (now is null || now == old) return null;
        sp["voice"] = now;
        return now;
    }
}
