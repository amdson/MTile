#!/bin/sh
# Linux launcher for KNI's content builder (MGCB).
#
# The nkast.Xna.Framework.Content.Pipeline.Builder targets invoke
# `Tools\MGCB.exe` — a path that fails on Linux twice over (case-sensitive
# `tools/`, and MGCB.exe is a Windows PE launcher). The managed MGCB.dll next
# to it runs fine under `dotnet`. MTile.Web.csproj points KniContentBuilderExe
# at this script on non-Windows.
#
# The .fx effect compile additionally needs two native libraries dropped into
# the package's tools/ dir (one-time, per machine):
#   libmojoshader_64.dll.so  — prebuilt Linux mojoshader from kniEngine/kniDependencies
#   d3dcompiler_47.dll.so    — SysV->ms_abi shim over vkd3d-utils >= 1.19 (vkd3d
#                              exports its API with the Windows calling convention;
#                              .NET P/Invoke uses SysV, so a direct load scrambles
#                              every argument) plus the patched vkd3d .so set.
# See the "Linux content build" section of Plans/Archive/BROWSER_PORT_PLAN.md.

VERSION=4.1.9001   # keep in step with the nkast package pins in MTile.Web.csproj
PKG="${NUGET_PACKAGES:-$HOME/.nuget/packages}/nkast.xna.framework.content.pipeline.builder/$VERSION/tools"

if [ ! -f "$PKG/MGCB.dll" ]; then
    echo "kni-mgcb.sh: $PKG/MGCB.dll not found (dotnet restore first?)" >&2
    exit 1
fi
exec dotnet "$PKG/MGCB.dll" "$@"
