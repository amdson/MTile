using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;

namespace MTile;

// The only file that knows which sim facts map to which sounds. Everything else in
// Audio/ is mechanism; this is policy, and it is where a new sound gets wired.
//
// Two halves, per AUDIO_PLAN.md §2:
//
//   Level-triggered  → CollectLevel(): re-derived from current sim state every frame.
//                      Idempotent by construction, so a rollback needs no handling.
//   Edge-triggered   → Present():      fed by PresentationEventLog, which has already
//                      deduped by (simFrame, id) so replays don't double-fire.
//
// Strictly render-side. Reads sim state, never writes it.
public sealed class GameAudio
{
    // How stale a sim frame stamp may be and still sound. Covers the case where several
    // sim Steps run inside one rendered frame (catch-up, fast-forward) — without it the
    // event would be missed entirely rather than merely late.
    private const int StampWindowFrames = 6;

    // Landing borrows the footstep clips (SoundKinds.Fallback), so it needs to sit
    // clearly above a step. Drop this toward 1.0 once a dedicated land clip exists.
    private const float LandGain = 1.35f;

    private readonly SoundBank  _bank  = new();
    private readonly AudioMixer _mixer;

    public GameAudio() { _mixer = new AudioMixer(_bank); }

    public AudioMixer Mixer   => _mixer;
    public string     Summary => _bank.Summary();

    public void Load(ContentManager content) => _bank.Load(content);

    public void BeginFrame() => _mixer.BeginFrame();
    public void EndFrame(Vector2 listener, float dt) => _mixer.EndFrame(listener, dt);
    public void StopAll() => _mixer.StopAll();

    // ── Edge-triggered ──────────────────────────────────────────────────────────
    // One case per PresentationKind. Adding a sound to an existing event is a line here.
    public void Present(in PresentationEvent e)
    {
        switch (e.Id.Kind)
        {
            case PresentationKind.TileBreak:
                _mixer.Fire(SoundId.ForTile(SoundKind.TileBreak, e.Id.A, e.Id.B),
                            e.SimFrame, gain: 0.9f, pitch: Jitter(e.Id.A * 31 + e.Id.B), pos: e.Position);
                break;

            case PresentationKind.TilePlace:
                _mixer.Fire(SoundId.ForTile(SoundKind.TilePlace, e.Id.A, e.Id.B),
                            e.SimFrame, gain: 0.5f, pitch: Jitter(e.Id.A * 17 + e.Id.B), pos: e.Position);
                break;

            case PresentationKind.PlayerRespawn:
                _mixer.Fire(new SoundId(SoundKind.Respawn), e.SimFrame,
                            gain: 1f, pitch: 0f, pos: e.Position);
                break;
        }
    }

    // ── Level-triggered ─────────────────────────────────────────────────────────
    // Called once per rendered frame against final sim state. Every sound here answers
    // "could I tell from a paused frame that this should be sounding?" with yes.
    //
    // Note the one-shots below are *also* derived rather than evented. Each keys its
    // dedup on a frame the sim already stamped (a hit frame, a state-entry frame, a
    // projectile's age), so the same event re-derives to the same (frame, id) after a
    // rollback and fires exactly once — plan §2a, the frame-stamp promotion. No sim
    // change was needed for any of them.
    public void CollectLevel(Simulation sim)
    {
        Player(sim, sim.Player, 0);
        var secondaries = sim.SecondaryPlayers;
        for (int i = 0; i < secondaries.Count; i++) Player(sim, secondaries[i].Player, i + 1);

        Eruptions(sim);
    }

    private void Player(Simulation sim, PlayerCharacter p, int index)
    {
        if (p == null) return;
        WallScrape(p, index);
        HitConnect(p, index);
        Jump(p, index);
        DoubleJump(p, index);
        ClimbEffort(p, index);
        Land(p, index);
    }

    // Continuous prerequisite: the movement state is a wall slide. Gain and pitch ride
    // slide speed, so the clip is sourced neutral and shaped here.
    private void WallScrape(PlayerCharacter p, int index)
    {
        if (p.CurrentState == null) return;
        if (p.CurrentState.AnimationTag != AnimTag.WallSlide) return;

        float speed = MathF.Abs(p.Body.Velocity.Y);
        if (speed < 30f) return;                       // resting against a wall is silent

        float t = MathF.Min(speed / 420f, 1f);
        _mixer.Sustain(SoundId.ForPlayer(SoundKind.WallScrape, index),
                       gain:  0.25f + 0.55f * t,
                       pitch: -0.15f + 0.35f * t,
                       pos:   p.Body.Position);
    }

    // CombatState already stamps the frame of the last hit and its impulse, so the sound
    // needs no event: the stamp IS the identity. Louder hits are louder and lower.
    private void HitConnect(PlayerCharacter p, int index)
    {
        var c = p.Combat;
        if (c == null || c.LastHitFrame <= 0) return;
        int age = p.Frame - c.LastHitFrame;
        if (age < 0 || age > StampWindowFrames) return;

        float t = MathF.Min(c.LastHitImpulse / 900f, 1f);
        _mixer.Fire(new SoundId(SoundKind.HitConnect, index, c.LastHitFrame),
                    simFrame: c.LastHitFrame,
                    gain:     0.55f + 0.45f * t,
                    pitch:    0.1f - 0.25f * t,
                    pos:      p.Body.Position);
    }

    // Entering a jump state is an edge, but a derivable one: the state ring is snapshotted,
    // so "current is a jump, previous frame was not" re-derives identically after a
    // rollback. Keyed on the entry frame, which is what makes it fire once.
    private void Jump(PlayerCharacter p, int index)
    {
        if (!IsJump(p.CurrentState)) return;
        if (IsJump(p.GetPreviousState(1))) return;     // already sounded on entry

        _mixer.Fire(new SoundId(SoundKind.Jump, index, p.Frame),
                    simFrame: p.Frame, gain: 0.7f, pitch: Jitter(p.Frame), pos: p.Body.Position);
    }

    // Landing: the air→ground transition, derived from the snapshotted state ring the
    // same way the jumps are. Gain rides the body's impact impulse, so stepping off a
    // ledge is a scuff and a long fall is a thud — which matters more than usual here,
    // since the clip is a footstep borrowed and pushed louder.
    private void Land(PlayerCharacter p, int index)
    {
        if (!p.IsGrounded) return;
        var prev = p.GetPreviousState(1);
        if (prev == null || prev is StandingState || prev is CrouchedState) return;

        float t = MathF.Min(p.Body.LastImpulseMagnitude / 700f, 1f);
        _mixer.Fire(new SoundId(SoundKind.Land, index, p.Frame),
                    simFrame: p.Frame,
                    gain:     LandGain * (0.7f + 0.5f * t),
                    pitch:    -0.15f - 0.15f * t,
                    pos:      p.Body.Position);
    }

    // Entering a double jump is its own sound — airier than the ground push-off, and the
    // clip set distinguishes them, so the generic jump set below deliberately excludes it.
    private void DoubleJump(PlayerCharacter p, int index)
    {
        if (p.CurrentState is not DoubleJumpingState) return;
        if (p.GetPreviousState(1) is DoubleJumpingState) return;

        _mixer.Fire(new SoundId(SoundKind.DoubleJump, index, p.Frame),
                    simFrame: p.Frame, gain: 0.7f, pitch: Jitter(p.Frame), pos: p.Body.Position);
    }

    // Effort grunt on a climb that costs something: hauling over a ledge or mantling.
    // Same state-entry derivation as the jumps.
    private void ClimbEffort(PlayerCharacter p, int index)
    {
        if (!IsEffortClimb(p.CurrentState)) return;
        if (IsEffortClimb(p.GetPreviousState(1))) return;

        _mixer.Fire(new SoundId(SoundKind.Grunt, index, p.Frame),
                    simFrame: p.Frame, gain: 0.65f, pitch: Jitter(p.Frame * 7), pos: p.Body.Position);
    }

    private static bool IsEffortClimb(MovementState s) => s is LedgePullState || s is MantleState;

    // DoubleJumpingState is excluded on purpose — it has its own clip (see DoubleJump).
    private static bool IsJump(MovementState s)
        => s is JumpingState || s is RunningJumpState || s is CoveredJumpState;

    // An eruption is a MassBall entering the world. Age is snapshotted, so subtracting it
    // from the current frame recovers the spawn frame — the same number on every replay,
    // no matter which rendered frame first observes the ball.
    private void Eruptions(Simulation sim)
    {
        var entities = sim.Entities;
        for (int i = 0; i < entities.Count; i++)
        {
            if (entities[i] is not MassBall ball) continue;
            int ageFrames = (int)MathF.Round(ball.AgeSeconds / Simulation.FixedDt);
            if (ageFrames > StampWindowFrames) continue;

            var id = ball.Id;
            _mixer.Fire(new SoundId(SoundKind.Eruption, id.Index, id.Generation),
                        simFrame: sim.Frame - ageFrames,
                        gain: 1f, pitch: -0.1f, pos: ball.Body.Position);
        }
    }

    // NOT WIRED, deliberately, though the clips are built and in the bank:
    //
    //   Footstep — the cadence lives in the animation, not the sim. Deriving it from sim
    //     state alone would mean re-inventing stride phase; reading the animator would
    //     make audio depend on a render system rather than on the sim, and would not
    //     survive a rollback the way everything else here does. Left silent by choice.
    //   Throw — CombatState.RegisterThrown funnels into the same LastHitFrame stamp a
    //     normal hit uses (CombatState.cs:90), so there is nothing render-side that
    //     distinguishes a throw from a hit. Needs its own stamp to sound distinctly.

    // ±5% pitch variety on repeated one-shots. Render-side only — the sim never sees it,
    // but it is derived from the event key so a replay picks the same jitter.
    private static float Jitter(int seed)
    {
        int h = (seed * 83492791) & 0x7fffffff;
        return (h % 1000 / 1000f - 0.5f) * 0.1f;
    }
}
