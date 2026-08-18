using System;

namespace MTile;

// Identity of a *voice*, not of a clip. Must be a pure function of sim state —
// AUDIO_PLAN.md §3. Two frames that describe the same ongoing sound must produce the
// same SoundId, because that identity is the only thing telling the mixer "this is the
// scrape that was already playing" rather than "start a second scrape".
//
// A and B are kind-specific and deliberately untyped: tile coords for TileBreak,
// player index for WallScrape/Jump/Land, HitId for HitConnect. Never a counter, never
// allocation order, never anything the audio layer remembers between frames.
public readonly struct SoundId : IEquatable<SoundId>
{
    public readonly SoundKind Kind;
    public readonly int A;
    public readonly int B;

    public SoundId(SoundKind kind, int a = 0, int b = 0) { Kind = kind; A = a; B = b; }

    public static SoundId ForPlayer(SoundKind kind, int playerIndex) => new(kind, playerIndex);
    public static SoundId ForTile(SoundKind kind, int gtx, int gty)  => new(kind, gtx, gty);

    public bool Equals(SoundId o) => Kind == o.Kind && A == o.A && B == o.B;
    public override bool Equals(object o) => o is SoundId s && Equals(s);
    public override int GetHashCode() => ((int)Kind * 397 ^ A) * 397 ^ B;
    public override string ToString() => $"{Kind}({A},{B})";

    // Deterministic variant pick. Derived from the id so a replayed event chooses the
    // same clip it did the first time — a rollback must not change which variant plays.
    public int VariantSeed => (A * 73856093) ^ (B * 19349663) ^ ((int)Kind * 83492791);
}
