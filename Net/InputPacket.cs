namespace MTile.Net;

// One datachannel message: a sender's recent inputs. Carries a small redundancy
// WINDOW of consecutive inputs ending at the newest scheduled frame (not just the
// newest), so over an unreliable channel a single dropped packet is covered by the
// next one (GGPO_PLAN §F). Inputs[i] is the input for frame FirstFrame + i.
//
// Stage 2 keeps this a plain struct passed in-process; Stage 4 adds the byte
// Encode/Decode (the ~10-byte packed form from §E) for the real WebRTC channel.
public struct InputPacket
{
    public int           Player;      // sender's player index (0 or 1) — informational
    public int           FirstFrame;  // absolute frame of Inputs[0]
    public PlayerInput[] Inputs;      // consecutive inputs, oldest → newest

    // Desync guard (GGPO_PLAN §F): the sender's Simulation.Checksum() for a frame it has
    // FULLY confirmed (every input for that frame is real, not predicted). The receiver
    // compares it against its own checksum for that frame once it too has confirmed it —
    // a mismatch with identical confirmed inputs means the two sims diverged. ChecksumFrame
    // = -1 when the sender has nothing confirmed yet.
    public int           ChecksumFrame;
    public ulong         Checksum;

    public int LastFrame => FirstFrame + Inputs.Length - 1;
}

public static class InputCompare
{
    // Field-wise equality. PlayerInput is a plain struct of bools + a Point + a world
    // Vector2; two inputs are "the same" when every field matches. Used by the
    // rollback loop to tell a correct prediction from a misprediction. MouseWorldPosition
    // compares by exact value — a peer sends the same bits it sampled, so an arriving
    // real input equals the imputed one only when they truly coincide.
    public static bool Equal(in PlayerInput a, in PlayerInput b) =>
        a.Left       == b.Left       &&
        a.Right      == b.Right      &&
        a.Up         == b.Up         &&
        a.Down       == b.Down       &&
        a.LeftClick  == b.LeftClick  &&
        a.RightClick == b.RightClick &&
        a.Space      == b.Space      &&
        a.Shift      == b.Shift      &&
        a.F          == b.F          &&
        a.P          == b.P          &&
        a.Num1       == b.Num1       &&
        a.Num2       == b.Num2       &&
        a.Num3       == b.Num3       &&
        a.Num4       == b.Num4       &&
        a.MouseWorldPosition == b.MouseWorldPosition;
}
