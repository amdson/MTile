using System;
using System.IO;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace MTile;

// Screenshot capture (desktop dev tool). F12 grabs a timestamped PNG next to the
// binary. If the MTILE_SCREENSHOT env var is set, auto-capture to that path after
// AutoShotFrame frames have rendered, then signal exit — lets a headless run produce
// a frame for review. Captures through a RenderTarget so it's immune to window focus.
// Render-only; threaded through Game1's Initialize/Update/Draw lifecycle.
public sealed class ScreenshotSystem
{
    private const int AutoShotFrame = 20;

    private string _autoShotPath;
    private int    _frameCount;
    private bool   _shotPending;
    private bool   _exitAfterShot;
    private Keys   _prevShotKey;   // F12 edge-detect

    // Auto-screenshot for headless review: MTILE_SCREENSHOT=path captures one frame
    // then exits. Desktop only (browser has no filesystem).
    public void Initialize()
    {
        if (OperatingSystem.IsBrowser()) return;
        _autoShotPath = Environment.GetEnvironmentVariable("MTILE_SCREENSHOT");
        if (!string.IsNullOrEmpty(_autoShotPath)) { _shotPending = true; _exitAfterShot = true; }
    }

    // F12: manual screenshot to a timestamped PNG next to the binary (desktop only).
    public void Update(KeyboardState keyboardState)
    {
        if (OperatingSystem.IsBrowser()) return;
        bool f12 = keyboardState.IsKeyDown(Keys.F12);
        if (f12 && _prevShotKey != Keys.F12)
        {
            _autoShotPath = Path.Combine(AppContext.BaseDirectory,
                $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png");
            _shotPending = true;
        }
        _prevShotKey = f12 ? Keys.F12 : Keys.None;
    }

    // Should this frame be captured? Manual (F12) fires immediately; auto (env var)
    // waits a few frames so the world settles. Returns an offscreen target bound for
    // rendering, or null when no capture is pending. Call at the top of Draw.
    public RenderTarget2D BeginCapture(GraphicsDevice graphicsDevice)
    {
        _frameCount++;
        bool capturing = _shotPending && !OperatingSystem.IsBrowser()
                         && (!_exitAfterShot || _frameCount >= AutoShotFrame);
        if (!capturing) return null;

        var pp = graphicsDevice.PresentationParameters;
        // PreserveContents: the glow pass rebinds this target mid-frame, so it must
        // not discard the scene drawn before the glow.
        var shotTarget = new RenderTarget2D(graphicsDevice, pp.BackBufferWidth, pp.BackBufferHeight,
            false, pp.BackBufferFormat, DepthFormat.None, 0, RenderTargetUsage.PreserveContents);
        graphicsDevice.SetRenderTarget(shotTarget);
        return shotTarget;
    }

    // Blit the captured target to the backbuffer, save it as a PNG, and dispose.
    // Returns true if the caller should exit (headless auto-capture mode).
    public bool EndCapture(RenderTarget2D shotTarget, SpriteBatch spriteBatch, GraphicsDevice graphicsDevice)
    {
        graphicsDevice.SetRenderTarget(null);
        graphicsDevice.Clear(Microsoft.Xna.Framework.Color.Black);
        spriteBatch.Begin();
        spriteBatch.Draw(shotTarget, graphicsDevice.Viewport.Bounds, Microsoft.Xna.Framework.Color.White);
        spriteBatch.End();
        try
        {
            using var fs = File.Create(_autoShotPath);
            shotTarget.SaveAsPng(fs, shotTarget.Width, shotTarget.Height);
        }
        catch { /* dev tool — never crash the game over a failed save */ }
        shotTarget.Dispose();
        _shotPending = false;
        return _exitAfterShot;
    }
}
