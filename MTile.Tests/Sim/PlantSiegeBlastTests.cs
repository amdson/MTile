using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The siege pod's shot (Entities/Plants/SiegeBlast.cs, Plans/PLANT_SEEDS_PLAN.md §4.4).
// Every assertion is written against MATERIAL COUNTS, not HP numbers, so it holds whether
// material_strengths.json or the code defaults are loaded (other test classes swap them).
//
// Lance geometry used throughout: the muzzle sits at the centre of column 0, exactly on the
// row-2 boundary (y = 2·ts), aimed +x — so the two lanes are rows 1 and 2. Walls start at
// column 10, well past BlastRadius (55 px = 5 tiles), so the blast never touches them.
//
// Run just these: scripts/test-group.py terrain   (or --filter "FullyQualifiedName~PlantSiege")
public class PlantSiegeLanceTests(ITestOutputHelper output)
{
    private const float Ts = Chunk.TileSize;
    private static readonly Vector2 Muzzle = new(0.5f * Ts, 2f * Ts);
    private static readonly Vector2 Right  = new(1f, 0f);
    private const int WallX = 10;

    // A wall in rows 1–2, columns [WallX, WallX + materials.Length), one material per column.
    internal static ChunkMap Wall(params TileType[] materials) => Wall(WallX, materials);

    internal static ChunkMap Wall(int x0, TileType[] materials)
    {
        // Base layout through the shared helper, then each column retyped.
        var rows = new string[4];
        for (int r = 0; r < 4; r++)
        {
            var line = new char[x0 + materials.Length + 2];
            for (int c = 0; c < line.Length; c++)
                line[c] = (r is 1 or 2) && c >= x0 && c < x0 + materials.Length ? 'X' : '.';
            rows[r] = new string(line);
        }
        var map = SimTerrain.FromAscii(string.Join("\n", rows));
        for (int i = 0; i < materials.Length; i++)
            for (int r = 1; r <= 2; r++)
                map.EditorSetCell(x0 + i, r, TileState.Solid, materials[i]);
        return map;
    }

    internal static TileType[] Run(TileType t, int n)
    {
        var a = new TileType[n];
        Array.Fill(a, t);
        return a;
    }

    private static bool Solid(ChunkMap m, int x, int y) => m.GetCellState(x, y) == TileState.Solid;

    // Both lanes: columns [WallX, WallX + broken) are gone, column WallX + broken stands.
    private void AssertThrough(ChunkMap map, SiegeReach reach, int broken, string what)
    {
        for (int r = 1; r <= 2; r++)
        {
            for (int c = WallX; c < WallX + broken; c++)
                Assert.False(Solid(map, c, r), $"{what}: cell ({c},{r}) should have been broken");
            Assert.True(Solid(map, WallX + broken, r), $"{what}: cell ({WallX + broken},{r}) should stand");
        }
        for (int j = 0; j < reach.LaneCount; j++)
        {
            var lane = reach[j];
            output.WriteLine($"{what} lane {j}: broke {lane.Broken}, stopped {lane.Stopped} at ({lane.StopX},{lane.StopY}), left {lane.UnitsLeft:F3}");
            Assert.Equal(broken, lane.Broken);
            Assert.True(lane.Stopped);
            Assert.Equal(WallX + broken, lane.StopX);
        }
    }

    [Fact]
    public void Stone_ThreeLayersThrough_FourthStands()
    {
        var map = Wall(Run(TileType.Stone, 6));
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        AssertThrough(map, reach, 3, "stone");
        // The 4th keeps the partial damage (only the float slack here); the 5th is untouched.
        Assert.True(map.Damage.Get(WallX + 3, 1) > 0f, "the stopping cell should keep the lance's leftover damage");
        Assert.True(map.Damage.Get(WallX + 3, 1) < 0.05f * TileDamage.MaxHPFor(TileType.Stone));
        Assert.Equal(0f, map.Damage.Get(WallX + 4, 1));
    }

    [Fact]
    public void Dirt_TwelveLayersThrough()
    {
        var map = Wall(Run(TileType.Dirt, 15));
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        AssertThrough(map, reach, 12, "dirt");
    }

    [Fact]
    public void Sand_LongRun_IsEffectivelyUnlimited()
    {
        // 100 sand columns, the lance reaches 120 tiles: all of it goes.
        var map = Wall(Run(TileType.Sand, 100));
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        for (int c = WallX; c < WallX + 100; c++)
            Assert.False(Solid(map, c, 1) || Solid(map, c, 2), $"sand ({c}) should be gone");
        Assert.False(reach[0].Stopped);
        Assert.False(reach[1].Stopped);
        Assert.Equal(100, reach[0].Broken);

        var foam = Wall(Run(TileType.Foam, 100));
        var fr = SiegeBlast.Fire(foam, null, null, Muzzle, Right, EntityId.None);
        Assert.False(fr[0].Stopped);
        Assert.Equal(100, fr[1].Broken);
    }

    [Fact]
    public void Lance_EndsAtItsLength()
    {
        // Sand out past 120 tiles: the far columns are beyond reach and stand.
        var map = Wall(1, Run(TileType.Sand, 140));
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        Assert.False(reach[0].Stopped);
        Assert.True(Solid(map, 125, 1), "the lance should end at its length");
        Assert.False(Solid(map, 115, 1));
        Assert.InRange(reach.Reach, SiegeBlast.LanceLength - Ts, SiegeBlast.LanceLength + Ts);
    }

    [Fact]
    public void Hardened_StopsAtTheFirstCell_AndItSurvives()
    {
        var map = Wall(new[] { TileType.Hardened, TileType.Dirt, TileType.Dirt });
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        AssertThrough(map, reach, 0, "hardened");
        Assert.True(Solid(map, WallX + 1, 1) && Solid(map, WallX + 1, 2));
        Assert.Equal(0f, map.Damage.Get(WallX + 1, 1));
    }

    [Fact]
    public void Mixed_OneStoneAndEightDirt_IsTheWholeBudget()
    {
        var mats = new List<TileType> { TileType.Stone };
        mats.AddRange(Run(TileType.Dirt, 10));
        var map = Wall(mats.ToArray());
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        AssertThrough(map, reach, 9, "1 stone + dirt");
        // Nothing meaningful left for the 10th cell — only the float slack.
        Assert.True(map.Damage.Get(WallX + 9, 1) < 0.05f * TileDamage.MaxHPFor(TileType.Dirt));
    }

    [Fact]
    public void PreDamagedCells_CostLess()
    {
        // Every stone cell already half broken costs half: six get through instead of three.
        var map = Wall(Run(TileType.Stone, 8));
        float half = 0.5f * TileDamage.MaxHPFor(TileType.Stone);
        for (int c = WallX; c < WallX + 8; c++)
            for (int r = 1; r <= 2; r++)
                Assert.False(map.DamageCell(c, r, half));
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        AssertThrough(map, reach, 6, "half-damaged stone");
    }

    [Fact]
    public void ScaledDownIncomingDamage_CostsMore()
    {
        // Models the planned thorn reinforcement (×0.5 inside DamageCell): DamageCell has no
        // hook for it yet, so the seam stands in for it on the first three columns. Those cost
        // double (8 each), so 12 units buy one reinforced stone and the lance stops in the
        // second — where an unreinforced wall would have let three through.
        var map = Wall(Run(TileType.Stone, 6));
        bool Reinforced(int x) => x >= WallX && x < WallX + 3;
        bool Damage(int x, int y, float a) => map.DamageCell(x, y, Reinforced(x) ? a * 0.5f : a);
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None, Damage);
        AssertThrough(map, reach, 1, "thorned stone");
        // It spent the rest on the second cell: half its HP (4 units at half rate).
        float hp = TileDamage.MaxHPFor(TileType.Stone);
        Assert.InRange(map.Damage.Get(WallX + 1, 1), 0.49f * hp, 0.51f * hp);
    }

    [Fact]
    public void Lanes_SpendIndependently()
    {
        // Stone only in row 1, dirt in row 2: each lane gets what its own row allows.
        var map = Wall(Run(TileType.Dirt, 15));
        for (int c = WallX; c < WallX + 15; c++) map.EditorSetCell(c, 1, TileState.Solid, TileType.Stone);
        var reach = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        Assert.Equal(3, reach[0].Broken);    // lane 0 = row 1
        Assert.Equal(12, reach[1].Broken);   // lane 1 = row 2
        Assert.True(Solid(map, WallX + 3, 1));
        Assert.True(Solid(map, WallX + 12, 2));
    }

    [Theory]
    [InlineData(1f, 0.3f)]      // shallow, mostly horizontal
    [InlineData(-1f, -0.6f)]    // up and left
    [InlineData(0.2f, 1f)]      // mostly vertical
    public void Stone_ThreeLayersThrough_AtAnAngle(float ax, float ay)
    {
        // A wall perpendicular to the MAJOR axis, uniformly 6 cells thick, tall enough to
        // catch both lanes everywhere: 3 layers through at any angle.
        var aim = Vector2.Normalize(new Vector2(ax, ay));
        bool xMajor = MathF.Abs(aim.X) >= MathF.Abs(aim.Y);
        int s = (xMajor ? aim.X : aim.Y) > 0f ? 1 : -1;
        var map = new ChunkMap();
        var muzzle = new Vector2(0.5f * Ts, 0.5f * Ts);
        for (int layer = 0; layer < 6; layer++)
        for (int across = -40; across <= 40; across++)
        {
            int major = s * (10 + layer);
            if (xMajor) map.EditorSetCell(major, across, TileState.Solid, TileType.Stone);
            else        map.EditorSetCell(across, major, TileState.Solid, TileType.Stone);
        }
        var reach = SiegeBlast.Fire(map, null, null, muzzle, aim, EntityId.None);
        for (int j = 0; j < reach.LaneCount; j++)
        {
            Assert.Equal(3, reach[j].Broken);
            Assert.True(reach[j].Stopped);
            Assert.Equal(s * 13, xMajor ? reach[j].StopX : reach[j].StopY);
        }
    }

    [Fact]
    public void Preview_EqualsActualReach_AndDoesNotMutate()
    {
        var mats = new List<TileType> { TileType.Stone, TileType.Sand };
        mats.AddRange(Run(TileType.Dirt, 6));
        mats.Add(TileType.Stone);
        mats.AddRange(Run(TileType.Dirt, 6));
        var map = Wall(mats.ToArray());
        map.DamageCell(WallX + 3, 2, 0.5f * TileDamage.MaxHPFor(TileType.Dirt));   // one pre-damaged cell

        long before = Fingerprint(map);
        var preview = SiegeBlast.Preview(map, Muzzle, Right);
        var t = new TelegraphList();
        var drawn = SiegeBlast.Telegraph(t, map, Muzzle, Right, 0.5f);
        Assert.Equal(before, Fingerprint(map));
        Assert.True(t.Count > 0);

        var fired = SiegeBlast.Fire(map, null, null, Muzzle, Right, EntityId.None);
        Assert.NotEqual(before, Fingerprint(map));
        for (int j = 0; j < fired.LaneCount; j++)
        {
            output.WriteLine($"lane {j}: preview stop ({preview[j].StopX},{preview[j].StopY}) broke {preview[j].Broken}; fired stop ({fired[j].StopX},{fired[j].StopY}) broke {fired[j].Broken}");
            Assert.Equal(fired[j].Stopped, preview[j].Stopped);
            Assert.Equal(fired[j].StopX,   preview[j].StopX);
            Assert.Equal(fired[j].StopY,   preview[j].StopY);
            Assert.Equal(fired[j].Broken,  preview[j].Broken);
            Assert.Equal(fired[j].End,     preview[j].End);
            Assert.Equal(fired[j].End,     drawn[j].End);
        }
    }

    [Fact]
    public void Fire_IsDeterministic()
    {
        static (long, SiegeReach) Once()
        {
            var mats = new List<TileType>();
            for (int i = 0; i < 20; i++) mats.Add((TileType)(i * 7 % 4));   // stone/dirt/sand/foam mix
            var map = Wall(mats.ToArray());
            // Some ground under the muzzle so the blast has cells too.
            for (int c = -6; c <= 6; c++) map.EditorSetCell(c, 3, TileState.Solid, TileType.Dirt);
            var hb = new HitboxWorld();
            var r = SiegeBlast.Fire(map, hb, new HitIdAllocator(), Muzzle, new Vector2(1f, 0.15f), new EntityId(7));
            return (Fingerprint(map) ^ hb.All.Count, r);
        }
        var (a, ra) = Once();
        var (b, rb) = Once();
        Assert.Equal(a, b);
        for (int j = 0; j < ra.LaneCount; j++) Assert.Equal(ra[j].End, rb[j].End);
        Assert.True(ra.BlastCellsBroken > 0);
    }

    // Terrain + damage over the test area, order-sensitive.
    internal static long Fingerprint(ChunkMap map)
    {
        long h = 17;
        for (int y = -45; y <= 45; y++)
        for (int x = -45; x <= 150; x++)
        {
            h = h * 31 + (int)map.GetCellState(x, y);
            h = h * 31 + (int)map.GetCellType(x, y);
            h = h * 31 + BitConverter.SingleToInt32Bits(map.Damage.Get(x, y));
        }
        return h;
    }
}

// Bodies: the hitboxes Fire publishes, resolved by a real Simulation's CombatSystem.
public class PlantSiegeBodyTests(ITestOutputHelper output)
{
    private const float Ts = Chunk.TileSize;
    // Floor top at row 10; the player stands on it. The muzzle sits on the row-9 boundary so
    // the lanes are rows 8 and 9 — the bottom 22 px of a standing body.
    private const int FloorRow = 10;
    private static readonly float FloorTop = FloorRow * Ts;

    // A rooted, weightless test stand-in for the pod: fires once, on its first update.
    private sealed class Firer : Entity
    {
        private readonly Vector2 _aim;
        private bool _fired;
        public SiegeReach Reach;
        public Firer(Vector2 at, Vector2 aim)
            : base(new PhysicsBody(Polygon.CreateRegular(4f, 6), at), health: 50f)
        {
            _aim = aim; Rooted = true; GravityScale = 0f;
        }
        public override void Update(float dt, PlayerCharacter player, HitboxWorld hitboxes, IEntitySpawner spawner)
        {
            if (_fired || spawner == null) return;
            _fired = true;
            Reach = SiegeBlast.Fire(spawner.Chunks, hitboxes, spawner.HitIds, Body.Position, _aim, Id);
        }
    }

    // Floor of `floorMat` from x = -20..60 tiles, plus optional columns of stone wall
    // (full height above the floor) at [wallX, wallX + wallCols).
    private static ChunkMap Arena(int wallX = 0, int wallCols = 0)
    {
        var rows = new List<string>();
        for (int r = 0; r <= FloorRow + 2; r++)
        {
            var line = new char[80];
            for (int c = 0; c < 80; c++)
            {
                bool floor = r >= FloorRow;
                bool wall  = r < FloorRow && c - 20 >= wallX && c - 20 < wallX + wallCols;
                line[c] = floor || wall ? 'X' : '.';
            }
            rows.Add(new string(line));
        }
        var map = SimTerrain.FromAscii(string.Join("\n", rows), originTileX: -20);
        for (int c = -20; c < 60; c++)
            for (int r = FloorRow; r <= FloorRow + 2; r++)
                map.EditorSetCell(c, r, TileState.Solid, TileType.Stone);
        return map;
    }

    private static Vector2 StandAt(int col) => new((col + 0.5f) * Ts, FloorTop - 18f);

    private (Simulation sim, Firer pod) Run(ChunkMap map, int playerCol, Vector2 muzzle, Vector2 aim,
                                             Action<Simulation> extra = null)
    {
        var sim = new Simulation(map, StandAt(playerCol));
        for (int f = 0; f < 20; f++) sim.Step(new PlayerInput());
        extra?.Invoke(sim);
        var pod = new Firer(muzzle, aim);
        sim.SpawnEntity(pod);
        for (int f = 0; f < 4; f++) sim.Step(new PlayerInput());
        return (sim, pod);
    }

    private static Vector2 MuzzleAt(int col) => new((col + 0.5f) * Ts, (FloorRow - 1) * Ts);

    [Fact]
    public void Lance_HitsABodyOnTheLine()
    {
        var (sim, pod) = Run(Arena(), playerCol: 30, MuzzleAt(0), new Vector2(1f, 0f));
        float dmg = sim.Player.Combat.DamageTaken;
        output.WriteLine($"damage {dmg}, lane0 end {pod.Reach[0].End}");
        Assert.Equal(SiegeBlast.LanceBodyDamage, dmg, 3);
        Assert.True(sim.Player.Combat.Launched, "a heavy lance hit should launch the body");
    }

    [Fact]
    public void Lance_DoesNotHitABodyBehindFourStoneLayers()
    {
        // 4 stone columns between pod and player: the lance stops in the 4th.
        var (sim, pod) = Run(Arena(wallX: 15, wallCols: 4), playerCol: 30, MuzzleAt(0), new Vector2(1f, 0f));
        Assert.True(pod.Reach[0].Stopped && pod.Reach[1].Stopped);
        Assert.Equal(18, pod.Reach[0].StopX);
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
    }

    [Fact]
    public void Lance_HitsABodyBehindThreeStoneLayers()
    {
        // The control: three layers are not cover.
        var (sim, pod) = Run(Arena(wallX: 15, wallCols: 3), playerCol: 30, MuzzleAt(0), new Vector2(1f, 0f));
        Assert.Equal(3, pod.Reach[0].Broken);
        Assert.Equal(SiegeBlast.LanceBodyDamage, sim.Player.Combat.DamageTaken, 3);
    }

    [Fact]
    public void Lance_DoesNotHitABodyOffTheLine()
    {
        // Aimed straight up from 20 tiles away: the player is nowhere near it.
        var (sim, _) = Run(Arena(), playerCol: 30, MuzzleAt(10), new Vector2(0f, -1f));
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
    }

    [Fact]
    public void Blast_HitsTheOwnerStandingNextToThePod()
    {
        // The player (Faction.Player1) is the pod's owner here; Hazard ignores that.
        // Aimed up so only the blast can reach them.
        var (sim, _) = Run(Arena(), playerCol: 3, MuzzleAt(1), new Vector2(0f, -1f));
        Assert.Equal(SiegeBlast.BlastBodyDamage, sim.Player.Combat.DamageTaken, 3);
    }

    [Fact]
    public void Shot_HitsAnEntityOfAnyFaction_Once()
    {
        // Neutral (Entity's default) and Enemy-faction bodies inside both the blast and the
        // lance: each is hit, and only once (one HitId for the whole shot).
        Entity neutral = null, enemy = null;
        var (sim, _) = Run(Arena(), playerCol: 50, MuzzleAt(0), new Vector2(1f, 0f), s =>
        {
            neutral = new Entity(new PhysicsBody(Polygon.CreateRegular(5f, 6), MuzzleAt(3)), health: 10f) { Rooted = true, GravityScale = 0f };
            enemy   = new Entity(new PhysicsBody(Polygon.CreateRegular(5f, 6), MuzzleAt(4) + new Vector2(0f, 4f)), health: 10f)
                      { Rooted = true, GravityScale = 0f, Faction = Faction.Enemy };
            s.SpawnEntity(neutral);
            s.SpawnEntity(enemy);
        });
        output.WriteLine($"neutral {neutral.Health}, enemy {enemy.Health}");
        Assert.Equal(10f - SiegeBlast.BlastBodyDamage, neutral.Health, 3);
        Assert.Equal(10f - SiegeBlast.BlastBodyDamage, enemy.Health, 3);
        // The player 50 tiles down the lance was hit too (lance, not blast).
        Assert.Equal(SiegeBlast.LanceBodyDamage, sim.Player.Combat.DamageTaken, 3);
    }

    [Fact]
    public void Blast_ClearsDirtInTheDisc_AndIsOccludedForBodies()
    {
        // Dirt pad under the muzzle: the blast eats it (2.5 > dirt's MaxHP in either config).
        // A 1-column HARDENED wall at x = 5 (it outlasts the blast in either config) shields a
        // player standing just behind it, inside BlastRadius. Aimed up so only the blast is
        // in play.
        var map = Arena(wallX: 5, wallCols: 1);
        for (int r = 0; r < FloorRow; r++) map.EditorSetCell(5, r, TileState.Solid, TileType.Hardened);
        for (int c = -3; c <= 3; c++)
            for (int r = FloorRow; r <= FloorRow + 1; r++)
                map.EditorSetCell(c, r, TileState.Solid, TileType.Dirt);
        var (sim, pod) = Run(map, playerCol: 7, MuzzleAt(2), new Vector2(0f, -1f));
        output.WriteLine($"blast broke {pod.Reach.BlastCellsBroken}, player damage {sim.Player.Combat.DamageTaken}");
        Assert.Equal(TileState.Empty, map.GetCellState(2, FloorRow));
        Assert.Equal(TileState.Empty, map.GetCellState(0, FloorRow));
        Assert.True(pod.Reach.BlastCellsBroken >= 7);
        Assert.Equal(TileState.Solid, map.GetCellState(5, FloorRow - 1));
        Assert.Equal(0f, sim.Player.Combat.DamageTaken);
    }

    [Fact]
    public void Blast_ReachesTheSameSpotWithoutTheWall()
    {
        // The control for the occlusion test: same spot, no wall — the body is hit.
        var (sim, _) = Run(Arena(), playerCol: 7, MuzzleAt(2), new Vector2(0f, -1f));
        Assert.Equal(SiegeBlast.BlastBodyDamage, sim.Player.Combat.DamageTaken, 3);
    }
}
