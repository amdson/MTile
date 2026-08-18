using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;

namespace MTile;

// The voice pool. Owns every SoundEffectInstance that is currently sounding, and the
// two rules from AUDIO_PLAN.md §5 that make level-triggered audio work:
//
//   • Retarget, don't restart. A Sustain for an id that is already playing updates
//     gain/pitch/position on the live voice. Restarting it instead is what produces
//     machine-gun retriggering — the audio equivalent of re-entering a looping clip
//     every frame.
//   • Fade on departure. A loop that stops being sustained ramps down over
//     FadeSeconds rather than cutting, which also absorbs single-frame predicate
//     flicker for free.
//
// Frame protocol, once per RENDERED frame (never per sim Step):
//     BeginFrame() → Sustain()/Fire() → EndFrame(listener, dt)
//
// Platform note: the API surface used here is deliberately narrow — Play/Stop/Volume/
// Pitch/Pan/IsLooped — all verified present on both MonoGame DesktopGL and KNI
// (AUDIO_PLAN.md §7). No Apply3D: pan and falloff are computed here from the camera,
// which is cheaper and sidesteps the one API whose web behaviour is unverified.
public sealed class AudioMixer
{
    private const float FadeSeconds      = 0.06f;   // §5: 50–80 ms
    private const float FalloffStart     = 260f;    // px; full gain inside this
    private const float FalloffEnd       = 1100f;   // px; silent beyond
    private const float PanWidth         = 700f;    // px offset that reaches full pan
    private const int   MaxVoicesPerKind = 8;       // §5 voice cap
    private const int   DedupWindow      = 120;     // frames; ~2× the rollback buffer

    private sealed class Voice
    {
        public SoundId               Id;
        public SoundKind             Kind;
        public SoundEffectInstance   Inst;
        public Vector2               Pos;
        public float                 TargetGain;
        public float                 Fade = 1f;     // 1 while sustained, ramps to 0 on departure
        public bool                  SustainedThisFrame;
        public bool                  Loop;
    }

    private readonly SoundBank            _bank;
    private readonly List<Voice>          _voices  = new();
    private readonly Dictionary<SoundId, Voice> _byId = new();
    private readonly int[]                _firedThisFrame = new int[SoundKinds.Count];

    // (simFrame, id) dedup. Belt to the presentation log's braces: events routed through
    // PresentationEventLog are already deduped, but Fire() is public and anything else
    // calling it gets the same guarantee. AUDIO_PLAN.md §4.
    private readonly HashSet<(int, SoundId)> _seen  = new();
    private readonly Queue<(int, SoundId)>   _order = new();

    public float MasterVolume = 0.8f;
    public bool  Muted;

    public int LiveVoices => _voices.Count;

    public AudioMixer(SoundBank bank) { _bank = bank; }

    public void BeginFrame()
    {
        for (int i = 0; i < _voices.Count; i++) _voices[i].SustainedThisFrame = false;
        Array.Clear(_firedThisFrame, 0, _firedThisFrame.Length);
    }

    // Level-triggered: "this sound should be alive right now, with these parameters."
    // Call every frame the predicate holds. Stopping the calls is what ends the sound.
    public void Sustain(SoundId id, float gain, float pitch, Vector2 pos)
    {
        if (Muted || !_bank.Has(id.Kind)) return;

        if (_byId.TryGetValue(id, out var v))
        {
            v.SustainedThisFrame = true;
            v.TargetGain         = gain;
            v.Pos                = pos;
            v.Fade               = 1f;                       // cancel any in-flight fade
            if (v.Inst != null && !v.Inst.IsDisposed) v.Inst.Pitch = Clamp(pitch, -1f, 1f);
            return;
        }

        var started = Start(id, gain, pitch, pos, loop: true);
        if (started != null) started.SustainedThisFrame = true;
    }

    // Edge-triggered: "this happened." Fires once per (simFrame, id) no matter how many
    // times a rollback replays that frame.
    public void Fire(SoundId id, int simFrame, float gain, float pitch, Vector2 pos)
    {
        if (Muted || !_bank.Has(id.Kind)) return;

        var key = (simFrame, id);
        if (!_seen.Add(key)) return;
        _order.Enqueue(key);
        while (_order.Count > 0 && simFrame - _order.Peek().Item1 > DedupWindow)
            _seen.Remove(_order.Dequeue());

        // Per-frame fold (§5, the burst/peel case): past the cap, drop the voice but let
        // the ones that did start carry a little more weight, so "40 tiles broke" reads
        // as louder rather than as forty overlapping copies.
        int k = (int)id.Kind;
        if (_firedThisFrame[k] >= MaxVoicesPerKind) return;
        _firedThisFrame[k]++;
        if (_firedThisFrame[k] > 1) gain *= 1f + 0.06f * (_firedThisFrame[k] - 1);

        Start(id, gain, pitch, pos, loop: false);
    }

    private Voice Start(SoundId id, float gain, float pitch, Vector2 pos, bool loop)
    {
        var clip = _bank.Pick(id.Kind, id.VariantSeed);
        if (clip == null) return null;

        SoundEffectInstance inst;
        try { inst = clip.CreateInstance(); }
        catch (Exception) { return null; }            // out of voices on the backend

        inst.IsLooped = loop || SoundKinds.IsLoop(id.Kind);
        inst.Pitch    = Clamp(pitch, -1f, 1f);
        inst.Volume   = 0f;                            // set properly in EndFrame
        inst.Play();

        var v = new Voice { Id = id, Kind = id.Kind, Inst = inst, Pos = pos,
                            TargetGain = gain, Loop = inst.IsLooped };
        _voices.Add(v);
        if (loop) _byId[id] = v;                       // only loops are retargetable by id
        return v;
    }

    // Apply this frame's gains, retire departed loops and finished one-shots.
    // `listener` is the camera position — pan and falloff are relative to what's on screen.
    public void EndFrame(Vector2 listener, float dt)
    {
        for (int i = _voices.Count - 1; i >= 0; i--)
        {
            var v = _voices[i];
            if (v.Inst == null || v.Inst.IsDisposed) { Remove(i, v); continue; }

            // A loop nobody sustained this frame is on its way out.
            if (v.Loop && !v.SustainedThisFrame)
            {
                v.Fade -= dt / FadeSeconds;
                if (v.Fade <= 0f) { Stop(v); Remove(i, v); continue; }
            }

            if (!v.Loop && v.Inst.State == SoundState.Stopped) { Stop(v); Remove(i, v); continue; }

            float dist = Vector2.Distance(v.Pos, listener);
            float fall = 1f - Clamp((dist - FalloffStart) / (FalloffEnd - FalloffStart), 0f, 1f);
            v.Inst.Volume = Clamp(v.TargetGain * v.Fade * fall * MasterVolume, 0f, 1f);
            v.Inst.Pan    = Clamp((v.Pos.X - listener.X) / PanWidth, -1f, 1f);
        }
    }

    private void Remove(int i, Voice v)
    {
        _voices.RemoveAt(i);
        if (v.Loop && _byId.TryGetValue(v.Id, out var held) && ReferenceEquals(held, v))
            _byId.Remove(v.Id);
    }

    private static void Stop(Voice v)
    {
        if (v.Inst == null || v.Inst.IsDisposed) return;
        try { v.Inst.Stop(); v.Inst.Dispose(); } catch (Exception) { }
    }

    // Kill everything — stage reload, or leaving a match.
    public void StopAll()
    {
        for (int i = 0; i < _voices.Count; i++) Stop(_voices[i]);
        _voices.Clear();
        _byId.Clear();
        _seen.Clear();
        _order.Clear();
    }

    private static float Clamp(float v, float lo, float hi) => v < lo ? lo : (v > hi ? hi : v);
}
