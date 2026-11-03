using System;
using System.Linq;
using Microsoft.Xna.Framework;
using MTile.Tests.Sim;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// BurstAction's pogo: an AIR burst aimed into DownAirSlash's bottom sextant bounces the
// attacker off a body it connects with, in the same band as the down-air
// (DownAirSlash.ApplyEntityPogo). Aimed sideways, the same connect must not lift you.
public class BurstPogoTests(ITestOutputHelper output)
{
    private const float Dt      = 1f / 60f;
    private const float Gravity = 600f;

    private static ChunkMap FlatGround(int openRows) => SimTerrain.FromAscii(
        string.Join("\n", Enumerable.Repeat(new string('O', 28), openRows).Append(new string('X', 28))),
        originTileX: 0, originTileY: 0);

    // LMB press (the wind-up picks it up), then — LMB still held — an RMB press edge.
    private static InputScript LmbThenRmb(Vector2 mouse, int lmbAt, int rmbAt) => new InputScript()
        .For(lmbAt,         new PlayerInput { MouseWorldPosition = mouse })
        .For(rmbAt - lmbAt, new PlayerInput { LeftClick = true, MouseWorldPosition = mouse })
        .Forever(           new PlayerInput { LeftClick = true, RightClick = true, MouseWorldPosition = mouse });

    private (float minVy, bool burst, bool connected) Run(Vector2 mouseOffset, float gapAbove, int lmbAt, int rmbAt)
    {
        var terrain  = FlatGround(12);
        float floorTop = 12 * Chunk.TileSize;
        var victim   = new Vector2(150f, floorTop - PlayerCharacter.Radius - MovementConfig.Current.FoldHoverOffset);
        var attacker = new Vector2(150f, victim.Y - gapAbove);

        var cfg = new SimConfigMulti
        {
            Terrain = terrain, Frames = 40, Dt = Dt, Gravity = new Vector2(0f, Gravity),
            Players = new[]
            {
                new SimPlayer { StartPosition = attacker,
                                Script = LmbThenRmb(attacker + mouseOffset, lmbAt, rmbAt) },
                new SimPlayer { StartPosition = victim, Faction = Faction.Neutral,
                                Script = InputScript.Always(default) },
            },
        };

        float minVy = float.MaxValue;
        bool burst = false, connected = false;
        SimRunner.RunMulti(cfg, onFrame: (f, ps) =>
        {
            if (ps[0].CurrentActionName == "BurstAction") burst = true;
            if (ps[1].Combat.HitstunActive) connected = true;
            if (burst) minVy = MathF.Min(minVy, ps[0].Body.Velocity.Y);
        });
        output.WriteLine($"min Vy = {minVy}, burst = {burst}, connected = {connected}");
        return (minVy, burst, connected);
    }

    [Fact]
    public void DownAimedAirBurst_OntoEntity_Pogos()
    {
        var (minVy, burst, connected) = Run(new Vector2(0f, 200f), gapAbove: 60f, lmbAt: 1, rmbAt: 4);
        Assert.True(burst, "LMB-hold then RMB should fire BurstAction.");
        Assert.True(connected, "The burst should have connected with the victim below.");
        Assert.True(minVy <= -195f, $"Expected a pogo of at least PogoSpeed; min Vy = {minVy}");
        Assert.True(minVy >= -300f, $"The pogo must stay inside PogoMaxSpeed; min Vy = {minVy}");
    }

    // Control: same fall, same wind-up, aimed level — the burst still fires but must not
    // lift the attacker (no recoil is requested outside the wedge).
    [Fact]
    public void SideAimedAirBurst_DoesNotPogo()
    {
        var (minVy, burst, _) = Run(new Vector2(200f, 0f), gapAbove: 60f, lmbAt: 1, rmbAt: 4);
        Assert.True(burst, "LMB-hold then RMB should fire BurstAction.");
        Assert.True(minVy > -100f, $"A sideways burst must not pogo; min Vy = {minVy}");
    }
}
