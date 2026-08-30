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
// Pure value state (six floats), so it snapshots with PlayerAbilityState's
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
    // Floor for the block-charge gesture (double-RMB on a solid tile). Deliberately far
    // above EruptMinToFire and at 3/4 of a full ramp: erupting is the cheap, improvised
    // use of a charge, and charging a block is the expensive one you have to actually
    // commit two seconds of holding to. It also keeps the two spends from competing at
    // the margin — a charge big enough to charge a block was never a marginal eruption.
    public const float BlockChargeMin    = EruptMax * 0.75f;
    // Above this, a held RMB charges instead of painting — out in open air included. A
    // banked charge is a commitment, and painting spent it by accident: sweeping the
    // cursor out of the ground demoted the hold to a paint stroke, and SpendForTiles
    // falls through to EruptMove once BuildMove runs dry, so a stroke you only meant as
    // a wind-up quietly ate the charge.
    //
    // This only ever applies to a charge held ACROSS a live hold now. It used to outlive
    // the button, and the claim here that the idle bleed "crosses back under it in a
    // third of a second" was only true of a nearly-empty meter: from a full 240 at
    // EruptDecay 60/s it is 3.7 SECONDS, all of it spent unable to place a block. See
    // RetireChargeOnRelease.
    public const float PaintLockoutMin   = EruptMinToFire * 0.5f;

    // ── Post-release reserve ─────────────────────────────────────────────────────
    // Releasing RMB now clears the meter outright instead of leaving it to bleed. What
    // was on it moves here: a short-lived reserve that ONLY the block-charge
    // double-click can draw on.
    //
    // The split exists because the two things a charge can still be spent on after the
    // button comes up want opposite treatment. An eruption fires from inside the hold
    // (BlockPaintAction.Exit), so it reads the live meter and never needs this. The
    // block charge cannot: IsRightDoubleClick deliberately REJECTS the hold→release→press
    // shape that charging produces, so the gesture is necessarily a separate pair of
    // clicks landed after the charging hold has already ended. Zeroing the meter with no
    // reserve would make that gesture unpayable — the failure the split is here to avoid.
    //
    // Meanwhile the live meter is what gates painting, so emptying it is what gives the
    // player their build back immediately.
    //
    // One second is not a guess: at EruptDecay 60/s a full meter used to fall from
    // EruptMax to BlockChargeMin in exactly 1.0s, so this preserves the window the
    // gesture already had to the frame. What it drops is the 2.7s of tail after that,
    // where the charge was too small to charge a block and still too big to paint past.
    public const float BankedGraceSeconds = 1.0f;

    public float Build     = BuildMax;
    public float BuildMove = MoveMax;
    public float EruptMove;
    // Seconds held charging inside terrain — drives the ramp / plateau / decay phases.
    public float ChargeHeld;
    // Charge retired off the meter at the last release, and the time it has left. Not
    // part of EruptMove on purpose: it must be invisible to ChargeLocksPaint and to
    // SpendForTiles, which is the whole point of moving it (see BankedGraceSeconds).
    public float BankedCharge;
    public float BankedSeconds;

    // Set each frame by the paint action while the cursor is biting into terrain; read
    // and cleared by Step. This indirection exists so upkeep runs exactly once per frame
    // for every player from PlayerCharacter.Update — regen has to happen whether or not
    // a placement action is live, but it must not double-tick when one is.
    public bool ChargingRequested;
    public ChargePhase Phase;

    public float ChargeFraction => EruptMove / EruptMax;
    public bool  CanFireEruption => EruptMove >= EruptMinToFire;
    // True when there's enough banked charge that a held RMB should keep charging rather
    // than fall through to painting. See PaintLockoutMin.
    public bool  ChargeLocksPaint => EruptMove >= PaintLockoutMin;

    // Single per-frame entry point. Call once per player per frame, after the action FSM
    // has had its chance to request charging.
    //
    // `buildHeld` is the raw RMB state, and the retire is keyed off it rather than off
    // the paint action's Exit for one reason: an attack can PREEMPT the stroke with the
    // button still down, and that path never runs Exit again. Keyed on the action, a
    // charge preempted and then released would keep the old lockout forever. Keyed on
    // the button, "not held" is not held however the stroke ended.
    //
    // It must not key on !ChargingRequested either — that is false for a single frame
    // during a preempt while RMB is still down, and the stroke is documented to resume
    // with its charge intact.
    // No default on `buildHeld`: omitting it would read as "released" and quietly retire
    // every charge, which is the one wrong answer a caller could give by accident.
    public void Step(float dt, bool buildHeld)
    {
        // Before the upkeep branch, not after: StepIdle bleeds EruptMove, so retiring
        // downstream of it banked one frame of decay less than the player actually built
        // (239 of a 240 meter). Immaterial to any gate, but the reserve should be what
        // was on the meter when the button came up, not what survived one more tick.
        if (!buildHeld) RetireChargeOnRelease();

        if (ChargingRequested) Phase = StepCharging(dt);
        else                 { StepIdle(dt); Phase = ChargePhase.Ramping; }
        TickBanked(dt);
        ChargingRequested = false;
    }

    // Clear the live meter and move what was on it into the reserve. Idempotent, so the
    // frames after a release cost nothing and can't refresh the window: with the meter
    // already empty there is nothing to retire, and the reserve keeps ticking down.
    //
    // An eruption has already run by the time this is called on the release frame — it
    // fires from BlockPaintAction.Exit, which is upstream of the meter upkeep — so it
    // reads and spends the full meter and this finds nothing left. That ordering is what
    // lets the normal avalanche release keep working unchanged.
    public void RetireChargeOnRelease()
    {
        float retiring = EruptMove;
        EruptMove  = 0f;
        ChargeHeld = 0f;

        // The reserve keeps the BEST charge recently released, not the latest one. This
        // is load-bearing rather than defensive: the double-click's first click lands
        // with the cursor in solid, so it charges for its four frames and then releases
        // ~8 units. Overwriting on release let that tap clobber the 240 the player
        // actually built, and the second click found a reserve too small to spend.
        //
        // Not refreshing the timer on a smaller retire matters too — otherwise tapping
        // RMB would renew the window indefinitely and the charge would never expire.
        // A genuine second full charge can't be caught by that: the ramp is 2s and the
        // window is 1s, so the old reserve is always gone before a new one is built.
        if (retiring <= BankedCharge) return;
        BankedCharge  = retiring;
        BankedSeconds = BankedGraceSeconds;
    }

    // The timer is the ONLY thing that ages the reserve — deliberately, after a first
    // cut also dropped it whenever charging resumed. That sounded right ("a fresh hold
    // owns the meter") and broke the gesture it exists for: the double-click's FIRST
    // click is itself a hold with the cursor in solid, so it requests charging for four
    // frames and wiped the reserve before the second click could spend it.
    //
    // Nothing is lost by letting it simply expire. A long hold cannot be half of a
    // double-click (IsRightDoubleClick rejects it), and two short clicks bank nothing
    // worth double-counting, so a reserve that outlives the start of a new charge still
    // has only the one gesture that can reach it.
    private void TickBanked(float dt)
    {
        if (BankedSeconds <= 0f) { BankedCharge = 0f; return; }
        BankedSeconds -= dt;
        if (BankedSeconds <= 0f) BankedCharge = BankedSeconds = 0f;
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
    //
    // `bonusUnits` is charge the METER never held: charged blocks recruited from around
    // the launch site each throw a whole EruptMax into the pot
    // (BlockEruptionHelpers.RecruitChargedBlocks). It is added on top rather than
    // clamped into EruptMax, which is the entire point of staging charges — an eruption
    // fired next to two of them is three meters wide.
    //
    // MaxBallMass scales with the bonus for the same reason. The cap exists to stop a
    // full charge of cheap material asking the cascade for ~960 tiles that mostly die in
    // open air; it is a per-meter budget, so a three-meter eruption gets three times the
    // budget rather than being quietly flattened back to one.
    public float ConsumeEruptionMass(TileType type, float bonusUnits = 0f)
    {
        float cost = MaterialStrengths.BuildCostFor(type);
        float mass = (EruptMove + MathF.Max(0f, bonusUnits)) / cost;
        float cap  = MaxBallMass * (1f + MathF.Max(0f, bonusUnits) / EruptMax);
        EruptMove  = 0f;
        ChargeHeld = 0f;
        return MathF.Min(mass, cap);
    }

    // Spend the WHOLE banked charge to charge one block. All-or-nothing: returns false
    // and touches nothing if the meter is short, so the caller can leave the gesture
    // inert rather than half-paying for it. Unlike ConsumeEruptionMass this converts to
    // nothing — the charge is the price of the flag, not a quantity the block stores.
    //
    // Draws on the live meter AND the post-release reserve, because in practice the
    // reserve is the only one that can ever pay: the gesture is two short clicks, and
    // IsRightDoubleClick rejects the long hold that fills a meter, so by the time the
    // second click lands the charging hold is necessarily over. Summing both keeps the
    // rule stated as what it means — "a full meter's worth, however recently it was
    // still on the meter" — rather than depending on that argument staying true.
    public bool TrySpendForBlockCharge()
    {
        if (EruptMove + BankedCharge < BlockChargeMin) return false;
        EruptMove     = 0f;
        ChargeHeld    = 0f;
        BankedCharge  = 0f;
        BankedSeconds = 0f;
        return true;
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
        BankedCharge      = o.BankedCharge;
        BankedSeconds     = o.BankedSeconds;
        ChargingRequested = o.ChargingRequested;
        Phase             = o.Phase;
    }
}

public enum ChargePhase { Ramping, Peak, Overheld }
