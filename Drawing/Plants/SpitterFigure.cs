using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace MTile;

// The spitter's code-built figure (Plans/PLANT_SEEDS_PLAN.md §10), PrismFigure style: one
// SpriteAnimation per (pose, facing), each frame a Pose built by Frame(...) from a few
// named scalars. Drawn upright in the cell frame (PlantFigureKit); PlantEntity.SyncSprite
// rotates it onto the root face. Its jobs, in order:
//   * telegraph — growth stage (seed → three sprout sizes → full), the spit wind-up (the
//     pod swells, the spout opens and glows), dormant (closed bud, outline only, so it
//     reads through the block covering it), wilting (browned, drooping);
//   * differentiate — a bulbous pod on a single curved stem with a spout is the spitter's
//     silhouette; the team colour rides the pod's rim and the stem collar.
// One instance per team, built lazily on first draw and cached (render-only data).
public sealed class SpitterFigure
{
    private static readonly Dictionary<int, SpitterFigure> _byTeam = new();

    public static SpitterFigure For(int team)
    {
        if (!_byTeam.TryGetValue(team, out var f)) _byTeam[team] = f = new SpitterFigure(team);
        return f;
    }

    private const int SproutStages = 3;
    private readonly Color _team;
    private readonly SpriteAnimation[,] _anims = new SpriteAnimation[6, 2];
    private readonly SpriteAnimation[,] _sprouts = new SpriteAnimation[SproutStages, 2];

    private SpitterFigure(int team)
    {
        _team = PlantPalette.Team(team);
        foreach (PlantPose k in Enum.GetValues(typeof(PlantPose)))
            for (int f = 0; f < 2; f++)
                _anims[(int)k, f] = Build(k, f == 0 ? -1 : 1);
        for (int s = 0; s < SproutStages; s++)
            for (int f = 0; f < 2; f++)
            {
                float g = 0.35f + 0.6f * (s + 0.5f) / SproutStages;
                int facing = f == 0 ? -1 : 1;
                _sprouts[s, f] = new SpriteAnimation(new[] {
                    Frame(facing, grow: g, sway: -0.5f),
                    Frame(facing, grow: g, sway: 0.5f) }, 0.45f, true);
            }
    }

    // growth01 picks the sprout stage; windup is unused (the Act animation is timed to the
    // wind-up and restarts when the pose changes).
    public SpriteAnimation Get(PlantPose pose, int facing, float growth01 = 1f, float windup = 0f)
    {
        int f = facing < 0 ? 0 : 1;
        if (pose == PlantPose.Sprout)
        {
            int s = Math.Clamp((int)((growth01 - 0.2f) / 0.8f * SproutStages), 0, SproutStages - 1);
            return _sprouts[s, f];
        }
        return _anims[(int)pose, f];
    }

    // The ghost's silhouette: the mature idle frame, facing right.
    public static Pose Silhouette => _silhouette ??= For(0).Frame(1, grow: 1f, sway: 0f);
    private static Pose _silhouette;

    private SpriteAnimation Build(PlantPose kind, int facing) => kind switch
    {
        PlantPose.Seed    => new SpriteAnimation(new[] {
            SeedFrame(0f), SeedFrame(1f) }, 0.5f, true),
        PlantPose.Sprout  => new SpriteAnimation(new[] { Frame(facing, grow: 0.6f, sway: 0f) }, 1f, true),
        PlantPose.Idle    => new SpriteAnimation(new[] {
            Frame(facing, grow: 1f, sway: -1f),
            Frame(facing, grow: 1f, sway: -0.3f, swell: 0.05f),
            Frame(facing, grow: 1f, sway: 0.5f,  swell: 0.1f),
            Frame(facing, grow: 1f, sway: 1f,    swell: 0.05f),
            Frame(facing, grow: 1f, sway: 0.3f),
            Frame(facing, grow: 1f, sway: -0.5f) }, 0.22f, true),
        // Timed to PlantTuning.SpitterWindup: swell, open, glow; the last frame holds.
        PlantPose.Act     => new SpriteAnimation(new[] {
            Frame(facing, grow: 1f, sway: 0f, swell: 0.3f),
            Frame(facing, grow: 1f, sway: 0f, swell: 0.6f, open: 0.4f),
            Frame(facing, grow: 1f, sway: 0f, swell: 0.85f, open: 0.8f),
            Frame(facing, grow: 1f, sway: 0f, swell: 1f, open: 1f) },
            PlantTuning.SpitterWindup / 4f, false),
        PlantPose.Dormant => new SpriteAnimation(new[] { DormantFrame(facing) }, 1f, true),
        PlantPose.Wilt    => new SpriteAnimation(new[] {
            Frame(facing, grow: 1f, sway: 0f,   droop: 0.8f),
            Frame(facing, grow: 1f, sway: 0.3f, droop: 0.9f) }, 0.6f, true),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // grow: 0..1 size (sprout stages). sway: -1..1 stem lean. swell: 0..1 pod inflation
    // (the wind-up). open: 0..1 spout opening + glow. droop: 0..1 wilt (head sags toward
    // the facing side, leaves fall, colours brown).
    private Pose Frame(int facing, float grow, float sway, float swell = 0f, float open = 0f, float droop = 0f)
    {
        var p = new Pose();
        float ry = PlantFigureKit.RootY;
        var stem = Color.Lerp(PlantPalette.Stem, PlantPalette.Wilt, droop * 0.8f);
        var leaf = Color.Lerp(PlantPalette.Leaf, PlantPalette.Wilt, droop);
        var leafDark = Color.Lerp(PlantPalette.LeafDark, PlantPalette.Soil, droop);
        var pod  = Color.Lerp(new Color(110, 185, 70), PlantPalette.Wilt, droop);

        PlantFigureKit.SoilTuft(p, 0.8f + 0.2f * grow);

        // Base leaves: a pair splayed low, lifting a little with the sway; they fall when wilting.
        float leafLen = 3.5f + 3.5f * grow;
        float lift = 0.35f - 0.8f * droop;
        PlantFigureKit.Leaf(p, new Vector2(-0.8f, ry - 1f), MathHelper.Pi + lift - 0.08f * sway, leafLen, 2.2f, leaf, leafDark, droop);
        PlantFigureKit.Leaf(p, new Vector2(0.8f, ry - 1f), -lift - 0.08f * sway, leafLen, 2.2f, leaf, leafDark, droop);

        // Stem up to the pod.
        float height = 6f + 9f * grow;
        var baseP = new Vector2(0f, ry - 0.5f);
        var head  = new Vector2(sway * 1.3f + facing * droop * 4f, ry - height + droop * 5f);
        var dir   = PlantFigureKit.Stem(p, baseP, head, bend: sway * 1.2f - facing * droop * 2.5f,
                                        color: stem, thick0: 2f, thick1: 1.3f);
        PlantFigureKit.Collar(p, Vector2.Lerp(baseP, head, 0.35f), dir, _team);
        // A small mid-stem leaf on the back side once it is past half grown.
        if (grow > 0.55f)
            PlantFigureKit.Leaf(p, Vector2.Lerp(baseP, head, 0.6f), facing > 0 ? MathHelper.Pi + 0.6f : -0.6f,
                                2.5f + 2f * grow, 1.6f, leaf, leafDark, droop);

        // The pod: body, team rim, a darker cap; it swells through the wind-up.
        float r = (1.6f + 2.2f * grow) * (1f + 0.35f * swell);
        var podC = head + new Vector2(0f, -r * 0.6f);
        p.Disc(podC, r, pod);
        p.Disc(podC + new Vector2(-facing * r * 0.35f, -r * 0.35f), r * 0.45f, PlantFigureKit.Scale(pod, 1.25f));
        p.Ring(podC, r + 0.4f, _team, 10, 1f);
        if (grow < 0.6f) return p;   // young sprouts have no spout yet

        // Spout: a short barrel on the facing side, angled up; opens and glows on the wind-up.
        var spoutDir = Vector2.Normalize(new Vector2(facing * 0.8f, -0.6f + 0.5f * droop));
        var spoutBase = podC + spoutDir * (r * 0.7f);
        float spoutLen = 2.2f + 1.2f * swell;
        p.Line(spoutBase, spoutBase + spoutDir * spoutLen, PlantPalette.StemDark, 2.4f + 0.8f * open);
        p.Line(spoutBase, spoutBase + spoutDir * spoutLen, pod, 1.2f + 0.6f * open);
        if (open > 0f)
            p.Disc(spoutBase + spoutDir * (spoutLen + 0.6f), 0.8f + 1.2f * open,
                   Color.Lerp(PlantPalette.Spit, Color.White, 0.3f * open));
        // An eye-ish pore on the facing side: what makes it read as a creature.
        p.Disc(podC + new Vector2(facing * r * 0.3f, r * 0.15f), 0.7f, PlantPalette.StemDark);
        return p;
    }

    // A seed in the soil: a speck that pulses its team colour so a fresh plant reads as
    // owned from the first frame.
    private Pose SeedFrame(float pulse)
    {
        var p = new Pose();
        PlantFigureKit.SoilTuft(p, 0.8f);
        float ry = PlantFigureKit.RootY;
        p.Box(new Vector2(0f, ry - 2.2f), new Vector2(2.6f, 2.6f), MathHelper.PiOver4, PlantPalette.Seed);
        p.Line(new Vector2(0f, ry - 3.4f), new Vector2(0.6f, ry - 5f - pulse), PlantPalette.Leaf, 1f);
        p.Ring(new Vector2(0f, ry - 2.2f), 2.6f + 0.8f * pulse, _team * (0.5f + 0.4f * pulse), 8, 1f);
        return p;
    }

    // Buried: a closed bud in outline only, greyed, so the block over it still reads as a
    // block while the plant inside stays visible.
    private Pose DormantFrame(int facing)
    {
        var p = new Pose();
        float ry = PlantFigureKit.RootY;
        var c = PlantPalette.Dormant;
        var baseP = new Vector2(0f, ry - 0.5f);
        var head  = new Vector2(0f, ry - 9f);
        p.Line(baseP, head, c, 1f);
        // Leaves folded up against the bud.
        PlantFigureKit.LeafOutline(p, new Vector2(-0.6f, ry - 1f), -MathHelper.PiOver2 - 0.45f, 5f, 2f, c);
        PlantFigureKit.LeafOutline(p, new Vector2(0.6f, ry - 1f),  -MathHelper.PiOver2 + 0.45f, 5f, 2f, c);
        p.Ring(head + new Vector2(0f, -2f), 2.6f, _team * 0.8f, 8, 1f);
        p.Line(head + new Vector2(-1.5f, -2f), head + new Vector2(1.5f, -2f), c, 1f);
        return p;
    }
}
