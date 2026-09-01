using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// Surface-relative standing + jumping (BACKLOG 5.8, the sprout-lift fix).
//
// A sprout grows under a standing player at Chunk.TileSize / SproutLifetime
// (~110 px/s) — faster than SpringMaxRiseSpeed (80). Before the fix every
// support gate measured ABSOLUTE rise, so the whole stack (FoldBaseline's
// gravity hold, the qp envelope reference, FoldReference/Lattice admits, the
// predictor's coast) declared the body launched and let raw contact
// resolution bulldoze it up against full gravity — per-frame velocity
// sawtooth and position pops. Now rise is measured against the support
// surface's own velocity (GroundChecker reports it for growing sprout
// volumes), FloorEnvelope sees the growing volume as floor, and the jumps
// already launch relative to their source surface — so the ride is smooth
// and a jump off a rising floor inherits its velocity.
//
// Fixture geometry derives from Chunk.TileSize (the StandingJitterTests
// fixtures predate the TileSize move and hardcode 16px).
public class SproutLiftJumpTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);
    private const int FloorRow = 20;
    private const int PlayerCol = 10;
    private static float Ts => Chunk.TileSize;
    private static float FloorTopY => FloorRow * Ts;
    private static float SproutRiseSpeed => Ts / MovementConfig.Current.SproutLifetime;

    private static ChunkMap WidePlatform()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            for (int i = 0; i < 20; i++) sb.Append(r >= FloorRow ? 'X' : 'O');
            sb.Append('\n');
        }
        return SimTerrain.FromAscii(sb.ToString());
    }

    // Contact-rest spawn (see StandingJitterTests.RestOffset): float height (R)
    // plus the hexagon's bottom extent, so the settle window is short.
    private static readonly float RestOffset =
        PlayerCharacter.Radius * (1f + MathF.Sin(MathF.PI / 3f));

    private record FrameSample(int Frame, float PosX, float PosY, float VelX, float VelY, string State, string Cons);

    private List<FrameSample> Run(ChunkMap terrain, PlayerCharacter player, int frames,
                                  Func<int, PlayerInput> input, Action<int, ChunkMap> beforeFrame = null)
    {
        var bodies = new List<PhysicsBody> { player.Body };
        var ctrl = new Controller();
        var samples = new List<FrameSample>(frames);
        for (int f = 0; f < frames; f++)
        {
            beforeFrame?.Invoke(f, terrain);
            ctrl.InjectInput(input?.Invoke(f) ?? new PlayerInput());
            terrain.TickSprouts(Dt);
            terrain.Impact.Tick(Dt);
            player.Update(ctrl, terrain, new HitboxWorld(), new HurtboxWorld(), Dt);
            PhysicsWorld.StepSwept(bodies, terrain, Dt, Gravity);
            var cons = new StringBuilder();
            foreach (var c in player.Body.Constraints)
                if (c is SurfaceDistance sd)
                    cons.Append($"[n=({sd.Normal.X:F0},{sd.Normal.Y:F0}) vs=({sd.SurfaceVelocity.X:F0},{sd.SurfaceVelocity.Y:F0})]");
            samples.Add(new FrameSample(f, player.Body.Position.X, player.Body.Position.Y,
                                        player.Body.Velocity.X, player.Body.Velocity.Y,
                                        player.CurrentStateName, cons.ToString()));
        }
        return samples;
    }

    private static PlayerCharacter SpawnStanding()
        => new(new Vector2(PlayerCol * Chunk.TileSize + Chunk.TileSize * 0.5f,
                           FloorRow * Chunk.TileSize - PlayerCharacter.Radius * (1f + MathF.Sin(MathF.PI / 3f))));

    // ── 1. The ride: smooth one-tile lift, no sawtooth, bounded overshoot ────
    [Fact]
    public void SproutLift_CarriesStandingPlayer_SmoothlyOneTile()
    {
        var terrain = WidePlatform();
        var player = SpawnStanding();
        const int requestFrame = 20;
        TileSproutNode sprout = null;

        var samples = Run(terrain, player, 120, null, (f, t) =>
        {
            if (f == requestFrame)
                sprout = t.TryRequestTile(PlayerCol, FloorRow - 1, TileType.Stone);
        });
        Assert.NotNull(sprout);

        float before = samples[requestFrame - 1].PosY;
        float after  = samples[^1].PosY;
        output.WriteLine($"y before {before:F2} → after {after:F2} (lift {before - after:F2}, tile {Ts})");

        // Net lift: exactly one tile of floor gained, at the same hover.
        Assert.True(MathF.Abs((before - after) - Ts) < 2f,
            $"expected ~{Ts}px lift, got {before - after:F2}");

        // Smoothness: from the request to the end, per-frame Δy stays within the
        // floor's own speed plus slack — motion, never a snap. (The floor moves
        // SproutRiseSpeed·Dt ≈ 1.8px per frame; the momentum hop after
        // completion decelerates from the same speed.)
        float dyCap = SproutRiseSpeed * Dt + 1.5f;
        for (int i = requestFrame + 1; i < samples.Count; i++)
        {
            float dy = samples[i].PosY - samples[i - 1].PosY;
            Assert.True(MathF.Abs(dy) < dyCap,
                $"frame {i}: Δy {dy:F2} exceeds {dyCap:F2} ({samples[i - 1].State}→{samples[i].State}) — snap, not motion");
        }

        // The completion hop (carried momentum decelerating under gravity) is
        // bounded by v²/2g ≈ 10px — anything much larger means support let go
        // during the ride and something re-launched the body.
        float minY = float.MaxValue;
        foreach (var s in samples) minY = MathF.Min(minY, s.PosY);
        float hop = after - minY;   // y-down: overshoot above the final rest
        float hopCap = SproutRiseSpeed * SproutRiseSpeed / (2f * Gravity.Y) + 5f;
        output.WriteLine($"overshoot above final rest: {hop:F2}px (cap {hopCap:F2})");
        Assert.True(hop < hopCap, $"overshoot {hop:F2}px exceeds momentum-hop bound {hopCap:F2}px");
    }

    // ── 1b. The chain: the discriminating case ───────────────────────────────
    // Three stacked sprouts grow in sequence (each promoted when its parent
    // completes) — 18 frames of continuous 110 px/s lift with two hand-offs.
    // Under the old ABSOLUTE rise gates, every hand-off found the body "rising
    // too fast to be supported": the hold dropped, the body flew a small hop,
    // fell back (vy briefly positive, downward) onto the next rising volume,
    // and bounced its way up the column. Surface-relative support rides the
    // whole chain as one smooth carry — the body never enters a falling phase
    // until the final block has finished.
    [Fact]
    public void SproutChain_CarriesPlayerContinuously_NoPerBlockBounce()
    {
        var terrain = WidePlatform();
        var player = SpawnStanding();
        const int requestFrame = 20;

        var samples = Run(terrain, player, 160, null, (f, t) =>
        {
            if (f != requestFrame) return;
            Assert.NotNull(t.TryRequestTile(PlayerCol, FloorRow - 1, TileType.Stone));
            t.TryRequestTile(PlayerCol, FloorRow - 2, TileType.Stone);
            t.TryRequestTile(PlayerCol, FloorRow - 3, TileType.Stone);
        });

        int growFrames = (int)MathF.Ceiling(3f * MovementConfig.Current.SproutLifetime / Dt);
        int chainEnd   = requestFrame + growFrames;

        for (int f = requestFrame - 2; f < Math.Min(chainEnd + 25, samples.Count); f++)
            output.WriteLine($"  f{samples[f].Frame,3}  y {samples[f].PosY,8:F3}  vy {samples[f].VelY,8:F2}  {samples[f].State}");

        // Through the chain (entry slack aside), the ride is continuous: the
        // body never falls (vy meaningfully positive) and never gives back
        // height mid-column. A per-block bounce breaks both.
        float maxVy = float.MinValue, maxBack = 0f, crest = samples[requestFrame].PosY;
        for (int f = requestFrame + 3; f <= chainEnd && f < samples.Count; f++)
        {
            maxVy = MathF.Max(maxVy, samples[f].VelY);
            crest = MathF.Min(crest, samples[f].PosY);
            maxBack = MathF.Max(maxBack, samples[f].PosY - crest);
        }
        output.WriteLine($"chain window: max vy {maxVy:F1} (falling > 0), max height give-back {maxBack:F2}px");
        Assert.True(maxVy < 20f,
            $"body entered a falling phase mid-chain (vy reached {maxVy:F1}) — per-block bounce");
        Assert.True(maxBack < 2.5f,
            $"body gave back {maxBack:F2}px mid-chain — per-block bounce");

        // The carry is VELOCITY-based, not positional extrusion. The old
        // absolute gates left the body's velocity near zero while MTV push-out
        // moved its position — every velocity reader (jump inheritance, the
        // predictor's coast, animation) saw a body at rest that was visibly
        // moving. Riding the chain, the body must actually carry most of the
        // floor's speed.
        float sumVy = 0f; int nVy = 0;
        for (int f = requestFrame + 3; f <= chainEnd && f < samples.Count; f++)
        { sumVy += samples[f].VelY; nVy++; }
        float meanVy = sumVy / nVy;
        output.WriteLine($"mean vy through chain: {meanVy:F1} (floor {-SproutRiseSpeed:F0})");
        Assert.True(meanVy < -0.6f * SproutRiseSpeed,
            $"body should carry the floor's velocity (~{-SproutRiseSpeed:F0}); mean vy was {meanVy:F1} — positional extrusion, not a ride");

        // THE JITTER ITSELF: per-frame advance is EVEN through the steady ride.
        // The old absolute gates dropped the gravity hold ("launched"), so vy
        // bled g·dt every frame until penetration re-snapped it to the floor's
        // speed — a 4-frame sawtooth whose displacement lurched between ~1.3px
        // and ~2.8px frame to frame. With support engaged in the floor's frame
        // the advance varies by hundredths of a pixel. Window skips the
        // spin-up and the final block's release tail.
        float prevDy = 0f, maxDdy = 0f; bool haveDy = false;
        for (int f = requestFrame + 8; f < chainEnd - 1 && f < samples.Count; f++)
        {
            float dy = samples[f].PosY - samples[f - 1].PosY;
            if (haveDy) maxDdy = MathF.Max(maxDdy, MathF.Abs(dy - prevDy));
            prevDy = dy; haveDy = true;
        }
        output.WriteLine($"max frame-to-frame Δy variation in steady ride: {maxDdy:F3}px");
        // Measured: 1.50px under the old absolute gates (the sawtooth, every
        // ~4 frames), 0.82px with surface-relative support (one residual
        // catch-up per block hand-off). The bar sits between them; if hand-off
        // smoothing improves further, tighten it.
        Assert.True(maxDdy < 1.1f,
            $"per-frame advance lurched by {maxDdy:F2}px during the steady ride — the gravity-fight sawtooth");

        // Net lift: the full three tiles.
        float lift = samples[requestFrame - 1].PosY - samples[^1].PosY;
        output.WriteLine($"net lift {lift:F2} (expected ~{3f * Ts})");
        Assert.True(MathF.Abs(lift - 3f * Ts) < 3f, $"expected ~{3f * Ts}px lift, got {lift:F2}");

        // A purely vertical lift is STANDING's regime — TerrainCarriedState
        // (the multi-directional carry) must not steal the elevator.
        for (int f = requestFrame; f <= chainEnd && f < samples.Count; f++)
            Assert.NotEqual("TerrainCarriedState", samples[f].State);
    }

    // ── 1c. Diagonal push: mass carries the player in its direction ──────────
    // A corner cell with solid below AND solid left grows both faces at once —
    // one volume rises into the player's feet, one sweeps rightward into their
    // side. The aggregate push points up-right, and the player should TRAVEL
    // that way (TerrainCarriedState), not just rise while station friction
    // quietly eats the horizontal half.
    [Fact]
    public void DiagonalGrowth_CarriesPlayerUpAndRight()
    {
        // Floor at row 20; a two-high step on the left through column 9. The
        // corner cell (10,19) then has parents below and to the left.
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            for (int c = 0; c < 20; c++)
                sb.Append(r >= FloorRow || (c <= 9 && r >= FloorRow - 2) ? 'X' : 'O');
            sb.Append('\n');
        }
        var terrain = SimTerrain.FromAscii(sb.ToString());

        // Standing on the floor, body overlapping column 10 (left edge near the
        // step face) so both growing volumes reach it.
        var player = new PlayerCharacter(new Vector2(
            10 * Ts + Ts * 0.7f, FloorTopY - RestOffset));

        const int requestFrame = 20;
        TileSproutNode sprout = null;
        var samples = Run(terrain, player, 120, null, (f, t) =>
        {
            if (f != requestFrame) return;
            // A wave: the corner cell (below+left faces — up + right push at
            // the feet), a torso-height cell off the step (left face) that
            // keeps sweeping as the body lifts, and the floor rising across
            // the next two columns so the up-push FOLLOWS the body as it is
            // carried right. Net mass direction: up-and-right.
            sprout = t.TryRequestTile(10, FloorRow - 1, TileType.Stone);
            t.TryRequestTile(10, FloorRow - 2, TileType.Stone);
            t.TryRequestTile(11, FloorRow - 1, TileType.Stone);
            t.TryRequestTile(12, FloorRow - 1, TileType.Stone);
        });
        Assert.NotNull(sprout);
        output.WriteLine($"sprout faces: {sprout.Faces}");

        for (int f = requestFrame - 1; f < requestFrame + 30; f++)
            output.WriteLine($"  f{samples[f].Frame,3}  x {samples[f].PosX,8:F3}  y {samples[f].PosY,8:F3}  " +
                             $"vx {samples[f].VelX,7:F1}  vy {samples[f].VelY,7:F1}  {samples[f].State} {samples[f].Cons}");

        // The carry state actually classified the push.
        bool sawCarried = false;
        for (int f = requestFrame; f < requestFrame + 12 && f < samples.Count; f++)
            sawCarried |= samples[f].State == "TerrainCarriedState";
        Assert.True(sawCarried, "the diagonal push never entered TerrainCarriedState");

        // The player TRAVELLED with the mass: meaningful rightward displacement
        // (the sweeping face covers ~a tile; friction-free carry keeps most of
        // it), and the lift arrived too.
        float dx = samples[^1].PosX - samples[requestFrame - 1].PosX;
        float dy = samples[requestFrame - 1].PosY - samples[^1].PosY;
        output.WriteLine($"net displacement: dx {dx:F2} (right), dy {dy:F2} (up)");
        Assert.True(dx > 6f, $"player should be carried rightward with the mass; dx was only {dx:F2}px");
        Assert.True(dy > Ts * 0.6f, $"player should also be lifted; dy was {dy:F2}px");

        // And the ride hands back to normal locomotion when the push ends.
        Assert.True(samples[^1].State is "StandingState" or "CrouchedState",
            $"expected a quiet stand at the end, got {samples[^1].State}");
    }

    // ── 1d. Multi-face sprouts move as ONE diagonal square ───────────────────
    // A diagonally growing cell (parents below + left) used to emit two
    // axis-aligned volumes; a body riding on TOP only ever touched the
    // upward-moving one and was carried straight up, out of the stream. The
    // combined volume translates from the summed parent offset with the true
    // crest velocity, so the top face itself carries the diagonal.
    [Fact]
    public void DiagonalSprout_MovesAsOneDiagonalSquare()
    {
        var n = new TileSproutNode(new Point(0, 0), 5, 5, 5, 5);
        n.PromoteToGrowing(SproutFaces.Below | SproutFaces.Left, lifetime: 0.1f);

        float speed = Ts / 0.1f;
        foreach (var face in TileSproutNode.FaceOrder)
        {
            if ((n.Faces & face) == 0) continue;
            // Every set face reports the SAME combined volume.
            Assert.Equal(new Vector2(speed, -speed), n.VolumeVelocity(face));
            Assert.Equal(n.CellCenter + new Vector2(-Ts, Ts), n.VolumeStart(face));
        }

        // Single-face geometry is untouched.
        var single = new TileSproutNode(new Point(0, 0), 5, 5, 5, 5);
        single.PromoteToGrowing(SproutFaces.Below, lifetime: 0.1f);
        Assert.Equal(new Vector2(0f, -speed), single.VolumeVelocity(SproutFaces.Below));

        // Opposed faces cancel the sum — degenerate multi-face sprouts fall
        // back to the symmetric per-face volumes.
        var squeezed = new TileSproutNode(new Point(0, 0), 5, 5, 5, 5);
        squeezed.PromoteToGrowing(SproutFaces.Below | SproutFaces.Above, lifetime: 0.1f);
        Assert.Equal(new Vector2(0f, -speed), squeezed.VolumeVelocity(SproutFaces.Below));
        Assert.Equal(new Vector2(0f,  speed), squeezed.VolumeVelocity(SproutFaces.Above));
    }

    // ── 1e. The carry reads full surface velocity, one count per mover ───────
    [Fact]
    public void AggregateCarry_FullVelocity_OncePerMover()
    {
        var body = new PlayerCharacter(new Vector2(50f, 50f)).Body;
        var diag = new Vector2(110f, -110f);

        // One diagonal square touched on its top: the carry is the FULL motion,
        // not just the vertical component the normal sees.
        body.Constraints.Add(new SurfaceDistance(new Vector2(50f, 60f), new Vector2(0f, -1f), 0.5f)
                             { SurfaceVelocity = diag });
        Assert.Equal(diag, TerrainCarriedState.AggregateCarry(body));

        // The same square also touched on its side: same mover, counted once.
        body.Constraints.Add(new SurfaceDistance(new Vector2(40f, 50f), new Vector2(1f, 0f), 0.5f)
                             { SurfaceVelocity = diag });
        Assert.Equal(diag, TerrainCarriedState.AggregateCarry(body));

        // A genuinely distinct mover still sums; a static floor adds nothing.
        body.Constraints.Add(new SurfaceDistance(new Vector2(50f, 40f), new Vector2(0f, 1f), 0.5f)
                             { SurfaceVelocity = new Vector2(0f, 60f) });
        body.Constraints.Add(new SurfaceDistance(new Vector2(50f, 60f), new Vector2(0f, -1f), 0.5f));
        Assert.Equal(diag + new Vector2(0f, 60f), TerrainCarriedState.AggregateCarry(body));
    }

    // ── 1e2. One diagonal block beside the player: the ride is SMOOTH ────────
    // The anchor-servo law's contract for the simplest case: a single
    // multi-face cell grows diagonally into a standing player. The carry must
    // read as one motion — per-frame displacement varies by at most a couple
    // of pixels (no cliff-gain twitching, no controller fights) once the
    // block has caught the body.
    [Fact]
    public void SingleDiagonalBlock_CarriesSmoothly()
    {
        var sb = new StringBuilder();
        for (int r = 0; r < 25; r++)
        {
            for (int c = 0; c < 20; c++)
                sb.Append(r >= FloorRow || (c <= 9 && r >= FloorRow - 2) ? 'X' : 'O');
            sb.Append('\n');
        }
        var terrain = SimTerrain.FromAscii(sb.ToString());
        var player = new PlayerCharacter(new Vector2(10 * Ts + Ts * 0.7f, FloorTopY - RestOffset));

        const int requestFrame = 20;
        var samples = Run(terrain, player, 90, null, (f, t) =>
        {
            if (f == requestFrame)
                Assert.NotNull(t.TryRequestTile(10, FloorRow - 1, TileType.Stone));
        });

        for (int f = requestFrame - 1; f < requestFrame + 20; f++)
            output.WriteLine($"  f{samples[f].Frame,3}  x {samples[f].PosX,7:F2}  y {samples[f].PosY,7:F2}  " +
                             $"vx {samples[f].VelX,6:F1}  vy {samples[f].VelY,6:F1}  {samples[f].State}");

        // Jerk bound over the interaction: after the catch (2 frames in),
        // through the block's growth and the settle.
        float maxDd = 0f;
        Vector2 prevD = Vector2.Zero; bool have = false;
        for (int f = requestFrame + 3; f < requestFrame + 40; f++)
        {
            var d = new Vector2(samples[f].PosX - samples[f - 1].PosX,
                                samples[f].PosY - samples[f - 1].PosY);
            if (have) maxDd = MathF.Max(maxDd, (d - prevD).Length());
            prevD = d; have = true;
        }
        output.WriteLine($"max frame-to-frame Δdisplacement: {maxDd:F2}px");
        Assert.True(maxDd < 2.5f,
            $"the single-block carry lurched by {maxDd:F2}px frame-to-frame — controller fight or cliff gain");

        // And the block actually moved the player up-right.
        float dx = samples[^1].PosX - samples[requestFrame].PosX;
        float dy = samples[requestFrame].PosY - samples[^1].PosY;
        output.WriteLine($"net dx {dx:F1}, dy {dy:F1}");
        Assert.True(dx > 4f && dy > -1f, $"expected an up-right displacement, got dx {dx:F1}, dy {dy:F1}");
    }

    // ── 1f. MACRO: a diagonal eruption stream carries the player ~20 tiles ───
    // The whole feature end to end. A 2-wide diagonal bar of pending sprouts is
    // requested in one frame; the promotion cascade then sweeps it up-right one
    // slice per SproutLifetime on its own — every head cell has its below AND
    // left parents completed by the previous slice, so the front is exactly the
    // multi-face diagonal growth block eruptions produce. The player starts
    // standing at the bar's origin, gives NO input, and must surf the head the
    // whole way: scooped by each new cell's combined diagonal volume, ~20 tiles
    // up and ~20 tiles right, alive, and standing on the pile when it tops out.
    [Fact]
    public void DiagonalEruptionStream_CarriesPlayerTwentyTilesUpAndRight()
    {
        const int Cols = 50, Rows = 46, Floor = 40, C = 10, Reach = 22;
        // Pre-solid landing platform at summit height, separated from the bar
        // by a 2-column gap the exit momentum clears. It must NOT touch the
        // pending sprout set: any solid neighbor promotes pending cells
        // immediately, which grows a SECOND wavefront backward from the far
        // end — the two fronts meet mid-bar and crush the rider between them.
        // (A growing plateau is no landing either: a growing shelf CONVEYS a
        // rider off its far edge, however long it is.)
        const int PadCol = C + Reach + 2;   // 1-col gap: non-adjacent to pending cells, within the exit arc
        int padTopRow = Floor - Reach - 1;
        var sb = new StringBuilder();
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Cols; c++)
                sb.Append(r >= Floor || (c >= PadCol && r >= padTopRow && r <= padTopRow + 2) ? 'X' : 'O');
            sb.Append('\n');
        }
        var terrain = SimTerrain.FromAscii(sb.ToString());
        // Spawn ON the boundary between the bar's first two columns: a rider
        // straddling the boundary is reachable by the trailing column's lateral
        // growth (each lateral arrives one cascade-slice before the cell
        // beneath the rider), which is what keeps the diagonal push flowing.
        var player = new PlayerCharacter(new Vector2((C + 1) * Ts, Floor * Ts - RestOffset));

        const int requestFrame = 30;
        int requested = 0;
        // ~0.1s per slice, 2·Reach slices, plus settle (the ride ends with an
        // honest momentum launch off the summit — give it room to land).
        int growFrames = (int)MathF.Ceiling(2f * Reach * MovementConfig.Current.SproutLifetime / Dt);
        int frames = requestFrame + growFrames + 180;

        var samples = Run(terrain, player, frames, null, (f, t) =>
        {
            if (f != requestFrame) return;
            for (int x = 0; x <= Reach; x++)
            for (int up = 0; up <= Reach + 2; up++)
            {
                // Bar lanes: up ∈ {x, x+1, x+2} — the crest runs two cells
                // ABOVE the diagonal, so the wave overtakes the rider from
                // behind-left and its growth reaches the hovering body instead
                // of grazing under the feet. The middle lane's cells have both
                // below and left parents → the multi-face diagonal front.
                int diff = x - up;
                if (diff < -2 || diff > 0) continue;
                if (t.TryRequestTile(C + x, Floor - 1 - up, TileType.Stone) != null)
                    requested++;
            }
        });
        Assert.True(requested > 40, $"bar construction failed — only {requested} sprouts requested");

        float dx = samples[^1].PosX - samples[requestFrame - 1].PosX;
        float dy = samples[requestFrame - 1].PosY - samples[^1].PosY;
        // The diagonal claim is judged at the ride's PEAK: after the summit,
        // the growing plateau legitimately conveys the rider further right.
        float peakY = float.MaxValue, dxAtPeak = 0f;
        foreach (var s in samples)
            if (s.PosY < peakY) { peakY = s.PosY; dxAtPeak = s.PosX - samples[requestFrame - 1].PosX; }
        float dyPeak = samples[requestFrame - 1].PosY - peakY;
        int carriedFrames = 0;
        foreach (var s in samples) if (s.State == "TerrainCarriedState") carriedFrames++;
        output.WriteLine($"net ride: dx {dx / Ts:F1} tiles right, dy {dy / Ts:F1} tiles up; " +
                         $"carried {carriedFrames} frames; final state {samples[^1].State}, HP {player.Health:F1}");
        // Sparse trace for diagnosis: one line every 12 frames (denser + with
        // contacts through the ride window).
        for (int f = requestFrame; f < samples.Count; f += f < requestFrame + 60 ? 3 : 12)
            output.WriteLine($"  f{samples[f].Frame,3}  x {samples[f].PosX,7:F1}  y {samples[f].PosY,7:F1}  " +
                             $"vx {samples[f].VelX,7:F1}  vy {samples[f].VelY,7:F1}  {samples[f].State} {samples[f].Cons}");

        output.WriteLine($"peak: dy {dyPeak / Ts:F1} tiles up at dx {dxAtPeak / Ts:F1}");
        Assert.True(dx >= 17f * Ts, $"player should be carried ~{Reach} tiles right, got {dx / Ts:F1}");
        Assert.True(dy >= 15f * Ts,
            $"player should END high on the pile (wall-backed plateau), got {dy / Ts:F1} tiles up");
        Assert.True(dyPeak >= 17f * Ts, $"the ride should crest ~{Reach} tiles up, peaked at {dyPeak / Ts:F1}");
        Assert.True(MathF.Abs(dxAtPeak - dyPeak) <= 6f * Ts,
            $"the ride to the crest should be roughly diagonal (dx {dxAtPeak / Ts:F1} vs dy {dyPeak / Ts:F1} tiles)");
        Assert.True(carriedFrames >= 20,
            $"TerrainCarriedState should classify much of the ride (saw {carriedFrames} frames)");
        // ANTI-JITTER: the ride is ONE classification, not a flicker. The
        // nearby-moving-mass query keeps the state alive between scoops, so
        // the whole stream should be at most a couple of carried runs —
        // state flapping is poison for animation and gamestate reasoning.
        int carriedRuns = 0;
        for (int f = 1; f < samples.Count; f++)
            if (samples[f].State == "TerrainCarriedState" && samples[f - 1].State != "TerrainCarriedState")
                carriedRuns++;
        output.WriteLine($"carried runs: {carriedRuns}");
        Assert.True(carriedRuns <= 3,
            $"the ride flickered in and out of TerrainCarriedState {carriedRuns} times — state jitter");
        Assert.True(player.Health > 0f, "the ride must not crush the player to death");
        Assert.True(samples[^1].State is "StandingState" or "CrouchedState",
            $"expected a quiet stand on the pile top, got {samples[^1].State}");
    }

    // ── 2. Jumping mid-lift inherits the floor's upward velocity ─────────────
    // Same jump input with and without a sprout growing underfoot: the rising
    // floor's ~110 px/s must add to the launch (JumpStates set vy relative to
    // the source surface — this pins that the source actually carries the
    // sprout's velocity through GroundChecker, and that standing's support
    // didn't strip the ride before the jump fired).
    [Fact]
    public void JumpOffRisingSprout_InheritsFloorVelocity()
    {
        const int requestFrame = 20;
        const int jumpFrame    = 22;   // mid-growth (growth spans ~6 frames at 1/60)

        List<FrameSample> RunJump(bool withSprout)
        {
            var terrain = WidePlatform();
            var player = SpawnStanding();
            return Run(terrain, player, 60,
                f => new PlayerInput { Space = f >= jumpFrame && f < jumpFrame + 6 },
                (f, t) =>
                {
                    if (withSprout && f == requestFrame)
                        Assert.NotNull(t.TryRequestTile(PlayerCol, FloorRow - 1, TileType.Stone));
                });
        }

        var flat  = RunJump(withSprout: false);
        var lift  = RunJump(withSprout: true);

        float MinVy(List<FrameSample> s)
        {
            float min = float.MaxValue;
            for (int f = jumpFrame; f < jumpFrame + 8; f++) min = MathF.Min(min, s[f].VelY);
            return min;
        }
        float flatVy = MinVy(flat), liftVy = MinVy(lift);
        output.WriteLine($"launch vy: flat {flatVy:F1}, off rising sprout {liftVy:F1} " +
                         $"(floor rises at {-SproutRiseSpeed:F0})");

        // The lift jump should carry most of the floor's velocity on top of the
        // normal launch. Slack allows a frame of gravity and gate timing.
        Assert.True(liftVy < flatVy - SproutRiseSpeed * 0.6f,
            $"jump off a rising floor should inherit its velocity: flat {flatVy:F1} vs lift {liftVy:F1}");

        // And both jumps actually left the ground (sanity).
        Assert.True(flatVy < -50f, $"control jump never launched (vy {flatVy:F1})");
    }
}
