using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Readout for the fight viewer (Game1 --fight): one line per fighter — name, team,
// health, energy, current action, target — plus the frame counter against the record's
// cap and the recorded result once the bout is over. Render-only; reads the entities
// the stage spawned and never touches the sim.
public static class FightHud
{
    public static void Draw(SpriteBatch sb, SpriteFont font, Texture2D pixel, Viewport vp,
                            FightRecord fight, EnemyEntity[] bots, Simulation sim, bool recording)
    {
        // Right edge, below the block picker / build meters (top ~70 px) and the optional
        // profiler overlay (~100-125 px) so nothing overlaps.
        const int Right = 12, Top = 140, LineH = 16, BarW = 90, BarH = 5;
        var sbText = new StringBuilder();

        sb.Begin();
        // Header: frame / cap, and how to drive it.
        string head = $"FIGHT  {fight.Terrain.Name}  frame {sim.Frame}/{fight.MaxFrames}" +
                      (recording ? "   [rec - Ctrl+P scrub]" : "") +
                      "   F6 pause  F7 step";
        var headSize = font.MeasureString(head);
        var headPos  = new Vector2(vp.Width - Right - headSize.X, Top);
        Shadowed(sb, font, head, headPos, Color.Gold);

        int alive = 0;
        for (int i = 0; i < bots.Length; i++)
        {
            var b = bots[i];
            if (b == null) continue;
            if (!b.IsDead) alive++;
            string name = i < fight.Entries.Count ? fight.Entries[i].Spec.Name : $"#{i}";
            string tgt  = b.CurrentTargetId.IsNone ? "-" : TargetName(b, bots, fight, sim);
            sbText.Clear();
            sbText.Append(name).Append("  T").Append(b.Team)
                  .Append(b.IsDead ? "  DEAD" : $"  hp {b.Health:0.0}/{b.MaxHealth:0.0}");
            if (b.EnergyMax > 0f) sbText.Append($"  e {b.Energy:0.0}/{b.EnergyMax:0.0}");
            sbText.Append("  ").Append(b.CurrentActionName == "" ? "idle" : b.CurrentActionName.Replace("Enemy", "").Replace("Action", ""));
            sbText.Append("  -> ").Append(tgt);
            string line = sbText.ToString();
            var size = font.MeasureString(line);
            float y = Top + LineH * (i + 1) + 4;
            var pos = new Vector2(vp.Width - Right - size.X, y);
            var tint = b.IsDead ? Color.Gray : TeamColor(b.Team);
            Shadowed(sb, font, line, pos, tint);

            // Health bar under the line; energy bar (thin, cyan) beneath it when the
            // fighter has a meter.
            float hpFrac = b.MaxHealth > 0f ? MathHelper.Clamp(b.Health / b.MaxHealth, 0f, 1f) : 0f;
            int bx = vp.Width - Right - BarW, by = (int)(y + LineH - 3);
            sb.Draw(pixel, new Rectangle(bx, by, BarW, BarH), Color.Black * 0.6f);
            sb.Draw(pixel, new Rectangle(bx, by, (int)(BarW * hpFrac), BarH), tint);
            if (b.EnergyMax > 0f)
            {
                float eFrac = MathHelper.Clamp(b.Energy / b.EnergyMax, 0f, 1f);
                sb.Draw(pixel, new Rectangle(bx, by + BarH + 1, BarW, 2), Color.Black * 0.6f);
                sb.Draw(pixel, new Rectangle(bx, by + BarH + 1, (int)(BarW * eFrac), 2), Color.Cyan);
            }
        }

        // Outcome line once the bout has ended (a team wiped, or the cap reached).
        if (fight.Result != null && (alive <= 1 || sim.Frame >= fight.MaxFrames))
        {
            string res = fight.Result.WinnerTeam < 0
                ? $"recorded: draw after {fight.Result.Frames} frames"
                : $"recorded: team {fight.Result.WinnerTeam} wins after {fight.Result.Frames} frames";
            var size = font.MeasureString(res);
            Shadowed(sb, font, res, new Vector2(vp.Width - Right - size.X, Top + LineH * (bots.Length + 2) + 8), Color.Gold);
        }
        sb.End();
    }

    // Also draws small health pips above each fighter in world space so the readout
    // and the body can be matched at a glance.
    public static void DrawWorld(SpriteBatch sb, Texture2D pixel, Matrix camTransform, EnemyEntity[] bots)
    {
        sb.Begin(transformMatrix: camTransform);
        foreach (var b in bots)
        {
            if (b == null || b.IsDead) continue;
            float w = 24f, h = 3f;
            var p = b.Body.Position + new Vector2(-w / 2f, -b.Body.Bounds.Height / 2f - 8f);
            float frac = b.MaxHealth > 0f ? MathHelper.Clamp(b.Health / b.MaxHealth, 0f, 1f) : 0f;
            sb.Draw(pixel, new Rectangle((int)p.X, (int)p.Y, (int)w, (int)h), Color.Black * 0.6f);
            sb.Draw(pixel, new Rectangle((int)p.X, (int)p.Y, (int)(w * frac), (int)h), TeamColor(b.Team));
        }
        sb.End();
    }

    private static string TargetName(EnemyEntity b, EnemyEntity[] bots, FightRecord fight, Simulation sim)
    {
        for (int j = 0; j < bots.Length; j++)
            if (bots[j] != null && bots[j].Id == b.CurrentTargetId)
                return j < fight.Entries.Count ? fight.Entries[j].Spec.Name : $"#{j}";
        return sim.Player.Id == b.CurrentTargetId ? "player" : b.CurrentTargetId.ToString();
    }

    private static Color TeamColor(int team) => team switch
    {
        0 => Color.White,
        1 => new Color(255, 120, 90),
        2 => new Color(110, 170, 255),
        3 => new Color(150, 230, 120),
        _ => Color.Orchid,
    };

    private static void Shadowed(SpriteBatch sb, SpriteFont font, string text, Vector2 pos, Color color)
    {
        sb.DrawString(font, text, pos + new Vector2(1, 1), Color.Black);
        sb.DrawString(font, text, pos, color);
    }
}
