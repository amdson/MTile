namespace MTile.Net;

// Fixed-length ring of SimSnapshots keyed by absolute frame (mod length). Stores the
// state captured at the *start* of each frame, so a rollback to frame F restores
// snapshots[F] and replays F..current. (Reference: gamestate_buffer[60].)
//
// As with InputRing, each slot records its frame so a reused index can't be mistaken
// for a frame that's already aged out of the window.
public sealed class SnapshotRing
{
    private readonly SimSnapshot[] _ring;
    private readonly int[]         _frames;
    public int Length { get; }

    public SnapshotRing(int length)
    {
        Length  = length;
        _ring   = new SimSnapshot[length];
        _frames = new int[length];
        for (int i = 0; i < length; i++) _frames[i] = -1;
    }

    private int Index(int frame) => ((frame % Length) + Length) % Length;

    public void Set(int frame, SimSnapshot snap)
    {
        int i      = Index(frame);
        _ring[i]   = snap;
        _frames[i] = frame;
    }

    public bool TryGet(int frame, out SimSnapshot snap)
    {
        int i = Index(frame);
        if (_frames[i] == frame) { snap = _ring[i]; return true; }
        snap = null;
        return false;
    }
}
