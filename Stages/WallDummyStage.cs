using Microsoft.Xna.Framework;

namespace MTile;

// wall_dummy — a physics-feel testbed: a flat stone floor, a tall wall on the right, and
// an AFK second player parked a step away from the wall. The dummy is a real
// PlayerCharacter (P2) whose controller is never driven, so every hit, knockback,
// wall crush and bounce goes through exactly the code a live opponent's body would.
// IdleSecondary keeps Game1 from handing it to the offline bot. It respawns at its post
// when killed; F5 reloads the stage to put it back after knocking it around.
//
// Layout (96 x 48 tiles): stone floor rows 36-39 over hardened rock; hardened end wall
// on the left (cols 0-3); the right wall is a 4-tile stone face (cols 60-63) backed by
// hardened rock, full height, so impacts can dent the face but never punch through.
public static partial class Stages
{
    private const int WallDummyFloor = 36, WallDummyWallX = 60;

    private static void RegisterWallDummy()
    {
        var home = DuelSpawn(WallDummyWallX - 3, WallDummyFloor);
        Register(new Stage {
            Name          = "wall_dummy",
            IdleSecondary = true,
            Terrain       = () => DuelChunks(96, 48, (x, y) =>
                y >= WallDummyFloor + 4 ? DuelHard
              : y >= WallDummyFloor     ? DuelStone
              : x < 4                   ? DuelHard
              : x >= WallDummyWallX + 4 ? DuelHard
              : x >= WallDummyWallX     ? DuelStone
              : DuelAir),
            PlayerSpawn   = DuelSpawn(WallDummyWallX - 24, WallDummyFloor),
            SecondSpawn   = home,
            Populate      = g =>
            {
                // SpawnSecondPlayer may already have made P2 (at SecondSpawn); otherwise
                // the stage adds it. Either way it is the dummy.
                var dummy = g.SecondaryPlayers.Count > 0
                    ? g.SecondaryPlayers[0].Player
                    : g.AddSecondaryPlayer(home).Player;
                g.AddTicker(_ => { if (!dummy.IsAlive) dummy.Respawn(home); });
            },
        });
    }
}
