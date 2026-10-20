using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Clod grab feel (Shift+LMB block grab → carry → throw), two rules:
//   1. Press arbitration: a press with the cursor ON reachable terrain is a block grab
//      even with a grabbable body standing right there; the entity grab takes the press
//      only off terrain, or with the cursor directly on the body
//      (BlockGrabAction.PressIsBlockGrab, shared by both actions).
//   2. The held clod follows the cursor freely in both axes (out to GrabHoldReach),
//      shrugs off terrain contact, and bursts on a body it can hurt with the thrown
//      clod's own Detonate — the holder untouched.
public class ClodGrabTests(ITestOutputHelper output)
{
    // ── 1. Press arbitration ───────────────────────────────────────────────────────

    private const float GrabDt = 1f / 60f;

    private static ChunkMap FlatGround() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        OOOOOOOOOOOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly float FloorTop = 2f * Chunk.TileSize;
    private static readonly float RestY    = FloorTop - PlayerCharacter.Radius;
    private static readonly Vector2 AttackerStart = new(70f, RestY);
    private static readonly Vector2 VictimStart   = new(95f, RestY);   // inside the grab field

    // Settle, then a Shift+LMB press held at `mouse`. Records every action the attacker ran.
    private HashSet<string> PressAt(Vector2 mouse, out bool victimGrabbed)
        => PressAt(FlatGround(), VictimStart, mouse, out victimGrabbed);

    private HashSet<string> PressAt(ChunkMap terrain, Vector2 victimStart, Vector2 mouse, out bool victimGrabbed)
    {
        var script = new InputScript()
            .For(10, new PlayerInput { MouseWorldPosition = mouse })
            .Forever(new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = mouse });
        var seen = new HashSet<string>();
        bool grabbed = false;
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain,
            Frames  = 30,
            Dt      = GrabDt,
            Gravity = new Vector2(0f, 600f),
            Players = new[]
            {
                new SimPlayer { StartPosition = AttackerStart, Script = script },
                new SimPlayer { StartPosition = victimStart,   Faction = Faction.Neutral },
            },
        }, onFrame: (f, ps) =>
        {
            seen.Add(ps[0].CurrentActionName);
            if (ps[1].Combat.GrabbedActive) grabbed = true;
            if (f == 9)
            {
                var b = ps[1].Body.Bounds;
                output.WriteLine($"victim bounds L{b.Left:F1} R{b.Right:F1} T{b.Top:F1} B{b.Bottom:F1}");
            }
        });
        victimGrabbed = grabbed;
        output.WriteLine($"mouse={mouse}: actions [{string.Join(", ", seen)}] victimGrabbed={grabbed}");
        return seen;
    }

    // The bug: a grabbable body nearby used to win a press aimed straight at a block.
    [Fact]
    public void CursorOnBlock_WithEnemyNearby_IsBlockGrab()
    {
        // A floor cell just ahead of the attacker's feet, short of the victim's body.
        var onFloor = new Vector2(78f, FloorTop + Chunk.TileSize * 0.5f);
        var seen = PressAt(onFloor, out bool grabbed);
        Assert.Contains("BlockGrabAction", seen);
        Assert.DoesNotContain("GrabAction", seen);
        Assert.False(grabbed, "a press aimed at terrain must not grab the nearby body");
    }

    [Fact]
    public void CursorInOpenAir_NextToEnemy_IsEntityGrab()
    {
        var seen = PressAt(new Vector2(VictimStart.X + 20f, RestY - 4f), out bool grabbed);
        Assert.Contains("GrabAction", seen);
        Assert.DoesNotContain("BlockGrabAction", seen);
        Assert.True(grabbed, "aimed at open air beside a body, the entity grab still takes it");
    }

    // Flat ground plus a wall column at col 9 rising four cells above it — something
    // solid a body can stand flush against, taller than the body.
    private static ChunkMap GroundWithWall() => SimTerrain.FromAscii(@"
        OOOOOOOOOXOOOOOOOOOOOOOOOOOO
        OOOOOOOOOXOOOOOOOOOOOOOOOOOO
        OOOOOOOOOXOOOOOOOOOOOOOOOOOO
        OOOOOOOOOXOOOOOOOOOOOOOOOOOO
        XXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: -2);

    private static readonly float   WallFace     = 9f * Chunk.TileSize;
    private static readonly Vector2 VictimAtWall = new(WallFace - 6f, RestY);   // body half-width is 5 px

    // Judgement call: a cursor directly on the body beats the terrain under it. The
    // victim stands flush against the wall; the cursor sits just inside the wall's face,
    // in solid AND touching the victim's hurtbox.
    [Fact]
    public void CursorOnEnemyBody_OverTerrain_IsEntityGrab()
    {
        var onFace = new Vector2(WallFace + 1f, RestY - 12f);
        var seen = PressAt(GroundWithWall(), VictimAtWall, onFace, out bool grabbed);
        Assert.Contains("GrabAction", seen);
        Assert.DoesNotContain("BlockGrabAction", seen);
        Assert.True(grabbed);
    }

    // The same wall, the cursor on it well above the victim's head: on terrain, not on
    // the body — a block grab, body or no body.
    [Fact]
    public void CursorOnWallAboveEnemy_IsBlockGrab()
    {
        var aboveHead = new Vector2(WallFace + 4f, -2f * Chunk.TileSize + 3f);
        var seen = PressAt(GroundWithWall(), VictimAtWall, aboveHead, out bool grabbed);
        Assert.Contains("BlockGrabAction", seen);
        Assert.DoesNotContain("GrabAction", seen);
        Assert.False(grabbed);
    }

    // ── 2. The held clod ───────────────────────────────────────────────────────────

    private const float Dt = 1f / 60f;

    // A ledge (cols 0-5, rows 5-6) with open air to its right falling to a floor at row
    // 10, and a free-floating block at (7,2) up-right of the holder to rip out.
    private static ChunkMap Ledge() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        OOOOOOOXOOOO
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        XXXXXXOOOOOO
        XXXXXXOOOOOO
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        OOOOOOOOOOOO
        XXXXXXXXXXXX
        XXXXXXXXXXXX", originTileX: 0, originTileY: 0);

    private static readonly float   LedgeTop = 5f * Chunk.TileSize;
    private static readonly Vector2 Holder   = new(55f, LedgeTop - PlayerCharacter.Radius);
    private static readonly Vector2 Enemy    = new(20f, LedgeTop - PlayerCharacter.Radius);
    private static readonly Vector2 OnBlock  = new(7 * Chunk.TileSize + Chunk.TileSize / 2f, 2 * Chunk.TileSize + Chunk.TileSize / 2f);
    private static readonly Vector2 PullTo   = OnBlock + new Vector2(48f, 0f);
    private const int GrabFrames = 35;   // settle 10 + paint 15 + pull 10

    private static InputScript Grab() => new InputScript()
        .For(10, new PlayerInput { MouseWorldPosition = OnBlock })
        .For(15, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = OnBlock })
        .For(10, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = PullTo });

    private static InputScript Hold(InputScript s, Vector2 at, int frames)
        => s.For(frames, new PlayerInput { Shift = true, LeftClick = true, MouseWorldPosition = at });

    private sealed class Trace
    {
        public readonly Dictionary<int, Vector2> BallPos  = new();   // frame → held ball
        public readonly Dictionary<int, Vector2> BodyPos  = new();
        public int   HeldFrames;
        public int   DetonateFrame = -1;
        public bool  DetonatedWhileTracking;
        public float EnemyHealth = float.NaN, HolderHealth = float.NaN;
        public float MaxHealth;
    }

    private Trace Run(InputScript holder, int frames, bool withEnemy)
    {
        var t = new Trace();
        var players = new List<SimPlayer> { new() { StartPosition = Holder, Script = holder } };
        if (withEnemy) players.Add(new SimPlayer { StartPosition = Enemy, Faction = Faction.Player2 });
        var cfg = MovementConfig.Current;
        bool prev = cfg.BlockPeelEnabled;
        cfg.BlockPeelEnabled = true;
        try
        {
            SimRunner.RunMulti(new SimConfigMulti
            {
                Terrain = Ledge(),
                Frames  = frames,
                Dt      = Dt,
                Gravity = new Vector2(0f, 600f),
                Players = players,
            }, onFrameEntities: (f, ps, es) =>
            {
                t.BodyPos[f]    = ps[0].Body.Position;
                t.HolderHealth  = ps[0].Health;
                t.MaxHealth     = ps[0].MaxHealth;
                if (withEnemy) t.EnemyHealth = ps[1].Health;
                foreach (var e in es)
                {
                    if (e is not LobbedAreaProjectile ball) continue;
                    if (ball.Detonated && t.DetonateFrame < 0) t.DetonateFrame = f;
                    if (ball.Tracking && ps[0].CurrentActionVars.OrbHeld)
                    {
                        t.HeldFrames++;
                        t.BallPos[f] = ball.Body.Position;
                    }
                }
            });
        }
        finally { cfg.BlockPeelEnabled = prev; }
        output.WriteLine($"held={t.HeldFrames} detonate=f{t.DetonateFrame} holderHp={t.HolderHealth} enemyHp={t.EnemyHealth}");
        return t;
    }

    // Cursor straight up, then down-right below the feet: the clod goes with it. The
    // old rest position capped it at GrabHandDistance+GrabHandLean (~37 px) from the
    // body and never below the feet, a ~44 px vertical band.
    [Fact]
    public void HeldClod_FollowsCursorUpAndDown()
    {
        var up   = Holder + new Vector2(0f, -60f);
        var down = new Vector2(115f, 70f);                 // past the ledge lip, below the feet
        var s = Grab();
        Hold(s, up, 30);
        Hold(s, down, 30);
        var t = Run(s, GrabFrames + 60, withEnemy: false);

        int fUp = GrabFrames + 29, fDown = GrabFrames + 59;
        Assert.True(t.BallPos.ContainsKey(fUp) && t.BallPos.ContainsKey(fDown), "the clod should be held throughout");
        float dyUp   = t.BallPos[fUp].Y   - t.BodyPos[fUp].Y;
        float dyDown = t.BallPos[fDown].Y - t.BodyPos[fDown].Y;
        output.WriteLine($"ball dy: up={dyUp:F1} down={dyDown:F1} range={dyDown - dyUp:F1}");
        Assert.True(dyUp < -40f, $"cursor 60 px overhead should lift the clod well above the body (dy={dyUp:F1})");
        Assert.True(dyDown > PlayerCharacter.Radius, $"the clod should drop below the feet off the ledge (dy={dyDown:F1})");
        Assert.True(dyDown - dyUp > 60f, $"vertical range {dyDown - dyUp:F1} px");
        // And never past the reach.
        foreach (var (f, p) in t.BallPos)
            Assert.True((p - t.BodyPos[f]).Length() <= MovementConfig.Current.GrabHoldReach + 8f,
                        $"f{f}: clod {(p - t.BodyPos[f]).Length():F1} px out");
    }

    // Cursor buried in the ledge: the clod presses against the terrain and survives.
    [Fact]
    public void HeldClod_PressedIntoTerrain_DoesNotBurst()
    {
        var s = Grab();
        Hold(s, new Vector2(30f, LedgeTop + 10f), 30);     // inside the ledge, down-left
        Hold(s, new Vector2(60f, LedgeTop + 15f), 20);     // straight down into it
        var t = Run(s, GrabFrames + 50, withEnemy: false);
        Assert.True(t.BallPos.ContainsKey(GrabFrames + 49), "the clod should still be in hand");
        Assert.Equal(-1, t.DetonateFrame);
        // Kept clear of the terrain rather than ground into it.
        var last = t.BallPos[GrabFrames + 49];
        Assert.True(last.Y < LedgeTop, $"clod should sit above the ledge surface, y={last.Y:F1}");
    }

    // Swing the held clod into an opponent: it bursts and hurts them; the holder is
    // untouched, and the grab ends (the ball left the hand).
    [Fact]
    public void HeldClod_TouchingEnemy_BurstsAndDamages()
    {
        var s = Grab();
        Hold(s, Enemy, 40);
        var t = Run(s, GrabFrames + 40, withEnemy: true);
        Assert.True(t.DetonateFrame > 0, "the held clod should burst on the opponent");
        Assert.True(t.EnemyHealth < t.MaxHealth, $"the opponent should take the burst (hp={t.EnemyHealth})");
        Assert.Equal(t.MaxHealth, t.HolderHealth);
        Assert.True(t.BallPos.Keys.Count == 0 || MaxKey(t.BallPos) < t.DetonateFrame,
                    "the clod is no longer held once it has burst");
    }

    // Release with the mouse still, over open air: it drops and bursts on the floor as
    // a thrown clod always has.
    [Fact]
    public void ThrownClod_StillBurstsOnTerrain()
    {
        var hold = new Vector2(100f, 40f);
        var s = Grab();
        Hold(s, hold, 20);
        s.Forever(new PlayerInput { MouseWorldPosition = hold });
        var t = Run(s, GrabFrames + 120, withEnemy: false);
        Assert.True(t.DetonateFrame > GrabFrames + 20, $"the dropped clod should burst on landing (f{t.DetonateFrame})");
    }

    private static int MaxKey(Dictionary<int, Vector2> d)
    {
        int m = int.MinValue;
        foreach (var k in d.Keys) m = Math.Max(m, k);
        return m;
    }
}
