using System;
using System.Collections.Concurrent;

namespace MTile;

// The seam between Game1 (Core) and a transport (desktop MTile.Rtc, browser JS, …).
// Game1 owns the RollbackSession; the host owns the connection. They meet here:
//   • the host sets Send to the transport's byte sender and routes incoming bytes into
//     Deliver (raised on the transport's network thread — hence the concurrent queue);
//   • Game1 reads LocalPlayerIndex, drains TryReceive on the game thread, and sends via Send.
// This keeps Core free of any socket/WebRTC dependency while still driving a real match.
public sealed class NetSetup
{
    // 0 = host (drives player 1 / the primary), 1 = joiner (drives player 2).
    public int LocalPlayerIndex;

    // Push one encoded InputPacket onto the wire. Set by the host to the transport.
    public Action<byte[]> Send;

    private readonly ConcurrentQueue<byte[]> _incoming = new();

    // Called by the transport when bytes arrive — may be off the game thread.
    public void Deliver(byte[] bytes) => _incoming.Enqueue(bytes);

    // Drained by Game1 on the game thread before each step.
    public bool TryReceive(out byte[] bytes) => _incoming.TryDequeue(out bytes);
}
