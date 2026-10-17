# Block ownership and spike overlays

Two block mechanics that make terrain matter in a fight beyond barricades. Decided with
the user 2026-10-17; the numbers below are starting points, kept as named constants.

## 1. Ownership (recently placed blocks are "yours")

- **What counts.** Any cell a player creates — paint, place, eruption/burst build — is
  owned by that player from the frame it is placed. Pre-existing terrain is unowned.
  Breaking or digging a cell clears its owner. Re-placing over a cell re-stamps it.
- **Fade.** Ownership strength `s = 0.5^(age / 60 s)`, computed on demand from the placed
  frame (nothing ticks). Below `s < 0.1` (~3.3 min) the claim is gone; entries are pruned
  on a fixed cadence so the table stays small.
- **Effect: owned blocks don't resist the owner's drag.** The block grab
  (`BlockGrabAction` → `PullPointEntity`: tether/glue wear per cell, break-out) scales each
  cell's resistance by `(1 - s)` when the grabber owns it. Owned walls rip out sooner and
  in bigger groups, so the thrown clod carries more mass and hits harder. Mixed clods scale
  per cell. Enemy-owned cells resist normally (no extra penalty, for now).
- **State.** A sparse value table `cell → (owner, placedFrame)`, snapshotted like the
  other sparse per-tile tables (`TileDamage`, `FoamDecay`…). Deterministic iteration and
  pruning. Must survive rollback.
- **Look.** Render-only: a hollow ring in the owner's colour inside each owned cell, alpha
  tracking `s`. Placeholder art.

## 2. Spike overlays

- **A placement option.** Spikes are selectable in the block picker alongside Stone / Dirt
  / Sand / Foam. Placing spikes targets an **existing solid cell** (never air) and overlays
  it — so spikes can't be spammed into the air to crush people.
- **Cost.** About **2×** a Stone placement's build cost (`configs/material_strengths.json`
  / the mass economy).
- **Reinforcement.** A spiked cell is tougher than its base material (start at 2× its
  HP). The overlay goes when the cell breaks.
- **Contact damage, opposing side only.** Bodies of an opposing faction/team touching a
  spiked cell take damage; the placer's side (and, in co-op, both players) never does.
  It is real contact damage — standing on or brushing spikes hurts, so they're an obstacle
  to avoid — **scaled up with impact velocity** (knocked into spikes hurts a lot more).
  Starting numbers: ~0.25 HP at walking contact, rising with the normal impact speed to
  ~1.5 HP at ~400 px/s, with a per-victim cooldown (~0.5 s) so contact ticks rather than
  shredding, and a small knockback away from the surface.
- **State.** Spiked cells and their owning side in a sparse snapshotted table; contact
  cooldowns snapshotted too. Deterministic.
- **Look.** Small triangles on the spiked cell's exposed faces, tinted by side.

## Later

- **Thorns:** growth into neighbouring air over time with contact damage (zone denial).
- Ownership interactions: owned proximity mines, remote-detonated owned blocks.
