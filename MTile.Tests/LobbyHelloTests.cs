using MTile.Net;
using Xunit;

namespace MTile.Tests;

// The lobby hello shares the data channel with InputPackets; its tag byte must never be
// mistaken for one in either direction.
public class LobbyHelloTests
{
    [Fact]
    public void HelloRoundTrips()
    {
        var bytes = LobbyHello.EncodeHello("depths", "abc123");
        Assert.True(LobbyHello.IsLobbyPacket(bytes));
        Assert.False(LobbyHello.IsAck(bytes));
        Assert.True(LobbyHello.TryDecodeHello(bytes, out var stage, out var build));
        Assert.Equal("depths", stage);
        Assert.Equal("abc123", build);
    }

    [Fact]
    public void AckIsLobbyPacketButNotHello()
    {
        var ack = LobbyHello.EncodeAck();
        Assert.True(LobbyHello.IsAck(ack));
        Assert.False(LobbyHello.TryDecodeHello(ack, out _, out _));
    }

    [Fact]
    public void InputPacketsAreNeverLobbyPackets()
    {
        for (int player = 0; player < 2; player++)
        {
            var pkt = new InputPacket { Player = player, Inputs = new PlayerInput[3] };
            Assert.False(LobbyHello.IsLobbyPacket(InputCodec.Encode(in pkt)));
        }
        // And a hello never decodes as an InputPacket of the right length by accident.
        Assert.False(InputCodec.TryDecode(LobbyHello.EncodeHello("arena", "x"), out _));
    }

    [Fact]
    public void BuildIdIsStable()
    {
        Assert.Equal(LobbyHello.BuildId, LobbyHello.BuildId);
        Assert.NotEqual("unknown", LobbyHello.BuildId);
    }
}
