using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Xna.Framework.Audio;

namespace MTile;

// Live sound swapping for the soundboard (scripts/soundboard/). Desktop dev only.
//
// The soundboard renders every mapped event's clips to WAVs in Audio/dev_cache/ and then
// atomically rewrites Audio/dev_cache/manifest.json:
//
//   { "version": 12, "events": { "hit_connect": ["hit_connect_01.wav", ...], ... } }
//
// This watches that one file. A change only raises a flag (the watcher runs on a pool
// thread); Poll, called from the render loop, does the actual load and swap on the main
// thread, where SoundEffect creation is safe. Every event listed replaces its kind's
// content-loaded clips via SoundBank.SetOverride; an event mapped to [] goes silent (or
// borrows its fallback). Kinds the manifest doesn't list keep their shipped clips.
//
// Audio is render-only, so a reload at any moment is safe, multiplayer included. The
// path is CWD-relative, like configs/: launched from the repo root it is the folder the
// soundboard writes. Delete Audio/dev_cache/ to go back to the shipped clips.
public sealed class SoundDevOverrides : IDisposable
{
    public const string DefaultDir = "Audio/dev_cache";

    private readonly string _dir;
    private readonly string _manifest;
    private readonly FileSystemWatcher _watcher;
    private volatile bool _dirty = true;          // load whatever is there at boot
    private List<SoundEffect> _owned = new();
    private long _version = -1;

    public string LastStatus { get; private set; } = "";

    private SoundDevOverrides(string dir)
    {
        _dir = dir;
        _manifest = Path.Combine(dir, "manifest.json");
        _watcher = new FileSystemWatcher(dir)
        {
            Filter = "manifest.json",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += (_, _) => _dirty = true;
        _watcher.Created += (_, _) => _dirty = true;
        _watcher.Renamed += (_, _) => _dirty = true;
    }

    // Null on the browser, or when not launched from the repo root (no Audio/ folder in
    // the CWD — e.g. a packaged build), so a shipped game never watches anything.
    public static SoundDevOverrides TryStart(string dir = DefaultDir)
    {
        if (OperatingSystem.IsBrowser()) return null;
        try
        {
            string full = Path.GetFullPath(dir);
            string parent = Path.GetDirectoryName(full);
            if (parent == null || !File.Exists(Path.Combine(parent, "GameAudio.cs"))) return null;
            Directory.CreateDirectory(full);
            return new SoundDevOverrides(full);
        }
        catch (Exception) { return null; }
    }

    // True when the bank changed this call (the caller stops live voices first).
    public bool Poll(SoundBank bank, Action stopVoices)
    {
        if (!_dirty) return false;
        _dirty = false;

        Dictionary<SoundKind, SoundEffect[]> loaded;
        long version;
        try
        {
            if (!File.Exists(_manifest)) return false;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_manifest));
            var root = doc.RootElement;
            version = root.TryGetProperty("version", out var v) ? v.GetInt64() : 0;
            if (version == _version) return false;
            loaded = Load(root.GetProperty("events"), out var errors);
            LastStatus = $"soundboard v{version}: {loaded.Count} event(s)"
                       + (errors.Count > 0 ? $", failed: {string.Join(", ", errors)}" : "");
        }
        catch (Exception ex)
        {
            // Mid-write or malformed: try again on the next change.
            LastStatus = "soundboard manifest unreadable: " + ex.Message;
            return false;
        }

        stopVoices();
        bank.ClearOverrides();
        var nowOwned = new List<SoundEffect>();
        foreach (var (kind, clips) in loaded)
        {
            bank.SetOverride(kind, clips);
            nowOwned.AddRange(clips);
        }
        foreach (var old in _owned) old.Dispose();
        _owned = nowOwned;
        _version = version;
        return true;
    }

    private Dictionary<SoundKind, SoundEffect[]> Load(JsonElement events, out List<string> errors)
    {
        errors = new List<string>();
        var byStem = new Dictionary<string, SoundKind>(StringComparer.Ordinal);
        for (int k = 1; k < SoundKinds.Count; k++) byStem[SoundKinds.Stem((SoundKind)k)] = (SoundKind)k;

        var result = new Dictionary<SoundKind, SoundEffect[]>();
        foreach (var ev in events.EnumerateObject())
        {
            if (!byStem.TryGetValue(ev.Name, out var kind)) { errors.Add(ev.Name + " (no SoundKind)"); continue; }
            var clips = new List<SoundEffect>();
            foreach (var f in ev.Value.EnumerateArray())
            {
                string path = Path.Combine(_dir, f.GetString() ?? "");
                try
                {
                    using var stream = File.OpenRead(path);
                    clips.Add(SoundEffect.FromStream(stream));
                }
                catch (Exception) { errors.Add(Path.GetFileName(path)); }
            }
            result[kind] = clips.ToArray();
        }
        return result;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        foreach (var c in _owned) c.Dispose();
        _owned.Clear();
    }
}
