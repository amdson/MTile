using System;
using Microsoft.Xna.Framework;

namespace MTile.Net;

// Wire (de)serialization for InputPacket — the byte form sent over the datachannel
// (GGPO_PLAN §E). Transport-agnostic: the same bytes go over SIPSorcery on desktop or
// the JS WebRTC channel in the browser, so this lives in Core (pure C#, no sockets).
//
// Per-input payload is 10 bytes: a ushort of packed buttons + MouseWorldPosition as two
// floats. The raw screen MousePosition is NEVER sent — the sim is world-coords-only and
// each peer computes its own cursor world position (§E). Little-endian throughout.
//
// Packet layout:
//   [0]      Player        (byte)
//   [1..4]   FirstFrame    (int32)
//   [5..8]   ChecksumFrame (int32)
//   [9..16]  Checksum      (uint64)
//   [17]     Count         (byte)   — number of inputs in the redundancy window
//   [18..]   Count × (ushort flags, float worldX, float worldY)
public static class InputCodec
{
    public const int HeaderBytes = 18;
    public const int InputBytes  = 10;

    // ── Flag bit layout (14 of 16 bits used) ──
    private const int FLeft = 0, FRight = 1, FUp = 2, FDown = 3,
                      FLeftClick = 4, FRightClick = 5, FSpace = 6, FShift = 7,
                      FF = 8, FP = 9, FNum1 = 10, FNum2 = 11, FNum3 = 12, FNum4 = 13;

    private static ushort PackFlags(in PlayerInput p)
    {
        ushort f = 0;
        if (p.Left)       f |= 1 << FLeft;
        if (p.Right)      f |= 1 << FRight;
        if (p.Up)         f |= 1 << FUp;
        if (p.Down)       f |= 1 << FDown;
        if (p.LeftClick)  f |= 1 << FLeftClick;
        if (p.RightClick) f |= 1 << FRightClick;
        if (p.Space)      f |= 1 << FSpace;
        if (p.Shift)      f |= 1 << FShift;
        if (p.F)          f |= 1 << FF;
        if (p.P)          f |= 1 << FP;
        if (p.Num1)       f |= 1 << FNum1;
        if (p.Num2)       f |= 1 << FNum2;
        if (p.Num3)       f |= 1 << FNum3;
        if (p.Num4)       f |= 1 << FNum4;
        return f;
    }

    private static PlayerInput UnpackFlags(ushort f, Vector2 world) => new()
    {
        Left       = (f & (1 << FLeft))       != 0,
        Right      = (f & (1 << FRight))      != 0,
        Up         = (f & (1 << FUp))         != 0,
        Down       = (f & (1 << FDown))       != 0,
        LeftClick  = (f & (1 << FLeftClick))  != 0,
        RightClick = (f & (1 << FRightClick)) != 0,
        Space      = (f & (1 << FSpace))      != 0,
        Shift      = (f & (1 << FShift))      != 0,
        F          = (f & (1 << FF))          != 0,
        P          = (f & (1 << FP))          != 0,
        Num1       = (f & (1 << FNum1))       != 0,
        Num2       = (f & (1 << FNum2))       != 0,
        Num3       = (f & (1 << FNum3))       != 0,
        Num4       = (f & (1 << FNum4))       != 0,
        MouseWorldPosition = world,
        // MousePosition (screen) intentionally not transmitted — render/debug only.
    };

    public static byte[] Encode(in InputPacket packet)
    {
        int count = packet.Inputs?.Length ?? 0;
        var buf = new byte[HeaderBytes + count * InputBytes];
        int o = 0;
        buf[o++] = (byte)packet.Player;
        WriteI32(buf, ref o, packet.FirstFrame);
        WriteI32(buf, ref o, packet.ChecksumFrame);
        WriteU64(buf, ref o, packet.Checksum);
        buf[o++] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            ref readonly var pi = ref packet.Inputs[i];
            WriteU16(buf, ref o, PackFlags(in pi));
            WriteF32(buf, ref o, pi.MouseWorldPosition.X);
            WriteF32(buf, ref o, pi.MouseWorldPosition.Y);
        }
        return buf;
    }

    // Returns false on a malformed/truncated buffer rather than throwing — a corrupt
    // datagram should be dropped, not crash the receive loop.
    public static bool TryDecode(byte[] buf, out InputPacket packet)
    {
        packet = default;
        if (buf == null || buf.Length < HeaderBytes) return false;
        int o = 0;
        int player        = buf[o++];
        int firstFrame    = ReadI32(buf, ref o);
        int checksumFrame = ReadI32(buf, ref o);
        ulong checksum    = ReadU64(buf, ref o);
        int count         = buf[o++];
        if (buf.Length != HeaderBytes + count * InputBytes) return false;

        var inputs = new PlayerInput[count];
        for (int i = 0; i < count; i++)
        {
            ushort flags = ReadU16(buf, ref o);
            float x = ReadF32(buf, ref o);
            float y = ReadF32(buf, ref o);
            inputs[i] = UnpackFlags(flags, new Vector2(x, y));
        }
        packet = new InputPacket
        {
            Player        = player,
            FirstFrame    = firstFrame,
            ChecksumFrame = checksumFrame,
            Checksum      = checksum,
            Inputs        = inputs,
        };
        return true;
    }

    // ── Little-endian primitive writers/readers ──
    private static void WriteU16(byte[] b, ref int o, ushort v) { b[o++] = (byte)v; b[o++] = (byte)(v >> 8); }
    private static void WriteI32(byte[] b, ref int o, int v)    { WriteU32(b, ref o, (uint)v); }
    private static void WriteU32(byte[] b, ref int o, uint v)   { b[o++] = (byte)v; b[o++] = (byte)(v >> 8); b[o++] = (byte)(v >> 16); b[o++] = (byte)(v >> 24); }
    private static void WriteU64(byte[] b, ref int o, ulong v)  { WriteU32(b, ref o, (uint)v); WriteU32(b, ref o, (uint)(v >> 32)); }
    private static void WriteF32(byte[] b, ref int o, float v)  { WriteU32(b, ref o, (uint)BitConverter.SingleToInt32Bits(v)); }

    private static ushort ReadU16(byte[] b, ref int o) { ushort v = (ushort)(b[o] | (b[o + 1] << 8)); o += 2; return v; }
    private static int    ReadI32(byte[] b, ref int o) => (int)ReadU32(b, ref o);
    private static uint   ReadU32(byte[] b, ref int o) { uint v = (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); o += 4; return v; }
    private static ulong  ReadU64(byte[] b, ref int o) { ulong lo = ReadU32(b, ref o); ulong hi = ReadU32(b, ref o); return lo | (hi << 32); }
    private static float  ReadF32(byte[] b, ref int o) => BitConverter.Int32BitsToSingle((int)ReadU32(b, ref o));
}
