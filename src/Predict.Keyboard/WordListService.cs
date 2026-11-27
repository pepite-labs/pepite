using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Predict.Core;

namespace Predict.Keyboard;

/// <summary>
/// The personal words of the running app (plan 10A, UI thread): builds the active profile's snapshot (« Classe » ∪ its
/// own lists − hidden words, <see cref="WordLayering"/>) into a <see cref="UserLexicon"/> and hands it to the engine
/// (<c>apply</c> = <c>engine.User = …</c>, one reference swap) at start, on a profile switch, after an edit or an
/// import. Edits go through <see cref="Update"/> (the store writes them). Logs carry ids and counts, never a word.
/// Words are only added on request: nothing the pupil types is recorded (owner 2026-11-14).
/// </summary>
internal sealed class WordListService
{
    private readonly ProfileStore _profiles;
    private readonly Action<UserLexicon> _apply;
    private readonly Action<string>? _log;
    private readonly Func<DateTime> _now;

    public WordListStore Store { get; }
    /// <summary>A profile's words changed (the editors refresh).</summary>
    public event Action<string>? Changed;
    /// <summary>The last snapshot handed to the engine.</summary>
    public UserLexicon Current { get; private set; } = UserLexicon.Empty;
    public double LastBuildMs { get; private set; }
    private readonly System.Collections.Generic.List<(string Id, WordBook Own, WordBook? Class, UserLexicon Lex)> _recent = new();   // lock: itself

    /// <param name="post">runs an action on the UI thread (the dispatcher). Null = everything synchronous on the caller's
    /// thread (tests); otherwise a snapshot that is not cached is read and built on a worker thread and swapped in on the
    /// UI thread when ready.</param>
    public WordListService(WordListStore store, ProfileStore profiles, Action<UserLexicon> apply, Action<string>? log, Func<DateTime>? now = null,
                           Action<Action>? post = null)
    {
        Store = store;
        _profiles = profiles;
        _apply = apply;
        _log = log;
        _now = now ?? (() => DateTime.Now);
        _post = post;
    }

    private readonly Action<Action>? _post;
    private int _gen;                 // UI thread: the latest Rebuild; an older background result is dropped
    private string? _appliedId;       // UI thread: the profile whose words the engine holds

    public DateTime Now => _now();

    /// <summary>The words <paramref name="id"/> inherits (« Classe »'s), or null for « Classe » itself / no readable class
    /// (the same rule as the settings layers, <see cref="ProfileStore.ClassLayerFor"/>).</summary>
    public WordBook? ClassBookFor(string id) =>
        HasClass(id) ? Store.Get(Profile.ClassId) : null;

    private bool HasClass(string id) => id != Profile.ClassId && _profiles.Class is { Unreadable: false };

    public WordBook BookOf(string id) => Store.Get(id);

    /// <summary>Plan 10C: the folder of a profile's pictures (imported names databases), or null in a session without files.</summary>
    public string? ImagesDir(string id) => _profiles.InMemory ? null : System.IO.Path.Combine(_profiles.ProfilesDir, id, "images");

    /// <summary>Plan 10C (review): delete the pictures of <paramref name="id"/> its lists no longer use (a names list deleted,
    /// the words erased, an import refused by the limits). Logs the count only.</summary>
    public void CleanImages(string id)
    {
        if (ImagesDir(id) is not { } dir) return;
        int n = NamesImport.DeleteUnreferenced(dir, Store.Get(id).ImageFiles);
        if (n > 0) _log?.Invoke($"words: {n} unused pictures deleted for {id}");
    }

    /// <summary>Plan 10C: the pictures of the active lists of <paramref name="id"/> and of its « Classe » (exact form and its
    /// variants → file), for <see cref="WordPictures.SetLists"/>.</summary>
    public (System.Collections.Generic.IReadOnlyDictionary<string, string> Own, System.Collections.Generic.IReadOnlyDictionary<string, string> Class) PictureMaps(string id)
    {
        System.Collections.Generic.Dictionary<string, string> Of(string pid, WordBook? b)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            var dir = ImagesDir(pid);
            if (b is null || dir is null) return d;
            foreach (var l in b.Lists)
            {
                if (!l.Active || !l.IsNames) continue;
                foreach (var e in l.Words)
                {
                    if (e.Meta?.Image is not { } img || !NamesImport.IsStoredImageName(img)) continue;
                    var path = System.IO.Path.Combine(dir, img);
                    d.TryAdd(e.W, path);
                    if (e.Meta.Variantes != null) foreach (var v in e.Meta.Variantes) d.TryAdd(v, path);
                }
            }
            return d;
        }
        var own = Of(id, Store.Get(id));
        var cls = ClassBookFor(id);
        return (own, Of(Profile.ClassId, cls));
    }

    public EffectiveWords EffectiveOf(string id) => WordLayering.Of(Store.Get(id), ClassBookFor(id));

    private sealed record Built(string Id, EffectiveWords Eff, UserLexicon Lex, bool Reused, double Ms, IReadOnlyList<string> ClassExclus);

    /// <summary>Read the books (from the files the first time) and build — or reuse — the snapshot. Any thread.</summary>
    private Built Compute(string id, bool hasClass)
    {
        var sw = Stopwatch.StartNew();
        var own = Store.Get(id);
        var cls = hasClass ? Store.Get(Profile.ClassId) : null;
        var eff = WordLayering.Of(own, cls);
        // the last two snapshots are kept (books are immutable: the same instances = the same words), so going back and
        // forth between two pupils does not rebuild
        UserLexicon? lex = null;
        lock (_recent)
        {
            int hit = _recent.FindIndex(r => r.Id == id && ReferenceEquals(r.Own, own) && ReferenceEquals(r.Class, cls));
            if (hit >= 0) { lex = _recent[hit].Lex; _recent.RemoveAt(hit); _recent.Insert(0, (id, own, cls, lex)); }
        }
        bool reused = lex != null;
        if (lex is null)
        {
            lex = UserLexicon.Build(eff.Words, eff.Fixes);
            lock (_recent)
            {
                _recent.Insert(0, (id, own, cls, lex));
                if (_recent.Count > 2) _recent.RemoveAt(2);
            }
        }
        // « Classe »'s banned words, whatever the active profile and the « Classe » profile's state (reviewer 2026-11-27)
        var exclus = id == Profile.ClassId ? own.Exclus : Store.Get(Profile.ClassId).Exclus;
        return new Built(id, eff, lex, reused, sw.Elapsed.TotalMilliseconds, exclus);
    }

    /// <summary>The cached snapshot of <paramref name="id"/> when its books are loaded and unchanged, else null.</summary>
    private UserLexicon? Cached(string id, bool hasClass)
    {
        if (!Store.TryGetLoaded(id, out var own)) return null;
        WordBook? cls = null;
        if (hasClass && !Store.TryGetLoaded(Profile.ClassId, out cls)) return null;
        lock (_recent)
            return _recent.Find(r => r.Id == id && ReferenceEquals(r.Own, own) && ReferenceEquals(r.Class, cls)).Lex;
    }

    /// <summary>Rebuild the active profile's snapshot and hand it to the engine. A cached snapshot is swapped in at once;
    /// otherwise (with a dispatcher) the files are read and the snapshot built on a worker thread — meanwhile the engine
    /// keeps the previous snapshot of the same profile, or none after a switch (another pupil's words are never shown).</summary>
    public void Rebuild(string reason)
    {
        var id = _profiles.ActiveId;
        bool hasClass = HasClass(id);
        int gen = ++_gen;
        // the banned words first, from what is known now (the machine's at least): a slow or failed snapshot never delays
        // or drops them; Finish completes them with « Classe »'s list read from its file
        if (Store.TryGetLoaded(Profile.ClassId, out var cb)) _classExclus = cb.Exclus;
        ApplyExclus(reason, always: false);
        if (_post is null || Cached(id, hasClass) != null) { Finish(Compute(id, hasClass), gen, reason, false); return; }
        if (_appliedId != id) { _apply(UserLexicon.Empty); Current = UserLexicon.Empty; _appliedId = id; }
        var post = _post;
        System.Threading.Tasks.Task.Run(() =>
        {
            Built? b = null;
            try { b = Compute(id, hasClass); }
            catch (Exception ex) { _log?.Invoke($"words: snapshot of {id} failed ({ex.GetType().Name})"); }
            if (b != null) post(() => Finish(b, gen, reason, true));
        });
    }

    /// <summary>UI thread: hand a snapshot to the engine unless a newer Rebuild was asked meanwhile.</summary>
    private void Finish(Built b, int gen, string reason, bool background)
    {
        if (gen != _gen) { _log?.Invoke($"words: snapshot of {b.Id} dropped (a newer one was asked)"); return; }
        _apply(b.Lex);
        Current = b.Lex;
        _appliedId = b.Id;
        LastBuildMs = b.Ms;
        var e = b.Eff;
        _log?.Invoke(FormattableString.Invariant(
            $"words: profile {b.Id} {e.Own} own + {e.Class} classe ({e.Hidden} hidden) + {e.Fixes.Count} fautes -> {b.Lex.Count} forms, {(b.Reused ? "reused" : "built")} in {b.Ms:0.0} ms{(background ? " on a worker" : "")} ({reason})"));
        _classExclus = b.ClassExclus;
        ApplyExclus(reason, always: false);
        Applied?.Invoke();
    }

    // ------------------------------------------------------------------ « Mots à ne jamais proposer » (plan offensive-words B3)
    /// <summary>The machine file's <c>locked.motsExclus</c> (set once at start; shown read-only, « Réglé par
    /// l'administrateur »).</summary>
    public IReadOnlyList<string> MachineExclus { get; init; } = Array.Empty<string>();

    /// <summary>Hands the teacher's words (« Classe » ∪ machine) to the engine — in the app <see cref="EngineExclusions"/>;
    /// null = not wired (tests of the lists alone).</summary>
    public Action<IReadOnlyList<string>>? ApplyExcluded { get; init; }

    /// <summary>The words last handed to <see cref="ApplyExcluded"/> (tests).</summary>
    public IReadOnlyList<string> AppliedExclus { get; private set; } = Array.Empty<string>();

    private IReadOnlyList<string> _classExclus = Array.Empty<string>();   // UI thread: « Classe »'s list, as last read
    private bool _exclusApplied;

    /// <summary>UI thread: the teacher part of the engine's exclusions = « Classe »'s list ∪ the machine's, rebuilt when it
    /// changed (or <paramref name="always"/>: after the grammar load, so that each word gets its lemma's forms).</summary>
    private void ApplyExclus(string reason, bool always)
    {
        if (ApplyExcluded is null) return;
        var all = _classExclus.Concat(MachineExclus).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!always && _exclusApplied && all.SequenceEqual(AppliedExclus, StringComparer.Ordinal)) return;
        ApplyExcluded(all);
        AppliedExclus = all;
        _exclusApplied = true;
        _log?.Invoke($"exclusions: teacher list {_classExclus.Count} classe + {MachineExclus.Count} machine words applied ({reason})");
    }

    /// <summary>UI thread: rebuild the teacher part once the grammar pack is in (<c>App</c> posts it after
    /// <see cref="GrammarPack.LoadAsync"/>). Every rebuild runs on the UI thread and reads the grammar then, and every change
    /// goes through <see cref="PredictionEngine.UpdateExcluded"/>, so the grammar forms and the teacher part never undo each
    /// other.</summary>
    public void RefreshExcluded(string reason) => ApplyExclus(reason, always: true);

    /// <summary>The app's <see cref="ApplyExcluded"/>: the words, expanded by <see cref="ExcludedWords.TeacherForms"/>
    /// (their lemma's forms once the grammar is in), replace the engine's teacher part (the file's and the grammar parts
    /// kept). Counts only in the log.</summary>
    public static Action<IReadOnlyList<string>> EngineExclusions(PredictionEngine engine, Action<string>? log) => words =>
    {
        var forms = ExcludedWords.TeacherForms(words, engine);
        var x = engine.UpdateExcluded(c => c.WithExtra(forms, engine));
        log?.Invoke($"exclusions: {words.Count} teacher words -> {forms.Count} forms (+{x.ExtraCount} new), {x.Count} in all");
    };

    /// <summary>« Classe » ∪ machine banned words (ignoring case), for the editors: marked in « Classe »'s own list, left out
    /// of a pupil's view of « Mots de la classe » (reviewer 2026-11-27).</summary>
    public ISet<string> BannedWords()
    {
        var s = new HashSet<string>(Store.Get(Profile.ClassId).Exclus, StringComparer.OrdinalIgnoreCase);
        s.UnionWith(MachineExclus);
        return s;
    }

    /// <summary>A snapshot was handed to the engine (tests).</summary>
    public event Action? Applied;

    /// <summary>Change the words of <paramref name="id"/>: <paramref name="f"/> returns the new book and null, or the
    /// unchanged book and the reason it was refused. The running engine follows when <paramref name="id"/> is the active
    /// profile or « Classe ».</summary>
    public WordProblem? Update(string id, Func<WordBook, (WordBook Book, WordProblem? Problem)> f, string what)
    {
        var before = Store.Get(id);
        var (after, problem) = f(before);
        if (problem != null) { _log?.Invoke($"words: {what} on {id} refused ({problem})"); return problem; }
        if (ReferenceEquals(after, before)) return null;
        Store.Set(id, after);
        CleanImages(id);
        _log?.Invoke($"words: {what} on {id} (now {after.WordCount} words, {after.Fautes.Count} fautes, {after.Masques.Count} hidden)");
        if (id == _profiles.ActiveId || id == Profile.ClassId) Rebuild(what);
        Changed?.Invoke(id);
        return null;
    }

    /// <summary>Add one word to the default list of <paramref name="id"/> (tray « Ajouter un mot… », Q3: no PIN).</summary>
    public WordProblem? AddWord(string id, string word, string src = WordEntry.Manual) =>
        Update(id, b => { var r = b.AddWord(null, word, src, _now(), id == Profile.ClassId, out var p); return (r, p); }, "word added");

    /// <summary>« Effacer tous mes mots… »: a profiles backup (<c>-mots</c>) first — none possible → nothing erased (null
    /// returned) —, then the lists, « Mes fautes » and hidden words of <paramref name="id"/> go; « Classe » keeps its
    /// own words (unless it is the profile erased). Returns the backup's file name ("" in a session without backups).</summary>
    public string? EraseAll(string id)
    {
        var zip = _profiles.Backup("mots");
        if (zip is null && _profiles.CanBackup) { _log?.Invoke($"words: erase of {id} cancelled (no backup)"); return null; }
        Store.Set(id, Store.Get(id).Cleared());
        Store.Flush();
        CleanImages(id);
        _log?.Invoke($"words: all words of {id} erased (backup {(zip is null ? "none" : System.IO.Path.GetFileName(zip))})");
        if (id == _profiles.ActiveId || id == Profile.ClassId) Rebuild("erase");
        Changed?.Invoke(id);
        return zip is null ? "" : System.IO.Path.GetFileName(zip);
    }

    /// <summary>A profile was deleted (its folder with its words).</summary>
    public void OnDeleted(string id) => Store.Forget(id);

    /// <summary>« Dupliquer »: the copy gets the same words.</summary>
    public void OnDuplicated(string from, string to)
    {
        var b = Store.Get(from);
        if (to != Profile.ClassId) b = b with { Exclus = Array.Empty<string>() };   // « Classe » only (a copy is a pupil)
        if (!b.IsEmpty) Store.Set(to, b);
    }

    /// <summary>Words of several profiles were replaced (an import): the engine follows, the editors refresh.</summary>
    public void Reloaded(string reason)
    {
        Rebuild(reason);
        Changed?.Invoke("");
    }
}
