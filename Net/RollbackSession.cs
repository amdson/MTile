using System;
using System.Collections.Generic;

namespace MTile.Net;

// The rollback loop for one peer (GGPO_PLAN §D), ported from rtc_netcode_reference.js
// with its two bugs fixed (we restore from the snapshot ring at the rollback frame and
// write the corrected timeline forward; the reference re-simulated into a throwaway).
//
// Owns a Simulation plus the per-player input rings, the snapshot ring, and the inbox
// of received packets. One sim sub-step is TryStep():
//   1. drain inbox → reconcile remote inputs, flag the earliest misprediction
//   2. if a misprediction was found: Restore(snapshot@F) and replay F..current
//   3. stall cap — don't outrun the peer past InputFrameDelay + StallSlack
//   4. predict the remote input for the frame about to run (repeat-last)
//   5. snapshot, then Step(p0, p1) with the delayed inputs
//   6. sample local input, schedule it InputFrameDelay frames ahead, buffer + send
//
// Input delay (step 6) hides up to InputFrameDelay frames of latency with NO rollback:
// the input sampled at completed-frame C isn't consumed until frame C + InputFrameDelay.
// Rollback only fires when a remote input arrives later than that.
//
// Determinism: TryStep advances exactly one fixed sim frame; the bot/keyboard is polled
// once per new frame (never during replay — replay reads buffered inputs). The session's
// frame counter stays in lockstep with the sim's internal frame across rollback because
// Restore reinstates the sim's frame from the snapshot.
public sealed class RollbackSession
{
    public const int BufferLen        = 60;
    public const int InputFrameDelay  = 3;
    public const int StallSlack       = 3;
    public const int RedundancyWindow = 8;

    private readonly Simulation _sim;
    private readonly int        _localPlayer;          // 0 or 1
    private readonly Func<int, PlayerInput> _pollLocal; // sampleFrame → this peer's input
    private readonly Action<InputPacket>    _send;

    private readonly InputRing    _local  = new(BufferLen);
    private readonly InputRing    _remote = new(BufferLen);
    private readonly SnapshotRing _snaps  = new(BufferLen);
    private readonly List<InputPacket> _inbox = new();

    // Per-frame state fingerprint for the desync guard (§F). Written after every step
    // (initial + replay), tagged by frame so a reused slot is recognised.
    private readonly ulong[] _checksums      = new ulong[BufferLen];
    private readonly int[]   _checksumFrames = new int[BufferLen];
    // Peer checksum claims awaiting a frame we've also confirmed (so the compare is
    // apples-to-apples). Drained by CheckPendingChecksums.
    private readonly List<(int Frame, ulong Checksum)> _pendingClaims = new();

    private int _frame;             // next frame to simulate (and the sim's current frame)
    private int _highestRemote;     // highest remote frame ever received
    private int _confirmedThrough;  // highest frame F with remote[0..F] all confirmed

    public int        Frame         => _frame;
    public int        RollbackCount { get; private set; }
    public int        DesyncCount   { get; private set; }
    public Simulation Sim           => _sim;
    // Highest frame whose state is final on our side: remote inputs confirmed AND the
    // frame has been stepped. Checksum comparisons only happen at/below this.
    private int ConfirmedFrame => Math.Min(_confirmedThrough, _frame - 1);
    // Fired when a peer's checksum claim disagrees with ours for a frame both have
    // confirmed: (frame, ourChecksum, theirChecksum). Hard desync — see §F.
    public Action<int, ulong, ulong> OnDesync;

    public RollbackSession(Simulation sim, int localPlayer,
                           Func<int, PlayerInput> pollLocal, Action<InputPacket> send)
    {
        _sim         = sim;
        _localPlayer = localPlayer;
        _pollLocal   = pollLocal;
        _send        = send;

        // The first InputFrameDelay frames consume inputs that were never scheduled
        // (sampling starts at frame 0 and schedules frame InputFrameDelay), so seed
        // both rings with confirmed defaults for the warm-up window. _highestRemote
        // covers those seeded remote frames so the stall cap lets the peers bootstrap.
        for (int f = 0; f < InputFrameDelay; f++)
        {
            _local .Set(f, default, imputed: false);
            _remote.Set(f, default, imputed: false);
        }
        _highestRemote    = InputFrameDelay - 1;
        _confirmedThrough = InputFrameDelay - 1;
        for (int i = 0; i < BufferLen; i++) _checksumFrames[i] = -1;
    }

    // Inbox: a received packet is queued and drained on the next TryStep. Receiving is
    // idempotent — re-confirming a frame with the same value is a no-op; a different
    // value flags a rollback.
    public void Receive(in InputPacket packet) => _inbox.Add(packet);

    // Steps 1–2 in isolation: drain the inbox, and if a stored prediction turned out
    // wrong, restore the snapshot at that frame and replay the corrected timeline back
    // up to the current frame. Does NOT advance past _frame. Exposed so a peer that has
    // reached its target frame can still absorb late arrivals and settle. Returns true
    // if a rollback occurred.
    public bool ProcessInbox()
    {
        int rollbackTo = ReconcileRemote();
        bool rolledBack = false;
        if (rollbackTo < _frame && _snaps.TryGet(rollbackTo, out var snap))
        {
            RollbackCount++;
            rolledBack = true;
            _sim.Restore(snap);
            for (int f = rollbackTo; f < _frame; f++)
            {
                _snaps.Set(f, _sim.Snapshot());
                _sim.Step(InputFor(0, f), InputFor(1, f));
                RecordChecksum(f);
            }
            // sim is now back at _frame with the corrected timeline.
        }
        // Our checksums for confirmed frames are now current → safe to compare.
        CheckPendingChecksums();
        return rolledBack;
    }

    private void RecordChecksum(int frame)
    {
        int idx = ((frame % BufferLen) + BufferLen) % BufferLen;
        _checksums[idx]      = _sim.Checksum();
        _checksumFrames[idx] = frame;
    }

    public bool InboxEmpty => _inbox.Count == 0;

    // Advance one fixed sim frame. Returns false if the stall cap held us back this
    // tick (no frame ran) — the caller should deliver more remote input and retry.
    public bool TryStep()
    {
        // 1–2. Reconcile arrivals and roll back if a prediction was wrong.
        ProcessInbox();

        // 3. Stall cap: never simulate more than InputFrameDelay + StallSlack frames
        //    ahead of the newest input we've heard from the peer.
        if (_frame >= _highestRemote + InputFrameDelay + StallSlack)
            return false;

        // 4. Predict the remote input for the frame about to run (repeat-last).
        EnsurePredicted(_remote, _frame);

        // 5. Snapshot the pre-step state, then advance one frame.
        _snaps.Set(_frame, _sim.Snapshot());
        _sim.Step(InputFor(0, _frame), InputFor(1, _frame));
        RecordChecksum(_frame);
        _frame++;

        // 6. Sample local input for the just-completed frame, schedule it
        //    InputFrameDelay frames into the future, buffer + send a redundancy window.
        int sampleFrame   = _frame - 1;
        int scheduleFrame = sampleFrame + InputFrameDelay;
        var localInput    = _pollLocal(sampleFrame);
        _local.Set(scheduleFrame, localInput, imputed: false);
        SendWindow(scheduleFrame);
        return true;
    }

    // Drain the inbox; store every in-window remote input. Returns the earliest frame
    // whose stored prediction turned out wrong (so the caller rolls back to it), or
    // int.MaxValue if every prediction held.
    private int ReconcileRemote()
    {
        int rollbackTo = int.MaxValue;
        foreach (var pkt in _inbox)
        {
            for (int i = 0; i < pkt.Inputs.Length; i++)
            {
                int frame = pkt.FirstFrame + i;
                if (frame > _highestRemote) _highestRemote = frame;

                if (frame >= _frame)
                {
                    // Not simulated yet — store it confirmed (no rollback). Skip frames
                    // so far ahead they'd alias an in-window slot (can't happen under
                    // the stall cap, but stay safe).
                    if (frame < _frame + BufferLen)
                        _remote.Set(frame, pkt.Inputs[i], imputed: false);
                }
                else if (frame > _frame - BufferLen)
                {
                    // Already simulated. If we'd predicted it (or never had it) and the
                    // real value differs, that frame must be re-simulated.
                    bool had = _remote.TryGet(frame, out var slot);
                    if (!had || !InputCompare.Equal(slot.Input, pkt.Inputs[i]))
                        rollbackTo = Math.Min(rollbackTo, frame);
                    _remote.Set(frame, pkt.Inputs[i], imputed: false);
                }
                // else: older than the buffer window — drop.
            }
            if (pkt.ChecksumFrame >= 0)
                _pendingClaims.Add((pkt.ChecksumFrame, pkt.Checksum));
        }
        _inbox.Clear();

        // Advance the contiguous-confirmed watermark (remote[0..N] all real).
        while (_remote.IsConfirmed(_confirmedThrough + 1)) _confirmedThrough++;

        // A misprediction is corrected by the caller's replay (which recomputes
        // checksums), so defer the desync comparison until after that — see the second
        // pass in ProcessInbox.
        return rollbackTo;
    }

    // Compare every buffered peer checksum claim against our own for that frame, once
    // we've confirmed it too. Called after any rollback replay so our checksum is current.
    private void CheckPendingChecksums()
    {
        for (int i = _pendingClaims.Count - 1; i >= 0; i--)
        {
            var (frame, theirs) = _pendingClaims[i];
            if (frame <= _frame - BufferLen) { _pendingClaims.RemoveAt(i); continue; } // too old to compare
            if (frame > ConfirmedFrame) continue;          // not yet confirmed on our side
            _pendingClaims.RemoveAt(i);
            int idx = ((frame % BufferLen) + BufferLen) % BufferLen;
            if (_checksumFrames[idx] != frame) continue;   // aged out of our window — skip
            ulong ours = _checksums[idx];
            if (ours != theirs)
            {
                DesyncCount++;
                OnDesync?.Invoke(frame, ours, theirs);
            }
        }
    }

    // Fill a missing slot with a repeat-last prediction. A slot already present
    // (confirmed OR previously imputed) is left untouched so we don't re-flag it.
    private static void EnsurePredicted(InputRing ring, int frame)
    {
        if (ring.Has(frame)) return;
        ring.Set(frame, ring.InputAt(frame - 1), imputed: true);
    }

    private PlayerInput InputFor(int player, int frame) =>
        (player == _localPlayer ? _local : _remote).InputAt(frame);

    private void SendWindow(int newestFrame)
    {
        int count = Math.Min(RedundancyWindow, newestFrame + 1);
        int first = newestFrame - count + 1;
        var window = new PlayerInput[count];
        for (int i = 0; i < count; i++) window[i] = _local.InputAt(first + i);

        // Piggyback a checksum for the newest fully-confirmed frame (if we have one
        // recorded). The peer compares it once it confirms that frame too.
        int csFrame = ConfirmedFrame;
        int csIdx   = ((csFrame % BufferLen) + BufferLen) % BufferLen;
        bool haveCs = csFrame >= 0 && _checksumFrames[csIdx] == csFrame;

        _send(new InputPacket
        {
            Player        = _localPlayer,
            FirstFrame    = first,
            Inputs        = window,
            ChecksumFrame = haveCs ? csFrame : -1,
            Checksum      = haveCs ? _checksums[csIdx] : 0UL,
        });
    }
}
