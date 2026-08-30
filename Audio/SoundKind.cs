using System;
using System.Text;

namespace MTile;

// The logical sound vocabulary. One entry per *sound*, not per file — variants
// (tile_break_01..06) live under a single kind and are chosen round-robin by the mixer.
//
// Adding a sound is two steps: an entry here, and clips named <stem>_01.ogg.. in
// Assets/Sounds (then `pwsh scripts/sync-sounds.ps1`). The stem is derived from the
// enum name, so there is no second list to keep in sync — HitConnect is "hit_connect".
// A kind with no clips on disk is silent, not an error — the whole bank is optional.
//
// The acquisition list these track is Plans/AUDIO_ASSET_LIST.md.
public enum SoundKind
{
    None = 0,

    // Dev: a generated 440 Hz tone, committed so the mixer can be verified by ear
    // (and pan/pitch checked on the web backend) before any real clip exists.
    DevTone,

    // Tier 1 — the vertical slice.
    TileBreak,
    TilePlace,
    WallScrape,     // loop
    Land,
    Footstep,
    HitConnect,
    Eruption,
    Jump,
    DoubleJump,
    Grunt,        // climb effort
    Throw,        // built, not yet wired — see GameAudio
    Swing,        // attack whoosh — see GameAudio.Swing
    LaserBlast,   // the laser's burn, one shot per firing — see GameAudio.Laser

    // Tier 3, but already flowing through the presentation seam.
    Respawn,
}

public static class SoundKinds
{
    public const int Count = (int)SoundKind.Respawn + 1;

    // Stems, derived once from the enum names: PascalCase -> snake_case. This used to be
    // a hand-maintained parallel array, which silently mis-mapped every kind after the
    // first insertion into the middle of the enum. Deriving them makes that impossible.
    private static readonly string[] _stems = BuildStems();

    private static string[] BuildStems()
    {
        var names = Enum.GetNames(typeof(SoundKind));
        var stems = new string[names.Length];
        var sb = new StringBuilder(24);
        for (int i = 0; i < names.Length; i++)
        {
            sb.Clear();
            string n = names[i];
            for (int c = 0; c < n.Length; c++)
            {
                if (char.IsUpper(n[c]) && c > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(n[c]));
            }
            stems[i] = sb.ToString();
        }
        stems[(int)SoundKind.None] = "";
        return stems;
    }

    public static string Stem(SoundKind k) => _stems[(int)k];

    // Clip fallback: a kind with no clips of its own borrows another's. Land reuses the
    // footsteps (played louder at the call site) — a landing is a heavier version of the
    // same contact, and sharing avoids two copies of the same audio on disk. Cutting a
    // real land_01.ogg later takes over automatically, with no code change.
    public static SoundKind Fallback(SoundKind k) => k switch
    {
        SoundKind.Land => SoundKind.Footstep,
        _              => SoundKind.None,
    };

    // True for kinds authored as seamless loops (level-triggered — AUDIO_PLAN.md §2).
    // Drives IsLooped on the voice; a one-shot retires itself, a loop is retired by
    // the frame it stops being sustained.
    public static bool IsLoop(SoundKind k) => k == SoundKind.WallScrape;
}
