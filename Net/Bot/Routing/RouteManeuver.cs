using System;
using System.Collections.Generic;

namespace MTile.Net;

// Path-planned approach (Plans/BOT_ROUTING_PLAN.md): plan a NavGrid route to a stand
// node within melee reach of the perceived target and drive it with a RouteFollower.
// Owns locomotion while it runs; ends when the bot is standing in the goal set (the
// brain's melee maneuvers take over) or when the plan can't be made or keeps failing.
//
// Scores only when the route needs a climb or a build; otherwise the brain's own chase
// handles it and this stays out of the way.
public sealed class RouteManeuver : BotManeuver
{
    public int   ReplanFrames   = 20;
    public int   MaxFrames      = 900;
    public int   MaxFailures    = 4;
    public int   GoalDx = 2, GoalDy = 1;       // melee goal set around the target node
    public float Utility        = 1.1f;
    public bool  AlwaysOn;                     // tests: score whenever a route exists
    public bool  AllowBuild = true;            // build-up edges (stack a column under the feet)
    public int   WindowMarginX = 16, WindowUp = 24, WindowDown = 22;

    // Diagnostics.
    public int Plans, Failures, Reached, PlannedClimbs, PlannedBuilds;
    public Action<string> Log;
    public List<NavGrid.Edge> LastPlan { get; private set; }
    public bool LastReached { get; private set; }

    private readonly RouteFollower _follow = new();
    public RouteFollower Follower => _follow;
    private int _t, _sincePlan, _fails, _scoreCooldown;
    private NavGrid.Node? _goalNode;
    private List<NavGrid.Edge> _cached;
    private bool _cachedReached;

    public override int  Cooldown       => 10;
    public override bool OwnsLocomotion => true;

    private bool InGoal(NavGrid.Node n, NavGrid.Node t)
        => Math.Abs(n.X - t.X) <= GoalDx && Math.Abs(n.Y - t.Y) <= GoalDy;

    private bool TryPlan(BotView v, out List<NavGrid.Edge> path, out bool reached)
    {
        path = null; reached = false;
        var grid = NavGrid.For(v);
        var me = grid.NodeUnder(v.Me);
        var tn = NavGrid.NodeUnder((x, y) => v.Solid(x, y), v.TargetPos, 30);
        if (me is not NavGrid.Node start || tn is not NavGrid.Node goal) return false;
        int x0 = Math.Min(start.X, goal.X) - WindowMarginX, x1 = Math.Max(start.X, goal.X) + WindowMarginX;
        int y0 = Math.Min(start.Y, goal.Y) - WindowUp,      y1 = Math.Max(start.Y, goal.Y) + WindowDown;
        grid.GoalRow = goal.Y;
        grid.AllowBuild = AllowBuild;
        grid.Build(x0, y0, Math.Min(x1 - x0 + 1, 160), Math.Min(y1 - y0 + 1, 120));
        _goalNode = goal;
        path = grid.Search(start, n => InGoal(n, goal),
                           n => 3 * Math.Max(0, Math.Abs(n.X - goal.X) - GoalDx) + 3 * Math.Max(0, Math.Abs(n.Y - goal.Y) - GoalDy),
                           out reached);
        Plans++;
        LastPlan = path; LastReached = reached;
        if (path.Exists(e => e.Kind == NavGrid.EdgeKind.Climb)) PlannedClimbs++;
        if (path.Exists(e => e.Kind == NavGrid.EdgeKind.Build)) PlannedBuilds++;
        return true;
    }

    public override float Score(BotView v)
    {
        if (!v.Grounded) return 0f;
        if (_scoreCooldown > 0) { _scoreCooldown--; return _cached != null && Worth(v, _cached, _cachedReached) ? Utility : 0f; }
        _scoreCooldown = ReplanFrames;
        if (!TryPlan(v, out _cached, out _cachedReached)) { _cached = null; return 0f; }
        return Worth(v, _cached, _cachedReached) ? Utility : 0f;
    }

    // Route only when the path needs a climb or a build — the cases the straight chase
    // (Bulldog's walk-and-jump-at-them) can't do. While this maneuver runs it owns the
    // bot and no attack can fire, so routing on mere hops cost real fights.
    private bool Worth(BotView v, List<NavGrid.Edge> path, bool reached)
    {
        if (path.Count == 0) return false;                       // already there (or stuck)
        if (AlwaysOn) return true;
        foreach (var e in path) if (e.Kind != NavGrid.EdgeKind.Jump) return true;
        return false;
    }

    // Close enough for the melee maneuvers: hand back.
    private bool InReach(BotView v) => MathF.Abs(v.Dx) < 3f * Chunk.TileSize && MathF.Abs(v.Dy) < 2f * Chunk.TileSize;

    public override void Begin(BotView v)
    {
        _t = 0; _sincePlan = 0; _fails = 0;
        // Always from a fresh plan: the Score cache can be from long before this start
        // (Score isn't polled while another maneuver runs).
        if (!TryPlan(v, out var path, out _)) path = new List<NavGrid.Edge>();
        _follow.Follow(path);
        _cached = null; _scoreCooldown = 0;
    }

    public override bool Step(BotView v, ref PlayerInput input)
    {
        _t++; _sincePlan++;
        if (_t > MaxFrames) return false;
        if (!AlwaysOn && v.Grounded && InReach(v)) return false;

        var st = _follow.Step(v, ref input);
        if (st == RouteFollower.Status.Failed)
        {
            Log?.Invoke($"f{v.Frame} edge {_follow.Current} failed at {v.Me} node {NavGrid.NodeUnder((x, y) => v.Solid(x, y), v.Me)} vel {v.MyVel} {v.Self.CurrentStateName}");
            Failures++; _fails++;
            if (_fails > MaxFailures) return false;
            if (!Replan(v)) return false;
            return true;
        }
        if (st == RouteFollower.Status.Done)
        {
            var me = NavGrid.NodeUnder((x, y) => v.Solid(x, y), v.Me);
            if (me is NavGrid.Node n && _goalNode is NavGrid.Node g && InGoal(n, g)) { Reached++; return false; }
            if (!Replan(v)) return false;
            return true;
        }
        // Periodic replan between edges: the target moves, terrain changes.
        if (_sincePlan >= ReplanFrames && _follow.Interruptible && v.Grounded) Replan(v);
        return true;
    }

    private bool Replan(BotView v)
    {
        _sincePlan = 0;
        if (!TryPlan(v, out var path, out _)) return false;
        if (path.Count == 0) return false;
        _follow.Follow(path);
        return true;
    }
}
