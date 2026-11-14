using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MTile;

// Blob shadows (todo V5): a soft dark ellipse on the ground under each character and
// entity, shrinking and fading with height. Render-only — reads sim state, writes none.
// Runs inside Game1's world-space SpriteBatch pass, AFTER the chunk tiles and BEFORE
// entities and players, so the blob darkens the ground's top edge and the body's own
// sprite covers whatever part of it rises above the ground line.
//
// The ground is seen edge-on in a side view, so this is really a contact-darkening
// strip: wide and thin. What sells "planted" is that it tracks the ground, not the body.
public sealed class BlobShadowRenderer
{
    // Opacity of the blob's centre when the body is standing on the ground.
    private const float ContactAlpha = 0.6f;
    // Shadow width at contact, as a multiple of the body's horizontal extent.
    private const float WidthScale = 1.35f;
    // Shadow thickness as a fraction of its width.
    private const float Aspect = 0.26f;
    // Thickness clamps (px): keeps a player's blob visible and a boss's from becoming a slab.
    private const float MinThickness = 5f;
    private const float MaxThickness = 9f;
    // Fraction of the thickness that sits ABOVE the ground line (the rest overlaps the tile top).
    private const float AboveGroundFraction = 0.35f;
    // Height (in tiles) above the ground at which the shadow has fully faded; also the scan cap.
    private const int MaxHeightTiles = 14;
    // Width multiplier at max height (1 at contact) — the blob tightens as the body rises.
    private const float MinWidthFactor = 0.5f;
    // Exponent on the height fade: >1 keeps the shadow strong near the ground, dropping late.
    private const float FadeExponent = 1.4f;
    // Feet may rest this far (px) inside the ground before we call the body embedded.
    private const float FeetTolerance = 2f;
    // Side ground samples sit this far (px) inside the body's edges, so a wall the body
    // is pressed against is never mistaken for ground under it.
    private const float SideSampleInset = 2f;
    // Bodies narrower than this (px) are debris/sparks — no shadow.
    private const float MinBodyWidth = 4f;
    // Gradient texture resolution (texels per side). Stretched, so it only needs to be smooth.
    private const int TextureSize = 64;

    private readonly SpriteBatch    _spriteBatch;
    private readonly Camera         _camera;
    private readonly GraphicsDevice _graphicsDevice;
    private readonly Texture2D      _blob;

    public BlobShadowRenderer(SpriteBatch spriteBatch, Camera camera, GraphicsDevice graphicsDevice)
    {
        _spriteBatch    = spriteBatch;
        _camera         = camera;
        _graphicsDevice = graphicsDevice;
        _blob           = BuildBlob(graphicsDevice);
    }

    // Radial falloff, premultiplied black: RGB stays 0 and only alpha varies, so the
    // default AlphaBlend pass darkens what is underneath without tinting it. Smoothstep
    // rather than linear so the centre is a plateau and the rim has no visible edge.
    private static Texture2D BuildBlob(GraphicsDevice device)
    {
        var data = new Color[TextureSize * TextureSize];
        float c = (TextureSize - 1) * 0.5f;
        for (int y = 0; y < TextureSize; y++)
        for (int x = 0; x < TextureSize; x++)
        {
            float dx = (x - c) / c, dy = (y - c) / c;
            float r = MathF.Sqrt(dx * dx + dy * dy);
            float f = MathF.Min(1f, MathF.Max(0f, 1f - r) * 1.6f);
            float a = f * f * (3f - 2f * f);
            data[y * TextureSize + x] = new Color(0, 0, 0, (int)(a * 255f + 0.5f));
        }
        var tex = new Texture2D(device, TextureSize, TextureSize);
        tex.SetData(data);
        return tex;
    }

    public void Draw(Simulation sim)
    {
        var viewport = _graphicsDevice.Viewport;
        float halfW = viewport.Width  / (2f * _camera.Zoom);
        float halfH = viewport.Height / (2f * _camera.Zoom);
        float viewL = _camera.Position.X - halfW, viewR = _camera.Position.X + halfW;
        float viewT = _camera.Position.Y - halfH, viewB = _camera.Position.Y + halfH;

        if (sim.Player.IsAlive) DrawFor(sim, sim.Player.Body, viewL, viewT, viewR, viewB, player: true);

        var secondaries = sim.SecondaryPlayers;
        for (int i = 0; i < secondaries.Count; i++)
        {
            var p = secondaries[i].Player;
            if (p.IsAlive) DrawFor(sim, p.Body, viewL, viewT, viewR, viewB, player: true);
        }

        var entities = sim.Entities;
        for (int i = 0; i < entities.Count; i++)
        {
            var e = entities[i];
            if (e.IsDead || !CastsShadow(e.Kind)) continue;
            DrawFor(sim, e.Body, viewL, viewT, viewR, viewB);
        }
    }

    // Things that are light, energy, or bookkeeping rather than a body with weight. A
    // shadow under a bullet reads as a second bullet; one under a beam or an invisible
    // helper reads as a bug. Lobbed/thrown ordnance (grenades, bombs, mass balls) stays
    // in: a shadow racing along the ground under an arc is exactly the depth cue we want.
    private static bool CastsShadow(EntityKind kind)
    {
        switch (kind)
        {
            case EntityKind.Bullet:
            case EntityKind.EnergyBall:
            case EntityKind.RailBolt:
            case EntityKind.AspidFireball:
            case EntityKind.WizardOrb:
            case EntityKind.StormBeam:
            case EntityKind.StormIce:
            case EntityKind.ChargedBlast:
            case EntityKind.RayEmitter:
            case EntityKind.PullPoint:
            case EntityKind.GrabThrow:
                return false;
            default:
                return true;
        }
    }

    private void DrawFor(Simulation sim, PhysicsBody body,
                         float viewL, float viewT, float viewR, float viewB,
                         bool player = false)
    {
        // Extents straight off the local vertices: PhysicsBody.Bounds goes through
        // Polygon.GetVertices, which allocates an array per call — not per frame per body.
        var poly = body.Polygon;
        int n = poly.VertexCount;
        if (n == 0) return;
        float minX = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            var v = poly.LocalVertex(i);
            if (v.X < minX) minX = v.X;
            if (v.X > maxX) maxX = v.X;
            if (v.Y > maxY) maxY = v.Y;
        }
        float bodyW = maxX - minX;
        if (bodyW < MinBodyWidth) return;
        float cx    = body.Position.X + (minX + maxX) * 0.5f;
        float feetY = body.Position.Y + maxY;
        // A player's physics polygon is only the torso: it rides StandingHeight above the
        // ground on a spring and is narrowed by BodyWidthScale, so sized from the polygon
        // the blob is a sliver that thinks the player is always airborne. Use the figure
        // the sprite actually occupies instead.
        //
        // The ground SEARCH still starts at the polygon's own bottom, though. The legs are
        // a spring: crouching, landing and running over a lip all bring the torso closer
        // to the floor than StandingHeight, so a search started at the nominal feet begins
        // BELOW a one-tile floor on those frames and finds the next floor down — the
        // shadow flickering through thin floors. The polygon never enters terrain, so the
        // first solid cell under it is always the floor the player is actually on.
        float scanY = feetY;
        if (player)
        {
            bodyW = 2f * PlayerCharacter.Radius;
            feetY = body.Position.Y + PlayerCharacter.StandingHeight;
        }
        float halfBody = bodyW * 0.5f;

        float ts        = Chunk.TileSize;
        float maxHeight = MaxHeightTiles * ts;
        float fullHalf  = bodyW * WidthScale * 0.5f;

        // Cheap reject before any tile lookups: the shadow can only land in the column
        // under the body, between its feet and the scan cap.
        if (cx + fullHalf < viewL || cx - fullHalf > viewR) return;
        if (feetY + maxHeight + MaxThickness < viewT || feetY - MaxThickness > viewB) return;

        // ── Ground search ───────────────────────────────────────────────────────────
        // Three columns (centre, and just inside each edge), highest ground wins: a body
        // whose centre overhangs a ledge still gets its shadow on the ledge it stands on.
        // Centre is tested first and ties keep it, so the clip walk below starts under
        // the body's middle whenever it can.
        var chunks  = sim.Chunks;
        int startRow = (int)MathF.Floor((scanY - FeetTolerance) / ts);
        int bestRow = int.MaxValue, bestCol = 0;
        float side = MathF.Max(0f, halfBody - SideSampleInset);
        for (int s = 0; s < 3; s++)
        {
            float sx = s == 0 ? cx : (s == 1 ? cx - side : cx + side);
            int col = (int)MathF.Floor(sx / ts);
            for (int k = 0; k <= MaxHeightTiles; k++)
            {
                int row = startRow + k;
                if (row >= bestRow) break;
                if (chunks.GetCellState(col, row) != TileState.Solid) continue;
                // Solid at the feet row itself: under the centre that is a body sunk a
                // little into the floor (still ground); at an edge it is a wall or step
                // the body is leaning on, which is not something to cast a shadow onto.
                if (k == 0 && s != 0) break;
                bestRow = row; bestCol = col;
                break;
            }
        }

        bool    haveGround = bestRow != int.MaxValue;
        float   groundY    = haveGround ? bestRow * ts : float.MaxValue;
        bool    onPlatform = false;
        float   platL = 0f, platR = 0f;

        var platforms = sim.Platforms;
        for (int i = 0; i < platforms.Count; i++)
        {
            var r = platforms[i].Rect;
            if (r.Right < cx - side || r.Left > cx + side) continue;
            float top = r.Top;
            if (top < scanY - FeetTolerance || top >= groundY) continue;
            groundY = top; haveGround = true; onPlatform = true;
            platL = r.Left; platR = r.Right;
        }
        if (!haveGround) return;

        float height = MathF.Max(0f, groundY - feetY);
        float t = height / maxHeight;
        if (t >= 1f) return;

        float alpha = ContactAlpha * MathF.Pow(1f - t, FadeExponent);
        float w     = bodyW * WidthScale * MathHelper.Lerp(1f, MinWidthFactor, t);
        float thick = MathHelper.Clamp(w * Aspect, MinThickness, MaxThickness);
        float x0 = cx - w * 0.5f, x1 = cx + w * 0.5f;
        float y0 = groundY - thick * AboveGroundFraction;
        if (y0 > viewB || y0 + thick < viewT) return;

        // ── Clip to the ground that is actually there ───────────────────────────────
        // A shadow that hangs out past a ledge into open air, or climbs the face of a
        // wall beside the body, is the giveaway that it is a sprite and not a shadow.
        // Walk outward from the hit column while the surface continues at this height
        // (solid here, open above); the walk is bounded by the shadow's own width.
        float clipL, clipR;
        if (onPlatform)
        {
            clipL = MathF.Max(x0, platL);
            clipR = MathF.Min(x1, platR);
        }
        else
        {
            int cL = bestCol, cR = bestCol;
            while (cL * ts > x0 && IsSurface(chunks, cL - 1, bestRow)) cL--;
            while ((cR + 1) * ts < x1 && IsSurface(chunks, cR + 1, bestRow)) cR++;
            clipL = MathF.Max(x0, cL * ts);
            clipR = MathF.Min(x1, (cR + 1) * ts);
        }
        if (clipR <= clipL) return;

        // Clip by trimming the SOURCE rectangle, not by squeezing the destination: the
        // gradient is cut off at the ledge instead of being re-centred on what is left.
        int srcL = (int)MathF.Round((clipL - x0) / w * TextureSize);
        int srcR = (int)MathF.Round((clipR - x0) / w * TextureSize);
        srcL = Math.Clamp(srcL, 0, TextureSize);
        srcR = Math.Clamp(srcR, 0, TextureSize);
        if (srcR <= srcL) return;

        float sxScale = w / TextureSize;
        _spriteBatch.Draw(
            _blob,
            new Vector2(x0 + srcL * sxScale, y0),
            new Rectangle(srcL, 0, srcR - srcL, TextureSize),
            Color.White * alpha,           // premultiplied: scales the texture's alpha
            0f, Vector2.Zero,
            new Vector2(sxScale, thick / TextureSize),
            SpriteEffects.None, 0f);
    }

    private static bool IsSurface(ChunkMap chunks, int col, int row)
        => chunks.GetCellState(col, row) == TileState.Solid
        && chunks.GetCellState(col, row - 1) != TileState.Solid;
}
