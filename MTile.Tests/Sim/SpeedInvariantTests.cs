using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// The speed-cap principle (movement_todo #3 + #5): no AUTOMATIC move — vault
// hops, corrector channels, any assist — may push the body past what a
// deliberate input could author.
//
//   Vertical: rise speed may only GROW beyond MaxAssistRiseSpeed (the fastest
//   ground jump, derived in MovementConfig) while a deliberate-launch state is
//   active. Gravity-decay of a bigger authored launch (WallJump -150,
//   LedgeJump servo -210, tech bounce -260) passes; an assist re-injecting
//   energy fails. Growth attribution — not an instantaneous clamp — is the
//   assertion: a clamp would mask new rockets, a growth check names the frame
//   and state that authored them.
//
//   Horizontal: |vx| may only grow beyond MaxWalkSpeed+10% in states with an
//   authored reason (WallJump's 250 kick). Maneuver-class and grounded states
//   hold the tight bound; free air control is bounded by MaxAirSpeed.
//
// The historically-rocketing script (hold jump around vaults — the b5c417b
// bug class) runs here permanently.
public class SpeedInvariantTests(ITestOutputHelper output)
{
    private const float Dt = 1f / 60f;
    private static readonly Vector2 Gravity = new(0f, 600f);

    // The whole deliberate-launch family: entry impulses plus HOLD forces
    // (JumpHoldForce sustains rise well past the launch constant — a held
    // ground jump legitimately reaches ~220), plus the combat band (knockback
    // / tech are ImpactDamage's domain, not movement's). Deliberately NOT a
    // bare "Jump" substring: ArcJumpState is the AUTOMATIC 2-block climb and
    // must stay under the assist cap.
    private static readonly string[] DeliberateRise =
        ["Jumping", "RunningJump", "DoubleJump", "WallJump", "CoveredJump", "LedgeJump",
         "Tumble", "Stunned"];
    // Authored horizontal bursts: WallJump's kick (250) plus the combat band.
    private static readonly string[] DeliberateVx = ["WallJump", "Tumble", "Stunned"];

    private static bool In(string state, string[] set) => set.Any(state.Contains);

    private static bool IsManeuver(string s) =>
        s.Contains("Parkour") || s.Contains("Mantle") || s.Contains("ArcJump");

    private static bool IsGrounded(string s) =>
        s.Contains("Standing") || s.Contains("Crouched");

    // ── The core assertion: attribute every bound-crossing GROWTH frame ──────
    private void AssertSpeedInvariants(SimFrame[] frames, string scenario)
    {
        var cfg = MovementConfig.Current;
        float riseBound = cfg.MaxAssistRiseSpeed + 5f;          // 125 at default tuning
        float vxTight   = cfg.MaxWalkSpeed * 1.1f;              // 110 — maneuvers + ground
        float vxAir     = cfg.MaxAirSpeed + 5f;                 // 155 — free air control ceiling
        const float Eps = 0.5f;

        var violations = new List<string>();
        int firstViolation = -1;
        float maxRise = 0f, maxVx = 0f;
        for (int i = 1; i < frames.Length; i++)
        {
            var f = frames[i];
            float rise = -f.Vy, prevRise = -frames[i - 1].Vy;
            float vx = MathF.Abs(f.Vx), prevVx = MathF.Abs(frames[i - 1].Vx);
            maxRise = MathF.Max(maxRise, rise);
            maxVx   = MathF.Max(maxVx, vx);

            if (rise > riseBound && rise > prevRise + Eps && !In(f.State, DeliberateRise))
            {
                violations.Add($"f{f.Frame} {f.State}: rise {prevRise:F0}→{rise:F0} past assist cap");
                if (firstViolation < 0) firstViolation = i;
            }

            if (vx > prevVx + Eps && !In(f.State, DeliberateVx))
            {
                string v = null;
                if (vx > vxTight && (IsManeuver(f.State) || IsGrounded(f.State)))
                    v = $"f{f.Frame} {f.State}: |vx| {prevVx:F0}→{vx:F0} past run bound";
                else if (vx > vxAir)
                    v = $"f{f.Frame} {f.State}: |vx| {prevVx:F0}→{vx:F0} past air ceiling";
                if (v != null)
                {
                    violations.Add(v);
                    if (firstViolation < 0) firstViolation = i;
                }
            }
        }

        output.WriteLine($"{scenario}: max rise {maxRise:F1}, max |vx| {maxVx:F1}, " +
                         $"{violations.Count} violation(s)");
        foreach (var v in violations.Take(10)) output.WriteLine("  " + v);
        if (violations.Count > 0 && firstViolation >= 0)
        {
            output.WriteLine("  context (frame: state vx vy | fx fy):");
            for (int i = Math.Max(0, firstViolation - 3);
                 i < Math.Min(frames.Length, firstViolation + 4); i++)
            {
                var f = frames[i];
                output.WriteLine($"    f{f.Frame}: {f.State,-24} vx {f.Vx,7:F1} vy {f.Vy,7:F1}" +
                                 $" | fx {f.Fx,8:F0} fy {f.Fy,8:F0}");
            }
        }
        Assert.Empty(violations);
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    // The CorrectorCost twin: repeating 1-block vaults at speed.
    private static ChunkMap VaultCourse() => SimTerrain.FromAscii(@"
        OOOOOOOOXXOOOOXXOOOOXXOOOOXXOOOOXXOOOOXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 5);

    private static ChunkMap Corridor()
    {
        const int W = 64;
        var rows = new string[7];
        for (int r = 0; r < 7; r++)
        {
            var sb = new StringBuilder(W);
            for (int c = 0; c < W; c++)
            {
                bool tunnel = c >= 16;
                sb.Append(r switch
                {
                    6 => 'X',
                    2 when tunnel => 'X',
                    3 when tunnel && c % 4 == 3 => 'X',
                    5 when tunnel && c % 4 == 1 => 'X',
                    _ => 'O',
                });
            }
            rows[r] = sb.ToString();
        }
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    // Open-sky 1-block step approached FAST from distance — the lip-timing
    // term's worst case (vyLip demand > any jump before the assist cap).
    private static ChunkMap FastStep() => SimTerrain.FromAscii(@"
        OOOOOOOOOOOOOOOOXXXXXXXXXXXXXXXX
        XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX", originTileX: 0, originTileY: 4);

    private static SimFrame[] Run(ChunkMap terrain, Vector2 start, PlayerInput held,
                                  int frames, Vector2 startVel = default) =>
        SimRunner.Run(new SimConfig
        {
            Terrain = terrain, StartPosition = start, StartVelocity = startVel,
            Script = InputScript.Always(held),
            Frames = frames, Dt = Dt, Gravity = Gravity,
        });

    // ── Contracts ────────────────────────────────────────────────────────────

    [Fact]
    public void AssistCap_IsWhatAHeldGroundJumpAchieves()
    {
        // Launch impulse + hold-force augmentation over the full hold window —
        // the bare launch constant (120) only reaches 12px of height; jumps
        // earn theirs from the hold. ≈208 at default tuning: enough for the
        // 1-block apex hop (~170), a hard ceiling for the lip-timing demand.
        var cfg = MovementConfig.Current;
        float g = Simulation.WorldGravityY;
        float expected = MathF.Max(
            -cfg.JumpVelocity + MathF.Max(0f, (-cfg.JumpHoldForce - g) * cfg.MaxJumpHoldTime),
            MathF.Max(
                -cfg.RunJumpVelocity + MathF.Max(0f, (-cfg.RunJumpHoldForce - g) * cfg.MaxJumpHoldTime),
                -cfg.DoubleJumpVelocity));
        Assert.Equal(expected, cfg.MaxAssistRiseSpeed);
        Assert.True(cfg.MaxAssistRiseSpeed >= 170f,
            "assist cap below the 1-block apex hop — vaults would be ballistically infeasible");
    }

    [Fact]
    public void VaultCourse_HoldingJump_NeverRocketsPastTheCap()
    {
        // THE historically-rocketing script: run the vault course with jump
        // held the whole way (vault→steal→re-trigger churn, b5c417b's class).
        var frames = Run(VaultCourse(), new Vector2(12f, 72f),
                         new PlayerInput { Right = true, Space = true }, 900);
        AssertSpeedInvariants(frames, "vault course, jump held");
    }

    [Fact]
    public void VaultCourse_PlainRun_StaysInsideAllBounds()
    {
        var frames = Run(VaultCourse(), new Vector2(12f, 72f),
                         new PlayerInput { Right = true }, 900);
        AssertSpeedInvariants(frames, "vault course, run");
    }

    [Fact]
    public void BumpyCorridor_FoldChannels_StayInsideAllBounds()
    {
        var frames = Run(Corridor(), new Vector2(24f, 74f),
                         new PlayerInput { Right = true }, 800);
        AssertSpeedInvariants(frames, "bumpy corridor");
    }

    [Fact]
    public void FastApproach_LipTimingDemand_IsCappedAtTheAssistBound()
    {
        // Sprint at the step from far out: vyLip = needH/tLip + g·tLip/2
        // demands well past any jump at this speed. With the cap the hop is
        // bounded; whether the capped arc delivers or the gate refuses, the
        // body must never be pushed up past the cap.
        var frames = Run(FastStep(), new Vector2(40f, 56f),
                         new PlayerInput { Right = true }, 400,
                         startVel: new Vector2(150f, 0f));
        AssertSpeedInvariants(frames, "fast lip approach");
    }
}
