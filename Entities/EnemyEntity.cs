using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// MVP enemy host. An Entity subtype so PhysicsWorld / CombatSystem / hurtboxes /
// snapshot work unchanged. Drives two concurrent FSMs (movement + action) using
// the same selection rules as PlayerCharacter: precondition+priority scan,
// incumbent gets its ActivePriority as a tiebreak. See
// Plans/ENEMY_CAPABILITY_FRAMEWORK.md.
//
// Concrete enemies subclass and supply their own movement-state and
// action-state lists in the ctor (analogous to how PlayerCharacter builds its
// registries). The lists' index order is the snapshot identity, so don't
// reorder once instances exist in saves/replays.
public abstract class EnemyEntity : Entity
{
    private readonly List<EnemyMovementState> _movement;
    private readonly List<EnemyActionState>   _actions;
    // Swappable brain. Each frame produces an EnemyInput; movement and action
    // states read that bag rather than re-interpreting the world themselves.
    // Stateless (config-only), so a single instance can be shared across all
    // entities sharing a blueprint and snapshot Rehydrate just reattaches the
    // same one.
    private readonly EnemyController _controller;

    private int _currentMovement = 0;     // movement always has a fallback (index 0)
    private int _currentAction   = -1;    // -1 == no action (player's NullAction equivalent)
    private EnemyMovementVars _moveVars;
    private EnemyActionVars   _actionVars;

    private int _facing = 1;
    private int _frame;

    // Read by EnemyMovementState.CheckPreConditions to detect "an attack is mid-flight"
    // — the only cross-FSM channel, mirroring MovementModifiers from player code.
    public bool IsActionCommitted => _currentAction >= 0 && _actionVars.Committed;

    // True while the currently-selected movement state is a stagger interrupt.
    // Action FSM checks this so a recovering brute can't immediately swing or
    // lunge through its own hitstun.
    public bool IsStaggered => _currentMovement >= 0 && _movement[_currentMovement] is EnemyStaggerState;

    public int Facing => _facing;

    protected EnemyEntity(PhysicsBody body, float health,
                          List<EnemyMovementState> movement,
                          List<EnemyActionState>   actions,
                          EnemyController          controller = null)
        : base(body, health)
    {
        if (movement == null || movement.Count == 0)
            throw new ArgumentException("EnemyEntity requires at least one movement state (the fallback).", nameof(movement));
        _movement   = movement;
        _actions    = actions ?? new List<EnemyActionState>();
        _controller = controller ?? EnemyController.Default;
        Faction     = Faction.Enemy;
    }

    // Knockback playout. Base.OnHit applies impulse to Body.Velocity, but
    // without this hook the very next frame's movement-FSM Update (Chase /
    // Jump / etc.) overwrites Velocity.X and erases the launch. Force-
    // transitioning into a stagger state — if the concrete enemy registered
    // one — gives the impulse a window to actually play out, mirroring how
    // StalkerEnemy.OnHit kicks itself into AIState.Stagger.
    public override Vector2 OnHit(in Hitbox hit, in Hurtbox myHurtbox)
    {
        var delivered = base.OnHit(hit, myHurtbox);
        if (!IsDead) TriggerStagger();
        return delivered;
    }

    // Force-transition the movement FSM into a stagger state (if registered).
    // Manual transition — we bypass the precondition scan because the hit IS
    // the trigger and stagger's CheckPreConditions deliberately returns false.
    // No-op for enemies that don't register a stagger state.
    //
    // OnHit fires from CombatSystem.Apply, after all entity Updates and outside
    // the per-frame context we built in Update — so we synthesize a minimal
    // EnemyContext carrying just Self + Facing. Movement-state Enter/Exit
    // implementations should read at most those two; current MVP states only
    // touch Self.Body and Facing, so this constraint is trivially satisfied.
    private void TriggerStagger()
    {
        var ctx = new EnemyContext { Self = this, Facing = _facing };
        for (int i = 0; i < _movement.Count; i++)
        {
            if (_movement[i] is not EnemyStaggerState) continue;
            if (_currentMovement != i)
            {
                _movement[_currentMovement].Exit(ctx, ref _moveVars);
                _currentMovement = i;
                _moveVars        = default;
                _movement[_currentMovement].Enter(ctx, ref _moveVars);
            }
            else
            {
                // Already staggering — restart the timer so successive hits
                // keep the interrupt fresh instead of letting it expire mid-combo.
                _moveVars.TimeInState = 0f;
            }
            return;
        }
    }

    public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
    {
        if (IsDead) return;
        _frame++;

        // Pre-input context — Facing/Input intentionally unset; the controller
        // doesn't read them (it produces them).
        var ctx = new EnemyContext {
            Dt       = dt,
            Frame    = _frame,
            Self     = this,
            Player   = player,
            Hitboxes = hitboxes,
            Spawner  = spawner,
        };

        // Ask the brain what we want to do this frame.
        var input = _controller.Decide(in ctx);

        // Derive facing from the controller's aim — only while NOT mid-action
        // (actions lock facing at Enter, mirroring how the player's slash
        // captures direction at Enter).
        if (_currentAction < 0)
        {
            float dx = input.AimWorld.X - Body.Position.X;
            if (MathF.Abs(dx) > 1f) _facing = dx >= 0 ? 1 : -1;
        }

        // Finalize the per-frame ctx with brain output + derived facing.
        // Movement and action states read these and should not re-derive them
        // by inspecting Player/Self positions themselves.
        ctx.Input  = input;
        ctx.Facing = _facing;

        // Order mirrors PlayerCharacter.Update:
        //   1. Action FSM selection — Enter/Exit transitions so Committed flag is
        //      visible to the movement selection scan this same frame.
        //   2. Movement FSM selection.
        //   3. Movement.Update — writes the baseline velocity for the frame.
        //   4. Action.Update — runs AFTER movement so an action that wants to
        //      override velocity (e.g. a lunge dash) wins, in the same spirit as
        //      the player's ApplyActionForces hook.
        SelectAction(in ctx);
        SelectMovement(in ctx);
        _movement[_currentMovement].Update(ctx, ref _moveVars);
        if (_currentAction >= 0)
            _actions[_currentAction].Update(ctx, ref _actionVars);
    }

    // Action FSM selection: -1 is the resting state (no action active). The
    // current entry is dropped when CheckConditions returns false; a new entry
    // is picked via the highest-passive-priority candidate whose precondition
    // passes, gated by the incumbent's active priority. Does NOT call Update —
    // the caller runs that after movement so velocity overrides land correctly.
    //
    // Stagger short-circuits: while the movement FSM holds a stagger interrupt
    // we cancel any in-flight action and skip the precondition scan so a hit
    // brute can't continue swinging through its own hitstun.
    private void SelectAction(in EnemyContext ctx)
    {
        if (IsStaggered)
        {
            if (_currentAction >= 0)
            {
                _actions[_currentAction].Exit(ctx, ref _actionVars);
                _currentAction = -1;
                _actionVars    = default;
            }
            return;
        }

        if (_currentAction >= 0 &&
            !_actions[_currentAction].CheckConditions(ctx, ref _actionVars))
        {
            _actions[_currentAction].Exit(ctx, ref _actionVars);
            _currentAction = -1;
            _actionVars    = default;
        }

        int bestIdx = -1;
        int bestPri = int.MinValue;
        for (int i = 0; i < _actions.Count; i++)
        {
            if (i == _currentAction) continue;
            if (!_actions[i].CheckPreConditions(in ctx)) continue;
            if (_actions[i].PassivePriority > bestPri)
            {
                bestPri = _actions[i].PassivePriority;
                bestIdx = i;
            }
        }

        int incumbentActive = _currentAction >= 0 ? _actions[_currentAction].ActivePriority : int.MinValue;
        if (bestIdx >= 0 && bestPri > incumbentActive)
        {
            if (_currentAction >= 0) _actions[_currentAction].Exit(ctx, ref _actionVars);
            _currentAction = bestIdx;
            _actionVars    = default;
            _actions[_currentAction].Enter(ctx, ref _actionVars);
        }
    }

    // Movement FSM selection: always has SOMETHING selected (index 0 is the
    // fallback — typically EnemyIdleState). When the current state's
    // CheckConditions drops, fall through to index 0 immediately. No Update.
    private void SelectMovement(in EnemyContext ctx)
    {
        if (!_movement[_currentMovement].CheckConditions(ctx, ref _moveVars))
        {
            _movement[_currentMovement].Exit(ctx, ref _moveVars);
            _currentMovement = 0;
            _moveVars        = default;
            _movement[_currentMovement].Enter(ctx, ref _moveVars);
        }

        int bestIdx = -1;
        int bestPri = int.MinValue;
        for (int i = 0; i < _movement.Count; i++)
        {
            if (i == _currentMovement) continue;
            if (!_movement[i].CheckPreConditions(in ctx)) continue;
            if (_movement[i].PassivePriority > bestPri)
            {
                bestPri = _movement[i].PassivePriority;
                bestIdx = i;
            }
        }

        if (bestIdx >= 0 && bestPri > _movement[_currentMovement].ActivePriority)
        {
            _movement[_currentMovement].Exit(ctx, ref _moveVars);
            _currentMovement = bestIdx;
            _moveVars        = default;
            _movement[_currentMovement].Enter(ctx, ref _moveVars);
        }
    }

    // Overlay rendering — the FSM-side analogue of player.CurrentAction.Draw in
    // Game1. Movement first, action on top (telegraphs are read most easily when
    // they sit above the body+ground state).
    public void DrawOverlay(SpriteBatch sb, Texture2D pixel)
    {
        _movement[_currentMovement].Draw(sb, pixel, Body, in _moveVars);
        if (_currentAction >= 0)
            _actions[_currentAction].Draw(sb, pixel, Body, in _actionVars);
    }

    // ── Snapshot/restore ────────────────────────────────────────────────────
    // Per the MVP plan we reuse a few shared EntityData fields (AIState,
    // StateTime, Facing, HitId) and the EnemyEntity action-FSM ones (ActionIdx,
    // ActionTime, LockedFacing). Per-action durations are recomputed from the live
    // flyweight on restore by replaying Enter into a scratch vars; this keeps
    // the snapshot footprint flat without coupling the component to
    // action-specific knobs.
    protected override void WriteState(ref EntityData s)
    {
        base.WriteState(ref s);
        s.AIState      = _currentMovement;
        s.StateTime    = _moveVars.TimeInState;
        s.Facing       = _facing;
        s.HitId        = _actionVars.HitId;
        s.ActionIdx    = _currentAction;
        s.ActionTime   = _actionVars.TimeInState;
        s.LockedFacing = _actionVars.LockedFacing;
    }

    protected override void ReadState(in EntityData s)
    {
        base.ReadState(in s);
        _currentMovement       = s.AIState;
        _moveVars              = default;
        _moveVars.TimeInState  = s.StateTime;
        _facing                = s.Facing;
        _currentAction         = s.ActionIdx;
        _actionVars            = default;
        _actionVars.HitId        = s.HitId;
        _actionVars.TimeInState  = s.ActionTime;
        _actionVars.LockedFacing = s.LockedFacing;
        _actionVars.Committed    = _currentAction >= 0;

        // Re-derive durations from the flyweight so Draw / phase math reads the
        // same Windup/Active/Recovery values the live action would have stamped
        // at Enter. Concrete actions implement PopulateDurations to write into
        // the vars; default = no-op for actions that don't care.
        if (_currentAction >= 0)
            _actions[_currentAction].PopulateDurations(ref _actionVars);
    }
}
