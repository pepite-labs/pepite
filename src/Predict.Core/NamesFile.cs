using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Predict.Core;

/// <summary>One name of a « pepite-noms » database (plan 10C): the shipped « Noms propres » pack or a teacher's import.</summary>
/// <param name="Nom">the form shown and inserted (« Belgique », « Ypres »).</param>
/// <param name="Variantes">other spellings recognised (✓) and offered next to the name (« Ieper » → « Ypres », owner Q9).</param>
/// <param name="Rang">popularity 0–1 (the pack's ranking; a teacher's names default to 1).</param>
/// <param name="Niveau">1 = predicted; 2 = recognised, predicted only after a capital typed inside a sentence (owner Q2).</param>
/// <param name="Image">a relative path inside the database (PNG / JPEG), or null.</param>
/// <param name="Licence">the licence of the entry (its picture when it has one), resolved from the entry, its source or
/// the file; a teacher's import requires one (owner Q8).</param>
/// <param name="Restreint">owner decision 2026-11-15: a figure taught for the harm done (« restreint »: true) is recognised
/// (✓, never red) but offered only after a capital typed inside a sentence and at least 3 letters, always after the first
/// names, and never with a picture.</param>
public sealed record NameRecord(string Nom, IReadOnlyList<string> Variantes, string Categorie, double Rang, int Niveau,
                                string? Image, string? Source, string? Licence, string? Auteur, bool Restreint = false);

/// <summary>A parsed names database: its title, edition, licence, the attribution text to show, the names, and the
/// lines refused (number + reason, in French for the teacher's report).</summary>
public sealed record NamesDocument(string Titre, string? Edition, string? Licence, string? Attribution,
                                   IReadOnlyList<NameRecord> Noms, IReadOnlyList<string> Refused);

/// <summary>
/// The documented « pepite-noms » format (plan 10C, owner Q19b / Q20; <c>design/noms-format.md</c>), shared by the
/// shipped pack (built by <c>Data.Build/pepite-noms/build_names.py</c>) and the teacher's import, so there is one parser
/// and one rule:
/// <code>
/// noms.json  { "format": "pepite-noms", "version": 1, "titre": "…", "edition": "…", "licence": "…", "attribution": "…",
///              "sources": { "&lt;id&gt;": { "titre", "licence", "url" } },
///              "noms": [ { "nom", "variantes": [..], "categorie", "rang", "niveau", "image", "source", "licence", "auteur" } ] }
/// noms.csv   nom;variantes;categorie;image;source;licence;auteur      (UTF-8, header line, variants separated by |)
/// </code>
/// A name: 2–40 characters, starting with a capital, letters with inner spaces, hyphens, apostrophes or a dot after a
/// letter; never all capitals. Pure; the caller reads the files and checks the pictures.
/// </summary>
public static class NamesFile
{
    public const string Format = "pepite-noms";
    public const int Version = 1;
    public const int MaxLength = 40;

    /// <summary>Trimmed, NFC, typographic apostrophes → ', inner spaces collapsed.</summary>
    public static string Clean(string? s)
    {
        var t = (s ?? "").Normalize(NormalizationForm.FormC).Replace('’', '\'').Replace('‘', '\'').Replace(' ', ' ').Trim();
        var sb = new StringBuilder(t.Length);
        foreach (var c in t)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        return sb.ToString();
    }

    /// <summary>Null when <paramref name="name"/> (cleaned) is a valid name, else the French reason.</summary>
    public static string? CheckName(string? name)
    {
        var s = Clean(name);
        if (s.Length < 2) return "nom vide ou trop court";
        if (new StringInfo(s).LengthInTextElements > MaxLength) return "nom trop long (40 caractères au plus)";
        if (!char.IsUpper(s[0])) return "le nom doit commencer par une majuscule";
        int letters = 0, upper = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                letters++;
                if (char.IsUpper(c)) upper++;
                continue;
            }
            bool prevLetter = i > 0 && char.IsLetter(s[i - 1]);
            bool nextLetter = i + 1 < s.Length && char.IsLetter(s[i + 1]);
            if ((c is '-' or '\'' or ' ') && prevLetter && nextLetter) continue;
            if (c == ' ' && i > 0 && s[i - 1] == '.' && nextLetter) continue;               // « J. R. R. Tolkien »
            if (c == '.' && prevLetter && (i + 1 == s.Length || s[i + 1] == ' ')) continue;  // an initial
            return "caractère non permis : « " + c + " »";
        }
        if (letters >= 2 && upper == letters) return "nom tout en majuscules";
        return null;
    }

    /// <summary>Parse a <c>noms.json</c>. Throws <see cref="InvalidDataException"/> (French message) when the file is not
    /// a « pepite-noms » database or comes from a newer version; refuses bad entries one by one.</summary>
    /// <param name="maxNames">at most this many names (the rest refused); a teacher's import allows 5 000.</param>
    /// <param name="requireLicence">a teacher's import: an entry without a resolvable licence is refused.</param>
    public static NamesDocument ParseJson(string json, int maxNames = int.MaxValue, bool requireLicence = false) =>
        ParseJson(Encoding.UTF8.GetBytes(json ?? ""), maxNames, requireLicence);

    /// <summary>The same from the file's bytes (UTF-8, BOM or not), read with a forward-only <see cref="Utf8JsonReader"/>:
    /// no tree and no pooled document — the 31 000 names of the shipped pack leave nothing but the records (plan 10C review,
    /// memory). <c>sources</c> may come before or after <c>noms</c>: licences are resolved at the end.</summary>
    public static NamesDocument ParseJson(byte[] utf8, int maxNames = int.MaxValue, bool requireLicence = false) =>
        ParseJson(utf8, null, maxNames, requireLicence);

    /// <summary>Streaming form (plan 10C review, memory): each accepted name goes to <paramref name="emit"/> as soon as it is
    /// read, and the returned document's <c>Noms</c> stays empty — no list of 31 000 records is ever held. When the file's
    /// <c>sources</c> come after <c>noms</c>, the names are buffered until the end to resolve their licences.</summary>
    public static NamesDocument ParseJson(byte[] utf8, Action<NameRecord>? emit, int maxNames = int.MaxValue, bool requireLicence = false)
    {
        var span = new ReadOnlySpan<byte>(utf8);
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF) span = span[3..];
        var reader = new Utf8JsonReader(span, new JsonReaderOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        string? format = null, titre = null, edition = null, licence = null, attribution = null;
        int? version = null;
        var sources = new Dictionary<string, string?>(StringComparer.Ordinal);
        var raw = new List<RawName>();
        var refused = new List<string>();
        var sink = new Sink(emit);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        bool sourcesRead = false;
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);   // one string per category / source / licence / author
        string? Shared(string? x) { if (x is null) return null; if (!pool.TryGetValue(x, out var c)) pool[x] = c = x; return c; }
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new InvalidDataException("le fichier n'est pas un objet JSON");
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "format": format = Str(ref reader); break;
                    case "titre": titre = Str(ref reader); break;
                    case "edition": edition = Str(ref reader); break;
                    case "licence": licence = Str(ref reader); break;
                    case "attribution": attribution = Str(ref reader); break;
                    case "version": version = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) ? v : null; break;
                    case "sources" when reader.TokenType == JsonTokenType.StartObject:
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var id = reader.GetString()!;
                            reader.Read();
                            string? lic = null;
                            if (reader.TokenType == JsonTokenType.StartObject)
                                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                                {
                                    var k = reader.GetString();
                                    reader.Read();
                                    if (k == "licence") lic = Str(ref reader); else reader.Skip();
                                }
                            else reader.Skip();
                            sources[id] = lic;
                        }
                        sourcesRead = true;
                        break;
                    case "noms" when reader.TokenType == JsonTokenType.StartArray:
                        int line = 0;
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                        {
                            line++;
                            if (reader.TokenType != JsonTokenType.StartObject) { refused.Add($"entrée {line} : pas un objet"); reader.Skip(); continue; }
                            var r = new RawName { Line = line };
                            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                            {
                                var k = reader.GetString();
                                reader.Read();
                                switch (k)
                                {
                                    case "nom": r.Nom = Str(ref reader); break;
                                    case "categorie": r.Cat = Shared(Str(ref reader)); break;
                                    case "image": r.Image = Str(ref reader); break;
                                    case "source": r.Src = Shared(Str(ref reader)); break;
                                    case "licence": r.Lic = Shared(Str(ref reader)); break;
                                    case "auteur": r.Aut = Shared(Str(ref reader)); break;
                                    case "rang": r.Rang = reader.TokenType == JsonTokenType.Number ? reader.GetDouble() : null; break;
                                    case "niveau": r.Niveau = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var nv) ? nv : null; break;
                                    case "restreint": r.Restreint = reader.TokenType == JsonTokenType.True; break;
                                    case "variantes" when reader.TokenType == JsonTokenType.StartArray:
                                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                                            if (reader.TokenType == JsonTokenType.String) (r.Var ??= new List<string>()).Add(reader.GetString()!);
                                            else reader.Skip();
                                        break;
                                    default: reader.Skip(); break;
                                }
                            }
                            if (sourcesRead && format == Format) Put(r);   // licences resolvable now: no buffering
                            else raw.Add(r);
                        }
                        break;
                    default: reader.Skip(); break;
                }
            }
        }
        catch (JsonException ex) { throw new InvalidDataException("fichier JSON illisible (" + ex.Message + ")"); }
        if (format != Format) throw new InvalidDataException("ce n'est pas une base de noms Pépite (« format » doit valoir « pepite-noms »)");
        if (version is not int ver || ver < 1) throw new InvalidDataException("« version » manquante");
        if (ver > Version) throw new InvalidDataException("base de noms d'une version plus récente : mettez Pépite à jour");
        foreach (var r in raw) Put(r);
        return new NamesDocument(Clean(titre) is { Length: > 0 } t ? t : "Base de noms", edition, licence, attribution, sink.List, refused);

        void Put(RawName r)
        {
            var lic = r.Lic ?? (r.Src != null && sources.TryGetValue(r.Src, out var sl) ? sl : null) ?? licence;
            Add(sink, refused, seen, r.Line, r.Nom, r.Var, r.Cat, r.Rang, r.Niveau, r.Image, r.Src, Shared(lic), r.Aut, maxNames, requireLicence, r.Restreint);
        }
    }

    /// <summary>Where accepted names go: a list (the document's <c>Noms</c>), or a callback (streaming, the list stays empty).</summary>
    private sealed class Sink
    {
        private readonly Action<NameRecord>? _emit;
        public readonly List<NameRecord> List = new();
        public int Count;
        public Sink(Action<NameRecord>? emit) => _emit = emit;
        public void Put(NameRecord r) { Count++; if (_emit != null) _emit(r); else List.Add(r); }
    }

    private sealed class RawName
    {
        public int Line; public string? Nom, Cat, Image, Src, Lic, Aut; public double? Rang; public int? Niveau; public List<string>? Var;
        public bool Restreint;
    }

    private static string? Str(ref Utf8JsonReader r)
    {
        if (r.TokenType == JsonTokenType.String) return r.GetString();
        if (r.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray) r.Skip();
        return null;
    }

    /// <summary>Parse a <c>noms.csv</c> (UTF-8, <c>;</c>-separated, a header line, variants separated by <c>|</c>).
    /// <paramref name="defaultLicence"/>: the licence the teacher typed for the whole file, used for rows without one.</summary>
    public static NamesDocument ParseCsv(string text, string titre, int maxNames = int.MaxValue, bool requireLicence = false, string? defaultLicence = null)
    {
        var lines = (text ?? "").TrimStart('﻿').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0 || lines[0].Trim().Length == 0) throw new InvalidDataException("fichier CSV vide");
        var head = lines[0].Split(';').Select(h => h.Trim().ToLowerInvariant()).ToList();
        int Col(string name) => head.IndexOf(name);
        int cNom = Col("nom");
        if (cNom < 0) throw new InvalidDataException("la première ligne doit nommer les colonnes : nom;variantes;categorie;image;source;licence;auteur");
        int cVar = Col("variantes"), cCat = Col("categorie"), cImg = Col("image"), cSrc = Col("source"), cLic = Col("licence"), cAut = Col("auteur");
        var noms = new Sink(null);
        var refused = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Replace(";", "").Trim().Length == 0) continue;   // an empty row (a spreadsheet's trailing lines)
            var f = lines[i].Split(';');
            string? F(int c) => c >= 0 && c < f.Length && f[c].Trim().Length > 0 ? f[c].Trim() : null;
            var variants = (F(cVar) ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Add(noms, refused, seen, i + 1, F(cNom), variants, F(cCat), null, null, F(cImg), F(cSrc), F(cLic) ?? defaultLicence, F(cAut),
                maxNames, requireLicence);
        }
        return new NamesDocument(titre, null, defaultLicence, null, noms.List, refused);
    }

    private static void Add(Sink noms, List<string> refused, HashSet<string> seen, int line, string? nom, List<string>? variants,
                            string? cat, double? rang, int? niveau, string? image, string? src, string? lic, string? aut, int max, bool requireLicence,
                            bool restreint = false)
    {
        var name = Clean(nom);
        if (CheckName(name) is { } why) { refused.Add($"ligne {line} : {why}"); return; }
        if (!seen.Add(name)) { refused.Add($"ligne {line} : « {name} » en double"); return; }
        if (requireLicence && string.IsNullOrWhiteSpace(lic)) { refused.Add($"ligne {line} : « {name} » sans licence"); return; }
        if (noms.Count >= max)
        {
            // one message for the whole overflow (a 30 000-line file must not make 25 000 refusal strings)
            if (seen.Count == max + 1) refused.Add($"ligne {line} et suivantes : plus de {max} noms");
            return;
        }
        IReadOnlyList<string> vs = Array.Empty<string>();
        if (variants is { Count: > 0 })
        {
            var l = new List<string>(variants.Count);
            foreach (var v in variants)
            {
                var c = Clean(v);
                if (c != name && CheckName(c) is null && !l.Contains(c)) l.Add(c);
            }
            if (l.Count > 0) vs = l;
        }
        noms.Put(new NameRecord(name, vs, cat is { Length: > 0 } k && k == Clean(k) ? k : Clean(cat) is { Length: > 0 } k2 ? k2 : "nom", Math.Clamp(rang ?? 1.0, 0.001, 1.0),
                                niveau == 2 || restreint ? 2 : 1, restreint || string.IsNullOrWhiteSpace(image) ? null : image.Trim(), src, lic?.Trim(), aut?.Trim(),
                                restreint));
    }

}
