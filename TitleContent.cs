using System.IO;
using Microsoft.Xna.Framework;

namespace MTile;

// Cross-platform read helper. On DesktopGL this resolves next to the binary;
// on Blazor/WASM builds (the planned browser port) it resolves over HTTP from
// the title's wwwroot. Absolute paths fall back to direct file I/O so dev
// tools and tests that pass temp paths keep working.
internal static class TitleContent
{
    public static Stream TryOpenRead(string path)
    {
        if (Path.IsPathRooted(path))
            return File.Exists(path) ? File.OpenRead(path) : null;
        try { return TitleContainer.OpenStream(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
