using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;

namespace MTile;

// "depths" — a two-player co-op gauntlet: a chain of underground stone rooms (terrain from
// DepthsLayout), each a short script of enemy waves, building from a handful of Stalkers up
// to a room holding all three bosses, three escorts' worth of league fighters and several
// nests. Meant to start easy and end "probably impossible" for two players.
//
// Everything a designer tunes is the DepthsPlan table below: per room a name, a size class
// and a list of waves; per wave the enemies to drop in (stock kinds by EntityKind, league
// fighters by name) and any spawners (EnemySpawner nests that refill up to k and must be
// destroyed before the wave counts as cleared). Edit it by hand after a playtest.
//
// Flow (one room at a time, driven by a ticker; all progress in g.StageVars so it rolls back):
//   waiting   — a living player is inside the room, 6+ tiles from its entrance:
//               the room activates, RespawnPoint moves to its Entry, wave 0 spawns.
//   fighting  — when no hostile (EnemyEntity or EnemySpawner) is left inside the room, the
//               next wave enters 1.5 s later; after the last wave the exit door is broken
//               open and the next room is the one waiting. Deaths don't reset anything.
//   StageVars: [0] room index (== plan length once the last room is cleared),
//              [1] phase (0 waiting, 1 fighting), [2] wave index, [3] frame the next wave
//              may spawn (0 = not scheduled), [4] frame the last wave spawned.
public static partial class Stages
{
    private const int DepthsSeed = 7;

    // ── Plan data types ─────────────────────────────────────────────────────
    public enum DepthsPlace { Floor, Ceiling, Wall, Air, Boss }

    // `Count` enemies of a stock kind (Fighter == null) or of the named league fighter.
    public readonly record struct DepthsSpawn(EntityKind Kind, string Fighter, int Count, DepthsPlace At);

    // A spawner nest: keeps up to MaxLive of the kind/fighter alive near itself.
    public readonly record struct DepthsNest(EntityKind Kind, string Fighter, int MaxLive, float Interval, float Health, DepthsPlace At);

    public sealed class DepthsWave
    {
        public DepthsSpawn[] Enemies;
        public DepthsNest[]  Nests;
    }

    // Shape picks the room's geometry (DepthsLayout); Size is its derived class
    // (0 small, 1 medium, 2 boss-capable).
    public sealed class DepthsRoomPlan
    {
        public string        Name;
        public DepthsShape   Shape;
        public DepthsWave[]  Waves;
        public int           Size => DepthsLayout.SizeClass(Shape);
    }

    // Table helpers (keep the table below readable).
    private static DepthsSpawn S(EntityKind k, int n, DepthsPlace at = DepthsPlace.Floor) => new(k, null, n, at);
    private static DepthsSpawn F(string name, int n, DepthsPlace at = DepthsPlace.Floor) => new(default, name, n, at);
    private static DepthsNest   N(EntityKind k, int maxLive, DepthsPlace at = DepthsPlace.Wall, float every = 4f, float hp = 3f)
        => new(k, null, maxLive, every, hp, at);
    private static DepthsNest   NF(string name, int maxLive, DepthsPlace at = DepthsPlace.Wall, float every = 4f, float hp = 3f)
        => new(default, name, maxLive, every, hp, at);
    private static DepthsWave   W(DepthsSpawn[] enemies, params DepthsNest[] nests)
        => new() { Enemies = enemies ?? Array.Empty<DepthsSpawn>(), Nests = nests };

    private const DepthsPlace Fl = DepthsPlace.Floor, Ce = DepthsPlace.Ceiling, Wa = DepthsPlace.Wall,
                              Ai = DepthsPlace.Air,   Bo = DepthsPlace.Boss;

    // ── THE ENCOUNTER TABLE ─────────────────────────────────────────────────
    // League fighters are named as in GauntletOrder (weakest -> strongest by Elo); a name
    // missing from this checkout (holdout packages) is skipped at spawn time.
    public static readonly DepthsRoomPlan[] DepthsPlan =
    {
        // 0-3: easy. A few melee grunts and the weakest fighters, 2-4 at a time; the
        // first climb comes early, while the enemies are still soft.
        new() { Name = "The Antechamber", Shape = DepthsShape.Small, Waves = new[] {
            W(new[] { S(EntityKind.Stalker, 2) }),
            W(new[] { S(EntityKind.Brute, 1), S(EntityKind.Stalker, 1) }),
        } },
        new() { Name = "The Weeping Hall", Shape = DepthsShape.Gallery, Waves = new[] {
            W(new[] { F("Sprinter", 1), S(EntityKind.Spearman, 2) }),
            W(new[] { F("ExampleBrawler", 2), S(EntityKind.Stalker, 1) }),
        } },
        new() { Name = "The Chimney", Shape = DepthsShape.Shaft, Waves = new[] {
            W(new[] { S(EntityKind.Stalker, 3) }),
            W(new[] { F("Flyer", 1, Ai), S(EntityKind.Latcher, 1, Ce), S(EntityKind.Spearman, 1) }),
        } },
        new() { Name = "Gravel Gallery", Shape = DepthsShape.Medium, Waves = new[] {
            W(new[] { S(EntityKind.Brute, 2), S(EntityKind.Turret, 1) }),
            W(new[] { F("Flyer", 2, Ai), S(EntityKind.Spearman, 2) }),
            W(new[] { F("Kestrel", 1, Ai), F("Builder", 1), S(EntityKind.Brute, 1) }),
        } },

        // 4-6: mixed sets, and the first nests (low k).
        new() { Name = "The Nursery", Shape = DepthsShape.Terraces, Waves = new[] {
            W(new[] { F("Turret", 1), S(EntityKind.Stalker, 2) }),
            W(new[] { F("Bulwark", 1), F("Gunner", 1), S(EntityKind.Spearman, 2) },
              N(EntityKind.Stalker, 2)),
        } },
        new() { Name = "Fungal Crossing", Shape = DepthsShape.Chasm, Waves = new[] {
            W(new[] { S(EntityKind.Pulser, 2), S(EntityKind.Spearman, 2) }),
            W(new[] { S(EntityKind.Latcher, 2, Ce), S(EntityKind.Brute, 2) }),
            W(new[] { F("Skirmish", 1), F("Gunner", 1), S(EntityKind.Brute, 1) },
              N(EntityKind.Spearman, 2)),
        } },
        new() { Name = "The Sump", Shape = DepthsShape.Dome, Waves = new[] {
            W(new[] { S(EntityKind.Aspid, 2, Ai), S(EntityKind.Stalker, 2), F("Gravemantle", 1) }),
            W(new[] { S(EntityKind.Warden, 1), F("Sapper", 1), S(EntityKind.Brute, 2) },
              N(EntityKind.Brute, 2)),
        } },

        // 7: first boss, alone.
        new() { Name = "Quillback's Den", Shape = DepthsShape.Large, Waves = new[] {
            W(new[] { S(EntityKind.Quillback, 1, Bo) }),
        } },

        // 8-11: harder mixed sets with armor, casters, fliers and several nests; a second
        // climb, this time with things on the walls.
        new() { Name = "The Ossuary", Shape = DepthsShape.Medium, Waves = new[] {
            W(new[] { F("Wraith", 2), S(EntityKind.Warden, 1), S(EntityKind.Wizard, 1) }),
            W(new[] { F("Brick", 1), F("Breacher", 1), S(EntityKind.Pulser, 2), S(EntityKind.Aspid, 1, Ai) }),
            W(new[] { S(EntityKind.Warden, 1) },
              N(EntityKind.Spearman, 3), N(EntityKind.Latcher, 2, Ce)),
        } },
        new() { Name = "The Flue", Shape = DepthsShape.Shaft, Waves = new[] {
            W(new[] { S(EntityKind.Aspid, 2, Ai), S(EntityKind.Latcher, 2, Ce), F("Kestrel", 1, Ai) }),
            W(new[] { S(EntityKind.Pulser, 2, Wa), S(EntityKind.Wizard, 1) },
              N(EntityKind.Aspid, 2, Wa)),
        } },
        new() { Name = "Cinder Stairs", Shape = DepthsShape.Terraces, Waves = new[] {
            W(new[] { F("Vanguard", 1), F("Sentinel", 1), S(EntityKind.Wizard, 2) }),
            W(new[] { S(EntityKind.Warden, 2), S(EntityKind.Aspid, 2, Ai) },
              NF("Mason", 2), N(EntityKind.Pulser, 1, Fl)),
        } },
        new() { Name = "The Drowned Forge", Shape = DepthsShape.Chasm, Waves = new[] {
            W(new[] { S(EntityKind.Bastion, 1), S(EntityKind.Pouncer, 2), F("Harrier", 1, Ai), F("Ironshot", 1) }),
            W(new[] { S(EntityKind.Pulser, 2), S(EntityKind.Wizard, 1), F("Lancer", 1) },
              N(EntityKind.Aspid, 2, Ce), NF("Phalanx", 1)),
        } },

        // 12: Prism and escorts.
        new() { Name = "The Prism Gallery", Shape = DepthsShape.Dome, Waves = new[] {
            W(new[] { S(EntityKind.Prism, 1, Bo), S(EntityKind.Spearman, 3), S(EntityKind.Wizard, 1) }),
            W(new[] { F("Arrowhawk", 1, Ai), F("Twinbarrel", 1) },
              N(EntityKind.Spearman, 3)),
        } },

        // 13: the first massive cavern: a long running fight across the whole roster so far.
        new() { Name = "The Great Hollow", Shape = DepthsShape.Massive, Waves = new[] {
            W(new[] { F("Wraith", 2), F("Vanguard", 1), S(EntityKind.Warden, 2), S(EntityKind.Aspid, 2, Ai) }),
            W(new[] { S(EntityKind.Pulser, 3), F("Harrier", 2, Ai), S(EntityKind.Brute, 3) },
              N(EntityKind.Spearman, 3), NF("Mason", 2)),
            W(new[] { F("Ironshot", 2), S(EntityKind.Wizard, 2), S(EntityKind.Shrike, 2, Ai) },
              N(EntityKind.Latcher, 2, Ce)),
        } },

        // 14-16: heavy mixed sets with nests of strong fighters, and a last climb under fire.
        new() { Name = "The Barbed Vault", Shape = DepthsShape.Medium, Waves = new[] {
            W(new[] { F("Stonecreep", 1), F("Ironmaw", 1), F("Thornback", 1), S(EntityKind.Warden, 2) }),
            W(new[] { S(EntityKind.Shrike, 2, Ai), S(EntityKind.Latcher, 2, Ce), F("Hollowstep", 1) },
              NF("Thornrush", 2)),
        } },
        new() { Name = "The Crush", Shape = DepthsShape.Gallery, Waves = new[] {
            W(new[] { F("Tripwire", 1), F("Thornbull", 1), F("Bullrush", 1), F("Gritmaul", 1) }),
            W(new[] { S(EntityKind.Pulser, 3), S(EntityKind.Wizard, 2), S(EntityKind.Warden, 1) },
              NF("Ironwing", 2, Ce), NF("Gatecrash", 2)),
        } },
        new() { Name = "The Gullet", Shape = DepthsShape.Shaft, Waves = new[] {
            W(new[] { S(EntityKind.Shrike, 2, Ai), S(EntityKind.Pulser, 2, Wa), S(EntityKind.Latcher, 2, Ce) }),
            W(new[] { F("Harrier", 1, Ai), S(EntityKind.Wizard, 2) },
              NF("Ironwing", 2, Wa), N(EntityKind.Aspid, 2, Wa)),
        } },

        // 17: Warlord and escorts, with nests.
        new() { Name = "The Warlord's Hall", Shape = DepthsShape.Large, Waves = new[] {
            W(new[] { S(EntityKind.Warlord, 1, Bo), S(EntityKind.Spearman, 2), F("Wardstep", 1) }),
            W(new[] { F("Ironhide", 1), F("Cinderhoof", 1) },
              N(EntityKind.Warden, 2), N(EntityKind.Spearman, 3)),
        } },

        // 18: two bosses together, in a cavern big enough to split them up.
        new() { Name = "The Twin Thrones", Shape = DepthsShape.Massive, Waves = new[] {
            W(new[] { S(EntityKind.Warlord, 1, Bo), S(EntityKind.Quillback, 1, Bo),
                      S(EntityKind.Wizard, 2), S(EntityKind.Warden, 2), F("Bramblehorn", 1) }),
            W(new[] { F("Brambleram", 1), F("Gravemarch", 1) },
              NF("Thornbull", 2), N(EntityKind.Pulser, 2, Fl)),
        } },

        // 19: probably impossible. All three bosses, the strongest fighters, big nests.
        new() { Name = "The Last Depth", Shape = DepthsShape.Massive, Waves = new[] {
            W(new[] { S(EntityKind.Quillback, 1, Bo), S(EntityKind.Prism, 1, Bo), S(EntityKind.Warlord, 1, Bo),
                      F("Gravemarch", 2), F("Brambleram", 2), F("Bramblehorn", 2), F("Cinderhoof", 1) }),
            W(new[] { S(EntityKind.Warden, 3), S(EntityKind.Aspid, 3, Ai) },
              NF("Gravemarch", 3), NF("Brambleram", 3), N(EntityKind.Spearman, 4), N(EntityKind.Wizard, 2, Ce)),
        } },
    };

    // League-fighter names the plan uses, in first-appearance order: index i compiles into
    // FighterSlot(i). Static and derived from the table, so every peer agrees.
    public static readonly string[] DepthsFighters = DepthsPlan
        .SelectMany(r => r.Waves)
        .SelectMany(w => w.Enemies.Select(e => e.Fighter).Concat(w.Nests.Select(n => n.Fighter)))
        .Where(n => n != null)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public static EntityKind DepthsKind(EntityKind kind, string fighter)
        => fighter == null ? kind
         : EntityKinds.FighterSlot(Array.FindIndex(DepthsFighters, n => string.Equals(n, fighter, StringComparison.OrdinalIgnoreCase)));

    public static DepthsShape[] DepthsShapes() => DepthsPlan.Select(r => r.Shape).ToArray();
    private static DepthsMap BuildDepths() => DepthsLayout.Build(DepthsSeed, DepthsShapes());

    private static void RegisterDepths()
    {
        var spawn = BuildDepths().PlayerSpawn;
        Register(new Stage {
            Name        = "depths",
            Coop        = true,
            Terrain     = () => BuildDepths().Chunks,   // fresh map per call (F5 reloads)
            PlayerSpawn = spawn,
            Populate    = PopulateDepths,
        });
    }

    // Body radius used to lift big bodies off the floor (spots are clear for ~16 px).
    private static float DepthsRadius(EntityKind k) => k == EntityKind.Quillback ? 32f : 16f;

    private static void PopulateDepths(Simulation g)
    {
        // Compile the league fighters the plan names into their slot kinds.
        var all   = FighterLeague.AllParticipants();
        var model = new PhysicsCostModel();
        for (int i = 0; i < DepthsFighters.Length; i++)
        {
            var p = all.Find(x => string.Equals(x.Name, DepthsFighters[i], StringComparison.OrdinalIgnoreCase));
            if (p == null) continue;   // holdout package not in this checkout
            var spec = p.Make();
            spec.Kind  = EntityKinds.FighterSlot(i);
            spec.Team  = Teams.Enemies;
            spec.AlertRange = MathF.Max(spec.AlertRange, 96 * Chunk.TileSize);   // they engage across the room
            var r = FighterCompiler.Compile(spec, model);
            if (!r.IsValid)
                throw new InvalidOperationException($"depths: '{spec.Name}' is invalid:\n{r.Report()}");
            g.Blueprints[spec.Kind] = r.Blueprint;
        }

        var rooms = BuildDepths().Rooms;
        var v     = g.StageVars;
        int delay = SimFrames.FromSeconds(1.5f, Simulation.FixedDt);
        v[0] = 0; v[1] = 0; v[2] = 0; v[3] = 0; v[4] = 0;
        const int ActivateTiles = 6;
        const float Margin = 2 * Chunk.TileSize;

        g.AddTicker(t =>
        {
            int frame = (int)MathF.Round(t / Simulation.FixedDt);
            int ri = v[0];
            if (ri >= DepthsPlan.Length || ri >= rooms.Count) return;   // victory: nothing more to do
            var room = rooms[ri];
            var plan = DepthsPlan[ri];
            var box  = room.Interior;
            float x0 = box.Left * Chunk.TileSize - Margin, x1 = box.Right * Chunk.TileSize + Margin;
            float y0 = box.Top  * Chunk.TileSize - Margin, y1 = box.Bottom * Chunk.TileSize + Margin;

            if (v[1] == 0)
            {
                foreach (var p in DepthsLivePlayers(g))
                {
                    var pos = p.Body.Position;
                    if (pos.X < box.Left * Chunk.TileSize || pos.X > x1 || pos.Y < y0 || pos.Y > y1) continue;
                    if (Vector2.Distance(pos, room.Entry) < ActivateTiles * Chunk.TileSize) continue;
                    g.RespawnPoint = room.Entry;
                    v[1] = 1; v[2] = 0; v[3] = 0;
                    DepthsSpawnWave(g, room, plan.Waves[0], 0);
                    v[4] = frame;
                    return;
                }
                return;
            }

            if (frame - v[4] < 3) return;   // let the new wave register before judging it cleared
            foreach (var e in g.Entities)
            {
                if (e.IsDead || !DepthsIsHostile(e)) continue;
                var q = e.Body.Position;
                if (q.X >= x0 && q.X <= x1 && q.Y >= y0 && q.Y <= y1) { v[3] = 0; return; }   // still fighting
            }

            if (v[2] + 1 < plan.Waves.Length)
            {
                if (v[3] == 0) v[3] = frame + delay;
                if (frame < v[3]) return;
                v[2]++; v[3] = 0; v[4] = frame;
                DepthsSpawnWave(g, room, plan.Waves[v[2]], v[2]);
                return;
            }

            // Last wave cleared: open the way on and wait for the next room.
            foreach (var c in room.ExitDoor) g.Chunks.BreakCell(c.X, c.Y);
            v[0]++; v[1] = 0; v[2] = 0; v[3] = 0;
        });
    }

    // What a room must be emptied of: enemies (Stalker and Turret predate EnemyEntity) and
    // nests. Projectiles, props and balls don't count.
    internal static bool DepthsIsHostile(Entity e) => e is EnemyEntity or StalkerEnemy or TurretEnemy or EnemySpawner;

    private static IEnumerable<PlayerCharacter> DepthsLivePlayers(Simulation g)
    {
        if (g.Player.IsAlive) yield return g.Player;
        foreach (var (p, _) in g.SecondaryPlayers) if (p.IsAlive) yield return p;
    }

    // The spots a placement draws from; falls back to floor spots, then the entry.
    private static List<Vector2> DepthsSpots(DepthsRoom room, DepthsPlace at)
    {
        List<Vector2> s = at switch
        {
            DepthsPlace.Ceiling => room.CeilingSpots,
            DepthsPlace.Wall    => room.WallSpots,
            DepthsPlace.Air     => room.AirSpots,
            DepthsPlace.Boss    => room.BossSpots.Count > 0 ? room.BossSpots : room.FloorSpots,   // radius-26 clearance
            _                   => room.FloorSpots,
        };
        if (s == null || s.Count == 0) s = room.FloorSpots;
        if (s.Count == 0) s = new List<Vector2> { room.Entry };
        return s;
    }

    // Spot for the i-th of n things in one entry: a deterministic stride through the list
    // (offset by wave and entry so waves differ), skipping any spot within 80 px of a player.
    private static Vector2 DepthsPick(Simulation g, List<Vector2> spots, int wave, int entry, int i, int n)
    {
        int step  = Math.Max(1, spots.Count / Math.Max(n, 1));
        int start = (wave * 5 + entry * 3 + i * step) % spots.Count;
        for (int k = 0; k < spots.Count; k++)
        {
            var s = spots[(start + k) % spots.Count];
            bool near = false;
            foreach (var p in DepthsLivePlayers(g))
                if (Vector2.DistanceSquared(p.Body.Position, s) < 80f * 80f) { near = true; break; }
            if (!near) return s;
        }
        return spots[start];
    }

    private static void DepthsSpawnWave(Simulation g, DepthsRoom room, DepthsWave wave, int waveIdx)
    {
        int entry = 0;
        foreach (var e in wave.Enemies)
        {
            var kind = DepthsKind(e.Kind, e.Fighter);
            if (e.Fighter != null && !g.Blueprints.ContainsKey(kind)) { entry++; continue; }
            var spots = DepthsSpots(room, e.At);
            for (int i = 0; i < e.Count; i++)
            {
                var pos = DepthsPick(g, spots, waveIdx, entry, i, e.Count);
                if (e.At == DepthsPlace.Floor) pos.Y -= DepthsRadius(kind) - 16f;
                else if (e.At == DepthsPlace.Boss) pos.Y -= DepthsRadius(kind) - 26f;   // BossSpots assume radius 26
                var en = EntityFactory.CreateEnemy(kind, pos, g.Blueprints);
                if (en is EnemyEntity ee)
                    ee.SeedBrain(BrainRandom.Mix((ulong)(room.Index + 1), (ulong)(waveIdx * 64 + entry * 8 + i + 1)));
                g.SpawnEntity(en);
            }
            entry++;
        }
        for (int j = 0; j < wave.Nests.Length; j++)
        {
            var n    = wave.Nests[j];
            var kind = DepthsKind(n.Kind, n.Fighter);
            if (n.Fighter != null && !g.Blueprints.ContainsKey(kind)) continue;
            var pos = DepthsPick(g, DepthsSpots(room, n.At), waveIdx, 17 + j, 0, 1);
            g.SpawnEntity(new EnemySpawner(pos, kind, n.MaxLive, n.Interval, health: n.Health));
        }
    }
}
