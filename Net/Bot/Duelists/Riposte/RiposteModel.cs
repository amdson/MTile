using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// What Riposte remembers about its opponent — built ONLY from the delayed, visible
// samples the brain feeds it (position, velocity, the visible action/pose, flinch) plus
// its own state (its own HP loss). No opponent internals are ever read.
//
// Everything is a deterministic running tally owned by one bot instance.
public sealed class RiposteModel
{
    // Observed attack starts (a visible attack pose begins) and the gap they started at.
    public int   MeleeStarts;
    public int   MeleeStartsInZone;     // started while within ZoneGap (they swing when I'm near)
    public int   ZoneFrames;            // perceived frames spent inside ZoneGap while they were neutral
    public float PreferredGap = 46f;    // EMA of the gap at their melee starts
    public float HitGap       = 50f;    // EMA of the gap at which their hits landed on me
    public int   RangedStarts;          // terrain / ranged moves started from outside the zone
    public float RhythmFrames = 40f;    // EMA of frames between their attack starts
    public int   Whiffs, Connects;      // their melee starts that did / didn't hurt me within a window
    public int   GuardsSeen;            // times they raised a guard
    public int   MyStabs, MyStabsHit, MySlashes, MySlashesHit;  // my own outcome stats
    public int   MyStabsParried, MySlashesParried;

    public const float ZoneGap = 64f;

    private int   _lastStartFrame = -1000;
    private string _lastAction = "NullAction";
    private readonly List<(int Frame, float Gap)> _pending = new();   // their melee starts awaiting a verdict

    public static bool IsMelee(string a)
        => a.Contains("Slash") || a == "StabAction" || a == "AirWhipKick"
        || a == "AirHammerAction" || a == "PulseAction" || a == "BurstAction" || a == "GrabAction";

    public static bool IsRanged(string a)
        => a == "BlockPaintAction" || a == "BlockPlaceAction" || a == "BlockBurstAction"
        || a == "BeamAction" || a == "LobbedAreaAction" || a == "BlockGrabAction";

    // How readily they swing when I'm close: attack starts per second spent in the zone.
    public float Aggression => (MeleeStartsInZone + 1f) / (ZoneFrames / 60f + 2f);
    // Share of their attacks that are ranged/terrain (zoner-ness).
    public float RangedShare => (RangedStarts + 0.5f) / (RangedStarts + MeleeStarts + 2f);
    public float WhiffRate => (Whiffs + 1f) / (Whiffs + Connects + 2f);
    public float StabSuccess => (MyStabsHit + 1f) / (MyStabs + 2f);
    public float SlashSuccess => (MySlashesHit + 1f) / (MySlashes + 2f);

    // A threat radius for spacing: where their swings start and where they actually hurt.
    public float ThreatGap => MathF.Max(PreferredGap, HitGap);

    // Called once per frame with the perceived (delayed) sample. `gap` is the horizontal
    // gap between the perceived target and where I was at that same (delayed) moment.
    public void Observe(int frame, string action, float gap, bool tookDamageThisFrame, float gapNow)
    {
        bool newAction = action != _lastAction;
        if (newAction && IsMelee(action) && frame - _lastStartFrame > 4)
        {
            MeleeStarts++;
            if (gap < ZoneGap) MeleeStartsInZone++;
            PreferredGap = PreferredGap * 0.85f + MathF.Min(gap, 90f) * 0.15f;
            RhythmFrames = RhythmFrames * 0.8f + Math.Clamp(frame - _lastStartFrame, 6, 180) * 0.2f;
            _lastStartFrame = frame;
            _pending.Add((frame, gap));
        }
        else if (newAction && IsRanged(action) && gap >= ZoneGap * 0.75f)
        {
            RangedStarts++;
        }
        if (newAction && action == "GuardAction") GuardsSeen++;
        if (!IsMelee(action) && !IsRanged(action) && gap < ZoneGap) ZoneFrames++;
        _lastAction = action;

        if (tookDamageThisFrame)
        {
            HitGap = HitGap * 0.8f + MathF.Min(gapNow, 100f) * 0.2f;
            if (_pending.Count > 0) { Connects++; _pending.Clear(); }
        }
        // A start that didn't hurt me within ~20 perceived frames was a whiff (or blocked).
        for (int i = _pending.Count - 1; i >= 0; i--)
            if (frame - _pending[i].Frame > 20) { Whiffs++; _pending.RemoveAt(i); }
    }
}
