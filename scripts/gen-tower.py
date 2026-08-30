#!/usr/bin/env python3
"""Generate the Zeus tower's authored chunk files (Levels/hill_*.txt).

The tower is 150 tiles tall, which is ten chunk rows — far past the point where
hand-drawn ASCII stays correct, and every mistake in it (a stair that steps two
tiles, a doorway one row too short) is silent at load time and only shows up as a
climb that dead-ends. So the geometry is defined once, here, and the .txt files are
build output that happens to be committed.

Why generated rather than ruled: TerrainLoader's rule grammar is a list of
axis-aligned half-planes (`x >= n`, `y <= n`) with no conjunction and no modulo. The
shaft, the ground and the outer walls are all expressible that way and stay in
hill.json as Rules; the decks, stairs and doorways repeat every LEVEL_STEP tiles and
are not.

Run from the repo root:  python3 scripts/gen-tower.py

Geometry (all values are world TILE coordinates, y-down):

    x = -19 .. 19        the deck span, 39 wide
    y = -137 .. 12       the tower, 150 tall; y >= 13 is the ground plane
    shaft  x in [-3, 3]  solid full height, with a doorway at every deck
    decks  every 15 tiles, 2 thick, at y = 13 - 15k for k = 1..10
    stairs one tile of rise per column, alternating sides, 15 columns per level
    doors  DOOR_H rows of clearance through the shaft above every deck AND the ground

The climb is a switchback: up the right-hand stair to a deck, left across the deck
and THROUGH the shaft's doorway, up the left-hand stair to the next deck, and back.
The doorway is what makes that work — without it the shaft splits every deck into
two balconies that never connect, and the tower is unclimbable past level 1.
"""

import os

CHUNK = 16

GROUND_Y   = 13                      # first solid ground row
HEIGHT     = 150                     # the ask: a 150-block tower
TOP_Y      = GROUND_Y - HEIGHT       # -137, the summit deck's surface row
SHAFT_HALF = 3                       # shaft spans x in [-3, 3]
DECK_HALF  = 19                      # decks span x in [-19, 19]
LEVEL_STEP = 15                      # tiles of rise between decks
LEVELS     = HEIGHT // LEVEL_STEP    # 10
DECK_THICK = 2
DOOR_H     = 3                       # rows of clearance through the shaft at a deck
PARAPET_H  = 5                       # summit rim wall, in tiles

# Stair run: from the deck rim (low) inward to the landing beside the shaft (high).
STAIR_IN   = SHAFT_HALF + 2          # 5  — landing column, at deck level
STAIR_OUT  = DECK_HALF               # 19 — rim column, one step above the deck below

STONE, DIRT, EMPTY = 'X', 'D', '.'

assert LEVELS * LEVEL_STEP == HEIGHT, "HEIGHT must divide evenly into LEVEL_STEP"
# One tile of rise per column means the run and the rise must be the same number.
assert STAIR_OUT - STAIR_IN + 1 == LEVEL_STEP, "stair run does not match its rise"


def deck_y(k):
    """Surface row of deck k (k = 1..LEVELS). Deck 0 is the ground plane."""
    return GROUND_Y - k * LEVEL_STEP


def stair_side(k):
    """Which side stair k climbs on. Odd levels climb west, because the player spawns
    on the western plain and the first stair has to be the one they walk into."""
    return -1 if k % 2 == 1 else 1


def stair_columns(k):
    """Columns of the stair climbing from deck k-1 to deck k, and their top rows.

    Rises inward: the rim column sits one tile above the deck below, and each column
    inward is one tile higher, so the landing beside the shaft lands exactly on deck k.
    One tile per column is the same rule the old hill's slope was held to — it is what
    "walkable" means here.
    """
    base = deck_y(k - 1)
    side = stair_side(k)
    for j in range(STAIR_IN, STAIR_OUT + 1):
        yield side * j, base - (STAIR_OUT + 1 - j)


def tile_at(x, y):
    """Material at world tile (x, y), or EMPTY."""
    # Ground plane. Authored chunks have to draw this themselves -- Rules apply only
    # to chunks with no file, so leaving it out would punch a hole under the tower.
    if y >= GROUND_Y:
        return DIRT if y < GROUND_Y + 2 else STONE

    # Summit parapet, two columns thick, on the rim opposite the stairwell. Checked
    # BEFORE the sky cutoff because it is the one piece of the tower that rises above
    # TOP_Y -- put it after and the early return silently deletes it, which looks
    # exactly like a wall that was never asked for.
    if (DECK_HALF - 1 <= abs(x) <= DECK_HALF and x * stair_side(LEVELS) < 0
            and TOP_Y - PARAPET_H <= y < TOP_Y):
        return STONE

    if y < TOP_Y:
        return EMPTY                                  # open sky above the summit

    # Stairs first, drawn solid from the step's top row down to the deck it stands on.
    # Checked before decks so that where the two coincide — the landing column — the
    # answer is the same either way and neither can punch a hole in the other.
    for k in range(1, LEVELS + 1):
        base = deck_y(k - 1)
        for sx, top in stair_columns(k):
            if x == sx and top <= y <= base:
                return STONE

    # Decks: two tiles thick so a beam that clips one doesn't open a hole through it,
    # minus the stairwell each one opens over its own stair.
    for k in range(1, LEVELS + 1):
        dy = deck_y(k)
        if dy <= y < dy + DECK_THICK and abs(x) <= DECK_HALF:
            in_stairwell = (x * stair_side(k) > 0
                            and STAIR_IN < abs(x) <= STAIR_OUT)
            if in_stairwell:
                continue
            return STONE

    # Shaft, minus a doorway above each deck so the switchback can cross it. Level 0
    # is the ground, and its doorway is the tower's front door: without it the shaft
    # walls the base off and the player can never reach the first stair, which starts
    # on the far side.
    if abs(x) <= SHAFT_HALF:
        for k in range(0, LEVELS + 1):
            dy = deck_y(k)
            if dy - DOOR_H <= y < dy:
                return EMPTY
        return STONE

    return EMPTY


def chunk_rows(cx, cy):
    rows = []
    for ty in range(CHUNK):
        y = cy * CHUNK + ty
        rows.append("".join(tile_at(cx * CHUNK + tx, y) for tx in range(CHUNK)))
    return rows


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    levels = os.path.join(root, "Levels")

    cx_lo, cx_hi = -(DECK_HALF // CHUNK) - 1, DECK_HALF // CHUNK
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
