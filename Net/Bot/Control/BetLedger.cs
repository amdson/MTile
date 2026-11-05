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
    // Step per settle. Adaptive: 1 / (PriorWeight + settles), floored at EtaMin, so the
    // corrected bet is the running mean of realised outcomes with the authored bet counting
    // as PriorWeight samples, and after many settles it still tracks a changing opponent at
    // EtaMin. (A constant 0.3 made every estimate swing on its last two or three outcomes —
    // in a game where most horizons realise 0 and a hit is 0.5 HP, selection chased noise.)
    public float PriorWeight = 2f;
    public float EtaMin      = 0.05f;
    // Hierarchical offsets: an arm-level offset (shared by all buckets of the arm, bucket key
    // -1) learns fast from every settle; the bucket-level offset on top needs more evidence
    // (BucketPriorWeight) before it deviates. One bad Grab then counts against Grab everywhere.
    public bool  Hierarchical      = true;
    public float BucketPriorWeight = 6f;
    // Bootstrapping (SARSA over options): when the controller closes a record because the
    // next arm in its category started, the target is realised + Gamma^frames * the next
    // arm's corrected bet, per channel. So a guard that deals nothing but leaves the
    // opponent in recovery inherits the value of the string that follows. Gamma per frame;
    // 1 = no discount, 0 = myopic (the pre-bootstrap behaviour). Records that time out at
    // their horizon are terminal.
    public float Gamma = 0.99f;
    // Legacy constant step: when > 0 it replaces the adaptive step (tests, A/B).
    public float Eta         = 0f;
    public float Share       = 0.02f;  // fixed-share decay of offsets toward 0 per settle
    public float MaxOffset   = 2f;     // HP clamp on each offset

    private sealed class Entry
    {
        public readonly string  Name;
        public readonly int     Bucket;
        public readonly float[] Offset = new float[2];
        public int Settles;
        public float RealisedDealt, RealisedTaken;   // sums over settled records: the arm's realised mean per bucket
        public Entry(string name, int bucket) { Name = name; Bucket = bucket; }
    }

    private struct Record
    {
        public int   Id;
        public Entry Entry, Arm;   // Arm: the arm-level entry (bucket -1), null when !Hierarchical
        public float BetDealt, BetTaken;
        public int   OpenFrame, SettleFrame;
        public float Dealt, Taken;
    }

    // A record closed (by the controller, when the next arm in its category starts) before
    // it has lived this many frames is dropped unscored: a stance that held Move for three
    // frames says nothing about the stance.
    public int MinRecordFrames = 8;

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

    public int Settles(string name, int bucket) => _entries.TryGetValue((name, bucket), out var e) ? e.Settles : 0;

    public Bet Corrected(string name, int bucket, Bet bet)
    {
        float d = 0f, t = 0f;
        if (_entries.TryGetValue((name, bucket), out var e)) { d += e.Offset[0]; t += e.Offset[1]; }
        if (Hierarchical && _entries.TryGetValue((name, -1), out var a)) { d += a.Offset[0]; t += a.Offset[1]; }
        return d == 0f && t == 0f ? bet : new Bet(bet.Dealt + d, bet.Taken + t, bet.Horizon);
    }

    // Opens a record settling at frame + bet.Horizon. Returns a record id (diagnostic only).
    public int Open(string name, int bucket, Bet bet, int frame)
    {
        var key = (name, bucket);
        if (!_entries.TryGetValue(key, out var e)) { e = new Entry(name, bucket); _entries[key] = e; }
        Entry arm = null;
        if (Hierarchical && !_entries.TryGetValue((name, -1), out arm)) { arm = new Entry(name, -1); _entries[(name, -1)] = arm; }
        _open.Add(new Record
        {
            Id = _nextId, Entry = e, Arm = arm, BetDealt = bet.Dealt, BetTaken = bet.Taken,
            OpenFrame = frame, SettleFrame = frame + bet.Horizon,
        });
        return _nextId++;
    }

    // The next arm in this record's category has started: score what was realised so far
    // (the option's return runs until the next decision; the horizon is only a cap), or drop
    // the record if it was too short to mean anything. Unknown or already settled ids are
    // ignored.
    public void Close(int id, int frame) => Close(id, frame, Bet.Pass);

    // `next`: the corrected bet of the arm that starts now in this record's category (Pass =
    // terminal, no bootstrap).
    public void Close(int id, int frame, Bet next)
    {
        for (int i = 0; i < _open.Count; i++)
        {
            if (_open[i].Id != id) continue;
            var r = _open[i];
            if (next.Applicable && Gamma > 0f)
            {
                float g = MathF.Pow(Gamma, Math.Max(0, frame - r.OpenFrame));
                r.Dealt += g * next.Dealt;
                r.Taken += g * next.Taken;
            }
            if (frame - r.OpenFrame >= MinRecordFrames) Settle(in r);
            int last = _open.Count - 1;
            _open[i] = _open[last];
            _open.RemoveAt(last);
            return;
        }
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
        if (r.Arm != null)
        {
            // Arm level first (its error is measured against bet + arm offset alone), then
            // the bucket level corrects what remains.
            var ao = r.Arm.Offset;
            float etaA = Eta > 0f ? Eta : MathF.Max(EtaMin, 1f / (PriorWeight + r.Arm.Settles));
            ao[0] = Step(ao[0], r.BetDealt, r.Dealt, etaA);
            ao[1] = Step(ao[1], r.BetTaken, r.Taken, etaA);
            r.Arm.RealisedDealt += r.Dealt; r.Arm.RealisedTaken += r.Taken;
            r.Arm.Settles++;
            float etaB = Eta > 0f ? Eta : MathF.Max(EtaMin, 1f / (BucketPriorWeight + r.Entry.Settles));
            off[0] = Step(off[0], r.BetDealt + ao[0], r.Dealt, etaB);
            off[1] = Step(off[1], r.BetTaken + ao[1], r.Taken, etaB);
        }
        else
        {
            float eta = Eta > 0f ? Eta : MathF.Max(EtaMin, 1f / (PriorWeight + r.Entry.Settles));
            off[0] = Step(off[0], r.BetDealt, r.Dealt, eta);
            off[1] = Step(off[1], r.BetTaken, r.Taken, eta);
        }
        r.Entry.RealisedDealt += r.Dealt; r.Entry.RealisedTaken += r.Taken;
        r.Entry.Settles++;
        Settled++;
    }

    private float Step(float offset, float bet, float realised, float eta)
    {
        offset += eta * (realised - (bet + offset));
        offset = Math.Clamp(offset, -MaxOffset, MaxOffset);
        return offset * (1f - Share);
    }

    // Per (arm, bucket): mean realised dealt / taken over its settled records.
    public IEnumerable<(string Name, int Bucket, float Dealt, float Taken, int Settles)> Realised
    {
        get
        {
            foreach (var e in _entries.Values)
                if (e.Settles > 0) yield return (e.Name, e.Bucket, e.RealisedDealt / e.Settles, e.RealisedTaken / e.Settles, e.Settles);
        }
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
