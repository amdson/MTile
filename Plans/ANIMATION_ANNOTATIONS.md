# Clip annotations: points, elements, contacts, curves

Status: **reference — describes what the code does as of 2026-09-20.** Not a proposal. If it
disagrees with the source, the source is right and this file is stale; fix it.

This is the handoff doc for anyone touching `Animation/`'s annotation layer or the
`MTile.Demo` editor that authors it. Read [CODEBASE_OVERVIEW.md](../CODEBASE_OVERVIEW.md)
first for the animation pipeline as a whole; this covers one slice of it in depth.

---

## 1. The model, in one picture

A clip annotates its rig in exactly three ways. Get this straight and the rest follows.

```
NamedPoint            ← the ANCHOR. A stable id for a location on the rig.
  ├── AnimAttachment  ← an ELEMENT:  add-on with its own frame, drawn.       Has a lifetime.
  └── ContactSpan     ← a CONTACT:   labeled spline data, solved, never drawn. Has a lifetime.
```

- **A point is not an annotation.** It is the thing annotations hang off. It has no lifetime;
  it exists for the whole clip. `Animation/Endpoints.cs`.
- **Both annotations are spans**: `[Start, End)` in normalized clip phase, independent of where
  the keyframes fall. Both get a draggable row under the editor's timeline.
- **Both reference their anchor by POINT ID, never by bone name.** This is load-bearing — see
  §4.1. A bare bone name still *resolves* (as that bone's End) so nothing needs a point
  declared in order to work, but only a declared point survives a bone rename.

The editor's endpoint menu is built around exactly this split: `Add element ▸` and
`Add contact ▸`, with presets (Knife; No slip / Planned support / External pin) as each
category's submenu, and naming the point in the header.

### The files

| Thing | File | Notes |
|---|---|---|
| `NamedPoint`, `ResolvedPoint`, `EndpointResolver` | `Animation/Endpoints.cs` | One resolver serves picking, rendering, contacts, IK, probe, attachments |
| `AnimAttachment`, `AttachmentSampling` | `Animation/AnimAttachment.cs` | Elements + their sampling/frame math |
| `ContactSpan`, `ContactSource` | `Animation/ContactSpan.cs` | Contacts |
| `AnimCurve`, `AnimCurveKey` | `Animation/AnimCurve.cs` | The scalar spline annotation values use |
| Document wiring | `Animation/AnimationDocument.cs` | `Points`, `Attachments`, `Contacts` are all **clip-level** lists |
| Editor | `MTile.Demo/DemoGame.cs`, `DemoGame.Ui.cs` | Menu, timeline rows, curve editor |
| Controls reference | `MTile.Demo/CONTROLS.md` | The user-facing description of every gesture |

---

## 2. `AnimCurve` — the value type

A cubic Hermite scalar curve on a **closed, normalized** domain `u ∈ [0,1]`, mapped onto an
annotation's own `[Start, End)`.

- **The domain is the span, not the clip.** Retiming a span *stretches* the curve; the shape
  survives. That was a deliberate choice over absolute-time ramps — a slower step should
  transfer weight more slowly. The cost is that a curve cannot express "40ms of ramp
  regardless of span". Nothing needs that yet and the serialized shape leaves room to add a
  flag for it without a migration.
- **The domain is closed.** `u = 1` evaluates the last key; it does **not** wrap to the first.
  The *span* owns wrapping, never the curve. (A sampler that silently wrapped its endpoint has
  bitten this codebase before — `AnimSceneTests` pins `BodyAt(1f)` for the same reason.)
- **Tangents are auto-derived** (Catmull-Rom secant through the neighbours) unless `Tan` is
  set. Amber keys in the editor are authored, blue are derived.
- **A null curve is a constant.** Every accessor is a static taking a possibly-null curve plus
  a fallback, so no consumer null-checks and `AnimationStore`'s `WhenWritingNull` keeps a flat
  annotation's JSON clean.

`SlopeOverSpan` is the one the solver differentiates through: the curve's own derivative
through a `1/width` chain-rule factor, zero outside the span.

---

## 3. `ContactSpan` → the solver

This is the part people get fuzzy on. **A span never becomes a constraint, and its `Source`
never decides who supplies `Target`.** Who does is decided per frame by the animator, for the
whole clip at once:

```
clip.Contacts (every span, any Source)
        │
        ├─► ClipStrideTrack.TryCompile → one track: stances + swings per named point
        │      (the timing stage reads its CycleDisplacement; the planner reads the rest)
        │
        └─► CharacterAnimator._plannerActive := locomotion clip ∧ terrain in the sample
                                               ∧ PlannerEnabled ∧ the track has feet
              false → RefreshContacts captures the rig's CURRENT world tip and holds it
                      (SelfPlant: the clip decides)
              true  → MirrorPlans writes Target = FootPlan.Target for every stance
                      (the terrain decides); nothing self-plants that frame
```

(2026-09-20: this replaced a per-point `PlannedSupport` opt-in with a per-bone ownership
predicate and a mixed-source compile error. The opt-in was rollout scaffolding; the clip
backup still serializes the name and `AnimationStore` reads it as `SelfPlant`.)

By the time `PlantedContactsConstraint` runs, both look identical — it never reads `Source`:

```csharp
float sw = MathF.Sqrt(p.Cfg.TierContact * c.Weight) * p.InvCharLen;
r[n++] = sw * (tip.X + dx - c.Target.X);   // horizontal no-slip → drives Δφ + d.x
r[n++] = sw * (tip.Y + dy - c.Target.Y);   // vertical ground hold → drives δ
```

Two least-squares rows per active contact, weighted `√(TierContact · Weight)`.

Other things worth knowing:

- **Swings are a different constraint.** A planner-owned foot mid-swing gets *no* planted
  contact; `SwingTargetConstraint` pulls it toward its landing **without** the `d.x`/`δ`
  columns, so a swinging foot never drags the root. That is why the plan mirror *removes* the
  contact rather than fading it.
- **A stance survives a clip switch.** `TransferContacts` keeps a contact whose bone is in
  a stance of the incoming clip and hands the already-captured target to `Planner.Rebind` as
  an *adopted* support, so a foot keeps standing on the same spot instead of re-selecting
  and popping.
- **`dw/dφ`'s SIGN is read by the SelfPlant lifecycle** to detect a release. Planner-owned feet
  do not use it — their release is `p.State != Stance`.

---

## 4. Invariants that will bite you

### 4.1 Annotations reference points, never bone names

Both `ContactSpan.Point` and `AnimAttachment.Point` are point ids. This was migrated *away*
from bone names twice (`ContactLabel.Node`, then `AnimAttachment.Bone`) for the same reason: a
bone rename silently orphaned everything on it, and two authoring tools could write different
shapes for the same thing. Do not add a third bone-name reference.

### 4.2 `EndpointResolver.BoneOf` THROWS — but only the solve path may call it

`BoneOf` throws on an unresolvable id or a point that is not an exact bone tip, deliberately:
the solver used to skip both silently, so a typo'd id just stopped planting a foot with no
error anywhere.

**Editor query/draw paths must NOT call it.** An editor that throws while painting cannot be
used to fix the data that made it throw. Those go through `DemoGame.EditorBoneOf` (tolerant,
returns −1) or `TryResolvePoint` directly. `ScenePreview` does the same for ghost marks.
Attachment resolution in `AttachmentSampling.Append` also skips rather than throws — a missing
effect anchor costs a cosmetic, and the renderer must not take the frame down over one.

### 4.3 Offset points: honoured by elements, refused by contacts

`NamedPoint.Ox/Oy` and `BoneEnd.Start` are expressible. `AttachmentSampling.Transform` honours
them (position moves, the bone's orientation is kept). Contacts refuse them — the solver pins
tips only, and `BoneOf` throws. That asymmetry is intentional, not an oversight.

### 4.4 `ContactSpan.DefaultWeight` is a SHARED static — never hand it out for editing

`EffectiveWeight` returns the shared instance when `Weight` is null. Mutating what you get back
retunes **every unauthored contact in the project** at once. Use `EnsureWeight()`, which forks
on first edit. Pinned by `AnimContactSpanTests.EnsureWeight_ForksTheSharedDefault_...`.

### 4.5 A null contact weight is a RAMP, not a flat 1.0

A constant weight has zero slope everywhere, so a release would never signal and
`RefreshContacts`' time-fade floor would never engage — the foot-swap deadlock. `DefaultWeight`
is `AnimCurve.Ramp(1, 0.15, 0.15)`.

### 4.6 Inserting a curve key must freeze the neighbours' tangents

An auto tangent is a secant through a key's **neighbours**, so a naive insert re-derives the
tangents either side and the line moves under the cursor. `AnimCurve.InsertPreservingShape`
freezes them first. Pinned by `AnimCurveTests.InsertPreservingShape_DoesNotMoveTheLine`.

### 4.7 Spans may wrap the loop seam

`End > 1` means a stance crosses the seam — it is **one** span, not two meeting at the
boundary. `Covers()` retries at φ+1. The editor's `SpanDrag` takes an `allowWrap` flag;
contacts allow it, attachment windows do not.

### 4.8 Deleting an element cascades

`RemoveAttachment` drops the attachment, then its own point if nothing else references it,
then the clip-local bone if nothing holds it (`BoneHolder`). The bone's pose entries and
additions go with it — a knife you *posed* must still be deletable. Historically `AddKnife`
was a one-way door: nothing in the codebase ever removed an `ExtraBones` record, so every
abandoned knife leaked a dead bone into the clip file.

---

## 5. Testing reality — read this before you panic at the red

**The suite is ~96 red and that is expected.** As of 2026-09-20: **869 passed / 96 failed /
14 skipped**. The animation group alone is 51 failed / 187 passed.

The reason: **every clip in `SkeletonStates/` is a blank rest-pose stub.** The 139 authored
clips were backed up and deleted on 2026-09-18 for a full re-author
(`Backups/clips-20260918-111934.tar.gz`, the only copy — it holds uncommitted working-tree
edits git never saw). 47 biped + 47 biped_rabbit stubs remain; `rabbit_derived` is empty and is
regenerated by `scripts/sync-rabbit-derived.sh`.

So any test that reads real clip content fails. Typical messages:

- `no cadence solve ran — nothing validated` — the clip has no contacts, so `HasContacts` is
  false and the solve never runs.
- `Assert.Single() Failure: The collection was empty` — no attachments authored.
- gait/stride/golden-trace failures — no authored motion.

**Before attributing a red test to your change, check whether it reads a real clip.** The
pre-stub baseline was 894 passed / 42 failed (those 42 are the known arm64-Mac golden-trace
reds plus BACKLOG §5's table).

### What IS covered, and what cannot be

| Covered | Where |
|---|---|
| `AnimCurve` evaluation, derivative, domain, insert, round-trip | `AnimCurveTests` (12) |
| `ContactSpan` coverage, wrap, weight, slope, crossfade, fork, round-trip | `AnimContactSpanTests` (14) |
| Stride compilation from spans | `AnimStrideTrackTests` |
| Point resolution + `BoneOf` throwing | `AnimEndpointTests` |
| Element anchoring, offsets, skip-on-unresolvable | `AttachmentTests` |

**`MTile.Tests` only references `MTile.Core`.** Nothing in `MTile.Demo` is unit-testable —
every editor gesture (timeline drags, the curve editor, the endpoint menu) is **build-verified
only** and unexercised until a human opens the editor. Push logic down into Core if you want it
tested; that is why `InsertPreservingShape` lives on `AnimCurve` rather than in the editor.

### `ContactFixture`

`MTile.Tests/Animation/ContactFixture.cs` converts "at this key, this foot is planted" into the
spans that labeling used to imply. It exists so the gait fixtures that predate the span model
keep meaning what they meant — otherwise they prove nothing about the migration. Use it for new
gait-shaped fixtures; author spans directly for anything testing span behaviour itself.

---

## 6. A caution about automated surveys

Two "this is dead, delete it" findings from agent sweeps were **wrong in ways that would have
broken shipped behaviour**:

1. `AnimAttachment.TrailStart`/`TrailEnd` reported as never-authored schema. They are authored
   in 9 of the backed-up clips (the three ground slashes, both rigs) and drive the knife trail.
   The agent grepped `SkeletonStates/`, which today holds only blank stubs, so the **entire
   clip library read as empty of everything**.
2. The ECS 2-/3-component `Query` overloads reported as zero-call-site. `WorldTests` covers
   both and exists *for* them.

**While the clips are stubs, no "this serialized field is unused" claim is provable from the
working tree.** Check the backup tarball. Verify reference counts yourself before deleting.

---

## 7. Commands

```bash
# Build (fastest correctness check)
dotnet build MTile.Core.csproj

# The group that covers this area (~3 s)
scripts/test-group.py animation

# Author a contact SPAN. end may exceed 1 to wrap the loop seam; --clear retimes in place.
P=MTile.Probe/bin/Debug/net8.0/MTile.Probe.dll
dotnet build MTile.Probe                       # once
dotnet $P contact walk 0.0 0.5 support_l planned
dotnet $P contact walk 0.8 1.2 support_r --clear     # one stance across the seam
dotnet $P contact walk none                          # clear every span
dotnet $P stride walk                                # what the planner compiled
dotnet $P digest walk                                # per-key pose digest + covering contacts

# The editor
dotnet run --project MTile.Demo -- walk
```

See [.claude/skills/anim-probe/SKILL.md](../.claude/skills/anim-probe/SKILL.md) for the full
authoring workflow — it is the operative guide for posing, and its cadence-gotchas section is
current with the span model.

---

## 8. Open work

- **The spline editor exists but is unverified** — build-clean, never opened. First human pass
  will find layout problems.
- **`AnimCurve` has one consumer.** `AnimAttachment.TrailWidth`/`TrailOpacity`/`Scale`/
  `Rotation` are constants that could adopt it. Deliberately not converted en masse; let each
  arrive with a reason.
- **Absolute-time ramps** are unsupported (§2). Add the per-curve flag only if something
  actually feels wrong when retimed.
- **`ClipStrideTrack` is expected to be rewritten** (owner's note, 2026-09-20). Its ring-walk
  derivation is already gone; the remaining geometry half (offsets, reach, swing residuals,
  `CycleDisplacement`) is what would survive.
- **`AnimSolverTests`' FD-vs-analytic oracle does not currently run** — it needs a clip with
  contacts. `AnimContactSpanTests` covers the derivative directly in the meantime; re-check the
  oracle once clips carry contacts again.
