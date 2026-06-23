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
        if (TryAttackKnifePos(player, out var knife))
            _knifeTrail.Push(knife);
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
            if (TryAttackKnifePos(player, out var knifeNow))
            {
                Vector2 from = _knifeFieldActive ? _knifeFieldPrev : knifeNow;
                _glowField.StampSweep(from, knifeNow,
                                      AttackGlowColor(player.CurrentAction, player.CurrentActionVars));
                _knifeFieldPrev = knifeNow;
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

    // The animated knife bone's world position during an authored slash/stab overlay,
    // or false when no attack clip is easing in. Same rig root the Draw path uses: drop
    // the rig so the current pose's sole rests on the ground line under the body center,
    // and read the LIVE pose so the glow stays welded to the rendered hand (the upper
    // body stiffens during the overlay, so the live rig keeps most of the authored sweep).
    private bool TryAttackKnifePos(PlayerCharacter player, out Vector2 knife)
    {
        knife = default;
        if (!(player.CurrentAction is SlashLikeAction or StabAction) || !_animator.OverlayActive)
            return false;
        return _animator.TryBoneOrigin(KnifeBone, RigRoot(player, _animator, _skeletonScale),
                                       player.Facing, out knife, fromOverlay: false);
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
            // The com anchor is the baseline; the solver (when active) adds VerticalOffset
            // δ — the body's bob that keeps the planted foot grounded during stance and
            // returns to the baseline in flight. δ is 0 on the golden path / flight frames.
            return new Vector2(player.Body.Position.X - dir * com.X * scale,
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
            Trail t = (knifeTrail != null && knifeTrail.Count >= 1) ? knifeTrail : slash.SlashTrail;
            if (t.Count >= 1)
                _glow.DrawTrailGlow(cam, t, slash.SlashGlowColor,
                                    auraRadius: 13f, coreSize: 5f, intensity: 0.8f);
        }
        else if (action is StabAction stab)
        {
            // Prefer the animated knife path (so the streak follows the authored thrust);
            // fall back to the sim's hitbox-driven tip when there's no clip / knife trail.
            Trail t = (knifeTrail != null && knifeTrail.Count >= 1) ? knifeTrail : stab.TipTrail;
            if (t.Count >= 1)
                _glow.DrawTrailGlow(cam, t, stab.StabColorFor(vars.IsGrounded),
                                    auraRadius: 14f, coreSize: 6f, intensity: 0.8f, core: GlowCore.Sphere);
        }
    }
}
