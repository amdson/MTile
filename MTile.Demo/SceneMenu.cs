using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MTile;

namespace MTileDemo;

// A compact header DROPDOWN (the "Scene" menu). Immediate-mode: the host rebuilds the item
// list each frame, the menu owns open/closed state and row hit-testing. Header UI has picking
// priority over everything in the working area, so a press that lands on the button or an
// open menu is always consumed; a press elsewhere closes the menu and is consumed too (so the
// closing click cannot also start a drag underneath).
internal sealed class SceneMenu
{
    private readonly struct Item
    {
        public readonly string Label; public readonly Action Act; public readonly bool Checked, Enabled, Separator;
        public Item(string label, Action act, bool @checked, bool enabled, bool separator)
        { Label = label; Act = act; Checked = @checked; Enabled = enabled; Separator = separator; }
    }

    public bool      Open;
    public Rectangle Button;          // set by the host each frame
    public string    Label = "Scene v";   // the button's text
    public string    Title;               // optional first row (an endpoint menu names its target)
    private readonly List<Item> _items = new();
    private const int RowH = 20, Width = 300;

    public void Clear() => _items.Clear();
    public void Add(string label, Action act, bool @checked = false, bool enabled = true)
        => _items.Add(new Item(label, act, @checked, enabled, false));
    public void Separator() => _items.Add(new Item(null, null, false, false, true));

    private int TitleH => Title == null ? 0 : RowH;
    private Rectangle Panel
    {
        get
        {
            // Keep the panel on screen when the button sits near the right edge.
            int left = Button.Left, h = _items.Count * RowH + 8 + TitleH;
            return new Rectangle(left, Button.Bottom + 2, Width, h);
        }
    }

    // Returns true when the press was consumed by the menu (button, a row, or a click-away close).
    public bool HandlePress(Vector2 mp)
    {
        var p = new Point((int)mp.X, (int)mp.Y);
        if (Button.Contains(p)) { Open = !Open; return true; }
        if (!Open) return false;
        if (Panel.Contains(p))
        {
            int row = (p.Y - Panel.Top - 4 - TitleH) / RowH;
            if (row >= 0 && row < _items.Count && _items[row].Enabled && !_items[row].Separator)
            { _items[row].Act?.Invoke(); Open = false; }
            return true;
        }
        Open = false;
        return true;
    }

    public void Draw(DrawContext draw, SpriteBatch sb, SpriteFont font)
    {
        sb.Draw(draw.Pixel, Button, Open ? new Color(70, 100, 150) : new Color(45, 55, 75));
        sb.DrawString(font, Label, new Vector2(Button.Left + 6, Button.Top + 3), Color.White);
        if (!Open) return;
        var panel = Panel;
        sb.Draw(draw.Pixel, panel, new Color(16, 18, 26, 245));
        draw.Line(new Vector2(panel.Left, panel.Top), new Vector2(panel.Right, panel.Top), new Color(90, 130, 120), 1f);
        draw.Line(new Vector2(panel.Left, panel.Bottom), new Vector2(panel.Right, panel.Bottom), new Color(90, 130, 120), 1f);
        if (Title != null) sb.DrawString(font, Title, new Vector2(panel.Left + 10, panel.Top + 6), new Color(150, 200, 255));
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            int y = panel.Top + 4 + TitleH + i * RowH;
            if (it.Separator) { draw.Line(new Vector2(panel.Left + 8, y + RowH / 2), new Vector2(panel.Right - 8, y + RowH / 2), new Color(60, 70, 85), 1f); continue; }
            var c = !it.Enabled ? new Color(90, 95, 110) : Color.White;
            sb.DrawString(font, (it.Checked ? "[x] " : "    ") + it.Label, new Vector2(panel.Left + 10, y + 2), c);
        }
    }
}
