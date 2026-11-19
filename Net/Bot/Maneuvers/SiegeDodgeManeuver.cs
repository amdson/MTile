using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Step off a telegraphed siege-pod line (Plans/PLANT_SEEDS_PLAN.md §8b). A Reflex: it
// holds Move exclusively and outbids stances and attacks for the short window it applies.
//
// When: some live, unfired, awake siege pod (BotView.SiegePods, any owner — the shot hits
// everyone) is within WindowSeconds of firing, and the bot stands inside its blast disc or
// inside its lance lanes (SiegeBlast.Preview from the pod's muzzle along its aim), each
// widened by Margin. Earlier in the pod's growth it does nothing at all.
//
// Then: walk sideways along the floor to the nearer spot that is outside every such pod's
// footprint (scanned in half-tile steps up to ScanTiles each way), hold there until the
// pod has fired (or died). If no safe spot is in range — deep down a horizontal lane — it
// hops just before the shot as a last resort.
public sealed class SiegeDodgeManeuver : BotManeuver
{
    public float WindowSeconds = 2f;
    public float Margin        = Chunk.TileSize;          // "about a tile either side"
    public float ScanTiles     = 20f;
    public float HopSeconds    = 0.3f;
    public int   MaxFrames     = 180;

    public override Category  Category => Category.Reflex;
    public override MoveClaim Move     => MoveClaim.Exclusive;
    public override int       Cooldown => 0;

    private SiegePodPlant _pod;
    private int _t;

    // Test visibility.
    public int Dodges;

    // Avoids a heavy hit (lance 0.8 / blast 0.9) as negative taken: value ≈ 0.9 HP, well
    // above any stance or attack bet plus the controller's PreemptGap.
    public override Bet Bet(BotView v) => Score(v) > 0f ? new Bet(0f, -0.9f, 30) : Net.Bet.Pass;

    public override float Score(BotView v) => Threat(v, v.Me) != null ? 2f : 0f;

    private float TimeToFire(SiegePodPlant p) => p.GrowSeconds - p.Growth;

    private bool InWindow(SiegePodPlant p) => !p.Dormant && !p.HasFired && !p.IsDead && TimeToFire(p) <= WindowSeconds;

    // The first in-window pod whose footprint contains `pos`, or null.
    private SiegePodPlant Threat(BotView v, Vector2 pos)
    {
        foreach (var p in v.SiegePods)
            if (InWindow(p) && Inside(v, p, pos)) return p;
        return null;
    }

    private bool Inside(BotView v, SiegePodPlant p, Vector2 pos)
    {
        var muzzle = p.Muzzle;
        float pad = Margin + PlayerCharacter.Radius;
        if (Vector2.Distance(pos, muzzle) <= SiegeBlast.BlastRadius + pad) return true;
        var reach = SiegeBlast.Preview(v.Sim.Chunks, muzzle, p.Aim);
        for (int j = 0; j < reach.LaneCount; j++)
        {
            var lane = reach[j];
            var d = lane.End - lane.Start;
            float len = d.Length();
            if (len < 1e-3f) continue;
            var u = d / len;
            var r = pos - lane.Start;
            float along = Vector2.Dot(r, u);
            if (along < -pad || along > len + pad) continue;
            float perp = MathF.Abs(r.X * u.Y - r.Y * u.X);
            if (perp <= Chunk.TileSize * 0.5f + pad) return true;
        }
        return false;
    }

    private bool SafeEverywhere(BotView v, Vector2 pos)
    {
        foreach (var p in v.SiegePods)
            if (InWindow(p) && Inside(v, p, pos)) return false;
        return true;
    }

    // Signed x offset to the nearest safe spot along the floor line, or null.
    private float? Escape(BotView v)
    {
        float step = Chunk.TileSize * 0.5f;
        int n = (int)(ScanTiles * 2f);
        for (int k = 1; k <= n; k++)
        {
            bool l = SafeEverywhere(v, v.Me + new Vector2(-k * step, 0f));
            bool r = SafeEverywhere(v, v.Me + new Vector2(k * step, 0f));
            if (l && r)
            {
                // Tie: away from the pod's muzzle.
                return (v.Me.X >= _pod.Muzzle.X ? 1f : -1f) * k * step;
            }
            if (l) return -k * step;
            if (r) return k * step;
        }
        return null;
    }

    public override void Begin(BotView v)
    {
        _pod = Threat(v, v.Me);
        _t = 0;
        Dodges++;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++;
        BotInput.Move(ref input, 0);
        input.Space = false;
        input.Down  = false;
        if (_pod == null || _pod.IsDead || _pod.HasFired || _t > MaxFrames) return false;

        if (!SafeEverywhere(v, v.Me))
        {
            var dx = Escape(v);
            if (dx.HasValue)
            {
                if (MathF.Abs(dx.Value) > 2f) BotInput.Move(ref input, dx.Value > 0f ? 1 : -1);
            }
            else if (TimeToFire(_pod) <= HopSeconds) input.Space = true;
        }
        return true;   // out (or getting out): hold until the shot
    }
}
