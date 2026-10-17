using System;
using System.Text;

namespace MTile;

// Pre-match settings handshake for the browser lobby (MTile.Web/Pages/Index.razor.cs).
//
// It rides the same unreliable data channel as the rollback InputPackets, so it needs a
// leading byte no InputPacket can start with: InputCodec writes the player index (0 or 1)
// first, so Tag = 0xFE is unambiguous. The lobby peels these off in OnRtcMessage before
// NetSetup.Deliver, so RollbackSession / Game1 never see one. Not a sim concern — nothing
// here touches Simulation; it only decides which stage Game1 is built with.
//
//   hello : [Tag, 'H', utf8("<stage>\n<buildId>")]   resent until acked
//   ack   : [Tag, 'A']                               reply to every hello received
public static class LobbyHello
{
    public const byte Tag      = 0xFE;
    public const byte KindHello = (byte)'H';
    public const byte KindAck   = (byte)'A';

    public static bool IsLobbyPacket(byte[] bytes) => bytes != null && bytes.Length >= 2 && bytes[0] == Tag;

    public static byte[] EncodeHello(string stage, string buildId)
    {
        var body = Encoding.UTF8.GetBytes((stage ?? "") + "\n" + (buildId ?? ""));
        var buf = new byte[2 + body.Length];
        buf[0] = Tag;
        buf[1] = KindHello;
        Buffer.BlockCopy(body, 0, buf, 2, body.Length);
        return buf;
    }

    public static byte[] EncodeAck() => new[] { Tag, KindAck };

    public static bool IsAck(byte[] bytes) => IsLobbyPacket(bytes) && bytes[1] == KindAck;

    public static bool TryDecodeHello(byte[] bytes, out string stage, out string buildId)
    {
        stage = buildId = null;
        if (!IsLobbyPacket(bytes) || bytes[1] != KindHello) return false;
        string text;
        try { text = Encoding.UTF8.GetString(bytes, 2, bytes.Length - 2); }
        catch (ArgumentException) { return false; }
        int nl = text.IndexOf('\n');
        if (nl < 0) return false;
        stage   = text.Substring(0, nl);
        buildId = text.Substring(nl + 1);
        return true;
    }

    // Both peers compute this from the build they're running: the module version id
    // (MVID) of the assembly the game code is compiled into. The SDK builds
    // deterministically by default, so the MVID is a content hash of that compilation —
    // the same deployed .dll gives the same id on every machine, and any source change
    // gives a new one. Under the web host that assembly is MTile.Web.dll (the root .cs
    // is recompiled into it), so any change to the web build counts as a new version,
    // which is the conservative side to err on for a float-determinism-sensitive netcode.
    public static string BuildId
    {
        get
        {
            try { return typeof(LobbyHello).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12); }
            catch (Exception) { return "unknown"; }
        }
    }
}
