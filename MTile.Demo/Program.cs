using MTileDemo;

// Standalone skeleton tooling — entirely separate from the main game (Game1).
//
// Animation editor:      dotnet run --project MTile.Demo
// Open a clip by name:   dotnet run --project MTile.Demo -- walk
// ... with sprite skin:  dotnet run --project MTile.Demo -- --usebind pumpkin_man_downsampled
//   (superimposes the binding's sprite on the rig through scrub/playback;
//    G toggles the sprite, W the deformed mesh wireframe.)
// Sprite bind editor:    dotnet run --project MTile.Demo -- --bind hero.png
//   (PNG resolved against SpriteBindings/ at the repo root; authors the
//    skeleton↔artwork alignment the runtime SpriteSkin deforms.)
// Take viewer:           dotnet run --project MTile.Demo -- --load Takes/<name>.take.json
//   (scrub a recorded gameplay take with solver overlays; record in-game with
//    Ctrl+R, save with Ctrl+S. See Plans/ANIM_TAKE_VIEWER_PLAN.md.)
// Reference-clip editor: dotnet run --project MTile.Demo -- --ref vault
//   (authors a maneuver's y(x) Hermite reference arc in the normalized frame;
//    loads/saves ReferenceClips/<name>.json. See Plans/BALLISTIC_CORRECTOR_PLAN.md §1.)

string bindPng = null, useBind = null, clip = null, takePath = null, refClip = null;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--bind" && i + 1 < args.Length)         bindPng = args[++i];
    else if (args[i] == "--usebind" && i + 1 < args.Length) useBind = args[++i];
    else if (args[i] == "--load" && i + 1 < args.Length)    takePath = args[++i];
    else if (args[i] == "--ref" && i + 1 < args.Length)     refClip = args[++i];
    else clip ??= args[i];
}

if (refClip != null)
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
    using var bind = new BindGame(bindPng);
    bind.Run();
}
else
{
    using var demo = new DemoGame(clip, useBind);
    demo.Run();
}
