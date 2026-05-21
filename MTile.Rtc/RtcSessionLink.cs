using System;
using System.Collections.Concurrent;
using MTile.Net;

namespace MTile.Rtc;

// Wires a RollbackSession (transport-agnostic, in Core) to an RtcConnection. The
// session's outgoing packets are encoded and pushed onto the data channel; incoming
// datachannel bytes are decoded and fed back in.
//
// Threading: SIPSorcery raises OnBytes on a network thread, but the Simulation and the
// session's ring/inbox are single-threaded game-loop state. So arrivals are parked on a
// concurrent queue and drained by PumpInbox on the game thread, right before TryStep —
// nothing touches the session off-thread. Send runs inside TryStep (game thread), which
// is fine.
public sealed class RtcSessionLink
{
    private readonly ConcurrentQueue<byte[]> _incoming = new();

    public RollbackSession Session { get; }

    public RtcSessionLink(RtcConnection rtc, Simulation sim, int localPlayer,
                          Func<int, PlayerInput> pollLocal)
    {
        Session = new RollbackSession(sim, localPlayer, pollLocal,
            pkt => rtc.Send(InputCodec.Encode(in pkt)));
        rtc.OnBytes += bytes => _incoming.Enqueue(bytes);
    }

    // Drain network arrivals into the session. Call on the game thread before stepping.
    public void PumpInbox()
    {
        while (_incoming.TryDequeue(out var bytes))
            if (InputCodec.TryDecode(bytes, out var pkt))
                Session.Receive(in pkt);
    }
}
