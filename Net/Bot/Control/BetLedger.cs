using System;
using System.Collections.Generic;
using System.Text;

namespace MTile.Net;

// The bet-correction learner (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, "Learning: bets").
// Bot-side only, not sim state; no statics; deterministic.
//
// Each (behaviour name, situation bucket) keeps a two-channel additive offset (0 = dealt,
// 1 = taken) on top of the behaviour's own bet. The controller opens a record whenever a
// behaviour actually acquires a resource; the record accumulates HP drops on both sides
// (a rise, from respawn, counts 0) and settles at frame + Horizon, nudging the offset so the
// corrected bet tracks the realised mean outcome. There is no early close: a preempted
// behaviour is scored through its whole horizon, tail included.
public sealed class BetLedger : IBetLedger
{
    public float Eta       = 0.3f;   // step per settle
    public float Share     = 0.02f;  // fixed-share decay of offsets toward 0 per settle
    public float MaxOffset = 2f;     // HP clamp on each offset

    private sealed class Entry
    {
        public readonly string  Name;
        public readonly int     Bucket;
        public readonly float[] Offset = new float[2];
        public int Settles;
        public Entry(string name, int bucket) { Name = name; Bucket = bucket; }
    }

    private struct Record
    {
        public Entry Entry;
        public float BetDealt, BetTaken;
        public int   SettleFrame;
        public float Dealt, Taken;
    }

    private readonly Dictionary<(string, int), Entry> _entries = new();
    private readonly List<Record> _open = new();
    private bool  _hasBaseline;
    private float _myHp, _theirHp;
    private int   _nextId;

    // Diagnostics: total records settled, and records currently open.
    public int Settled { get; private set; }
    public int OpenCount => _open.Count;

    // Keep the learned offsets but forget the bout: drop open records and the HP baseline.
    // For a ledger carried from one bout to the next against the same opponent.
    public void NewBout()
    {
        _open.Clear();
        _hasBaseline = false;
    }

    public Bet Corrected(string name, int bucket, Bet bet)
    {
        if (!_entries.TryGetValue((name, bucket), out var e)) return bet;
        return new Bet(bet.Dealt + e.Offset[0], bet.Taken + e.Offset[1], bet.Horizon);
    }

    // Opens a record settling at frame + bet.Horizon. Returns a record id (diagnostic only).
    public int Open(string name, int bucket, Bet bet, int frame)
    {
        var key = (name, bucket);
        if (!_entries.TryGetValue(key, out var e)) { e = new Entry(name, bucket); _entries[key] = e; }
        _open.Add(new Record
        {
            Entry = e, BetDealt = bet.Dealt, BetTaken = bet.Taken,
            SettleFrame = frame + bet.Horizon,
        });
        return _nextId++;
    }

    // Once per frame. The first call only establishes the HP baseline.
    public void Tick(int frame, float myHp, float theirHp)
    {
        float taken = 0f, dealt = 0f;
        if (_hasBaseline)
        {
            taken = MathF.Max(0f, _myHp - myHp);
            dealt = MathF.Max(0f, _theirHp - theirHp);
        }
        _hasBaseline = true;
        _myHp = myHp; _theirHp = theirHp;

        for (int i = _open.Count - 1; i >= 0; i--)
        {
            var r = _open[i];
            r.Dealt += dealt;
            r.Taken += taken;
            if (frame < r.SettleFrame) { _open[i] = r; continue; }

            Settle(in r);
            int last = _open.Count - 1;
            _open[i] = _open[last];
            _open.RemoveAt(last);
        }
    }

    private void Settle(in Record r)
    {
        var off = r.Entry.Offset;
        off[0] = Step(off[0], r.BetDealt, r.Dealt);
        off[1] = Step(off[1], r.BetTaken, r.Taken);
        r.Entry.Settles++;
        Settled++;
    }

    private float Step(float offset, float bet, float realised)
    {
        offset += Eta * (realised - (bet + offset));
        offset = Math.Clamp(offset, -MaxOffset, MaxOffset);
        return offset * (1f - Share);
    }

    public IEnumerable<(string Name, int Bucket, float DealtOffset, float TakenOffset, int Settles)> Offsets
    {
        get
        {
            foreach (var e in _entries.Values)
                yield return (e.Name, e.Bucket, e.Offset[0], e.Offset[1], e.Settles);
        }
    }

    public string Report(int top = 12)
    {
        var list = new List<Entry>(_entries.Values);
        list.Sort((a, b) =>
        {
            int c = Mag(b).CompareTo(Mag(a));
            if (c != 0) return c;
            c = string.CompareOrdinal(a.Name, b.Name);
            return c != 0 ? c : a.Bucket.CompareTo(b.Bucket);
        });
        var sb = new StringBuilder();
        sb.Append("BetLedger: ").Append(Settled).Append(" settled, ").Append(_open.Count).Append(" open\n");
        int n = Math.Min(top, list.Count);
        for (int i = 0; i < n; i++)
        {
            var e = list[i];
            sb.Append($"  {e.Name,-24} {Situation.Describe(e.Bucket),-16} dealt {e.Offset[0],6:+0.00;-0.00} taken {e.Offset[1],6:+0.00;-0.00}  n={e.Settles}\n");
        }
        return sb.ToString();
    }

    private static float Mag(Entry e) => MathF.Max(MathF.Abs(e.Offset[0]), MathF.Abs(e.Offset[1]));
}
