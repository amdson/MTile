---
name: anim-probe
description: >-
  Use when authoring or debugging MTile skeletal animation — walk/run/idle clips
  in SkeletonStates/*.json, the biped rig in Skeletons/biped.json, or any
  joint-angle question (backward knees, feet not planting, limbs swinging the
  wrong way, foot bob/slip). Drives the work by converting joint angles to world
  positions with MotionProbe and a throwaway test harness, so geometry is read as
  numbers instead of guessed. Do NOT eyeball joint angles for this codebase.
allowed-tools: [Read, Write, Edit, Bash, Glob, Grep]
shell: bash
---

# Probe-driven skeletal animation authoring

Joint-angle intuition is unreliable on this rig (Y-down, side-view, and rotations
compound down the chain). The rig is a **pure joint chain** (FK composes each local
transform as **`R·T·S`**): a bone attaches at its parent's tip, `Rotation` is its
**full local angle** (rest + swing) about that joint, and it runs `Length` to its own
tip. Crucially, **`world[i].Translation` IS the bone's far tip** (the point its
children attach to) — do NOT add `Length` along local +X again to "get the tip"
(that overshoots a whole bone; it was a real renderer/solver bug). A bone's *near*
joint is its **parent's** `.Translation`. `bindRot` carries the rest orientation, so
an authored `Rotation` is the full local angle, not a deviation. The rule of this
workflow: **never guess limb geometry — measure it.** Pose the rig, run forward
kinematics to world (x, y), read the numbers, iterate.

## Principles (hard-won — read first)

These are the lessons that cost the most time. They generalize beyond any one clip.

1. **Suspect the DATA before the math.** A measured "left/right asymmetry" looked
   like an FK bug; it was the rig (`biped.json` had mirrored, sideways-splayed hip
   struts). Validate FK once on a trivial hand-computable rig, confirm it's right,
   then stop blaming it and look at the rig/clip data. Don't inherit a prior
   "maybe it's a bug" framing — re-measure.
2. **`world[i].Translation` reads as an anatomical landmark — it's the bone's far tip.**
   `leg_*_upper`→KNEE, `leg_*_lower`→ANKLE, `foot_*`→TOE, `arm_*_lower`→HAND,
   `chest`→shoulders/neck. A bone's near joint is its parent's translation (so
   `leg_*_upper`'s joint is the hip). Still prefer computing these world points + the
   signed-cross knee test over eyeballing angles. (Pre-migration consumers that built a
   tip as `world[i] + Length·localX` overshot by a bone — that's fixed; tip = `.Translation`.)
3. **Verify on the ASSEMBLED clip, not just discrete poses.** Every keyframe pose
   can look correct while the *interpolated* in-between grazes the ground or pops.
   After authoring, re-run `MotionProbeTests` and read the 16-sample track.
4. **The editor plays the RAW clip** — no cadence solver, no CoM anchor. Contact /
   cadence fixes are invisible there; only the foot *poses* show. Gate cadence
   work on the behavioral tests (advance / no-freeze), not on what you see in
   `MTile.Demo`.
5. **Name the rig's limits instead of fighting them forever.** The foot can't tuck
   heel-up-and-back (that direction is recurvatum), so swing clearance only comes
   from the thigh pendulum — the run foot grazes the ground mid-recovery. That's a
   rig DOF limitation (would need IK / a heel bone), not an authoring bug. Note it
   and move on rather than burning hours chasing a textbook tuck.
6. **Diagnose a frozen/odd cadence by printing per-frame phase.** It sticks at a
   specific phase value, which points straight at the offending keyframe/contact.
7. **It's a tradeoff space — probe the safe window.** "Pin the contact longer"
   fights "don't freeze"; "more foot clearance" fights "forward knee". There's
   usually a narrow good range; find it with numbers, don't guess a single value.

## The loop

1. **Probe before authoring.** Two ways to get the digest — **strongly prefer the fast one for the
   edit→verify loop** (it's ~10× cheaper and is what made past batches slow when skipped):
   ```bash
   # FAST (~1–3s, NO rebuild): clips load at runtime, so editing a clip JSON needs no recompile.
   dotnet build MTile.Probe                                          # ONCE
   dotnet run --project MTile.Probe --no-build -- digest <clip>      # prints ONE clip's digest to stdout
   dotnet MTile.Probe/bin/Debug/net8.0/MTile.Probe.dll digest <clip> # tightest loop (skips `dotnet run`)
   #   `digest` (no clip) writes .probe/<all>.digest.md;  also: `diff <clip> <ref>`, `report <clip>`, `list`.

   # FULL SWEEP (slow, rebuilds the test project): regenerates EVERY .probe/*.md incl. raw tables.
   dotnet test MTile.Tests/MTile.Tests.csproj --filter "FullyQualifiedName~MotionProbeTests"
   ```
   **Parallel/worktree workers: build and run the probe from YOUR OWN checkout.** The probe
   reads and writes `SkeletonStates/` relative to its own DLL's checkout, not your cwd —
   running another checkout's DLL silently authors your clip into that other tree (a real
   calibration-run bug: a worker's clip landed in the main checkout and nearly missed its commit).

   Use the fast `MTile.Probe -- digest <clip>` for every iteration; only fall back to the test sweep
   when you need the raw `.probe/<clip>.md` tables or a batch refresh. Read the digest output (whether
   from stdout or `.probe/<clip>.digest.md`) first:
   - **`.probe/<clip>.digest.md` — a semantic digest for EVERY clip. Read this first.** Per
     keyframe: ground line, planted feet, knee/elbow direction, hand-height bucket, torso lean,
     then an assembled-clip trajectory summary (foot bob / sweep / clearance) and auto-**FLAGS**
     (recurvatum). It is the verification checklist below, computed — so you rarely read raw
     coordinates anymore.
   - `.probe/<clip>.md` — the raw joint/tip world table for the locomotion subset, for deep
     dives: `+x = forward, +y = DOWN`, `vel = d(tip)/d(phase)`. NOTE: `report` on a
     NON-locomotion clip prints headers with no rows — for a static/overlay pose use
     `digest` + `diff` (or a Zzz harness), not `report`.
   - `anim <clip> <Type>` (runtime compose smoke) prints only frames 0/15/30 over a ~1s run
     and clamps a short action to its rest pose — it verifies binding/masking/ease/no-crash,
     NOT what a ≤0.2s overlay looks like mid-swing. Judge the pose from `digest`/`diff`.
   - `.probe/<clip>.vs-<ref>.md` — `MotionProbe.Diff`: per-keyframe tip deltas vs a reference
     clip. Use it to confirm a pose actually **departs** from a baseline (the classic blind miss:
     "this reads the same as idle / rest"). +Δy = lower, +Δx = forward.

   Sampling is the game's **C1 spline** (`SampleSmooth`), so in-betweens match what ships. To
   digest/diff an arbitrary clip or pair, call `MotionProbe.Digest` / `MotionProbe.Diff` from a
   `Zzz` harness or add a line to `MotionProbeTests`.

2. **For a specific question, write a throwaway harness.** When you need a pose
   the clips don't contain (e.g. "which `leg_lower` sign bends the knee forward?",
   "what plants the lead foot?"), add a temp test under
   `MTile.Tests/Animation/Zzz<Thing>.cs` that poses the rig **directly** and dumps
   what you care about to `.probe/`. Template at the bottom. Run it with
   `--filter "FullyQualifiedName~Zzz<Thing>"`, read, retune, repeat.

3. **Author through probe commands — do NOT hand-write clip JSON or raw angles.** The probe
   console has a full authoring command set; every mutating command saves the clip and prints
   the digest, so each step's geometry is read back as numbers immediately:
   ```bash
   P=MTile.Probe/bin/Debug/net8.0/MTile.Probe.dll
   dotnet $P new <name> <Type> --dur 0.8 --from crouch      # create; pose copied from an existing clip (or rest)
   dotnet $P addkey <clip> 0.25 [--from clip@t]             # insert key; default = own C1 sample (shape-preserving)
   dotnet $P ik <clip> 0.25 foot_l --to 4.5,15 --write      # place limb tips (the main posing tool)
   dotnet $P contact <clip> 0.25 foot_l                     # set the planted contact ('none' clears)
   dotnet $P rot <clip> 0 chest -1.25                       # raw-angle escape hatch (torso/head only; --deg)
   dotnet $P retime <clip> 0.5 0.6 | delkey <clip> 0.5 | dur <clip> 0.9
   dotnet $P addcom <clip>                                  # stamp the grounded com anchor LAST
   ```
   Command semantics that cost batch workers cycles (learn them here instead):
   - **Keyframe `Time` args are normalized [0,1]** fractions of the clip, NOT seconds —
     `Duration` is seconds, but `addkey <clip> 0.25` means quarter-phase.
   - **`new --from <clip>` copies only that clip's t=0 pose** (one keyframe), and
     `addkey` with no `--from` seeds from the clip's OWN current C1 sample at that time —
     neither walks the reference clip's later keys; use `addkey <clip> <t> --from ref@t`
     per key if you want the reference's timeline.
   - **`rot` sets ANY bone** (legs and arms included) — the "torso/head" wording is about
     typical use, not a restriction. Iterating `rot` + digest is a legitimate fully
     measurement-driven fallback when IK fights you.
   - **The probe rig has NO knife bone** (`ExtraBones` are clip-local and never composed
     into the probe's rig), so `new`/`addkey` silently DROP the knife entry from copied
     keyframes and `rot`/`ik` can't touch it. Every knife-carrying keyframe needs its
     constant knife entry hand-pasted via direct JSON edit — this is an ALLOWED direct
     edit, alongside Region/OffRegionWeight/Loop/Type/Duration and per-keyframe
     `com` Additions (`addcom` only stamps one constant from kf0; a per-key com profile —
     e.g. run's vertical pop — is a direct edit, and being a render offset it is
     INVISIBLE to the FK digest, so verify it by reasoning/tests instead).

   The proven recipe (how crouchwalk was authored end-to-end without computing one leg angle):
   copy a base pose into each key (`new --from` / `addkey --from`), read the reference clip's
   digest for choreography numbers (stride, plant depth, contact pattern), then `ik --to` every
   foot/hand target per key, `contact` per key, `addcom`, digest. Mirror-symmetric targets +
   mirrored seeds give exactly mirrored solved halves for free.

4. **Verify** by re-running the fast `MTile.Probe -- digest <clip>` (no rebuild) and checking the
   assembled clip against the checklist below — iterate edit→digest here, it's a ~1–3s loop.

5. **Clean up.** Delete every `Zzz*.cs` harness and its `.probe/*.md`. Leave
   `Animation/MotionProbe.cs` and `MotionProbeTests.cs` (the standing tools).

## Rig conventions — MEASURED, trust these (Skeletons/biped.json)

Side-view figure, faces +X, Y-down. Pure joint chain (FK = `R·T·S` per local): a bone
attaches at its parent's tip, `Rotation` is the **full local angle** (`bindRot` + swing),
and it runs `Length` to its own tip (= `world[i].Translation`). `bindRot` carries the rest
orientation, so a
clip's authored `Rotation` already includes it — a synthetic test that means "swing
the thigh ±δ from rest" must write `Bind.Rotation + δ`, not `δ`.

**Anatomical joint chain (joints now line up — post-migration):**
`hip (pelvis) → leg_*_upper.tip = leg_*_lower.joint (= KNEE) → foot_*.joint (= ANKLE)
→ foot_*.tip (= TOE)`. The foot is a single ankle→toe segment (the old heel dummy was
collapsed). Arms: `arm_*_upper.tip = arm_*_lower.joint (= elbow) → arm_*_lower.tip
(= hand)`.

**Bind orientations** (rest `Rotation`, radians): chest `-π/2` (up), head `0`, legs
`+π/2` (down), `*_lower` `0` (straight), arms `+π` (hang), foot `≈-1.33`. A swing is a
deviation from these — **forward = swing toward +X** (e.g. thigh `Rotation < π/2`),
but **measure swing signs with the digest** rather than trusting a remembered sign:
the migration re-mapped every authored value, so old sign lore (and
[[project_rig_leg_asymmetry]]) no longer applies verbatim. Knee direction (recurvatum
vs forward) is auto-flagged in `.probe/<clip>.digest.md` — read it.

## Verification checklist (the digest computes all of this — FLAGS + the lines below)

The `.probe/<clip>.digest.md` already classifies each of these per keyframe; the definitions
here explain what its labels/flags mean and how to fix a bad one.

- **Airborne / no-contact clips** (jump, fall, tumble, …): the digest still prints
  PLANTED/swing labels (relative to the pose's lowest point) and the "planted-foot bob
  ≈ 0" trajectory hint — for a clip with zero Contact labels these are NOISE, not gate
  items. The planted/bob criteria below apply only to clips with authored Contacts.
- **Planted foot** (the keyframe's Contact node): toe/ankle `y` ≈ constant across
  the stance keyframes (no bob), and `tipX` sweeps **monotonically backward**
  (the body moves forward over a fixed foot).
- **Swing foot:** toe `y` rises well above stance level at mid-swing (clears).
- **Knees forward:** for each leg, signed cross of (knee − hip) × (ankle − hip)
  should be **positive** (knee on the +x/front side of the hip→ankle line).
  Negative = recurvatum — flip the `leg_*_lower` sign / make it positive.
- **STEEP-interval flag** — the *other* auto-FLAG, and the usual gate blocker for fast
  overlays (throws/slashes): a bone whose angle travels ≥ **8 rad/phase** between adjacent
  keyframes (`MotionProbe.cs`). The digest's "walk max ≈ 4" line is a reference stat, NOT
  the threshold — don't burn iterations chasing 4. STEEP counts as a gate FLAG. Fix by
  widening the phase gap between keys or splitting the travel across an extra key; the
  shipping snappy overlay `energyball` tops out ≈ 6.3 and is a good ceiling to match.
  Rig reality: a hand cocked truly *behind* the shoulder needs a deep elbow fold whose
  unwind trips STEEP on short clips — author "over-shoulder" throws as raised overhand
  lobs (clearly up, only marginally back, elbow fold carrying the cocked read).
  **How STEEP is computed and what actually fixes it** (five batch workers burned cycles
  on this): rate = WrapAngle(Δauthored-rotation) / Δkeyframe-Time between ADJACENT keys
  (keyframe `Time` is normalized [0,1]; clip `Duration` plays no role). Therefore:
  - **Adding keyframes does NOT help.** Subdividing an interval splits Δrot and Δt
    proportionally — the rate survives (measured: 16.3 → {16, 17.5}). Pigeonhole: if
    total swing > 8 × window width, no key count fixes it.
  - What works: **`retime` the boundary key outward** (widen the real budget — one worker
    cleared a flag with a single retime and zero pose edits), **shrink the swing** (reduce
    the pose's angular distance), or fix a **2π-wrapped joint**: a solved angle far
    outside ±π (e.g. −5.1 instead of +1.18) is positionally identical but blows up the
    rate vs neighbors — reset it via `rot` to the wrapped-equivalent value.
  - If the interval is pinned by the action's real phase timing (hitbox window), only a
    smaller swing works.
  - The `ik` command's per-call "steep interval" WARN fires at >1.2 rad vs a neighbor —
    far below the digest's 8 rad/phase FLAG. During deliberate big pose changes those
    WARNs are noise; the digest is the gate, not the WARN.
  - Because WrapAngle is per-interval, hip-lever rotations are STEEP-safe as long as
    each interval's wrapped delta stays under the ceiling, even if the cumulative spin
    passes π.
  - The runtime spline WRAPS too: `SampleSmooth`'s tangents use `MathHelper.WrapAngle`
    (`Animation/AnimationSampler.cs:131`), so the render takes the same shortest path
    the digest measures — the gate and the drawn motion cannot disagree about wrap.

## Authoring quality — what an attack/pose clip should read like

The digest tells you a pose is *correct* (feet planted, knees forward); these are what make it
look *good*. Aim for all of them, and prefer measuring (IK harness below) over eyeballing.

- **Never pin a limb dead straight.** A `*_lower` at exactly `0` (fully extended arm/leg) reads
  stiff and robotic. Carry a slight elbow/knee bend even at rest and on the follow-through
  (arms ≈ `lower -0.5..-0.9`). Straight only at the very instant of a full-extension strike.
- **Differentiate the stance — don't mirror both legs (or arms) identically.** Equal left/right
  angles look flat and posed. Stagger them like idle: one leg forward, one back
  (`leg_l_upper 1.18` fwd / `leg_r_upper 1.62` back, with different `_lower`/foot too).
  For overlay legs, the batch doc's verbatim idle lower-body block is CANONICAL — live
  `idle.json` has drifted from it (1.064/1.813; both flag-free), so paste the block
  rather than trusting `--from idle` to match the documented numbers.
- **Blend from the locomotion pose.** An overlay attack should START (and, for a looping clip,
  END) close to the idle/run pose so it eases in/out without a pop: make the first keyframe ≈
  idle (or run), and the last keyframe == the first for a seamless loop. Hold the locomotion
  stance (legs, off-hand) through the clip rather than re-inventing it.
- **Beat structure: anticipation → strike → follow-through → recovery.** Don't open on the
  windup. Rest (≈idle) → coil/cock back → extended strike → follow-through → settle back to rest.
- **Reach contrast — coil the windup, extend the strike.** Vary the acting hand's reach from the
  shoulder across the arc: short/coiled at the windup, near-straight (far from the body) at the
  strike so the attack visibly reaches *out*. A strike that stays close to the chest reads weak.
- **Keep the elbow/knee fold sign consistent across keyframes.** If the bend flips sign between
  frames the limb snaps/inverts through the interpolation. (Grid-IK returns either fold for the
  same hand point — constrain `lower` to one sign when solving, e.g. `lower ≤ 0`.)
- **Lean the torso into the strike.** A little `chest` lean toward +X at the strike (back to
  rest after) adds weight; keep the `hip` stable.
- **Only move what the action needs.** A one-arm slash = the other arm held at a constant pose
  and the legs in a held stance, so the acting limb reads clearly against a calm body.
- **Whole-body rotation reads through the `hip`** (Length 0: it rotates chest+legs as a unit
  without translating, and the knee cross-test is rotation-invariant so the digest stays
  meaningful). Partial wobbles only — angles don't wrap, so a full 2π spin can never
  loop-seam; author e.g. `0.35→1.3→0.31→−0.6→0.35` for a tumbling read.
- **Steal signs and magnitudes from shipping clips instead of deriving them.** Unsure which
  way a chest lean or arm swing goes? `digest` 2–3 existing clips that do the move
  (`crouch`, `stab`, `guardretaliate`) and interpolate from their authored values — faster
  and safer than bind math.

**IK by target — use the built-in `ik` command, don't guess angles.** To place a toe/hand at a
point, nudge it from wherever the keyframe currently puts it:
```bash
dotnet MTile.Probe/bin/Debug/net8.0/MTile.Probe.dll ik <clip> <keyTime> <tip> <dx,dy>   # move BY delta
#   ... ik run 0.32 foot_l -2,-3            # toe 2 back + 3 up (rig units, +y DOWN); dry-run prints angles
#   ... ik idle 0 foot_r --to 6,17          # absolute root-local target instead of a delta
#   flags: --write (save into the clip)  --chain a,b,c (override the solved bones; default = the tip's limb)
```
It solves the limb's angles by least squares from the CURRENT keyframe pose (minimal-change: the
rest of the pose is untouched, the fold branch is inherited), reports the achieved point + miss —
an unreachable target returns the closest reachable pose and how far short it fell — and warns on
recurvatum and on solved angles jumping > 1.2 rad from the neighboring keyframes (steep interval).
Workflow: `digest` to read positions → `ik` with a delta → `--write` → `digest` to verify.
(`Animation/PoseIk.cs` is the callable core if a harness needs it, e.g. sweeping an arc of
targets across keyframes the way the slash was authored.)

Three solver gotchas (each cost a calibration worker real iterations):
- **Per-call clamp:** every invocation clamps solved joints to **±2.5 rad from the current
  pose** (`PoseIk` `range`) — this applies to `--to` absolute targets too, so a long reach
  can undershoot with a large reported miss. **Reissue the identical `ik` call**: it reseeds
  from the new pose and converges the rest of the way.
- **The fold branch is inherited from the seed and sticky.** Nudging a hand keeps the current
  elbow/knee fold. To *change* the fold — an overhead reach from a folded crouch arm, or
  shallowing a deep fold — first `rot` the `*_lower` toward straight (or re-seed the
  keyframe: `delkey` + `addkey --from idle`), then IK; the re-solve lands on the intended
  branch.
- **Torso before limbs.** The default chain stops at hip/chest, so a later `rot` of
  `hip`/`chest` on a keyframe moves every already-solved limb target without re-solving
  them — this is not just ordering guidance: a chest `rot` AFTER `ik --write` silently
  detaches the already-written hands from their solved targets (large lever arm). Set
  hip/chest rotation for a keyframe FIRST, then IK the limbs.
- **Cross-keyframe fold coherence.** Solving each keyframe's arm independently toward
  nearby targets can pick visually-different elbow folds per key (e.g. upper −0.06 at
  one key, +3.39 at the next) — each key digests fine but the deltas STEEP-flag. Recipes
  that work: (a) before re-solving a drifted key, reset its chain angles to the
  NEIGHBOR keyframe's values via `rot`, then `ik`; (b) for cramped/short overlays, lock
  the proximal joint via `rot` and solve only the distal bone (`ik --chain arm_r_lower`)
  — sidesteps the ambiguity entirely; (c) keep the hand's distance-from-shoulder roughly
  constant across an arc — a mid-path dip toward the shoulder passes near the fold
  singularity and inflates deltas even with fine subdivision.
- **Tip-bone naming is asymmetric:** the arm's IK tip is `arm_*_lower` (hand = its tip);
  the foot's is `foot_*` (toe). There is no `arm_l` bone.

## Cadence gotchas (CharacterAnimator solver)

- **One planted Contact per keyframe.** Two simultaneous contacts moving opposite
  ways (lead sweeping back + trailing toe-off) cancel in the slip minimizer and
  **freeze** Δφ ≈ 0 (the walk won't advance). Use a single dominant foot.
- **Contact timing = labels + `FeatherWidth` (0.12), NOT foot detection.** A
  contact holds full weight from its keyframe until `nextKeyframe.Time − 0.12`,
  then crossfades 1→0 to the next keyframe over that 0.12, and is removed at
  weight ≤ 1e-3 ([WeightedContactsAtPhase](../../../Animation/CharacterAnimator.cs)).
  The target is captured ONCE when the contact appears and held until it drops.
- **The freeze rule (run/short stance).** If a contact (even at tiny residual
  weight) is still alive when the planted foot **reverses** (toe-off → swings
  forward), its now-stale target can't be reached, the slip term pins Δφ→0, and
  the **phase freezes** at that value. Diagnose by printing per-frame phase — it
  sticks. So the contact must fully fade BEFORE the foot's rearmost point.
- **Extending the pin across the whole stance (the right way).** To keep a foot
  pinned through its full backward sweep (not just the strike instant): put the
  Contact label on the strike AND mid-stance keyframes (consecutive contact
  keyframes stay full weight — they crossfade foot→foot), then make the **toe-off
  keyframe a NO-contact DROP point placed a feather-width before rearmost**. The
  fade then completes over [toe-off−0.12, toe-off], entirely within the backward
  sweep, releasing before the reversal. Find rearmost from the probe (min toe.X /
  where toe vX flips sign) and put the drop keyframe ~0.04 phase before it.
- Slip is **horizontal-only** by design (penalizing the foot's vertical arc froze
  the cadence below run speed).
- Re-authoring a clip shifts its cadence objective, so the experimental
  LM-vs-golden **parity** test (`AnimSolverTests`, only the opt-in `AnimSolver`
  path) can drift out of `[0.85,1.15]`. The production path is golden-section;
  gate on the **behavioral** `RealWalkJson/RealRunJson_AdvancesPhase` tests, not
  parity. Parity is solver-tuning (see Plans/ANIMATION_SOLVER_PLAN.md).

## Key files

- `Animation/MotionProbe.cs` — angles → world (x,y), C1 sampling. `Report` (raw joint/tip
  table + phase velocity), `Digest` (semantic per-keyframe readout + trajectory + auto-flags),
  `Diff` (tip deltas vs a reference clip).
- `MTile.Probe/Program.cs` — headless probe console (NO test runner / rebuild): inspection
  (`digest` / `diff` / `report` / `list` / `anim`) + authoring (`new` / `addkey` / `ik` /
  `contact` / `rot` / `retime` / `delkey` / `dur` / `addcom`). The fast edit→verify loop —
  build once, re-run per edit; clip JSON is never hand-written.
- `Animation/PoseIk.cs` — the IK core behind `ik`: seed-prior least squares over a bone chain.
- `MTile.Tests/Animation/MotionProbeTests.cs` — dumps `.probe/<clip>.digest.md` (every clip),
  `.probe/<clip>.md` (locomotion subset raw tables), and a diff example. The full (slow) sweep.
- `Drawing/Skeleton.cs`, `SkeletonPose.cs`, `Affine2.cs` — rig + FK (R·T·S).
- `Skeletons/biped.json`, `SkeletonStates/*.json` — rig + clips.
- `Animation/CharacterAnimator.cs` — cadence solver / contacts.

## Throwaway harness template

Drop in `MTile.Tests/Animation/Zzz<Thing>.cs`, run, read `.probe/<thing>.md`,
then **delete it**.

```csharp
using System;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// TEMP probe — delete when done. Poses the rig directly and dumps world positions.
public class ZzzThing
{
    [Fact]
    public void Probe()
    {
        var rig = SkeletonExamples.Biped();
        var root = Affine2.FromTRS(Vector2.Zero, 0f, Vector2.One); // origin, +x, scale 1
        var sb = new StringBuilder();
        sb.AppendLine("# +x forward, +y DOWN");

        // Pose one leg at (upper, lower) and report knee/ankle/toe + knee direction.
        foreach (var (up, lo) in new[] { (-0.5f, 0.6f), (0f, 0.2f), (0.5f, 0.5f), (-0.5f, 0.15f) })
        {
            var p = rig.CreatePose(); p.SetToDefault();
            p.Local[rig.IndexOf("leg_l_upper")].Rotation = up;
            p.Local[rig.IndexOf("leg_l_lower")].Rotation = lo;
            var w = p.ComputeWorld(root);
            Vector2 hip   = w[rig.IndexOf("hip")].Translation;
            Vector2 knee  = w[rig.IndexOf("leg_l_upper")].Translation;   // tip = KNEE
            Vector2 ankle = w[rig.IndexOf("leg_l_lower")].Translation;   // tip = ankle
            Vector2 toe   = w[rig.IndexOf("foot_l")].Translation;        // tip = TOE (NOT +Length)
            Vector2 d = ankle - hip; float L = d.Length();
            float side = L > 1e-4f ? ((knee.X-hip.X)*d.Y - (knee.Y-hip.Y)*d.X)/L : 0f;
            sb.AppendLine($"up={up,5:0.00} lo={lo,5:0.00} | knee=({knee.X,5:0.0},{knee.Y,5:0.0})"
                + $" toe=({toe.X,5:0.0},{toe.Y,5:0.0}) side={side,5:0.0} "
                + (side > 0.2f ? "FWD" : side < -0.2f ? "BACK(recurv)" : "straight"));
        }

        string outDir = Path.Combine(FindUp("MTile.sln"), ".probe");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "thing.md"), sb.ToString());
        Assert.True(true);
    }

    private static string FindUp(string marker)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null) { if (File.Exists(Path.Combine(d.FullName, marker))) return d.FullName; d = d.Parent; }
        throw new DirectoryNotFoundException(marker);
    }
}
```

## Observing in the editor

The game moves at run speed, so observe **walk** in the standalone viewer:
```bash
dotnet run --project MTile.Demo -- walk      # open a named clip
```
