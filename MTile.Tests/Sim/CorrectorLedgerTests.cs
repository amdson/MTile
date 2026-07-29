using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests.Sim;

// Force-ledger bookkeeping (CorrectorLedger): the applied solve's tick-0 output,
// broken down by channel (what each actuator exerted) and by contact (which tile
// the reaction came from — the block-breaking input). Unit-level oracle on a
// hand-built problem, plus an end-to-end landing scenario through the real
// ambient fold.
public class CorrectorLedgerTests
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Up = new(0f, -1f);

    [Fact]
    public void Record_SingleForceChannel_NamesChannelAndSourceCell()
    {
        // One force channel serving one hard row from tile (3, 7). The ledger
        // must name the channel, report its tick-0 force, and attribute the
        // contact to the source cell with an upward force on the body.
        const int H = 10, T = 9;
        const float m = 4f;
        var p = new CorrectionProblem
        {
            H = H, Dt = Dt,
            CoastVel = new Vector2[H],
            Rows = new[] { new ClearanceRow { Tick = T, Normal = Up, Depth = m, HingeScale = 1f,
                                              CellX = 3, CellY = 7, HasCell = true } },
            RowCount = 1,
            Channels = new[] { new ChannelDef { Id = CorrectionChannel.LegServo,
                Lever = LeverKind.Force, Weight = 1e-4f, Cap = 1e9f, ActiveFrom = 0, ActiveTo = H } },
            ChannelCount = 1,
            PrevApplied = new Vector2[1],
            DeltaWeight = 0f, HingeWeight = 1e6f,
        };
        var z = new Vector2[H];
        var rowPush = new Vector2[ClearanceConstraintBuilder.MaxEvents];
        p.RowPush = rowPush;
        CorrectionSolver.Solve(p, z, new Vector2[H]);

        var samples = new CoastSample[H];
        var ledger = new CorrectorLedger();
        ledger.Record(p, z, rowPush, samples, Dt);

        Assert.Equal(1, ledger.ChannelCount);
        Assert.Equal(CorrectionChannel.LegServo, ledger.Channels[0].Channel);
        Assert.Equal(z[0], ledger.Channels[0].Force);       // Force lever: z IS the force
        Assert.Equal(z[0], ledger.TotalForce);
        Assert.True(Vector2.Dot(ledger.TotalForce, Up) > 0f, "correction must push up");

        Assert.Equal(1, ledger.ContactCount);
        var contact = ledger.Contacts[0];
        Assert.True(contact.HasCell);
        Assert.Equal(3, contact.CellX);
        Assert.Equal(7, contact.CellY);
        Assert.Equal(Up, contact.Normal);
        // RowPush is uniform δv units → force = RowPush/dt, along the row normal.
        Assert.Equal(rowPush[0] / Dt, contact.Force);
        Assert.True(Vector2.Dot(contact.Force, Up) > 0f, "terrain reaction pushes the body up");
    }

    [Fact]
    public void Record_VelocityUpdateChannel_ReportsForceAsDvOverDt()
    {
        // A velocity-update δv reports as δv/dt so every ledger entry shares
        // force (acceleration) units.
        const int H = 6, T = 5;
        var p = new CorrectionProblem
        {
            H = H, Dt = Dt,
            CoastVel = new Vector2[H],
            Rows = new[] { new ClearanceRow { Tick = T, Normal = Up, Depth = 2f, HingeScale = 1f } },
            RowCount = 1,
            Channels = new[] { new ChannelDef { Id = CorrectionChannel.Redirect,
                Lever = LeverKind.VelocityUpdate, Weight = 1e-4f, Cap = 1e9f, ActiveFrom = 0, ActiveTo = 1 } },
            ChannelCount = 1,
            PrevApplied = new Vector2[1],
            DeltaWeight = 0f, HingeWeight = 1e6f,
        };
        var z = new Vector2[H];
        CorrectionSolver.Solve(p, z, new Vector2[H]);

        var ledger = new CorrectorLedger();
        ledger.Record(p, z, new Vector2[ClearanceConstraintBuilder.MaxEvents],
                      new CoastSample[H], Dt);

        Assert.Equal(1, ledger.ChannelCount);
        Assert.Equal(CorrectionChannel.Redirect, ledger.Channels[0].Channel);
        Assert.Equal(z[0] / Dt, ledger.Channels[0].Force);
        // Soft/no-cell row with zero recorded push is fine; the channel side is
        // the pinned contract here.
    }

    [Fact]
    public void Landing_LedgerAttributesReactionToFloorTiles()
    {
        // Drop the player onto a flat stone floor (top faces at gty = 10) and
        // watch the ledger through the landing: the ambient fold's catch must
        // record leg-family channels and at least one contact whose source cell
        // is a floor tile pushing the body UP.
        const int floorGty = 10;
        var terrain = SimTerrain.FromAscii(new string('X', 40) + "\n" + new string('X', 40),
                                           originTileX: 0, originTileY: floorGty);
        var start = new Vector2(20 * Chunk.TileSize, floorGty * Chunk.TileSize - 6f * PlayerCharacter.Radius);

        bool sawFloorContact = false;
        bool sawUpChannel    = false;
        var channelsSeen = new HashSet<CorrectionChannel>();
        SimRunner.RunMulti(new SimConfigMulti
        {
            Terrain = terrain,
            Players = new List<SimPlayer> { new() { StartPosition = start } },
            Frames  = 90,
            Dt      = Dt,
        }, onFrame: (f, players) =>
        {
            var ledger = players[0].ForceLedger;
            for (int c = 0; c < ledger.ChannelCount; c++)
            {
                channelsSeen.Add(ledger.Channels[c].Channel);
                if (ledger.Channels[c].Force.Y < -1f) sawUpChannel = true;
            }
            for (int j = 0; j < ledger.ContactCount; j++)
            {
                var contact = ledger.Contacts[j];
                if (!contact.HasCell) continue;
                Assert.True(terrain.GetCellState(contact.CellX, contact.CellY) == TileState.Solid,
                    $"frame {f}: ledger contact names empty cell ({contact.CellX},{contact.CellY})");
                if (contact.CellY == floorGty && contact.Force.Y < 0f) sawFloorContact = true;
            }
        });

        Assert.True(sawUpChannel, "some channel should have pushed up during the catch");
        Assert.Contains(CorrectionChannel.LegServo, channelsSeen);
        Assert.True(sawFloorContact,
            "the landing reaction should be attributed to a floor tile pushing the body up");
    }
}
