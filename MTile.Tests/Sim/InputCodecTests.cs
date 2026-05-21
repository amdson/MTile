using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;

namespace MTile.Tests;

// GGPO_PLAN §E — the wire codec. Round-trips InputPackets through bytes so the
// SIPSorcery (desktop) and JS (browser) transports can share one format. Every button
// bit and the world cursor must survive; the screen MousePosition must NOT (it's never
// sent), and malformed buffers must be rejected rather than throw.
public class InputCodecTests
{
    private static PlayerInput Sample(int seed) => new()
    {
        Left       = (seed & 1)  != 0,
        Right      = (seed & 2)  != 0,
        Up         = (seed & 4)  != 0,
        Down       = (seed & 8)  != 0,
        LeftClick  = (seed & 16) != 0,
        RightClick = (seed & 32) != 0,
        Space      = (seed & 64) != 0,
        Shift      = (seed & 128) != 0,
        F          = (seed & 256) != 0,
        P          = (seed & 512) != 0,
        Num1       = (seed & 1024) != 0,
        Num2       = (seed & 2048) != 0,
        Num3       = (seed & 4096) != 0,
        Num4       = (seed & 8192) != 0,
        MouseWorldPosition = new Vector2(12.5f + seed, -7.25f - seed),
        MousePosition      = new Point(seed, seed * 2),   // must be dropped on the wire
    };

    [Fact]
    public void RoundTrips_AllFieldsAndWindow()
    {
        var inputs = new PlayerInput[8];
        for (int i = 0; i < inputs.Length; i++) inputs[i] = Sample(i * 1019 + 3);

        var packet = new InputPacket
        {
            Player        = 1,
            FirstFrame    = 12345,
            ChecksumFrame = 12340,
            Checksum      = 0xDEADBEEFCAFEF00DUL,
            Inputs        = inputs,
        };

        var bytes = InputCodec.Encode(packet);
        Assert.Equal(InputCodec.HeaderBytes + inputs.Length * InputCodec.InputBytes, bytes.Length);

        Assert.True(InputCodec.TryDecode(bytes, out var back));
        Assert.Equal(packet.Player, back.Player);
        Assert.Equal(packet.FirstFrame, back.FirstFrame);
        Assert.Equal(packet.ChecksumFrame, back.ChecksumFrame);
        Assert.Equal(packet.Checksum, back.Checksum);
        Assert.Equal(inputs.Length, back.Inputs.Length);

        for (int i = 0; i < inputs.Length; i++)
        {
            Assert.True(InputCompare.Equal(inputs[i], back.Inputs[i]), $"input {i} button/world mismatch");
            // Screen position is not on the wire — decode leaves it at default.
            Assert.Equal(Point.Zero, back.Inputs[i].MousePosition);
        }
    }

    [Fact]
    public void Decode_RejectsTruncatedAndMalformed()
    {
        var packet = new InputPacket
        {
            Player = 0, FirstFrame = 1, ChecksumFrame = -1, Checksum = 0,
            Inputs = new[] { Sample(1), Sample(2) },
        };
        var bytes = InputCodec.Encode(packet);

        Assert.False(InputCodec.TryDecode(null, out _));
        Assert.False(InputCodec.TryDecode(new byte[3], out _));            // shorter than header
        Assert.False(InputCodec.TryDecode(bytes[..^1], out _));            // count says 2, body short
    }

    [Fact]
    public void EmptyWindow_RoundTrips()
    {
        var packet = new InputPacket { Player = 0, FirstFrame = 0, ChecksumFrame = -1, Inputs = new PlayerInput[0] };
        var bytes = InputCodec.Encode(packet);
        Assert.Equal(InputCodec.HeaderBytes, bytes.Length);
        Assert.True(InputCodec.TryDecode(bytes, out var back));
        Assert.Empty(back.Inputs);
        Assert.Equal(-1, back.ChecksumFrame);
    }
}
