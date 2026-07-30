using System;
using System.Collections.Generic;
using System.IO;
using MTile;
using Xunit;

namespace MTile.Tests.Animation;

// Serialization contract for SpriteBindings/<name>.json (Plans/SPRITE_SKIN_PLAN.md §10.3):
// legacy single-image + mask bindings must keep loading and re-saving without gaining
// fields, and the multi-image form (per-layer Image + OffsetX/Y, optional doc Image)
// must round-trip. Pure logic — no GraphicsDevice.
public class SpriteBindingSerializationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mtile-binding-tests").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteJson(string name, string json)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private const string LegacyMaskedJson = """
        {
          "Skeleton": "biped",
          "Image": "hero.png",
          "ImageToRigScale": 0.31,
          "ImageToRigTx": -14.2,
          "ImageToRigTy": -22.0,
          "MeshStep": 10,
          "Mask": "hero_mask.png",
          "Layers": [
            { "Name": "arm_far", "Color": "#FF0000", "Bones": ["arm_r_*"] },
            { "Name": "body", "Bones": ["*"] }
          ],
          "BindPose": [
            { "Bone": "chest", "Rotation": -1.52, "Length": 16.8 }
          ]
        }
        """;

    [Fact]
    public void LegacyMaskedBinding_LoadsAndRoundTrips()
    {
        var doc = SpriteBindingDocument.Load(WriteJson("legacy.json", LegacyMaskedJson));
        Assert.NotNull(doc);
        Assert.Equal("hero.png", doc.Image);
        Assert.Equal(2, doc.Layers.Count);
        Assert.Null(doc.Layers[0].Image);
        Assert.Equal(0, doc.Layers[0].OffsetX);

        doc.Save(Path.Combine(_dir, "legacy_resaved.json"));
        string resaved = File.ReadAllText(Path.Combine(_dir, "legacy_resaved.json"));
        // Mask-carved layers must not gain multi-image fields on re-save.
        Assert.DoesNotContain("OffsetX", resaved);
        Assert.DoesNotContain("OffsetY", resaved);
        Assert.DoesNotContain("\"Image\": null", resaved);

        var again = SpriteBindingDocument.Load(Path.Combine(_dir, "legacy_resaved.json"));
        Assert.Equal(doc.Image, again.Image);
        Assert.Equal(doc.ImageToRigScale, again.ImageToRigScale);
        Assert.Equal(doc.Mask, again.Mask);
        Assert.Equal(doc.Layers.Count, again.Layers.Count);
        Assert.Equal(doc.Layers[0].Color, again.Layers[0].Color);
        Assert.Equal(doc.BindPose[0].Length, again.BindPose[0].Length);
    }

    [Fact]
    public void MultiImageBinding_RoundTripsLayerImageAndOffsets()
    {
        var doc = new SpriteBindingDocument
        {
            Skeleton = "biped",
            ImageToRigScale = 0.25f,
            Layers = new List<SpriteSkinLayer>
            {
                new() { Name = "arm_far", Image = "rabbit/arm_far.png", OffsetX = 812, OffsetY = 340, Bones = { "arm_r_*" }, Tint = "#C8C8C8" },
                new() { Name = "torso",   Image = "rabbit/torso.png",   OffsetX = 0,   OffsetY = 122, Bones = { "chest", "hip" } },
            },
        };
        string path = Path.Combine(_dir, "multi.json");
        doc.Save(path);

        var again = SpriteBindingDocument.Load(path);
        Assert.NotNull(again);
        Assert.Null(again.Image);
        Assert.Equal("rabbit/arm_far.png", again.Layers[0].Image);
        Assert.Equal(812, again.Layers[0].OffsetX);
        Assert.Equal(340, again.Layers[0].OffsetY);
        Assert.Equal("#C8C8C8", again.Layers[0].Tint);
        Assert.Null(again.Layers[1].Tint);
        Assert.Equal(0, again.Layers[1].OffsetX);
        Assert.Equal(122, again.Layers[1].OffsetY);
    }

    [Fact]
    public void LayerImagePath_ResolvesSiblingRelative()
    {
        var doc = SpriteBindingDocument.Load(WriteJson("paths.json", """
            { "Layers": [ { "Name": "a", "Image": "rabbit/head.png" } ],
              "BindPose": [] }
            """));
        Assert.NotNull(doc);
        Assert.Null(doc.ImagePath);
        Assert.Equal(Path.Combine(_dir, "rabbit/head.png"), doc.LayerImagePath(doc.Layers[0]));
    }

    [Fact]
    public void MissingPixelSource_FailsToLoad()
    {
        // No doc image and one layer without its own → every-layer-covered rule broken.
        Assert.Null(SpriteBindingDocument.Load(WriteJson("bad1.json", """
            { "Layers": [ { "Name": "a", "Image": "a.png" }, { "Name": "b" } ] }
            """)));
        // No image anywhere.
        Assert.Null(SpriteBindingDocument.Load(WriteJson("bad2.json", "{ }")));
    }
}
