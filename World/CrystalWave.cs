using System;
using Microsoft.Xna.Framework;

namespace MTile;

// todo.txt #7 — the spiked avalanche ("crystal wave"). Every dial for the attack lives
// here, as code constants for the same reason CrystalLifecycle's spike timings are: they
// are sim-affecting combat telegraph numbers pinned by tests, not movement tuning, so
// they don't belong on the hot-reloaded MovementConfig.
//
// The attack, end to end:
//   INPUT   — spikes selected (picker 5), plain RMB held INSIDE solid charges exactly like
//             the painter's eruption, and a fast release launches it: the painter's own
//             gesture (BlockPaintAction's spike mode), where a stroke spikes the cells its
//             ball crosses instead of painting. Shift+RMB spiking is untouched
//             (BlockPlaceAction).
//   PATH    — the normal avalanche path: a MassBall (flagged Crystal) flies, leaks mass
//             into TileMassField, and the cascade's commits become crystal REQUESTS
//             (ChunkMap.TryRequestCrystal) instead of tile requests. Same accept/reject
//             shape as a tile — the cell must be empty with support — so the wave covers
//             the cells an ordinary avalanche would have built. Walls stop the ball (#4's
//             swept stop) and a crystal can't be requested into anything occupied.
//   TIMING  — CrystalWaveField: the wave's first crystal (and any patch not connected to
//             it) is a ROOT and runs the long Root lifecycle — the ~1 s warning. Every
//             other requested cell waits, and is seeded the moment a 4-neighbour crystal
//             of its wave reaches Grown (the grown crystal is the substrate that seeds the
//             next one), then runs the short Follow lifecycle. So the front sweeps the
//             requested set outward from the root at FollowFramesPerCell, and warning
//             time and wave speed are separate dials. Grown crystals then vanish.
//   DAMAGE  — CrystalContactSystem, only in the Grown phase: CrystalDamage per crystal,
//             capped at WaveDamageCap per victim per wave; the crystal that lands BREAKS.
//             Flashing (forming) crystals shove bodies out along their growth normal with
//             no damage. The grown shard's hit is occluded by terrain from its base.
//   COST    — UnitsPerCrystal meter units per crystal (sand's price), and a
//             per-player CooldownSeconds on BuildMeters, set at launch.
public static class CrystalWave
{
    // ── Timing ──────────────────────────────────────────────────────────────────
    // The warning: a root crystal seeds for RootSeedSeconds and flashes for
    // RootFlashSeconds, so the first crystals on the ground become dangerous exactly
    // 1.0 s after they appear.
    public const float RootSeedSeconds  = 0.8f;
    public const float RootFlashSeconds = 0.2f;
    // Wave speed: a seeded follower is dangerous FollowFramesPerCell frames later and
    // seeds its neighbours on that same frame, so the front advances one cell per
    // FollowFramesPerCell frames (3 → 20 cells/s ≈ 220 px/s at TileSize 11 — a touch
    // under walk-to-dash range, and faster than the 60 px/s walk).
    public const int   FollowSeedFrames  = 1;
    public const int   FollowFlashFrames = 2;
    public const int   FollowFramesPerCell = FollowSeedFrames + FollowFlashFrames;
    // How long a crystal stays grown (dangerous) before it vanishes. At 3 f/cell the
    // dangerous band trailing the front is GrownFrames / 3 ≈ 7 cells deep.
    public const float GrownSeconds = 0.35f;

    public static readonly CrystalLifecycle Root = new(
        CrystalLifecycle.Frames(RootSeedSeconds), CrystalLifecycle.Frames(RootFlashSeconds),
        CrystalLifecycle.Frames(GrownSeconds));
    public static readonly CrystalLifecycle Follow = new(
        FollowSeedFrames, FollowFlashFrames, CrystalLifecycle.Frames(GrownSeconds));

    // ── Bookkeeping horizons (frames) ───────────────────────────────────────────
    // A vanished/broken crystal lingers this long as RESIDUE: invisible and harmless, but
    // its cell refuses a second crystal from mass still sloshing around in the field,
    // and a late request beside it continues the wave instead of opening a new root.
    public const int ResidueFrames = 90;
    // A requested crystal the front never reached (its connection was broken — a wall
    // went up across it, a crystal on its only path broke on a player) is dropped.
    public const int StalePendingFrames = 300;
    // A wave record outlives its ball's last touch by this much (the ball lives ≤ 2.5 s;
    // every one of its crystals is gone well inside this).
    public const int WaveIdleFrames = 600;

    // ── Damage ──────────────────────────────────────────────────────────────────
    public const float CrystalDamage = 0.5f;
    public const float WaveDamageCap = 1.5f;
    // Minimum frames between two crystals of one wave landing on one victim — so a body
    // dropped onto a grown field isn't billed three crystals on one frame. The cap is
    // still what bounds a wave; this just spreads it over a beat.
    public const int   ReHitFrames = 9;
    // Knockback along the crystal's growth normal (Impulse mode; the victim's mass
    // divides it). Under CombatState.BigHitStrength (280) so a crystal never stuns.
    public const float KnockbackImpulse = 260f;
    public const float HitstunSeconds   = 0.15f;
    // Forming crystals (Flash phase) set a body's speed along the growth normal to at
    // least this — the shove out of the shard's way. No damage.
    public const float PushSpeed = 140f;
    // Contact slop (px) between a hurtbox and a shard, like SpikeContactSystem's.
    public const float ContactSlop = 2f;

    // ── Geometry (px) ───────────────────────────────────────────────────────────
    // A shard stands on the face it grew from (the substrate) and points away from it.
    // Grown it spans its own cell and pokes most of a cell past it: the "large" shape,
    // versus #8's spikes, which never leave their face.
    public const float GrownLength = 1.8f * Chunk.TileSize;
    public const float SeedLength  = 0.3f * Chunk.TileSize;
    public const float HalfWidth   = 0.42f * Chunk.TileSize;

    // ── Economy ─────────────────────────────────────────────────────────────────
    // Meter units per crystal: sand's price (configs/material_strengths.json — Stone
    // 1.94, Dirt 0.77, Sand 0.44), the cheapest block that lasts. Cheap on purpose: the
    // wave is supposed to be big, and what keeps it in check is the meter and the
    // telegraph. Capped like an ordinary avalanche's ball.
    public const float UnitsPerCrystal = 0.44f;
    public const float MaxBallMass     = BuildMeters.MaxBallMass;
    // No cooldown (2026-11-05, user: "just remove that"); the plumbing (BuildMeters.
    // CrystalCooldown / CrystalWaveReady) stays so one is a number away if it comes back.
    public const float CooldownSeconds = 0f;
    // The crystal ball's leak curve (MassBall: per second, mass·LeakFraction + LeakFloor).
    // The same as the build ball's, so a crystal wave takes an ordinary avalanche's shape.
    public const float LeakFraction = 6f;
    public const float LeakFloor    = 60f;

    // Length of a shard at a given point in its life (Seed swells to SeedLength; Flash
    // grows to full; Grown is full). Shared by the contact system and the renderer so
    // the push-out shape is the shape you see.
    public static float LengthAt(in CrystalLifecycle life, int elapsed)
    {
        var phase = life.PhaseAt(elapsed);
        float p = life.PhaseProgress(elapsed);
        return phase switch
        {
            CrystalPhase.Seed  => MathHelper.Lerp(0.4f * SeedLength, SeedLength, p),
            CrystalPhase.Flash => MathHelper.Lerp(SeedLength, GrownLength, p),
            CrystalPhase.Grown => GrownLength,
            _                  => 0f,
        };
    }

    // Unit growth normal for a crystal whose substrate lies on `baseFace` of its cell.
    public static Vector2 Normal(SproutFaces baseFace)
    {
        var o = TileSproutNode.FaceOffset(baseFace);
        return new Vector2(-o.X, -o.Y);
    }

    // The midpoint of the substrate face of cell (gtx, gty): where the shard stands.
    public static Vector2 BasePoint(int gtx, int gty, SproutFaces baseFace)
    {
        const float T = Chunk.TileSize;
        var o = TileSproutNode.FaceOffset(baseFace);
        return new Vector2(gtx * T + T * 0.5f + o.X * T * 0.5f, gty * T + T * 0.5f + o.Y * T * 0.5f);
    }

    // AABB of a shard of `length` standing on the base face of (gtx, gty).
    public static BoundingBox ShardBox(int gtx, int gty, SproutFaces baseFace, float length)
    {
        var b = BasePoint(gtx, gty, baseFace);
        var n = Normal(baseFace);
        var tip = b + n * length;
        float hw = HalfWidth;
        if (n.X == 0f)
            return new BoundingBox(b.X - hw, MathF.Min(b.Y, tip.Y), b.X + hw, MathF.Max(b.Y, tip.Y));
        return new BoundingBox(MathF.Min(b.X, tip.X), b.Y - hw, MathF.Max(b.X, tip.X), b.Y + hw);
    }

    // Occlusion origin for a grown shard: just inside its own (empty) cell, off the base
    // face, so a hit reaches only what the shard has a clear line to from where it grew.
    public static Vector2 OcclusionOrigin(int gtx, int gty, SproutFaces baseFace)
        => BasePoint(gtx, gty, baseFace) + Normal(baseFace) * 0.5f;
}
