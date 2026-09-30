using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// A planted contact the cadence solver pins this frame: a bone whose tip should stay at
// Target (world). Captured when the label appears, held until it drops (CharacterAnimator.
// RefreshContacts owns the lifecycle; the solve sees a frozen copy in SolveProblem.Contacts).
public struct ActiveContact
{
    public int     Bone;
    public Vector2 Target;
    public float   Weight;
}

// A residual block of the composite solve objective (workplan chunk 1.5 — the functional solve
// core): a PURE function of the frozen problem `p` and the forward pass's evaluation `e` at the
// candidate `x`. Emits its rows into `r`/`jac` (starting at row0) and returns the count. Both
// methods run AFTER the shared forward pass (SolveForward.Run, plus SolveForward.Velocities for
// the Jacobian), so implementations read the composed, corrected world pose in e.Pose. The
// count MUST be stable across a single Minimize call (the LM core's fixed-row contract) — every
// varying input is frozen into `p` before the solve starts. Blocks hold no state of their own:
// the same instance serves every animator, and a move driver can contribute its own block via
// FrameInputs.Constraints. `Name` is the block's printable identity (diagnostics key on it).
public interface ISolveConstraint
{
    string Name { get; }
    int Residuals(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> r);
    int Jacobian(SolveProblem p, PoseEval e, ReadOnlySpan<float> x, Span<float> jac, int stride, int row0);
}

// THE FROZEN PROBLEM of one LM solve (Plans/ANIMATION_SOLVER_PLAN §11; workplan chunk 1.5):
// everything a residual or Jacobian evaluation may read besides the candidate x. Filled once per
// solve by CharacterAnimator.FreezeProblem (contacts/targets/weights, swing targets, pins,
// surfaces, the aim, the config snapshot, the placement, the prior anchors) and then treated as
// IMMUTABLE until the solve ends — purity by convention: nothing in SolveForward, SolveObjective
// or a block writes to it. Preallocated and reused (zero per-frame allocation); the arrays
// IsCore / LambdaSmooth / BaseBlend are animator-owned and merely referenced, on the same
// convention (the animator recomputes them per frame, before any solve).
//
// The FD oracle (CharacterAnimator.MaxJacobianError) is the one deliberate exception: it shifts
// Body and the frozen targets to the origin for its evaluation and restores them.
public sealed class SolveProblem
{
    // Variable layout of x (§11.1). d = (d.x, δ≡d.y) is the solved rig-root offset from the
    // host's baseline placement, RESIDUAL-SIDE (tip + d in the geometric rows; the FK never
    // sees it, so its Jacobian columns are constants). d.x was un-deferred 2026-07-14: at a
    // planted foot's horizontal turning point ∂slipX/∂Δφ = 0, so the no-slip row is locally
    // unsatisfiable by cadence alone — d.x is the well-conditioned escape (slight fore-aft
    // body sway, which is also physically real). The §11.1 absorption trap (d.x eating ALL
    // body travel and stalling the cycle) is guarded twice: the ABSOLUTE com row √λx·d.x
    // charges sustained absorption quadratically, and the HorizOffsetLimit box hard-caps it.
    public const int IdxPhi = 0, IdxDy = 1, IdxDx = 2, IdxTheta0 = 3;

    public const int MaxPins     = 4;   // sizes the residual scratch; excess pins are dropped
    public const int MaxSurfaces = 16;  // caps the frozen face list; excess faces are dropped
                                        // (terrain extraction emits a handful + the wall plane)
    // How near an upward-facing face must be to a toe to count as SUPPORTING its plant. One
    // meaning, two users that must agree: CharacterAnimator.SnapToSupport captures the target
    // onto such a face, and NoPenetrationConstraint.SkipPair mutes its own row against the
    // same face because the contact now owns it. If they disagreed, a plant could be snapped
    // to a face that still fires a no-pen row against it, or held off a face that stays muted.
    public const float ContactSupportBand = 8f;

    public readonly Skeleton Skeleton;
    public int Vars => IdxTheta0 + Skeleton.Count;   // n: x = [Δφ, δ, d.x, Δθ_0 … Δθ_{bones−1}]

    // ── The pose source ───────────────────────────────────────────────────────────────
    public AnimationDocument Clip;        // the base clip sampled at TimeAt(Phi + Δφ)
    public float             Phi;         // entry phase (or the static solve's clip time)
    public bool              WrapPhase;   // Phi is a cycle phase (CadencePhase/IdleBob/Hold) → Phi+Δφ wraps
    public OverlayStack      Overlays;    // the frozen overlay stack composed onto the base
    public float[]           BaseBlend;   // per-bone Π(1−w) — the Δφ channel's overlay attenuation

    // ── Placement (BodyPath's one contract) ───────────────────────────────────────────
    public Vector2 Body;     // sim body position the rig root hangs off
    public int     Dir;      // facing sign (never 0)
    public float   Scale;    // rig → world scale

    // ── Geometric rows (frozen copies of the animator's per-frame lists) ─────────────
    public readonly List<ActiveContact>             Contacts = new();   // planted feet (H no-slip + V hold)
    public readonly List<(int Bone, Vector2 Target)> Swings  = new();   // planner-owned swinging feet
    public readonly List<(int Bone, Vector2 Target)> Pins    = new();   // external hard pins
    public readonly List<SolverSurface>             Surfaces = new();   // no-penetration faces
    public bool    AimActive;
    public Vector2 AimTarget;             // û*: frozen target unit vector of the action aim row
    public int     AimBoneL, AimBoneR;    // the L→R hand pair whose vector encodes the aim

    // ── Weights and prior anchors ─────────────────────────────────────────────────────
    public AnimSolverConfig Cfg;          // this frame's EFFECTIVE config (driver overrides applied)
    public float   InvCharLen;            // 1/reach: px rows → dimensionless
    public float   EaseBase;              // b: the base ease factor the com smoothness rows derive λs from
    public float   DyEmitted, DxEmitted;  // last frame's EMITTED root offset — the com smoothness anchor
    public bool[]  IsCore;                // torso bones take CorePosePrior, the rest LimbPosePrior
    public float[] LambdaSmooth;          // per-bone λs_i of the Δθ smoothness row
    public readonly float[] SmoothTarget; // per-bone t_i = wrapAngle(emitted_i − composedEntry_i)

    // ── The objective ─────────────────────────────────────────────────────────────────
    // Row order = list order (load-bearing: the LM core and the FD oracle assume a fixed row
    // order). Assembled per frame by the animator (core geometric head + driver contributions
    // + prior tail) and frozen for the frame — both the cadence and the static solve walk it.
    public readonly List<ISolveConstraint> Blocks = new();

    public SolveProblem(Skeleton rig)
    {
        Skeleton     = rig;
        SmoothTarget = new float[rig.Count];
    }

    public static float Wrap01(float x) => x - MathF.Floor(x);

    // The clip time a candidate Δφ addresses — the same time the draw samples (SampleT): a
    // cycle phase wraps (φ+Δφ may cross the seam); a one-shot's / progress clip's time is
    // already clamped by its producer and passes through. (Wrapping those too sent a one-shot
    // HELD at t = 1 back to its first frame, so the static solve corrected the wrong pose
    // there — fixed 2026-09-10, workplan chunk 1.5 loose end.)
    public float TimeAt(float t) => WrapPhase ? Wrap01(t) : t;
}

// THE FORWARD PASS'S OUTPUT (workplan chunk 1.5): the world pose and the Δφ-channel velocities
// SolveForward leaves behind for a candidate x, plus the column scratch the point-Jacobian
// primitive writes into. Caller-owned, preallocated once per animator, overwritten by every
// evaluation — a value in spirit (nothing here outlives the evaluation that filled it).
public sealed class PoseEval
{
    public readonly SkeletonPose Pose;                  // composed, Δθ-corrected pose; world buffer valid after Run
    public readonly SkeletonPose KfA, KfB, KfC, KfD;    // the sampler's keyframe-quad scratch
    public float   T;                                   // normalized clip time the pose was built at
    public Affine2 Root;                                // the rig root at T (SolveForward.RootAt)
    public readonly float[]   AngVel;                   // per-bone clip dθ/dt at T (Velocities)
    public readonly Vector2[] TransVel;                 // per-bone clip dT/dt (animated Stretch) at T
    public Vector2 RootVel;                             // ∂root/∂φ: the anchor drifting with phase
    public readonly float[] ColX, ColY, ColX2, ColY2;   // ∂p/∂x column scratch (two points for the aim row)

    public PoseEval(Skeleton rig, int maxVars)
    {
        Pose = rig.CreatePose();
        KfA = rig.CreatePose(); KfB = rig.CreatePose(); KfC = rig.CreatePose(); KfD = rig.CreatePose();
        AngVel   = new float[rig.Count];
        TransVel = new Vector2[rig.Count];
        ColX = new float[maxVars]; ColY = new float[maxVars];
        ColX2 = new float[maxVars]; ColY2 = new float[maxVars];
    }
}
