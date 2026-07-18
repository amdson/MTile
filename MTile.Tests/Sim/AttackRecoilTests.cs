using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Newton's-third-law recoil — verifies that hitboxes with RecoilScale > 0
// accumulate an opposite impulse into CombatSystem's per-HitId tally, with the
// "BreakProtected" flag suppressing the contribution from cells that broke
// (so a stab can plough through soft terrain without pogo but bounce off
// material too tough to break in one hit).
public class AttackRecoilTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 30f;

    // Helper: a 16×16 hitbox sitting on top of cell (gtx, gty), with caller-chosen
    // recoil parameters. KnockbackImpulse is +X, magnitude 1000, so recoil should be
    // approximately (-1000, 0) scaled by RecoilScale.
    private static Hitbox MakeOneCellHitbox(int gtx, int gty, int hitId,
                                            float damage, float recoilScale, bool breakProtected)
    {
        var region = new BoundingBox(
            gtx * Chunk.TileSize,       gty * Chunk.TileSize,
            (gtx + 1) * Chunk.TileSize, (gty + 1) * Chunk.TileSize);
        return new Hitbox(
            region, hitId, damage,
            knockbackImpulse: new Vector2(1000f, 0f),
            owner: Faction.Player1, source: EntityId.None,
            recoilScale: recoilScale,
            recoilBreakProtected: breakProtected);
    }

    // A non-breaking tile produces recoil ≈ -KnockbackImpulse * RecoilScale.
    [Fact]
    public void StoneCell_ContributesFullRecoil()
    {
        var chunks = SimTerrain.FromAscii("X");  // single Stone cell at (0,0); Type defaults to Stone
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        // Damage 0.25 (= a stab frame) — well below Stone's MaxHP (2.0 × TileMaxHP).
        // Tile survives, contact registered, full recoil delivered.
        hitboxes.Publish(MakeOneCellHitbox(0, 0, hitId: 1,
            damage: 0.25f, recoilScale: 1.0f, breakProtected: true));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        var recoil = combat.PeekRecoil(1);
        Assert.Equal(-1000f, recoil.X, precision: 3);
        Assert.Equal(    0f, recoil.Y, precision: 3);
    }

    // A breakable cell delivers no recoil when BreakProtected is set — the stab
    // ploughs through. (RecoilScale > 0, but the tile broke this frame.)
    [Fact]
    public void BreakableCell_BreakProtected_NoRecoil()
    {
        // One sand cell. Sand MaxHP = 0.5 × TileMaxHP; we hit with damage = 1.0 (above max)
        // so DamageCell returns true (broke this frame).
        var chunks = SimTerrain.FromAscii("X");
        // Override the cell's material to Sand. Tile.IsSolid setter doesn't touch Type.
        chunks.TryGet(new Point(0, 0), out var chunk);
        chunk.Tiles[0, 0].Type = TileType.Sand;

        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        hitboxes.Publish(MakeOneCellHitbox(0, 0, hitId: 7,
            damage: 1.0f, recoilScale: 1.0f, breakProtected: true));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        var recoil = combat.PeekRecoil(7);
        Assert.Equal(Vector2.Zero, recoil);
    }

    // Same setup, but BreakProtected is false: the cell that breaks still contributes
    // its share of recoil. (Confirms BreakProtected is the actual gate.)
    [Fact]
    public void BreakableCell_NotBreakProtected_StillContributesRecoil()
    {
        var chunks = SimTerrain.FromAscii("X");
        chunks.TryGet(new Point(0, 0), out var chunk);
        chunk.Tiles[0, 0].Type = TileType.Sand;

        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        hitboxes.Publish(MakeOneCellHitbox(0, 0, hitId: 9,
            damage: 1.0f, recoilScale: 1.0f, breakProtected: false));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        var recoil = combat.PeekRecoil(9);
        Assert.Equal(-1000f, recoil.X, precision: 3);
    }

    // Recoil sums across multiple overlapping cells. A hitbox spanning two stone
    // cells delivers 2× recoil to the attacker.
    [Fact]
    public void TwoStoneCells_RecoilSums()
    {
        var chunks = SimTerrain.FromAscii("XX");  // two adjacent Stone cells

        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        // Region covers both cells horizontally.
        var region = new BoundingBox(0f, 0f, 32f, 16f);
        hitboxes.Publish(new Hitbox(
            region, hitId: 2, damage: 0.25f,
            knockbackImpulse: new Vector2(1000f, 0f),
            owner: Faction.Player1, source: EntityId.None,
            recoilScale: 1.0f, recoilBreakProtected: true));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        var recoil = combat.PeekRecoil(2);
        // Two cells survived → recoil = 2 × -1000 = -2000 on X.
        Assert.Equal(-2000f, recoil.X, precision: 3);
    }

    // An empty cell inside the hitbox contributes no recoil — there's nothing to
    // push off of. Guards the "empty-cell" branch in CombatSystem's recoil gate.
    [Fact]
    public void EmptyCells_DoNotContributeRecoil()
    {
        // Only one solid cell at (0,0); region covers (0,0) and (1,0) (which is empty).
        var chunks = SimTerrain.FromAscii("XO");

        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        var region = new BoundingBox(0f, 0f, 32f, 16f);
        hitboxes.Publish(new Hitbox(
            region, hitId: 3, damage: 0.25f,
            knockbackImpulse: new Vector2(1000f, 0f),
            owner: Faction.Player1, source: EntityId.None,
            recoilScale: 1.0f, recoilBreakProtected: true));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        var recoil = combat.PeekRecoil(3);
        // Only the solid stone cell contributes ⇒ exactly one cell's worth of recoil.
        Assert.Equal(-1000f, recoil.X, precision: 3);
    }

    // RecoilScale = 0 (the default for every existing hitbox) ⇒ no recoil at all.
    // Guards against unintended recoil contributions from un-opted-in attacks.
    [Fact]
    public void DefaultRecoilScale_NoRecoilAccumulated()
    {
        var chunks = SimTerrain.FromAscii("X");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        // Default Hitbox constructor: recoilScale defaults to 0.
        var region = new BoundingBox(0f, 0f, 16f, 16f);
        hitboxes.Publish(new Hitbox(
            region, hitId: 4, damage: 0.25f,
            knockbackImpulse: new Vector2(1000f, 0f),
            owner: Faction.Player1, source: EntityId.None));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        Assert.Equal(Vector2.Zero, combat.PeekRecoil(4));
    }

    // Tally is cleared at the start of each Apply — a one-frame inbox, not a
    // running total. (Important: actions read in frame N+1, but a second
    // Apply with no hits returns zero, not the previous frame's recoil.)
    [Fact]
    public void RecoilTally_ClearedBetweenApplies()
    {
        var chunks = SimTerrain.FromAscii("X");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        hitboxes.Publish(MakeOneCellHitbox(0, 0, hitId: 5,
            damage: 0.25f, recoilScale: 1.0f, breakProtected: true));
        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);
        Assert.Equal(-1000f, combat.PeekRecoil(5).X, precision: 3);

        // Second Apply with empty HitboxWorld — tally should be cleared.
        hitboxes.Clear();
        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);
        Assert.Equal(Vector2.Zero, combat.PeekRecoil(5));
    }

    // ──────────────── Collision-mode tile recoil (per-surface) ────────────────

    private static Hitbox MakeCollisionHitbox(BoundingBox region, int hitId, float damage,
                                              Vector2 dir, Vector2 strikeVel, float recoilScale,
                                              bool breakProtected = true)
        => new Hitbox(region, hitId, damage,
                      knockbackImpulse: dir * 1000f,   // direction hint; unused for momentum
                      owner: Faction.Player1, source: EntityId.None,
                      recoilScale: recoilScale, recoilBreakProtected: breakProtected,
                      mode: KnockbackMode.Collision, strikeDir: dir,
                      strikeVelocity: strikeVel, strikeMass: 2.5f);

    // A stone surface reflects the approach: recoil = -(1+e_stone)·u·scale, NOT the
    // authored knockback vector (which stays a direction hint in collision mode).
    [Fact]
    public void CollisionMode_StoneCell_ReflectsApproachSpeed()
    {
        var chunks = SimTerrain.FromAscii("X");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        var region = new BoundingBox(0f, 0f, 16f, 16f);
        hitboxes.Publish(MakeCollisionHitbox(region, hitId: 11, damage: 0.25f,
            dir: new Vector2(1f, 0f), strikeVel: new Vector2(400f, 0f), recoilScale: 0.5f));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        // Stone restitution defaults to 0.7 ⇒ -(1.7 · 400 · 0.5) = -340.
        Assert.Equal(-340f, combat.PeekRecoil(11).X, precision: 2);
    }

    // A wall face of two cells is ONE surface — collision recoil must not double
    // with contact area (unlike the legacy per-cell impulse accumulation).
    [Fact]
    public void CollisionMode_TwoStoneCells_OneSurfaceOneBounce()
    {
        var chunks = SimTerrain.FromAscii("XX");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        var region = new BoundingBox(0f, 0f, 32f, 16f);
        hitboxes.Publish(MakeCollisionHitbox(region, hitId: 12, damage: 0.25f,
            dir: new Vector2(1f, 0f), strikeVel: new Vector2(400f, 0f), recoilScale: 0.5f));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        Assert.Equal(-340f, combat.PeekRecoil(12).X, precision: 2);   // same as one cell
    }

    // A multi-frame hitbox (same HitId re-published every frame, like the stab's
    // active window) bounces ONCE per attack — the swing speed baked into
    // StrikeVelocity would otherwise re-fire the floor every frame.
    [Fact]
    public void CollisionMode_MultiFrameOverlap_BouncesOncePerAttack()
    {
        var chunks = SimTerrain.FromAscii("X");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        var region = new BoundingBox(0f, 0f, 16f, 16f);
        Hitbox Box() => MakeCollisionHitbox(region, hitId: 14, damage: 0.01f,
            dir: new Vector2(1f, 0f), strikeVel: new Vector2(400f, 0f), recoilScale: 0.5f);

        hitboxes.Publish(Box());
        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);
        Assert.Equal(-340f, combat.PeekRecoil(14).X, precision: 2);   // frame 1: bounce

        hitboxes.Clear();
        hitboxes.Publish(Box());                                       // frame 2: still overlapping
        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);
        Assert.Equal(Vector2.Zero, combat.PeekRecoil(14));             // latched — no re-fire
    }

    // The existing gates still apply in collision mode: a break-protected cell that
    // shatters this frame contributes no surface, so no bounce at all.
    [Fact]
    public void CollisionMode_BrokenCell_BreakProtected_NoRecoil()
    {
        var chunks = SimTerrain.FromAscii("X");
        chunks.TryGet(new Point(0, 0), out var chunk);
        chunk.Tiles[0, 0].Type = TileType.Sand;

        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        var region = new BoundingBox(0f, 0f, 16f, 16f);
        hitboxes.Publish(MakeCollisionHitbox(region, hitId: 13, damage: 1.0f,   // breaks sand
            dir: new Vector2(1f, 0f), strikeVel: new Vector2(400f, 0f), recoilScale: 0.5f));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        Assert.Equal(Vector2.Zero, combat.PeekRecoil(13));
    }

    // ──────────────── Integration: StabAction pogo through SimRunner.RunMulti ──

    // Drive a real stab into a Stone wall; assert the attacker's Vx flips sign
    // (forward → backward), confirming the collision-mode wiring end-to-end:
    // StabAction publishes a Collision hitbox → CombatSystem resolves one bounce
    // off the stone surface (HitResolver.TileRecoil, floored at MinRecoilSpeed)
    // → the attacker applies it via PeekRecoil in ApplyActionForces.
    [Fact]
    public void StabIntoStoneWall_PlayerPogosBackward()
    {
        // Flat ground, with a 3-tile-tall stone wall just to the right of the
        // attacker. Stab Reach ≈ 55 px from body center; player at x=70 reaches
        // to ~125, so a wall at col 7 (x=112-128) is well inside the active box.
        //
        // Row layout:
        //   Rows 0-1: empty (above-head clearance, so the stab can publish)
        //   Row 2:    empty except wall column
        //   Row 3:    ground row (all solid)
        // Attacker stands on row 3 at col 4 (x=64+8=72 ≈ 70 close enough).
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOXOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        var attackerStart = new Vector2(70f, 20f);
        // Mouse swipe right: press at x=120, release at x=180 (>12 px swipe,
        // >6 frame hold ⇒ Stab intent).
        var pressMouse   = new Vector2(120f, 28f);
        var releaseMouse = new Vector2(180f, 28f);

        var attackerScript = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = pressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = pressMouse })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = releaseMouse })
            .Forever   (new PlayerInput { MouseWorldPosition = releaseMouse });

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 40,
            Dt      = Dt,
            Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attackerScript },
            },
        };

        float minVxObserved = float.MaxValue;
        SimRunner.RunMulti(cfg,
            onFrame: (f, ps) =>
            {
                float vx = ps[0].Body.Velocity.X;
                if (vx < minVxObserved) minVxObserved = vx;
            });

        // The stab's LungeSpeed (90 px/s) drives Vx positive during the active
        // window. The stone bounce is ≥ MinRecoilSpeed (380 px/s), easily
        // flipping Vx negative. Asserting < 0 (rather than a specific magnitude)
        // keeps the test robust to tuning RecoilScale / restitution.
        output.WriteLine($"min Vx observed = {minVxObserved}");
        Assert.True(minVxObserved < 0f,
            $"Expected stab into stone wall to recoil player backward (Vx < 0); observed min Vx = {minVxObserved}");
    }

    // Same stab geometry but the wall is Sand instead of Stone. Stab damage
    // (0.25 × Boost per frame, accumulating across active frames) breaks sand
    // in 2 frames; with BreakProtected on the primary, broken cells don't
    // contribute recoil. Player should NOT recoil backward.
    [Fact]
    public void StabIntoSandWall_NoBackwardPogo()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOXOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);
        // Reach into the wall cell at (col=7, row=2) and flip it to Sand.
        terrain.TryGet(new Point(0, 0), out var chunk);
        chunk.Tiles[7, 2].Type = TileType.Sand;

        var attackerStart = new Vector2(70f, 20f);
        var pressMouse   = new Vector2(120f, 28f);
        var releaseMouse = new Vector2(180f, 28f);

        var attackerScript = new InputScript()
            .For   (15, new PlayerInput { MouseWorldPosition = pressMouse })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = pressMouse })
            .For   ( 8, new PlayerInput { LeftClick = true, MouseWorldPosition = releaseMouse })
            .Forever   (new PlayerInput { MouseWorldPosition = releaseMouse });

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 40,
            Dt      = Dt,
            Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attackerScript },
            },
        };

        float minVxObserved = float.MaxValue;
        SimRunner.RunMulti(cfg,
            onFrame: (f, ps) =>
            {
                float vx = ps[0].Body.Velocity.X;
                if (vx < minVxObserved) minVxObserved = vx;
            });

        output.WriteLine($"min Vx observed (sand) = {minVxObserved}");
        // Sand breaks in ≤2 frames; ground friction can briefly drag Vx to ≈0
        // during recovery, so the bound is "didn't bounce backward meaningfully"
        // rather than "stayed positive every frame". -20 px/s gives slack for
        // residual jitter while still ruling out the pogo (which goes to -50+).
        Assert.True(minVxObserved > -20f,
            $"Expected stab into sand to plough through (no backward pogo); observed min Vx = {minVxObserved}");
    }
}
