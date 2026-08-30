#!/usr/bin/env python3
"""Generate the Zeus tower's authored chunk files (Levels/hill_*.txt).

The tower is a solid spire: 37 tiles wide where it meets the plain, tapering at
three tiles of rise per tile of run per side until it reaches spire width, then
running straight up as an 11-wide needle to the summit Zeus stands on, 150 tiles
above the ground. No interior, no stairs, no decks — the climb is the outside face.

It is generated rather than hand-drawn because 150 tiles is ten chunk rows, and a
taper that steps twice somewhere is silent at load time: the terrain loads fine and
looks fine, and you find the mistake by climbing into it.

Why generated rather than ruled: TerrainLoader's rule grammar is a list of
axis-aligned half-planes (`x >= n`, `y <= n`) with no conjunction. The ground, the
outer walls and the open sky are expressible that way and stay in hill.json as
Rules; a width that is a function of height is not.

Run from the repo root:  python3 scripts/gen-tower.py

Geometry (all values are world TILE coordinates, y-down):

    y >= 13              the ground plane
    y = 12 .. -137       the tower, 150 rows tall; y = -137 is the summit surface
    half-width           BASE_HALF at the ground, shrinking one tile per TAPER_RISE
                         rows, floored at SPIRE_HALF

Nothing is built above the summit row. The old wide deck carried a rim parapet; an
11-wide tip cannot. A wall on this rim would sit squarely between the statue (which
stands 16px above the surface) and anything off to the side, and every Zeus laser
gates on EnemyAim.HasLineOfSight — so a railing here would blind the boss rather than
fence it in. The tip is bare on purpose.
"""

import os

CHUNK = 16

GROUND_Y   = 13                      # first solid ground row
HEIGHT     = 150                     # the ask: a 150-block tower
TOP_Y      = GROUND_Y - HEIGHT       # -137, the summit surface row
BASE_HALF  = 18                      # 37 tiles across where the spire meets the plain
SPIRE_HALF = 5                       # 11 tiles across for the needle above the taper
TAPER_RISE = 3                       # rows of rise per tile of run per side (3:1)

STONE, DIRT, EMPTY = 'X', 'D', '.'
# The spire body is HARDENED rock, not stone: it is bedrock-grade (10x stone HP),
# unplaceable and — the part that matters here — UNGRABBABLE. That makes the tower
# terrain the player fights *within* rather than *with*: the climb can't be short-cut
# by ripping a staircase out of the face or peeling the spire into throwable clods,
# which is what the block-throw kit does to any ordinary stone wall. The ground plane
# below stays dirt-over-stone so the plain is still normal, workable terrain.
HARD = 'H'

# Where the taper runs out and the needle begins, as rows above the ground surface.
SPIRE_RISE = (BASE_HALF - SPIRE_HALF) * TAPER_RISE     # 39
assert SPIRE_RISE < HEIGHT, "the taper must finish below the summit"


def half_at(y):
    """Half-width of the spire at world row y. The tower spans x in [-half, half],
    so it is 2*half + 1 tiles across — 37 at the base, 11 in the needle."""
    rise = (GROUND_Y - 1) - y                          # 0 for the first row above ground
    return max(SPIRE_HALF, BASE_HALF - rise // TAPER_RISE)


def tile_at(x, y):
    """Material at world tile (x, y), or EMPTY."""
    # Ground plane. Authored chunks have to draw this themselves -- Rules apply only
    # to chunks with no file, so leaving it out would punch a hole under the tower.
    if y >= GROUND_Y:
        return DIRT if y < GROUND_Y + 2 else STONE

    if y < TOP_Y:
        return EMPTY                                   # open sky above the summit

    return HARD if abs(x) <= half_at(y) else EMPTY


def chunk_rows(cx, cy):
    rows = []
    for ty in range(CHUNK):
        y = cy * CHUNK + ty
        rows.append("".join(tile_at(cx * CHUNK + tx, y) for tx in range(CHUNK)))
    return rows


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    levels = os.path.join(root, "Levels")

    cx_lo, cx_hi = -(BASE_HALF // CHUNK) - 1, BASE_HALF // CHUNK
    cy_lo, cy_hi = -((-TOP_Y + CHUNK - 1) // CHUNK), 0

    written = []
    for cx in range(cx_lo, cx_hi + 1):
        for cy in range(cy_lo, cy_hi + 1):
            name = f"hill_{cx}_{cy}.txt"
            with open(os.path.join(levels, name), "w") as f:
                f.write("\n".join(chunk_rows(cx, cy)) + "\n")
            written.append((f"{cx},{cy}", name))

    print(f"wrote {len(written)} chunk files to Levels/")
    print("ChunkFiles block for hill.json:")
    for key, name in written:
        print(f'    "{key}": "{name}",')


if __name__ == "__main__":
    main()
