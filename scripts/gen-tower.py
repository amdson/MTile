#!/usr/bin/env python3
"""Generate the Zeus tower's authored chunk files (Levels/hill_*.txt).

The tower is a solid spire: 37 tiles wide where it meets the plain, tapering at
three tiles of rise per tile of run per side until it is 11 across, then easing off
and dwindling the rest of the way to a single tile at the summit Zeus stands on,
150 tiles above the ground. No interior, no stairs, no decks — the climb is the
outside face.

The dwindle is not decoration. A constant-width pillar hides its own face from its
own tip: the sight line from the statue down to a climber on the flank runs through
the pillar's bulk, so Zeus could never see anyone on the last hundred tiles of the
climb. A face that keeps receding is one the summit can look down.

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
                         rows until it reaches SPIRE_HALF, then straight down to 0
                         (a one-tile tip) across the rest of the height

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
SPIRE_HALF = 5                       # 11 tiles across where the lower taper hands over
TAPER_RISE = 3                       # rows of rise per tile of run per side (3:1)

STONE, DIRT, EMPTY = 'X', 'D', '.'
# The spire body is HARDENED rock, not stone: it is bedrock-grade (10x stone HP),
# unplaceable and — the part that matters here — UNGRABBABLE. That makes the tower
# terrain the player fights *within* rather than *with*: the climb can't be short-cut
# by ripping a staircase out of the face or peeling the spire into throwable clods,
# which is what the block-throw kit does to any ordinary stone wall. The ground plane
# below stays dirt-over-stone so the plain is still normal, workable terrain.
HARD = 'H'

# Where the lower taper hands over to the spire, as rows above the ground surface.
SPIRE_RISE = (BASE_HALF - SPIRE_HALF) * TAPER_RISE     # 39
TOP_RISE   = HEIGHT - 1                                # rise of the summit row, 149
assert SPIRE_RISE < TOP_RISE, "the lower taper must finish below the summit"


def half_at(y):
    """Half-width of the spire at world row y. The tower spans x in [-half, half], so it
    is 2*half + 1 tiles across: 37 at the base, 11 where the taper hands over, 1 at the tip.

    Two rates, because the rise the design is stated in (3:1) spends the whole width in a
    third of the height. The lower taper runs at that ratio down to SPIRE_HALF; the spire
    then eases off and spends its last SPIRE_HALF tiles over everything that is left, so
    the face keeps receding all the way to the summit instead of standing up as a pillar.
    Integer rounding rather than float — the .txt files are build output and have to come
    out identical on every machine that regenerates them."""
    rise = (GROUND_Y - 1) - y                          # 0 for the first row above ground
    if rise < SPIRE_RISE:
        return BASE_HALF - rise // TAPER_RISE
    # SPIRE_HALF at SPIRE_RISE, 0 at the summit, rounded half-up in between.
    span = TOP_RISE - SPIRE_RISE
    return (SPIRE_HALF * (TOP_RISE - rise) * 2 + span) // (2 * span)


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
