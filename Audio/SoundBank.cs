using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework.Audio;
using Microsoft.Xna.Framework.Content;

namespace MTile;

// Loads the clip set once at startup and hands the mixer a SoundEffect per (kind, seed).
//
// Everything here is optional by design. A kind with no clips on disk resolves to null
// and the mixer skips it, so the game runs identically with an empty Assets/Sounds —
// which is the state it ships in today. That also means a missing clip is never a crash
// during content work, only a silence, reported once at load.
public sealed class SoundBank
{
    private readonly SoundEffect[][] _byKind = new SoundEffect[SoundKinds.Count][];
    private readonly List<SoundKind> _missing = new();

    public int LoadedClips { get; private set; }
    public IReadOnlyList<SoundKind> MissingKinds => _missing;

    // Content is loaded through the pipeline (asset names "Sounds/<stem>[_NN]"), built
    // from Assets/Sounds/*.ogg by the entries scripts/sync-sounds.ps1 writes into
    // Content/Content.mgcb. Both toolchains have OggImporter + SoundEffectProcessor.
    public void Load(ContentManager content)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (stem, variants) in SoundManifest.Entries) counts[stem] = variants;

        for (int k = 1; k < SoundKinds.Count; k++)
        {
            var kind = (SoundKind)k;
            string stem = SoundKinds.Stem(kind);
            if (!counts.TryGetValue(stem, out int variants)) { _missing.Add(kind); continue; }

            var clips = new List<SoundEffect>();
            if (variants <= 0) TryLoad(content, $"Sounds/{stem}", clips);
            else for (int v = 1; v <= variants; v++) TryLoad(content, $"Sounds/{stem}_{v:00}", clips);

            if (clips.Count == 0) { _missing.Add(kind); continue; }
            _byKind[k] = clips.ToArray();
            LoadedClips += clips.Count;
        }
    }

    private void TryLoad(ContentManager content, string asset, List<SoundEffect> into)
    {
        try { into.Add(content.Load<SoundEffect>(asset)); }
        catch (ContentLoadException) { /* manifest is ahead of the built content — silent */ }
    }

    public bool Has(SoundKind kind) => Resolve(kind) != SoundKind.None;

    // The kind whose clips will actually sound for `kind` — itself, or its fallback.
    private SoundKind Resolve(SoundKind kind)
    {
        if (_byKind[(int)kind] != null) return kind;
        var fb = SoundKinds.Fallback(kind);
        return fb != SoundKind.None && _byKind[(int)fb] != null ? fb : SoundKind.None;
    }

    // Round-robin by a seed the caller derives from the SoundId, so the same event
    // picks the same variant on a replay (AUDIO_PLAN.md §3).
    public SoundEffect Pick(SoundKind kind, int seed)
    {
        var resolved = Resolve(kind);
        if (resolved == SoundKind.None) return null;
        var clips = _byKind[(int)resolved];
        int i = (seed & 0x7fffffff) % clips.Length;
        return clips[i];
    }

    public string Summary()
    {
        var silent = new List<string>();
        var borrowed = new List<string>();
        foreach (var k in _missing)
        {
            var fb = Resolve(k);
            if (fb == SoundKind.None) silent.Add(SoundKinds.Stem(k));
            else borrowed.Add($"{SoundKinds.Stem(k)}<-{SoundKinds.Stem(fb)}");
        }
        return $"audio: {LoadedClips} clip(s)"
             + (borrowed.Count > 0 ? $", borrowed: {string.Join(", ", borrowed)}" : "")
             + (silent.Count   > 0 ? $", no clips for: {string.Join(", ", silent)}" : "");
    }
}
