using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Screen-space HUD: cursor marker, player health bar, escalation percent meter,
// state/action/anim debug text, and the block-picker swatches. Owns its own
// SpriteBatch.Begin/End pass (untransformed, screen pixels) — call Draw once per
// frame after the world-space passes have ended.
public sealed class HudRenderer
{
    private readonly SpriteBatch     _spriteBatch;
    private readonly Texture2D       _pixel;
    private readonly SpriteFont      _debugFont;
    private readonly GraphicsDevice  _graphicsDevice;

    public HudRenderer(SpriteBatch spriteBatch, Texture2D pixel, SpriteFont debugFont,
                       GraphicsDevice graphicsDevice)
    {
        _spriteBatch    = spriteBatch;
        _pixel          = pixel;
        _debugFont      = debugFont;
        _graphicsDevice = graphicsDevice;
    }

    public void Draw(Simulation sim, CharacterAnimator animator, GameConfig config)
    {
        var player = sim.Player;

        _spriteBatch.Begin();
        var mousePos = sim.CurrentInput.MousePosition;
        _spriteBatch.Draw(_pixel, new Rectangle(mousePos.X - 2, mousePos.Y - 2, 5, 5), Color.Red);
        if (config.DebugDrawHealthBars) DrawPlayerHealthBar(sim);
        DrawPercentHud(sim);   // always on — the percent meter is a core gameplay readout, not debug
        _spriteBatch.DrawString(_debugFont, player.CurrentStateName,  new Vector2(8,  8), Color.White);
        _spriteBatch.DrawString(_debugFont, player.CurrentActionName, new Vector2(8, 24), Color.White);
        _spriteBatch.DrawString(_debugFont, $"Anim: {animator.State.Clip}", new Vector2(8, 40), Color.Aqua);
        _spriteBatch.DrawString(_debugFont,
            $"Planner: {sim.EruptionMode}  (P to toggle)",
            new Vector2(8, 56),
            sim.EruptionMode == EruptionPlannerMode.MassBall ? Color.LightCoral : Color.LightSkyBlue);

        DrawBlockPickerHud(sim);

        _spriteBatch.End();
    }

    // Top-right block-picker indicator: four 24x24 swatches in a row, one per
    // pickable TileType, the selected one brightened and outlined, with 1-4 labels.
    private void DrawBlockPickerHud(Simulation sim)
    {
        var types = new[] { TileType.Stone, TileType.Dirt, TileType.Sand, TileType.Foam };

        const int SwatchSize    = 24;
        const int SwatchGap     = 6;
        const int RightPadding  = 12;
        const int TopPadding    = 8;
        const int LabelOffset   = SwatchSize + 4;

        int viewportW = _graphicsDevice.Viewport.Width;
        int totalW    = types.Length * SwatchSize + (types.Length - 1) * SwatchGap;
        int x0        = viewportW - RightPadding - totalW;
        int y0        = TopPadding;

        var activeBlockType = sim.ActiveBlockType;
        for (int i = 0; i < types.Length; i++)
        {
            int x = x0 + i * (SwatchSize + SwatchGap);
            bool selected = types[i] == activeBlockType;

            var col = TilePalette.BaseColor(types[i]);
            var fill = selected ? col : new Color((int)(col.R * 0.4f), (int)(col.G * 0.4f), (int)(col.B * 0.4f));
            _spriteBatch.Draw(_pixel, new Rectangle(x, y0, SwatchSize, SwatchSize), fill);

            var border = selected ? Color.White : new Color(80, 80, 80);
            _spriteBatch.Draw(_pixel, new Rectangle(x,                  y0,                  SwatchSize, 1), border);
            _spriteBatch.Draw(_pixel, new Rectangle(x,                  y0 + SwatchSize - 1, SwatchSize, 1), border);
            _spriteBatch.Draw(_pixel, new Rectangle(x,                  y0,                  1, SwatchSize), border);
            _spriteBatch.Draw(_pixel, new Rectangle(x + SwatchSize - 1, y0,                  1, SwatchSize), border);

            string keyLabel = (i + 1).ToString();
            _spriteBatch.DrawString(_debugFont, keyLabel,
                new Vector2(x + SwatchSize / 2f - 4, y0 + LabelOffset),
                selected ? Color.White : new Color(160, 160, 160));
        }

        _spriteBatch.DrawString(_debugFont, activeBlockType.ToString(),
            new Vector2(x0, y0 + LabelOffset + 16), Color.White);
    }

    private void DrawPlayerHealthBar(Simulation sim)
    {
        const int X = 8, Y = 56, BarW = 120, BarH = 8;
        var player = sim.Player;
        float frac = MathHelper.Clamp(player.Health / player.MaxHealth, 0f, 1f);
        _spriteBatch.Draw(_pixel, new Rectangle(X, Y, BarW, BarH), new Color(40, 40, 40));
        _spriteBatch.Draw(_pixel, new Rectangle(X, Y, (int)(BarW * frac), BarH),
            Color.Lerp(Color.Red, Color.LimeGreen, frac));
        _spriteBatch.DrawString(_debugFont,
            $"HP {player.Health:F1}/{player.MaxHealth:F1}",
            new Vector2(X + BarW + 8, Y - 4), Color.White);
    }

    // Escalation percent (COMBAT_FEEL_PLAN Phase 5) — the monotonic meter that scales
    // incoming knockback. A core gameplay readout, so it's always on (independent of
    // DebugDrawHealthBars), pinned to the lower-left and tinted hotter as it climbs.
    private void DrawPercentHud(Simulation sim)
    {
        var vp = _graphicsDevice.Viewport;
        float pct = sim.Player.Combat.DamagePercent;
        var color = Color.Lerp(Color.White, Color.OrangeRed, MathHelper.Clamp(pct / 200f, 0f, 1f));
        _spriteBatch.DrawString(_debugFont, $"{pct:F0}%",
            new Vector2(12f, vp.Height - 34f), color,
            0f, Vector2.Zero, 1.6f, SpriteEffects.None, 0f);
    }
}
