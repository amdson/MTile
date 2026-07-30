using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MTile;

namespace MTileDemo;

// One-time intake of decomposed-limb character art (Plans/SPRITE_SKIN_PLAN.md §10.2):
//
//   dotnet run --project MTile.Demo -- --import SkeletonAssets/rabbit_and_badger --scale 0.25
//
// Per source PNG: crop to the alpha bounding box (+2 px margin), downscale by the shared
// --scale, write SpriteBindings/<character>/<part>.png. Per character: generate a
// first-pass SpriteBindings/<character>.json whose layers carry OffsetX/Y (crop origin
// × scale, so the parts reassemble on the shared canvas) in the plan's back-to-front
// order, with a rough auto-fit ImageToRig + default bind pose — the interactive bind
// session starts from "adjust", not zero.
//
// Desktop-only tooling: needs a GraphicsDevice for PNG decode/encode (headless boxes run
// it under xvfb). Runtime never sees the full-canvas originals (~435 MB decoded).
public sealed class ImportGame : Game
{
    private readonly GraphicsDeviceManager _graphics;
    private readonly string _srcDir, _outDir;
    private readonly float  _scale;

    // Which source file is which body part (Plans/SPRITE_SKIN_PLAN.md §10.1 — near/far
    // assignment is a data GUESS; fix by editing the generated json, not this table).
    private static readonly (string File, string Character, string Part)[] Manifest =
    {
        ("IMG_4367", "rabbit", "head"),
        ("IMG_4368", "rabbit", "leg_near"),
        ("IMG_4369", "rabbit", "leg_far"),
        ("IMG_4370", "rabbit", "arm_near"),
        ("IMG_4371", "rabbit", "torso"),
        ("IMG_4372", "rabbit", "arm_far"),
        ("IMG_4373", "badger", "arm_near"),
        ("IMG_4374", "badger", "leg_near"),
        ("IMG_4375", "badger", "leg_far"),
        ("IMG_4376", "badger", "torso"),
        ("IMG_4377", "badger", "arm_far"),
        ("IMG_4378", "badger", "head"),
    };

    // Back-to-front layer order + bone ownership per part (§10.6). Badger's boots both
    // draw behind the cloak so the unfilled white thigh outlines hide under it.
    private static readonly Dictionary<string, string[]> PartOrder = new()
    {
        ["rabbit"] = new[] { "arm_far", "leg_far", "torso", "leg_near", "arm_near", "head" },
        ["badger"] = new[] { "arm_far", "leg_far", "leg_near", "torso", "arm_near", "head" },
    };

    private static readonly Dictionary<string, string[]> PartBones = new()
    {
        ["arm_far"]  = new[] { "arm_r_*" },
        ["leg_far"]  = new[] { "leg_r_*", "foot_r" },
        ["torso"]    = new[] { "chest", "hip" },
        ["leg_near"] = new[] { "leg_l_*", "foot_l" },
        ["arm_near"] = new[] { "arm_l_*" },
        ["head"]     = new[] { "head" },
    };

    public ImportGame(string srcDir, string outDir, float scale)
    {
        _srcDir = srcDir; _outDir = outDir; _scale = scale;
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = 320, PreferredBackBufferHeight = 200,
        };
    }

    protected override void LoadContent()
    {
        try { RunImport(); }
        catch (Exception e)
        {
            Console.WriteLine($"Import FAILED: {e}");
            Environment.ExitCode = 1;
        }
        Exit();
    }

    private void RunImport()
    {
        // Layer records per character: (part, relative png path, offset, placed size).
        var layers = new Dictionary<string, List<(string Part, string Png, Point Off, Point Size)>>();

        foreach (var (file, character, part) in Manifest)
        {
            string src = Directory.EnumerateFiles(_srcDir)
                .FirstOrDefault(p => Path.GetFileNameWithoutExtension(p)
                    .Equals(file, StringComparison.OrdinalIgnoreCase));
            if (src == null)
            {
                Console.WriteLine($"  {file}: MISSING in {_srcDir} — skipped.");
                continue;
            }

            Color[] px; int w, h;
            using (var fs = File.OpenRead(src))
            using (var tex = Texture2D.FromStream(GraphicsDevice, fs))
            {
                w = tex.Width; h = tex.Height;
                px = new Color[w * h];
                tex.GetData(px);
            }

            // Alpha bounding box (+2 px margin, clamped) + coverage stat.
            int minX = w, minY = h, maxX = -1, maxY = -1; long opaque = 0;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (px[y * w + x].A > 0)
                {
                    opaque++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            if (maxX < 0) { Console.WriteLine($"  {file}: fully transparent — skipped."); continue; }
            minX = Math.Max(0, minX - 2); minY = Math.Max(0, minY - 2);
            maxX = Math.Min(w - 1, maxX + 2); maxY = Math.Min(h - 1, maxY + 2);
            int cw = maxX - minX + 1, ch = maxY - minY + 1;

            // Downscale the crop with a premultiplied box filter (straight-alpha
            // averaging would bleed the RGB of transparent texels into edges).
            int dw = Math.Max(1, (int)MathF.Round(cw * _scale));
            int dh = Math.Max(1, (int)MathF.Round(ch * _scale));
            var dst = new Color[dw * dh];
            for (int dy = 0; dy < dh; dy++)
            for (int dx = 0; dx < dw; dx++)
            {
                int sx0 = minX + (int)(dx * (float)cw / dw), sx1 = minX + Math.Max(1, (int)((dx + 1) * (float)cw / dw));
                int sy0 = minY + (int)(dy * (float)ch / dh), sy1 = minY + Math.Max(1, (int)((dy + 1) * (float)ch / dh));
                sx1 = Math.Min(sx1, maxX + 1); sy1 = Math.Min(sy1, maxY + 1);
                if (sx1 <= sx0) sx1 = sx0 + 1;
                if (sy1 <= sy0) sy1 = sy0 + 1;
                float r = 0, g = 0, b = 0, a = 0; int n = 0;
                for (int sy = sy0; sy < sy1; sy++)
                for (int sx = sx0; sx < sx1; sx++)
                {
                    var c = px[sy * w + sx];
                    float ca = c.A / 255f;
                    r += c.R * ca; g += c.G * ca; b += c.B * ca; a += ca; n++;
                }
                if (a <= 0) { dst[dy * dw + dx] = Color.Transparent; continue; }
                // Un-premultiply back to straight alpha (PNG convention).
                dst[dy * dw + dx] = new Color(
                    (int)MathF.Round(r / a), (int)MathF.Round(g / a), (int)MathF.Round(b / a),
                    (int)MathF.Round(a / n * 255f));
            }

            string rel = Path.Combine(character, part + ".png");
            string outPath = Path.Combine(_outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            using (var outTex = new Texture2D(GraphicsDevice, dw, dh))
            {
                outTex.SetData(dst);
                using var os = File.Create(outPath);
                outTex.SaveAsPng(os, dw, dh);
            }

            var off = new Point((int)MathF.Round(minX * _scale), (int)MathF.Round(minY * _scale));
            if (!layers.TryGetValue(character, out var list))
                layers[character] = list = new List<(string, string, Point, Point)>();
            list.Add((part, rel.Replace('\\', '/'), off, new Point(dw, dh)));

            Console.WriteLine($"  {file} -> {rel}: crop {cw}x{ch} @({minX},{minY}), " +
                              $"out {dw}x{dh} offset ({off.X},{off.Y}), " +
                              $"alpha coverage {100.0 * opaque / (w * (long)h):F1}%");
        }

        foreach (var (character, parts) in layers)
            WriteBinding(character, parts);
    }

    // First-pass <character>.json: layers in back-to-front order with offsets, plus a
    // rough auto-fit ImageToRig (default rig pose scaled onto the assembled canvas
    // bbox) and the default pose captured as the starting bind pose.
    private void WriteBinding(string character, List<(string Part, string Png, Point Off, Point Size)> parts)
    {
        string jsonPath = Path.Combine(_outDir, character + ".json");
        if (File.Exists(jsonPath))
        {
            Console.WriteLine($"  {character}.json exists — NOT overwriting (delete it to regenerate).");
            return;
        }

        var skel = SkeletonExamples.Biped();
        var pose = skel.CreatePose();
        var world = pose.ComputeWorld(Affine2.Identity);
        Vector2 rigMin = new(float.MaxValue), rigMax = new(float.MinValue);
        foreach (var wtf in world)
        {
            rigMin = Vector2.Min(rigMin, wtf.Translation);
            rigMax = Vector2.Max(rigMax, wtf.Translation);
        }

        // Union bbox of the placed parts on the shared (post-scale) canvas.
        Vector2 imgMin = new(float.MaxValue), imgMax = new(float.MinValue);
        foreach (var p in parts)
        {
            imgMin = Vector2.Min(imgMin, p.Off.ToVector2());
            imgMax = Vector2.Max(imgMax, p.Off.ToVector2() + p.Size.ToVector2());
        }

        float s = MathF.Max(rigMax.Y - rigMin.Y, 1f) / MathF.Max(imgMax.Y - imgMin.Y, 1f);
        Vector2 t = (rigMin + rigMax) / 2f - (imgMin + imgMax) / 2f * s;

        var doc = new SpriteBindingDocument
        {
            Skeleton = skel.Name,
            ImageToRigScale = s,
            ImageToRigTx = t.X,
            ImageToRigTy = t.Y,
            Layers = new List<SpriteSkinLayer>(),
        };
        foreach (string part in PartOrder[character])
        {
            var rec = parts.FirstOrDefault(p => p.Part == part);
            if (rec.Png == null) continue;
            doc.Layers.Add(new SpriteSkinLayer
            {
                Name = part, Image = rec.Png,
                OffsetX = rec.Off.X, OffsetY = rec.Off.Y,
                Bones = new List<string>(PartBones[part]),
                // Far limbs darken in data, not art (classic side-scroller depth cue).
                Tint = part.EndsWith("_far", StringComparison.Ordinal) ? "#C8C8C8" : null,
            });
        }
        doc.CaptureBindPose(pose);
        doc.Save(jsonPath);
        Console.WriteLine($"  wrote {jsonPath}: {doc.Layers.Count} layers, " +
                          $"canvas bbox {(int)imgMin.X},{(int)imgMin.Y}..{(int)imgMax.X},{(int)imgMax.Y}, " +
                          $"ImageToRigScale {s:F4}");
    }
}
