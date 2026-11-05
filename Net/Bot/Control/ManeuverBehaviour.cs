using System;
using System.Collections.Generic;

namespace MTile.Net;

// Migration adapter (Plans/BOT_BEHAVIOR_ARCHITECTURE.md, "Adapter"): runs an existing
// BotManeuver under BotController unchanged. Category and Move claim come from
// the maneuver's own Category/Move (or, transitionally, ManeuverRegistry); the bet is the
// maneuver's Bet (default 0.5 * Score HP over 90 frames).
// Name is the wrapped maneuver's Name, so diagnostics keep today's names.
public sealed class ManeuverBehaviour : BotBehaviour
{
    public const float ScoreToHp  = 0.5f;
    public const int   BetHorizon = 90;

    public readonly BotManeuver Maneuver;
    private readonly Category  _category;
    private readonly MoveClaim _move;

    public ManeuverBehaviour(BotManeuver m)
    {
        Maneuver = m ?? throw new ArgumentNullException(nameof(m));
        // Transitional: a registry entry wins while maneuvers are still being converted to
        // native overrides (M3); once the registry is empty it is deleted and the
        // maneuver's own Category/Move are the only source.
        (_category, _move) = ManeuverRegistry.IsRegistered(m.GetType()) ? ManeuverRegistry.Lookup(m) : (m.Category, m.Move);
    }

    public override string    Name     => Maneuver.Name;
    public override Category  Category => _category;
    public override MoveClaim Move     => _move;
    public override int       Cooldown => Maneuver.Cooldown;

    public override Bet Bet(BotView v) => Maneuver.Bet(v);

    public override void Begin(BotView v) => Maneuver.Begin(v);
    public override bool Step(BotView v, ref PlayerInput input) => Maneuver.Step(v, ref input);
    public override void Aborted(BotView v, ref PlayerInput input) => Maneuver.Abort(v, ref input);
}

// Maneuver type -> (category, Move claim) for the adapter. Unregistered types default to
// Action with Exclusive if OwnsLocomotion, else None.
public static class ManeuverRegistry
{
    private static readonly Dictionary<Type, (Category Category, MoveClaim Move)> Map = new();

    static ManeuverRegistry()
    {
        // Parity rule for the adapter: only maneuvers that already own locomotion today get
        // a claim here, so running under the controller changes nothing about their input.
        // Maneuvers that run under the default chase today (Dash, DownAir, Burst, the
        // Brawler BlockThrow, BulldogPunish) stay Move=None via the default; M3 converts
        // them natively with the claim they should have.

        // Hold still: guards, pulse, the aimed throws and waves, anti-air stab, escape grab.
        foreach (var t in new[]
                 {
                     typeof(GuardManeuver), typeof(BulldogGuard), typeof(BulldogBreaker), typeof(PulseManeuver),
                     typeof(RangerBlockThrowManeuver), typeof(CrystalWaveManeuver),
                     typeof(RangerCrystalWaveManeuver), typeof(AntiAirStabManeuver), typeof(EscapeGrabManeuver),
                 })
            Register(t, Category.Action, MoveClaim.Still);

        // Movers: they only move.
        foreach (var t in new[]
                 {
                     typeof(CornerEscapeManeuver), typeof(AntiAirSidestepManeuver), typeof(BackoffManeuver),
                 })
            Register(t, Category.Reflex, MoveClaim.Exclusive);

        // Actions whose input IS movement.
        foreach (var t in new[]
                 {
                     typeof(AntiAirJumpSlashManeuver), typeof(WhiffPunishManeuver), typeof(AirHammerManeuver),
                     typeof(AirWhipKickManeuver), typeof(WallClimbManeuver), typeof(BreachManeuver),
                     typeof(BuildClimbManeuver), typeof(WallUpManeuver), typeof(EncloseManeuver), typeof(BulldogCrossUp),
                 })
            Register(t, Category.Action, MoveClaim.Exclusive);
    }

    public static void Register(Type maneuverType, Category category, MoveClaim move)
    {
        if (!typeof(BotManeuver).IsAssignableFrom(maneuverType))
            throw new ArgumentException($"{maneuverType.Name} is not a BotManeuver", nameof(maneuverType));
        if (category is Category.Positioning or Category.Constraint)
            throw new ArgumentException("a wrapped maneuver is a Reflex or an Action", nameof(category));
        Map[maneuverType] = (category, move);
    }

    public static void Register<T>(Category category, MoveClaim move) where T : BotManeuver
        => Register(typeof(T), category, move);

    public static (Category Category, MoveClaim Move) Lookup(BotManeuver m)
        => Map.TryGetValue(m.GetType(), out var e)
            ? e
            : (Category.Action, m.OwnsLocomotion ? MoveClaim.Exclusive : MoveClaim.None);

    public static bool IsRegistered(Type maneuverType) => Map.ContainsKey(maneuverType);
}
