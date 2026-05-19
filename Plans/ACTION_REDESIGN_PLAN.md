# Action System Redesign

## Goals (from notes)

Three principles, in tension:

- **Free.** Don't lock the player. Don't drop inputs. No microsecond timing. 2 s input buffer. Smooth transitions, especially across posture (ground ↔ air).
- **Intuitive.** Same input → same result, regardless of internal state the player can't see.
- **Not spammy.** Big moves need wind-up; recovery can't be cancelled by most other moves. DPS shouldn't reward weird input-cancel loops.

The current SlashAction satisfies "intuitive" and roughly "not spammy" (0.15 s cooldown) but doesn't compose: it does its own ad-hoc gesture detection, has no combo concept, no Recovery, and can't share input with future moves like Stab without messy precondition cross-checks.

This redesign rebuilds the action layer around two decoupled systems — an **input parser** that emits abstract intents, and an **action FSM** that consumes them. The hard part (and the one you flagged) is parser semantics, so this doc spends most of its detail there.

## Architecture at a glance

```
  raw Controller frames
          │
          ▼
   ┌──────────────┐
   │ InputParser  │   stateful gesture detectors; runs once per frame
   └──────┬───────┘
          │  emits ActionIntent(type, frame, data)
          ▼
   ┌──────────────┐
   │ IntentBuffer │   ring of recent intents; pruned by age + consumption
   └──────┬───────┘
          │  peek / consume
          ▼
   ┌──────────────┐
   │  Action FSM  │   same priority-based selection as today; preconditions
   └──────┬───────┘   read intent buffer + ConditionState + posture
          │  selected action runs Update; may write ConditionState
          ▼
   visual + hitbox / movement effects
```

Two parallel state stores:

- **IntentBuffer**: "what did the player ask for, recently?"
- **ConditionState**: "what is the player's combat condition right now?" (combo flags, recovery phase, etc.) Lives on `PlayerAbilityState`.

The action FSM's `CheckPreConditions` queries both; `Enter` may consume an intent and/or mutate condition state.

---

## Input parser — the heart of the redesign

This is the part the notes flagged as fuzzy (decisions 4, 5, 7). The clarifying move is:

> **Detect gestures on the input *edge*, not by pattern-matching the live buffer every frame.**

The parser holds one detector per gesture type. Each detector tracks the controller stream and *emits at most one intent per discrete event*. A "click" intent fires once, on the frame the click release is recognized — never again, no matter how long the release lingers in the recent buffer.

### Concrete: ClickDetector

State: `lastEmittedReleaseFrame`.

Per frame:
1. Is the current frame an LMB-release edge (current up, previous down)?
2. Walk back, find the press edge.
3. Was the hold duration ≤ `ClickMaxHoldFrames`?
4. If all yes, AND `releaseFrame > lastEmittedReleaseFrame`: emit `ActionIntent.Click` and update `lastEmittedReleaseFrame`.

The edge check (current up, previous down) is true for *exactly one frame* per release. So even without the `lastEmitted` guard the detector wouldn't double-fire — but the guard makes the invariant explicit and survives detector restarts.

### StabDetector

Same shape, different gesture:
- Press edge → start tracking. Record `pressFrame`, `pressMousePos`.
- Each frame while held → measure `swipeDistance = |currentMousePos - pressMousePos|`.
- On release edge: if `holdDuration > ClickMaxHoldFrames` AND `swipeDistance > StabSwipeThreshold` → emit `ActionIntent.Stab(swipeDir = currentMousePos - pressMousePos)`.
- If neither click nor stab qualifies on release → no intent (the press was a "drop").

Click and stab are mutually exclusive by construction — different hold-duration windows.

### Adding more gestures later

Each gesture is a small class with one method, `Detect(controller, currentFrame) -> Intent?`. Add a `RightClickDragDetector` for build-mode, `DoubleClickDetector`, whatever. Parser iterates all of them.

### Why this answers decision #7

> "How to prevent the input buffer parsing from repeatedly triggering attacks."

Because **detection is edge-triggered and idempotent**. Each release edge emits at most one intent. The action FSM doesn't pattern-match the input buffer at all — it reads from the intent stream, which is already deduplicated.

The flip side concern from the notes — "Unfortunately, some inputs truely should correspond to multiple actions. E.g. entering ready with left click down, then slashing with left-click down-up" — also resolves cleanly: a single click produces **two distinct edges** (press + release) and emits **two distinct intents** (`PressEdge` for Ready, `Click` for Slash). Each intent is consumed by a different state transition. No magic.

---

## IntentBuffer

```csharp
public enum IntentType { PressEdge, Click, Stab, RightClick, ... }

public struct ActionIntent {
    public IntentType Type;
    public int  IssuedFrame;
    public Vector2 Direction;   // gesture-specific (Stab swipe direction, etc.)
    public bool Consumed;
}

public class IntentBuffer {
    private readonly List<ActionIntent> _intents = new();
    public void Issue(in ActionIntent intent);
    public bool Peek(IntentType type, int currentFrame, int maxAge, out ActionIntent intent);
    public void Consume(IntentType type, int currentFrame, int maxAge);  // mark first matching as Consumed
    public void Prune(int currentFrame);                                  // drop Consumed + age > MaxAge
}
```

- **MaxAge** = `BufferLifetimeSeconds × fps` ≈ 60 frames at 30 fps. The notes set this to 2 s.
- **Peek vs Consume**: preconditions Peek (no side effect, OK to call multiple times during selection). The winning action's Enter calls Consume.
- **Pruning**: Consumed intents are flagged but not immediately removed; they're dropped on the next frame's `Prune` call. Keeps the data structure simple and consistent.

### Lifetime tuning

2 s is the upper bound on "I clicked, where is my slash". Set it. The slash visual itself is 0.5 s + ~0.1 s recovery, so 2 s gives 4× headroom. Easy to tune from one constant.

---

## ConditionState — combo flags + recovery phase

Lives on `PlayerAbilityState` alongside `Facing`. Each flag has:
- A boolean value
- An optional auto-expire frame (`-1` = no expiration)

```csharp
public class ConditionState {
    public bool Slash2Ready;        public int Slash2ExpireFrame;
    public bool Slash3Ready;        public int Slash3ExpireFrame;
    public bool AirSlash2Ready;     public int AirSlash2ExpireFrame;
    // RecoveryActive replaced by checking "current state is Recovery"
    // GuardWindow:    set during Recovery's last N frames; lets Guard preempt

    public bool GuardWindow;        public int  GuardWindowExpireFrame;

    public void Tick(int currentFrame);   // unsets flags past their expire
    public void Set(ref bool flag, ref int expireField, int durationFrames, int currentFrame);
}
```

The motivating cases:
- **Slash1 → Slash2 chain**: Slash1.Exit calls `Set(ref Slash2Ready, ref Slash2ExpireFrame, 30, currentFrame)`. Slash2's precondition: `cond.Slash2Ready && intentBuffer.Peek(Click)`.
- **Slash3 → no further**: Slash3.Exit sets *no* flag. After Recovery, the next Click triggers a fresh Slash1.

This answers decision #5: combos are flag-gated. Slash2's precondition only fires when Slash1 has just ended and a buffered click is available.

### Why binary flags, not the "readiness value" from decision #2

The notes float a scalar readiness ("higher readiness lets moves start at a more advanced stage"). It's expressive but two costs: more tuning surface, and "starts at advanced stage" usually means re-entering a state with phase offset, which is a different abstraction. Binary flags + dedicated states are simpler.

Promote to a scalar later if a specific move actually wants three readiness levels. For V1, binary suffices.

---

## Recovery — a real state, parameterized

Decision #3. Recovery exists. It's a concrete state in the action FSM with:

- **Duration**: set at Enter from a parameter the previous move passed (`_recoveryDuration`). Small moves → short recovery. Big moves (Stab) → long recovery.
- **PreemptionWindow**: a late phase where some-but-not-all actions can interrupt. Implemented as `ConditionState.GuardWindow` being true for the last N frames.
- **Priority**: high passive priority, so once entered nothing easy preempts it. Combo chain moves (Slash2 from Slash1) have priority *just barely* exceeding Recovery's active priority, gated by the combo flag.

So the precedence chain looks like:

```
ActivePriority    PassivePriority    State
0                 0                  Null
20                20                 Recovery
25                ? (gated)          Slash2 (only when Slash2Ready + Click in buffer)
30                30                 Slash1, Stab, …
35                ? (gated)          Guard (only during GuardWindow)
```

Combo moves' passive priority is dynamic — `cond.Slash2Ready ? 26 : -1`. This is a small extension to the FSM but fits naturally; alternatively we just include the flag check in CheckPreConditions and use a static high priority (cleaner).

### Decision #2's "fuse Recovery and Ready" — recommend keeping separate

Recovery means "I just did a move, I'm locked out for a moment." Ready means "I'm about to do a move, I'm accepting refinement." Different semantics, different priorities, different visuals. Fusing them tangles the rules. Keep separate.

But: Recovery → Ready is a *fast transition*. When Recovery's GuardWindow opens and the player has already pressed LMB during Recovery, the next intent transitions us directly to Ready (or to the chained Slash). The "discount" from the notes is implemented as the GuardWindow letting certain preempts in.

---

## Ready — the wind-up state

Decision #4 + the notes' workaround:

> "Replace the very short initial phase of slash with a Ready state that the player enters immediately upon pressing left mouse down."

This is exactly right. Ready is its own state:

- **Trigger** (precondition): `PressEdge` intent in buffer AND not in Recovery.
- **Lifetime**: while LMB held AND hold duration ≤ `ClickMaxHoldFrames + StabMaxHoldFrames` (some upper bound past which the press has clearly failed to commit to anything).
- **Visual**: a small wind-up indicator. Same color logic as the slash (red ground, blue air).
- **Exits to**: Slash1 / AirSlash1 / Stab — whichever's precondition fires when the player commits.
- **Cancel exit**: if LMB still held past the upper bound and no other move qualifies, exit to Null. No move fires; the player just held the button too long without committing.

Crucially, Ready exits *don't* fire from Ready's own checks — they fire because the next move (Slash1, Stab) has higher priority and its precondition just became true. The FSM's existing preemption logic handles it.

This decouples the "I'm starting an attack" commitment from "which attack." The player sees the anticipation immediately on press; the system picks the attack on release.

---

## The state cast

| State | Priority (A/P) | Enters from | Notable transitions |
|---|---|---|---|
| **Null** | 0/0 | end of Recovery, no further intent | always-true fallback |
| **Ready** | 10/15 | PressEdge intent | Slash1, AirSlash1, Stab (preempted on next move's precondition) |
| **Slash1** | 30/30 | Click intent + Ready + grounded | → Recovery (sets `Slash2Ready`) |
| **AirSlash1** | 30/30 | Click intent + Ready + airborne | → Recovery (sets `AirSlash2Ready`) |
| **Slash2** | 30/30 | Click intent + `Slash2Ready` + (in Recovery OR just exited Slash1) | → Recovery (sets `Slash3Ready`) |
| **Slash3** | 30/30 | Click intent + `Slash3Ready` | → Recovery (no further flag) |
| **AirSlash2** | 30/30 | Click intent + `AirSlash2Ready` + airborne | → Recovery |
| **Stab** | 30/30 | Stab intent + Ready | → Recovery (longer duration) |
| **Recovery** | 28/45 | end of any attack | duration set at Enter; ConditionState ticks |

Priority numbers are placeholders; the structure is what matters:
- All attacks at the same active priority (30) so they don't accidentally interrupt each other; preemption between them is gated by intents + ConditionState, not priority.
- Recovery's passive (45) > attack active (30): nothing short of a combo-flagged chain preempts Recovery.
- Combo chain moves preempt Recovery via the `cond.Slash2Ready` gate inside their precondition — not via priority math.

### Air ↔ ground continuation (decision #6)

The current SlashAction already plays out fully across posture changes — captures `_isGrounded` at Enter, doesn't interrupt on ground loss. **Keep this property** for all attack states. Don't try to convert mid-execution. The combo chain re-evaluates posture at the *transition point*: when Slash1 ends and the next click is buffered, posture decides whether Slash2 or AirSlash2 fires.

For visuals: `_isGrounded` can be refreshed every frame to drive the color, even if the action itself doesn't change behavior. That's a one-line change.

---

## Scenario walkthroughs

All scenarios assume 30 fps, `ClickMaxHoldFrames = 6`, `BufferLifetimeFrames = 60`.

### Scenario 1 (notes): single fast click → Slash1

```
F0:  LMB down              → InputParser issues PressEdge   → FSM enters Ready
F1:  LMB down              
F2:  LMB up                → InputParser issues Click (hold=2)
                           → Slash1 precondition fires (Click intent + Ready + grounded)
                           → FSM enters Slash1, consumes Click
F2-F17: Slash1 runs (~0.5s = 15 frames)
F17: Slash1 ends           → ConditionState.Slash2Ready = true (expires F47)
                           → FSM enters Recovery (duration = 3 frames)
F18-F20: Recovery
F21: end of Recovery, no more intents → FSM enters Null
```

### Scenario 2 (notes): 3 rapid clicks → S1, S2, S3

```
F0:  PressEdge → Ready
F2:  Click(F2) → Slash1
F4:  PressEdge   (Ready can't re-enter — already in Slash1; FSM ignores)
F6:  Click(F6) → buffered (sits in IntentBuffer)
F8:  PressEdge   (buffered)
F10: Click(F10)  (buffered)
F17: Slash1 ends → Recovery + Slash2Ready
F18: Slash2 precondition: Click(F6) in buffer + Slash2Ready → true
     Slash2 preempts Recovery (priority gated by flag), consumes Click(F6)
F18-F33: Slash2 runs
F33: Slash2 ends → Recovery + Slash3Ready
F34: Slash3 fires from Click(F10), consumes it
F34-F49: Slash3 runs
F49: Slash3 ends → Recovery (no flag set; chain ends)
F50-F52: Recovery
F53: Null
```

The PressEdge intents at F4 and F8 are mostly noise — they could be consumed by Ready, but since Ready's precondition is "PressEdge AND not in another attack," they're effectively wasted. That's fine; they expire harmlessly.

### Scenario 3 (notes): 4 rapid clicks → S1, S2, S3, S1 again

Same as Scenario 2 for the first three. The 4th click sits in buffer. After Slash3's Recovery ends, the FSM is Null. Click(F14) is still in buffer; Slash1's precondition (Click + grounded + not in Recovery) fires. New Slash1 starts.

Note: the 4th click is consumed by Slash1, *not* by Ready. Ready is only reached via a fresh PressEdge while in Null. The PressEdge for the 4th click was at F12 — still in buffer (60-frame window), but Ready's precondition needs not-in-attack-or-recovery, which only becomes true at F52. By F52, PressEdge(F12) is still within MaxAge (40 frames old). So Ready *could* fire at F52. Then at F53, Click(F14) fires Slash1.

Net effect: brief flicker through Ready before Slash1. Acceptable. Or we can require PressEdge intent to be < ~30 frames old. Tunable.

### Scenario 4: stab → release mid-stab → Recovery

```
F0:  PressEdge → Ready
F1-F8: LMB held, mouse drags right
F8:  threshold crossed but waiting for release (StabDetector emits on release)
F12: LMB up → Stab intent emitted (hold=12, swipe=right)
              Stab.precondition fires (Stab intent + Ready) → Stab enters
F12-F28: Stab plays (~0.6 s, longer than slash)
F28: Stab ends → Recovery (duration = 8 frames — longer for big moves)
```

The user's "midway through stab player releases left mouse" — if we use release-triggered Stab, the release IS the trigger. So "releasing mid-stab" is just "the release that completes the swipe gesture." Stab runs to completion regardless of further input (same as Slash).

### Scenario 5: stab → fast click → Slash after Recovery

```
F0-F12: Stab triggers and runs (as Scenario 4)
F14: LMB down                (player tries to click during Stab)
F16: LMB up → Click(F16) buffered. Stab continues running.
F28: Stab ends → Recovery (8 frames)
F36: Recovery ends. Click(F16) still in buffer (age 20 < MaxAge 60).
     Slash1.precondition fires → Slash1 enters.
```

Stab can't be cancelled into Slash (decision #3 — recovery prevents cancel-spam). The buffered click waits.

### Scenario 6: slash mid-stab → stab queued

```
F0:  PressEdge → Ready
F2:  Click(F2) → Slash1
F8:  LMB down (during Slash1)
F8-F18: LMB held, mouse drags
F18: LMB up → Stab intent buffered. Slash1 continues running.
F17: Slash1 ends → Recovery + Slash2Ready (expires F47)
F18: Slash2 precondition checks for Click — none. Stab.precondition checks for
     Stab intent + Ready — wait, Stab requires Ready. But we're in Recovery,
     not Ready. Stab won't fire here.
F20: Recovery ends → FSM enters Null. Stab intent still in buffer.
     Stab's precondition: Stab intent + Ready. We're in Null, not Ready.
     But Stab should fire here, per Scenario 6.
```

**Problem!** Stab requires Ready, which requires a PressEdge intent. The Scenario-6 player's PressEdge happened at F8 (during Slash1) and is gone by F20 (12 frames later — within buffer, OK). But Ready's precondition is "PressEdge AND not in attack/recovery." At F20 that's true; Ready fires. F21: Stab's precondition fires (Stab intent + Ready). Stab runs.

OK actually it works, with a one-frame Ready flicker between Null and Stab. Either accept that, or allow Stab to fire directly from Null when both PressEdge and Stab intents are buffered (skip Ready when committing immediately).

I'd lean on the simpler version — flicker through Ready — and only optimize if it looks visibly wrong.

---

## Migration order

Each step compiles, tests pass, game is playable.

1. **InputParser + IntentBuffer infrastructure.** Empty intent buffer wired through `EnvironmentContext`. ClickDetector emits Click intents. SlashAction's existing precondition keeps working (still does its own gesture detection); the new system runs in parallel, unused. Verify intents appear in a debug overlay (a small text line listing buffered intents).
2. **Migrate SlashAction's trigger to IntentBuffer.** Replace its inline release-detection with `intentBuffer.Peek(Click)` + Consume on Enter. Same gameplay, cleaner code. Delete the inline detection.
3. **Add ConditionState (combo flags) + Recovery state.** SlashAction (still single) transitions into Recovery on completion instead of straight to Null. Recovery is high-priority and uninterruptible for V1.
4. **Split single SlashAction into Slash1.** Just a rename; behavior identical. Sets `Slash2Ready` at the end of Recovery (or end of itself — both work; latter is cleaner).
5. **Add Slash2 and Slash3.** Combo chain works for the first time. Test against Scenarios 2 and 3.
6. **Add Ready.** Replaces the very-first frames of Slash with the new pre-commit state. Visual is just the wind-up dot, no arc yet.
7. **Add StabDetector + Stab state.** Test Scenarios 4–6.
8. **Add AirSlash1 / AirSlash2.** Posture-dependent fork at Slash1's precondition + at the combo transition.
9. **Remove ad-hoc cooldown logic** (the current `Cooldown = 0.15s` field). Recovery now subsumes it.

Steps 1–3 are scaffolding (no visible behavior change). Steps 4–9 each introduce one new piece of player-facing behavior.

---

## Files to touch

| File | Role |
|---|---|
| `Character/InputParser.cs` | NEW. Owns ClickDetector + StabDetector instances. Called once per frame from `PlayerCharacter.Update`. |
| `Character/IntentBuffer.cs` | NEW. ActionIntent struct + buffer with Peek/Consume/Prune. |
| `Character/ConditionState.cs` | NEW. Combo flag bundle with auto-expiry. |
| `Character/EnvironmentContext.cs` | EDIT. Add `IntentBuffer Intents` and `ConditionState Condition`. |
| `Character/PlayerAbilityState.cs` | EDIT. Own a `ConditionState` field. |
| `Character/ActionStates.cs` | REWRITE. Split SlashAction into Slash1/Slash2/Slash3, add AirSlash1/AirSlash2, Stab, Ready, Recovery. |
| `Character/PlayerCharacter.cs` | EDIT. Construct InputParser; run it pre-action-FSM each frame. |

The `Facing` and slash arc math stay. The geometry layer is unchanged.

---

## Open questions (please confirm before I implement)

1. **Recovery durations.** Each move passes a duration when it transitions into Recovery. Starting values: Slash1/2/3 = 0.1 s, AirSlash1/2 = 0.1 s, Stab = 0.3 s. Tunable. Are these in the right ballpark or do you want them tighter?
2. **Intent expiry per-type.** Plan uses one global `BufferLifetimeFrames = 60`. Alternative: per-intent-type max age (PressEdge expires in 20 frames, Click in 60). More tuning surface but more precise. Default to single global; add per-type if needed.
3. **Visual identity of Ready.** Plan says "small wind-up dot." Concrete proposal: a single dot at body center pulsing up to 50% of `ArcRadius` and back over the held window. Acceptable, or do you want something more distinct?
4. **Slash2/Slash3 visual differentiation.** Right now Slash1 is the existing arc. Should S2 be a mirrored arc (reverse handedness), S3 a longer arc with stronger knockback? Or all three identical for V1 and we differentiate later? Recommend identical-shape for V1, differentiated knockback magnitude only.
5. **Stab geometry.** Linear thrust along `swipeDir`, length ≈ `2 × ArcRadius`. Hurtbox: a thin rect along the thrust axis. The notes specify "swipe direction, not mouse-to-player" — confirming this means at Stab's Enter we capture `swipeDir` from the Stab intent's `Direction` field and forget about cursor position.
6. **Guard.** Mentioned in passing in the notes ("Guard in late stages of recovery"). Not in this plan's scope — assume it's a later layer that hooks into ConditionState.GuardWindow when needed.
