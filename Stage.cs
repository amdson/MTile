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
        // Boss approach, as a 150-tile spire (Levels/hill.json, generated by
        // scripts/gen-tower.py). 37 tiles across where it meets the plain, tapering three
        // rows of rise per tile of run per side until it is 11 across, then easing off and
        // dwindling the rest of the way to the single tile Zeus is rooted on. There is no
        // interior and no stair — the outside face IS the route, climbed and/or built up.
        //
        // The spire is HARDENED ROCK, and that choice is what makes the climb a climb:
        // hardened is ungrabbable and unplaceable at 10x stone HP, so the face cannot be
        // ripped into a staircase or peeled into throwable clods the way any ordinary
        // stone wall can. The terrain is the thing you move through here, not the thing
        // you fight with — the one stage where the build/throw kit is answered by the
        // material itself. The plain around it stays ordinary dirt-over-stone.
        //
        // The climb is deliberately the birds' half of the encounter, not Zeus's:
        // ZeusController.AlertRange is 620px ≈ 39 tiles, so from the summit the statue
        // cannot even see the lower two-thirds of its own spire, and below the taper the
        // shaft itself breaks line of sight to the far face (every laser gates on
        // EnemyAim.HasLineOfSight). Zeus is the summit; the tower is the birds — patrol
        // flocks you route around at five heights, and shrike pairs that dive at you in
        // the gaps between them.
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

    // Zeus on the spire. The statue's position has to be exact rather than
    // approximately-above-the-summit: the blueprint sets GravityScale 0 (the statue must
    // not fall into the crater its own beams dig), so nothing settles it onto the
    // surface. It is derived from the geometry below instead of written as a literal.
    //
    // Tower geometry, mirrored from scripts/gen-tower.py. These numbers are the contract
    // between the generated terrain and everything placed on it — change them in the
    // generator and they must change here too, or the statue ends up buried in the summit
    // and the flocks spawn inside the stonework. ZeusHillTests pins the pairing.
    private const int TowerGroundTileY = 13;
    private const int TowerHeight      = 150;
    private const int TowerTopTileY    = TowerGroundTileY - TowerHeight;  // -137, summit surface
    private const int TowerBaseHalf    = 18;   // 37 tiles across where it meets the plain
    private const int TowerSpireHalf   = 5;    // 11 tiles across where the taper hands over
    private const int TowerTaperRise   = 3;    // rows of rise per tile of run per side (3:1)
    private const int TowerSpireRise   = (TowerBaseHalf - TowerSpireHalf) * TowerTaperRise;
    private const int TowerTopRise     = TowerHeight - 1;

    // Rows above the ground surface at which a flock holds station. Evenly spaced over
    // the 150-tile climb rather than tied to any feature of the stonework — the spire has
    // no decks to hang them off, so the only thing left to be periodic in is height.
    private static readonly int[] FlockRises = { 25, 50, 75, 100, 125 };

    // Shrike stations, sitting in the gaps BETWEEN the flock heights — 50 apart, offset
    // from the flocks by ~12 tiles — so the climb alternates between the two hazards
    // instead of stacking them. The player meets a shrike alone, in clear air, before
    // ever meeting one while a flock is already on them.
    private static readonly int[] ShrikeRises = { 37, 87, 137 };

    // Half-width of the spire at world row gty — the same function the generator draws
    // with. Everything placed against the tower's FACE has to ask this rather than assume
    // a width, because the face moves as you climb.
    private static int TowerHalfAt(int gty)
    {
        int rise = (TowerGroundTileY - 1) - gty;      // 0 for the first row above ground
        if (rise < TowerSpireRise) return TowerBaseHalf - rise / TowerTaperRise;
        int span = TowerTopRise - TowerSpireRise;     // the spire eases to 0 over this
        return (TowerSpireHalf * (TowerTopRise - rise) * 2 + span) / (2 * span);
    }

    private static void PopulateHill(Simulation g)
    {
        // Summit. The statue sits half a body above the top face, the same 16px offset
        // the old crown used.
        float summitTop = TowerTopTileY * Chunk.TileSize;
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Zeus, new Vector2(0f, summitTop - 16f)));

        // Flocks at five heights up the spire, alternating sides so neither face is the
        // safe one to climb. The old tower hung them over its decks; a solid spire has no
        // decks, so they hold open air beside the face instead — which is also the only
        // place they CAN hold, since the stonework is solid all the way through.
        for (int i = 0; i < FlockRises.Length; i++)
            SpawnBirdFlock(g, TowerGroundTileY - 1 - FlockRises[i], leftSide: i % 2 == 0);

        // Shrikes between the flock heights, so the climb alternates between the hazard
        // you route around and the one that comes to you. Sides alternate the opposite
        // way to the flocks (flock 0 is west, shrike 0 is east), which keeps a station
        // and its neighbouring flock off the same face.
        for (int i = 0; i < ShrikeRises.Length; i++)
            SpawnShrikePair(g, TowerGroundTileY - 1 - ShrikeRises[i], leftSide: i % 2 == 1);

        // Ammo on the plain either side of the tower, clear of the footprint — the same
        // reason the other stages carry it, and a free readout of where a beam is.
        float groundTop = TowerGroundTileY * Chunk.TileSize;
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(-480f, groundTop - 48f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2( 480f, groundTop - 48f)));
    }

    // Three birds holding the airspace beside the spire at one height. The lane is
    // measured OUT from the tower's face at that row, not from world zero: at the base
    // the face is 18 tiles out and at the needle it is 5, so a fixed x that reads as open
    // sky up top is buried in stone at the bottom.
    //
    // A patrol leg covers ~190px ≈ 12 tiles and PatrolController always opens by flying
    // right, so a flock is placed at the end of its lane that makes the first sweep run
    // through the useful air: the west flock starts far out and sweeps in toward the
    // face, the east flock starts at the face and sweeps out. Either way the birds stay
    // inside LaneNear..LaneFar and never stall against the stonework.
    private const int LaneNear = 4;    // tiles of clearance from the tower's face
    private const int LaneFar  = 16;   // ...to the far end of the patrol lane

    private static void SpawnBirdFlock(Simulation g, int flockTileY, bool leftSide)
    {
        int half = TowerHalfAt(flockTileY);
        float y = flockTileY * Chunk.TileSize;
        float laneStart = leftSide
            ? -(half + LaneFar)  * Chunk.TileSize     // sweeping in toward the west face
            :  (half + LaneNear) * Chunk.TileSize;    // sweeping out from the east face

        for (int i = 0; i < 3; i++)
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Bird,
                new Vector2(laneStart + i * 18f, y + (i % 2 == 0 ? 0f : -20f))));
    }

    // Two shrikes holding station beside the spire, placed with exactly the same
    // face-relative lane maths as a bird flock — same lane, same altitude — because the
    // point is that a shrike LOOKS like one more bird until it turns and hovers. Reusing
    // TowerHalfAt here rather than a fixed x is not cosmetic: the face is 18 tiles out at
    // the base and 5 at the needle, so a literal that reads as open sky up top is buried
    // in stonework at the bottom. See Entities/Enemies/Types/ShrikeEnemy.cs.
    //
    // Two rather than three: a shrike craters what it goes off over, and the spire is
    // hardened rock — the blast won't open the face, so a third would only add noise to
    // a read the player is meant to be able to make.
    private static void SpawnShrikePair(Simulation g, int shrikeTileY, bool leftSide)
    {
        int half = TowerHalfAt(shrikeTileY);
        float y = shrikeTileY * Chunk.TileSize;
        float laneStart = leftSide
            ? -(half + LaneFar)  * Chunk.TileSize      // sweeping in toward the west face
            :  (half + LaneNear) * Chunk.TileSize;     // sweeping out from the east face

        for (int i = 0; i < 2; i++)
            g.SpawnEntity(EnemyFactory.Create(EntityKind.Shrike,
                new Vector2(laneStart + i * 26f, y + (i % 2 == 0 ? 0f : -20f))));
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

        // One shrike holding the air above them — the flat plain is the clearest
        // place to read a dive, since there is no terrain for it to clip on the
        // way in. Spawned to the right so its opening patrol leg carries it away
        // from the player before it turns back and notices them.
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Shrike, new Vector2(180f, floorY - 90f)));

        // A couple of ammo balls so the player has something to chuck at the
        // brutes — mirrors the arena setup.
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(-40f, 40f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2( 80f, 40f)));
    }
}
