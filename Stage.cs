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
        var movingRect = new MovingRectangle(new Vector2(baseX, baseY), 64f, 16f);
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
        const float cx = -120f, cy = -150f, radius = 80f, fw = 32f, fh = 16f, fperiod = 6f;
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
        const float floorY = 80f;   // body-center y when standing on the floor
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
        // its spawn point, five tiles above the plateau floor (floor top world
        // y = 96, so 96 − 5·16 = 16). Off to the player-spawn side so the rally
        // has open air away from the dummy's attack cycle.
        g.SpawnEntity(EntityFactory.Practice(new Vector2(-60f, 16f)));

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

    private static void PopulatePlain(Simulation g)
    {
        // Floor sits at world tile y = 6 → world Y = 96; a body with radius ~12
        // centers on Y ≈ 80 when standing on the floor. Skirmishers spawn on the
        // flat section, one to each side of the player so the engagement reads
        // immediately on stage load. Built via EnemyFactory so the blueprint
        // (radius / health / FSM lists) is the single source of truth — swap
        // EntityKind here to test other registered enemies.
        const float floorY = 80f;
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Skirmisher, new Vector2(-100f, floorY)));
        g.SpawnEntity(EnemyFactory.Create(EntityKind.Skirmisher, new Vector2( 140f, floorY)));

        // A couple of ammo balls so the player has something to chuck at the
        // brutes — mirrors the arena setup.
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2(-40f, 40f)));
        g.SpawnEntity(EntityFactory.FloatingBall(new Vector2( 80f, 40f)));
    }
}
