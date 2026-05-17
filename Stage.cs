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
    public string         Name;
    public string         TerrainConfig;   // filename inside Levels/ — TerrainLoader handles the rest
    public Vector2        PlayerSpawn;
    public Action<Game1>  Populate;
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
    }

    public static void Register(Stage s) => _registry[s.Name] = s;

    public static Stage Get(string name) =>
        _registry.TryGetValue(name, out var s) ? s : _registry["start"];

    // ─── populate implementations ─────────────────────────────────────────────

    private static void PopulateStart(Game1 g)
    {
        // Sinusoidal vertical bobber — tests landing on a vertically-moving surface.
        const float baseX = 180f, baseY = -140f, amp = 40f, period = 3f;
        var movingRect = new MovingRectangle(new Vector2(baseX, baseY), 64f, 16f);
        g.AddPlatform(movingRect, Color.SteelBlue);
        float t1 = 0f;
        g.AddTicker(dt => {
            t1 += dt;
            float y = baseY + amp * MathF.Sin(t1 * MathHelper.TwoPi / period);
            movingRect.SetPosition(new Vector2(baseX, y), dt);
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
        float t2 = 0f;
        g.AddTicker(dt => {
            t2 += dt;
            float wheelAngle = t2 * MathHelper.TwoPi / fperiod;
            for (int i = 0; i < count; i++)
            {
                float angle = wheelAngle + i * MathHelper.TwoPi / count;
                var pos = new Vector2(cx + radius * MathF.Cos(angle), cy + radius * MathF.Sin(angle));
                blocks[i].SetPosition(pos, dt);
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

    private static void PopulateArena(Game1 g)
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
}
