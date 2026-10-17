using System;
using System.Linq;
using Xunit;

namespace MTile.Tests;

// The web lobby's curated stage list (LobbyStages.cs) is hand-written data so the lobby
// never runs the Stages static constructor. Guard it against drifting from the registry:
// a typo would silently boot "start" (Stages.Get's fallback), and a wrong Coop tag would
// tell players to fight on a stage with no friendly fire.
public class LobbyStageCatalogTests
{
    [Fact]
    public void EveryLobbyStageIsRegisteredWithMatchingCoopFlag()
    {
        Assert.NotEmpty(LobbyStages.All);
        foreach (var entry in LobbyStages.All)
        {
            var stage = Stages.Get(entry.Name);
            Assert.True(string.Equals(stage.Name, entry.Name, StringComparison.OrdinalIgnoreCase),
                $"lobby stage '{entry.Name}' is not registered (Stages.Get fell back to '{stage.Name}')");
            Assert.True(stage.Coop == entry.Coop,
                $"lobby stage '{entry.Name}' tagged Coop={entry.Coop} but the Stage says {stage.Coop}");
        }
    }

    [Fact]
    public void NamesAreUniqueAndFindIsCaseInsensitive()
    {
        Assert.Equal(LobbyStages.All.Length,
            LobbyStages.All.Select(s => s.Name.ToLowerInvariant()).Distinct().Count());
        Assert.Same(LobbyStages.All[0], LobbyStages.Find(LobbyStages.All[0].Name.ToUpperInvariant()));
        Assert.Null(LobbyStages.Find("no-such-stage"));
    }
}
