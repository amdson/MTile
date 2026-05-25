using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The clips this first-draft animator can play. Selection is derived purely from
// the observed CharacterAnimSample — never pushed by the sim.
// Walk vs WalkBack distinguishes moving with vs against the facing direction
// (forward stride vs backpedal). Air is split into Jump (rising) and Fall. Vault
// covers the guided ParkourState traversal.
public enum AnimClip { Idle, Walk, WalkBack, Crouch, Jump, Fall, Vault }

// The animation-side state, deliberately separate from any character/sim state.
// The animator owns and evolves this; it is the "previous state" the animator is
// allowed to remember between frames (alongside the previous sample).
public struct CharacterAnimState
{
    public AnimClip Clip;       // currently-selected clip
    public float    ClipTime;   // seconds spent in the current clip
    public float    Phase;      // locomotion cycle phase, wrapped to [0,1)
    public float    LandSquash; // 1 on touchdown, decays to 0 — drives a landing squash
}

// Drives a skeleton from a character's observed motion. Pure pull model and
// render-only: Update() reads a CharacterAnimSample, evolves the animation state,
// builds a target pose, and eases the live pose toward it. It NEVER writes back to
// the character — movement/action stay agnostic to animation entirely.
public sealed class CharacterAnimator
{
    // --- tuning (first-draft constants; no real velocity matching yet) ---
    private const float WalkSpeedThreshold = 12f;    // px/s before Idle -> Walk
    private const float PhasePerPixel       = 0.010f; // walk cycles/sec per px/s of speed
    private const float IdleBobHz           = 0.30f;  // breathing cycles/sec
    private const float Stiffness           = 20f;    // pose-follow rate (1/sec)
    private const float WalkLean            = 0.25f;  // torso lean at full walk speed
    private const float WalkLeanRefSpeed    = 160f;   // px/s at which lean reaches max

    private readonly Skeleton     _skeleton;
    private readonly SkeletonPose _pose;    // live output, eased each frame
    private readonly SkeletonPose _target;  // target assembled this frame
    private readonly SkeletonPose _kfA, _kfB;   // scratch for animation sampling

    // Authored clips keyed by category, matched from the loaded animations' Type.
    // When a clip has an authored animation it plays that; otherwise the procedural
    // builder below is the fallback.
    private readonly Dictionary<AnimClip, AnimationDocument> _clips = new();

    private CharacterAnimState  _state;
    private CharacterAnimSample _prev;      // previous frame's sample
    private bool _hasPrev;

    // Cached bone indices (resolved once).
    private readonly int _hip, _chest, _head;
    private readonly int _armLU, _armLL, _armRU, _armRL;
    private readonly int _legLU, _legLL, _legRU, _legRL;

    public Skeleton           Skeleton => _skeleton;
    public SkeletonPose       Pose     => _pose;
    public CharacterAnimState State    => _state;

    public CharacterAnimator(Skeleton skeleton, IEnumerable<AnimationDocument> animations = null)
    {
        _skeleton = skeleton;
        _pose     = skeleton.CreatePose();
        _target   = skeleton.CreatePose();
        _kfA      = skeleton.CreatePose();
        _kfB      = skeleton.CreatePose();

        int I(string n) => skeleton.IndexOf(n);
        _hip = I("hip"); _chest = I("chest"); _head = I("head");
        _armLU = I("arm_l_upper"); _armLL = I("arm_l_lower");
        _armRU = I("arm_r_upper"); _armRL = I("arm_r_lower");
        _legLU = I("leg_l_upper"); _legLL = I("leg_l_lower");
        _legRU = I("leg_r_upper"); _legRL = I("leg_r_lower");

        // Bind each clip category to the first authored animation whose Type matches
        // the enum name (case-insensitive). Missing categories fall back to procedural.
        if (animations != null)
            foreach (var anim in animations)
                if (Enum.TryParse<AnimClip>(anim.Type, ignoreCase: true, out var clip)
                    && !_clips.ContainsKey(clip))
                    _clips[clip] = anim;
    }

    public void Update(in CharacterAnimSample s)
    {
        float dt = s.Dt;

        // 0. Use the previous frame's state: detect a touchdown (was airborne, now
        //    grounded) and arm a landing squash that decays over the next frames.
        if (_hasPrev && !_prev.Grounded && s.Grounded) _state.LandSquash = 1f;
        _state.LandSquash = MathF.Max(0f, _state.LandSquash - dt * 4f);

        // 1. Select a clip from observed state only.
        AnimClip clip = SelectClip(in s);
        if (clip != _state.Clip) { _state.Clip = clip; _state.ClipTime = 0f; }
        else                     { _state.ClipTime += dt; }

        // 2. Advance the locomotion phase. (First draft: phase rate scales with
        //    horizontal speed — enough to read as walking, not true foot-locking.)
        float speed = MathF.Abs(s.Velocity.X);
        if (clip == AnimClip.Walk || clip == AnimClip.WalkBack)
            _state.Phase = Wrap01(_state.Phase + speed * dt * PhasePerPixel);
        else if (clip == AnimClip.Idle)
            _state.Phase = Wrap01(_state.Phase + dt * IdleBobHz);

        // 3. Build the target pose for the current clip. If an authored animation is
        //    bound to this clip, sample it (driven by ClipTime, the time since the
        //    state was entered); otherwise fall back to the procedural builder.
        if (_clips.TryGetValue(clip, out var anim))
        {
            AnimationSampler.SampleAtTime(anim, _state.ClipTime, _kfA, _kfB, _target);
        }
        else
        {
            _target.SetToBind();
            switch (clip)
            {
                case AnimClip.Walk:     BuildWalk(_state.Phase, forward: true);  break;
                case AnimClip.WalkBack: BuildWalk(_state.Phase, forward: false); break;
                case AnimClip.Crouch:   BuildCrouch();                           break;
                case AnimClip.Jump:     BuildJump();                             break;
                case AnimClip.Fall:     BuildFall(in s);                         break;
                case AnimClip.Vault:    BuildVault();                            break;
                default:                BuildIdle(_state.Phase);                 break;
            }
        }

        // 3b. Directional lean for locomotion — applied on top of the base pose
        //     (authored OR procedural) so forward/backpedal read distinctly: lean
        //     into travel when walking forward, lean back when backpedaling.
        if (clip == AnimClip.Walk || clip == AnimClip.WalkBack)
        {
            float lean = (clip == AnimClip.Walk ? 1f : -1f)
                       * WalkLean * MathHelper.Clamp(speed / WalkLeanRefSpeed, 0f, 1f);
            Rot(_chest, lean);
        }

        // 3c. Landing squash on top of any clip: flatten + sink briefly on touchdown.
        if (_state.LandSquash > 0f)
        {
            float k = _state.LandSquash;
            Scale(_hip, new Vector2(0.35f * k, -0.35f * k));
            Translate(_hip, new Vector2(0f, 3f * k));
        }

        // 4. Ease the live pose toward the target (framerate-independent).
        _pose.BlendToward(_target, 1f - MathF.Exp(-Stiffness * dt));

        _prev = s;
        _hasPrev = true;
    }

    // Render the eased pose at the character's world position. `scale` blows the
    // small rig up to world size; facing flips X.
    public void Draw(DrawContext ctx, Vector2 worldPos, int facing, float scale)
        => Draw(ctx, worldPos, facing, scale, SkeletonDrawStyle.Default);

    public void Draw(DrawContext ctx, Vector2 worldPos, int facing, float scale, in SkeletonDrawStyle style)
    {
        int dir = facing == 0 ? 1 : facing;
        var root = Affine2.FromTRS(worldPos, 0f, new Vector2(dir * scale, scale));
        SkeletonRenderer.Draw(ctx, _pose, root, style);
    }

    // --- clip selection ------------------------------------------------------

    private static AnimClip SelectClip(in CharacterAnimSample s)
    {
        // Guided traversal wins over the generic ground/air clips while active.
        if (s.MovementState != null && s.MovementState.Contains("Parkour")) return AnimClip.Vault;
        if (s.MovementState != null && s.MovementState.Contains("Crouch")) return AnimClip.Crouch;
        if (!s.Grounded) return s.Velocity.Y < 0f ? AnimClip.Jump : AnimClip.Fall;
        if (MathF.Abs(s.Velocity.X) > WalkSpeedThreshold)
            // Moving with facing = forward stride; against = backpedal.
            return Math.Sign(s.Velocity.X) == s.Facing ? AnimClip.Walk : AnimClip.WalkBack;
        return AnimClip.Idle;
    }

    // --- clip pose builders (add rotations on top of the bind pose) ----------

    private void BuildWalk(float phase, bool forward)
    {
        float a = phase * MathHelper.TwoPi;
        float dir = forward ? 1f : -1f;   // reverse the stride when backpedaling

        // Upper legs scissor; lower legs add a knee bend when the leg is trailing.
        float legSwing = MathF.Sin(a) * 0.6f * dir;
        Rot(_legLU,  legSwing);
        Rot(_legRU, -legSwing);
        Rot(_legLL, MathF.Max(0f, -MathF.Sin(a)) * 0.7f);
        Rot(_legRL, MathF.Max(0f,  MathF.Sin(a)) * 0.7f);

        // Arms counter-swing to the legs.
        Rot(_armLU, -legSwing * 0.8f);
        Rot(_armRU,  legSwing * 0.8f);

        // Torso bob: drop the hip on each footfall (twice per cycle).
        float bob = MathF.Abs(MathF.Sin(a));
        Translate(_hip, new Vector2(0f, -bob * 1.5f));
    }

    private void BuildCrouch()
    {
        // Deep knee bend, hip sunk, slight forward hunch.
        Rot(_legLU,  0.7f); Rot(_legLL, -1.3f);
        Rot(_legRU, -0.7f); Rot(_legRL,  1.3f);
        Translate(_hip, new Vector2(0f, 7f));
        Rot(_chest, 0.25f);
        Rot(_armLU, -0.3f); Rot(_armRU, 0.3f);
    }

    private void BuildJump()
    {
        // Rising: arms up, knees tucked toward the chest.
        Rot(_armLU, -1.4f); Rot(_armRU, 1.4f);
        Rot(_legLU,  0.5f); Rot(_legLL, -0.8f);
        Rot(_legRU, -0.4f); Rot(_legRL,  0.8f);
    }

    private void BuildIdle(float phase)
    {
        float a = phase * MathHelper.TwoPi;
        // Gentle breathing: scale the chest a hair (its children ride along).
        float breathe = MathF.Sin(a) * 0.04f;
        Scale(_chest, new Vector2(0f, breathe));
        // Tiny relaxed arm sway.
        Rot(_armLU,  MathF.Sin(a) * 0.05f);
        Rot(_armRU, -MathF.Sin(a) * 0.05f);
    }

    private void BuildVault()
    {
        // Pitched forward over an obstacle, knees tucked, arms reaching to plant.
        Rot(_chest, 0.6f);
        Rot(_armLU, -0.8f); Rot(_armRU, 0.8f);
        Rot(_legLU, 1.0f);  Rot(_legLL, -1.4f);
        Rot(_legRU, 1.0f);  Rot(_legRL, -1.4f);
        Translate(_hip, new Vector2(0f, 4f));
    }

    private void BuildFall(in CharacterAnimSample s)
    {
        // Falling: arms out/up bracing, legs reaching down for the ground; the brace
        // opens up the faster we fall.
        float fall = MathHelper.Clamp(s.Velocity.Y / 600f, 0f, 1f);
        Rot(_armLU, -1.4f - fall * 0.6f);
        Rot(_armRU,  1.4f + fall * 0.6f);
        Rot(_legLU,  0.25f);
        Rot(_legRU, -0.35f);
        Rot(_legLL,  0.5f);
        Rot(_legRL, -0.5f);
    }

    // --- helpers -------------------------------------------------------------

    private void Rot(int bone, float delta)       { if (bone >= 0) _target.Local[bone].Rotation    += delta; }
    private void Translate(int bone, Vector2 d)    { if (bone >= 0) _target.Local[bone].Translation += d;     }
    private void Scale(int bone, Vector2 d)        { if (bone >= 0) _target.Local[bone].Scale       += d;     }

    private static float Wrap01(float x) => x - MathF.Floor(x);
}
