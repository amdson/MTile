using System;
using System.Runtime.InteropServices;

namespace MTile.Desktop;

// Retina (high-DPI) rendering on macOS. MonoGame DesktopGL 3.8.4.1 creates its SDL
// window without SDL_WINDOW_ALLOW_HIGHDPI, so the GL surface is allocated in POINTS
// (1470x920 on a 2560x1664 panel) and the OS stretches every frame ~1.74x. SDL's flag
// does exactly one thing on Cocoa — [contentView setWantsBestResolutionOpenGLSurface:YES]
// — and the window already exists by the time game code runs, so this does that one
// thing by hand through the Objective-C runtime and tells the GL context its drawable
// changed. Game1 then sizes the backbuffer to client size x the returned scale
// (Game1.SyncHighDpi), because MonoGame still believes the surface is in points.
//
// Lives in the desktop host, not Core: Core is re-compiled for the browser, where a
// libobjc P/Invoke has no business being. Wired in through Game1.HighDpiHook.
internal static class MacRetina
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);

    // objc_msgSend must be called through the callee's exact signature (arm64 passes
    // variadic arguments differently), hence one import per shape.
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr self, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendIndex(IntPtr self, IntPtr sel, nuint index);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void   SendBool(IntPtr self, IntPtr sel, byte value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern double SendDouble(IntPtr self, IntPtr sel);

    // Opts every window's content view into a full-resolution GL surface and returns
    // the backing scale (2 on Retina, 1 otherwise or on any failure). Idempotent; call
    // on the main thread with the GL context current — i.e. from Game.Update/Draw.
    public static float Enable()
    {
        if (!OperatingSystem.IsMacOS()) return 1f;
        try
        {
            IntPtr app = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            IntPtr windows = Send(app, sel_registerName("windows"));
            nuint count = (nuint)Send(windows, sel_registerName("count"));
            double scale = 1.0;
            for (nuint i = 0; i < count; i++)
            {
                IntPtr window = SendIndex(windows, sel_registerName("objectAtIndex:"), i);
                IntPtr view = Send(window, sel_registerName("contentView"));
                if (view == IntPtr.Zero) continue;
                SendBool(view, sel_registerName("setWantsBestResolutionOpenGLSurface:"), 1);
                scale = Math.Max(scale, SendDouble(window, sel_registerName("backingScaleFactor")));
            }
            IntPtr ctx = Send(objc_getClass("NSOpenGLContext"), sel_registerName("currentContext"));
            if (ctx != IntPtr.Zero) Send(ctx, sel_registerName("update"));
            return (float)scale;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[retina] unavailable: {e.Message}");
            return 1f;
        }
    }
}
