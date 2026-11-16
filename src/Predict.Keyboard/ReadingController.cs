using System;

namespace Predict.Keyboard;

/// <summary>Idle = no reading mode; Ready = tiles shown, nothing spoken (no autoplay, D21); Playing; Paused.</summary>
internal enum ReadingState { Idle, Ready, Playing, Paused }

/// <summary>What the reading state machine asks of the app (UI thread). Tests record the calls.</summary>
internal interface IReadingOutput
{
    /// <summary>Speak one utterance (interrupts anything being said). Progress and completion come back with <paramref name="id"/>.</summary>
    void Speak(long id, Utterance u);
    /// <summary>A clicked tile: say this word alone (interrupts).</summary>
    void SayWord(string word);
    /// <summary>Stop speaking at once.</summary>
    void Cancel();
    /// <summary>The highlighted tile changed (-1 = none). Our bar (and the large window) only: never the source app.</summary>
    void Highlight(int tile);
    void StateChanged(ReadingState state);
}

/// <summary>
/// The reading mode's state machine (plan 07, D21/D24), pure and UI-thread only:
/// <code>
///  Idle ──Show──► Ready ──Play──► Playing ──Pause──► Paused ──Play──► Playing (from the highlighted word)
///                   ▲               │ done: next sentence … last one done ──► Ready (cursor back to the start)
///                   └──── click on a tile (says the word; next Play = its sentence) ◄── any state
///  any ──Exit──► Idle (speech cancelled)
/// </code>
/// One utterance per sentence, auto-continuing. ⏮ / ⏭ move the sentence cursor while playing (the new sentence is spoken
/// at once) and while stopped (its first word is highlighted). Every utterance has an id: a progress or completion of an
/// older one (cancelled, replaced) is ignored.
/// </summary>
internal sealed class ReadingController
{
    private readonly IReadingOutput _out;
    private long _id;            // id of the utterance being spoken (0 = none)
    private Utterance? _utt;
    private int _resume = -1;    // tile to resume from after a pause (-1 = sentence start)

    public ReadingController(IReadingOutput output) => _out = output;

    public ReadingState State { get; private set; } = ReadingState.Idle;
    public ReadingDocument? Doc { get; private set; }
    /// <summary>The sentence the next Play starts (or the one being spoken).</summary>
    public int Sentence { get; private set; }
    /// <summary>The highlighted tile, -1 when none.</summary>
    public int Current { get; private set; } = -1;
    public long UtteranceId => _id;

    /// <summary>A new selection: tiles shown, nothing spoken.</summary>
    public void Show(ReadingDocument doc)
    {
        if (State == ReadingState.Playing) _out.Cancel();
        _id = 0; _utt = null; _resume = -1;
        Doc = doc;
        Sentence = NextWithWords(-1, +1) is int s and >= 0 ? s : 0;
        SetHighlight(-1);
        SetState(ReadingState.Ready);
    }

    /// <summary>Back to predictions (selection ended, Esc, typing, window switch, pause).</summary>
    public void Exit()
    {
        if (State == ReadingState.Idle) return;
        if (State == ReadingState.Playing) _out.Cancel();
        _id = 0; _utt = null; _resume = -1;
        Doc = null;
        Current = -1;
        SetState(ReadingState.Idle);
    }

    /// <summary>Space / F9 / the ▶❚❚ button.</summary>
    public void TogglePlay()
    {
        if (State == ReadingState.Playing) Pause();
        else Play();
    }

    public void Play()
    {
        if (Doc is null || State is ReadingState.Idle or ReadingState.Playing || Doc.Sentences.Count == 0) return;
        if (!Doc.Sentences[Sentence].HasWords && NextWithWords(Sentence, +1) is int n and >= 0) Sentence = n;
        if (!Doc.Sentences[Sentence].HasWords) return;   // nothing to say
        SetState(ReadingState.Playing);
        SpeakSentence(_resume);
    }

    public void Pause()
    {
        if (State != ReadingState.Playing) return;
        _id = 0; _utt = null;
        _out.Cancel();
        // resume at the highlighted word when it is in this sentence (else at the sentence start)
        var s = Doc!.Sentences[Sentence];
        _resume = Current >= s.FirstTile && Current < s.EndTile ? Current : -1;
        SetState(ReadingState.Paused);
    }

    public void Next() => Move(+1);
    public void Prev() => Move(-1);

    private void Move(int dir)
    {
        if (Doc is null || State == ReadingState.Idle) return;
        int to = NextWithWords(Sentence, dir);
        if (to < 0) to = Sentence;   // first / last sentence: stay (playing: say it again from its start)
        Sentence = to;
        _resume = -1;
        if (State == ReadingState.Playing) SpeakSentence(-1);
        else
        {
            if (State == ReadingState.Paused) SetState(ReadingState.Ready);
            SetHighlight(Doc.FirstWordOf(to));
        }
    }

    /// <summary>A click on a tile: stop, say that word, the next Play starts at its sentence.</summary>
    public void Click(int tile)
    {
        if (Doc is null || State == ReadingState.Idle || tile < 0 || tile >= Doc.Tiles.Count) return;
        _id = 0; _utt = null; _resume = -1;
        var t = Doc.Tiles[tile];
        Sentence = t.Sentence;
        SetHighlight(tile);
        if (t.IsWord) _out.SayWord(t.Text);   // interrupts the reading
        else _out.Cancel();
        SetState(ReadingState.Ready);
    }

    /// <summary>Engine progress (UI thread, marshalled with the utterance id).</summary>
    /// <returns>the tile highlighted after the event, or -1 when the event was stale / pointed at no word.</returns>
    public int OnProgress(long id, int charPos, int charLen)
    {
        if (id == 0 || id != _id || State != ReadingState.Playing || _utt is null || Doc is null) return -1;
        int tile = Doc.TileAt(_utt.Sentence, _utt.ToDocument(charPos), charLen);
        if (tile >= 0 && tile != Current) SetHighlight(tile);
        return tile;
    }

    /// <summary>The utterance <paramref name="id"/> ended. Done → the next sentence (or Ready after the last one);
    /// cancelled by someone else (another utterance, the speech thread) → Paused; error → Ready.</summary>
    public void OnDone(long id, SpeechOutcome outcome)
    {
        if (id == 0 || id != _id || State != ReadingState.Playing || Doc is null) return;
        _id = 0; _utt = null; _resume = -1;
        if (outcome == SpeechOutcome.Done)
        {
            int next = NextWithWords(Sentence, +1);
            if (next >= 0) { Sentence = next; SpeakSentence(-1); return; }
            Sentence = NextWithWords(-1, +1) is int f and >= 0 ? f : 0;   // the end: next Play starts again at the top
            SetHighlight(-1);
            SetState(ReadingState.Ready);
            return;
        }
        if (outcome == SpeechOutcome.Cancelled)
        {
            var s = Doc.Sentences[Sentence];
            _resume = Current >= s.FirstTile && Current < s.EndTile ? Current : -1;
            SetState(ReadingState.Paused);
            return;
        }
        SetState(ReadingState.Ready);
    }

    private void SpeakSentence(int fromTile)
    {
        var u = Doc!.UtteranceOf(Sentence, fromTile);
        _resume = -1;
        _utt = u;
        _id = NewId();
        SetHighlight(fromTile >= 0 ? fromTile : Doc.FirstWordOf(Sentence));
        _out.Speak(_id, u);
    }

    private static long _nextId;
    private static long NewId() => System.Threading.Interlocked.Increment(ref _nextId);

    /// <summary>The next (dir +1) or previous (-1) sentence that has a word, from <paramref name="from"/>; -1 when none.</summary>
    private int NextWithWords(int from, int dir)
    {
        if (Doc is null) return -1;
        for (int i = from + dir; i >= 0 && i < Doc.Sentences.Count; i += dir)
            if (Doc.Sentences[i].HasWords) return i;
        return -1;
    }

    private void SetHighlight(int tile)
    {
        Current = tile;
        _out.Highlight(tile);
    }

    private void SetState(ReadingState s)
    {
        if (State == s) return;
        State = s;
        _out.StateChanged(s);
    }
}
