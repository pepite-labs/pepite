using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using Predict.Core;
using Xunit;
using Xunit.Abstractions;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 10A review fixes: PIN for a tray add on « Classe », backups and delete with pending word writes, the
/// one-pass rebase, the snapshot built on a worker, the bar's spare slots, plurals, NFC lessons, the archive version.</summary>
public class WordReviewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kbwrev-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = new();
    private readonly ITestOutputHelper _out;
    private static readonly DateTime Now = new(2026, 11, 14);

    public WordReviewTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        TestDirs.Delete(_dir);
    }

    private sealed class Host : IProfileHost
    {
        public void ResetForSwitch() { }
        public void CancelSpeech() { }
        public void CloseProfileWindows() { }
    }

    private (ProfileStore Store, ProfileService Svc, WordListService Words, List<UserLexicon> Applied, string Lea, string Tom) Pc(Action<Action>? post = null)
    {
        var store = new ProfileStore(_dir, Path.Combine(_dir, "sauvegardes"), _log.Add);
        store.Load();
        store.Add(Strings.ClassProfileName, new Avatar(null, ProfileColor.Vert), null, Profile.ClassId);
        var lea = store.Add("Léa", Avatar.Default).Id;
        var tom = store.Add("Tom", Avatar.Default).Id;
        store.SetActive(lea);
        var svc = new ProfileService(store, ProfileService.CreateSettings(store, MachineLayer.None, _log.Add), new Host(), _log.Add);
        var applied = new List<UserLexicon>();
        var words = new WordListService(new WordListStore(store.ProfilesDir, _log.Add), store, applied.Add, _log.Add, () => Now, post);
        svc.Words = words;
        return (store, svc, words, applied, lea, tom);
    }

    [Fact]
    public void Tray_Add_On_Classe_Needs_The_Edit_Pin_When_Management_Is_On()
    {
        Assert.Equal(PinReason.Edit, PinGate.AddWordReason(true, Profile.ClassId));
        Assert.Equal(PinReason.AddWord, PinGate.AddWordReason(true, "p-abcdef"));
        Assert.Equal(PinReason.AddWord, PinGate.AddWordReason(false, Profile.ClassId));
        // Edit follows « Code requis pour modifier le profil »: off → no PIN, on → PIN; AddWord never
        var on = new ProfilePolicy(PinToEdit: true);
        var off = new ProfilePolicy(PinToEdit: false);
        Assert.True(PinGate.Protects(PinGate.AddWordReason(true, Profile.ClassId), true, on));
        Assert.False(PinGate.Protects(PinGate.AddWordReason(true, Profile.ClassId), true, off));
        Assert.False(PinGate.Protects(PinGate.AddWordReason(true, "p-abcdef"), true, on));
    }

    [Fact]
    public void A_Backup_Holds_The_Words_Just_Added()
    {
        var pc = Pc();
        pc.Words.AddWord(pc.Lea, "Vésuve");   // written in the background
        var zip = pc.Store.Backup("test")!;
        using var z = ZipFile.OpenRead(zip);
        var e = z.GetEntry($"{pc.Lea}/mots.json");
        Assert.NotNull(e);
        using var r = new StreamReader(e!.Open());
        Assert.Contains("suve", r.ReadToEnd());
    }

    [Fact]
    public void Delete_Right_After_An_Edit_Leaves_No_Folder()
    {
        var pc = Pc();
        pc.Words.AddWord(pc.Tom, "Zorglub");
        pc.Svc.Delete(pc.Tom);
        System.Threading.Thread.Sleep(300);   // a write still queued would recreate profiles\<id>\mots.json now
        Assert.False(Directory.Exists(Path.Combine(pc.Store.ProfilesDir, pc.Tom)));
    }

    [Fact]
    public void Rebase_Of_5000_Words_For_40_Pupils_Is_Fast()
    {
        var cls = WordBook.Empty.AddWords(Enumerable.Range(0, 5000).Select(WordListModelTests.Name), WordEntry.Manual, Now, true);
        var pupil = WordBook.Empty.AddWord(null, "Vésuve", WordEntry.Manual, Now, false, out _);
        var sw = Stopwatch.StartNew();
        WordBook last = pupil;
        for (int i = 0; i < 40; i++) last = WordLayering.Rebase(cls, pupil, WordBook.Empty, Now);
        sw.Stop();
        _out.WriteLine($"40 rebases of 5 000 words: {sw.ElapsedMilliseconds} ms");
        Assert.Equal(5000, last.WordCount);   // the limit: 5 000 words in all
        // a loose bound: the full suite runs the test classes in parallel (2 088 ms seen once at 2 s, plan 10B re-review)
        Assert.True(sw.ElapsedMilliseconds < 5000, $"{sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Snapshot_Is_Built_On_A_Worker_And_Swapped_In_Order()
    {
        var queue = new List<Action>();
        var pc = Pc(a => { lock (queue) queue.Add(a); });
        pc.Words.AddWord(pc.Lea, "Vésuve");
        void Drain()
        {
            for (int i = 0; i < 100; i++)
            {
                Action[] q;
                lock (queue) { q = queue.ToArray(); queue.Clear(); }
                foreach (var a in q) a();
                if (q.Length == 0) System.Threading.Thread.Sleep(20); else return;
            }
        }
        // the edit of the loaded active profile rebuilt on the worker; nothing applied before the dispatcher runs
        int before = pc.Applied.Count;
        Drain();
        Assert.True(pc.Applied.Count > before);
        Assert.True(pc.Applied.Last().Contains("vésuve"));
        // a switch to Tom: none of Léa's words meanwhile (empty), then Tom's
        pc.Words.AddWord(pc.Tom, "Zorglub");
        Drain();
        pc.Svc.Switch(pc.Tom, "test");
        Assert.False(pc.Applied.Last().Contains("vésuve"));
        Drain();
        Assert.True(pc.Applied.Last().Contains("zorglub"));
        // two rebuilds in a row: only the newer one is applied
        pc.Svc.Switch(pc.Lea, "test");
        pc.Svc.Switch(pc.Tom, "test");
        Drain(); Drain();
        Assert.True(pc.Applied.Last().Contains("zorglub"));
    }

    [Fact]
    public void Bar_Keeps_Its_Count_When_Two_Words_Merge()
    {
        var ranked = new[]
        {
            new Suggestion("pierre", 0, MatchKind.Prefix), new Suggestion("Pierre", -1, MatchKind.Prefix, Name: true),
            new Suggestion("pied", -2, MatchKind.Prefix), new Suggestion("pire", -3, MatchKind.Prefix), new Suggestion("pis", -4, MatchKind.Prefix),
        };
        var bar = KeyboardPolicy.CasedDistinct(ranked, "Pi", false, 4);
        Assert.Equal(new[] { "Pierre", "Pied", "Pire", "Pis" }, bar.Select(s => s.Word));
    }

    [Fact]
    public void Plurals_And_Nfc()
    {
        Assert.Equal("1 mot dans cette liste ; 0 mot en tout (au plus 5000).", Strings.WordsCount(1, 0, 5000));
        Assert.StartsWith("2 mots dans", Strings.WordsCount(2, 2, 5000));
        var decomposed = "éruption éruption";
        var r = LessonExtractor.Extract(decomposed, _ => true, new HashSet<string>());
        Assert.Single(r);
        Assert.Equal("éruption", r[0].Form);
    }

    [Fact]
    public void Archive_Without_Words_Is_Version_1()
    {
        var pc = Pc();
        var o = ProfileArchive.Export(pc.Store, new[] { pc.Lea }, false, null, "t", Now);
        Assert.Equal(1, (int)o["version"]!);
        var w = ProfileArchive.Export(pc.Store, new[] { pc.Lea }, false, null, "t", Now, pc.Words.BookOf);
        Assert.Equal(2, (int)w["version"]!);
    }

    [Fact]
    public void Word_Store_Memory_With_15000_Words()
    {
        // what the app keeps for two pupils of a class with 10 000 + 5 000 + 5 000 words: the JSON trees of the three
        // files, the books and the two cached snapshots (managed heap; the plan's gate is ≤ +5 MB working set)
        var pc = Pc();
        void Write(string id, string seed, int n) =>
            pc.Words.Store.Set(id, WordBook.Empty.AddWords(Enumerable.Range(0, n).Select(i => seed + WordListModelTests.Name(i)), WordEntry.Import, Now, id == Profile.ClassId));
        Write(Profile.ClassId, "k", 10000); Write(pc.Lea, "l", 5000); Write(pc.Tom, "t", 5000);
        pc.Words.Store.Flush();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long m0 = GC.GetTotalMemory(true);
        var store = new WordListStore(pc.Store.ProfilesDir, null);
        var svc = new WordListService(store, pc.Store, _ => { }, null, () => Now);
        svc.Rebuild("lea");
        pc.Store.SetActive(pc.Tom);
        svc.Rebuild("tom");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long m1 = GC.GetTotalMemory(true);
        GC.KeepAlive(svc);
        double mb = (m1 - m0) / 1048576.0;
        _out.WriteLine($"managed heap for 3 files (20 000 words) + 2 snapshots of 15 000: +{mb:0.0} MB");
        Assert.True(mb < 200, $"{mb:0.0} MB");
    }

    [Fact]
    public void A_Slow_File_Read_Does_Not_Block_The_Ui_Thread()
    {
        var pc = Pc();
        pc.Words.AddWord(pc.Tom, "Zorglub");
        pc.Words.Store.Flush();
        var store = new WordListStore(pc.Store.ProfilesDir, null);
        store.Get(pc.Lea);
        using var reading = new System.Threading.ManualResetEventSlim();
        using var release = new System.Threading.ManualResetEventSlim();
        store.BeforeRead = id => { if (id == pc.Tom) { reading.Set(); release.Wait(5000); } };
        var worker = System.Threading.Tasks.Task.Run(() => store.Get(pc.Tom));   // a slow disk: held inside the read
        Assert.True(reading.Wait(5000));
        var sw = Stopwatch.StartNew();
        Assert.True(store.TryGetLoaded(pc.Lea, out _));      // the UI thread's check does not wait for the read
        Assert.False(store.TryGetLoaded(pc.Tom, out _));
        Assert.True(sw.ElapsedMilliseconds < 500, $"{sw.ElapsedMilliseconds} ms");
        release.Set();
        Assert.Equal("Zorglub", worker.Result.Default!.Words.Single().W);
        Assert.True(store.TryGetLoaded(pc.Tom, out var b) && b.WordCount == 1);
    }
}
