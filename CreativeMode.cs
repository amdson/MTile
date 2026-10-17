using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile;

// Level-editing mode (Tab in Game1, offline only). The sim FREEZES — no Step runs while
// it's on — and the camera comes loose: pan with WASD/arrows or a middle-drag, zoom on
// the wheel. LMB paints the selected material, RMB erases, both in a square brush with
// stroke-level undo. Tab again resumes play exactly where you left it, so build → test
// → build is one key. Ctrl+S (handled by Game1) writes the result back into the loaded
// level via LevelWriter.
//
// Edits go through ChunkMap.EditorSetCell: instant Solid/Empty, no support rule, no
// sprouting, no break events. Hardened is paintable here even though no player verb
// can place it — levels use it.
public sealed class CreativeMode
{
    public bool Active { get; private set; }
    public TileType Material { get; private set; } = TileType.Stone;
    public int Brush { get; private set; } = 1;     // odd square side, in cells
    public int UnsavedEdits { get; private set; }

    private static readonly TileType[] Materials =
        { TileType.Stone, TileType.Dirt, TileType.Sand, TileType.Foam, TileType.Hardened };
    private static readonly Keys[] MaterialKeys = { Keys.D1, Keys.D2, Keys.D3, Keys.D4, Keys.D5 };
    private const int MaxBrush = 9;
    private const float PanSpeed = 700f;            // screen px/s; divided by zoom
    private const float MinZoom = 0.25f, MaxZoom = 6f;

    private readonly record struct Edit(int Gtx, int Gty, TileState State, TileType Type);
    private readonly List<List<Edit>> _undo = new();
    private List<Edit> _stroke;
    private readonly HashSet<(int, int)> _strokeCells = new();
    private (int x, int y)? _lastCell;

    private KeyboardState _prevKeys;
    private MouseState _prevMouse;
    private (int x, int y) _hover;

    public void Toggle(Camera camera, float playZoom)
    {
        Active = !Active;
        EndStroke();
        if (!Active) camera.Zoom = playZoom;   // the tracking camera takes over again next frame
        _prevKeys = Keyboard.GetState();
        _prevMouse = Mouse.GetState();
    }

    // The stage was reloaded (F5): old undo entries describe a world that's gone.
    public void ResetHistory()
    {
        EndStroke();
        _undo.Clear();
        UnsavedEdits = 0;
    }

    public void MarkSaved() => UnsavedEdits = 0;

    public void Update(KeyboardState k, MouseState m, Camera camera, Vector2 screenCenter,
                       Simulation sim, float dt)
    {
        bool Pressed(Keys key) => k.IsKeyDown(key) && !_prevKeys.IsKeyDown(key);
        bool ctrl  = k.IsKeyDown(Keys.LeftControl) || k.IsKeyDown(Keys.RightControl);
        bool shift = k.IsKeyDown(Keys.LeftShift) || k.IsKeyDown(Keys.RightShift);

        for (int i = 0; i < Materials.Length; i++)
            if (Pressed(MaterialKeys[i])) Material = Materials[i];
        if (Pressed(Keys.OemCloseBrackets)) Brush = Math.Min(MaxBrush, Brush + 2);
        if (Pressed(Keys.OemOpenBrackets))  Brush = Math.Max(1, Brush - 2);

        // Pan: keys (Ctrl held = a shortcut, not a pan), then middle-drag.
        if (!ctrl)
        {
            var pan = Vector2.Zero;
            if (k.IsKeyDown(Keys.A) || k.IsKeyDown(Keys.Left))  pan.X -= 1;
            if (k.IsKeyDown(Keys.D) || k.IsKeyDown(Keys.Right)) pan.X += 1;
            if (k.IsKeyDown(Keys.W) || k.IsKeyDown(Keys.Up))    pan.Y -= 1;
            if (k.IsKeyDown(Keys.S) || k.IsKeyDown(Keys.Down))  pan.Y += 1;
            camera.Position += pan * PanSpeed * (shift ? 3f : 1f) * dt / camera.Zoom;
        }
        if (m.MiddleButton == ButtonState.Pressed && _prevMouse.MiddleButton == ButtonState.Pressed)
            camera.Position -= (m.Position - _prevMouse.Position).ToVector2() / camera.Zoom;

        // Zoom about the cursor: the world point under it stays put.
        int wheel = m.ScrollWheelValue - _prevMouse.ScrollWheelValue;
        if (wheel != 0)
        {
            var before = camera.ScreenToWorld(m.Position.ToVector2(), screenCenter);
            camera.Zoom = Math.Clamp(camera.Zoom * MathF.Pow(1.0015f, wheel), MinZoom, MaxZoom);
            var after = camera.ScreenToWorld(m.Position.ToVector2(), screenCenter);
            camera.Position += before - after;
        }

        var world = camera.ScreenToWorld(m.Position.ToVector2(), screenCenter);
        _hover = ((int)MathF.Floor(world.X / Chunk.TileSize), (int)MathF.Floor(world.Y / Chunk.TileSize));

        if (ctrl && Pressed(Keys.Z)) Undo(sim.Chunks);

        // T: drop the player at the cursor, at rest — for testing a spot you just built.
        if (Pressed(Keys.T))
        {
            sim.Player.Body.Position = world;
            sim.Player.Body.Velocity = Vector2.Zero;
        }

        bool paint = m.LeftButton  == ButtonState.Pressed;
        bool erase = m.RightButton == ButtonState.Pressed;
        if (paint || erase)
        {
            if (_stroke == null) { _stroke = new List<Edit>(); _strokeCells.Clear(); _lastCell = null; }
            // Walk from last frame's cell so a fast drag leaves a line, not dots.
            var from = _lastCell ?? _hover;
            int steps = Math.Max(Math.Abs(_hover.x - from.x), Math.Abs(_hover.y - from.y));
            for (int s = 0; s <= steps; s++)
            {
                float t = steps == 0 ? 1f : s / (float)steps;
                int cx = (int)MathF.Round(MathHelper.Lerp(from.x, _hover.x, t));
                int cy = (int)MathF.Round(MathHelper.Lerp(from.y, _hover.y, t));
                Stamp(sim.Chunks, cx, cy, paint ? TileState.Solid : TileState.Empty);
            }
            _lastCell = _hover;
        }
        else EndStroke();

        _prevKeys = k;
        _prevMouse = m;
    }

    private void Stamp(ChunkMap map, int cx, int cy, TileState state)
    {
        int r = Brush / 2;
        for (int x = cx - r; x <= cx + r; x++)
            for (int y = cy - r; y <= cy + r; y++)
            {
                var prevState = map.GetCellState(x, y) == TileState.Solid ? TileState.Solid : TileState.Empty;
                var prevType  = map.GetCellType(x, y);
                if (!map.EditorSetCell(x, y, state, Material)) continue;
                if (_strokeCells.Add((x, y))) _stroke.Add(new Edit(x, y, prevState, prevType));
                UnsavedEdits++;
            }
    }

    private void EndStroke()
    {
        if (_stroke is { Count: > 0 }) _undo.Add(_stroke);
        _stroke = null;
        _lastCell = null;
    }

    private void Undo(ChunkMap map)
    {
        EndStroke();
        if (_undo.Count == 0) return;
        var stroke = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        for (int i = stroke.Count - 1; i >= 0; i--)
        {
            var e = stroke[i];
            if (map.EditorSetCell(e.Gtx, e.Gty, e.State, e.Type)) UnsavedEdits++;
        }
    }

    // World-space overlay: a faint tile grid over the view, chunk borders stronger, and
    // the brush footprint under the cursor in the selected material's colour.
    public void DrawWorld(SpriteBatch sb, Texture2D pixel, Camera camera, Vector2 screenCenter)
    {
        var tl = camera.ScreenToWorld(Vector2.Zero, screenCenter);
        var br = camera.ScreenToWorld(screenCenter * 2f, screenCenter);
        const int ts = Chunk.TileSize;
        int x0 = (int)MathF.Floor(tl.X / ts), x1 = (int)MathF.Ceiling(br.X / ts);
        int y0 = (int)MathF.Floor(tl.Y / ts), y1 = (int)MathF.Ceiling(br.Y / ts);
        float line = 1f / camera.Zoom;

        sb.Begin(transformMatrix: camera.GetTransform(screenCenter), samplerState: SamplerState.PointClamp);
        // Skip the per-tile grid when zoomed far out — it'd be a solid wash.
        bool cells = camera.Zoom >= 0.8f;
        for (int x = x0; x <= x1; x++)
        {
            bool chunk = x % Chunk.Size == 0;
            if (!chunk && !cells) continue;
            sb.Draw(pixel, new Vector2(x * ts, y0 * ts), null, Color.White * (chunk ? 0.25f : 0.07f),
                    0f, Vector2.Zero, new Vector2(line, (y1 - y0) * ts), SpriteEffects.None, 0f);
        }
        for (int y = y0; y <= y1; y++)
        {
            bool chunk = y % Chunk.Size == 0;
            if (!chunk && !cells) continue;
            sb.Draw(pixel, new Vector2(x0 * ts, y * ts), null, Color.White * (chunk ? 0.25f : 0.07f),
                    0f, Vector2.Zero, new Vector2((x1 - x0) * ts, line), SpriteEffects.None, 0f);
        }

        int r = Brush / 2;
        var origin = new Vector2((_hover.x - r) * ts, (_hover.y - r) * ts);
        float side = Brush * ts;
        var fill = TilePalette.BaseColor(Material);
        sb.Draw(pixel, origin, null, fill * 0.35f, 0f, Vector2.Zero, new Vector2(side, side), SpriteEffects.None, 0f);
        float w = 2f / camera.Zoom;
        sb.Draw(pixel, origin, null, Color.White, 0f, Vector2.Zero, new Vector2(side, w), SpriteEffects.None, 0f);
        sb.Draw(pixel, origin + new Vector2(0, side - w), null, Color.White, 0f, Vector2.Zero, new Vector2(side, w), SpriteEffects.None, 0f);
        sb.Draw(pixel, origin, null, Color.White, 0f, Vector2.Zero, new Vector2(w, side), SpriteEffects.None, 0f);
        sb.Draw(pixel, origin + new Vector2(side - w, 0), null, Color.White, 0f, Vector2.Zero, new Vector2(w, side), SpriteEffects.None, 0f);
        sb.End();
    }

    public void DrawHud(SpriteBatch sb, SpriteFont font, Texture2D pixel, int viewportWidth)
    {
        string title = $"CREATIVE   {Material}   brush {Brush}x{Brush}   cell {_hover.x},{_hover.y}"
                     + (UnsavedEdits > 0 ? $"   {UnsavedEdits} unsaved" : "   saved");
        const string help = "LMB paint  RMB erase  1-5 material  [ ] brush  wheel zoom  WASD/MMB pan  "
                          + "Ctrl+Z undo  T teleport  Ctrl+S save level  Tab play";
        sb.Begin();
        var pos = new Vector2(MathF.Max(8f, (viewportWidth - font.MeasureString(help).X) / 2f), 8f);
        // Material swatches, the selected one outlined.
        for (int i = 0; i < Materials.Length; i++)
        {
            var at = new Rectangle((int)pos.X + i * 26, (int)pos.Y, 22, 22);
            if (Materials[i] == Material) sb.Draw(pixel, new Rectangle(at.X - 2, at.Y - 2, 26, 26), Color.White);
            sb.Draw(pixel, at, TilePalette.BaseColor(Materials[i]));
            sb.DrawString(font, (i + 1).ToString(), new Vector2(at.X + 7, at.Y + 3), Color.Black);
        }
        var text = pos + new Vector2(Materials.Length * 26 + 12, 3);
        sb.DrawString(font, title, text + Vector2.One, Color.Black);
        sb.DrawString(font, title, text, UnsavedEdits > 0 ? Color.Orange : Color.LightGreen);
        var helpPos = pos + new Vector2(0, 30);
        sb.DrawString(font, help, helpPos + Vector2.One, Color.Black);
        sb.DrawString(font, help, helpPos, Color.LightGray);
        sb.End();
    }
}
