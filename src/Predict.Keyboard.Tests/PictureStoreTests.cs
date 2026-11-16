using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>PictureStore against an in-test SQLite pack; the "decoder" turns a blob into a string.</summary>
public sealed class PictureStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pictostore-" + Guid.NewGuid().ToString("N"));
    private readonly List<PictureStore> _open = new();

    public PictureStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        foreach (var s in _open) s.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string MakePack(int schema = 1, bool attribution = true, bool tables = true)
    {
        var path = Path.Combine(_dir, $"p{Guid.NewGuid():N}.sqlite");
        using var con = new SqliteConnection($"Data Source={path};Pooling=False");
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = "CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT);" +
            (tables ? "CREATE TABLE picto(id INTEGER PRIMARY KEY, png BLOB NOT NULL);" +
                      "CREATE TABLE word_picto(form TEXT PRIMARY KEY, picto_id INTEGER NOT NULL) WITHOUT ROWID;" : "");
        cmd.ExecuteNonQuery();
        void Exec(string sql) { cmd.CommandText = sql; cmd.ExecuteNonQuery(); }
        Exec($"INSERT INTO meta VALUES ('schema', '{schema}'), ('build_date', '2026-11-08')");
        if (attribution) Exec("INSERT INTO meta VALUES ('attribution_fr', 'Sergio Palao pour ARASAAC (test)')");
        if (tables)
        {
            for (int id = 1; id <= 6; id++) Exec($"INSERT INTO picto VALUES ({id}, x'{id:X2}')");
            Exec("INSERT INTO word_picto VALUES ('maison',1),('chat',2),('chats',2),('voiture',3),('lune',4),('mer',5),('été',6)");
        }
        return path;
    }

    private PictureStore Open(string path, Func<byte[], object>? decode = null, int cap = PictureStore.CacheCapacity, List<string>? log = null)
    {
        var s = PictureStore.Open(path, decode ?? (b => "img" + b[0]), m => { lock (log ?? new List<string>()) log?.Add(m); }, cap)!;
        Assert.NotNull(s);
        _open.Add(s);
        Assert.True(s.Loaded.Wait(5000));
        return s;
    }

    [Fact]
    public void Meta_And_Case_Insensitive_Lookup()
    {
        var log = new List<string>();
        var s = Open(MakePack(), log: log);
        Assert.Equal("Sergio Palao pour ARASAAC (test)", s.Attribution);
        Assert.Equal("2026-11-08", s.BuildDate);
        Assert.Equal(6, s.PictureCount);
        Assert.Equal(7, s.WordCount);
        Assert.True(s.Has("Maison"));
        Assert.True(s.Has("ÉTÉ"));
        Assert.False(s.Has("bleu"));
        Assert.Contains(log, l => l.StartsWith("pictos: 7 words, 6 pictures, loaded in"));
    }

    [Fact]
    public void Attribution_Falls_Back_To_The_Verbatim_Constant()
    {
        var s = Open(MakePack(attribution: false));
        Assert.Equal(Strings.ArasaacAttribution, s.Attribution);
        Assert.Contains("Sergio Palao", s.Attribution);
    }

    [Fact]
    public void Missing_File_Or_Bad_Schema_Gives_Null()
    {
        var log = new List<string>();
        Assert.Null(PictureStore.Open(Path.Combine(_dir, "none.sqlite"), b => b, log.Add));
        Assert.Null(PictureStore.Open(null, b => b, log.Add));
        Assert.Null(PictureStore.Open(MakePack(schema: 2), b => b, log.Add));
        Assert.Null(PictureStore.Open(MakePack(tables: false), b => b, log.Add));
        var junk = Path.Combine(_dir, "junk.sqlite");
        File.WriteAllText(junk, "not a database");
        Assert.Null(PictureStore.Open(junk, b => b, log.Add));
        Assert.Contains("pictures: off (pack not found)", log);
        Assert.Contains(log, l => l.StartsWith("pictures: off (pack schema '2'"));
    }

    [Fact]
    public void Request_Delivers_Pictures_And_Fills_The_Cache()
    {
        var s = Open(MakePack());
        var got = new ConcurrentQueue<(int, string, object)>();
        var done = new CountdownEvent(2);
        Assert.False(s.TryGetCached("maison", out _));
        s.Request(1, new[] { "maison", "inconnu", "chat" }, (g, w, img) => { got.Enqueue((g, w, img)); done.Signal(); });
        Assert.True(done.Wait(5000));
        Assert.Equal(new[] { (1, "maison", (object)"img1"), (1, "chat", (object)"img2") }, got.ToArray());
        Assert.True(s.TryGetCached("MAISON", out var m));
        Assert.Equal("img1", m);
        Assert.True(s.TryGetCached("chats", out var c));   // same picture id as « chat »
        Assert.Equal("img2", c);
        Assert.False(s.TryGetCached("inconnu", out _));
    }

    [Fact]
    public void Latest_Generation_Wins()
    {
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        int decodes = 0;
        var s = Open(MakePack(), b =>
        {
            if (Interlocked.Increment(ref decodes) == 1) { entered.Set(); gate.Wait(5000); }
            return "img" + b[0];
        });
        var got = new ConcurrentQueue<(int G, string W)>();
        var gen2 = new ManualResetEventSlim(false);
        s.Request(1, new[] { "maison", "chat", "voiture", "lune" }, (g, w, _) => got.Enqueue((g, w)));
        Assert.True(entered.Wait(5000));                      // decoding « maison » (gen 1)
        s.Request(2, new[] { "mer" }, (g, w, _) => { got.Enqueue((g, w)); gen2.Set(); });
        gate.Set();
        Assert.True(gen2.Wait(5000));
        var all = got.ToArray();
        Assert.Equal((2, "mer"), all.Last());
        Assert.DoesNotContain(all, x => x.G == 1 && x.W != "maison");   // chat, voiture, lune skipped
        Assert.Equal(2, decodes);
    }

    [Fact]
    public void Lru_Evicts_The_Least_Recently_Used()
    {
        var s = Open(MakePack(), cap: 2);
        Assert.Equal("img1", s.GetNow("maison"));
        Assert.Equal("img2", s.GetNow("chat"));
        Assert.True(s.TryGetCached("maison", out _));          // maison becomes most recent
        Assert.Equal("img3", s.GetNow("voiture"));             // evicts chat
        Assert.Equal(2, s.CachedCount);
        Assert.False(s.TryGetCached("chat", out _));
        Assert.True(s.TryGetCached("maison", out _));
        Assert.True(s.TryGetCached("voiture", out _));
        Assert.Null(s.GetNow("bleu"));
    }

    [Fact]
    public void One_Bad_Picture_Does_Not_Cost_The_Rest_And_Is_Not_Retried()
    {
        int chatDecodes = 0;
        var s = Open(MakePack(), b =>
        {
            if (b[0] == 2) { Interlocked.Increment(ref chatDecodes); throw new InvalidDataException("corrupt"); }
            return "img" + b[0];
        });
        var got = new ConcurrentQueue<string>();
        var done = new CountdownEvent(2);
        s.Request(1, new[] { "maison", "chat", "voiture" }, (_, w, _) => { got.Enqueue(w); done.Signal(); });
        Assert.True(done.Wait(5000));
        Assert.Equal(new[] { "maison", "voiture" }, got.ToArray());
        var again = new ManualResetEventSlim(false);
        s.Request(2, new[] { "chats", "lune" }, (_, w, _) => { if (w == "lune") again.Set(); });   // chats = same id as chat
        Assert.True(again.Wait(5000));
        Assert.Equal(1, chatDecodes);
    }

    [Fact]
    public void Dispose_During_Prefetch_Returns_At_Once_And_Delivers_Nothing_More()
    {
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        int decodes = 0;
        var store = PictureStore.Open(MakePack(), b =>
        {
            if (Interlocked.Increment(ref decodes) == 1) { entered.Set(); gate.Wait(5000); }
            return "img" + b[0];
        }, null)!;
        Assert.True(store.Loaded.Wait(5000));
        store.Prefetch(new[] { "maison", "chat", "voiture", "lune", "mer", "été" });
        Assert.True(entered.Wait(5000));                          // loader busy decoding « maison »
        int delivered = 0;
        store.Request(1, new[] { "chat" }, (_, _, _) => Interlocked.Increment(ref delivered));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var disposing = System.Threading.Tasks.Task.Run(store.Dispose);
        Thread.Sleep(50);
        gate.Set();                                               // the decode in progress finishes
        Assert.True(disposing.Wait(3000));
        Assert.True(sw.ElapsedMilliseconds < 2000);
        Thread.Sleep(200);
        Assert.Equal(0, delivered);                               // queued request abandoned
        Assert.Equal(1, decodes);                                 // rest of the prefetch abandoned
        store.Request(2, new[] { "mer" }, (_, _, _) => Interlocked.Increment(ref delivered));   // no throw
        store.Prefetch(new[] { "mer" });
        store.Dispose();                                          // idempotent
        Assert.Equal(0, delivered);
    }

    [Fact]
    public void Prefetch_Warms_The_Cache_And_Yields_To_Requests()
    {
        var s = Open(MakePack());
        s.Prefetch(new[] { "lune", "mer", "été" });
        var done = new ManualResetEventSlim(false);
        s.Request(1, new[] { "maison" }, (_, _, _) => done.Set());
        Assert.True(done.Wait(5000));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!(s.TryGetCached("lune", out _) && s.TryGetCached("mer", out _) && s.TryGetCached("été", out _)) && DateTime.UtcNow < deadline)
            Thread.Sleep(10);
        Assert.True(s.TryGetCached("été", out _));
        Assert.Equal(4, s.CachedCount);
    }
}
