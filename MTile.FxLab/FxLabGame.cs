using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile.FxLab;

// A primitive sandbox for tuning ONE effect on screen. No game, no sim, no content
// pipeline — just a black window, additive vertex-color triangles, and a shock-cone
// draw you can hack. The cone is centered on screen and points at the mouse.
//
// Live controls (also printed to the console at startup):
//   Left / Right     spread half-angle
//   Up   / Down      ring count
//   Q / A            ring spacing
//   W / S            ring thickness
//   E / D            inner radius
//   R / F            cycle hue
//   [ / ]            brightness
//   Tab              cycle primitive (shock cone → arc swoosh → spline swoosh)
//   Mouse            aim direction (cone/arc) · reshapes the spline (spline swoosh)
//   Esc              quit
public sealed class FxLabGame : Game
{
    // ── KNOBS: the whole effect lives here. Tweak, rebuild, or nudge live. ──────────
    private int     _ringCount    = 4;       // how many concentric arcs
    private float   _innerRadius  = 28f;     // radius of the innermost arc (px)
    private float   _ringSpacing  = 18f;     // gap between consecutive arcs (px)
    private float   _ringThick    = 7f;      // radial thickness of one arc (px)
    private float   _halfAngle    = 0.85f;   // cone half-width (radians); spread = 2×
    private float   _angleFalloff = 1.6f;    // >1 = sharper bright center, softer edges
    private float   _ringFade     = 0.78f;   // each outer ring ×this (outer = dimmer)
    private float   _brightness   = 1.0f;    // overall intensity
    private float   _hue          = 0.55f;   // 0..1 around the color wheel
    private float   _saturation   = 0.85f;

    // Swoosh knobs (the second primitive — Tab to view). A tapering half-band:
    private float   _swoopSweep   = 1.7f;    // angular length (radians); sign curves it
    private float   _swoopThick   = 16f;     // radial thickness at the fat end (px)
    private float   _swoopTaper   = 1.1f;    // >1 = the bright fat end stays full longer

    // Spline-swoosh knobs (third primitive). Same ribbon, but the centerline is an
    // arbitrary Catmull-Rom spline instead of an arc:
    private float   _splineAmp     = 1.0f;   // S-curve bulge of the demo spline
    private float   _splineNarrow  = 0.7f;   // 0 = constant width, 1 = taper ribbon to a point
    private int     _splineSamples = 14;     // polyline samples per spline segment (smoothness)
    // ───────────────────────────────────────────────────────────────────────────────

    private readonly GraphicsDeviceManager _graphics;
    private BasicEffect _effect;
    private readonly List<VertexPositionColor> _verts = new(4096);
    private readonly List<Vector2> _splinePts = new(256);   // reused centerline buffer
    private KeyboardState _prevKb;
    private int _mode = 2;   // 0 = shock cone, 1 = arc swoosh, 2 = spline swoosh. Tab cycles.

    public FxLabGame()
    {
        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth  = 1000,
            PreferredBackBufferHeight = 700,
        };
        IsMouseVisible = true;
        Window.Title   = "MTile FxLab — shock cone";
        Window.AllowUserResizing = true;
    }

    protected override void Initialize()
    {
        _effect = new BasicEffect(GraphicsDevice)
        {
            VertexColorEnabled = true,
            TextureEnabled     = false,
            LightingEnabled    = false,
            World              = Matrix.Identity,
            View               = Matrix.Identity,
        };
        Console.WriteLine(
            "FxLab controls:  Tab = primitive,  arrows/QA/WS/ED = shape,  R/F = hue,  [ ] = brightness,  mouse = aim,  Esc = quit");
        base.Initialize();
    }

    protected override void Update(GameTime gameTime)
    {
        var kb = Keyboard.GetState();
        if (kb.IsKeyDown(Keys.Escape)) Exit();

        // Held keys ramp continuously; counts step on key-down so they don't blur past.
        float dt = (float)gameTime.ElapsedGameTime.TotalSeconds;
        if (Pressed(kb, Keys.Tab)) { _mode = (_mode + 1) % 3; Console.WriteLine("mode: " + ModeName(_mode)); }

        // Left/Right (+ W/S on the swoosh modes) drive the active primitive's main shape knob.
        switch (_mode)
        {
            case 0:   // shock cone — angular half-width
                if (kb.IsKeyDown(Keys.Left))  _halfAngle = MathF.Max(0.02f, _halfAngle - 1.2f * dt);
                if (kb.IsKeyDown(Keys.Right)) _halfAngle = MathF.Min(MathF.PI, _halfAngle + 1.2f * dt);
                break;
            case 1:   // arc swoosh — angular sweep + thickness
                if (kb.IsKeyDown(Keys.Left))  _swoopSweep -= 1.5f * dt;
                if (kb.IsKeyDown(Keys.Right)) _swoopSweep += 1.5f * dt;
                if (kb.IsKeyDown(Keys.W))     _swoopThick  = MathF.Max(1f, _swoopThick - 22f * dt);
                if (kb.IsKeyDown(Keys.S))     _swoopThick += 22f * dt;
                break;
            default:  // spline swoosh — S-curve amplitude + thickness
                if (kb.IsKeyDown(Keys.Left))  _splineAmp = MathF.Max(0f, _splineAmp - 1.5f * dt);
                if (kb.IsKeyDown(Keys.Right)) _splineAmp += 1.5f * dt;
                if (kb.IsKeyDown(Keys.W))     _swoopThick = MathF.Max(1f, _swoopThick - 22f * dt);
                if (kb.IsKeyDown(Keys.S))     _swoopThick += 22f * dt;
                break;
        }
        if (kb.IsKeyDown(Keys.Q))     _ringSpacing = MathF.Max(1f, _ringSpacing - 25f * dt);
        if (kb.IsKeyDown(Keys.A))     _ringSpacing += 25f * dt;
        if (kb.IsKeyDown(Keys.W))     _ringThick   = MathF.Max(0.5f, _ringThick - 15f * dt);
        if (kb.IsKeyDown(Keys.S))     _ringThick  += 15f * dt;
        if (kb.IsKeyDown(Keys.E))     _innerRadius = MathF.Max(0f, _innerRadius - 40f * dt);
        if (kb.IsKeyDown(Keys.D))     _innerRadius += 40f * dt;
        if (kb.IsKeyDown(Keys.R))     _hue         = (_hue + 0.4f * dt) % 1f;
        if (kb.IsKeyDown(Keys.F))     _hue         = (_hue + 1f - 0.4f * dt) % 1f;
        if (kb.IsKeyDown(Keys.OemOpenBrackets))  _brightness = MathF.Max(0.05f, _brightness - 1.2f * dt);
        if (kb.IsKeyDown(Keys.OemCloseBrackets)) _brightness += 1.2f * dt;

        if (Pressed(kb, Keys.Up))   _ringCount++;
        if (Pressed(kb, Keys.Down)) _ringCount = Math.Max(1, _ringCount - 1);

        _prevKb = kb;
        base.Update(gameTime);
    }

    private bool Pressed(KeyboardState kb, Keys k) => kb.IsKeyDown(k) && _prevKb.IsKeyUp(k);

    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(new Color(10, 10, 14));

        var center = new Vector2(GraphicsDevice.Viewport.Width * 0.5f,
                                 GraphicsDevice.Viewport.Height * 0.5f);
        var mouse  = Mouse.GetState().Position.ToVector2();
        var aim    = mouse - center;
        float dirAngle = aim.LengthSquared() > 1f ? MathF.Atan2(aim.Y, aim.X) : 0f;

        _verts.Clear();
        switch (_mode)
        {
            case 0:
                BuildShockCone(center, dirAngle);
                break;
            case 1:
                ArcSwoosh(center, dirAngle, _innerRadius, _swoopThick, _swoopSweep,
                          FromHsv(_hue, _saturation, 1f), _brightness);
                break;
            default:
                BuildSplineSwoosh(center, mouse);
                break;
        }
        FlushAdditive();

        base.Draw(gameTime);
    }

    // The effect: `_ringCount` annular arc bands at growing radii, each spanning
    // ±`_halfAngle` around `dirAngle`. All geometry is emitted into `_verts`.
    private void BuildShockCone(Vector2 center, float dirAngle)
    {
        Color hue = FromHsv(_hue, _saturation, 1f);
        for (int k = 0; k < _ringCount; k++)
        {
            float r          = _innerRadius + k * _ringSpacing;
            float ringAlpha  = _brightness * MathF.Pow(_ringFade, k);
            ArcBand(center, dirAngle, r, _ringThick, _halfAngle, hue, ringAlpha);
        }
    }

    // One glowing arc band: radius r .. r+thickness, ±half around dirAngle. Built as a
    // triangle list with TWO sources of soft falloff so it reads as light, not a solid
    // wedge:
    //   • radial — bright at the band's mid-line, alpha 0 at the inner & outer rims,
    //   • angular — bright along dirAngle, alpha 0 at the ±half edges.
    private void ArcBand(Vector2 c, float dirAngle, float r, float thick, float half,
                         Color hue, float ringAlpha)
    {
        int seg = Math.Max(8, (int)(half * 2f / 0.06f));   // ~0.06 rad per segment
        float mid = r + thick * 0.5f, outer = r + thick;
        Color rim = hue * 0f;                               // same hue, alpha 0

        // Previous angular sample's three radial points (inner/mid/outer) + mid color.
        Vector2 pIn = default, pMid = default, pOut = default;
        Color   pCol = default;
        for (int i = 0; i <= seg; i++)
        {
            float t   = i / (float)seg;                     // 0..1 across the arc
            float ang = dirAngle + MathHelper.Lerp(-half, half, t);
            var   u   = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            float aA  = MathF.Pow(MathF.Sin(t * MathF.PI), _angleFalloff); // 0 edge → 1 center
            Color col = hue * (ringAlpha * aA);

            Vector2 vIn = c + u * r, vMid = c + u * mid, vOut = c + u * outer;
            if (i > 0)
            {
                // inner half-quad: rim(in) → mid
                Quad(pIn, vIn, vMid, pMid, rim, rim, col, pCol);
                // outer half-quad: mid → rim(out)
                Quad(pMid, vMid, vOut, pOut, pCol, col, rim, rim);
            }
            pIn = vIn; pMid = vMid; pOut = vOut; pCol = col;
        }
    }

    // A tapering arc "swoosh": HALF an ArcBand. Full/bright at the start end (along
    // dirAngle) and tapering to nothing across `sweep` radians, with a ROUNDED cap on the
    // fat start end so it reads as a comet/slash head rather than a flat cut. `sweep` may
    // be negative to curve the other way. Radial falloff (bright mid-line → 0 rims) is the
    // same as ArcBand — only the angular profile differs (one-sided taper instead of a
    // symmetric bump). A natural trail primitive: fat where the blade is, thin in its wake.
    private void ArcSwoosh(Vector2 c, float dirAngle, float r, float thick, float sweep,
                           Color hue, float alpha)
    {
        int seg = Math.Max(8, (int)(MathF.Abs(sweep) / 0.05f));
        float mid = r + thick * 0.5f, outer = r + thick;
        Color rim = hue * 0f;

        Vector2 pIn = default, pMid = default, pOut = default;
        Color   pCol = default;
        for (int i = 0; i <= seg; i++)
        {
            float t   = i / (float)seg;                     // 0 = fat start, 1 = tapered tip
            float ang = dirAngle + sweep * t;
            var   u   = new Vector2(MathF.Cos(ang), MathF.Sin(ang));
            float aA  = MathF.Pow(1f - t, _swoopTaper);     // 1 at start → 0 at tip
            Color col = hue * (alpha * aA);

            Vector2 vIn = c + u * r, vMid = c + u * mid, vOut = c + u * outer;
            if (i > 0)
            {
                Quad(pIn, vIn, vMid, pMid, rim, rim, col, pCol);
                Quad(pMid, vMid, vOut, pOut, pCol, col, rim, rim);
            }
            pIn = vIn; pMid = vMid; pOut = vOut; pCol = col;
        }

        // Rounded cap on the fat start end: a soft half-disc of radius thick/2 centered on
        // the band's mid-line, bulging OUTWARD (away from the sweep). Its diameter lies on
        // the band's start cross-section (rim→mid→rim), so it seams seamlessly with the
        // band: bright center fading to alpha-0 rim, exactly matching the band's start face.
        var   radial  = new Vector2(MathF.Cos(dirAngle), MathF.Sin(dirAngle));  // inner → outer
        var   tangent = new Vector2(-radial.Y, radial.X);                       // +angle direction
        var   outward = tangent * (sweep >= 0f ? -1f : 1f);                     // away from the band
        Vector2 capC  = c + radial * mid;
        float   cr    = thick * 0.5f;
        Color   capCol = hue * alpha;
        const int cseg = 16;
        Vector2 pr = default;
        for (int i = 0; i <= cseg; i++)
        {
            float phi = MathF.PI * i / cseg;                // 0..π around the outward semicircle
            var   dir = radial * MathF.Cos(phi) + outward * MathF.Sin(phi);
            Vector2 rimP = capC + dir * cr;
            if (i > 0) Tri(capC, pr, rimP, capCol, rim, rim);
            pr = rimP;
        }
    }

    // The same tapering, rounded-cap ribbon as ArcSwoosh — but the centerline is an
    // ARBITRARY spline instead of an arc. Demo path: a Catmull-Rom S-curve from the mouse
    // (the fat, bright "blade" head) back to screen center (the faded tail). Move the mouse
    // to reshape the spline; `_splineAmp` controls how much the S bulges.
    private void BuildSplineSwoosh(Vector2 center, Vector2 mouse)
    {
        Vector2 head = mouse, tail = center;
        Vector2 d = tail - head;
        float   len = d.Length();
        Vector2 dir = len > 1f ? d / len : new Vector2(1f, 0f);
        Vector2 perp = new(-dir.Y, dir.X);
        float   amp = len * 0.25f * _splineAmp;

        // Four control points: head → two offset waypoints (the S) → tail.
        Span<Vector2> cps = stackalloc Vector2[4]
        {
            head,
            head + d * 0.33f + perp * amp,
            head + d * 0.66f - perp * amp,
            tail,
        };
        SampleCatmullRom(cps, _splineSamples, _splinePts);
        SplineRibbon(_splinePts, _swoopThick, FromHsv(_hue, _saturation, 1f), _brightness);
    }

    // Ribbon along an arbitrary centerline polyline `pts` (ordered head→tail): a glowing
    // strip centered on the path, soft across its width (bright mid-line → alpha-0 rims),
    // tapering in alpha (and optionally width, via `_splineNarrow`) toward the tail, capped
    // with a rounded half-disc at the head. This is the arc-free generalization of the
    // swoosh: feed it ANY path (a real motion Trail, a Bezier, a hand-drawn stroke).
    private void SplineRibbon(List<Vector2> pts, float thick, Color hue, float alpha)
    {
        int n = pts.Count;
        if (n < 2) return;
        Color rim = hue * 0f;

        Vector2 pIn = default, pMid = default, pOut = default;
        Color   pCol = default;
        for (int i = 0; i < n; i++)
        {
            float t = i / (float)(n - 1);                   // 0 = head, 1 = tail
            // Centerline tangent from neighbors → normal is the ribbon's width direction.
            Vector2 fwd = pts[Math.Min(i + 1, n - 1)] - pts[Math.Max(i - 1, 0)];
            if (fwd.LengthSquared() < 1e-6f) fwd = new Vector2(1f, 0f);
            fwd.Normalize();
            var nrm = new Vector2(-fwd.Y, fwd.X);

            float w   = thick * 0.5f * (1f - _splineNarrow * t);
            float aA  = MathF.Pow(1f - t, _swoopTaper);     // 1 at head → 0 at tail
            Color col = hue * (alpha * aA);
            Vector2 mid = pts[i], vIn = mid - nrm * w, vOut = mid + nrm * w;
            if (i > 0)
            {
                Quad(pIn, vIn, mid, pMid, rim, rim, col, pCol);   // rim(in) → mid
                Quad(pMid, mid, vOut, pOut, pCol, col, rim, rim); // mid → rim(out)
            }
            pIn = vIn; pMid = mid; pOut = vOut; pCol = col;
        }

        // Rounded cap at the head: half-disc of radius thick/2 on the head cross-section,
        // bulging FORWARD (away from the ribbon, along −tangent at the head).
        Vector2 h0 = pts[0];
        Vector2 fwd0 = pts[1] - pts[0];
        if (fwd0.LengthSquared() < 1e-6f) fwd0 = new Vector2(1f, 0f);
        fwd0.Normalize();
        var     nrm0 = new Vector2(-fwd0.Y, fwd0.X);   // cap diameter axis (the width line)
        var     outward = -fwd0;                       // bulge ahead of the head
        float   cr = thick * 0.5f;
        Color   capCol = hue * alpha;
        const int cseg = 16;
        Vector2 pr = default;
        for (int i = 0; i <= cseg; i++)
        {
            float phi = MathF.PI * i / cseg;            // 0..π around the forward semicircle
            var   dir = nrm0 * MathF.Cos(phi) + outward * MathF.Sin(phi);
            Vector2 rimP = h0 + dir * cr;
            if (i > 0) Tri(h0, pr, rimP, capCol, rim, rim);
            pr = rimP;
        }
    }

    // Sample a Catmull-Rom spline through `cps` into `dst` (cleared first): `perSeg` points
    // per segment, endpoints duplicated as the phantom tangents so the curve passes through
    // every control point. The spline IS the swoosh's centerline.
    private static void SampleCatmullRom(ReadOnlySpan<Vector2> cps, int perSeg, List<Vector2> dst)
    {
        dst.Clear();
        int n = cps.Length;
        for (int s = 0; s < n - 1; s++)
        {
            Vector2 p0 = cps[Math.Max(s - 1, 0)], p1 = cps[s];
            Vector2 p2 = cps[s + 1],             p3 = cps[Math.Min(s + 2, n - 1)];
            for (int k = 0; k < perSeg; k++)
                dst.Add(CatmullRom(p0, p1, p2, p3, k / (float)perSeg));
        }
        dst.Add(cps[n - 1]);
    }

    private static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2f * p1
                       + (-p0 + p2) * t
                       + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2
                       + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    private static string ModeName(int m) => m switch
    {
        0 => "shock cone", 1 => "arc swoosh", _ => "spline swoosh",
    };

    // Emit a single triangle with per-corner color.
    private void Tri(Vector2 a, Vector2 b, Vector2 cc, Color ca, Color cb, Color ccol)
    {
        _verts.Add(new VertexPositionColor(new Vector3(a, 0f), ca));
        _verts.Add(new VertexPositionColor(new Vector3(b, 0f), cb));
        _verts.Add(new VertexPositionColor(new Vector3(cc, 0f), ccol));
    }

    // Emit a quad (a,b,c,d wound consistently) as two triangles with per-corner color.
    private void Quad(Vector2 a, Vector2 b, Vector2 cc, Vector2 d,
                      Color ca, Color cb, Color ccol, Color cd)
    {
        _verts.Add(new VertexPositionColor(new Vector3(a, 0f), ca));
        _verts.Add(new VertexPositionColor(new Vector3(b, 0f), cb));
        _verts.Add(new VertexPositionColor(new Vector3(cc, 0f), ccol));
        _verts.Add(new VertexPositionColor(new Vector3(a, 0f), ca));
        _verts.Add(new VertexPositionColor(new Vector3(cc, 0f), ccol));
        _verts.Add(new VertexPositionColor(new Vector3(d, 0f), cd));
    }

    // Draw the accumulated triangles additively. Screen-space ortho (Y-down, top-left
    // origin) so the Vector2 positions above are literally pixels.
    private void FlushAdditive()
    {
        if (_verts.Count < 3) return;
        var vp = GraphicsDevice.Viewport;
        _effect.Projection = Matrix.CreateOrthographicOffCenter(0, vp.Width, vp.Height, 0, 0, 1);

        GraphicsDevice.BlendState        = BlendState.Additive;
        GraphicsDevice.DepthStencilState = DepthStencilState.None;
        GraphicsDevice.RasterizerState   = RasterizerState.CullNone;

        foreach (var pass in _effect.CurrentTechnique.Passes)
        {
            pass.Apply();
            GraphicsDevice.DrawUserPrimitives(PrimitiveType.TriangleList,
                _verts.ToArray(), 0, _verts.Count / 3);
        }
    }

    // Tiny HSV→Color so the hue knob is one float. h,s,v in 0..1.
    private static Color FromHsv(float h, float s, float v)
    {
        h = (h % 1f + 1f) % 1f * 6f;
        int   i = (int)h;
        float f = h - i, p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        (float r, float g, float b) = i switch
        {
            0 => (v, t, p), 1 => (q, v, p), 2 => (p, v, t),
            3 => (p, q, v), 4 => (t, p, v), _ => (v, p, q),
        };
        return new Color(r, g, b);
    }
}
