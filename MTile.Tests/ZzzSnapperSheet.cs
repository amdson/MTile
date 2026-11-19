using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Microsoft.Xna.Framework;
using Xunit;

namespace MTile.Tests;

// Scratch harness (Zzz* is excluded from normal runs): CPU-rasterises SnapperFigure poses
// into contact-sheet PNGs. No GPU. Output dir = $SNAPPER_SHEET_DIR (skips if unset).
public class ZzzSnapperSheet
{
    private static readonly Color TeamA = new(255, 80, 70), TeamB = new(70, 150, 255);
    private const int SS = 2;

    private sealed class Img
    {
        public readonly int W, H; public readonly float[] Px;   // premultiplied RGBA, SS-resolution
        public Img(int w, int h) { W = w; H = h; Px = new float[w * h * 4]; }
    }

    [Fact]
    public void Render()
    {
        var dir = Environment.GetEnvironmentVariable("SNAPPER_SHEET_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var S = SnapperFigure.State.Seed;
        var all = new[] { SnapperFigure.State.Seed, SnapperFigure.State.Sprout, SnapperFigure.State.Idle, SnapperFigure.State.WindUp,
            SnapperFigure.State.Bite, SnapperFigure.State.Rearm, SnapperFigure.State.Rearm, SnapperFigure.State.Rearm,
            SnapperFigure.State.Dormant, SnapperFigure.State.Wilt };
        float[] rearmOf = { 0, 0, 0, 0, 0, 0f, 0.5f, 1f, 0, 0 };
        var F = SnapperFigure.RootFace.Floor;

        // Sheet 1: every state, floor, both teams (A leans right, B leans left) — enlarged and gameplay scale.
        foreach (var (name, scale) in new[] { ("states_big", 9f), ("states_game", 3.5f) })
        {
            var rows = new List<(SnapperFigure.RootFace, Color, float, float)> { (F, TeamA, 1f, 1f), (F, TeamB, 1f, -1f), (F, TeamA, 0.5f, 1f) };
            Save(Path.Combine(dir, name + ".png"), Sheet(all, rearmOf, rows, scale));
        }
        // Sheet 2: growth 0 / .5 / 1 for the mature states.
        {
            var rows = new List<(SnapperFigure.RootFace, Color, float, float)> { (F, TeamA, 0f, 1f), (F, TeamA, 0.5f, 1f), (F, TeamA, 1f, 1f) };
            Save(Path.Combine(dir, "growth.png"), Sheet(all, rearmOf, rows, 7f));
        }
        // Sheet 3: root faces.
        {
            var rows = new List<(SnapperFigure.RootFace, Color, float, float)>
            {
                (SnapperFigure.RootFace.Ceiling, TeamA, 1f, 1f), (SnapperFigure.RootFace.WallOnLeft, TeamB, 1f, 1f),
                (SnapperFigure.RootFace.WallOnRight, TeamA, 1f, -1f),
            };
            Save(Path.Combine(dir, "roots.png"), Sheet(all, rearmOf, rows, 7f));
        }
    }

    private static Img Sheet(SnapperFigure.State[] states, float[] rearm, List<(SnapperFigure.RootFace, Color, float, float)> rows, float scale)
    {
        int cw = (int)(34 * scale), ch = (int)(42 * scale);
        var img = new Img(cw * states.Length * SS, ch * rows.Count * SS);
        for (int i = 0; i < img.W * img.H; i++) { img.Px[i * 4] = 0.10f; img.Px[i * 4 + 1] = 0.12f; img.Px[i * 4 + 2] = 0.18f; img.Px[i * 4 + 3] = 1f; }
        for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < states.Length; c++)
            {
                var (face, team, growth, lean) = rows[r];
                var st = states[c];
                // root point within the cell
                float rx = cw / 2f, ry = ch * 0.78f;
                switch (face)
                {
                    case SnapperFigure.RootFace.Ceiling: ry = ch * 0.22f; break;
                    case SnapperFigure.RootFace.WallOnLeft: rx = cw * 0.3f; ry = ch / 2f; break;
                    case SnapperFigure.RootFace.WallOnRight: rx = cw * 0.7f; ry = ch / 2f; break;
                }
                float ox = c * cw + rx, oy = r * ch + ry;
                DrawDirt(img, c * cw, r * ch, cw, ch, ox, oy, face, scale, st == SnapperFigure.State.Dormant);
                var pose = SnapperFigure.Build(st, growth, lean, rearm[c], face, team, 0.37f);
                foreach (var sh in pose.Shapes) Raster(img, sh, ox, oy, scale);
            }
        return Down(img);
    }

    private static void DrawDirt(Img img, int x0, int y0, int w, int h, float ox, float oy, SnapperFigure.RootFace face, float scale, bool cover)
    {
        void Rect(float ax, float ay, float bx, float by, Color col, float tile)
        {
            for (int y = (int)Math.Max(y0 * SS, ay * SS); y < Math.Min((y0 + h) * SS, by * SS); y++)
                for (int x = (int)Math.Max(x0 * SS, ax * SS); x < Math.Min((x0 + w) * SS, bx * SS); x++)
                {
                    float lx = (x / (float)SS - ox) / scale, ly = (y / (float)SS - oy) / scale;
                    bool edge = Math.Abs(lx % 11f) < 0.35f || Math.Abs(ly % 11f) < 0.35f;
                    var c = edge ? col * 0.75f : col;
                    int i = (y * img.W + x) * 4; img.Px[i] = c.R / 255f; img.Px[i + 1] = c.G / 255f; img.Px[i + 2] = c.B / 255f; img.Px[i + 3] = 1f;
                }
        }
        var dirt = new Color(112, 78, 48);
        switch (face)
        {
            case SnapperFigure.RootFace.Floor: Rect(x0, oy, x0 + w, y0 + h, dirt, 11); break;
            case SnapperFigure.RootFace.Ceiling: Rect(x0, y0, x0 + w, oy, dirt, 11); break;
            case SnapperFigure.RootFace.WallOnLeft: Rect(x0, y0, ox, y0 + h, dirt, 11); break;
            default: Rect(ox, y0, x0 + w, y0 + h, dirt, 11); break;
        }
        if (cover)   // a stone block sitting on the plant's cell (floor case) — the plant is drawn over it
        {
            float s = 11f * scale;
            Rect(ox - s / 2, oy - s, ox + s / 2, oy, new Color(120, 124, 134), 11);
        }
    }

    private static void Blend(Img img, int x, int y, Color c)
    {
        if (x < 0 || y < 0 || x >= img.W || y >= img.H) return;
        int i = (y * img.W + x) * 4;
        float a = c.A / 255f, k = 1f - a;
        img.Px[i] = c.R / 255f + img.Px[i] * k; img.Px[i + 1] = c.G / 255f + img.Px[i + 1] * k;
        img.Px[i + 2] = c.B / 255f + img.Px[i + 2] * k; img.Px[i + 3] = a + img.Px[i + 3] * k;
    }

    private static void Raster(Img img, Shape sh, float ox, float oy, float scale)
    {
        Func<Vector2, bool> inside; Vector2 lo, hi; Color col;
        switch (sh)
        {
            case LineShape l:
            {
                var d = l.B - l.A; float len2 = d.LengthSquared(); if (len2 < 1e-6f) return;
                float hw = l.Thickness / 2f; col = l.Color;
                inside = p => { float t = Vector2.Dot(p - l.A, d) / len2; if (t < 0 || t > 1) return false;
                                return (p - (l.A + d * t)).Length() <= hw; };
                lo = Vector2.Min(l.A, l.B) - new Vector2(hw); hi = Vector2.Max(l.A, l.B) + new Vector2(hw); break;
            }
            case DiscShape d2:
                col = d2.Color; inside = p => (p - d2.Center).Length() <= d2.Radius;
                lo = d2.Center - new Vector2(d2.Radius); hi = d2.Center + new Vector2(d2.Radius); break;
            case RingShape r:
                col = r.Color; inside = p => Math.Abs((p - r.Center).Length() - r.Radius) <= r.Thickness / 2f;
                lo = r.Center - new Vector2(r.Radius + r.Thickness); hi = r.Center + new Vector2(r.Radius + r.Thickness); break;
            case BoxShape bx:
            {
                col = bx.Color; float c = MathF.Cos(-bx.LocalRotation), s = MathF.Sin(-bx.LocalRotation);
                inside = p => { var q = p - bx.Center; var u = new Vector2(q.X * c - q.Y * s, q.X * s + q.Y * c);
                                return Math.Abs(u.X) <= bx.Size.X / 2 && Math.Abs(u.Y) <= bx.Size.Y / 2; };
                float rad = bx.Size.Length(); lo = bx.Center - new Vector2(rad); hi = bx.Center + new Vector2(rad); break;
            }
            default: return;
        }
        int x0 = (int)((ox + lo.X * scale) * SS) - 1, x1 = (int)((ox + hi.X * scale) * SS) + 1;
        int y0 = (int)((oy + lo.Y * scale) * SS) - 1, y1 = (int)((oy + hi.Y * scale) * SS) + 1;
        for (int y = Math.Max(0, y0); y <= Math.Min(img.H - 1, y1); y++)
            for (int x = Math.Max(0, x0); x <= Math.Min(img.W - 1, x1); x++)
                if (inside(new Vector2(((x + 0.5f) / SS - ox) / scale, ((y + 0.5f) / SS - oy) / scale))) Blend(img, x, y, col);
    }

    private static Img Down(Img s)
    {
        var o = new Img(s.W / SS, s.H / SS);
        for (int y = 0; y < o.H; y++)
            for (int x = 0; x < o.W; x++)
                for (int ch = 0; ch < 4; ch++)
                {
                    float sum = 0;
                    for (int j = 0; j < SS; j++) for (int i = 0; i < SS; i++) sum += s.Px[((y * SS + j) * s.W + x * SS + i) * 4 + ch];
                    o.Px[(y * o.W + x) * 4 + ch] = sum / (SS * SS);
                }
        return o;
    }

    private static void Save(string path, Img img)
    {
        var raw = new byte[(img.W * 3 + 1) * img.H];
        for (int y = 0; y < img.H; y++)
            for (int x = 0; x < img.W; x++)
                for (int c = 0; c < 3; c++)
                    raw[y * (img.W * 3 + 1) + 1 + x * 3 + c] = (byte)Math.Clamp(img.Px[(y * img.W + x) * 4 + c] * 255f + 0.5f, 0, 255);
        using var ms = new MemoryStream();
        void Chunk(string t, byte[] d)
        {
            var len = BitConverter.GetBytes(d.Length); Array.Reverse(len); ms.Write(len);
            var td = new byte[4 + d.Length]; for (int i = 0; i < 4; i++) td[i] = (byte)t[i]; Array.Copy(d, 0, td, 4, d.Length);
            ms.Write(td); uint crc = Crc(td); var cb = BitConverter.GetBytes(crc); Array.Reverse(cb); ms.Write(cb);
        }
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        void BE(int v, int o) { ihdr[o] = (byte)(v >> 24); ihdr[o + 1] = (byte)(v >> 16); ihdr[o + 2] = (byte)(v >> 8); ihdr[o + 3] = (byte)v; }
        BE(img.W, 0); BE(img.H, 4); ihdr[8] = 8; ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        using (var z = new MemoryStream()) { using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(raw); Chunk("IDAT", z.ToArray()); }
        Chunk("IEND", Array.Empty<byte>());
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static uint Crc(byte[] d)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in d) { c ^= b; for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1; }
        return c ^ 0xFFFFFFFF;
    }
}
