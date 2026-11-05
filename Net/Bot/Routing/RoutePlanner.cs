using Microsoft.Xna.Framework;

namespace MTile.Net;

// RouteManeuver behind the MoveExecutor (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, "Outputs"):
// the router is execution, not a behaviour. It drives Move in place of the straight line
// while the route needs a climb or a build (RouteManeuver.Score > 0), from a fresh plan,
// until the maneuver's Step ends; then its Cooldown runs before it may want again.
//
// The route always plans to the perceived opponent (RouteManeuver's goal set is melee
// reach around v.TargetPos), not to the stance's target point.
public sealed class RoutePlanner : IMovePlanner
{
    public readonly RouteManeuver Route;
    public RoutePlanner(RouteManeuver route) { Route = route; }

    public bool Running => _running;

    private bool  _running;
    private int   _lastFrame = int.MinValue / 2, _readyFrame = int.MinValue / 2;
    private float _lastDamage;

    public bool Wants(BotView v, Vector2 target)
    {
        float dmg = v.Self.Combat.DamageTaken;
        bool hit = dmg > _lastDamage;
        _lastDamage = dmg;

        // Getting hit ends a route as it ended the maneuver (then its cooldown), as does
        // any gap in driving (a reflex or a Move-holding action took Move): a resumed
        // route starts from a fresh plan.
        if (_running && hit) Stop(v);
        if (_running && v.Frame != _lastFrame + 1) _running = false;
        if (_running) return true;
        if (v.Frame < _readyFrame) return false;
        return Route.Score(v) > 0f;
    }

    public bool Step(BotView v, Vector2 target, ref PlayerInput input)
    {
        if (!_running) { Route.Begin(v); _running = true; }
        _lastFrame = v.Frame;
        if (Route.Step(v, ref input)) return true;
        Stop(v);
        return false;
    }

    private void Stop(BotView v)
    {
        _running = false;
        _readyFrame = v.Frame + Route.Cooldown;
    }
}
