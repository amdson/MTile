using System;
using System.Threading.Tasks;
using MTile;
using MTile.Rtc;

// Entry point. Three modes:
//   (no args)        solo / offline play (the original behaviour)
//   host  [stun...]  create the offer, take the peer's answer, then play as player 1
//   join  [stun...]  take the host's offer, emit an answer, then play as player 2
//
// Signaling is manual copy/paste of base64 SDP blobs (offer ⇄ answer). Pass STUN
// server URLs as extra args for internet NAT traversal; the default Google STUN works
// for most setups, and LAN/loopback connects even without it.

string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "solo";

if (mode is "host" or "join")
{
    string[] stun = args.Length > 1 ? args[1..] : new[] { "stun:stun.l.google.com:19302" };
    using var rtc = new RtcConnection(stun);
    var net = new NetSetup { Send = rtc.Send };
    rtc.OnBytes += net.Deliver;

    try
    {
        if (mode == "host")
        {
            net.LocalPlayerIndex = 0;
            Console.WriteLine("Gathering connection info (a few seconds)…");
            string offer = await rtc.CreateOfferAsync();
            Console.WriteLine("\n================ OFFER (give this to the joiner) ================");
            Console.WriteLine(offer);
            Console.WriteLine("================================================================\n");
            Console.WriteLine("Paste the joiner's ANSWER on one line, then press Enter:");
            rtc.AcceptAnswer(ReadBlob());
        }
        else
        {
            net.LocalPlayerIndex = 1;
            Console.WriteLine("Paste the host's OFFER on one line, then press Enter:");
            string offer = ReadBlob();
            Console.WriteLine("Gathering connection info (a few seconds)…");
            string answer = await rtc.CreateAnswerAsync(offer);
            Console.WriteLine("\n=============== ANSWER (give this back to the host) ===============");
            Console.WriteLine(answer);
            Console.WriteLine("==================================================================\n");
        }

        Console.WriteLine("Connecting…");
        bool opened = await Task.WhenAny(rtc.Opened, Task.Delay(30000)) == rtc.Opened;
        if (!opened)
        {
            Console.WriteLine("Connection timed out — check the blobs were pasted intact / STUN reachable.");
            return;
        }

        Console.WriteLine($"Connected. You are player {net.LocalPlayerIndex + 1}. Launching…");
        using var game = new Game1(net);
        game.Run();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Networking error: {ex.Message}");
    }
    return;
}

using var solo = new Game1();
solo.Run();

// Read one line; blobs are single-line base64 so a paste + Enter is enough.
static string ReadBlob() => (Console.ReadLine() ?? "").Trim();
