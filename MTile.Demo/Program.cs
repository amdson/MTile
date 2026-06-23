using MTileDemo;

// Standalone skeleton viewer — entirely separate from the main game (Game1).
// Run with:            dotnet run --project MTile.Demo
// Open a clip by name: dotnet run --project MTile.Demo -- walk
// (handy when the sidebar has more clips than fit on screen.)
using var demo = new DemoGame(args.Length > 0 ? args[0] : null);
demo.Run();
