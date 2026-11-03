using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using MTile.Net;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Scratch probe for Plans/PLAYER_AGENTS.md §6 (is hit-and-run punishable?). Not a
// regression test. P1 (the runner) swings a Slash 1 at a P2 standing out of reach, then
// walks straight back; P2 (the punisher) sees the swing `react` frames late and dashes in
// with a DashSlash. The runner optionally answers the dash, also `react` frames late.
public class ZzzHitAndRunProbe(ITestOutputHelper output)
{
    public enum Answer { None, Slash, Guard }

    private record Outcome(float Gap, float RunnerHurt, float PunisherHurt, int Clanks, int HitFrame, string Note);

    private Outcome Probe(float gap, int react, Answer answer, float clickDist, int punishDelay = 0)
    {
        var sim = BotDuel.BuildArena("plain_close");
        int clanks = 0; sim.OnClank += (_, _, _, _) => clanks++;
        var r = sim.Player; var p = sim.SecondaryPlayers[0].Player;
        var rPos = new List<Vector2>(); var pPos = new List<Vector2>(); var pState = new List<string>();
        int T = -1, hitFrame = -1, chord = -1;
        bool clicked = false, answered = false;
        float startGap = 0f;
        for (int f = 0; f < 1200; f++)
        {
            rPos.Add(r.Body.Position); pPos.Add(p.Body.Position); pState.Add(p.CurrentStateName);
            float d = p.Body.Position.X - r.Body.Position.X;
            var a = new PlayerInput { MouseWorldPosition = p.Body.Position };
            var b = new PlayerInput { MouseWorldPosition = r.Body.Position };
            if (T < 0)
            {
                // Walk in to the gap: the runner does the walking so it faces the punisher.
                if (d > gap + 1f) a.Right = true;
                if (d < gap - 1f) b.Right = true;
                if (MathF.Abs(d - gap) <= 1.5f && r.IsGrounded && p.IsGrounded
                    && MathF.Abs(r.Body.Velocity.X) < 1f && MathF.Abs(p.Body.Velocity.X) < 1f
                    && r.Facing == 1)
                { T = f + 10; startGap = d; }
            }
            else
            {
                int t = f - T;
                // Runner: S1 on frame T, then walk back 20 frames (RampartBackoff's shape).
                if (t == 0) a.LeftClick = true;
                else if (t > 0 && t <= 20) a.Left = true;
                bool sawDash = f - react >= 0 && pState[f - react] == "DashState";
                if (sawDash && answer != Answer.None)
                {
                    a.Left = false;
                    if (answer == Answer.Slash && !answered) { a.LeftClick = true; answered = true; }
                    if (answer == Answer.Guard) a.Shift = true;
                }
                // Punisher: chord `react + punishDelay` frames after the swing started.
                if (t == react + punishDelay) { b.Space = true; b.Shift = true; chord = f; }
                else if (t == react + punishDelay + 1) b.Shift = true;
                if (p.CurrentStateName == "DashState")
                {
                    b.Left = true;
                    if (!clicked && MathF.Abs(d) <= clickDist) { b.LeftClick = true; clicked = true; }
                }
                if (t > 120) break;
            }
            sim.Step(a, b);
            if (hitFrame < 0 && T >= 0 && (r.Combat.DamageTaken > 0f || p.Combat.DamageTaken > 0f)) hitFrame = f - T;
        }
        return new Outcome(startGap, r.Combat.DamageTaken, p.Combat.DamageTaken, clanks, hitFrame,
                           clicked ? "" : "never clicked");
    }

    [Fact]
    public void Sweep()
    {
        foreach (var answer in new[] { Answer.None, Answer.Slash, Answer.Guard })
        foreach (int react in new[] { 6, 8 })
        foreach (float gap in new[] { 52f, 60f, 70f, 80f, 90f })
        foreach (float click in new[] { 40f, 48f, 56f })
        {
            var o = Probe(gap, react, answer, click);
            output.WriteLine($"{answer,-5} react {react} gap {o.Gap,5:F1} click {click}: runner -{o.RunnerHurt:F2} punisher -{o.PunisherHurt:F2} clanks {o.Clanks} first hit t+{o.HitFrame} {o.Note}");
        }
    }
}
