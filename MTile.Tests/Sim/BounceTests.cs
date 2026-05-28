using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Coverage for ImpactDamage.BounceRestitution + BounceImpulseThreshold —
// the rebound branch in PhysicsWorld.ResolveChunkCollisions{,Swept}.
//
// Three classes of test:
//   * Vertical: bare body falls onto stone, expected to bounce upward
//     with restitution scaling, and to settle once |vnRel| < threshold.
//   * Horizontal: bare body launched at a wall, expected to bounce back.
//   * Below threshold: low-velocity impact should NOT bounce (settles).
//
// PLUS a documentation/regression test for the player: dropped from a
// high enough height onto stone with a non-zero BounceRestitution, the
// player should bounce. Today the StandingState FSD spring catches the
// fall before swept impact damage / bounce can fire — so the player
// doesn't bounce yet. The test is marked Skip with a note describing the
// FSD-catch issue that needs resolving for player-bounce to work.
public class BounceTests
{
    private readonly ITestOutputHelper _out;
    public BounceTests(ITestOutputHelper o) => _out = o;

    private const float Dt = 1f / 30f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const int FloorRow = 20;
    private const float FloorTopY = FloorRow * 16f;

    private static ChunkMap WidePlatform()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            var line = new char[20];
            for (int i = 0; i < 20; i++) line[i] = (r >= 20) ? 'X' : 'O';
            sb.Append(line).Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // 1-col stone wall on the right (col 12, rows 5..24), floor at row 20+,
    // open air everywhere else.
    private static ChunkMap WallAndFloor()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            var line = new char[20];
            for (int i = 0; i < 20; i++)
            {
                bool wall  = (i == 12 && r >= 5);
                bool floor = (r >= 20);
                line[i] = (wall || floor) ? 'X' : 'O';
            }
            sb.Append(line).Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // 16×16 box with non-damaging Impact + configurable bounce. BreakThreshold
    // set to +∞ so the tile under test never break-throughs; the bounce path
    // is always the one we're exercising.
    private static PhysicsBody BounceBox(Vector2 pos, float restitution, float threshold = 200f)
    {
        var poly = new Polygon(new[]
        {
            new Vector2(-8f, -8f), new Vector2( 8f, -8f),
            new Vector2( 8f,  8f), new Vector2(-8f,  8f),
        });
        return new PhysicsBody(poly, pos)
        {
            Impact = new ImpactDamage
            {
                Mass                   = 1f,
                ImpulseThreshold       = 200f,
                DamagePerUnitImpulse   = 0f,                       // never chip
                BreakThreshold         = float.PositiveInfinity,    // never break
                BounceRestitution      = restitution,
                BounceImpulseThreshold = threshold,
            },
        };
    }

    private void Step(PhysicsBody body, ChunkMap terrain, int frames, Action<int, PhysicsBody> log = null)
    {
        var bodies = new List<PhysicsBody> { body };
        for (int f = 0; f < frames; f++)
        {
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            log?.Invoke(f, body);
        }
    }

    // Bare body, dropped onto stone, restitution=0.6 ⇒ each bounce loses
    // 64% of its kinetic energy. From a 200px drop (impact vy ≈ 490),
    // expected first-rebound vy ≈ -294, apex height ≈ 72 px above floor.
    [Fact]
    public void VerticalBounce_BareBodyOnStone_ReachesTwoDistinctApexes()
    {
        var terrain = WidePlatform();
        var body = BounceBox(new Vector2(10 * 16f + 8f, FloorTopY - 200f), restitution: 0.6f);

        var ys      = new List<float>();
        var vys     = new List<float>();
        var bounces = new List<int>();   // frame indices where vy flipped negative
        Step(body, terrain, 90, (f, b) =>
        {
            ys.Add(b.Position.Y);
            vys.Add(b.Velocity.Y);
            // A bounce is a frame where vy transitioned from + (falling) to − (rising).
            if (f > 0 && vys[f] < -5f && vys[f - 1] > 5f) bounces.Add(f);
        });

        // Print a sparse trace so the apexes are easy to read.
        _out.WriteLine("  f    y         vy");
        for (int f = 0; f < ys.Count; f++)
            if (f % 3 == 0 || (f > 0 && Math.Sign(vys[f]) != Math.Sign(vys[f - 1])))
                _out.WriteLine($"  {f,3}  {ys[f],8:F2}  {vys[f],8:F2}");
        _out.WriteLine($"detected bounces at frames: [{string.Join(", ", bounces)}]");

        // First impact velocity is ~sqrt(2·600·200) ≈ 490 → rebound vy ≈ -294.
        // The body should reach at least TWO separable apexes before
        // |vnRel| drops below the 200-threshold and it sticks.
        Assert.True(bounces.Count >= 2,
            $"expected at least 2 distinct bounces, saw {bounces.Count}");

        // After settling, body should be at rest near floor top - half-height.
        // Box body half-height = 8, floor top = 320 ⇒ rest y ≈ 312 (no
        // FSD spring on a bare body, so it sits flush against the tile).
        float restY = ys[^1];
        Assert.InRange(restY, 311f, 313f);
    }

    // Restitution = 1 (perfect elastic) bouncing on a static surface should
    // approximately preserve apex height across bounces. Use a clean drop
    // and check that the body returns to a height comparable to where it
    // started (minus gravity-during-the-step losses).
    [Fact]
    public void VerticalBounce_PerfectElastic_ApexNearStartHeight()
    {
        var terrain = WidePlatform();
        float startY = FloorTopY - 100f;   // 100 px above floor
        var body = BounceBox(new Vector2(10 * 16f + 8f, startY), restitution: 1f);

        float minY = body.Position.Y;   // highest point reached (smallest y)
        Step(body, terrain, 60, (f, b) =>
        {
            if (b.Position.Y < minY) minY = b.Position.Y;
        });

        // Perfect elastic + a brief 30fps integration error should put the
        // first apex within a few px of the start height.
        _out.WriteLine($"start y={startY:F2}, post-bounce apex y={minY:F2}");
        Assert.InRange(minY, startY - 5f, startY + 10f);
    }

    // Below the bounce threshold, the body should stick (no rebound).
    // From a 20px drop, impact vy ≈ 155 → impulse = 155 (Mass=1), below the
    // default 200 threshold. Body must NOT bounce.
    [Fact]
    public void NoBounce_BelowThreshold_BodyStops()
    {
        var terrain = WidePlatform();
        var body = BounceBox(new Vector2(10 * 16f + 8f, FloorTopY - 20f),
                             restitution: 0.6f, threshold: 200f);

        Step(body, terrain, 30);

        // Body should be at rest atop the floor.
        Assert.InRange(body.Position.Y, 311f, 313f);
        Assert.InRange(body.Velocity.Y,  -1f,   1f);
    }

    // Bare body launched horizontally into a stone wall. The bounce reverses
    // its X velocity; with restitution=0.6, post-bounce |vx| ≈ 0.6 × pre.
    // Gravity is zeroed so a clean horizontal scenario can be observed.
    [Fact]
    public void HorizontalBounce_BareBodyIntoWall_VxReverses()
    {
        var terrain = WallAndFloor();
        // Spawn 5 tiles left of the wall (col 12 starts at x=192), at row 18 ⇒
        // y center = 18*16+8 = 296. Aim straight at the wall.
        var body = BounceBox(new Vector2(7 * 16f + 8f, 18 * 16f + 8f), restitution: 0.6f);
        body.Velocity = new Vector2(500f, 0f);

        var noGravity = Vector2.Zero;
        var bodies = new List<PhysicsBody> { body };
        var vxs = new List<float>();
        float maxXReached = body.Position.X;

        for (int f = 0; f < 30; f++)
        {
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, noGravity);
            vxs.Add(body.Velocity.X);
            if (body.Position.X > maxXReached) maxXReached = body.Position.X;
        }

        _out.WriteLine($"max x reached: {maxXReached:F2} (wall left face at x=192)");
        _out.WriteLine($"vx trace: [{string.Join(", ", vxs.ConvertAll(v => v.ToString("F1")))}]");

        // vx must flip sign (positive → negative) at some point, and at the
        // final frame the body must be moving back away from the wall.
        bool flipped = false;
        for (int i = 1; i < vxs.Count; i++)
            if (vxs[i] < 0f && vxs[i - 1] > 0f) { flipped = true; break; }
        Assert.True(flipped, "vx never flipped sign — body didn't bounce off the wall");
        Assert.True(body.Velocity.X < 0f, "final vx should be negative (moving away from wall)");

        // The body's max-x must be ≤ wall's left face + a small overshoot
        // (Epsilon push-out from the solver).
        Assert.InRange(maxXReached, 180f, 192f);
    }

    // Stacking restitution on horizontal: vx after bounce should be roughly
    // -0.6 × pre-bounce vx (with one frame of integration error, ±20%).
    [Fact]
    public void HorizontalBounce_RestitutionScalesVelocity()
    {
        var terrain = WallAndFloor();
        var body = BounceBox(new Vector2(7 * 16f + 8f, 18 * 16f + 8f), restitution: 0.6f);
        body.Velocity = new Vector2(500f, 0f);

        var bodies = new List<PhysicsBody> { body };
        float vxAtBounce = float.NaN;
        bool seenPositive = false;

        for (int f = 0; f < 30; f++)
        {
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Vector2.Zero);   // gravity = 0
            // Detect the frame the bounce fires: vx flips from positive to negative.
            if (!seenPositive && body.Velocity.X > 0f) seenPositive = true;
            if (seenPositive && body.Velocity.X < 0f && float.IsNaN(vxAtBounce))
                vxAtBounce = body.Velocity.X;
        }

        _out.WriteLine($"vx immediately after bounce: {vxAtBounce:F2}");
        // Pre-impact vx is 500; expected post-bounce ≈ -300 with restitution 0.6.
        Assert.InRange(vxAtBounce, -360f, -240f);
    }

    // The player drop case. With BounceRestitution set on the player and
    // MovementConfig.MaxGroundEngageVnRel lowered, the FSD declines to
    // engage at the high inbound vnRel — so the swept-tile-impact path
    // is what resolves the landing, and the bounce branch fires.
    //
    // Player.Impact.BreakThreshold is also bumped to ∞ for this test so
    // stone never break-throughs from the player's impulse — the bounce
    // branch is the only outcome above ImpulseThreshold. (Real-game
    // tuning would weigh up Mass / DamagePerUnitImpulse on the player
    // so they can't smash stone — separate concern.)
    [Fact]
    public void PlayerDropOntoStone_HighEnoughToBounce_ReboundsUpward()
    {
        var terrain = WidePlatform();

        // Mutate MovementConfig.Current for the duration of the test — save/
        // restore so other tests sharing the singleton aren't affected.
        float savedCap = MovementConfig.Current.MaxGroundEngageVnRel;
        MovementConfig.Current.MaxGroundEngageVnRel = 200f;
        try
        {
            var player = new PlayerCharacter(new Vector2(10 * 16f + 8f, FloorTopY - 200f));
            player.Body.Impact.BounceRestitution      = 0.6f;
            player.Body.Impact.BounceImpulseThreshold = 200f;
            // Disable break-through for this test so the bounce branch is
            // the *only* outcome on impact (player would otherwise smash
            // stone at sufficiently high impulse).
            player.Body.Impact.BreakThreshold         = float.PositiveInfinity;

            var bodies = new List<PhysicsBody> { player.Body };
            var ctrl = new Controller();
            float startY = player.Body.Position.Y;
            var ys  = new List<float>();
            var vys = new List<float>();

            for (int f = 0; f < 90; f++)
            {
                ctrl.InjectInput(new PlayerInput());
                terrain.TickSprouts(Dt);
                terrain.Impact.Tick(Dt);
                player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);
                PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
                ys.Add(player.Body.Position.Y);
                vys.Add(player.Body.Velocity.Y);
            }

            // Identify each bounce (vy flipped + → −) and the apex y between bounces.
            var bounceFrames = new List<int>();
            for (int i = 1; i < vys.Count; i++)
                if (vys[i] < -50f && vys[i - 1] > 50f) bounceFrames.Add(i);

            // Apex y = min y between each bounce and the next downward acceleration
            // settling back. Find the first local minimum of y after each bounce.
            var apexYs = new List<float>();
            foreach (int b in bounceFrames)
            {
                float apex = ys[b];
                for (int i = b; i < ys.Count - 1 && ys[i + 1] < ys[i]; i++) apex = ys[i + 1];
                apexYs.Add(apex);
            }

            // Per-frame trace at the bounce events.
            _out.WriteLine($"startY={startY:F2}, bounces at frames [{string.Join(", ", bounceFrames)}], " +
                           $"apex ys=[{string.Join(", ", apexYs.ConvertAll(y => y.ToString("F2")))}]");
            _out.WriteLine("  f    y         vy");
            for (int f = 0; f < ys.Count; f++)
                if (f % 5 == 0 || bounceFrames.Contains(f))
                    _out.WriteLine($"  {f,3}  {ys[f],8:F2}  {vys[f],8:F2}");

            // Bounce expectations: drop from 200 px ⇒ impact vy ≈ 490 ⇒ post-bounce
            // vy ≈ -294 ⇒ apex ≈ 72 px above floor (apex y ≈ 248).
            Assert.True(bounceFrames.Count >= 1, $"expected at least 1 bounce; saw {bounceFrames.Count}");
            Assert.True(vys[bounceFrames[0]] < -100f,
                $"first-bounce upward vy = {vys[bounceFrames[0]]:F2}, expected < -100");
            Assert.True(apexYs[0] < FloorTopY - 30f,
                $"first-bounce apex y = {apexYs[0]:F2}, expected at least 30 px above floor top {FloorTopY}");
        }
        finally
        {
            MovementConfig.Current.MaxGroundEngageVnRel = savedCap;
        }
    }
}
