namespace MTile;

// The curated stage list the browser lobby offers (MTile.Web/Pages/Index.razor). Plain
// data on purpose: the lobby renders before Game1 exists, and touching Stages there would
// run its static constructor — which builds the depths map — behind the menu. Names must
// be registered Stages and Coop must match the Stage's own flag;
// MTile.Tests/LobbyStageCatalogTests.cs checks both. First entry is the lobby default.
public sealed record LobbyStage(string Name, string Label, bool Coop, string Blurb);

public static class LobbyStages
{
    public static readonly LobbyStage[] All =
    {
        new("depths",   "Depths",   true,  "Two-player co-op cave gauntlet, room by room."),
        new("arena",    "Arena",    false, "Walled combat room — fight each other (and the stalkers)."),
        new("gauntlet", "Gauntlet", false, "Left-to-right combat run."),
        new("training", "Training", false, "Plateau over the void with a training dummy."),
    };

    public static LobbyStage Find(string name)
    {
        foreach (var s in All)
            if (string.Equals(s.Name, name, System.StringComparison.OrdinalIgnoreCase)) return s;
        return null;
    }
}
