using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Xna.Framework;
using MTile;

namespace MTile.Tests;

public class SpriteSheetTests
{
    // Trimmed Aseprite "array" export: two frames on a 40x30 canvas, the second offset.
    private const string ArrayExport = """
    {
      "frames": [
        { "filename": "a 0", "frame": { "x": 1, "y": 1, "w": 10, "h": 12 }, "rotated": false, "trimmed": true,
          "spriteSourceSize": { "x": 5, "y": 6, "w": 10, "h": 12 }, "sourceSize": { "w": 40, "h": 30 }, "duration": 50 },
        { "filename": "a 1", "frame": { "x": 13, "y": 1, "w": 20, "h": 8 }, "rotated": false, "trimmed": true,
          "spriteSourceSize": { "x": 15, "y": 2, "w": 20, "h": 8 }, "sourceSize": { "w": 40, "h": 30 }, "duration": 150 }
      ],
      "meta": {
        "image": "a.png", "size": { "w": 34, "h": 14 }, "scale": "0.5",
        "frameTags": [ { "name": "Hit", "from": 0, "to": 1, "direction": "forward" } ],
        "slices": [
          { "name": "pivot", "keys": [ { "frame": 0, "bounds": { "x": 18, "y": 10, "w": 4, "h": 4 }, "pivot": { "x": 2, "y": 3 } } ] },
          { "name": "tip", "keys": [ { "frame": 0, "bounds": { "x": 0, "y": 0, "w": 2, "h": 2 } },
                                     { "frame": 1, "bounds": { "x": 30, "y": 4, "w": 2, "h": 2 } } ] }
        ]
      }
    }
    """;

    [Fact]
    public void ArrayExport_ReadsFramesTrimCanvasTimingScaleAndPivot()
    {
        var s = SpriteSheetData.Parse(ArrayExport);

        Assert.Equal("a.png", s.Image);
        Assert.Equal(new Point(40, 30), s.Canvas);
        Assert.Equal(0.5f, s.Scale);
        Assert.Equal(new Rectangle(13, 1, 20, 8), s.Frames[1].Source);
        Assert.Equal(new Vector2(15, 2), s.Frames[1].Offset);
        Assert.Equal(0.05f, s.Frames[0].Seconds, 5);
        Assert.Equal(0.15f, s.Frames[1].Seconds, 5);
        Assert.Equal(new Vector2(20, 13), s.PivotAt(0));   // bounds origin + slice pivot
        Assert.True(s.Tags.ContainsKey("hit"));             // tag names are case-insensitive
    }

    [Fact]
    public void SliceKeys_ApplyFromTheirFrameOnward_AndDefaultToBoundsCenter()
    {
        var s = SpriteSheetData.Parse(ArrayExport);
        Assert.True(s.TryGetPoint("tip", 0, out var p0));
        Assert.True(s.TryGetPoint("tip", 5, out var p5));
        Assert.Equal(new Vector2(1, 1), p0);
        Assert.Equal(new Vector2(31, 5), p5);
        Assert.False(s.TryGetPoint("missing", 0, out _));
    }

    [Fact]
    public void HashExport_KeepsDocumentOrder()
    {
        const string hash = """
        { "frames": {
            "z": { "frame": { "x": 0, "y": 0, "w": 4, "h": 4 }, "duration": 100 },
            "a": { "frame": { "x": 4, "y": 0, "w": 4, "h": 4 }, "duration": 100 } },
          "meta": { "image": "h.png" } }
        """;
        var s = SpriteSheetData.Parse(hash);
        Assert.Equal(0, s.Frames[0].Source.X);
        Assert.Equal(4, s.Frames[1].Source.X);
        Assert.Equal(new Vector2(2, 2), s.PivotAt(0));   // no pivot slice → canvas center
    }

    [Fact]
    public void RotatedFrames_AreRejected()
    {
        const string rotated = """
        { "frames": [ { "frame": { "x": 0, "y": 0, "w": 4, "h": 4 }, "rotated": true } ], "meta": { "image": "r.png" } }
        """;
        Assert.Throws<NotSupportedException>(() => SpriteSheetData.Parse(rotated));
    }

    [Theory]
    [InlineData("forward",          new[] { 1, 2, 3, 4 })]
    [InlineData("reverse",          new[] { 4, 3, 2, 1 })]
    [InlineData("pingpong",         new[] { 1, 2, 3, 4, 3, 2 })]
    [InlineData("pingpong_reverse", new[] { 4, 3, 2, 1, 2, 3 })]
    public void TagDirections_MatchAsepritePlayback(string direction, int[] expected)
    {
        var frames = string.Join(",", Enumerable.Range(0, 6).Select(i =>
            $$"""{ "frame": { "x": {{i * 4}}, "y": 0, "w": 4, "h": 4 } }"""));
        var json = $$"""
        { "frames": [{{frames}}], "meta": { "image": "t.png",
          "frameTags": [ { "name": "t", "from": 1, "to": 4, "direction": "{{direction}}" } ] } }
        """;
        Assert.Equal(expected, SpriteSheetData.Parse(json).Sequence("t"));
    }

    [Fact]
    public void Timing_FollowsAuthoredDurations_AndProgressKeepsTheirProportions()
    {
        var s = SpriteSheetData.Parse(ArrayExport);   // 50 ms then 150 ms
        var seq = s.Sequence(null);
        Assert.Equal(0.2f, s.Duration(seq), 5);
        Assert.Equal(0, s.FrameAt(seq, 0.049f, loop: false));
        Assert.Equal(1, s.FrameAt(seq, 0.051f, loop: false));
        Assert.Equal(1, s.FrameAt(seq, 5f, loop: false));     // clamps on the last frame
        Assert.Equal(0, s.FrameAt(seq, 0.21f, loop: true));   // wraps when looping
        Assert.Equal(0, s.FrameAtProgress(seq, 0.2f));        // first quarter is frame 0
        Assert.Equal(1, s.FrameAtProgress(seq, 0.3f));
    }

    [Fact]
    public void LegacyStrip_LoadsAsAbuttingGridWithItsPivot()
    {
        var spec = new SpriteAttachmentAsset
        {
            Image = "k.png", FrameWidth = 30, FrameHeight = 20, FrameCount = 3, PivotX = 25, PivotY = 10,
            RigUnitsPerPixel = 0.1f,
        };
        var s = SpriteSheetData.FromStrip(spec);
        Assert.True(s.AbuttingCells);
        Assert.Equal(new Rectangle(60, 0, 30, 20), s.Frames[2].Source);
        Assert.Equal(new Vector2(25, 10), s.PivotAt(2));
        Assert.True(s.FitsTexture(90, 20));
        Assert.False(s.FitsTexture(89, 20));
    }
}

// The shipped catalog resolves: every effect names a sheet that parses (either format)
// and whose frames fit inside its PNG, so a typo or a re-export with different
// dimensions fails here instead of silently dropping the effect in-game.
public class VfxCatalogTests
{
    private static string EffectsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !File.Exists(Path.Combine(d.FullName, "MTile.Core.csproj"))) d = d.Parent;
        return Path.Combine(d!.FullName, "Assets", "AnimationEffects");
    }

    [Fact]
    public void EveryCatalogEntry_ResolvesToASheetThatFitsItsImage()
    {
        string dir = EffectsDir();
        var catalog = VfxPlayer.ParseCatalog(File.ReadAllText(Path.Combine(dir, "vfx.json")));
        Assert.NotEmpty(catalog);
        Assert.Contains("body_hit", catalog.Keys);
        Assert.Contains("terrain_strike", catalog.Keys);
        Assert.Contains("parry", catalog.Keys);

        foreach (var (name, def) in catalog)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, def.Sheet + ".json")));
            var sheet = SpriteSheetData.IsSheetJson(doc.RootElement)
                ? SpriteSheetData.Parse(doc.RootElement)
                : SpriteSheetData.FromStrip(doc.RootElement.Deserialize<SpriteAttachmentAsset>()!);
            byte[] png = File.ReadAllBytes(Path.Combine(dir, sheet.Image));
            int w = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16));
            int h = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20));
            Assert.True(sheet.FitsTexture(w, h), $"{name}: frames fall outside {sheet.Image} ({w}x{h})");
            Assert.True(def.Tag == null || sheet.Tags.ContainsKey(def.Tag), $"{name}: no tag '{def.Tag}'");
            Assert.True(def.PixelSize > 0f, $"{name}: PixelSize");
            Assert.Contains(def.Align?.ToLowerInvariant() ?? "direction",
                            new[] { "direction", "againstdirection", "random", "none" });
        }
    }
}
