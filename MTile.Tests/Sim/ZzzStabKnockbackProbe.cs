using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Xunit;
using Xunit.Abstractions;

namespace MTile.Tests.Sim;

// Scratch diagnostic (Zzz*: excluded from group runs). How far does one stab send a target,
// as a function of target kind/mass, gap, and aim angle?
public class ZzzStabKnockbackProbe(ITestOutputHelper output)
{
    private const float FloorTopY = 12 * Chunk.TileSize;

    private static ChunkMap Hall()
    {
        var rows = new List<string>();
        for (int y = 0; y < 16; y++) rows.Add(new string(y >= 12 ? 'X' : 'O', 120));
        return SimTerrain.FromAscii(string.Join("\n", rows), originTileX: 0, originTileY: 0);
    }

    private string Run(string label, Func<Vector2, Entity> make, float gap, float aimDeg, float targetLift = 0f, bool walk = false)
    {
        const float px = 300f;
        Entity target = null;
        var sim = new Simulation(Hall(), new Vector2(px, FloorTopY - PlayerCharacter.Radius - 1f), g =>
        {
            target = make(new Vector2(px + gap, FloorTopY - 12f - targetLift));
            g.SpawnEntity(target);
        });
        float a = aimDeg * MathF.PI / 180f;
        var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
        int gen0 = -1, hitFrame = -1; float dv = 0, x0 = 0, y0 = 0, maxDx = 0, maxUp = 0, dx30 = 0; Vector2 vAfter = default;
        string act = "";
        for (int f = 0; f < 260; f++)
        {
            var p = sim.Player.Body.Position;
            var inp = new PlayerInput { MouseWorldPosition = p + dir * (f < 22 ? 40f : 110f), Right = walk && f < 60 };
            if (f >= 21 && f < 38) inp.LeftClick = true;
            if (f == 20) gen0 = target.HitGeneration;
            sim.Step(inp);
            if (sim.Player.CurrentActionName == "StabAction") act = "Stab";
            if (hitFrame < 0 && f >= 20 && target.HitGeneration != gen0)
            {
                hitFrame = f; dv = target.LastHitImpulse; x0 = target.Body.Position.X; y0 = target.Body.Position.Y;
            }
            if (hitFrame >= 0)
            {
                if (f == hitFrame + 12) vAfter = target.Body.Velocity;
                maxDx = MathF.Max(maxDx, target.Body.Position.X - x0);
                maxUp = MathF.Max(maxUp, y0 - target.Body.Position.Y);
                if (f == hitFrame + 30) dx30 = target.Body.Position.X - x0;
                if (f > hitFrame + 150) break;
            }
        }
        return $"{label,-14} gap={gap,3} aim={aimDeg,4}° {act,-4} hit@{hitFrame,3} dv={dv,5:F0} v+12f=({vAfter.X,5:F0},{vAfter.Y,5:F0}) dx30={dx30,5:F0} maxDx={maxDx,5:F0} maxUp={maxUp,4:F0} hp={target.Health:F2}";
    }

    private static Entity Ball(Vector2 p, float m) => new Entity(new PhysicsBody(Polygon.CreateRegular(8f, 8), p), health: 50f) { Mass = m, Faction = Faction.Neutral };
    private static Entity Sparrer(Vector2 p, float m) { var e = EnemyFactory.Create(EntityKind.Sparring, p); e.Mass = m; e.Health = 50f; return e; }

    [Theory]
    [InlineData(1, 0f, 30f)]
    public void Trace(int kind, float aimDeg, float gap)
    {
        const float px = 300f;
        Entity target = null;
        var sim = new Simulation(Hall(), new Vector2(px, FloorTopY - PlayerCharacter.Radius - 1f), g =>
        {
            var p = new Vector2(px + gap, FloorTopY - 12f);
            target = kind == 0 ? Ball(p, 1.5f) : Sparrer(p, 2.5f);
            g.SpawnEntity(target);
        });
        float a = aimDeg * MathF.PI / 180f;
        var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
        for (int f = 0; f < 110; f++)
        {
            var p = sim.Player.Body.Position;
            var inp = new PlayerInput { MouseWorldPosition = p + dir * (f < 22 ? 40f : 110f) };
            if (f >= 21 && f < 38) inp.LeftClick = true;
            sim.Step(inp);
            var b = target.Body;
            var en = target as EnemyEntity;
            if (f >= 44 && f < 72) { var sb = new StringBuilder(); for (int ty = 9; ty <= 12; ty++) { for (int tx = 26; tx < 40; tx++) sb.Append(sim.Chunks.GetCellState(tx, ty).ToString()[0]); sb.Append('|'); } if (f >= 53 && f <= 67) output.WriteLine($"     rows9-12[26..40)={sb} ents={sim.Entities.Count}"); }
            if (f >= 44 && f < 72 && (target.HitGeneration > 0 || f % 3 == 0)) output.WriteLine($"f{f,3} {sim.Player.CurrentActionName,-11} pos=({b.Position.X,6:F1},{b.Position.Y,6:F1}) v=({b.Velocity.X,6:F1},{b.Velocity.Y,6:F1}) launch=({b.Launch.Vel.X,6:F1},{b.Launch.Vel.Y,6:F1}) frozen={b.Frozen} gen={target.HitGeneration} plX={sim.Player.Body.Position.X:F1} plV={sim.Player.Body.Velocity.X:F0} eAct={en?.CurrentActionName} stag={en?.IsStaggered} ev={string.Join(",", b.ContactEvents.Select(e => $"{e.Outcome}@{e.Normal.X:F1},{e.Normal.Y:F1}"))}");
        }
    }

    // No stab at all: a hexagon (the blueprint enemy body) given a horizontal velocity on a flat floor.
    [Fact]
    public void SlideOnly()
    {
        output.WriteLine($"TileSize={Chunk.TileSize} Radius={PlayerCharacter.Radius}");
        foreach (int sides in new[] { 6, 8 })
        for (float x = 320f; x <= 352f; x += 2f)
        {
            Entity t = null;
            var sim = new Simulation(Hall(), new Vector2(100f, FloorTopY - PlayerCharacter.Radius - 1f), g =>
            {
                t = new Entity(new PhysicsBody(Polygon.CreateRegular(10f, sides), new Vector2(x, FloorTopY - 12f)), health: 50f) { Mass = 1f, Faction = Faction.Neutral };
                t.Body.FrictionScale = 0.1f;
                g.SpawnEntity(t);
            });
            for (int f = 0; f < 30; f++) sim.Step(default);
            float restY = t.Body.Position.Y;
            t.Body.Velocity = new Vector2(470f, 0f);
            float maxUp = 0; Vector2 v1 = default;
            for (int f = 0; f < 40; f++) { sim.Step(default); if (f == 0) v1 = t.Body.Velocity; maxUp = MathF.Max(maxUp, restY - t.Body.Position.Y); }
            output.WriteLine($"sides={sides} x={x,5:F0} restY={restY:F2} v1=({v1.X,6:F1},{v1.Y,6:F1}) maxUp={maxUp,5:F1} dx={t.Body.Position.X - x,5:F0}");
        }
    }

    // A synthetic stab-shaped hit (collision mode, launch decay, hitstop) on a lone passive body.
    [Fact]
    public void SyntheticHit_ByPosition()
    {
        var mc = MovementConfig.Current;
        foreach (var (sides, fr) in new[] { (6, 0.1f), (8, 1f), (8, 0.1f) })
        for (float x = 320f; x <= 343f; x += 1f)
        {
            Entity t = null;
            var sim = new Simulation(Hall(), new Vector2(100f, FloorTopY - PlayerCharacter.Radius - 1f), g =>
            {
                t = new Entity(new PhysicsBody(Polygon.CreateRegular(10f, sides), new Vector2(x, FloorTopY - 12f)), health: 50f) { Mass = 1f, Faction = Faction.Neutral };
                t.Body.FrictionScale = fr;
                g.SpawnEntity(t);
            });
            for (int f = 0; f < 30; f++) sim.Step(default);
            float restY = t.Body.Position.Y;
            var dir = new Vector2(1f, 0f);
            var hit = new Hitbox(t.Body.Bounds, 424242, 0.5f, dir * 1140f, Faction.Player1, sim.Player.Id,
                mode: KnockbackMode.Collision, strikeDir: dir, strikeVelocity: dir * mc.StabStrikeSpeed,
                strikeMass: 2.5f * mc.StabStrikeMassScale, restitution: mc.StabRestitution, minLaunch: mc.StabMinLaunch,
                launchDecay: mc.StabLaunchDecay, launchImpactMass: mc.StabLaunchImpactMass, launchBounce: mc.StabLaunchBounce);
            t.OnHit(in hit, default);
            float maxUp = 0; Vector2 v1 = default; bool got = false;
            for (int f = 0; f < 150; f++)
            {
                sim.Step(default);
                if (!got && !t.Body.Frozen) { v1 = t.Body.Velocity; got = true; }
                maxUp = MathF.Max(maxUp, restY - t.Body.Position.Y);
            }
            output.WriteLine($"sides={sides} fr={fr} x={x,5:F0} v1=({v1.X,6:F1},{v1.Y,6:F1}) maxUp={maxUp,5:F1} dx={t.Body.Position.X - x,5:F0}");
        }
    }

    // Synthetic stab hit on a real Sparring enemy, with the player far away (idle) or adjacent (attacking).
    [Theory]
    [InlineData(100f)] [InlineData(299f)] [InlineData(305f)] [InlineData(311f)] [InlineData(317f)]
    public void SyntheticHit_OnEnemy(float playerX)
    {
        var mc = MovementConfig.Current;
        Entity t = null;
        var sim = new Simulation(Hall(), new Vector2(playerX, FloorTopY - PlayerCharacter.Radius - 1f), g =>
        {
            t = Sparrer(new Vector2(329f, FloorTopY - 12f), 2.5f);
            g.SpawnEntity(t);
        });
        for (int f = 0; f < 55; f++) sim.Step(default);
        var en = (EnemyEntity)t;
        float restY = t.Body.Position.Y, x0 = t.Body.Position.X;
        var dir = new Vector2(1f, 0f);
        var hit = new Hitbox(t.Body.Bounds, 424242, 0.5f, dir * 1140f, Faction.Player1, sim.Player.Id,
            mode: KnockbackMode.Collision, strikeDir: dir, strikeVelocity: dir * mc.StabStrikeSpeed,
            strikeMass: 2.5f * mc.StabStrikeMassScale, restitution: mc.StabRestitution, minLaunch: mc.StabMinLaunch,
            launchDecay: mc.StabLaunchDecay, launchImpactMass: mc.StabLaunchImpactMass, launchBounce: mc.StabLaunchBounce);
        string actAtHit = en.CurrentActionName; var vAtHit = t.Body.Velocity;
        t.OnHit(in hit, default);
        float maxUp = 0; Vector2 v1 = default; bool got = false; var sb = new StringBuilder();
        for (int f = 0; f < 150; f++)
        {
            sim.Step(default);
            if (!got && !t.Body.Frozen) { v1 = t.Body.Velocity; got = true; }
            if (f < 14) sb.Append($"[{t.Body.Velocity.X:F0},{t.Body.Velocity.Y:F0} {(t.Body.Frozen ? "F" : "")} pl={sim.Player.Body.Position.X:F0},{sim.Player.Body.Position.Y:F0}]");
            maxUp = MathF.Max(maxUp, restY - t.Body.Position.Y);
        }
        output.WriteLine($"playerX={playerX} enemyX={x0:F1} actAtHit='{actAtHit}' vAtHit=({vAtHit.X:F0},{vAtHit.Y:F0}) v1=({v1.X,6:F1},{v1.Y,6:F1}) maxUp={maxUp,5:F1} dx={t.Body.Position.X - x0,5:F0}");
        output.WriteLine("   " + sb);
    }

    // Player victim on flat ground: one stab, victim idle / holding away / holding toward.
    [Theory]
    [InlineData(0, 0f)] [InlineData(1, 0f)] [InlineData(-1, 0f)] [InlineData(0, -15f)]
    public void PlayerVictim(int hold, float aimDeg)
    {
        var sim = new Simulation(Hall(), new Vector2(300f, FloorTopY - PlayerCharacter.Radius - 1f));
        var (v, _) = sim.AddSecondaryPlayer(new Vector2(335f, FloorTopY - PlayerCharacter.Radius - 1f));
        float a = aimDeg * MathF.PI / 180f; var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
        float x0 = 0; int hitF = -1; float hp0 = v.Health; var sb = new StringBuilder(); float maxDx = 0, maxUp = 0, y0 = 0;
        for (int f = 0; f < 260; f++)
        {
            var p = sim.Player.Body.Position;
            var inp = new PlayerInput { MouseWorldPosition = p + dir * (f < 22 ? 40f : 110f) };
            if (f >= 21 && f < 38) inp.LeftClick = true;
            var vin = new PlayerInput { MouseWorldPosition = v.Body.Position + new Vector2(-50f, 0f), Right = hitF >= 0 && hold > 0, Left = hitF >= 0 && hold < 0 };
            sim.Step(inp, vin);
            if (hitF < 0 && v.Health < hp0) { hitF = f; x0 = v.Body.Position.X; y0 = v.Body.Position.Y; }
            if (hitF >= 0)
            {
                maxDx = MathF.Max(maxDx, v.Body.Position.X - x0); maxUp = MathF.Max(maxUp, y0 - v.Body.Position.Y);
                int k = f - hitF;
                if (k <= 40 && k % 2 == 0) sb.Append($"{k}:{v.Body.Velocity.X:F0}{(v.IsGrounded ? "g" : "a")} ");
                if (hold < 0 && k >= 8 && k <= 17) output.WriteLine($"  k{k,2} {v.CurrentStateName,-16} {v.CurrentActionName,-14} pos=({v.Body.Position.X,6:F1},{v.Body.Position.Y,6:F1}) v=({v.Body.Velocity.X,6:F1},{v.Body.Velocity.Y,6:F1}) launch=({v.Body.Launch.Vel.X:F0},{v.Body.Launch.Vel.Y:F0}) {v.CorrectorDebug.LatticeOutcome}{Chan(v)}{PathStr(v)}");
            }
        }
        output.WriteLine($"hold={hold} aim={aimDeg} hit@{hitF} dv={v.Combat.LastHitImpulse:F0} maxDx={maxDx:F0} finalDx={v.Body.Position.X - x0:F0} maxUp={maxUp:F0} hp={v.Health:F2}");
        output.WriteLine("   vx by frame after hit: " + sb);
    }

    // No hit, no hitstun: a standing player given +vx on flat ground, then holding a direction.
    [Fact]
    public void SlideHoldingBack_NoHit()
    {
        foreach (float vx in new[] { 100f, 150f, 200f, 282f, 400f })
        foreach (int hold in new[] { -1, 0, 1 })
        {
            var sim = new Simulation(Hall(), new Vector2(300f, FloorTopY - PlayerCharacter.Radius - 1f));
            var pl = sim.Player;
            for (int f = 0; f < 40; f++) sim.Step(default);
            float x0 = pl.Body.Position.X, y0 = pl.Body.Position.Y, maxUp = 0, maxDx = 0, maxDown = 0; int air = 0; var sb = new StringBuilder();
            pl.Body.Velocity = new Vector2(vx, 0f);
            for (int f = 0; f < 90; f++)
            {
                sim.Step(new PlayerInput { Left = hold < 0, Right = hold > 0, MouseWorldPosition = pl.Body.Position + new Vector2(50f, 0f) });
                maxUp = MathF.Max(maxUp, y0 - pl.Body.Position.Y); maxDx = MathF.Max(maxDx, pl.Body.Position.X - x0); maxDown = MathF.Max(maxDown, pl.Body.Position.Y - y0);
                if (!pl.IsGrounded) air++;
                if (vx == 282f && hold < 0 && f < 6) sb.Append($"\n      f{f} v=({pl.Body.Velocity.X:F0},{pl.Body.Velocity.Y:F0}){Chan(pl)}{PathStr(pl)}");
            }
            output.WriteLine($"vx={vx,4} hold={hold,2} maxDx={maxDx,5:F0} maxUp={maxUp,5:F1} maxDown={maxDown,4:F1} airFrames={air}{sb}");
        }
    }

    // Victim mashes the dash chord (Shift held, Space tapped) back toward the attacker after a stab.
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DashBackAfterStab(bool airborne)
    {
        var sim = new Simulation(Hall(), new Vector2(300f, FloorTopY - PlayerCharacter.Radius - 1f));
        var (v, _) = sim.AddSecondaryPlayer(new Vector2(335f, FloorTopY - PlayerCharacter.Radius - 1f));
        var dir = airborne ? Vector2.Normalize(new Vector2(1f, -0.45f)) : new Vector2(1f, 0f);
        float hp0 = v.Health, x0 = 0; int hitF = -1, dashF = -1, hsEnd = -1, launchEnd = -1; string last = "";
        for (int f = 0; f < 330; f++)
        {
            var p = sim.Player.Body.Position;
            var inp = new PlayerInput { MouseWorldPosition = p + dir * (f < 22 ? 40f : 110f) };
            if (f >= 21 && f < 38) inp.LeftClick = true;
            var vin = new PlayerInput { MouseWorldPosition = v.Body.Position + new Vector2(-50f, 0f) };
            if (hitF < 0 && airborne && f >= 34 && f < 44) vin.Space = true;                  // be in the air for the hit
            if (hitF >= 0 && dashF < 0) { vin.Left = true; vin.Shift = true; vin.Space = (f % 4) < 2; }
            if (dashF >= 0) vin.Left = true;
            sim.Step(inp, vin);
            if (hitF < 0 && v.Health < hp0) { hitF = f; x0 = v.Body.Position.X; }
            if (hitF < 0) continue;
            int k = f - hitF;
            if (hsEnd < 0 && !v.Combat.HitstunActive) hsEnd = k;
            if (launchEnd < 0 && k > 12 && !v.Body.Launch.Active) launchEnd = k;
            string row = $"{v.CurrentStateName} launched={v.Combat.Launched} hitstun={v.Combat.HitstunActive}";
            if (row != last) { output.WriteLine($"   k{k,3} {row} x-x0={v.Body.Position.X - x0:F0} v=({v.Body.Velocity.X:F0},{v.Body.Velocity.Y:F0}) launch={v.Body.Launch.Vel.Length():F0}"); last = row; }
            if (dashF < 0 && v.CurrentStateName == "DashState") dashF = k;
            if (k > 200) break;
        }
        output.WriteLine($"airborne={airborne}: hitstun ends k{hsEnd}, launch spent k{launchEnd}, dash fires k{dashF}");
    }

    // Stab aimed steeply upward into a victim: standing vs already airborne.
    [Theory]
    [InlineData(false, -70f)] [InlineData(true, -70f)] [InlineData(false, -90f)]
    public void UpStab(bool airborne, float aimDeg)
    {
        var sim = new Simulation(Hall(), new Vector2(300f, FloorTopY - PlayerCharacter.Radius - 1f));
        var (v, _) = sim.AddSecondaryPlayer(new Vector2(aimDeg <= -90f ? 301f : 316f, FloorTopY - PlayerCharacter.Radius - 1f));
        float a = aimDeg * MathF.PI / 180f; var dir = new Vector2(MathF.Cos(a), MathF.Sin(a));
        float hp0 = v.Health, y0 = 0, maxUp = 0; int hitF = -1; bool wasGrounded = false;
        for (int f = 0; f < 260; f++)
        {
            var p = sim.Player.Body.Position;
            var inp = new PlayerInput { MouseWorldPosition = p + dir * (f < 22 ? 40f : 110f) };
            if (f >= 21 && f < 38) inp.LeftClick = true;
            var vin = new PlayerInput { MouseWorldPosition = v.Body.Position + new Vector2(-50f, 0f) };
            if (hitF < 0 && airborne && f >= 40 && f < 46) vin.Space = true;
            bool g = v.IsGrounded;
            sim.Step(inp, vin);
            if (hitF < 0 && v.Health < hp0) { hitF = f; y0 = v.Body.Position.Y; wasGrounded = g; }
            if (hitF < 0) continue;
            int k = f - hitF; maxUp = MathF.Max(maxUp, y0 - v.Body.Position.Y);
            if (k <= 22) output.WriteLine($"   k{k,3} {v.CurrentStateName,-14} y-y0={v.Body.Position.Y - y0,6:F1} v=({v.Body.Velocity.X,5:F0},{v.Body.Velocity.Y,5:F0}) launch=({v.Body.Launch.Vel.X:F0},{v.Body.Launch.Vel.Y:F0}){Chan(v)}");
        }
        output.WriteLine($"airborne={airborne} aim={aimDeg}: groundedAtHit={wasGrounded} dv={v.Combat.LastHitImpulse:F0} maxUp={maxUp:F0}");
    }

    private static string Chan(PlayerCharacter p)
    {
        var l = p.CorrectorDebug.Ledger; var sb = new StringBuilder();
        for (int c = 0; c < l.ChannelCount; c++) { var f = l.Channels[c].Force; if (f.LengthSquared() < 25f) continue; sb.Append($" {l.Channels[c].Channel}({f.X:F0},{f.Y:F0})"); }
        return sb.ToString();
    }
    private static string PathStr(PlayerCharacter p)
    {
        var cd = p.CorrectorDebug; if (cd.LatticePathFrame < 0 || cd.LatticePathCount < 2) return "";
        var sb = new StringBuilder(" path-body:");
        for (int i = 0; i < Math.Min(7, cd.LatticePathCount); i++) { var d = cd.LatticePath[i].Pos - p.Body.Position; sb.Append($"({d.X:F0},{d.Y:F0})"); }
        return sb.ToString();
    }

    private static char TileProbe(Simulation sim, int tx, int ty)
    {
        return TileQuery.IsSolidAt(sim.Chunks, (tx + 0.5f) * Chunk.TileSize, (ty + 0.5f) * Chunk.TileSize) ? 'X' : '.';
    }

    [Fact]
    public void Sweep()
    {
        foreach (float aim in new[] { -20f, -8f, 0f, 8f, 20f })
            output.WriteLine(Run("ball 1.5", p => Ball(p, 1.5f), 30, aim));
        output.WriteLine("");
        foreach (float gap in new[] { 14f, 22f, 30f, 40f, 50f, 58f })
            output.WriteLine(Run("ball 1.5", p => Ball(p, 1.5f), gap, 0));
        output.WriteLine("");
        foreach (float m in new[] { 0.3f, 1f, 2.5f, 8f, 40f })
            output.WriteLine(Run($"ball {m}", p => Ball(p, m), 30, 0));
        output.WriteLine("");
        foreach (float m in new[] { 0.3f, 1f, 2.5f, 8f })
            foreach (float aim in new[] { -8f, 0f, 8f })
                output.WriteLine(Run($"sparrer {m}", p => Sparrer(p, m), 40, aim));
        output.WriteLine("");
        foreach (float gap in new[] { 20f, 30f, 40f, 50f, 60f })
            output.WriteLine(Run("sparrer 1", p => Sparrer(p, 1f), gap, 0));
        output.WriteLine("");
        output.WriteLine(Run("brute", p => new BruteEnemy(p), 40, 0));
        output.WriteLine(Run("warden", p => new WardenEnemy(p), 40, 0));
        output.WriteLine(Run("stalker", p => new StalkerEnemy(p), 40, 0));
        output.WriteLine(Run("airball 1.5", p => Ball(p, 1.5f), 30, 0, targetLift: 6f));
        output.WriteLine(Run("walk ball", p => Ball(p, 1.5f), 60, 0, walk: true));
    }
}
