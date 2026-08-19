#!/bin/sh
# Unix launcher for KNI's content builder (MGCB).
#
# The nkast.Xna.Framework.Content.Pipeline.Builder targets invoke
# `Tools\MGCB.exe` — a path that fails off Windows twice over (case-sensitive
# `tools/`, and MGCB.exe is a Windows PE launcher). The managed MGCB.dll next
# to it runs fine under `dotnet`. MTile.Web.csproj points KniContentBuilderExe
# at this script on non-Windows.
#
# KNI ships only Windows natives in that tools/ dir (ffmpeg.exe, freetype6.dll,
# libmojoshader_64.dll), so the font and sound processors need Unix equivalents
# supplied alongside. macOS is handled automatically below by borrowing them from
# MonoGame's own mgcb tool, which is already pinned in .config/dotnet-tools.json
# and does ship them. Linux still needs the manual one-time drop described under
# "Linux native libraries" further down.
#
# Effects (.fx) are a different story and are NOT solved here: compiling them
# needs d3dcompiler_47.dll, which has no macOS build at all. MTile.Web.csproj
# therefore selects the shader-free Content.Mac.mgcb on macOS, the same set the
# Mac desktop build uses.

VERSION=4.1.9001   # keep in step with the nkast package pins in MTile.Web.csproj
NUGET="${NUGET_PACKAGES:-$HOME/.nuget/packages}"
PKG="$NUGET/nkast.xna.framework.content.pipeline.builder/$VERSION/tools"

if [ ! -f "$PKG/MGCB.dll" ]; then
    echo "kni-mgcb.sh: $PKG/MGCB.dll not found (dotnet restore first?)" >&2
    exit 1
fi

if [ "$(uname -s)" = "Darwin" ]; then
    # MonoGame's mgcb tool (version-pinned in .config/dotnet-tools.json; run
    # `dotnet tool restore` if this is missing) carries the macOS natives KNI's
    # package lacks.
    MG_VERSION=3.8.4.1
    MG="$NUGET/dotnet-mgcb/$MG_VERSION/tools/net8.0/any"
    if [ ! -d "$MG" ]; then
        echo "kni-mgcb.sh: $MG not found — run 'dotnet tool restore'" >&2
        exit 1
    fi

    # The audio processor shells out to ffmpeg/ffprobe by bare name, so PATH is
    # the whole fix for sounds.
    PATH="$MG/osx:$PATH"
    export PATH

    # The font processor P/Invokes DllImport("freetype6"). .NET probes the
    # MGCB.dll app directory for `libfreetype6.dylib` among other manglings, so a
    # symlink under that name is enough. (The SharpFont.dll.config dllmap next to
    # it is a Mono feature and is ignored on .NET 8, so it can't do this for us.)
    # Recreated every run: this lives in the NuGet cache, which any `nuget locals
    # --clear` wipes, and a stale symlink is worse than a missing one.
    if [ -w "$PKG" ]; then
        ln -sf "$MG/runtimes/osx/native/libfreetype.dylib" "$PKG/libfreetype6.dylib"
    elif [ ! -e "$PKG/libfreetype6.dylib" ]; then
        echo "kni-mgcb.sh: $PKG is not writable and has no libfreetype6.dylib;" >&2
        echo "  the .spritefont build will fail. Symlink it there by hand." >&2
    fi
fi

# Linux native libraries — still a manual one-time drop into the tools/ dir:
#   libmojoshader_64.dll.so  — prebuilt Linux mojoshader from kniEngine/kniDependencies
#   d3dcompiler_47.dll.so    — SysV->ms_abi shim over vkd3d-utils >= 1.19 (vkd3d
#                              exports its API with the Windows calling convention;
#                              .NET P/Invoke uses SysV, so a direct load scrambles
#                              every argument) plus the patched vkd3d .so set.
# See the "Linux content build" section of Plans/Archive/BROWSER_PORT_PLAN.md.

exec dotnet "$PKG/MGCB.dll" "$@"
