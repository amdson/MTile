using System;
using Microsoft.Xna.Framework;

namespace MTile;

public class EnvironmentContext
{
    public PlayerInput Input;
    public Controller Controller;
    public Func<int, MovementState> PreviousState;
    public Func<int, ActionState>   PreviousAction;
    public ChunkMap Chunks;
    public HitboxWorld  Hitboxes;     // offensive — publishers (action FSM, AI) push hitboxes here
    public HurtboxWorld Hurtboxes;    // defensive — read-only for actions, populated each frame by IHittable.PublishHurtboxes
    public IEntitySpawner Spawner;    // ranged-attack actions call SpawnEntity to launch projectiles; null in headless tests
    // Owning player's faction. Stamped on every hitbox/projectile an action publishes
    // so attacks from different players resolve against each other (and stay self-
    // immune). Set by PlayerCharacter.Update from its own Faction each frame.
    public Faction Faction;
    // Owning entity's id. Stamped on every hitbox an action publishes (Hitbox.Source)
    // so a hit can be attributed back to its attacker. Set by PlayerCharacter.Update.
    public EntityId SelfId;
    // Deterministic HitId source for any hitbox an action publishes / projectile it
    // spawns. Set by PlayerCharacter.Update from its (sim-shared) allocator. Replaces
    // the old per-class static counters — see HitIdAllocator.
    public HitIdAllocator HitIds;
    // Player-selected block material, driven by this player's own input (1-4 keys).
    // Read by every action that deposits terrain. Carried here (rather than a static)
    // so each player's selection is independent and rollback-deterministic.
    public TileType ActiveBlockType;
    public IntentBuffer Intents;      // gesture-parsed action intents (Click, Stab, PressEdge); action FSM reads + consumes
    public ConditionState Condition;  // combat condition flags (Slash2Ready, RecoveryActive, …) — lives on PlayerAbilityState
    public CombatState    Combat;     // defensive condition: hitstun, stun, last-hit data — gates jump preconditions etc.
    public CombatSystem   CombatSystem; // sim-shared hit resolver — actions read PeekRecoil(HitId) to apply Newton's-third-law back-impulse
    public int   CurrentFrame;        // monotonic frame counter for intent age + flag expiry
    public float Dt;
    // Frame-scoped force-field registry (COMBAT_FEEL_PLAN Phase 2) — actions publish
    // holding/push fields here, mirroring the Hitboxes lifecycle (cleared every frame
    // by the sim, re-broadcast by live states). Null in hosts that don't drive fields.
    public ForceFieldWorld ForceFields;
    // Jump-press buffer window in frames at THIS context's step rate. Jump states
    // pass this to Intents.Peek/Consume/Refresh so the real-time window
    // (IntentBuffer.JumpBufferSeconds) is rate-independent.
    public int JumpBufferFrames => SimFrames.FromSeconds(IntentBuffer.JumpBufferSeconds, Dt);
    public PhysicsBody Body;
    // Owning player's mass — collision-mode attacks stamp Hitbox.StrikeMass from it
    // (PhysicsBody itself is massless). Set by PlayerCharacter.Update.
    public float Mass;
    public InputIntent Intent;
    // Full action registry, for RecoveryAction's eviction lookahead (it re-runs
    // candidate preconditions under a hypothetical future). Null in hand-built
    // test contexts — lookahead eviction simply disables there.
    public System.Collections.Generic.IReadOnlyList<ActionState> ActionRegistry;
    // Struct copy of the live activation's ActionVars, refreshed each frame by
    // PlayerCharacter.Update before action selection — lets precondition-side
    // logic (the lookahead → CommitProfile) read the incumbent's phase without
    // widening the CheckPreConditions signature.
    public ActionVars CurrentActionVars;
    // The LIVE incumbent action at scan time — unlike PreviousAction(0), which on
    // an exit frame still points at the action that just ended. RecoveryAction
    // keys its entry cases off this: a countdown handoff only happens from a
    // neutral incumbent, so a state that legitimately entered mid-countdown
    // (guard's MaxEntry window) isn't dragged back into the airlock while the
    // stamp is still ticking. Refreshed by PlayerCharacter.Update right before
    // the action scan; null in hand-built test contexts (treated as neutral).
    public ActionState CurrentAction;
    // Lookahead overlay: while RecoveryAction probes a hypothetical future frame,
    // this carries the hypothetical countdown index and RecoveryIndex() returns
    // it verbatim (CurrentFrame is shifted by the same probe). Null outside it.
    public int? LookaheadRecoveryIndex;

    // Frames of hit-imposed disadvantage remaining (hitstun or stun, whichever is
    // longer) — the INVOLUNTARY twin of the Condition recovery stamp
    // (Plans/HIT_AIRLOCK_PLAN.md). Folded into RecoveryIndex below so per-action
    // entry windows gate disadvantage with the same law as self-recovery. Reads
    // CurrentFrame, so it shrinks honestly under the lookahead's frame shift.
    public int HitDisadvantageFrames()
    {
        if (Combat == null) return 0;
        int h = Combat.HitstunActive ? Math.Max(0, Combat.HitstunExpireFrame - CurrentFrame) : 0;
        int s = Combat.StunActive    ? Math.Max(0, Combat.StunExpireFrame    - CurrentFrame) : 0;
        return Math.Max(h, s);
    }

    // The recovery countdown as entrants see it: 0 = fully ready (neutral, or the
    // stamped recovery has expired), >0 = frames remaining on the countdown,
    // null = a live action holds the slot — no direct entry; reaching this state
    // routes through RecoveryAction's eviction lookahead. Neutral hub states
    // (Null, the build holds — ActionState.NeutralForEntry) read through to the
    // countdown; every other action is opaque.
    //
    // The countdown is the MAX of the self stamp (an attack's Exit) and the
    // hit-imposed disadvantage window — max-merge, so getting jabbed mid-move
    // can never shorten the move's own tail (a hit is not a cheap self-cancel).
    // `includeHitDisadvantage: false` is the struggle channel's exemption
    // (GrabbedSlash): it reads the self stamp alone, since pummel hitstun must
    // not lock a grabbed victim out of struggling.
    public int? RecoveryIndex(bool includeHitDisadvantage = true)
    {
        int hit = includeHitDisadvantage ? HitDisadvantageFrames() : 0;
        if (LookaheadRecoveryIndex is int overlay) return Math.Max(overlay, hit);
        var cur = PreviousAction?.Invoke(0);
        if (cur != null && cur is not RecoveryAction && !cur.NeutralForEntry) return null;
        int stamp = Condition != null && Condition.RecoveryActive
            ? Math.Max(0, Condition.RecoveryExpireFrame - CurrentFrame)
            : 0;
        return Math.Max(stamp, hit);
    }
    // Multiplicative scalars on movement knobs (WalkAccel, MaxAirSpeed, GroundFriction, …).
    // Reset to Identity each frame in PlayerCharacter.Update, populated by the current
    // action's ApplyMovementModifiers, then read by movement states at their config sites.
    public MovementModifiers Modifiers;

    private bool _groundSearched;
    private bool _hasGround;
    private FloatingSurfaceDistance _groundContact;

    private bool _crouchGroundSearched;
    private bool _hasCrouchGround;
    private FloatingSurfaceDistance _crouchGroundContact;

    private bool _wallSearched1;
    private bool _hasWall1;
    private FloatingSurfaceDistance _wallContact1;

    private bool _wallSearchedMinus1;
    private bool _hasWallMinus1;
    private FloatingSurfaceDistance _wallContactMinus1;

    private bool _cornerSearched1;
    private bool _hasCorner1;
    private ExposedCorner _corner1;

    private bool _cornerSearchedMinus1;
    private bool _hasCornerMinus1;
    private ExposedCorner _cornerMinus1;

    private bool _lowerCornerSearched1;
    private bool _hasLowerCorner1;
    private ExposedLowerCorner _lowerCorner1;

    private bool _lowerCornerSearchedMinus1;
    private bool _hasLowerCornerMinus1;
    private ExposedLowerCorner _lowerCornerMinus1;

    private bool _ledgeCornerSearched1;
    private bool _hasLedgeCorner1;
    private ExposedCorner _ledgeCorner1;

    private bool _ledgeCornerSearchedMinus1;
    private bool _hasLedgeCornerMinus1;
    private ExposedCorner _ledgeCornerMinus1;

    private bool _ceilingSearched;
    private bool _hasCeiling;
    private FloatingSurfaceDistance _ceilingContact;

    private Corridor _corridor1;
    private Corridor _corridorMinus1;

    // Optional long-lived scratch corridors (owned by PlayerCharacter) so the per-frame
    // corridor cache above doesn't allocate; a scan fully rewrites them before any read.
    // Null (e.g. a hand-built test context) falls back to allocating per scan.
    public Corridor CorridorScratch1;
    public Corridor CorridorScratchMinus1;

    // Pooled corrector scratch (predict → rows → solve buffers), owned by
    // PlayerCharacter. Pure derived data, never snapshot state. Null in hand-built
    // test contexts — corrector states degrade to their authored feedforward.
    public CorrectorScratch Corrector;
    // World gravity as the owning sim applies it (PlayerCharacter.Gravity) — the
    // corrector's predictor integrates against the same field the physics step uses.
    public Vector2 Gravity;

    public bool TryGetGround(out FloatingSurfaceDistance ground) =>
        TryGetGroundAt(PlayerCharacter.Radius, GroundChecker.ProbeSlack, ref _groundSearched, ref _hasGround, ref _groundContact, out ground);

    // Crouch probe keeps floatHeight 0 (the contact's hover distance — CoveredJump
    // servos against it), but must still SEE ground from the fold-standing hover
    // (FoldHoverOffset above the surface), so the hover is added to the probe reach.
    public bool TryGetCrouchGround(out FloatingSurfaceDistance ground) =>
        TryGetGroundAt(0f, GroundChecker.ProbeSlack + MovementConfig.Current.FoldHoverOffset,
            ref _crouchGroundSearched, ref _hasCrouchGround, ref _crouchGroundContact, out ground);

    private bool TryGetGroundAt(float floatHeight, float probeSlack, ref bool searched, ref bool has, ref FloatingSurfaceDistance contact, out FloatingSurfaceDistance ground)
    {
        if (!searched)
        {
            // Thread Dt through so end-of-step prediction picks the surface that
            // *will* be highest after one timestep — see GroundChecker.TryFind.
            // MaxGroundEngageVnRel gates the spring against high-relative-speed
            // engagement, so a hard-bouncing body lands via the swept-impact
            // path instead of being caught by the FSD. Off by default.
            has = GroundChecker.TryFind(
                Body, Chunks,
                PlayerCharacter.Radius, floatHeight,
                probeSlack, Dt,
                MovementConfig.Current.MaxGroundEngageVnRel,
                out contact);
            searched = true;
        }
        ground = contact;
        return has;
    }

    public bool TryGetWall(int dir, out FloatingSurfaceDistance wall)
    {
        if (dir == 1)
        {
            if (!_wallSearched1)
            {
                _hasWall1 = WallChecker.TryFind(Body, Chunks, PlayerCharacter.Radius, 0f, 1, out _wallContact1);
                _wallSearched1 = true;
            }
            wall = _wallContact1;
            return _hasWall1;
        }
        else if (dir == -1)
        {
            if (!_wallSearchedMinus1)
            {
                _hasWallMinus1 = WallChecker.TryFind(Body, Chunks, PlayerCharacter.Radius, 0f, -1, out _wallContactMinus1);
                _wallSearchedMinus1 = true;
            }
            wall = _wallContactMinus1;
            return _hasWallMinus1;
        }
        
        wall = null;
        return false;
    }

    public bool TryGetExposedCorner(int dir, out ExposedCorner corner)
    {
        if (dir == 1)
        {
            if (!_cornerSearched1)
            {
                _hasCorner1 = ExposedUpperCornerChecker.TryFind(Body, Chunks, 1, out _corner1);
                _cornerSearched1 = true;
            }
            corner = _corner1;
            return _hasCorner1;
        }
        else if (dir == -1)
        {
            if (!_cornerSearchedMinus1)
            {
                _hasCornerMinus1 = ExposedUpperCornerChecker.TryFind(Body, Chunks, -1, out _cornerMinus1);
                _cornerSearchedMinus1 = true;
            }
            corner = _cornerMinus1;
            return _hasCornerMinus1;
        }
        corner = default;
        return false;
    }
    
    public bool TryGetLedgeCorner(int dir, out ExposedCorner corner)
    {
        if (dir == 1)
        {
            if (!_ledgeCornerSearched1)
            {
                _hasLedgeCorner1 = ExposedUpperCornerChecker.TryFindAboveHead(Body, Chunks, 1, out _ledgeCorner1);
                _ledgeCornerSearched1 = true;
            }
            corner = _ledgeCorner1;
            return _hasLedgeCorner1;
        }
        else if (dir == -1)
        {
            if (!_ledgeCornerSearchedMinus1)
            {
                _hasLedgeCornerMinus1 = ExposedUpperCornerChecker.TryFindAboveHead(Body, Chunks, -1, out _ledgeCornerMinus1);
                _ledgeCornerSearchedMinus1 = true;
            }
            corner = _ledgeCornerMinus1;
            return _hasLedgeCornerMinus1;
        }
        corner = default;
        return false;
    }

    // Local-geometry corridor ahead of the body (Plans/CORRIDOR_MANEUVER_PLAN.md). Unlike the
    // TryGetX queries there is no "absent" case — a scan always yields a Corridor (possibly with
    // ColumnCount == 0 when the very first column is a wall). Cached per frame per direction;
    // the Corridor itself is pure derived data and never snapshot state.
    public Corridor GetCorridor(int dir)
    {
        if (dir == 1)  return _corridor1      ??= CorridorProbe.ScanInto(CorridorScratch1      ?? new Corridor(1),  Body, Chunks);
        if (dir == -1) return _corridorMinus1 ??= CorridorProbe.ScanInto(CorridorScratchMinus1 ?? new Corridor(-1), Body, Chunks);
        return null;
    }

    public bool TryGetCeiling(out FloatingSurfaceDistance ceiling)
    {
        if (!_ceilingSearched)
        {
            _hasCeiling = CeilingChecker.TryFind(Body, Chunks, out _ceilingContact);
            _ceilingSearched = true;
        }
        ceiling = _ceilingContact;
        return _hasCeiling;
    }

    public bool TryGetExposedLowerCorner(int dir, out ExposedLowerCorner corner)
    {
        if (dir == 1)
        {
            if (!_lowerCornerSearched1)
            {
                _hasLowerCorner1 = ExposedLowerCornerChecker.TryFind(Body, Chunks, 1, out _lowerCorner1);
                _lowerCornerSearched1 = true;
            }
            corner = _lowerCorner1;
            return _hasLowerCorner1;
        }
        else if (dir == -1)
        {
            if (!_lowerCornerSearchedMinus1)
            {
                _hasLowerCornerMinus1 = ExposedLowerCornerChecker.TryFind(Body, Chunks, -1, out _lowerCornerMinus1);
                _lowerCornerSearchedMinus1 = true;
            }
            corner = _lowerCornerMinus1;
            return _hasLowerCornerMinus1;
        }
        corner = default;
        return false;
    }
}
