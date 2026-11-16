using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Predict.Keyboard;

/// <summary>The 8 named colours of a profile picture (D29, Q9): high-contrast, each with a name a screen reader says
/// (<see cref="Strings.ColorName"/>). No personal pictures: an ARASAAC pictogram on this colour, or the initial.</summary>
internal enum ProfileColor { Rouge, Orange, Jaune, Vert, Bleu, Violet, Rose, Marron }

/// <param name="Picto">the word whose ARASAAC pictogram is shown (null = the initial of the name).</param>
internal sealed record Avatar(string? Picto, ProfileColor Color)
{
    public const int MaxPicto = 40;
    public static readonly Avatar Default = new(null, ProfileColor.Bleu);

    /// <summary>JSON names of the colours (an admin or a teacher can read the file).</summary>
    public static readonly IReadOnlyDictionary<ProfileColor, string> ColorNames = new Dictionary<ProfileColor, string>
    {
        [ProfileColor.Rouge] = "rouge", [ProfileColor.Orange] = "orange", [ProfileColor.Jaune] = "jaune", [ProfileColor.Vert] = "vert",
        [ProfileColor.Bleu] = "bleu", [ProfileColor.Violet] = "violet", [ProfileColor.Rose] = "rose", [ProfileColor.Marron] = "marron",
    };

    /// <summary>sRGB of each colour (dark enough for white text, distinct from each other).</summary>
    public static uint Argb(ProfileColor c) => c switch
    {
        ProfileColor.Rouge => 0xFFC62828,
        ProfileColor.Orange => 0xFFE65100,
        ProfileColor.Jaune => 0xFFF9A825,
        ProfileColor.Vert => 0xFF2E7D32,
        ProfileColor.Bleu => 0xFF1565C0,
        ProfileColor.Violet => 0xFF6A1B9A,
        ProfileColor.Rose => 0xFFAD1457,
        _ => 0xFF5D4037,
    };

    public JsonObject ToJson() => new() { ["picto"] = Picto, ["color"] = ColorNames[Color] };

    public static Avatar FromJson(JsonNode? n)
    {
        var o = n as JsonObject;
        string? picto = o?["picto"] is JsonValue pv && pv.TryGetValue<string>(out var p) && p.Trim().Length > 0 ? p.Trim() : null;
        if (picto != null && picto.Length > MaxPicto) picto = picto.Substring(0, MaxPicto);
        var color = Default.Color;
        if (o?["color"] is JsonValue cv && cv.TryGetValue<string>(out var c))
            foreach (var kv in ColorNames) if (string.Equals(kv.Value, c, StringComparison.OrdinalIgnoreCase)) color = kv.Key;
        return new Avatar(picto, color);
    }
}

/// <summary>Why a name cannot be used.</summary>
internal enum NameProblem { Empty, TooLong, ControlChars, Duplicate }

/// <summary>
/// One profile (plan 09, D29): « Classe » (id <see cref="ClassId"/>, the inherited layer) or a pupil (<c>p-</c> + 6
/// random base32 characters). <see cref="Layer"/> holds only the settings that differ from what the profile inherits.
/// The file <c>profiles\&lt;id&gt;\profile.json</c> = <c>{ schema, id, name, avatar{picto,color}, created, settings }</c>;
/// parsing is tolerant and unknown keys are written back (<see cref="Raw"/>). The first name is personal data: it stays
/// in this file and on screen, never in a log (logs carry the id).
/// </summary>
internal sealed record Profile(string Id, string Name, Avatar Avatar, DateTime CreatedUtc, JsonObject Layer, JsonObject? Raw = null)
{
    public const int Schema = 1;
    public const int MaxProfiles = 40;   // « Classe » included (owner, Q13)
    public const int MaxName = 30;
    public const string ClassId = "classe";
    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    public bool IsClass => Id == ClassId;

    /// <summary><c>p-</c> + 6 random base32 characters, not in <paramref name="taken"/>.</summary>
    public static string NewId(ICollection<string>? taken = null)
    {
        while (true)
        {
            var b = RandomNumberGenerator.GetBytes(6);
            var id = "p-" + new string(b.Select(x => Base32[x & 31]).ToArray());
            if (taken is null || !taken.Contains(id)) return id;
        }
    }

    /// <summary>A safe folder name: « classe » or <c>p-</c> + 6 base32 characters.</summary>
    public static bool IsValidId(string? id) =>
        id == ClassId || id is { Length: 8 } && id.StartsWith("p-", StringComparison.Ordinal) && id.Skip(2).All(c => Base32.Contains(c));

    /// <summary>Trimmed, inner runs of white space made one space, NFC.</summary>
    public static string CleanName(string? name) =>
        string.Join(' ', (name ?? "").Normalize().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Null when <paramref name="name"/> (cleaned) may be used: 1–30 characters, no control character, unique
    /// ignoring case among <paramref name="others"/>.</summary>
    public static NameProblem? CheckName(string? name, IEnumerable<string> others)
    {
        var n = CleanName(name);
        if (n.Length == 0) return NameProblem.Empty;
        if (new StringInfo(n).LengthInTextElements > MaxName) return NameProblem.TooLong;
        if (n.Any(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
                                                                                  or UnicodeCategory.ParagraphSeparator))
            return NameProblem.ControlChars;
        if (others.Any(o => string.Compare(CleanName(o), n, CultureInfo.GetCultureInfo("fr-FR"),
                                           CompareOptions.IgnoreCase) == 0)) return NameProblem.Duplicate;
        return null;
    }

    /// <summary>The first letter of the name (the picture without a pictogram).</summary>
    public string Initial => Name.Length == 0 ? "?" : new StringInfo(Name).SubstringByTextElements(0, 1).ToUpperInvariant();

    public JsonObject ToJson()
    {
        var o = Json.Clone(Raw);
        o["schema"] = Schema;
        o["id"] = Id;
        o["name"] = Name;
        o["avatar"] = Avatar.ToJson();
        o["created"] = CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        o["settings"] = Json.Clone(Layer);
        return o;
    }

    /// <summary>Tolerant parse; the folder name wins over a different <c>id</c> in the file.</summary>
    public static Profile FromJson(JsonObject o, string folderId, string fallbackName)
    {
        var name = o["name"] is JsonValue nv && nv.TryGetValue<string>(out var n) ? CleanName(n) : "";
        if (name.Length == 0) name = fallbackName;
        if (new StringInfo(name).LengthInTextElements > MaxName) name = new StringInfo(name).SubstringByTextElements(0, MaxName);
        var created = o["created"] is JsonValue cv && cv.TryGetValue<string>(out var cs) &&
                      DateTime.TryParse(cs, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
                      ? d : DateTime.UnixEpoch;
        var layer = Json.Clone(o["settings"] as JsonObject);
        VoiceId.MigrateJson(layer);
        return new Profile(folderId, name, Avatar.FromJson(o["avatar"]), created, layer, Json.Clone(o));
    }
}
