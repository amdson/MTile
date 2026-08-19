using System;

namespace MTile;

// The player's block-placement economy. Three pools on three deliberately different
// time horizons, so build capacity has a burst, a recovery, and a ceiling:
//
//   Build      — the reservoir. Big, regenerates slowly. This is what actually caps a
//                long build once everything else is drained.
//   BuildMove  — the working pool placement spends from. Small, refills quickly by
//                pulling from Build. Its refill rate is what sets the sustained
//                placement rate (~4 stone/sec).
//   EruptMove  — the eruption charge. Filled by holding RMB inside terrain, pulling
//                from Build at a FAVORABLE rate, and use-it-or-lose-it (see Decay).
//
// Everything is denominated in "meter units". A tile of material M costs
// MaterialStrengths.BuildCostFor(M) units, so the same rate buys 16× more foam than
// stone. Mass in the TileMassField stays in tile-equivalents (Threshold 1.0) — cost is
// charged here at the source, NOT by varying the cascade quantum, so material changes
// how fast you build without changing the shape of what you build.
//
// Pure value state (four floats), so it snapshots with PlayerAbilityState's
// MemberwiseClone and needs no deep-copy hook.
public sealed class BuildMeters
{
    // ── Reservoir ────────────────────────────────────────────────────────────────
    public const float BuildMax     = 200f;
    // ~1.5 stone/sec truly sustained, once the reservoir is the only source left.
    public const float BuildRegen   = 24f;

    // ── Working pool ─────────────────────────────────────────────────────────────
    // Burst capacity: 6 stone, or ~96 foam, before the refill rate takes over.
    public const float MoveMax      = 48f;
    // The headline number: 16 units/sec ÷ stone's 4.0 cost = 4 stone/sec.
    public const float MoveRefill   = 12f;

    // ── Eruption charge ──────────────────────────────────────────────────────────
    // 1 unit of reservoir buys 2 units of eruption charge. The favorable rate is what
    // makes committing to a charge worth it — and it's also rough compensation for the
    // fact that a fast ball outruns its own scaffolding and wastes some of its mass.
    public const float EruptConversion = 2f;
    // Peak charge, reached in ChargeRampSeconds. 240 units = 60 stone or ~960 foam.
    public const float EruptMax        = 240f;
    public const float ChargeRampSeconds = 2.0f;
    // How long the peak holds before capacity starts bleeding — the "sweet spot",
    // widened from the old hard cliff into an actual timing window.
    public const float PlateauSeconds    = 0.25f;
    // Post-plateau bleed on banked charge. Also runs whenever the player ISN'T charging,
    // which is what keeps the favorable conversion rate from becoming an arbitrage pump:
    // you can't hold RMB in a wall to mint cheap charge and then spend it on ordinary
    // painting, because it drains while you paint.
    public const float EruptDecay        = 60f;
    // Reservoir drain while overholding past the plateau — you keep paying for nothing.
    // Deliberately low rather than capped: the punishment for dithering should be a slow
    // bleed you can notice and stop, not a cliff.
    public const float OverholdDrain     = 15f;
    // Below this, a release can't fire an eruption at all. This is the gate that keeps
    // ordinary painting from erupting: no time spent biting into terrain, no charge, no
    // eruption, regardless of how the button comes up.
    public const float EruptMinToFire    = 40f;

    public float Build     = BuildMax;
    public float BuildMove = MoveMax;
    public float EruptMove;
    // Seconds held charging inside terrain — drives the ramp / plateau / decay phases.
    public float ChargeHeld;

    // Set each frame by the paint action while the cursor is biting into terrain; read
    // and cleared by Step. This indirection exists so upkeep runs exactly once per frame
    // for every player from PlayerCharacter.Update — regen has to happen whether or not
    // a placement action is live, but it must not double-tick when one is.
    public bool ChargingRequested;
    public ChargePhase Phase;

    public float ChargeFraction => EruptMove / EruptMax;
    public bool  CanFireEruption => EruptMove >= EruptMinToFire;

    // Single per-frame entry point. Call once per player per frame, after the action FSM
    // has had its chance to request charging.
    public void Step(float dt)
    {
        if (ChargingRequested) Phase = StepCharging(dt);
        else                 { StepIdle(dt); Phase = ChargePhase.Ramping; }
        ChargingRequested = false;
    }

    // Upkeep for a player who is NOT charging: reservoir regen, working-pool refill, and
    // the use-it-or-lose-it bleed on any banked eruption charge.
    private void StepIdle(float dt)
    {
        Build = MathF.Min(BuildMax, Build + BuildRegen * dt);

        float want = MathF.Min(MoveMax - BuildMove, MoveRefill * dt);
        float take = MathF.Min(want, Build);
        BuildMove += take;
        Build     -= take;

        EruptMove  = MathF.Max(0f, EruptMove - EruptDecay * dt);
        ChargeHeld = 0f;
    }

    // Upkeep while the cursor is biting into terrain with RMB held. Ramps to EruptMax,
    // holds through the plateau, then bleeds capacity while still charging the reservoir
    // for the privilege. Returns the phase for HUD/feedback purposes.
    private ChargePhase StepCharging(float dt)
    {
        // The working pool still refills while charging — the two economies share the
        // reservoir but not the moment.
        float want = MathF.Min(MoveMax - BuildMove, MoveRefill * dt);
        float take = MathF.Min(want, Build);
        BuildMove += take;
        Build     -= take;

        ChargeHeld += dt;

        if (ChargeHeld <= ChargeRampSeconds)
        {
            // Ramp: linear in time, paid for out of the reservoir at the favorable rate.
            float rate    = EruptMax / ChargeRampSeconds;          // charge units/sec
            float gain    = MathF.Min(rate * dt, EruptMax - EruptMove);
            float cost    = gain / EruptConversion;
            float paid    = MathF.Min(cost, Build);
            Build    -= paid;
            EruptMove = MathF.Min(EruptMax, EruptMove + paid * EruptConversion);
            return ChargePhase.Ramping;
        }

        if (ChargeHeld <= ChargeRampSeconds + PlateauSeconds)
            return ChargePhase.Peak;

        // Overhold: capacity bleeds away and the reservoir keeps paying for nothing.
        EruptMove = MathF.Max(0f, EruptMove - EruptDecay * dt);
        Build     = MathF.Max(0f, Build - OverholdDrain * dt);
        return ChargePhase.Overheld;
    }

    // Spend up to `tiles` worth of material. Draws from BuildMove first and falls back to
    // EruptMove, so a charge that never got spent as an eruption can at least be painted
    // out. Returns how many tile-equivalents were actually affordable, which may be less
    // than requested — callers scale their emission by it rather than going into debt.
    public float SpendForTiles(float tiles, TileType type)
    {
        if (tiles <= 0f) return 0f;
        float cost      = tiles * MaterialStrengths.BuildCostFor(type);
        float available = BuildMove + EruptMove;
        float paid      = MathF.Min(cost, available);
        if (paid <= 0f) return 0f;

        float fromMove = MathF.Min(paid, BuildMove);
        BuildMove -= fromMove;
        EruptMove -= paid - fromMove;
        return paid / MaterialStrengths.BuildCostFor(type);
    }

    // True if one tile of `type` is affordable right now (the single-place path, which
    // is all-or-nothing rather than proportional).
    public bool CanAfford(TileType type)
        => BuildMove + EruptMove >= MaterialStrengths.BuildCostFor(type);

    // Convert the banked eruption charge into ball mass (tile-equivalents) and clear it.
    public float ConsumeEruptionMass(TileType type)
    {
        float mass = EruptMove / MaterialStrengths.BuildCostFor(type);
        EruptMove  = 0f;
        ChargeHeld = 0f;
        return MathF.Min(mass, MaxBallMass);
    }

    // Ceiling on a single ball's mass. Without it a full charge spent on foam would ask
    // the cascade for ~960 tiles, almost all of which would be wasted flowing into
    // unsupported air — expensive to simulate and invisible in the result.
    public const float MaxBallMass = 400f;

    public BuildMeters Clone() => (BuildMeters)MemberwiseClone();

    public void CopyFrom(BuildMeters o)
    {
        Build             = o.Build;
        BuildMove         = o.BuildMove;
        EruptMove         = o.EruptMove;
        ChargeHeld        = o.ChargeHeld;
        ChargingRequested = o.ChargingRequested;
        Phase             = o.Phase;
    }
}

public enum ChargePhase { Ramping, Peak, Overheld }
