namespace MTile.Net;

// Source of the *second* player's input — the "remote" side of a 2-player match.
//
// This is the seam the GGPO plan (§G) hangs the offline/online split on. Stage 1
// uses a local BotInputSource standing in for a network peer — the spoof that lets
// us exercise a real two-player Simulation without any transport. Stage 5 swaps in a
// NetRemoteInput that pulls confirmed inputs off the WebRTC datachannel; nothing else
// in the loop has to change.
//
// Polled exactly once per *new* sim frame to produce that frame's input. The result
// is what would cross the wire / fill the rollback input buffer, so the source itself
// is never part of the sim snapshot — during a rollback we replay buffered inputs, we
// do NOT re-invoke the source. (Mirrors the reference's evil_AI(gamestate) call.)
public interface IRemoteInputSource
{
    PlayerInput Poll(Simulation sim, int frame);
}
