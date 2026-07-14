using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The per-frame cosmetic update pass: cursor ribbon, sprite↔body sync, procedural
// skeleton animators (primary + one per secondary player, grown lazily), the knife
// trail, particle stepping, the air→ground landing puff, and camera tracking. All of
// it reads sim state but never writes it. Holds the shared render systems by reference
// (Game1 also draws through them) and owns only the landing-puff edge state.
public sealed class CosmeticUpdateSystem
{
    private readonly CharacterAnimator        _animator;
    private readonly List<CharacterAnimator>  _secondaryAnimators;
    private readonly List<AnimationDocument>  _skeletonAnims;
    private readonly float                    _skeletonScale;
    private readonly Camera                   _camera;
    private readonly ParticleSystem           _particles;
    private readonly Trail                     _cursorTrail;
    private readonly AttackGlowSystem         _attackGlow;

    // Tracked frame-to-frame so the landing puff fires exactly once on the
    // air→ground transition.
    private bool _wasGroundedLastFrame;

    public CosmeticUpdateSystem(CharacterAnimator animator, List<CharacterAnimator> secondaryAnimators,
                                List<AnimationDocument> skeletonAnims, float skeletonScale,
                                Camera camera, ParticleSystem particles, Trail cursorTrail,
                                AttackGlowSystem attackGlow)
    {
        _animator           = animator;
        _secondaryAnimators = secondaryAnimators;
        _skeletonAnims      = skeletonAnims;
        _skeletonScale      = skeletonScale;
        _camera             = camera;
        _particles          = particles;
        _cursorTrail        = cursorTrail;
        _attackGlow         = attackGlow;
    }

    // Cosmetic-only systems; they read sim state but never write it. Call once per frame
    // after the sim has stepped. `localPlayer` is the camera/landing-puff target (the
    // joiner controls P2), `mouseWorldPos`/`screenCenter` come from the input pass.
    public void Update(Simulation sim, GameConfig config, float dt,
                       Vector2 mouseWorldPos, Vector2 screenCenter, PlayerCharacter localPlayer)
    {
        // Cursor ribbon in world-space from the residue of the cursor's recent motion.
        if (config.MouseTrail)
        {
            _cursorTrail.Tick(dt);
            _cursorTrail.Push(mouseWorldPos);
        }
        else _cursorTrail.Clear();

        // Sync sprites to their physics bodies + advance animations.
        var player = sim.Player;
        if (player.Sprite != null)
        {
            player.Sprite.Position = player.Body.Position;
            player.Sprite.Update(dt);
        }

        // Procedural skeleton: pull a read-only sample of the player and advance the
        // animator every frame. One-way — the sim is unaware this happens. The rig is
        // drawn only under DebugDrawSkeleton, but the pose runs always so render effects
        // (the knife-anchored slash glow) can read animated bone positions.
        // Scale the animator's dt too so easing/idle slow with the sim under TimeScale.
        _animator.Update(CharacterAnimSample.From(player, dt * config.TimeScale));
        // Secondary players (training dummy, P2) get their own animators so
        // each rig tracks its own body, facing, and action timing.
        while (_secondaryAnimators.Count < sim.SecondaryPlayers.Count)
            _secondaryAnimators.Add(new CharacterAnimator(
                SkeletonExamples.Biped(), _skeletonScale, _skeletonAnims));
        for (int i = 0; i < sim.SecondaryPlayers.Count; i++)
            _secondaryAnimators[i].Update(
                CharacterAnimSample.From(sim.SecondaryPlayers[i].Player, dt * config.TimeScale));
        _attackGlow.Update(player, dt);
        foreach (var (p, _) in sim.SecondaryPlayers)
        {
            if (p.Sprite == null) continue;
            p.Sprite.Position = p.Body.Position;
            p.Sprite.Update(dt);
        }
        foreach (var e in sim.Entities)
        {
            if (e.Sprite == null) continue;
            e.SyncSprite();
            e.Sprite.Update(dt);
        }

        // Air→ground transition: small dust puff at the local player's feet.
        bool grounded = localPlayer.IsGrounded;
        if (grounded && !_wasGroundedLastFrame)
            Effects.Puff(_particles, localPlayer.Body.Position + new Vector2(0f, PlayerCharacter.Radius * 0.8f),
                new Color(180, 160, 120));
        _wasGroundedLastFrame = grounded;

        _particles.Update(dt);

        _camera.TrackTarget(localPlayer.Body.Position, screenCenter, dt);
    }
}
