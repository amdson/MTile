using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Guard and shove: a defensive commitment and the control move that beats it.
//
//   Guard beats frontal attacks (a touch, a swing, a shot, a blast from the front) for
//   energy; it loses to a shove (which breaks it), to anything from behind (bodies pass
//   through each other, so a dash crosses to the open back), and to a meter that runs dry.
//   Shove is weak damage with a big push: it breaks a guard and moves a body onto a mine,
//   off a ledge or into a wall; a miss leaves its user open for half a second.

// Raise a guard toward the aim. The block itself lives in EnemyEntity.OnHit (the hit path
// is the entity's), keyed on this action's IsGuarding window and its locked facing.
//
// Held, not timed: after the 0.1 s raise the guard stays up while the brain keeps
// EnemyInput.Guard, up to Active seconds, then lowers through a 0.25 s recovery in which
// the body is open. Letting go early jumps straight to that recovery — so a guard is a
// commitment, never a free layer. Energy: GuardRaiseCost at Enter (the priced use cost),
// GuardDrain per second while up (an empty meter lowers it), and per blocked hit.
//
// Not Committed: the body keeps walking, slowed by GuardMoveScale (EnemyEntity.MoveScale).
// Facing is frozen at Enter like every action's, which is what leaves the back open.
public class EnemyGuardAction : EnemyActionState
{
    public EnemyGuardAction() : this(ActionSpec.Default(ActionKind.Guard)) {}
    public EnemyGuardAction(ActionSpec spec) { Spec = spec; }

    public override int  ActivePriority  => Spec.ActivePriority;
    public override int  PassivePriority => Spec.PassivePriority;
    public override bool RequestOnly     => true;

    public override bool CheckPreConditions(in EnemyContext ctx) => ctx.Input.Guard;

    public override bool CheckConditions(in EnemyContext ctx, ref EnemyActionVars v)
        => v.TimeInState < v.WindupDuration + v.ActiveDuration + v.RecoveryDuration;

    public override void Enter(in EnemyContext ctx, ref EnemyActionVars v)
    {
        float dx = ctx.Input.AimWorld.X - ctx.Self.Body.Position.X;
        v.LockedFacing = MathF.Abs(dx) > 1f ? (dx > 0f ? 1 : -1) : (ctx.Facing == 0 ? 1 : ctx.Facing);
        v.Committed    = false;
        PopulateDurations(ref v);
    }

    public override void PopulateDurations(ref EnemyActionVars v)
    {
        v.WindupDuration   = Spec.Windup;
        v.ActiveDuration   = Spec.Active;
        v.RecoveryDuration = Spec.Recovery;
    }

    public override bool IsGuarding(in EnemyActionVars v)
        => v.TimeInState >= v.WindupDuration && v.TimeInState < v.WindupDuration + v.ActiveDuration;

    // Drop straight into the recovery: the guard is lowering from here.
    public static void Lower(ref EnemyActionVars v)
        => v.TimeInState = MathF.Max(v.TimeInState, v.WindupDuration + v.ActiveDuration);

    public override void Update(in EnemyContext ctx, ref EnemyActionVars v)
    {
        v.TimeInState += ctx.Dt;
        if (v.TimeInState >= v.WindupDuration + v.ActiveDuration) return;     // lowering
        if (!ctx.Input.Guard) { Lower(ref v); return; }
        if (v.TimeInState < v.WindupDuration) return;                         // raising
        var self = ctx.Self;
        float drain = FighterCosts.Current.GuardDrain * ctx.Dt;
        if (self.Energy < drain) { Lower(ref v); return; }
        self.Energy -= drain;
    }

    // A bar on the guarded side: faint while raising, solid while up.
    public override void Telegraph(TelegraphList t, PhysicsBody body, in EnemyActionVars v)
    {
        float time = v.TimeInState, up = v.WindupDuration + v.ActiveDuration;
        if (time >= up) return;
        float a = time < v.WindupDuration ? 0.35f : 0.85f;
        var b = body.Bounds;
        float x = v.LockedFacing > 0 ? b.Right + 2f : b.Left - 4f;
        t.Box(x, b.Top - 2f, 2f, b.Bottom - b.Top + 4f, Color.LightSteelBlue * a);
    }
}

// A short shove: Reach px in front, light damage, big knockback, and Hitbox.GuardBreak so
// a raised guard does not stop it. The same shape as EnemyMeleeAction; the recovery is
// what a miss costs.
public class EnemyShoveAction : EnemyMeleeAction
{
    public EnemyShoveAction() : this(ActionSpec.Default(ActionKind.Shove)) {}
    public EnemyShoveAction(ActionSpec spec) : base(spec) {}

    protected override Color TelegraphColor => Color.Gold;
    protected override Color StrikeColor    => Color.Goldenrod;
    protected override bool  BreaksGuard    => true;
    protected override bool  UsesStrength   => false;
}
