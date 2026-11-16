using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Predict.Keyboard.Tests;

/// <summary>Plan 08a step 6: progress and end driven by the played position (a fake position, no device).</summary>
public class PlaybackClockTests
{
    private sealed class Rec
    {
        public readonly List<int> Progress = new();
        public readonly List<SpeechOutcome> Done = new();
        public void P(SpeechProgress p) => Progress.Add(p.CharPos);
        public void D(SpeechOutcome o, string? e) => Done.Add(o);
    }

    [Fact]
    public void Events_In_Order_Once_Never_Early_Then_Done_After_The_Last_Sample()
    {
        var c = new PlaybackClock();
        var r = new Rec();
        c.Begin(r.D, r.P);
        c.AddMarks(new[] { new ClockMark(100, 0, 3, 0), new ClockMark(300, 4, 4, 0), new ClockMark(500, 9, 2, 0) });
        Assert.True(c.Tick(99));
        Assert.Empty(r.Progress);
        c.Tick(100);
        Assert.Equal(new[] { 0 }, r.Progress);
        c.Tick(250);
        Assert.Equal(new[] { 0 }, r.Progress);
        c.Tick(600);                     // two marks passed at once: both, in order
        Assert.Equal(new[] { 0, 4, 9 }, r.Progress);
        Assert.Empty(r.Done);            // the end is not known yet (synthesis still running)
        c.SetEnd(900);
        c.Tick(899);
        Assert.Empty(r.Done);
        Assert.False(c.Tick(900));
        Assert.Equal(new[] { SpeechOutcome.Done }, r.Done);
        c.Tick(2000);
        Assert.Equal(3, r.Progress.Count);
        Assert.Single(r.Done);
    }

    [Fact]
    public void Nothing_After_Cancel_And_Cancelled_Once()
    {
        var c = new PlaybackClock();
        var r = new Rec();
        c.Begin(r.D, r.P);
        c.AddMarks(new[] { new ClockMark(100, 0, 3, 0), new ClockMark(300, 4, 4, 0) });
        c.Tick(150);
        Assert.True(c.Cancel());
        Assert.False(c.Cancel());
        c.AddMarks(new[] { new ClockMark(400, 9, 1, 0) });
        c.SetEnd(500);
        Assert.False(c.Tick(1000));
        Assert.Equal(new[] { 0 }, r.Progress);
        Assert.Equal(new[] { SpeechOutcome.Cancelled }, r.Done);
    }

    [Fact]
    public void Device_Error_Ends_With_Error()
    {
        var c = new PlaybackClock();
        var r = new Rec();
        c.Begin(r.D, r.P);
        Assert.True(c.Fail("audio device: default device changed"));
        Assert.False(c.Active);
        Assert.Equal(new[] { SpeechOutcome.Error }, r.Done);
        Assert.False(c.Cancel());
    }

    [Fact]
    public void A_New_Utterance_Starts_Clean()
    {
        var c = new PlaybackClock();
        var r1 = new Rec();
        c.Begin(r1.D, r1.P);
        c.AddMarks(new[] { new ClockMark(10, 0, 1, 0) });
        c.Cancel();
        var r2 = new Rec();
        c.Begin(r2.D, null);   // no progress wanted
        c.AddMarks(new[] { new ClockMark(10, 0, 1, 0) });
        c.SetEnd(20);
        c.Tick(30);
        Assert.Empty(r2.Progress);
        Assert.Equal(new[] { SpeechOutcome.Done }, r2.Done);
    }

    [Fact]
    public void Playback_Queue_Never_Stops_And_Flush_Drops_At_Once()
    {
        var q = new PlaybackQueue(22050);
        var buf = new float[100];
        Assert.Equal(100, q.Read(buf, 0, 100));                 // silence when empty
        Assert.All(buf, x => Assert.Equal(0f, x));
        long s1 = q.Enqueue(Enumerable.Repeat(0.5f, 150).ToArray(), 150);
        Assert.Equal(100, s1);                                  // lands where the next read starts
        long s2 = q.Enqueue(Enumerable.Repeat(0.25f, 50).ToArray(), 50);
        Assert.Equal(250, s2);                                  // right after the first chunk
        q.Read(buf, 0, 100);
        Assert.All(buf, x => Assert.Equal(0.5f, x));
        q.Flush();
        Assert.Equal(200, q.QueuedEnd);
        q.Read(buf, 0, 100);
        Assert.All(buf, x => Assert.Equal(0f, x));              // nothing left after the flush
        Assert.Equal(300, q.Enqueue(new float[10], 10));
    }

    [Fact]
    public void Idle_Bypass_Keeps_The_Timeline()
    {
        var q = new PlaybackQueue(22050);
        var buf = new float[100];
        q.Enqueue(Enumerable.Repeat(0.5f, 50).ToArray(), 50);
        q.Read(buf, 0, 100);                                     // audio + tail silence: not idle yet
        Assert.False(q.IsIdle);
        for (int i = 0; i < PlaybackQueue.IdleReads; i++) q.Read(buf, 0, 100);
        Assert.True(q.IsIdle);                                   // the resampler has flushed: zeros may bypass it
        q.AdvanceSilence(1000);
        Assert.Equal(100 + PlaybackQueue.IdleReads * 100 + 1000, q.QueuedEnd);
        Assert.Equal(1500, q.Enqueue(new float[10], 10));        // a new chunk lands at the advanced position
        Assert.False(q.IsIdle);
    }
}
