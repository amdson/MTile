using System;
using System.Collections.Generic;

namespace MTile.Net;

// Online learning over a brain's maneuvers: multiplicative weights (Hedge).
//
// Each maneuver carries a weight w (start 1). Choosing: among the maneuvers that score
// > 0 this frame, pick one with probability ∝ (score · w)^Sharpness — the scores still say
// what applies right now, the weights say what has been paying off against THIS opponent,
// and Sharpness keeps the choice mostly greedy. Learning: the HP dealt minus TakenWeight ×
// HP taken in the CreditFrames after a maneuver starts is its reward r, and
// w ← w · e^(Eta · r),
// clamped to [MinWeight, MaxWeight] so nothing is ever ruled out for good.
//
// Bot-side only (not sim state); randomness through the brain's seeded Random, so bouts
// stay reproducible.
public sealed class ManeuverWeights
{
    public float Eta        = 0.7f;   // per HP of net damage
    public float Sharpness  = 3f;
    public float MinWeight  = 0.1f, MaxWeight = 10f;
    public int   CreditFrames = 90;
    // Reward = dealt − TakenWeight · taken. 1: net damage; 0: damage dealt only.
    public float TakenWeight = 1f;

    public readonly Dictionary<string, float> Weight = new();
    public float W(string name) => Weight.TryGetValue(name, out var w) ? w : 1f;

    // The open credit record: the most recently started maneuver.
    private string _open;
    private int    _age;
    private float  _dealt, _taken;
    private float  _myHp = -1f, _theirHp = -1f;

    // Once per frame, before choosing: accumulate HP changes into the open record and
    // settle it once its window has run out. A rise in HP (regen, respawn) counts as 0.
    public void Tick(float myHp, float theirHp)
    {
        if (_myHp >= 0f)
        {
            _taken += MathF.Max(0f, _myHp - myHp);
            _dealt += MathF.Max(0f, _theirHp - theirHp);
        }
        _myHp = myHp; _theirHp = theirHp;
        if (_open != null && ++_age >= CreditFrames) Settle();
    }

    public void Started(string name)
    {
        Settle();
        _open = name; _age = 0; _dealt = 0f; _taken = 0f;
    }

    private void Settle()
    {
        if (_open == null) return;
        float w = W(_open) * MathF.Exp(Eta * (_dealt - TakenWeight * _taken));
        Weight[_open] = Math.Clamp(w, MinWeight, MaxWeight);
        _open = null;
    }

    // Weighted draw among the positive scores. `scores` pairs a maneuver with its score.
    public BotManeuver Pick(List<(BotManeuver M, float Score)> scores, Random rng)
    {
        if (scores.Count == 0) return null;
        double total = 0;
        Span<double> p = scores.Count <= 64 ? stackalloc double[scores.Count] : new double[scores.Count];
        for (int i = 0; i < scores.Count; i++)
        {
            p[i] = Math.Pow(scores[i].Score * W(scores[i].M.Name), Sharpness);
            total += p[i];
        }
        double x = rng.NextDouble() * total;
        for (int i = 0; i < scores.Count; i++) { x -= p[i]; if (x <= 0) return scores[i].M; }
        return scores[^1].M;
    }
}
