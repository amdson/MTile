using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The seed-bar cursor ghost (Plans/PLANT_SEEDS_PLAN.md §3): while the local player has the
// seed bar up, the selected seed's silhouette sits in the cell under the cursor, rotated
// onto the face it would root in, tinted legal (green) or illegal (red). Legality is the
// sim's own PlantRules.Evaluate — the same call TryPlant makes — so the ghost cannot
// disagree with the planting. Render-only: reads sim state, writes only the TelegraphList.
public static class PlantGhost
{
    public static void Emit(TelegraphList t, PlayerCharacter player, ChunkMap chunks,
                            IEnumerable<Entity> entities, Vector2 cursorWorld)
    {
        if (player == null || chunks == null || !player.SeedBarActive) return;
        int cx = (int)System.MathF.Floor(cursorWorld.X / Chunk.TileSize);
        int cy = (int)System.MathF.Floor(cursorWorld.Y / Chunk.TileSize);
        var req = new PlantRequest
        {
            Seed = player.SelectedSeed, Owner = player.Id, Faction = player.Faction, Team = player.Team,
            OwnerPos = player.Body.Position, CellX = cx, CellY = cy,
        };
        var verdict = PlantRules.Evaluate(in req, chunks, entities, player.Abilities.Meters, out var site);
        bool ok = verdict == PlantVerdict.Ok;
        var center = new Vector2((cx + 0.5f) * Chunk.TileSize, (cy + 0.5f) * Chunk.TileSize);
        // Oriented onto whatever root the cell has even when another rule rejects it (out
        // of reach, capped); a cell with no root at all is shown upright.
        float rot = PlantRules.TryFindSite(req.Seed, chunks, cx, cy, out var any) ? PlantRules.RotationOf(any.Root) : 0f;
        var tint = (ok ? PlantPalette.Legal : PlantPalette.Illegal) * (ok ? 0.55f : 0.4f);

        PoseTelegraph.Emit(t, Silhouette(req.Seed), center, rot, tint);

        const float h = Chunk.TileSize * 0.5f;
        var edge = (ok ? PlantPalette.Legal : PlantPalette.Illegal) * 0.5f;
        t.Line(center + new Vector2(-h, -h), center + new Vector2(h, -h), edge, 1f);
        t.Line(center + new Vector2(h, -h),  center + new Vector2(h, h),  edge, 1f);
        t.Line(center + new Vector2(h, h),   center + new Vector2(-h, h), edge, 1f);
        t.Line(center + new Vector2(-h, h),  center + new Vector2(-h, -h), edge, 1f);
        if (ok)
        {
            // Mark the root tile it would grow from.
            var rc = site.RootCell;
            var rcC = new Vector2((rc.X + 0.5f) * Chunk.TileSize, (rc.Y + 0.5f) * Chunk.TileSize);
            t.Line(center, Vector2.Lerp(center, rcC, 0.9f), edge, 1f);
        }
        else
        {
            t.Line(center + new Vector2(-h * 0.6f, -h * 0.6f), center + new Vector2(h * 0.6f, h * 0.6f), edge, 1f);
            t.Line(center + new Vector2(h * 0.6f, -h * 0.6f), center + new Vector2(-h * 0.6f, h * 0.6f), edge, 1f);
        }
    }

    // Each seed's ghost silhouette. One line per kind — M3-M6 swap in their figure's.
    public static Pose Silhouette(SeedKind seed) => seed switch
    {
        SeedKind.Spitter => SpitterFigure.Silhouette,
        SeedKind.Snapper => SnapperAnims.Silhouette,
        _                => GenericSilhouette,
    };

    private static Pose _generic;
    private static Pose GenericSilhouette => _generic ??= BuildGeneric();

    private static Pose BuildGeneric()
    {
        var p = new Pose();
        float ry = PlantFigureKit.RootY;
        p.Line(new Vector2(0f, ry), new Vector2(0f, ry - 9f), Color.White, 1.5f);
        p.Ring(new Vector2(0f, ry - 11f), 2.5f, Color.White, 8, 1f);
        return p;
    }
}
