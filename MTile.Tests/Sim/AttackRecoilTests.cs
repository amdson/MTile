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
    private const float Dt = 1f / 60f;

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
                                              bool breakProtected = true, float maxRecoilSpeed = 0f)
        => new Hitbox(region, hitId, damage,
                      knockbackImpulse: dir * 1000f,   // direction hint; unused for momentum
                      owner: Faction.Player1, source: EntityId.None,
                      recoilScale: recoilScale, recoilBreakProtected: breakProtected,
                      mode: KnockbackMode.Collision, strikeDir: dir,
                      strikeVelocity: strikeVel, strikeMass: 2.5f,
                      maxRecoilSpeed: maxRecoilSpeed);

    // ──────────────── Recoil ceiling (Hitbox.MaxRecoilSpeed) ────────────────

    // The same stone bounce as above (-340 uncapped) is pulled down to the ceiling,
    // direction intact. This is the stab's "a pogo is at most a jump" rule.
    [Fact]
    public void CollisionMode_MaxRecoilSpeed_CapsTheBounce()
    {
        var chunks = SimTerrain.FromAscii("X");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        var region = new BoundingBox(0f, 0f, 16f, 16f);
        hitboxes.Publish(MakeCollisionHitbox(region, hitId: 21, damage: 0.25f,
            dir: new Vector2(1f, 0f), strikeVel: new Vector2(400f, 0f), recoilScale: 0.5f,
            maxRecoilSpeed: 200f));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        Assert.Equal(-200f, combat.PeekRecoil(21).X, precision: 2);
        Assert.Equal(   0f, combat.PeekRecoil(21).Y, precision: 2);
    }

    // The cap is on the per-frame TALLY, not on each contribution: two legacy cells
    // that would sum to -2000 (see TwoStoneCells_RecoilSums) stop at the ceiling.
    [Fact]
    public void LegacyMode_MaxRecoilSpeed_CapsTheSummedTally()
    {
        var chunks = SimTerrain.FromAscii("XX");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        var region = new BoundingBox(0f, 0f, 2 * Chunk.TileSize, Chunk.TileSize);
        hitboxes.Publish(new Hitbox(
            region, 22, damage: 0.1f,
            knockbackImpulse: new Vector2(1000f, 0f),
            owner: Faction.Player1, source: EntityId.None,
            recoilScale: 1f, recoilBreakProtected: true,
            maxRecoilSpeed: 1500f));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        Assert.Equal(-1500f, combat.PeekRecoil(22).X, precision: 2);
    }

    // Default (0) is uncapped — the slashes don't set it and must not change.
    [Fact]
    public void MaxRecoilSpeed_ZeroMeansUncapped()
    {
        var chunks = SimTerrain.FromAscii("X");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();

        var region = new BoundingBox(0f, 0f, 16f, 16f);
        hitboxes.Publish(MakeCollisionHitbox(region, hitId: 23, damage: 0.25f,
            dir: new Vector2(1f, 0f), strikeVel: new Vector2(400f, 0f), recoilScale: 0.5f,
            maxRecoilSpeed: 0f));

        combat.Apply(chunks, hitboxes, hurtboxes, _ => null);

        Assert.Equal(-340f, combat.PeekRecoil(23).X, precision: 2);
    }

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

    // ──────────────── Hitstop must not reset the attack's dedupe ────────────────

    private sealed class Dummy : IHittable
    {
        public Faction Faction => Faction.Enemy;
        public EntityId Id { get; } = new EntityId(50);
        public BoundingBox Region = new(0f, 0f, 16f, 16f);
        public int Hits;
        public void PublishHurtboxes(HurtboxWorld world) => world.Publish(new Hurtbox(Region, Faction, Id));
        public Vector2 OnHit(in Hitbox hit, in Hurtbox myHurtbox) { Hits++; return hit.KnockbackImpulse; }
    }

    // An attacker in hitstop doesn't publish its hitbox. That pause used to read as
    // "attack over": the dedupe set was pruned and the same swing landed on the same
    // target again when the freeze lifted. HoldHitId keeps the set across the gap.
    [Theory]
    [InlineData(true,  1)]
    [InlineData(false, 2)]   // an unheld gap is still a real end-of-attack prune
    public void HeldHitId_SurvivesFramesWithoutBroadcast(bool hold, int expectedHits)
    {
        var chunks    = SimTerrain.FromAscii("O");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();
        var dummy     = new Dummy();
        IHittable Resolve(EntityId id) => id == dummy.Id ? dummy : null;

        void Frame(bool publish)
        {
            hitboxes.Clear(); hurtboxes.Clear();
            dummy.PublishHurtboxes(hurtboxes);
            if (publish)
                hitboxes.Publish(new Hitbox(dummy.Region, 31, 0.1f, new Vector2(100f, 0f),
                                            Faction.Player1, EntityId.None, recoilScale: 1f));
            else if (hold) combat.HoldHitId(31);
            combat.Apply(chunks, hitboxes, hurtboxes, Resolve);
        }

        Frame(publish: true);
        Frame(publish: false);
        Frame(publish: false);
        Frame(publish: true);
        Assert.Equal(expectedHits, dummy.Hits);
    }

    // A hold lasts one Apply: an attack that ends while held is pruned the frame after.
    [Fact]
    public void HeldHitId_ExpiresAfterOneApply()
    {
        var chunks    = SimTerrain.FromAscii("O");
        var hitboxes  = new HitboxWorld();
        var hurtboxes = new HurtboxWorld();
        var combat    = new CombatSystem();
        var dummy     = new Dummy();
        IHittable Resolve(EntityId id) => id == dummy.Id ? dummy : null;
        dummy.PublishHurtboxes(hurtboxes);

        hitboxes.Publish(new Hitbox(dummy.Region, 32, 0.1f, new Vector2(100f, 0f),
                                    Faction.Player1, EntityId.None));
        combat.Apply(chunks, hitboxes, hurtboxes, Resolve);
        hitboxes.Clear();
        combat.HoldHitId(32);
        combat.Apply(chunks, hitboxes, hurtboxes, Resolve);   // held
        combat.Apply(chunks, hitboxes, hurtboxes, Resolve);   // not held → pruned
        Assert.Empty(combat.CaptureDedupe());
    }

    // End to end: a stab down onto a player standing below lands ONCE, and
    // the bounce it hands the attacker stays jump-sized (StabAction.MaxRecoilRiseSpeed)
    // rather than the stacked launch a re-hit after hitstop used to produce.
    // The swipe is diagonal (~50° off vertical) on purpose: an air swipe within 30° of
    // straight down is AirHammerAction now, not a stab. The cursor is released on the
    // victim, so the stab's mouse-steer carries the thrust onto them.
    [Fact]
    public void DownStabOntoPlayer_HitsOnce_RiseCapped()
    {
        var terrain = SimTerrain.FromAscii(@"
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            OOOOOOOOOOOOOOOO
            XXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

        const float dt = 1f / 60f;
        var attackerStart = new Vector2(80f, 30f);
        var victimStart   = new Vector2(120f, 8 * Chunk.TileSize - PlayerCharacter.Radius - 1f);
        var release = victimStart;
        var press   = release - new Vector2(60f, 50f);
        var attackerScript = new InputScript()
            .For   ( 2, new PlayerInput { MouseWorldPosition = press })
            .For   ( 1, new PlayerInput { LeftClick = true, MouseWorldPosition = press })
            .For   (16, new PlayerInput { LeftClick = true, MouseWorldPosition = release })
            .Forever   (new PlayerInput { MouseWorldPosition = release });

        var cfg = new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 90,
            Dt      = dt,
            Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = attackerStart, Script = attackerScript },
                new SimPlayer { StartPosition = victimStart,   Script = new InputScript().Forever(new PlayerInput()),
                                Faction = Faction.Neutral },
            },
        };

        int hits = 0;
        float lastHp = float.NaN, minVy = float.MaxValue;
        SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            if (!float.IsNaN(lastHp) && ps[1].Health < lastHp) { hits++; output.WriteLine($"f{f}: hit, hp {ps[1].Health}"); }
            lastHp = ps[1].Health;
            minVy = MathF.Min(minVy, ps[0].Body.Velocity.Y);
            output.WriteLine($"f{f} a=({ps[0].Body.Position.X:F0},{ps[0].Body.Position.Y:F0}) v=({ps[0].Body.Velocity.X:F0},{ps[0].Body.Velocity.Y:F0}) act={ps[0].CurrentAction?.GetType().Name} victim=({ps[1].Body.Position.Y:F0}, v{ps[1].Body.Velocity.Y:F0}, hp{ps[1].Health}, hit{ps[1].Abilities.Combat.LastHitFrame})");
        });

        output.WriteLine($"hits={hits} minVy={minVy}");
        Assert.Equal(1, hits);
        Assert.True(minVy >= -300.5f, $"stab bounce launched the attacker at {minVy} px/s");
    }
}
