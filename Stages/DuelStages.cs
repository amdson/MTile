using System;
using Microsoft.Xna.Framework;

namespace MTile;

// Pure PvP rooms: terrain and two players, no other entities. Built in code (like depths)
// so both peers construct the identical map. Both are Versus stages, so either player
// respawns at their own start after a kill.
//   duel_hall  — a large roofed stone hall (144 x 32 tiles open), Hardened beyond a stone skin.
//   duel_plain — a flat stone plain 500 tiles wide under open sky, Hardened end walls.
public static partial class Stages
{
    private const char DuelAir = ' ', DuelStone = 'X', DuelHard = 'H';

    private static void RegisterDuels()
    {
        // ── duel_hall ──
        const int hallW = 160, hallH = 64, hallX0 = 8, hallX1 = 152, hallTop = 20, hallFloor = 52;
        Register(new Stage {
            Name        = "duel_hall",
            Versus      = true,
            Terrain     = () => DuelChunks(hallW, hallH, (x, y) =>
                x >= hallX0 && x < hallX1 && y >= hallTop && y < hallFloor ? DuelAir
              : x >= hallX0 - 3 && x < hallX1 + 3 && y >= hallTop - 3 && y < hallFloor + 3 ? DuelStone
              : DuelHard),
            PlayerSpawn = DuelSpawn(hallX0 + 36, hallFloor),
            SecondSpawn = DuelSpawn(hallX1 - 36, hallFloor),
            Populate    = _ => { },
        });

        // ── duel_plain ──
        const int plainW = 512, plainH = 48, plainFloor = 32, plainWall = 6;
        Register(new Stage {
            Name        = "duel_plain",
            Versus      = true,
            Terrain     = () => DuelChunks(plainW, plainH, (x, y) =>
                y >= plainFloor + 4 ? DuelHard
              : y >= plainFloor ? DuelStone
              : x < plainWall || x >= plainW - plainWall ? DuelHard
              : DuelAir),
            PlayerSpawn = DuelSpawn(plainW / 2 - 40, plainFloor),
            SecondSpawn = DuelSpawn(plainW / 2 + 40, plainFloor),
            Populate    = _ => { },
        });
    }

    // Body centre standing on the floor row `floorRow` at tile column `tx`.
    private static Vector2 DuelSpawn(int tx, int floorRow)
        => new(tx * Chunk.TileSize + Chunk.TileSize * 0.5f, floorRow * Chunk.TileSize - 20f);

    // A dense w x h tile map (both multiples of 16) from a per-cell char function.
    private static ChunkMap DuelChunks(int w, int h, Func<int, int, char> cell)
    {
        var chunks = new ChunkMap();
        for (int cx = 0; cx < w / 16; cx++)
            for (int cy = 0; cy < h / 16; cy++)
            {
                var ch = new Chunk { ChunkPos = new Point(cx, cy) };
                for (int lx = 0; lx < 16; lx++)
                    for (int ly = 0; ly < 16; ly++)
                    {
                        char c = cell(cx * 16 + lx, cy * 16 + ly);
                        if (c == DuelAir) continue;
                        ch.Tiles[lx, ly].IsSolid = true;
                        ch.Tiles[lx, ly].Type = c == DuelHard ? TileType.Hardened : TileType.Stone;
                    }
                chunks[ch.ChunkPos] = ch;
            }
        return chunks;
    }
}
