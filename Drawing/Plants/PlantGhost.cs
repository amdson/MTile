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
        // A live planting channel (BlockPlaceAction seed mode): the ghost stays on the
        // channel's cell and, for the siege pod, aims from that cell to the cursor — exactly
        // what completion plants.
        bool aiming = player.CurrentAction is BlockPlaceAction
                      && BlockPlaceAction.SeedChannelProgress(player.CurrentActionVars) >= 0f;
        if (aiming)
        {
            var v = player.CurrentActionVars;
            cx = (int)v.OriginCell.X;
            cy = (int)v.OriginCell.Y;
        }
        // Out in open air the ghost is just noise: draw nothing unless the cursor is near
        // ground. A live channel keeps its own site ghost wherever the cursor wanders.
        else if (!NearGround(chunks, cx, cy)) return;
        var drag = SeedPlanting.AimFrom(cx, cy, cursorWorld);
        // The same request + verdict the seed-crouch gate and the channel use: legal FROM A
        // CROUCH (the right-click crouches the player itself), so standing is not red. In
        // the air a right-click does nothing, so it is.
        var req = SeedPlanting.CrouchRequest(player.SelectedSeed, player.Id, player.Faction, player.Team,
                                             player.Body, chunks, cx, cy, drag);
        var verdict = PlantRules.Evaluate(in req, chunks, entities, player.Abilities.Meters, out var site);
        bool ok = verdict == PlantVerdict.Ok && player.IsGrounded;
        var center = new Vector2((cx + 0.5f) * Chunk.TileSize, (cy + 0.5f) * Chunk.TileSize);
        // Oriented onto whatever root the cell has even when another rule rejects it (out
        // of reach, capped); a cell with no root at all is shown upright.
        // A legal site uses the face planting would actually pick (the in-reach one).
        var any = site;
        bool rooted = verdict == PlantVerdict.Ok || PlantRules.TryFindSite(req.Seed, chunks, cx, cy, out any);
        float rot = rooted ? PlantRules.RotationOf(any.Root) : 0f;
        var tint = (ok ? PlantPalette.Legal : PlantPalette.Illegal) * (ok ? 0.55f : 0.4f);

        if (req.Seed == SeedKind.SiegePod)
        {
            // The pod takes its aim, and shows the lance it would fire from the would-be
            // muzzle (the same Preview its growth telegraph draws). Brighter while aiming.
            var podSite = rooted ? any : new PlantSite(cx, cy, PlantRoot.Below);
            var aim = SiegePodPlant.AimFor(podSite, drag);
            PoseTelegraph.Emit(t, SiegePodAnims.Silhouette(System.MathF.Atan2(aim.Y, aim.X) - rot), center, rot, tint);
            if (rooted) SiegeBlast.Telegraph(t, chunks, SiegePodPlant.MuzzleFor(podSite), aim, aiming ? 0.6f : 0.15f);
        }
        else
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

    // The ghost shows only when the cursor's cell is solid or has a solid cell within this
    // many tiles (Chebyshev) of it.
    public const int GroundProximityTiles = 1;

    private static bool NearGround(ChunkMap chunks, int cx, int cy)
    {
        for (int dy = -GroundProximityTiles; dy <= GroundProximityTiles; dy++)
        for (int dx = -GroundProximityTiles; dx <= GroundProximityTiles; dx++)
            if (chunks.GetCellState(cx + dx, cy + dy) == TileState.Solid) return true;
        return false;
    }

    // Each seed's ghost silhouette. One line per kind — M3-M6 swap in their figure's.
    public static Pose Silhouette(SeedKind seed) => seed switch
    {
        SeedKind.Spitter => SpitterFigure.Silhouette,
        SeedKind.Snapper => SnapperAnims.Silhouette,
        SeedKind.SiegePod => SiegePodAnims.Silhouette(-MathHelper.PiOver2),   // default aim: straight up
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
