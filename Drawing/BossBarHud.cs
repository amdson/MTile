using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Top-of-screen health bars for major bosses (EnemyEntity.BossTitle != null): the name,
// then a wide bar centred under it. Lost health first shows as a pale chunk that drains
// after a short hold, so a hit reads as a bite out of the bar. A boss with a shield pool
// (Quillback's shell) gets a thin bar above its health. Several live bosses stack.
// Render-only: reads entity state, never touches the sim; the lag values are keyed on
// EntityId so a rollback that rewrites health just eases the trail to the new value.
public sealed class BossBarHud
{
    private const int   MaxBarW   = 420;
    private const int   BarH      = 9;
    private const int   ShieldH   = 3;
    private const int   Top       = 10;
    private const int   RowGap    = 8;
    private const float TrailHold = 0.35f;   // s the bitten chunk lingers before draining
    private const float TrailRate = 0.6f;    // fraction of the bar drained per second

    private static readonly Color Back   = new(24, 24, 28);
    private static readonly Color Edge   = new(70, 70, 78);
    private static readonly Color Fill   = new(200, 50, 55);
    private static readonly Color Trail  = new(240, 215, 180);
    private static readonly Color Shield = new(150, 175, 215);

    private sealed class Lag { public float Frac, Hold; public bool Seen; }

    private readonly Dictionary<EntityId, Lag> _lag  = new();
    private readonly HashSet<EntityId>         _live = new();

    public void Clear() => _lag.Clear();

    public void Draw(SpriteBatch sb, SpriteFont font, Texture2D pixel, Viewport vp,
                     IEnumerable<Entity> entities, float dt)
    {
        _live.Clear();
        int barW = Math.Min(MaxBarW, (int)(vp.Width * 0.5f));
        int x    = (vp.Width - barW) / 2;
        int y    = Top;
        bool begun = false;

        foreach (var e in entities)
        {
            if (e.IsDead || e is not EnemyEntity en || en.BossTitle is not { Length: > 0 } title) continue;
            if (!begun) { sb.Begin(); begun = true; }
            _live.Add(e.Id);

            float frac = e.MaxHealth > 0f ? MathHelper.Clamp(e.Health / e.MaxHealth, 0f, 1f) : 0f;
            if (!_lag.TryGetValue(e.Id, out var lag)) _lag[e.Id] = lag = new Lag();
            if (!lag.Seen || frac >= lag.Frac) { lag.Frac = frac; lag.Hold = 0f; lag.Seen = true; }
            else if (lag.Hold < TrailHold) lag.Hold += dt;
            else lag.Frac = MathF.Max(frac, lag.Frac - TrailRate * dt);

            var size = font.MeasureString(title);
            var namePos = new Vector2(MathF.Round(vp.Width / 2f - size.X / 2f), y);
            sb.DrawString(font, title, namePos + new Vector2(1, 1), Color.Black);
            sb.DrawString(font, title, namePos, new Color(230, 230, 236));
            y += font.LineSpacing + 2;

            float shield = en.BossShieldFraction;
            if (shield >= 0f)
            {
                sb.Draw(pixel, new Rectangle(x, y, barW, ShieldH), Back);
                sb.Draw(pixel, new Rectangle(x, y, (int)(barW * MathHelper.Clamp(shield, 0f, 1f)), ShieldH), Shield);
                y += ShieldH + 1;
            }

            sb.Draw(pixel, new Rectangle(x - 1, y - 1, barW + 2, BarH + 2), Edge);
            sb.Draw(pixel, new Rectangle(x, y, barW, BarH), Back);
            sb.Draw(pixel, new Rectangle(x, y, (int)(barW * lag.Frac), BarH), Trail);
            sb.Draw(pixel, new Rectangle(x, y, (int)(barW * frac), BarH), Fill);
            y += BarH + RowGap;
        }
        if (begun) sb.End();

        // Forget bosses that died or despawned.
        if (_lag.Count > _live.Count)
        {
            var stale = new List<EntityId>();
            foreach (var id in _lag.Keys) if (!_live.Contains(id)) stale.Add(id);
            foreach (var id in stale) _lag.Remove(id);
        }
    }
}
