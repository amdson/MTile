using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// CamperBot — a scripted opponent for the routing work (Plans/BOT_ROUTING_PLAN.md), not a
// league entrant. It plays the escape a human uses against the bots: when the opponent
// comes near, stack a column under your own feet (BuildClimbManeuver's elevator gesture:
// stand still, Shift+RMB on the first empty cell under the feet) until you are LeadRows
// above them, keep stacking whenever they climb to within that, and swing at anything
// that reaches you. It never walks.
//
// Perception is the league's: the opponent's position through a ReactionFrames-old ring.
public sealed class CamperBot : IRemoteInputSource
{
    public static readonly DuelBot Create = (self, seed) => new CamperBot(self);
    public static readonly DuelBot Tower  = (self, seed) => new CamperBot(self) { TowerRows = 15 };

    public int   ReactionFrames = 6;
    public float TriggerCells   = 12f;   // start stacking when the opponent is this close sideways
    public int   LeadRows       = 6;     // ...and stay this many cells above them
    public int   MaxRows        = 40;    // never stack higher than this above the spawn floor
    public int   TowerRows      = 0;     // > 0: stack this high at once, whatever the opponent does
    public float SlashReach     = 42f, SlashVertical = 34f;
    public int   SlashGap       = 16;

    // Diagnostics.
    public int Placed, Swings;

    private readonly int _selfIndex;
    private const int Ring = 32;
    private readonly Vector2[] _seen = new Vector2[Ring];
    private int _count, _lastSwing = -1000;
    private bool _picked;
    private int _col = int.MinValue;
    private float _spawnFloorY = float.NaN;
    private PlayerInput _prev;

    public CamperBot(int selfIndex = -1) => _selfIndex = selfIndex;

    private static float T => Chunk.TileSize;
    private static float FootDrop => PlayerCharacter.StandingHeight - PlayerCharacter.Radius;

    public PlayerInput Poll(Simulation sim, int frame)
    {
        if (sim.Player == null || sim.SecondaryPlayers.Count < 1) return default;
        var me = BotDuel.Self(sim, _selfIndex);
        var them = BotDuel.Opponent(sim, _selfIndex);
        _seen[_count++ % Ring] = them.Body.Position;
        var tp = _seen[(_count - 1 - Math.Min(ReactionFrames, _count - 1)) % Ring];
        var pos = me.Body.Position;
        var input = new PlayerInput { MouseWorldPosition = tp };

        if (float.IsNaN(_spawnFloorY) && me.IsGrounded) _spawnFloorY = pos.Y;
        if (me.Combat.DamageTaken <= 0f && me.IsGrounded && MathF.Abs(pos.Y - _spawnFloorY) < 1f) _col = int.MinValue;

        if (!_picked) { input.Num2 = true; _picked = true; _prev = input; return input; }   // dirt

        float dx = tp.X - pos.X, dy = tp.Y - pos.Y;
        // Defend: anything in reach gets a slash.
        if (MathF.Abs(dx) <= SlashReach && MathF.Abs(dy) <= SlashVertical && frame - _lastSwing >= SlashGap && !_prev.LeftClick)
        {
            input.LeftClick = true; _lastSwing = frame; Swings++;
            _prev = input; return input;
        }

        // Stack: opponent near sideways and not LeadRows below us yet.
        bool near = MathF.Abs(dx) < TriggerCells * T;
        bool tooClose = dy < LeadRows * T;               // y-down: they are less than LeadRows below
        bool roomToGrow = _spawnFloorY - pos.Y < MaxRows * T;
        if (TowerRows > 0 && _spawnFloorY - pos.Y < TowerRows * T) { near = true; tooClose = true; }
        if (near && tooClose && roomToGrow && me.IsGrounded || (_col != int.MinValue && near && tooClose && roomToGrow))
        {
            if (_col == int.MinValue) _col = (int)MathF.Floor(pos.X / T);
            float err = (_col + 0.5f) * T - pos.X;
            if (MathF.Abs(err) > 2.5f) BotInput.Move(ref input, err > 0 ? 1 : -1);
            if (FootCell(sim, pos, _col, out var cell) && !Solid(sim, _col, (int)MathF.Floor(pos.Y / T) - 2))
            {
                input.Shift = true; input.RightClick = true;
                input.MouseWorldPosition = BotView.CellCenter(cell);
                if (!_prev.RightClick) Placed++;
            }
        }
        _prev = input;
        return input;
    }

    private static bool Solid(Simulation sim, int x, int y) => sim.Chunks.GetCellState(x, y) == TileState.Solid;

    // BuildClimbManeuver.FootCell: the first empty cell of the column above whatever is
    // under the body.
    private static bool FootCell(Simulation sim, Vector2 pos, int col, out Point cell)
    {
        int top    = (int)MathF.Floor(pos.Y / T) + 1;
        int maxRow = (int)MathF.Floor((pos.Y + FootDrop + T) / T);
        for (int y = top; y <= maxRow; y++)
        {
            if (sim.Chunks.GetCellState(col, y).IsOpen()) continue;
            cell = new Point(col, y - 1);
            return true;
        }
        cell = default;
        return false;
    }
}
