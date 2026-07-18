using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile;

// In-game animation recorder / scrubber (desktop dev tool). Captures the full game
// state plus the exact drawn skeleton pose every rendered frame, then lets you freeze
// the take and scrub it back and forth, frame by frame, to study the animation.
//
// Two things are captured per frame and WHY:
//   • SimSnapshot + a full dense terrain copy → the scene (terrain, entities, physics,
//     combat, action FSM). The sim snapshot restores all of that in place. Terrain is
//     captured DENSELY (DenseTerrainCapture) rather than via the snapshot's journal,
//     because the journal truncates history on rewind and so can't scrub forward again
//     after a backward jump.
//   • The composed skeleton pose(s) + the world root Draw() used → the EXACT rig. The
//     pose lives in the render-only CharacterAnimator (phase, easing, smoothness priors),
//     NOT in the deterministic snapshot, and that temporal state is one-directional —
//     re-deriving it while scrubbing backward would jitter. Storing the pose makes
//     playback pixel-exact in either direction. The root is captured too because
//     RigRoot reads animator-internal clip/δ state we don't restore.
//
// In-memory only (clips are lost on exit). Disk persistence is a planned follow-up —
// SimSnapshot can't be serialized as-is (Type-keyed component stores, a terrain journal
// indexed into the live instance); see Plans/.
//
// Hotkeys (combos chosen to avoid accidental presses and gameplay-input bleed — Ctrl is
// not a gameplay key):
//   Ctrl+R  — start / stop recording (start clears the previous take)
//   Ctrl+P  — enter / exit playback of the last take (sim frozen while scrubbing)
//   In playback:  ←/→ step ∓1   Shift+←/→ step ∓10   Home/End first/last
//                 J/K/L  play-reverse / pause / play-forward   (Space = pause)
public sealed class GameRecorder
{
    public enum Mode { Idle, Recording, Playback }

    // One animator's exact drawn state for a frame.
    public struct PoseCapture
    {
        public BoneTransform[] Pose;          // composed local bone transforms
        public Vector2         RootWorldPos;  // world placement Draw() used (RigRoot result)
        public int             Facing;
    }

    private struct CameraState { public Vector2 Position; public float Zoom; }

    private sealed class Frame
    {
        public SimSnapshot         Sim;
        public DenseTerrainCapture Terrain;
        public PoseCapture         Primary;
        public PoseCapture[]       Secondaries;
        public CameraState         Camera;
        // The animator input of this frame (pins/surfaces arrays cloned — the live
        // sample shares scratch arrays). This is what Ctrl+S persists: the take file
        // stores the sample STREAM and the offline viewer re-runs the animator over it
        // (Plans/ANIM_TAKE_VIEWER_PLAN.md).
        public CharacterAnimSample Sample;
    }

    private readonly List<Frame> _frames = new();
    // Cap so a forgotten recording can't exhaust memory. ~30 s at 60 fps.
    private const int MaxFrames = 1800;

    public Mode State { get; private set; } = Mode.Idle;
    public bool IsRecording => State == Mode.Recording;
    public bool IsPlayback  => State == Mode.Playback;

    private int  _cursor;        // current frame shown in playback
    private int  _applied = -1;  // last frame actually applied to the sim (avoid redundant restores)
    private int  _playDir;       // 0 paused, +1 forward, -1 reverse
    private KeyboardState _prevKb;

    // The live state captured on entering playback, restored on exit so normal play
    // resumes exactly where it left off (scrubbing mutates the live sim).
    private Frame _resume;

    // ── per-frame entry point ───────────────────────────────────────────────────
    // Handles mode toggles + playback scrubbing. Returns true when Game1 should run its
    // normal step + cosmetic pass this frame (i.e. NOT in playback). Call CaptureFrame
    // afterward (only when IsRecording) so the pose captured reflects this frame's anim.
    public bool HandleInput(KeyboardState kb, Simulation sim, Camera cam,
                            CharacterAnimator primary, List<CharacterAnimator> secondaries, float scale)
    {
        bool ctrl = kb.IsKeyDown(Keys.LeftControl) || kb.IsKeyDown(Keys.RightControl);

        if (ctrl && Pressed(kb, Keys.R) && State != Mode.Playback)
        {
            if (State == Mode.Recording) State = Mode.Idle;          // stop
            else { _frames.Clear(); State = Mode.Recording; }        // start fresh take
        }
        else if (ctrl && Pressed(kb, Keys.P))
        {
            if (State == Mode.Playback) ExitPlayback(sim, cam);
            else if (_frames.Count > 0) EnterPlayback(sim, cam, primary, secondaries, scale);
        }
        else if (ctrl && Pressed(kb, Keys.S) && State != Mode.Recording && _frames.Count > 0)
        {
            SaveTake();   // Idle-with-take or mid-playback: persist for the offline viewer
        }
        if (_noticeFrames > 0) _noticeFrames--;

        if (State == Mode.Playback)
            ScrubAndApply(kb, sim, cam, primary, secondaries);

        _prevKb = kb;
        return State != Mode.Playback;
    }

    // ── recording ───────────────────────────────────────────────────────────────
    // Append a frame. Call once per rendered frame while IsRecording, AFTER the cosmetic
    // pass (so the animator pose + RigRoot reflect this frame). `dt` is the render delta
    // the cosmetic pass fed the animators — recorded so an offline replay ticks the
    // animator with the exact same steps.
    public void CaptureFrame(Simulation sim, Camera cam,
                             CharacterAnimator primary, List<CharacterAnimator> secondaries,
                             float scale, float dt)
    {
        if (State != Mode.Recording) return;
        _frames.Add(BuildFrame(sim, cam, primary, secondaries, scale, dt));
        if (_frames.Count >= MaxFrames) State = Mode.Idle;   // auto-stop at the cap
    }

    private static Frame BuildFrame(Simulation sim, Camera cam,
                                    CharacterAnimator primary, List<CharacterAnimator> secondaries,
                                    float scale, float dt)
    {
        var secs = new PoseCapture[sim.SecondaryPlayers.Count];
        for (int i = 0; i < secs.Length && i < secondaries.Count; i++)
        {
            var p = sim.SecondaryPlayers[i].Player;
            secs[i] = new PoseCapture
            {
                Pose         = secondaries[i].Pose.CloneLocal(),
                RootWorldPos = AttackGlowSystem.RigRoot(p, secondaries[i], scale),
                Facing       = p.Facing,
            };
        }

        // Rebuild the primary's animator input for this frame — a pure read of the same
        // state the cosmetic pass just consumed, so it matches what the animator saw.
        // Pins/surfaces are cloned: the live sample reuses shared scratch arrays.
        var s = CharacterAnimSample.From(sim.Player, dt);
        var sample = new CharacterAnimSample(
            s.Position, s.Velocity, s.Facing, s.Grounded, s.MovementState, s.Action, s.Dt,
            s.ActionTime, s.ActionDuration, s.MovementProgress,
            s.Pins is { Length: > 0 } ? (ExternalPin[])s.Pins.Clone() : null,
            s.Surfaces is { Length: > 0 } ? (SolverSurface[])s.Surfaces.Clone() : null,
            s.HasGrip, s.GripTarget, s.HasAim, s.AimDir, s.Tag);

        return new Frame
        {
            Sim     = sim.Snapshot(),
            Terrain = sim.Chunks.CaptureDense(),
            Primary = new PoseCapture
            {
                Pose         = primary.Pose.CloneLocal(),
                RootWorldPos = AttackGlowSystem.RigRoot(sim.Player, primary, scale),
                Facing       = sim.Player.Facing,
            },
            Secondaries = secs,
            Camera      = new CameraState { Position = cam.Position, Zoom = cam.Zoom },
            Sample      = sample,
        };
    }

    // ── save to disk (Ctrl+S) ───────────────────────────────────────────────────
    // Persist the take for the offline viewer: the sample stream + deduped terrain
    // states (AnimTake). Poses/sim are NOT saved — the viewer re-runs the animator.
    private void SaveTake()
    {
        var take = new AnimTake
        {
            SkeletonScale = Game1.SkeletonScale,
            PlayerRadius  = PlayerCharacter.Radius,
        };
        foreach (var f in _frames)
            take.AddFrame(f.Sample, f.Terrain);

        string path = System.IO.Path.Combine(
            AnimTake.DefaultDir(), $"take_{System.DateTime.Now:yyyyMMdd_HHmmss}.take.json");
        try
        {
            take.Save(path);
            Notice($"saved {_frames.Count} frames -> {path}");
        }
        catch (System.Exception ex)
        {
            Notice($"save FAILED: {ex.Message}");
        }
    }

    // ── playback ────────────────────────────────────────────────────────────────
    private void EnterPlayback(Simulation sim, Camera cam,
                               CharacterAnimator primary, List<CharacterAnimator> secondaries, float scale)
    {
        _resume  = BuildFrame(sim, cam, primary, secondaries, scale, 1f / 60f);
        State    = Mode.Playback;
        _cursor  = 0;
        _applied = -1;
        _playDir = 0;
    }

    private void ExitPlayback(Simulation sim, Camera cam)
    {
        ApplyFrame(_resume, sim, cam, null, null);   // restore live state; animators re-driven next frame
        cam.Position = _resume.Camera.Position;
        cam.Zoom     = _resume.Camera.Zoom;
        State    = Mode.Idle;
        _applied = -1;
    }

    private void ScrubAndApply(KeyboardState kb, Simulation sim, Camera cam,
                               CharacterAnimator primary, List<CharacterAnimator> secondaries)
    {
        bool shift = kb.IsKeyDown(Keys.LeftShift) || kb.IsKeyDown(Keys.RightShift);
        int step = shift ? 10 : 1;

        if (Pressed(kb, Keys.Right)) { _cursor += step; _playDir = 0; }
        if (Pressed(kb, Keys.Left))  { _cursor -= step; _playDir = 0; }
        if (Pressed(kb, Keys.Home))  { _cursor = 0;                _playDir = 0; }
        if (Pressed(kb, Keys.End))   { _cursor = _frames.Count - 1; _playDir = 0; }
        if (Pressed(kb, Keys.L)) _playDir = +1;                       // play forward
        if (Pressed(kb, Keys.J)) _playDir = -1;                       // play reverse
        if (Pressed(kb, Keys.K) || Pressed(kb, Keys.Space)) _playDir = 0;   // pause

        if (_playDir != 0) _cursor += _playDir;

        // Wrap so a loop keeps cycling; clamp keeps manual stepping in range.
        if (_frames.Count > 0)
        {
            if (_cursor < 0) _cursor = _frames.Count - 1;
            else if (_cursor >= _frames.Count) _cursor = 0;
        }

        if (_cursor != _applied && _cursorValid)
        {
            ApplyFrame(_frames[_cursor], sim, cam, primary, secondaries);
            _applied = _cursor;
        }
    }

    // Restore a captured frame for display: scene + terrain + poses + camera + sprites.
    private static void ApplyFrame(Frame f, Simulation sim, Camera cam,
                                   CharacterAnimator primary, List<CharacterAnimator> secondaries)
    {
        sim.Restore(f.Sim);                 // players, entities, combat, platforms, sparse terrain
        sim.Chunks.RestoreDense(f.Terrain); // dense tile grid (journal-independent)

        if (primary != null) primary.Pose.LoadLocal(f.Primary.Pose);
        if (secondaries != null)
            for (int i = 0; i < f.Secondaries.Length && i < secondaries.Count; i++)
                secondaries[i].Pose.LoadLocal(f.Secondaries[i].Pose);

        cam.Position = f.Camera.Position;
        cam.Zoom     = f.Camera.Zoom;

        SyncSprites(sim);   // sprite positions track restored bodies (cosmetics is skipped in playback)
    }

    // Mirror the position sync the cosmetic pass does, WITHOUT advancing any animation
    // (which would overwrite the loaded poses).
    private static void SyncSprites(Simulation sim)
    {
        if (sim.Player.Sprite != null) sim.Player.Sprite.Position = sim.Player.Body.Position;
        foreach (var (p, _) in sim.SecondaryPlayers)
            if (p.Sprite != null) p.Sprite.Position = p.Body.Position;
        foreach (var e in sim.Entities) e.SyncSprite();
    }

    // ── draw-side accessors (Game1.Draw reads captured roots during playback) ─────
    public bool TryPlaybackPrimary(out PoseCapture primary)
    {
        if (State == Mode.Playback && _cursorValid) { primary = _frames[_cursor].Primary; return true; }
        primary = default; return false;
    }

    public bool TryPlaybackSecondary(int i, out PoseCapture sec)
    {
        if (State == Mode.Playback && _cursorValid)
        {
            var arr = _frames[_cursor].Secondaries;
            if (i >= 0 && i < arr.Length) { sec = arr[i]; return true; }
        }
        sec = default; return false;
    }

    // Transient HUD notice (e.g. "saved ... -> Takes/...json"), shown for a few seconds.
    private string _notice;
    private int    _noticeFrames;
    private void Notice(string text) { _notice = text; _noticeFrames = 240; }

    public void DrawHud(SpriteBatch sb, SpriteFont font)
    {
        string text = State switch
        {
            Mode.Recording => $"[REC]  {_frames.Count} frames   (Ctrl+R stop)",
            Mode.Playback  => $"[PLAY {(_playDir > 0 ? ">>" : _playDir < 0 ? "<<" : "||")}]  {_cursor + 1}/{_frames.Count}"
                              + "   <-/-> step, Shift x10, J/K/L, Home/End, Ctrl+S save, Ctrl+P exit",
            _ when _frames.Count > 0 => $"[TAKE]  {_frames.Count} frames held   (Ctrl+P scrub, Ctrl+S save, Ctrl+R re-record)",
            _ => null,
        };
        if (_noticeFrames > 0 && _notice != null)
            text = text == null ? _notice : text + "\n" + _notice;
        if (text == null) return;

        var color = State == Mode.Recording ? Color.OrangeRed : Color.Aqua;
        var pos   = new Vector2(8, 44);   // below Game1's state/action HUD lines
        sb.Begin();
        sb.DrawString(font, text, pos + new Vector2(1, 1), Color.Black);
        sb.DrawString(font, text, pos, color);
        sb.End();
    }

    private bool _cursorValid => _cursor >= 0 && _cursor < _frames.Count;
    private bool Pressed(KeyboardState kb, Keys k) => kb.IsKeyDown(k) && !_prevKb.IsKeyDown(k);
}
