using System;
using Microsoft.Xna.Framework;

namespace MTile;

// The attack glow cluster: feeds a knife-anchored Trail from the animated blade
// bone and renders the glowing slash/stab effect — either through the reaction–
// diffusion GlowTrailField (primary player) or the lightweight GlowRenderer streak
// (secondary players, and everyone when the field is off). Render-only: reads the
// live animator pose and sim action state, writes nothing back to the sim.
public sealed class AttackGlowSystem
{
    private const string KnifeBone          = "knife";
    private const float   GlowFieldIntensity = 0.9f;

    private readonly CharacterAnimator _animator;
    private readonly GlowRenderer      _glow;
    private readonly GlowTrailField    _glowField;
    private readonly float             _skeletonScale;

    private Trail   _knifeTrail;
    private bool    _knifeTrailActive;   // was an attack feeding the trail last frame?
    // Previous-frame knife world position + whether last frame stamped, so the field
    // can stamp the swept SEGMENT (prev→cur) rather than isolated dabs.
    private Vector2 _knifeFieldPrev;
    private bool    _knifeFieldActive;

    public AttackGlowSystem(CharacterAnimator animator, GlowRenderer glow,
                            GlowTrailField glowField, float skeletonScale)
    {
        _animator      = animator;
        _glow          = glow;
        _glowField     = glowField;
        _skeletonScale = skeletonScale;
    }

    // Feed the primary player's knife-anchored attack trail. While an authored slash or
    // stab overlay is eased in (its clip is playing), push the animated knife bone's world
    // position so the glow sweeps with the blade along the path the animation describes;
    // otherwise let the trail age out. Attacks without an authored clip never raise
    // ActionWeight, so they fall back to the hitbox-driven trail in RenderActionGlow.
    public void Update(PlayerCharacter player, float dt)
    {
        _knifeTrail ??= new Trail(24, 10.0f);
        _knifeTrail.Tick(dt);
        if (TryAttackTipPos(player, out var tip))
        {
            // On the first frame of a new attack, drop any leftover samples from the
            // previous swing — otherwise the ribbon's first frame stretches from this
            // move's start point back to wherever the knife last was. Cleared, the trail
            // grows out from the start point as the move progresses.
            if (!_knifeTrailActive) _knifeTrail.Clear();
            _knifeTrail.Push(tip);
            _knifeTrailActive = true;
        }
        else _knifeTrailActive = false;
    }

    // The glowing-shape pass (world space): the slash apex renders as a glowing triangle +
    // trail here, since the glow renderers need their own pass outside the SpriteBatch.
    public void Draw(Matrix cam, Simulation sim, GameConfig config, float fdt)
    {
        var player = sim.Player;
        if (config.GlowTrailField)
        {
            // Primary player: route the attack glow through the reaction–diffusion
            // accumulation buffer. Always advance the field (so a finished slash keeps
            // decaying/blurring into a lingering world-anchored mark); stamp the swept
            // knife segment while an attack clip is playing.
            _glowField.BeginFrame(cam, fdt);
            if (TryAttackTipPos(player, out var tipNow))
            {
                Vector2 from = _knifeFieldActive ? _knifeFieldPrev : tipNow;
                _glowField.StampSweep(from, tipNow,
                                      AttackGlowColor(player.CurrentAction, player.CurrentActionVars));
                _knifeFieldPrev = tipNow;
                _knifeFieldActive = true;
            }
            else _knifeFieldActive = false;
            _glowField.Composite(GlowFieldIntensity);

            // Secondary players keep the lightweight primitive glow (no per-player field).
            foreach (var (p, _) in sim.SecondaryPlayers)
                RenderActionGlow(cam, p.CurrentAction, p.CurrentActionVars);
        }
        else
        {
            RenderActionGlow(cam, player.CurrentAction, player.CurrentActionVars, _knifeTrail);
            foreach (var (p, _) in sim.SecondaryPlayers)
                RenderActionGlow(cam, p.CurrentAction, p.CurrentActionVars);
        }
    }

    // The world position the attack glow chases this frame, or false when no attack clip
    // is easing in. Render-only: reads sim action state + the live pose, writes nothing.
    //   • Stab: the SIM thrust tip (body + StabDir · TipExt) — the SAME point the hurtbox
    //     now extends to — so the glowing sphere leads the spear out PAST the hand. The
    //     knife bone only travels a fraction of the reach, so welding to it (as a slash
    //     does) left the glow stalling short; tracking the tip shoots it to full reach in
    //     lock-step with the dig. Clamped to ≥0 so it never dips behind the body during
    //     the wind-up draw.
    //   • Slash: the animated knife bone, read from the LIVE pose so the streak follows
    //     the authored arc (upper body stiffens during the overlay, so the live rig keeps
    //     most of the sweep). Same rig root the Draw path uses (com-anchored sole drop).
    private bool TryAttackTipPos(PlayerCharacter player, out Vector2 tip)
    {
        tip = default;
        if (!_animator.OverlayActive) return false;

        if (player.CurrentAction is StabAction)
        {
            var v = player.CurrentActionVars;
            if (v.StabDir.LengthSquared() < 1e-6f) return false;
            tip = player.Body.Position + v.StabDir * MathF.Max(v.TipExt, 0f);
            return true;
        }
        if (player.CurrentAction is SlashLikeAction)
            return _animator.TryBoneOrigin(KnifeBone, RigRoot(player, _animator, _skeletonScale),
                                           player.Facing, out tip, fromOverlay: false);
        return false;
    }

    // World position to place a rig's root so the drawn pose lines up with the player's
    // body. Preferred: anchor the clip's bundled center-of-mass reference point (its
    // "com" addition) onto the physics body — the polygon centroid IS the real COM — so
    // the pose's feet land where authored relative to it (a run's flight tuck genuinely
    // leaves the ground). Clips that don't author a COM fall back to the legacy rule:
    // drop the rig until the current pose's lowest point rests on the ground line.
    public static Vector2 RigRoot(PlayerCharacter player, CharacterAnimator anim, float scale)
    {
        int dir = player.Facing == 0 ? 1 : player.Facing;
        if (anim.TryComReference(out var com))
            // The com anchor is the baseline; the solver adds its solved root offset on top —
            // VerticalOffset δ (the body's bob that keeps the planted foot grounded during
            // stance, back to baseline in flight) and HorizontalOffset d.x (the slight fore-aft
            // sway that absorbs no-slip at a planted foot's horizontal turning point). Both are
            // 0 on frames with no solve.
            return new Vector2(player.Body.Position.X - dir * com.X * scale + anim.HorizontalOffset,
                               player.Body.Position.Y -       com.Y * scale + anim.VerticalOffset);

        float groundY = player.Body.Position.Y + 2f * PlayerCharacter.Radius;
        return new Vector2(player.Body.Position.X, groundY - anim.CurrentSoleY() * scale);
    }

    // The glow color for an attack: per-action (slash hue, stab gold/purple by stance).
    private static Color AttackGlowColor(ActionState action, in ActionVars vars) => action switch
    {
        SlashLikeAction slash => slash.SlashGlowColor,
        StabAction      stab  => stab.StabColorFor(vars.IsGrounded),
        _                     => Color.White,
    };

    // Render the glowing-shape effect for an attack: a slash sweeps a glowing triangle, a
    // stab drives a glowing sphere, each trailing a colored aura streak. `knifeTrail`
    // (primary player only) anchors the streak to the animated knife bone so it follows
    // the authored motion; when it's empty (an attack with no authored clip) it falls back
    // to the action's own hitbox-driven trail.
    private void RenderActionGlow(Matrix cam, ActionState action, in ActionVars vars, Trail knifeTrail = null)
    {
        if (action is SlashLikeAction slash)
        {
            Trail t = (knifeTrail != null && knifeTrail.Count >= 2) ? knifeTrail : slash.SlashTrail;
            _glow.DrawTrailRibbon(cam, t, slash.SlashGlowColor, headWidth: 24f, intensity: 0.85f);
        }
        else if (action is StabAction stab) 
        {
            // Prefer the animated knife path (so the streak follows the authored thrust);
            // fall back to the sim's hitbox-driven tip when there's no clip / knife trail.
            Trail t = (knifeTrail != null && knifeTrail.Count >= 2) ? knifeTrail : stab.TipTrail;
            _glow.DrawTrailRibbon(cam, t, stab.StabColorFor(vars.IsGrounded),
                                  headWidth: 19f, intensity: 0.85f, widthTaper: 0.9f);
        }
    }
}
