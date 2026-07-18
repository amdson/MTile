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

    // Reused scratch for terrain no-penetration half-planes (TerrainSurfaces.Extract).
    // Safe to share across characters: each sample is built and consumed by its
    // animator's Update before the next extraction overwrites it.
    private readonly SolverSurface[] _terrainScratch = new SolverSurface[8];

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
    // `dt` is RENDER time (drives screen-space feel: cursor ribbon, particles, camera);
    // `simDt` is the SIM time advanced this frame (stepsRun × FixedDt; 0 when the sim
    // didn't step, e.g. 9 of 10 frames under TimeScale 0.1) — the animators tick on it
    // so animation and physics share one timeline. Feeding the animator render-dt while
    // the body moves in sim steps made the cadence solver see stop-jump-stop body motion:
    // phase stalls + MaxPhaseStep catch-up snaps under slow-mo (manual_183352 trace).
    public void Update(Simulation sim, GameConfig config, float dt, float simDt,
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
        // animator in LOCKSTEP WITH THE SIM — only on frames the sim stepped, by the sim
        // time it advanced. One-way — the sim is unaware this happens. The rig is drawn
        // only under DebugDrawSkeleton, but the pose runs always so render effects (the
        // knife-anchored slash glow) can read animated bone positions.
        while (_secondaryAnimators.Count < sim.SecondaryPlayers.Count)
            _secondaryAnimators.Add(new CharacterAnimator(
                SkeletonExamples.Biped(), _skeletonScale, _skeletonAnims));
        if (simDt > 0f)
        {
            // Terrain no-penetration: extract nearby exposed tile faces around LAST frame's
            // pose (must run before this animator's Update) and ride them into the sample.
            int tc = TerrainSurfaces.Extract(sim.Chunks, _animator, player.Body.Position,
                                             player.Facing, _skeletonScale, _terrainScratch,
                                             out bool near);
            _animator.Update(CharacterAnimSample.From(player, simDt, _terrainScratch, tc, near, sim.Chunks));
            // Secondary players (training dummy, P2) get their own animators so
            // each rig tracks its own body, facing, and action timing.
            for (int i = 0; i < sim.SecondaryPlayers.Count; i++)
            {
                var sp = sim.SecondaryPlayers[i].Player;
                tc = TerrainSurfaces.Extract(sim.Chunks, _secondaryAnimators[i], sp.Body.Position,
                                             sp.Facing, _skeletonScale, _terrainScratch, out near);
                _secondaryAnimators[i].Update(
                    CharacterAnimSample.From(sp, simDt, _terrainScratch, tc, near, sim.Chunks));
            }
        }
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
