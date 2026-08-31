using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Screen-space HUD: the always-on player panel (health / blocks) bottom-left,
// plus the cursor marker, state/action/anim debug text, the block-picker swatches and
// the build-meter bars. Owns its own SpriteBatch.Begin/End pass (untransformed, screen
// pixels) — call Draw once per frame after the world-space passes have ended.
public sealed class HudRenderer
{
    private readonly DrawContext     _draw;
    private readonly SpriteBatch     _spriteBatch;
    private readonly Texture2D       _pixel;
    private readonly SpriteFont      _debugFont;
    private readonly GraphicsDevice  _graphicsDevice;

    public HudRenderer(DrawContext draw, SpriteFont debugFont, GraphicsDevice graphicsDevice)
    {
        _draw           = draw;
        _spriteBatch    = draw.SpriteBatch;
        _pixel          = draw.Pixel;
        _debugFont      = debugFont;
        _graphicsDevice = graphicsDevice;
    }

    public void Draw(Simulation sim, CharacterAnimator animator)
    {
        var player = sim.Player;

        _spriteBatch.Begin();
        var mousePos = sim.CurrentInput.MousePosition;
        _spriteBatch.Draw(_pixel, new Rectangle(mousePos.X - 2, mousePos.Y - 2, 5, 5), Color.Red);
        DrawAvalancheCharge(sim);
        // Always on: health and blocks are what the player steers by, so unlike the
        // readouts below they are not behind a GameConfig debug flag.
        DrawPlayerHud(sim);
        _spriteBatch.DrawString(_debugFont, player.CurrentStateName,  new Vector2(8,  8), Color.White);
        _spriteBatch.DrawString(_debugFont, player.CurrentActionName, new Vector2(8, 24), Color.White);
        _spriteBatch.DrawString(_debugFont, $"Anim: {animator.State.Clip}", new Vector2(8, 40), Color.Aqua);
        DrawBlockPickerHud(sim);
        DrawBuildMetersHud(sim);

        _spriteBatch.End();
    }

    // Build economy readout, under the block picker. Two stacked bars — the reservoir and
    // the working pool — because both live on time horizons the player cannot otherwise
    // reason about: a reservoir that drains while you hold a button is invisible without
    // this. The third pool, the eruption charge, is NOT here: it moved to the ring around
    // the cursor (DrawAvalancheCharge), which is where that mechanic actually happens.
    private void DrawBuildMetersHud(Simulation sim)
    {
        var m = sim.Player.Abilities.Meters;

        const int BarW = 126, BarH = 7, Gap = 4;
        const int RightPadding = 12;
        int x = _graphicsDevice.Viewport.Width - RightPadding - BarW;
        int y = 8 + 24 + 18;   // below the picker swatches + their labels

        void Bar(int yy, float frac, Color fill, string label)
        {
            _spriteBatch.Draw(_pixel, new Rectangle(x, yy, BarW, BarH), new Color(24, 24, 28));
            int w = (int)(BarW * MathHelper.Clamp(frac, 0f, 1f));
            if (w > 0) _spriteBatch.Draw(_pixel, new Rectangle(x, yy, w, BarH), fill);
            _spriteBatch.Draw(_pixel, new Rectangle(x, yy, BarW, 1), new Color(70, 70, 78));
            _spriteBatch.Draw(_pixel, new Rectangle(x, yy + BarH - 1, BarW, 1), new Color(70, 70, 78));
            _spriteBatch.DrawString(_debugFont, label, new Vector2(x - 34, yy - 3), new Color(150, 150, 160));
        }

        Bar(y,                     m.Build     / BuildMeters.BuildMax, new Color(90, 130, 200),  "res");
        Bar(y + BarH + Gap,        m.BuildMove / BuildMeters.MoveMax,  new Color(120, 200, 235), "mov");
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

    // ── Avalanche charge ─────────────────────────────────────────────────────────
    //
    // The eruption charge (BuildMeters.EruptMove) as a ring that expands around the
    // cursor, rather than a bar in a corner. It belongs at the mouse because that is
    // where the mechanic is: the charge only builds while the cursor is biting into
    // terrain, and it is the cursor that decides where the avalanche goes — a corner bar
    // asked the player to watch the wrong half of the screen at the one moment they
    // could least afford to.
    //
    // Everything here is a pure function of this frame's sim state, the flash included:
    // it reads its own progress out of BuildMeters.ChargeHeld instead of latching a
    // render-side timer, so a rollback that rewinds the charge rewinds the indicator
    // with it, and a mid-charge desync correction can't leave a flash stuck on.
    private const float RingMinRadius = 10f;
    private const float RingMaxRadius = 64f;
    private const int   RingSegments  = 40;
    // How far the flash's second ring races past the charge ring as it fades.
    private const float FlashSpread   = 26f;

    private void DrawAvalancheCharge(Simulation sim)
    {
        var m = sim.Player.Abilities.Meters;
        float frac = MathHelper.Clamp(m.ChargeFraction, 0f, 1f);
        if (frac <= 0.001f) return;      // nothing banked and nothing building

        var mouse  = sim.CurrentInput.MousePosition;
        var center = new Vector2(mouse.X, mouse.Y);
        float radius = MathHelper.Lerp(RingMinRadius, RingMaxRadius, frac);

        // The release gate: under EruptMinToFire a release paints instead of erupting.
        // A fixed faint ring makes the charge visibly cross a line — the same number the
        // corner bar marked with a tick, in the same units, just bent into a circle.
        float gate = MathHelper.Lerp(RingMinRadius, RingMaxRadius,
                                     BuildMeters.EruptMinToFire / BuildMeters.EruptMax);
        _draw.Ring(center, gate, new Color(120, 120, 130) * 0.45f, RingSegments);

        // Colour-coded by phase, because the timing window is the mechanic: below the gate
        // is dim (a release does nothing yet), armed is amber, Peak is the sweet spot, and
        // Overheld is money burning.
        var (color, thickness) = m.Phase switch
        {
            ChargePhase.Peak     => (Color.Lerp(Color.Gold, Color.White, PeakFlash(m)), 3f),
            ChargePhase.Overheld => (new Color(215, 85, 45), 2f),
            _ => m.CanFireEruption ? (new Color(235, 175, 80), 2f)
                                   : (new Color(150, 120, 80), 1f),
        };
        _draw.Ring(center, radius, color, RingSegments, thickness);

        // The flash. Peak lasts only PlateauSeconds, so the plateau IS the flash: a second
        // ring breaks outward from the charge ring and fades as the release window closes,
        // which reads from the corner of the eye instead of needing to be looked at.
        if (m.Phase == ChargePhase.Peak)
        {
            float f = PeakFlash(m);
            _draw.Ring(center, radius + (1f - f) * FlashSpread,
                       Color.White * (f * 0.7f), RingSegments, 2f);
        }
    }

    // 1 at the instant the plateau opens, 0 as it closes. Pure function of sim state, so
    // the flash needs no render-side clock of its own.
    private static float PeakFlash(BuildMeters m)
    {
        float t = (m.ChargeHeld - BuildMeters.ChargeRampSeconds) / BuildMeters.PlateauSeconds;
        return MathHelper.Clamp(1f - t, 0f, 1f);
    }

    // ── The player panel ─────────────────────────────────────────────────────────
    //
    // Health and blocks-in-hand, stacked bottom-left. Laid out bottom-up from a single
    // cursor so a row can change height (the blocks row with the swatch) without any
    // row below it moving.
    //
    // There used to be a third row here, a big escalation-percent readout above the
    // health bar. It went with the percent model itself: hits chip HP directly now, so
    // the health bar IS the damage meter and a second number would just be the same
    // information with the wrong units.
    private const int   PanelX       = 12;
    private const int   PanelBottom  = 12;
    private const int   PanelW       = 132;
    private const int   RowGap       = 6;
    private const int   HealthBarH   = 12;
    private const int   SwatchS      = 14;

    private void DrawPlayerHud(Simulation sim)
    {
        var vp = _graphicsDevice.Viewport;
        int blocksH  = Math.Max(SwatchS, _debugFont.LineSpacing);

        int y = vp.Height - PanelBottom - blocksH;
        DrawBlocksRow(sim, PanelX, y, blocksH);

        y -= RowGap + HealthBarH;
        DrawHealthRow(sim, PanelX, y);
    }

    // Health is the whole damage model now: every landed hit comes off it (a stock
    // slash is 0.5 of MaxHealth 5), plus crush impact into terrain. There is little
    // enough of it that a smooth bar reads as noise, so it is segmented at 1 HP — a
    // lost point is a visibly lost chunk — and the slow out-of-combat regen is what
    // makes the leading segment partial.
    private void DrawHealthRow(Simulation sim, int x, int y)
    {
        var p = sim.Player;
        float frac = MathHelper.Clamp(p.Health / p.MaxHealth, 0f, 1f);

        _spriteBatch.Draw(_pixel, new Rectangle(x, y, PanelW, HealthBarH), new Color(24, 24, 28));
        int w = (int)(PanelW * frac);
        if (w > 0)
            _spriteBatch.Draw(_pixel, new Rectangle(x, y, w, HealthBarH),
                Color.Lerp(new Color(200, 60, 55), new Color(95, 205, 110), frac));

        // Separators at each whole HP. Guarded on segment count so a future MaxHealth of
        // 100 degrades to a plain bar instead of a solid wall of ticks.
        int segments = (int)MathF.Round(p.MaxHealth);
        if (segments > 1 && segments <= 12)
            for (int i = 1; i < segments; i++)
            {
                int sx = x + (int)(PanelW * (i / (float)segments));
                _spriteBatch.Draw(_pixel, new Rectangle(sx, y, 1, HealthBarH), new Color(24, 24, 28));
            }

        Frame(x, y, PanelW, HealthBarH);
        _spriteBatch.DrawString(_debugFont, $"HP {p.Health:0.#}/{p.MaxHealth:0.#}",
            new Vector2(x + PanelW + 8, y - 2), new Color(200, 200, 210));
    }

    // Blocks in hand: the reservoir converted into placeable tiles of the CURRENTLY
    // selected material, so the number answers "how many of these can I put down"
    // instead of reporting an abstract meter unit. It jumps when you switch material —
    // foam is 16x cheaper than stone — which is exactly the thing worth seeing.
    //
    // Only the reservoir (BuildMeters.Build) is counted, not the working pool or the
    // banked eruption charge. Those two are the fast tiers and both refill out of this
    // one, so the reservoir is what actually caps a long build; the three-bar breakdown
    // top-right stays for the moment-to-moment detail.
    private void DrawBlocksRow(Simulation sim, int x, int y, int rowH)
    {
        var type   = sim.ActiveBlockType;
        var meters = sim.Player.Abilities.Meters;
        float cost = MaterialStrengths.BuildCostFor(type);
        int blocks = cost > 0f ? (int)(meters.Build / cost) : 0;

        int sy = y + (rowH - SwatchS) / 2;
        _spriteBatch.Draw(_pixel, new Rectangle(x, sy, SwatchS, SwatchS), TilePalette.BaseColor(type));
        Frame(x, sy, SwatchS, SwatchS, new Color(30, 30, 34));

        // The count comes from the reservoir, but the warning tint asks the pool that
        // actually pays for a placement (CanAfford spends working + charge). Colouring
        // off the reservoir instead would flash red while you still had a full working
        // pool to place from.
        var textColor = meters.CanAfford(type) ? new Color(225, 225, 235) : new Color(205, 90, 70);
        _spriteBatch.DrawString(_debugFont, $"x {blocks}",
            new Vector2(x + SwatchS + 8, y + (rowH - _debugFont.LineSpacing) / 2f), textColor);
    }

    private void Frame(int x, int y, int w, int h, Color? color = null)
    {
        var c = color ?? new Color(70, 70, 78);
        _spriteBatch.Draw(_pixel, new Rectangle(x,         y,         w, 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(x,         y + h - 1, w, 1), c);
        _spriteBatch.Draw(_pixel, new Rectangle(x,         y,         1, h), c);
        _spriteBatch.Draw(_pixel, new Rectangle(x + w - 1, y,         1, h), c);
    }
}
