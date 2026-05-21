namespace MTile.Net;

// Fixed-length ring of one player's per-frame inputs, indexed by absolute frame mod
// length. Each slot remembers which frame it holds so a stale slot (the same index
// reused BufferLen frames later) is never mistaken for the frame asked for. The
// `Imputed` flag marks a slot as a *prediction* (repeat-last) rather than a confirmed
// input — the rollback loop compares an arriving real input against an imputed slot to
// decide whether a misprediction happened. (Reference: local_input_buffer /
// remote_input_buffer, the `[frame, input, imputed]` triples.)
public sealed class InputRing
{
    public struct Slot
    {
        public int         Frame;     // absolute frame this slot holds (-1 = empty)
        public PlayerInput Input;
        public bool        Imputed;   // true = predicted, not yet confirmed by the owner
    }

    private readonly Slot[] _ring;
    public int Length { get; }

    public InputRing(int length)
    {
        Length = length;
        _ring  = new Slot[length];
        for (int i = 0; i < length; i++) _ring[i].Frame = -1;
    }

    private int Index(int frame) => ((frame % Length) + Length) % Length;

    // True iff the ring currently holds an entry for exactly this frame (confirmed or
    // imputed). A stale slot from an older frame at the same index reads as absent.
    public bool Has(int frame) => _ring[Index(frame)].Frame == frame;

    public bool TryGet(int frame, out Slot slot)
    {
        slot = _ring[Index(frame)];
        return slot.Frame == frame;
    }

    // Raw slot at the frame's index regardless of whether it matches — used only for
    // repeat-last prediction off the immediately preceding frame, which the caller has
    // just stepped (so it's guaranteed current). Returns default Input if empty.
    public PlayerInput InputAt(int frame)
    {
        var s = _ring[Index(frame)];
        return s.Frame == frame ? s.Input : default;
    }

    public void Set(int frame, in PlayerInput input, bool imputed)
    {
        ref var s = ref _ring[Index(frame)];
        s.Frame   = frame;
        s.Input   = input;
        s.Imputed = imputed;
    }

    public bool IsConfirmed(int frame)
    {
        var s = _ring[Index(frame)];
        return s.Frame == frame && !s.Imputed;
    }
}
