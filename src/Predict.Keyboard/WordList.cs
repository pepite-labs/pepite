using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>Why a word, a list or a spelling pair cannot be stored (the French sentence: <see cref="Strings.WordProblemText"/>).</summary>
internal enum WordProblem { Empty, TooLong, BadChars, Duplicate, TooManyWords, TooManyInList, TooManyLists, TooManyFautes, SamePair, TooManyExclus, ClassOnly }

/// <param name="W">the word as shown and suggested (capitals kept, plan 10 Q13).</param>
/// <param name="Src">« manuel » (added by hand), « lecon » (a lesson), « import » (a .txt or a .pepite rebase).</param>
/// <param name="Added">the day it was added (« 2026-11-14 »; a day, never a time).</param>
internal sealed record WordEntry(string W, string Src = WordEntry.Manual, string? Added = null, NameMeta? Meta = null)
{
    public const string Manual = "manuel", Lesson = "lecon", Import = "import";
}

/// <summary>Plan 10C: what a names database gives an entry of a « noms » list (<see cref="NamesImport"/>): its picture (a file
/// name in <c>profiles\&lt;id&gt;\images\</c>), the picture's licence, source and author (required / shown, owner Q8), its
/// category and the spellings recognised with it.</summary>
internal sealed record NameMeta(string? Image, string? Licence, string? Source, string? Auteur, string? Categorie, IReadOnlyList<string>? Variantes);

/// <summary>A named word list. <paramref name="Type"/>: « perso » (« Mes mots », « Mots de la classe ») or « lecon » (a
/// lesson, switched on / off by hand — lessons never expire, Q11). <paramref name="Raw"/>: keys this version does not
/// know, written back.</summary>
internal sealed record WordListDef(string Id, string Name, bool Active, string Type, IReadOnlyList<WordEntry> Words, JsonObject? Raw = null,
                                  string? Licence = null)
{
    /// <summary>« noms » (plan 10C): a names database imported by the teacher; its entries may hold spaces and dots (« La
    /// Louvière », « J. R. R. Tolkien »), a picture and its licence; <see cref="Licence"/> = the database's licence.</summary>
    public const string Personal = "perso", Lesson = "lecon", Names = "noms";
    public bool IsLesson => Type == Lesson;
    public bool IsNames => Type == Names;
}

/// <summary>« Mes fautes » (Q12): what the pupil writes → the word they want. Manual only.</summary>
internal sealed record SpellingPair(string Ecrit, string Mot);

/// <summary>
/// The words of one profile (plan 10A, D10): <c>profiles\&lt;id&gt;\mots.json</c> =
/// <c>{ schema, listes: [ { id, nom, active, type, mots: [ { w, src, ajoute } ] } ], fautes: [ { ecrit, mot } ], masques: [ … ] }</c>.
/// <c>masques</c> (a pupil only): « Classe » words this pupil does not want proposed (Q2). Pure and immutable: every
/// change returns a new book (or the reason it was refused). Parsing is tolerant (bad entries dropped, limits applied,
/// unknown keys kept). Words are only ever added on request (owner 2026-11-14: no automatic learning, nothing typed
/// is recorded). Entry rules: 1–40 characters, letters (any case, accents, œ æ ç), an inner <c>-</c> or <c>'</c> (Q16:
/// « Jean-Pierre », « aujourd'hui »); unique per list as stored (« Pierre » and « pierre » may both exist). Limits (Q15):
/// 5 000 words for a pupil, 10 000 for « Classe », 500 « Mes fautes », 50 lists, 500 words per lesson.
/// <c>exclus</c> (« Classe » only, 0.11.0, D35): « Mots à ne jamais proposer », see <see cref="Exclus"/>.
/// </summary>
internal sealed record WordBook(IReadOnlyList<WordListDef> Lists, IReadOnlyList<SpellingPair> Fautes, IReadOnlyList<string> Masques,
                                JsonObject? Raw = null)
{
    public const int Schema = 1;
    public const int MaxPupilWords = 5000, MaxClassWords = 10000, MaxFautes = 500, MaxLists = 50, MaxPerLesson = 500, MaxLength = 40;
    /// <summary>At most 500 « Mots à ne jamais proposer » (plan offensive-words B1; the machine file has the same limit).</summary>
    public const int MaxExclus = 500;

    /// <summary>
    /// 0.11.0 (plan offensive-words B1, D35): the teacher's « Mots à ne jamais proposer » — whole words added to the built-in
    /// exclusion list for every profile of the PC. « Classe » only: <see cref="FromJson"/> drops them from a pupil's file,
    /// and no pupil editor shows them, so a pupil can neither hide nor remove one (<see cref="WordLayering.Of"/> hands them on
    /// untouched by <see cref="Masques"/>). Same entry rule as a word, at most <see cref="MaxExclus"/>, unique ignoring case.
    /// </summary>
    public IReadOnlyList<string> Exclus { get; init; } = Array.Empty<string>();
    /// <summary>The default list's id: « Mes mots » (a pupil), « Mots de la classe » (« Classe »).</summary>
    public const string DefaultListId = "mes-mots";
    private const string Base32 = "abcdefghijklmnopqrstuvwxyz234567";

    public static readonly WordBook Empty = new(Array.Empty<WordListDef>(), Array.Empty<SpellingPair>(), Array.Empty<string>());

    public int WordCount => Lists.Sum(l => l.Words.Count);
    public bool IsEmpty => Lists.Count == 0 && Fautes.Count == 0 && Masques.Count == 0 && Exclus.Count == 0;
    public WordListDef? Default => Lists.FirstOrDefault(l => l.Id == DefaultListId);
    public WordListDef? Get(string id) => Lists.FirstOrDefault(l => l.Id == id);

    public static int MaxWords(bool isClass) => isClass ? MaxClassWords : MaxPupilWords;

    // ------------------------------------------------------------------ entry rules
    /// <summary>Trimmed, NFC, typographic apostrophe → <c>'</c>.</summary>
    public static string Clean(string? word) => (word ?? "").Trim().Normalize().Replace('’', '\'').Replace('‘', '\'');

    /// <summary>Null when <paramref name="word"/> may be stored in a list of <paramref name="type"/> (a « noms » list takes
    /// the names-database rule, <see cref="NamesFile.CheckName"/>).</summary>
    public static WordProblem? CheckFor(string type, string? word) =>
        type != WordListDef.Names ? Check(word)
        : NamesFile.CheckName(word) is null ? null : string.IsNullOrWhiteSpace(word) ? WordProblem.Empty : WordProblem.BadChars;

    /// <summary>Null when <paramref name="word"/> (cleaned) may be stored.</summary>
    public static WordProblem? Check(string? word)
    {
        var w = Clean(word);
        if (w.Length == 0) return WordProblem.Empty;
        if (new StringInfo(w).LengthInTextElements > MaxLength) return WordProblem.TooLong;
        for (int i = 0; i < w.Length; i++)
        {
            char c = w[i];
            if (char.IsLetter(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            // an inner hyphen or apostrophe, between two letters
            if ((c == '-' || c == '\'') && i > 0 && i < w.Length - 1 && char.IsLetter(w[i - 1]) && char.IsLetter(w[i + 1])) continue;
            return WordProblem.BadChars;
        }
        return null;
    }

    private static string NewListId(IEnumerable<string> taken)
    {
        var t = new HashSet<string>(taken, StringComparer.Ordinal);
        while (true)
        {
            var b = RandomNumberGenerator.GetBytes(6);
            var id = "l-" + new string(b.Select(x => Base32[x & 31]).ToArray());
            if (!t.Contains(id)) return id;
        }
    }

    public static string Today(DateTime now) => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ changes (pure)
    private WordBook WithList(WordListDef l)
    {
        var lists = Lists.ToList();
        int i = lists.FindIndex(x => x.Id == l.Id);
        if (i >= 0) lists[i] = l; else lists.Insert(l.Id == DefaultListId ? 0 : lists.Count, l);
        return this with { Lists = lists };
    }

    /// <summary>Add <paramref name="word"/> to a list (null = the default list, created when missing).</summary>
    public WordBook AddWord(string? listId, string word, string src, DateTime now, bool isClass, out WordProblem? problem)
    {
        problem = Check(word);
        if (problem != null) return this;
        var w = Clean(word);
        var list = listId is null ? Default ?? new WordListDef(DefaultListId, isClass ? Strings.ClassWordsList : Strings.MyWordsList, true, WordListDef.Personal,
                                                               Array.Empty<WordEntry>())
                                  : Get(listId);
        if (list is null) { problem = WordProblem.Empty; return this; }
        if (list.Words.Any(e => e.W == w)) { problem = WordProblem.Duplicate; return this; }
        if (WordCount >= MaxWords(isClass)) { problem = WordProblem.TooManyWords; return this; }
        if (list.IsLesson && list.Words.Count >= MaxPerLesson) { problem = WordProblem.TooManyInList; return this; }
        if (Get(list.Id) is null && Lists.Count >= MaxLists) { problem = WordProblem.TooManyLists; return this; }
        return WithList(list with { Words = list.Words.Append(new WordEntry(w, src, Today(now))).ToList() });
    }

    /// <summary>Append many words to the default list in one pass (an import rebase): invalid and duplicate words are
    /// skipped, the profile's limit stops the copy (the first words kept). O(n), unlike repeated <see cref="AddWord"/>.</summary>
    public WordBook AddWords(IEnumerable<string> words, string src, DateTime now, bool isClass)
    {
        var list = Default ?? new WordListDef(DefaultListId, isClass ? Strings.ClassWordsList : Strings.MyWordsList, true, WordListDef.Personal,
                                              Array.Empty<WordEntry>());
        if (Get(list.Id) is null && Lists.Count >= MaxLists) return this;
        var have = new HashSet<string>(list.Words.Select(e => e.W), StringComparer.Ordinal);
        int room = MaxWords(isClass) - WordCount;
        var added = new List<WordEntry>();
        var day = Today(now);
        foreach (var raw in words)
        {
            if (added.Count >= room) break;
            if (Check(raw) != null) continue;
            var w = Clean(raw);
            if (have.Add(w)) added.Add(new WordEntry(w, src, day));
        }
        return added.Count == 0 ? this : WithList(list with { Words = list.Words.Concat(added).ToList() });
    }

    public WordBook RemoveWord(string listId, string word)
    {
        var list = Get(listId);
        if (list is null) return this;
        return WithList(list with { Words = list.Words.Where(e => e.W != word).ToList() });
    }

    /// <summary>« Modifier… »: <paramref name="old"/> becomes <paramref name="word"/> in the same place.</summary>
    public WordBook ReplaceWord(string listId, string old, string word, out WordProblem? problem)
    {
        var list = Get(listId);
        problem = CheckFor(list?.Type ?? WordListDef.Personal, word);
        if (problem != null || list is null) { problem ??= WordProblem.Empty; return this; }
        var w = list.IsNames ? NamesFile.Clean(word) : Clean(word);
        if (w == old) return this;
        if (list.Words.Any(e => e.W == w)) { problem = WordProblem.Duplicate; return this; }
        return WithList(list with { Words = list.Words.Select(e => e.W == old ? e with { W = w } : e).ToList() });
    }

    /// <summary>A new named list (a lesson, a .txt import): the valid words, at most <see cref="MaxPerLesson"/> for a
    /// lesson, within the profile's limit. Returns the new list's id (or null with the problem).</summary>
    public WordBook AddList(string name, string type, IEnumerable<string> words, string src, DateTime now, bool isClass,
                            out string? id, out WordProblem? problem)
    {
        id = null;
        problem = null;
        if (Lists.Count >= MaxLists) { problem = WordProblem.TooManyLists; return this; }
        var entries = new List<WordEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in words)
        {
            if (Check(raw) != null) continue;
            var w = Clean(raw);
            if (!seen.Add(w)) continue;
            entries.Add(new WordEntry(w, src, Today(now)));
        }
        if (type == WordListDef.Lesson && entries.Count > MaxPerLesson) { problem = WordProblem.TooManyInList; return this; }
        if (WordCount + entries.Count > MaxWords(isClass)) { problem = WordProblem.TooManyWords; return this; }
        var nm = string.IsNullOrWhiteSpace(name) ? Strings.LessonDefaultName : name.Trim();
        if (nm.Length > 60) nm = nm.Substring(0, 60);
        id = NewListId(Lists.Select(l => l.Id));
        return this with { Lists = Lists.Append(new WordListDef(id, nm, true, type, entries)).ToList() };
    }

    /// <summary>Plan 10C: add a names database built by <see cref="NamesImport.Build"/> as a new « noms » list (a fresh id),
    /// within the profile's limits (a names database counts as words: 5 000 for a pupil, 10 000 for « Classe »).</summary>
    public WordBook AddNamesList(WordListDef names, bool isClass, out string? id, out WordProblem? problem)
    {
        id = null;
        problem = null;
        if (Lists.Count >= MaxLists) { problem = WordProblem.TooManyLists; return this; }
        if (WordCount + names.Words.Count > MaxWords(isClass)) { problem = WordProblem.TooManyWords; return this; }
        id = NewListId(Lists.Select(l => l.Id));
        return this with { Lists = Lists.Append(names with { Id = id, Type = WordListDef.Names }).ToList() };
    }

    /// <summary>The picture files the « noms » lists of this book refer to (to export them, or to clean unused ones).</summary>
    public IEnumerable<string> ImageFiles => Lists.Where(l => l.IsNames).SelectMany(l => l.Words).Select(e => e.Meta?.Image)
                                                  .Where(i => i != null).Cast<string>().Distinct(StringComparer.Ordinal);

    public WordBook RemoveList(string id) => this with { Lists = Lists.Where(l => l.Id != id).ToList() };

    public WordBook SetActive(string id, bool on) => Get(id) is { } l ? WithList(l with { Active = on }) : this;

    /// <summary>Add or change a « Mes fautes » pair (one pair per spelling, compared without accents or case).</summary>
    public WordBook SetFaute(string ecrit, string mot, out WordProblem? problem)
    {
        problem = Check(ecrit) ?? Check(mot);
        if (problem != null) return this;
        var e = Clean(ecrit);
        var m = Clean(mot);
        if (string.Equals(e, m, StringComparison.OrdinalIgnoreCase)) { problem = WordProblem.SamePair; return this; }
        var key = PhoneticKey.StripLower(e);
        var list = Fautes.Where(p => PhoneticKey.StripLower(p.Ecrit) != key).ToList();
        if (list.Count >= MaxFautes) { problem = WordProblem.TooManyFautes; return this; }
        list.Add(new SpellingPair(e, m));
        return this with { Fautes = list };
    }

    public WordBook RemoveFaute(string ecrit) => this with { Fautes = Fautes.Where(p => p.Ecrit != ecrit).ToList() };

    /// <summary>« Ne pas me les proposer » (a pupil, Q2): a « Classe » word hidden for this pupil, or shown again.</summary>
    public WordBook SetHidden(string word, bool hidden)
    {
        var m = Masques.Where(x => x != word).ToList();
        if (hidden) m.Add(word);
        return this with { Masques = m };
    }

    /// <summary>« Mots à ne jamais proposer » (« Classe », plan offensive-words B1): add a word (the entry rule of a word,
    /// unique ignoring case, at most <see cref="MaxExclus"/>).</summary>
    public WordBook AddExclu(string word, bool isClass, out WordProblem? problem)
    {
        if (!isClass) { problem = WordProblem.ClassOnly; return this; }   // a pupil's book never holds one
        problem = Check(word);
        if (problem != null) return this;
        var w = Clean(word);
        if (Exclus.Any(x => string.Equals(x, w, StringComparison.OrdinalIgnoreCase))) { problem = WordProblem.Duplicate; return this; }
        if (Exclus.Count >= MaxExclus) { problem = WordProblem.TooManyExclus; return this; }
        return this with { Exclus = Exclus.Append(w).ToList() };
    }

    public WordBook RemoveExclu(string word) =>
        Exclus.Contains(word) ? this with { Exclus = Exclus.Where(x => x != word).ToList() } : this;

    /// <summary>« Effacer tous mes mots… »: the lists, « Mes fautes » and hidden words go; unknown keys stay. The « Mots à
    /// ne jamais proposer » stay too: erasing the class's words must not bring back a word the teacher banned (they are
    /// removed one by one).</summary>
    public WordBook Cleared() => Empty with { Raw = Raw, Exclus = Exclus };

    // ------------------------------------------------------------------ JSON
    public JsonObject ToJson()
    {
        var o = Json.Clone(Raw);
        o["schema"] = Schema;
        o["listes"] = new JsonArray(Lists.Select(l =>
        {
            var lo = Json.Clone(l.Raw);
            lo["id"] = l.Id;
            lo["nom"] = l.Name;
            lo["active"] = l.Active;
            lo["type"] = l.Type;
            if (l.Licence != null) lo["licence"] = l.Licence;
            lo["mots"] = new JsonArray(l.Words.Select(e =>
            {
                var eo = new JsonObject { ["w"] = e.W, ["src"] = e.Src };
                if (e.Added != null) eo["ajoute"] = e.Added;
                if (e.Meta is { } m)
                {
                    if (m.Image != null) eo["image"] = m.Image;
                    if (m.Licence != null) eo["licence"] = m.Licence;
                    if (m.Source != null) eo["source"] = m.Source;
                    if (m.Auteur != null) eo["auteur"] = m.Auteur;
                    if (m.Categorie != null) eo["categorie"] = m.Categorie;
                    if (m.Variantes is { Count: > 0 } vs) eo["variantes"] = new JsonArray(vs.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                }
                return (JsonNode?)eo;
            }).ToArray());
            return (JsonNode?)lo;
        }).ToArray());
        o["fautes"] = new JsonArray(Fautes.Select(p => (JsonNode?)new JsonObject { ["ecrit"] = p.Ecrit, ["mot"] = p.Mot }).ToArray());
        o["masques"] = new JsonArray(Masques.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
        if (Exclus.Count > 0) o["exclus"] = new JsonArray(Exclus.Select(m => (JsonNode?)JsonValue.Create(m)).ToArray());
        return o;
    }

    /// <summary>Tolerant parse: bad entries dropped, the limits applied (the first words kept). <paramref name="overLimit"/>:
    /// the file held more than the limits allow (an import refuses such a file).</summary>
    public static WordBook FromJson(JsonObject? o, bool isClass, out bool overLimit)
    {
        overLimit = false;
        if (o is null) return Empty;
        static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        var lists = new List<WordListDef>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int total = 0, max = MaxWords(isClass);
        foreach (var n in o["listes"] as JsonArray ?? new JsonArray())
        {
            if (n is not JsonObject lo) continue;
            if (lists.Count >= MaxLists) { overLimit = true; break; }
            var id = S(lo["id"]) is { Length: > 0 and <= 40 } sid && ids.Add(sid) ? sid : NewListId(ids);
            ids.Add(id);
            var type = S(lo["type"]) switch { WordListDef.Lesson => WordListDef.Lesson, WordListDef.Names => WordListDef.Names, _ => WordListDef.Personal };
            var name = S(lo["nom"])?.Trim() is { Length: > 0 } nm ? (nm.Length > 60 ? nm.Substring(0, 60) : nm)
                     : id == DefaultListId ? (isClass ? Strings.ClassWordsList : Strings.MyWordsList) : Strings.LessonDefaultName;
            bool active = !(lo["active"] is JsonValue av && av.TryGetValue<bool>(out var a) && !a);
            var words = new List<WordEntry>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var wn in lo["mots"] as JsonArray ?? new JsonArray())
            {
                var w = S(wn is JsonObject wo ? wo["w"] : wn);
                if (w is null || CheckFor(type, w) != null) continue;
                w = type == WordListDef.Names ? NamesFile.Clean(w) : Clean(w);
                if (!seen.Add(w)) continue;
                if (total >= max || (type == WordListDef.Lesson && words.Count >= MaxPerLesson)) { overLimit = true; break; }
                var src = S((wn as JsonObject)?["src"]) is { } sv && sv is WordEntry.Manual or WordEntry.Lesson or WordEntry.Import ? sv : WordEntry.Manual;
                var added = S((wn as JsonObject)?["ajoute"]) is { Length: 10 } d && DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ? d : null;
                words.Add(new WordEntry(w, src, added, type == WordListDef.Names ? MetaOf(wn as JsonObject) : null));
                total++;
            }
            lists.Add(new WordListDef(id, name, active, type, words, Unknown(lo, "id", "nom", "active", "type", "mots", "licence"), S(lo["licence"])));
        }
        var fautes = new List<SpellingPair>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in o["fautes"] as JsonArray ?? new JsonArray())
        {
            if (n is not JsonObject fo || S(fo["ecrit"]) is not { } e || S(fo["mot"]) is not { } m || Check(e) != null || Check(m) != null) continue;
            if (!keys.Add(PhoneticKey.StripLower(Clean(e)))) continue;
            if (fautes.Count >= MaxFautes) { overLimit = true; break; }
            fautes.Add(new SpellingPair(Clean(e), Clean(m)));
        }
        var masques = (o["masques"] as JsonArray ?? new JsonArray()).Select(S).Where(s => s != null && Check(s) == null)
                      .Select(s => Clean(s)).Distinct(StringComparer.Ordinal).Take(MaxClassWords).ToList();
        // « Mots à ne jamais proposer »: « Classe » only (a pupil's file never brings one, and never keeps the key)
        var exclus = new List<string>();
        if (isClass)
        {
            var seenX = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in (o["exclus"] as JsonArray ?? new JsonArray()).Select(S))
            {
                if (s is null || Check(s) != null || !seenX.Add(Clean(s))) continue;
                if (exclus.Count >= MaxExclus) { overLimit = true; break; }
                exclus.Add(Clean(s));
            }
        }
        return new WordBook(lists, fautes, isClass ? Array.Empty<string>() : masques, Unknown(o, "schema", "listes", "fautes", "masques", "exclus"))
               { Exclus = exclus };
    }

    /// <summary>The names-database fields of a « noms » entry (a picture is a bare file name: no folder, no « .. »).</summary>
    private static NameMeta? MetaOf(JsonObject? o)
    {
        if (o is null) return null;
        static string? S(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length <= 500 ? s : null;
        var img = S(o["image"]);
        if (img != null && !NamesImport.IsStoredImageName(img)) img = null;
        var vs = (o["variantes"] as JsonArray ?? new JsonArray()).Select(S).Where(v => v != null && NamesFile.CheckName(v) is null)
                 .Select(v => NamesFile.Clean(v)).Distinct(StringComparer.Ordinal).Take(20).ToList();
        var m = new NameMeta(img, S(o["licence"]), S(o["source"]), S(o["auteur"]), S(o["categorie"]), vs.Count > 0 ? vs : null);
        return m.Image is null && m.Licence is null && m.Source is null && m.Auteur is null && m.Categorie is null && m.Variantes is null ? null : m;
    }

    /// <summary>A copy of the keys of <paramref name="o"/> this version does not know (null when there are none); the
    /// word arrays themselves are never copied twice.</summary>
    private static JsonObject? Unknown(JsonObject o, params string[] known)
    {
        JsonObject? r = null;
        foreach (var (k, v) in o)
            if (Array.IndexOf(known, k) < 0) (r ??= new JsonObject())[k] = Json.Copy(v);
        return r;
    }
}
