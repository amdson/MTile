using System;
using Microsoft.Xna.Framework;

namespace MTile;

// plant_show — a DEBUG showcase for visual QA of the plants (Plans/PLANT_SEEDS_PLAN.md §10).
// Not in the lobby. Eight dirt rooms in a row, each a little narrower than one screen at
// gameplay zoom and walled off from the next (the walls block spitter sightlines, so each
// room only acts on the player standing in it). The primary player (team 0, blue) is the
// camera; the AFK second player (team 1, red) stands in room 5. One stage name per room
// puts the primary there:
//
//   plant_show          room 0  nursery: spitter + snapper x seed / half-grown / mature, both
//                               teams (team 0 left, team 1 right) — idle sway, growth stages
//   plant_show_faces    room 1  roots on a ceiling overhang and both faces of a pillar; one
//                               plant buried under stone, one under dirt; two wilting
//   plant_show_bite     room 2  red snapper on the floor beside the player + red spitter
//   plant_show_wallbite room 3  red snapper on a pillar face beside the player + red spitter
//                               on the room wall
//   plant_show_ceilbite room 4  red snapper + red spitter under a low ceiling slab
//   plant_show_p2       room 5  blue snapper beside the AFK red player, blue wall spitter
//   plant_show_thorns   room 6  a full and a part-way thorn patch per team (part-way ones on
//                               a pillar), hand-placed spikes of both teams beside them
//   plant_show_eater    room 7  each team's block-eater chewing a stone wall the other owns
//   plant_show_ghost    room 1  as plant_show_faces, with the primary's seed bar up
//
// Deterministic: plants are spawned directly (bypassing caps and costs) and pre-grown by
// stepping their own Update with no world, thorn patches are pre-spread by ticking the
// patch table ahead. Buried plants drain to death about 10 s in — capture before that.
public static partial class Stages
{
    private const int PsW = 320, PsH = 48, PsFloor = 36, PsRoom = 38;
    private const char PsAir = ' ', PsDirt = 'D', PsStone = 'S', PsHard = 'H';

    // Room k's centre column.
    private static int PsCx(int k) => PsRoom * k + 20;

    private static void RegisterPlantShow()
    {
        var p2Home = DuelSpawn(PsCx(5), PsFloor);
        (string name, int room, int col)[] views =
        {
            ("plant_show", 0, PsCx(0) + 4), ("plant_show_faces", 1, PsCx(1) + 4),
            ("plant_show_bite", 2, PsCx(2)), ("plant_show_wallbite", 3, PsCx(3)),
            ("plant_show_ceilbite", 4, PsCx(4)), ("plant_show_p2", 5, PsCx(5) + 5),
            ("plant_show_thorns", 6, PsCx(6) + 3), ("plant_show_eater", 7, PsCx(7) - 1),
            ("plant_show_ghost", 1, PsCx(1) + 4),
        };
        foreach (var (name, _, col) in views)
        {
            bool ghost = name == "plant_show_ghost";
            Register(new Stage {
                Name          = name,
                IdleSecondary = true,
                Terrain       = () => DenseChunks(PsW, PsH, PlantShowCell),
                PlayerSpawn   = DuelSpawn(col, PsFloor),
                SecondSpawn   = p2Home,
                Populate      = g => PopulatePlantShow(g, p2Home, ghost),
            });
        }
    }

    private static char PlantShowCell(int x, int y)
    {
        if (y >= PsFloor + 4) return PsHard;
        if (y >= PsFloor) return PsDirt;
        if (x >= PsRoom * 8 + 3) return PsHard;                      // east end
        if (x % PsRoom < 3) return PsDirt;                           // room walls (dirt: wall roots)
        int k = x / PsRoom, cx = PsCx(k);
        switch (k)
        {
            case 1:
                if (y is 26 or 27 && x <= PsRoom + 3 + 11) return PsDirt;           // ceiling overhang
                if (y >= 27 && (x == cx || x == cx + 1)) return PsDirt;              // pillar
                if (y == 35 && x == cx + 8) return PsStone;                          // buried under stone
                if (y == 35 && x == cx + 11) return PsDirt;                          // buried under dirt
                break;
            case 3:
                if (y >= 27 && (x == cx + 2 || x == cx + 3)) return PsDirt;          // pillar
                break;
            case 4:
                if (y is 30 or 31 && x >= cx - 7 && x <= cx + 6) return PsDirt;      // low ceiling slab
                break;
            case 6:
                if (y >= 27 && (x == cx - 1 || x == cx)) return PsDirt;              // pillar
                break;
            case 7:
                if (y >= 30 && ((x >= cx - 12 && x <= cx - 9) || (x >= cx + 7 && x <= cx + 10))) return PsStone;
                break;
        }
        return PsAir;
    }

    private static void PopulatePlantShow(Simulation g, Vector2 p2Home, bool seedBar)
    {
        var blue = g.Player;
        var red  = g.SecondaryPlayers.Count > 0 ? g.SecondaryPlayers[0].Player : g.AddSecondaryPlayer(p2Home).Player;
        g.AddTicker(_ => { if (!red.IsAlive) red.Respawn(p2Home); });
        if (seedBar) blue.SeedBarActive = true;
        var chunks = g.Chunks;

        void Plant(SeedKind kind, PlayerCharacter owner, int x, int y, PlantRoot root, float grow01, float health01 = 1f)
        {
            // The ghost view leaves the primary plantless, so its planting cap is free and
            // the cursor ghost can show a LEGAL cell.
            if (seedBar && owner == blue) return;
            var site = new PlantSite(x, y, root);
            PlantEntity p = kind == SeedKind.Snapper
                ? new SnapperPlant(site, owner.Id, owner.Faction, owner.Team)
                : new SpitterPlant(site, owner.Id, owner.Faction, owner.Team);
            int steps = (int)MathF.Ceiling(grow01 * p.GrowSeconds / Simulation.FixedDt) + (grow01 >= 1f ? 2 : 0);
            for (int i = 0; i < steps; i++) p.Update(Simulation.FixedDt, null, null, null);
            if (health01 < 1f) p.Health = p.MaxHealth * health01;
            g.SpawnEntity(p);
        }

        // ── room 0: nursery ──
        int c = PsCx(0);
        const int Fl = PsFloor - 1;   // the open row on the floor
        float[] stages = { 0f, 0.5f, 1f };
        for (int i = 0; i < 3; i++)
        {
            Plant(SeedKind.Spitter, blue, c - 16 + 3 * i, Fl, PlantRoot.Below, stages[i]);
            Plant(SeedKind.Snapper, blue, c - 7 + 3 * i,  Fl, PlantRoot.Below, stages[i]);
            Plant(SeedKind.Spitter, red,  c + 6 + 2 * i,  Fl, PlantRoot.Below, stages[i]);
            Plant(SeedKind.Snapper, red,  c + 12 + 2 * i + (i == 2 ? 1 : 0), Fl, PlantRoot.Below, stages[i]);
        }

        // ── room 1: faces, burial, wilt ──
        c = PsCx(1);
        int a = PsRoom + 3;
        Plant(SeedKind.Spitter, blue, a + 3, 28, PlantRoot.Above, 1f);
        Plant(SeedKind.Snapper, red,  a + 8, 28, PlantRoot.Above, 1f);
        Plant(SeedKind.Spitter, blue, c - 1, 33, PlantRoot.Right, 1f);
        Plant(SeedKind.Snapper, red,  c - 1, 29, PlantRoot.Right, 1f);
        Plant(SeedKind.Spitter, red,  c + 2, 33, PlantRoot.Left, 1f);
        Plant(SeedKind.Snapper, blue, c + 2, 29, PlantRoot.Left, 1f);
        Plant(SeedKind.Spitter, blue, c + 8, Fl, PlantRoot.Below, 1f);     // under stone
        Plant(SeedKind.Snapper, red,  c + 11, Fl, PlantRoot.Below, 1f);    // under dirt
        Plant(SeedKind.Spitter, red,  c + 14, Fl, PlantRoot.Below, 1f, 0.25f);   // wilting
        Plant(SeedKind.Snapper, blue, c + 17, Fl, PlantRoot.Below, 1f, 0.25f);

        // ── room 2: floor bite on the primary ──
        c = PsCx(2);
        Plant(SeedKind.Snapper, red, c + 1, Fl, PlantRoot.Below, 1f);
        Plant(SeedKind.Spitter, red, c + 12, Fl, PlantRoot.Below, 1f);

        // ── room 3: wall bite ──
        c = PsCx(3);
        Plant(SeedKind.Snapper, red, c + 1, 33, PlantRoot.Right, 1f);
        Plant(SeedKind.Spitter, red, PsRoom * 3 + 3, 30, PlantRoot.Left, 1f);

        // ── room 4: ceiling bite ──
        c = PsCx(4);
        Plant(SeedKind.Snapper, red, c + 1, 32, PlantRoot.Above, 1f);
        Plant(SeedKind.Spitter, red, c - 5, 32, PlantRoot.Above, 1f);

        // ── room 5: the blue plants on the AFK red player ──
        c = PsCx(5);
        Plant(SeedKind.Snapper, blue, c - 1, Fl, PlantRoot.Below, 1f);
        Plant(SeedKind.Spitter, blue, PsRoom * 6 - 1, 31, PlantRoot.Right, 1f);

        // ── room 6: thorns ──
        c = PsCx(6);
        byte tb = (byte)blue.Team, tr = (byte)red.Team;
        chunks.TryPlantThornPatch(c - 14, Fl, tb);           // full (pre-spread below)
        chunks.TryPlantThornPatch(c + 13, Fl, tr);
        for (int f = 1; f <= ThornPatches.MaxCellsPerPatch + 1; f++)
            chunks.Thorns.Tick(chunks, f * ThornPatches.SpreadIntervalFrames);
        chunks.TryPlantThornPatch(c - 2, 31, tb);            // part-way: grow live, up a pillar face
        chunks.TryPlantThornPatch(c + 1, 31, tr);
        for (int x = c - 8; x <= c - 6; x++) chunks.TryRequestSpike(x, Fl, tb);   // hand-placed
        for (int x = c + 5; x <= c + 7; x++) chunks.TryRequestSpike(x, Fl, tr);

        // ── room 7: block-eaters ──
        c = PsCx(7);
        int now = chunks.TerrainFrame;
        byte ob = BlockOwnership.OwnerFor(blue.Faction), orr = BlockOwnership.OwnerFor(red.Faction);
        for (int y = 30; y <= 35; y++)
        {
            for (int x = c - 12; x <= c - 9; x++) chunks.Ownership.Stamp(x, y, orr, now);
            for (int x = c + 7; x <= c + 10; x++) chunks.Ownership.Stamp(x, y, ob, now);
        }
        chunks.TryPlantBlockEater(c - 11, 33, ob);
        chunks.TryPlantBlockEater(c + 8, 32, orr);
    }

    // A dense w x h tile map (both multiples of 16) from a per-cell char: D dirt, S stone,
    // H hardened, space air.
    private static ChunkMap DenseChunks(int w, int h, Func<int, int, char> cell)
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
                        if (c == PsAir) continue;
                        ch.Tiles[lx, ly].IsSolid = true;
                        ch.Tiles[lx, ly].Type = c switch
                        {
                            PsDirt  => TileType.Dirt,
                            PsStone => TileType.Stone,
                            _       => TileType.Hardened,
                        };
                    }
                chunks[ch.ChunkPos] = ch;
            }
        return chunks;
    }
}
