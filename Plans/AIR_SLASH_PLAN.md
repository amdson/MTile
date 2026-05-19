# Air Slash & Slash Direction

## Context

Adding air-variants of `SlashAction` surfaces a question the ground slash didn't have to answer: **what determines the direction of a slash?** Two candidate inputs:

1. **Keyboard / arrow keys** — the player's movement intent. Already drives `Facing` and (for vertical) the Up/Down branches in MovementStates. Discrete, 4–8 directions.
2. **Mouse cursor** — the player's pointing intent. Already drives drag-to-build (`Controller.MouseWorldPosition`). Continuous, any angle.

For the ground slash, the only sensible answer was "horizontal in `Facing` direction" — Down is for crouching, Up isn't used. In the air, both axes are open: the player could naturally want to slash down (a dive-stab), up (an anti-air swat), or laterally.

These two input channels can disagree. A player running right but holding the mouse to the upper-left is asking which one? The ground slash dodged the question; air slash can't.

## Three design options

### A. Keyboard-only direction (4 or 8 variants)

The slash direction snaps to whichever arrow(s) are held at trigger time. With no arrow, it falls back to `Facing` (so a standstill air-slash still has a side). Implementation: read input on the release frame, quantize to a fixed set of directions, pick an arc preset per direction.

- **Pros**: Consistent with the rest of the movement FSM (also keyboard-driven). Discrete state — easy to reason about, easy to debug, easy to map to fixed animations later. Crouch/Down means the same thing whether or not LeftClick was tapped. Cheap to implement — three new `ActionState`s with shared base.
- **Cons**: Mouse position becomes purely a building input, ignored for combat. Air combos lose precision. Diagonal slashes need explicit two-key combinations (Up+Right) that don't otherwise exist in the input model.

### B. Mouse-aim direction (analog or 8-quantized)

The slash direction is the angle from body center to cursor at trigger time. Keyboard direction stays exclusively about movement. Holding any arrow during the click doesn't redirect the slash — the cursor wins.

- **Pros**: Maximally expressive — every angle is reachable. Reads as a real combat input ("strike where I'm pointing"). Natural for click-to-attack since the cursor is already in play. Trivial conflict resolution (mouse always wins because keyboard isn't asked).
- **Cons**: A new input modality for combat — keyboard players need to mouse-aim even for "obvious" attacks. Player has to look at the cursor when they want to slash, which competes with looking at the body. Mouse Y-axis matters now (more head movement for the player). Visual: arc rendering needs to rotate around the body cleanly.

### C. Hybrid — mouse-primary, keyboard fallback

Mouse-aim is the default. *But* if an arrow is held that matches a strong cardinal (Down, Up, or away from Facing), and the cursor is within some tolerance of "neutral" (close to body), the keyboard wins — so a player can still hold Down and click for a fast dive-stab without having to drag the cursor below the body.

- **Pros**: Best of both — analog precision when the player wants it, ergonomic keyboard shortcuts for the common cases.
- **Cons**: The tolerance threshold is a tuning knob with non-obvious right answer. Two ways to express "slash down" can be confusing. Implementation complexity bigger than A or B combined.

## Recommendation: **B (mouse-aim, analog)**

The cursor is already where the player's intent lives in this game — drag-to-build relies on it heavily. Combat reading "strike where I'm pointing" feels more like a coherent extension of the existing input scheme than a parallel system. The Facing field stays — it still governs ground-slash and idle-pose orientation — but air slashes ignore it.

Why not 8-quantized: analog is no harder to implement (the arc is parametrized by a 2D direction vector anyway), and quantizing is something we can layer on top later if precision turns out to be unwanted. Hard to back out of quantization without breaking muscle memory.

Why not C (hybrid): the tolerance threshold is unprincipled. A player will accidentally cross it during fast combat and get a different slash than they expected. "Mouse always wins" is a teachable rule; "mouse usually wins but sometimes keyboard" is not.

## Design (assuming option B)

### Unification with ground slash

Ground slash currently uses `Facing` (a left/right enum). With mouse-aim, the natural thing is to **also redirect ground slash to mouse-aim**, so the rule is uniform: *the slash direction is the angle from body to cursor at trigger time, on every slash, ground or air.* The arc still anchors at body center; only the direction changes.

Implications:
- `Facing` keeps its role for non-slash things (idle pose orientation, future animation system). It just doesn't drive slash anymore.
- Single `SlashAction` covers ground and air. Distinction collapses: the ground-requirement (and ground-loss interrupt) goes away. A slash works in any posture.

This is a simplification, not a complication — the variant-explosion ("three air slashes plus the ground slash") becomes one parametrized state.

### Arc geometry, parametrized

Currently the arc is anchored to `(±1, 0)` (the `_facing` vector) and sweeps 90° around it. Replace `_facing` with `_slashDir`, a unit vector captured at Enter:

```csharp
_slashDir = Vector2.Normalize(ctx.Input.MouseWorldPosition - ctx.Body.Position);
// fallback: if cursor is on top of the body, use (Facing, 0)
if (!float.IsFinite(_slashDir.X) || _slashDir.LengthSquared() < 1e-4f)
    _slashDir = new Vector2(ab.Facing, 0f);
```

The arc rotates 90° **counterclockwise** through the swing — visually, the dot traces a perpendicular hook off the direction vector, regardless of orientation. Math change: replace `(cos(angle) * _facing, sin(angle))` with a 2D rotation of `_slashDir` by the swing-angle. Concretely:

```csharp
float angle = -MathF.PI * 0.5f * MathF.Sin(MathF.PI * t);   // 0 → -π/2 → 0
float c = MathF.Cos(angle), s = MathF.Sin(angle);
Vector2 dir = new Vector2(_slashDir.X * c - _slashDir.Y * s,
                          _slashDir.X * s + _slashDir.Y * c);
return anchor + dir * (ArcRadius * MathF.Sin(MathF.PI * t));
```

Hurtbox apex follows the same formula at t=0.5 (peak of `sin(πt)`):

```csharp
var apex = anchor + _slashDir * ArcRadius;   // the *outward* extent of the swing
```

Wait — that's not quite right; the apex of the arc is rotated 45° from `_slashDir`. Let me sketch: at t=0.5, `angle=-π/2`, so `dir` is `_slashDir` rotated 90° CCW. The dot is at `anchor + rotated * ArcRadius` — that's the *side* of `_slashDir`, not the front. For the slash to feel like it strikes *in the cursor's direction*, the arc's apex should be along `_slashDir`, with the arc sweeping from one perpendicular to the other.

Re-parameterizing: arc starts at angle=+π/2 (perpendicular on one side), sweeps through 0 (along `_slashDir`, the apex), to -π/2 (other side). So:

```csharp
float angle = MathF.PI * 0.5f * MathF.Cos(MathF.PI * t);   // π/2 → 0 → -π/2 (NOT out-and-back)
```

But the user's original spec was "out and back" — the dot leaves the body and returns to it. That's `outwardFactor = sin(πt)` modulating the *radius*, not the angle. The angle should be monotonic.

Synthesis: keep `outwardFactor = sin(πt)` for the in-out motion, but make the angle monotonic across `[+π/2, -π/2]` so the dot sweeps cleanly through `_slashDir` at t=0.5:

```csharp
float outF  = MathF.Sin(MathF.PI * t);                  // 0 → 1 → 0 — distance from body
float angle = MathF.PI * 0.5f * (1f - 2f * t);          // +π/2 at t=0, 0 at t=0.5, -π/2 at t=1
// rotate _slashDir by angle, scale by ArcRadius * outF
```

At t=0.5: angle=0, dir = `_slashDir`, dot at body + `_slashDir * ArcRadius`. ✓ Apex along the cursor direction.

Hurtbox apex: same formula, t=0.5 → `anchor + _slashDir * ArcRadius`. The damage window (t ∈ [0.4, 0.64] roughly) brackets the apex.

### Preconditions

The ground-requirement disappears. Remaining preconditions:
- Fast left-click (release within `MaxClickHoldFrames`) — unchanged.
- Cooldown — unchanged.

What was previously "ground-loss interrupt" goes away too — air slash is fine in any posture. The interrupt rule (movement-induced state changes) becomes: nothing interrupts a slash except completion or another action with higher priority.

### Files

| File | Change |
|---|---|
| `Character/ActionStates.cs` | Replace `_facing` with `_slashDir`; rewrite `ComputeDotPosition` for arbitrary direction; rewrite hurtbox apex formula; drop ground check; drop ground-loss interrupt; drop `Exit`'s `SlashInterrupted` write (interrupt path is gone). |
| `Game1.cs` / `PlayerCharacter.cs` | No change. |
| `Character/PlayerAbilityState.cs` | `SlashInterrupted` becomes unused — can remove now, or leave for symmetry with future interruptible actions. Recommend: leave, mark as TODO. |

This is **less code than today**, not more — the variant-explosion is avoided, and the parametrization is just one Vector2 instead of an int.

### Visual & gameplay verification

- Click while mouse is to the right → horizontal-right slash (same as before).
- Click while mouse is above → slash arcs upward, apex above body, perpendicular-sweep crosses through the up direction.
- Click while mouse is diagonal → arc oriented diagonally, apex toward cursor.
- Mid-jump click anywhere → slash fires (no ground check); arc points at cursor.
- Cursor on top of body → fallback to `(Facing, 0)`. Avoids NaN.
- Test: tile in the direction of the cursor breaks; tile in a perpendicular direction unscathed.

## Open questions

1. **Confirm: ground slash also moves to mouse-aim?** The plan assumes yes for uniformity. If you want ground slash to keep its keyboard-tied behavior, the variant-explosion comes back.
2. **Cursor-on-body fallback.** `(Facing, 0)` is safe and intuitive. Alternative: cancel the trigger (no slash if cursor is too close to body). Probably overkill — falling back is fine.
3. **Arc visual orientation.** Currently the arc only sweeps "upward" (negative Y) because Facing was always horizontal. With arbitrary direction the sweep can go either way — needs a tie-breaker for "which side of `_slashDir` does the arc sweep through?". Proposal: always sweep counterclockwise relative to the screen. Player-friendly default. Tuneable.
4. **Diagonal slash radius.** Currently `ArcRadius = Radius * 1.5`. Same in all directions, or longer reach for horizontal vs. shorter for vertical? Probably same — simpler, no surprise factor.

## Migration

1. Land the parametrization (`_slashDir`, new arc math, drop ground check) in a single edit to `ActionStates.cs`. Test ground slash still feels right (mouse roughly horizontal-right → same arc as before).
2. Try in-air slashing in all eight cardinal directions and 2–3 diagonals. Tune `HurtboxStartTime` / `ActiveDuration` / `ArcRadius` if any direction feels off.
3. Decide on cursor-on-body fallback and arc-sweep handedness from feel.
