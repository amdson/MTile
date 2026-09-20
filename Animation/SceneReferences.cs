using System;

namespace MTile;

// DERIVED SCENE REFERENCES — the numbers a display overlay (ClipScene.Overlays) draws,
// computed from the game's own constants rather than stored in the clip. This is the whole
// reason overlays carry no coordinates: a hover line saved as a scene Y would be a copy of
// FoldHoverOffset, and the copy goes stale the first time hover is retuned. Reading
// MovementConfig.Current here means the editor draws what the sim currently does — and,
// because that config hot-reloads, keeps drawing it while you tune.
//
// Units are RIG units (px / Game1.SkeletonScale) measured from a surface, i.e. the same
// frame as ScenePlacement.GroundBelowComRig and a SceneGuide's Y. Render-only: nothing in
// here feeds the sim, and ClipSceneBake never consults it.
public static class SceneReferences
{
    // How far the body polygon's lowest vertex sits BELOW the body centre (the com anchor),
    // in px. The polygon is scaled about its top vertex (PlayerCharacter.CreateBodyPolygon),
    // so the bottom lands at −Radius + 2·Radius·BodyHeightScale.
    public static float BodyBottomPx
        => PlayerCharacter.Radius * (2f * PlayerCharacter.BodyHeightScale - 1f);

    // Height of the BODY CENTRE above a surface it is standing on: the fold's hover gap plus
    // the polygon's own bottom. Worth contrasting with the authoring convention
    // (ScenePlacement.GroundBelowComRig = 2·Radius/scale, what `addcom` stamps): the gap
    // between the two is exactly what the dashed line exists to show.
    public static float StandingComRig
        => (MovementConfig.Current.FoldHoverOffset + BodyBottomPx) / Game1.SkeletonScale;

    public static float CrouchComRig
        => (MovementConfig.Current.CrouchHoverOffset + BodyBottomPx) / Game1.SkeletonScale;

    // Which family an overlay kind belongs to: a derived height drawn over every surface, or
    // a trajectory read out of another document.
    public static bool IsHoverLine(SceneOverlayKind kind)
        => kind is SceneOverlayKind.HoverLine or SceneOverlayKind.CrouchLine;

    public static bool IsTrajectory(SceneOverlayKind kind)
        => kind is SceneOverlayKind.Arc or SceneOverlayKind.ClipPath;

    // The body-centre height a given overlay kind marks, rig units above its surface. Only
    // meaningful for the hover kinds (IsHoverLine).
    public static float ComRig(SceneOverlayKind kind) => kind switch
    {
        SceneOverlayKind.CrouchLine => CrouchComRig,
        _                           => StandingComRig,
    };

    public static string Name(SceneOverlayKind kind) => kind switch
    {
        SceneOverlayKind.HoverLine  => "standing hover",
        SceneOverlayKind.CrouchLine => "crouched hover",
        SceneOverlayKind.Arc        => "reference arc",
        SceneOverlayKind.ClipPath   => "clip path",
        _                           => kind.ToString(),
    };

    // One line for the editor's list/tooltip: what the overlay is showing, in both units,
    // plus how far it sits off the authored ground-to-com convention.
    public static string Describe(SceneOverlayKind kind)
    {
        if (IsTrajectory(kind))
            return kind == SceneOverlayKind.Arc
                ? "a ReferenceClips arc drawn over its own parameter — display only, it never owns this clip's placement"
                : "another clip's body path through its own motion source — display only, drawn in this clip's scene frame";
        float rig = ComRig(kind);
        float convention = 2f * PlayerCharacter.Radius / Game1.SkeletonScale;
        float delta = convention - rig;
        return $"{Name(kind)}: com {rig:0.0} rig ({rig * Game1.SkeletonScale:0.0} px) above the surface"
             + $" — {MathF.Abs(delta):0.0} rig {(delta >= 0f ? "above" : "below")} the authored ground line";
    }
}
