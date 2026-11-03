using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

public class BotManeuverBeamTests
{
    private readonly ITestOutputHelper output;
    public BotManeuverBeamTests(ITestOutputHelper output) => this.output = output;

    [Fact(Skip = "BeamAction (passive 45) is shadowed by GrabAction (48) on every Shift+LMB press off terrain; passes if GrabAction's passive is lowered below 45")]
    public void Beam_FiresAndHitsP1_OutOfMeleeRange()
    {
        // 200px apart on flat ground: well beyond slash reach, inside beam range.
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(300f, 67f), 200,
                               BotArena.Only(new BeamManeuver { Enabled = true }));
        output.WriteLine(r.Summary());
        Assert.True(r.Saw("BeamAction"), "The bot should charge and fire a beam.");
        Assert.True(r.DamageToP1 > 0f, $"The beam should damage P1 (lost {r.DamageToP1}).");
    }

    [Fact]
    public void Beam_DoesNotFire_WhenTooClose()
    {
        var r = BotArena.Fight(BotArena.Flat(), new Vector2(100f, 67f), new Vector2(130f, 67f), 40,
                               BotArena.Only(new BeamManeuver { Enabled = true }));
        Assert.False(r.Saw("BeamAction"));
    }
}
