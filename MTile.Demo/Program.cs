using MTileDemo;

// Standalone skeleton tooling — entirely separate from the main game (Game1).
//
// Animation editor:      dotnet run --project MTile.Demo [-- --rig <skeleton>]
//   (default rig: biped_rabbit; --rig biped opens the legacy one.)
// Open a clip by name:   dotnet run --project MTile.Demo -- walk
// ... with sprite skin:  dotnet run --project MTile.Demo -- --usebind pumpkin_man_downsampled
//   (superimposes the binding's sprite on the rig through scrub/playback;
//    G toggles the sprite, W the deformed mesh wireframe.)
// Sprite bind editor:    dotnet run --project MTile.Demo -- --bind hero.png [--rig <skeleton>]
//   (PNG resolved against SpriteBindings/ at the repo root; authors the
//    skeleton↔artwork alignment the runtime SpriteSkin deforms. --rig picks the
//    rig from Skeletons/<name>.json — default: the binding's own Skeleton field,
//    then biped. Passing a different rig re-targets the binding on Ctrl-S.)
// Art import:            dotnet run --project MTile.Demo -- --import SkeletonAssets/rabbit_and_badger [--out SpriteBindings] [--scale 0.25]
//   (one-time intake of decomposed-limb art: crop/downscale each part PNG and
//    generate first-pass multi-image bindings. See SPRITE_SKIN_PLAN.md §10.2.)
// Take viewer:           dotnet run --project MTile.Demo -- --load Takes/<name>.take.json
//   (scrub a recorded gameplay take with solver overlays; record in-game with
//    Ctrl+R, save with Ctrl+S. See Plans/ANIM_TAKE_VIEWER_PLAN.md.)
// Reference-clip editor: dotnet run --project MTile.Demo -- --ref parkour
//   (authors a maneuver's Hermite reference arc in game pixels, against the clip's
//    own entry/gate anchors; loads/saves ReferenceClips/<name>.json.
//    See Plans/BALLISTIC_CORRECTOR_PLAN.md §1.)
// Tree backdrop viewer:  dotnet run --project MTile.Demo -- --trees
//   (just the TreeParallaxBackground, no sim/stage — WASD pan, R rebuild. Best
//    with hot reload:  dotnet watch run --project MTile.Demo --non-interactive -- --trees
//    then edit TreeParallaxBackground.cs, save, press R to re-bake.)

string bindPng = null, useBind = null, clip = null, takePath = null, refClip = null, rig = null;
string importDir = null, importOut = "SpriteBindings"; float importScale = 0.25f;
bool trees = false;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--trees")                               trees = true;
    else if (args[i] == "--bind" && i + 1 < args.Length)    bindPng = args[++i];
    else if (args[i] == "--rig" && i + 1 < args.Length)     rig = args[++i];
    else if (args[i] == "--usebind" && i + 1 < args.Length) useBind = args[++i];
    else if (args[i] == "--load" && i + 1 < args.Length)    takePath = args[++i];
    else if (args[i] == "--ref" && i + 1 < args.Length)     refClip = args[++i];
    else if (args[i] == "--import" && i + 1 < args.Length)  importDir = args[++i];
    else if (args[i] == "--out" && i + 1 < args.Length)     importOut = args[++i];
    else if (args[i] == "--scale" && i + 1 < args.Length)   importScale = float.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture);
    else clip ??= args[i];
}

if (trees)
{
    using var viewer = new TreeViewerGame();
    viewer.Run();
}
else if (importDir != null)
{
    using var import = new ImportGame(importDir, importOut, importScale);
    import.Run();
}
else if (refClip != null)
{
    using var hermite = new HermiteClipGame(refClip);
    hermite.Run();
}
else if (takePath != null)
{
    using var viewer = new ViewerGame(takePath);
    viewer.Run();
}
else if (bindPng != null)
{
    using var bind = new BindGame(bindPng, rig);
    bind.Run();
}
else
{
    using var demo = new DemoGame(clip, useBind, rig);
    demo.Run();
}
