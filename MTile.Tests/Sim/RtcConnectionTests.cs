using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using MTile.Net;
using MTile.Rtc;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests;

// GGPO_PLAN stage 4 — proves the SIPSorcery transport actually works, fully in-process:
// two RtcConnections complete the offer/answer handshake over loopback (no STUN, host
// candidates) and round-trip an InputCodec-encoded InputPacket across the data channel.
// This is the desktop analogue of "open two browser tabs" — but headless and asserted.
public class RtcConnectionTests(ITestOutputHelper output)
{
    private static async Task<bool> WaitOrTimeout(Task t, int ms)
        => await Task.WhenAny(t, Task.Delay(ms)) == t;

    [Fact]
    public async Task TwoPeers_Connect_AndRoundTripInputPacket()
    {
        using var a = new RtcConnection();   // no STUN — loopback/host candidates suffice
        using var b = new RtcConnection();

        var received = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        b.OnBytes += bytes => received.TrySetResult(bytes);

        // Manual signaling, in-process: offer → answer → accept (the same blobs you'd
        // copy/paste or push through Firestore between two machines).
        string offer  = await a.CreateOfferAsync();
        string answer = await b.CreateAnswerAsync(offer);
        a.AcceptAnswer(answer);

        Assert.True(await WaitOrTimeout(Task.WhenAll(a.Opened, b.Opened), 20000),
            "data channels never opened (ICE/DTLS handshake failed)");

        var packet = new InputPacket
        {
            Player = 0, FirstFrame = 42, ChecksumFrame = 40, Checksum = 0x0123456789ABCDEFUL,
            Inputs = new[]
            {
                new PlayerInput { Right = true, MouseWorldPosition = new Vector2(1f, 2f) },
                new PlayerInput { LeftClick = true, Space = true, MouseWorldPosition = new Vector2(3f, 4f) },
            },
        };
        var bytes = InputCodec.Encode(packet);

        // The channel is unreliable+unordered, so resend until it lands (over loopback
        // the first or second try always does).
        for (int i = 0; i < 100 && !received.Task.IsCompleted; i++)
        {
            a.Send(bytes);
            await Task.WhenAny(received.Task, Task.Delay(50));
        }

        Assert.True(received.Task.IsCompleted, "peer received no datachannel message");
        Assert.True(InputCodec.TryDecode(received.Task.Result, out var got));
        Assert.Equal(packet.FirstFrame, got.FirstFrame);
        Assert.Equal(packet.ChecksumFrame, got.ChecksumFrame);
        Assert.Equal(packet.Checksum, got.Checksum);
        Assert.Equal(packet.Inputs.Length, got.Inputs.Length);
        for (int i = 0; i < packet.Inputs.Length; i++)
            Assert.True(InputCompare.Equal(packet.Inputs[i], got.Inputs[i]));

        output.WriteLine("WebRTC loopback: handshake completed and InputPacket round-tripped.");
    }
}
