using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// A Stage bundles "what to load when the game starts": which terrain file to
// read, where to drop the player, and a Populate delegate that spawns entities
// and registers per-frame tickers (moving platforms, etc.) on Game1.
//
// Stages are defined in code rather than data because their content includes
// behavior (moving platforms have update logic). game_config.json selects which
// stage to load by name; the registry below is the canonical list.
public sealed class Stage
{
    public string             Name;
    public string             TerrainConfig;   // filename inside Levels/ — TerrainLoader handles the rest
    public Vector2            PlayerSpawn;
    public Action<Simulation> Populate;
}

public static class Stages
{
    private static readonly Dictionary<string, Stage> _registry = new(StringComparer.OrdinalIgnoreCase);

    static Stages()
    {
        // ─── start ────────────────────────────────────────────────────────────
        // Original test world. Hand-authored intro chunks (start.txt, course.txt)
        // bleed into Perlin-generated terrain at the edges. Includes the moving
        // platform, ferris-wheel cluster, a few balloons / balls / floating balls,
        // and one stalker NPC for combat smoke-testing.
        Register(new Stage {
            Name          = "start",
            TerrainConfig = "terrain.json",
            PlayerSpawn   = new Vector2(0f, -200f),
            Populate      = PopulateStart,
        });

        // ─── arena ────────────────────────────────────────────────────────────
        // Bounded combat room. Walls on all four sides, flat floor, a handful of
        // stalkers and a couple of ammo balls. No moving platforms — focus is
        // pure encounter testing.
        Register(new Stage {
            Name          = "arena",
            TerrainConfig = "arena.json",
            PlayerSpawn   = new Vector2(64f, 0f),
            Populate      = PopulateArena,
        });

        // ─── plain ────────────────────────────────────────────────────────────
        // Flat open plain flanked by stepped hills on either side. Smoke-test
        // stage for the MVP EnemyEntity framework — two BruteEnemy spawns on
        // the flat section so the player can engage the new melee AI without
        // other terrain distractions. Open sky (no ceiling) so the hills read
        // as outdoor terrain rather than a bounded room.
        Register(new Stage {
            Name          = "plain",
            TerrainConfig = "plain.json",
            PlayerSpawn   = new Vector2(16f, 0f),
            Populate      = PopulatePlain,
        });

        // ─── training ─────────────────────────────────────────────────────────
        // Combat-feel testbed (COMBAT_FEEL_PLAN): a plateau over void with a
        // training dummy — a secondary PlayerCharacter parked at the center that
        // periodically slashes or stabs without moving. The dummy auto-resets to
        // its home spot when killed or displaced too far (knocked off the edge),
        // and the primary player respawns if they fall into the void.
        Register(new Stage {
            Name          = "training",
            TerrainConfig = "training.json",
            PlayerSpawn   = TrainingPlayerSpawn,
            Populate      = PopulateTraining,
        });

        // ─── corridor ─────────────────────────────────────────────────────────
        // Ambient-corrector stress harness: flat runway (floor tile y = 6) feeding
        // into a 64-tile bumpy tunnel (corridor.txt × 4 chunks, world x 256..1280):
        // 3-tile interior, floor bumps at col ≡ 1 (mod 4), ceiling bumps at
        // col ≡ 3 (mod 4). Full state machine; the fall/stand-only acceptance
        // traces apply RestrictToFallAndStand themselves (BumpyTunnelSpeedTests).
        Register(new Stage {
            Name          = "corridor",
            TerrainConfig = "corridor.json",
            PlayerSpawn   = new Vector2(0f, 60f),
            Populate      = _ => { },
        });

        // ─── gym ──────────────────────────────────────────────────────────────
        // Channel-scenario proving ground: flat floor (tile y = 8) with a
        // repeating 1-high ledge (up at col 6, down at col 12, every 16 tiles).
        // Full state machine; scenario tests restrict to fall/stand themselves.
        // Pick the channel set via movement_config.json "CorrectorScenario" —
        // hot-reloads live.
        Register(new Stage {
            Name          = "gym",
            TerrainConfig = "gym.json",
            PlayerSpawn   = new Vector2(8f, 100f),
            Populate      = _ => { },
        });

        // ─── gauntlet ─────────────────────────────────────────────────────────
        // Left-to-right combat run: eight authored chunks (world x 0..2048)
        // strung as gallery → terraces → tunnel → chamber, each section built
        // around one of the three gauntlet enemies and the last mixing all
        // three. See Levels/gauntlet.json for the terrain and PopulateGauntlet
        // below for who stands where and why.
        Register(new Stage {
            Name          = "gauntlet",
            TerrainConfig = "gauntlet.json",
            PlayerSpawn   = new Vector2(40f, 150f),
            Populate      = PopulateGauntlet,
        });

        // ─── sandbox ──────────────────────────────────────────────────────────
        // One Template enemy on empty flat ground, and nothing else. The edit
        // loop for Entities/Enemies/Types/TemplateEnemy.cs: change a number, `dotnet run
        // --project MTile.Desktop`, watch what it does. Reuses flat.json (floor
        // at world tile y = 6), so there's no terrain to read around the
        // behaviour. Turn on "DebugDrawHitboxes" in game_config.json to see the
        // damage volume the attack publishes.
        Register(new Stage {
            Name          = "sandbox",
            TerrainConfig = "flat.json",
            PlayerSpawn   = new Vector2(-80f, 40f),
            Populate      = PopulateSandbox,
        });

        // ─── hill ─────────────────────────────────────────────────────────────
        // Boss approach, as a 150-tile tower (Levels/hill.json, generated by
        // scripts/gen-tower.py). Zeus is rooted on the summit deck; the player starts
        // on the plain to the west and climbs a switchback stair, deck by deck,
        // through bird flocks holding the airspace at five of the levels.
        //
        // The climb is deliberately the birds' half of the encounter, not Zeus's:
        // ZeusController.AlertRange is 620px ≈ 39 tiles, so from the summit the statue
        // cannot even see the lower two-thirds of its own tower, and the decks break
        // its line of sight besides (every laser gates on EnemyAim.HasLineOfSight).
        // Zeus is the summit; the tower is the birds.
        Register(new Stage {
            Name          = "hill",
            TerrainConfig = "hill.json",
            PlayerSpawn   = new Vector2(-400f, 150f),
            Populate      = PopulateHill,
        });

        // ─── flat ─────────────────────────────────────────────────────────────
        // Empty, perfectly flat plain (floor at world tile y = 6, open sky, no
        // hills/chunk art, no entities or platforms). A clean testbed for the
        // locomotion/cadence work: walk back and forth and watch the skeleton's
        // foot-plant against featureless ground. Select via game_config "Stage":"flat".
        Register(new Stage {
            Name          = "flat",
            TerrainConfig = "flat.json",
            PlayerSpawn   = new Vector2(0f, -200f),
            Populate      = _ => { },
        });
    }

    public static void Register(Stage s) => _registry[s.Name] = s;

    public static Stage Get(string name) =>
        _registry.TryGetValue(name, out var s) ? s : _registry["start"];

    // ─── populate implementations ─────────────────────────────────────────────

    private static void PopulateStart(Simulation g)
    {
        // Sinusoidal vertical bobber — tests landing on a vertically-moving surface.
        const float baseX = 180f, baseY = -140f, amp = 40f, period = 3f;
        var movingRect = new MovingRectangle(new Vector2(baseX, baseY), 4f * Chunk.TileSize, Chunk.TileSize);
        g.AddPlatform(movingRect, Color.SteelBlue);
        // Ticker receives ABSOLUTE elapsed sim time (not dt) so platform motion is a
        // pure function of time — snapshot/restore just records the elapsed clock and
        // platform pose, with no hidden closure accumulator (roadmap goal 4 §H).
        g.AddTicker(t => {
            float y = baseY + amp * MathF.Sin(t * MathHelper.TwoPi / period);
            movingRect.SetPosition(new Vector2(baseX, y), Simulation.FixedDt);
        });

        // Ferris-wheel cluster — four blocks rotating 90° apart around a shared
        // center. Each is its own provider so the solver sees them independently.
        const float cx = -120f, cy = -150f, radius = 80f, fw = 2f * Chunk.TileSize, fh = Chunk.TileSize, fperiod = 6f;
        const int count = 4;
        var blocks = new MovingRectangle[count];
        for (int i = 0; i < count; i++)
        {
            float angle = i * MathHelper.TwoPi / count;
            var pos = new Vector2(cx + radius * MathF.Cos(angle), cy + radius * MathF.Sin(angle));
            blocks[i] = new MovingRectangle(pos, fw, fh);
            g.AddPlatform(blocks[i], Color.DarkOrange);
        }
        g.AddTicker(t => {
            float wheelAngle = t * MathHelper.TwoPi / fperiod;
            for (int i = 0; i < count; i++)
            {
                float angle = wheelAngle + i * MathHelper.TwoPi / count;
                var pos = new Vector2(cx + radius * MathF.Cos(angle), cy + radius * MathF.Sin(angle));
                blocks[i].SetPosition(pos, Simulation.FixedDt);
            }
        });

        g.SpawnEntity(EntityFactory.Balloon(new Vector2( 60f, -240f)));
        g.SpawnEntity(EntityFactory.Balloon(new Vector2(100f, -260f)));
        g.SpawnEntity(EntityFactory.Balloon(new Vector2(-60f, -250f)));
        g.SpawnEntity(EntityFactory.Ball   (new Vector2( 40f, -160f)));
        g.SpawnEntity(EntityFactory.Ball   (new Vector2(-40f, -160f)));

        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(140f, -208f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(100f, -216f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2( 60f, -212f)));

        g.SpawnEntity(EntityFactory.Stalker(new Vector2(180f, -200f)));
    }

    private static void PopulateArena(Simulation g)
    {
        // Floor at world y ≈ 96 (tile y=6); player spawn (64,0) is mid-arena and
        // drops cleanly to the floor. Walls at world x ≈ -192 and 320, ceiling at
        // y ≈ -160. See arena.json for the exact rules.
        const float floorTopY = 6 * Chunk.TileSize;          // arena floor surface (tile y = 6)
        const float floorY = floorTopY - Chunk.TileSize;     // body-center y when standing on the floor
        g.SpawnEntity(EntityFactory.Stalker(new Vector2(-100f, floorY)));
        g.SpawnEntity(EntityFactory.Stalker(new Vector2(  64f, floorY)));
        g.SpawnEntity(EntityFactory.Stalker(new Vector2( 220f, floorY)));

        // Ammo. Coral floating balls the player can slash into stalkers for big
        // chip damage; the impact system will dent the walls if they ricochet hard.
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(-80f, -40f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(150f, -40f)));

        // A pair of turrets perched up on the ceiling corners of the arena —
        // visibly charge, then snipe across the room. Forces the player to
        // either dodge their line of fire or close in and slash them down.
        g.SpawnEntity(EntityFactory.Turret(new Vector2(-160f, -140f)));
        g.SpawnEntity(EntityFactory.Turret(new Vector2( 280f, -140f)));
    }

    // Shared between the stage registration and the void-respawn ticker below.
    private static readonly Vector2 TrainingPlayerSpawn = new(-120f, 60f);

    private static void PopulateTraining(Simulation g)
    {
        // Plateau: solid for tile x ∈ [-18, 18], top at tile y = 6 (world y = 96).
        // Dummy home is the plateau center, body resting on the floor.
        var home = new Vector2(8f, 75f);
        const float MaxDrift = 150f;   // px from home before the dummy auto-resets
        const float VoidY    = 320f;   // below the plateau face — somebody fell off

        var (dummy, ctrl) = g.AddSecondaryPlayer(home);

        // Juggling drill: a ball that breaks on any tile contact and reappears at
        // its spawn point, five tiles above the plateau floor (floor top at tile
        // y = 6). Off to the player-spawn side so the rally has open air away
        // from the dummy's attack cycle.
        const float floorTopY = 6 * Chunk.TileSize;
        g.SpawnEntity(EntityFactory.Practice(new Vector2(-60f, floorTopY - 5 * Chunk.TileSize)));

        // Dummy attack script, driven as a pure function of the sim clock + sim
        // state (positions, facing) — deterministic and rollback-safe for the same
        // reason entity AI is: it reads only sim state and is re-derived on replay.
        // Cycle: face the player → attack (alternating slash / stab) → idle.
        //
        // NOTE: this stage drives secondary player 0's controller from the ticker.
        // Don't combine it with the two-input Step(p0, p1) netcode path — both
        // would inject into the same controller each frame.
        const int CycleFrames    = 150;  // 2.5 s at 60 fps
        const int AttackStart    = 30;   // cycle frame the button goes down
        const int StabHoldFrames = 20;   // > the 0.2 s click window ⇒ reads as a stab

        g.AddTicker(t =>
        {
            // Auto-reset: killed, knocked off the plateau, or otherwise displaced.
            if (!dummy.IsAlive || Vector2.Distance(dummy.Body.Position, home) > MaxDrift)
                dummy.Respawn(home);

            // The void has no floor — give the primary player a respawn too.
            var hero = g.Player;
            if (hero.Body.Position.Y > VoidY)
                hero.Respawn(TrainingPlayerSpawn);

            int frame = (int)MathF.Round(t / Simulation.FixedDt);
            int cf        = frame % CycleFrames;
            bool stabTurn = (frame / CycleFrames) % 2 == 1;

            Vector2 toPlayer = hero.Body.Position - dummy.Body.Position;
            int wantFacing = toPlayer.X >= 0f ? 1 : -1;
            Vector2 dir = toPlayer.LengthSquared() < 1f
                ? new Vector2(wantFacing, 0f)
                : Vector2.Normalize(toPlayer);

            var input = new PlayerInput
            {
                // Default aim: at the player. Slash direction comes from
                // mouse-relative-to-body; the stab frames override this below.
                MouseWorldPosition = dummy.Body.Position + dir * 60f,
            };

            // One frame of directional input right before the attack flips Facing
            // toward the player (ground facing tracks horizontal input). Gated on
            // a mismatch so the dummy doesn't creep — a single frame of walk accel
            // is ~1 px, and MaxDrift catches any slow accumulation.
            if (cf == AttackStart - 1 && dummy.Facing != wantFacing)
            {
                if (wantFacing > 0) input.Right = true; else input.Left = true;
            }

            // Walk back to the post between attacks. The stab's grounded lunge
            // glides the dummy ~25 px toward its target each stab turn — rather
            // than letting that accumulate into a MaxDrift reset, the dummy
            // re-centers itself outside the attack window.
            //
            // BUT not while it's recently been hit: otherwise the re-center input
            // fights the player's knockback every frame and the dummy reads as
            // "stuck in place" — it strolls back the instant a hit displaces it.
            // Hold off until it's been un-hit for ReturnDelaySeconds so the
            // knockback (and the percent-scaled launches at higher %) actually land.
            const float ReturnDelaySeconds = 0.9f;
            int returnDelayFrames = SimFrames.FromSeconds(ReturnDelaySeconds, Simulation.FixedDt);
            bool recentlyHit = dummy.Combat.HitstunActive || dummy.Combat.StunActive
                || (dummy.Combat.LastHitFrame > 0
                    && dummy.Frame - dummy.Combat.LastHitFrame < returnDelayFrames);

            bool inAttackWindow = cf >= AttackStart - 1 && cf < AttackStart + StabHoldFrames + 8;
            float dxHome = home.X - dummy.Body.Position.X;
            if (!inAttackWindow && !recentlyHit && MathF.Abs(dxHome) > 4f)
            {
                if (dxHome > 0f) input.Right = true; else input.Left = true;
            }

            if (!stabTurn)
            {
                // Slash: 1-frame click (release next frame ⇒ Click intent).
                input.LeftClick = cf == AttackStart;
            }
            else
            {
                // Stab: hold past the click window while the cursor swipes outward
                // toward the player; the release frame's default mouse position
                // still sits along `dir`, so the press→release swipe reads as a
                // clean stab gesture in that direction.
                int hold = cf - AttackStart;
                if (hold >= 0 && hold < StabHoldFrames)
                {
                    input.LeftClick = true;
                    input.MouseWorldPosition = dummy.Body.Position
                        + dir * (10f + 80f * hold / (StabHoldFrames - 1f));
                }
            }

            ctrl.InjectInput(input);
        });
    }

    private static void PopulateSandbox(Simulation g)
    {
        // flat.json's floor surface is world tile y = 6; a body rests one radius
        // above it. Spawned to the player's right, far enough out that you can
        // watch it close the distance before it starts swinging.
        const float floorTopY = 6 * Chunk.TileSize;
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Template,
                                          new Vector2(120f, floorTopY - 11f)));
    }

    // Gauntlet encounter layout. Positions are given in world pixels and derived
    // from the chunk grid: chunk cx spans world x [256·cx, 256·cx+255], and every
    // gauntlet chunk shares a floor whose top surface is tile y 12 → world y 192.
    // A body resting on a surface sits one radius above it, which is where the
    // "surface − radius" figures below come from.
    //
    // The run is paced as three teaching sections plus a test:
    //   x    0.. 512  gallery   — one Bastion, two consumable pillars
    //   x  768..1279  terraces  — three Pouncers on stacked platforms
    //   x 1280..1791  tunnel    — Latchers on a ceiling, then a pinch point
    //   x 1792..2047  chamber   — one of each, in a walled room
    private static void PopulateGauntlet(Simulation g)
    {
        const float FloorTop   = 12 * Chunk.TileSize;   // 192 — shared across all eight chunks
        const float FloorStand = FloorTop - 11f;        // body centre for a radius-11 enemy

        // ── Gallery (cx 1-2) ────────────────────────────────────────────────
        // The Bastion perches on the cx=2 platform (top surface tile y 8 → 128)
        // and fires back down the open lane. Its own MinRange (70px) is the
        // player's escape hatch: get under the perch and it cannot charge.
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Bastion, new Vector2(700f, 128f - 14f)));

        // ── Terraces (cx 3-4) ───────────────────────────────────────────────
        // Three Pouncers seeded at three heights. The top one has the longest
        // fall, so it hits hardest — the slam scales on impact speed — which
        // makes "which one is above me" the question the section asks.
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Pouncer, new Vector2( 820f,  80f - 11f)));  // cx3 upper terrace
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Pouncer, new Vector2( 990f, 128f - 11f)));  // cx3 lower terrace
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Pouncer, new Vector2(1090f,  96f - 11f)));  // cx4 terrace

        // ── Tunnel (cx 5-6) ─────────────────────────────────────────────────
        // Spawned just under the corridor ceiling (underside at tile y 7 → 112)
        // so they latch on frame one rather than falling to the floor first and
        // having to climb back up — the section only reads correctly if the
        // player meets them overhead.
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Latcher, new Vector2(1400f, 112f + 11f)));
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Latcher, new Vector2(1560f, 112f + 11f)));
        // Floor-level crawler by the pinch point, so the player is pincered
        // between an overhead lash and a ground-level one at the tightest spot.
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Latcher, new Vector2(1700f, FloorTop - 10f)));

        // ── Final chamber (cx 7) ────────────────────────────────────────────
        // One of each, with the Bastion on the elevated platform (tile y 7 →
        // 112) covering the room. The cover stub at world x ≈ 1880 is the only
        // thing between the tunnel mouth and its firing line.
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Bastion, new Vector2(1990f, 112f - 14f)));
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Pouncer, new Vector2(1860f, FloorStand)));
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Latcher, new Vector2(1930f, FloorTop - 10f)));

        // Ammo. Same idea as the arena/plain stages — weightless balls the
        // player can slash into an emplacement that otherwise ignores knockback.
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(300f, 150f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(1150f, 150f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(1850f, 150f)));
    }

    // Zeus on the tower. The statue's position has to be exact rather than
    // approximately-above-the-deck: the blueprint sets GravityScale 0 (the statue must
    // not fall into the crater its own beams dig), so nothing settles it onto the
    // surface. It is derived from the geometry below instead of written as a literal.
    //
    // Tower geometry, mirrored from scripts/gen-tower.py. These five numbers are the
    // contract between the generated terrain and everything placed on it — change them
    // in the generator and they must change here too, or the statue ends up buried in a
    // deck and the flocks spawn inside the shaft. ZeusHillTests pins the pairing.
    private const int TowerGroundTileY = 13;
    private const int TowerLevelStep   = 15;
    private const int TowerLevels      = 10;
    private const int TowerShaftHalf   = 3;
    private const int TowerDeckHalf    = 19;

    // Surface row of deck k (k = 1..TowerLevels); k = 0 is the ground plane.
    private static int TowerDeckTileY(int k) => TowerGroundTileY - k * TowerLevelStep;

    private static void PopulateHill(Simulation g)
    {
        // Summit deck. The statue sits half a body above the deck's top face, the same
        // 16px offset the old crown used.
        float summitTop = TowerDeckTileY(TowerLevels) * Chunk.TileSize;
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Zeus, new Vector2(0f, summitTop - 16f)));

        // Flocks over five of the decks. Which LANE a flock gets is derived from the
        // geometry rather than picked: deck k is cut away over its own stairwell, on
        // stair k's side (west for odd k), and that cut is the one lane with nothing
        // built in it — the opposite lane carries the next stair. Choosing the side by
        // hand is how the first pass put three birds inside a staircase.
        foreach (int k in new[] { 2, 4, 6, 8, 9 })
            SpawnBirdFlock(g, TowerDeckTileY(k), leftSide: k % 2 == 1);

        // Ammo on the plain either side of the tower, clear of the footprint — the same
        // reason the other stages carry it, and a free readout of where a beam is.
        float groundTop = TowerGroundTileY * Chunk.TileSize;
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(-480f, groundTop - 48f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2( 480f, groundTop - 48f)));
    }

    // Three birds holding the airspace over one deck's stairwell — open from the deck's
    // own level all the way up to the underside of the deck above, which is 14 tiles of
    // clear air. They are placed at the LEFT end of their lane on purpose:
    // PatrolController always opens by flying right, so the sweep runs from the spawn
    // inward and back rather than immediately stalling against the rim. The lane is 16
    // tiles — comfortably wider than the ~190px a patrol leg covers.
    private static void SpawnBirdFlock(Simulation g, int deckTileY, bool leftSide)
    {
        // Seven tiles of altitude: high enough to clear a player standing on the deck,
        // low enough to stay under the next deck's underside (14 tiles of headroom).
        float y = (deckTileY - 7) * Chunk.TileSize;
        // Inner edge of the lane, stepped one tile clear of the shaft / rim.
        float laneStart = leftSide
            ? -(TowerDeckHalf - 1) * Chunk.TileSize          // -288, sweeping in toward the shaft
            :  (TowerShaftHalf + 2) * Chunk.TileSize;        //   80, sweeping out toward the rim

        for (int i = 0; i < 3; i++)
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Bird,
                new Vector2(laneStart + i * 18f, y + (i % 2 == 0 ? 0f : -20f))));
    }

    private static void PopulatePlain(Simulation g)
    {
        // Floor sits at world tile y = 6; a standing body centers roughly one
        // tile above the floor surface. Skirmishers spawn on the
        // flat section, one to each side of the player so the engagement reads
        // immediately on stage load. Built via EnemyFactory so the blueprint
        // (radius / health / FSM lists) is the single source of truth — swap
        // EntityKind here to test other registered enemies.
        const float floorY = 6 * Chunk.TileSize - Chunk.TileSize;
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Skirmisher, new Vector2(-100f, floorY)));
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Skirmisher, new Vector2( 140f, floorY)));

        // A couple of ammo balls so the player has something to chuck at the
        // brutes — mirrors the arena setup.
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(-40f, 40f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2( 80f, 40f)));
    }
}
