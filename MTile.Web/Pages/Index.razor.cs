using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.Xna.Framework;

namespace MTile.Web.Pages;

// Host page + copy/paste lobby for browser-vs-browser PvP.
//
// The lobby is a small state machine over wwwroot/mtileRtc.js (the browser twin of
// MTile.Rtc/RtcConnection.cs). It hands Game1 a NetSetup — the transport-agnostic seam the
// rollback session already speaks — so nothing in the sim knows a browser is involved:
//
//   Host : createOffer -> show blob -> paste peer's answer -> acceptAnswer -> channel open
//   Join : paste offer -> acceptOfferCreateAnswer -> show blob -> wait -> channel open
//   Solo : no NetSetup, Game1's parameterless ctor (bot opponent)
//
// The render loop (initRenderJS -> rAF -> TickDotNet) only starts once we reach Playing, so
// the game is never constructed behind the lobby overlay.
public partial class Index : IDisposable
{
    private enum Phase
    {
        Menu,
        HostGathering,
        HostShowOffer,
        JoinPasteOffer,
        JoinShowAnswer,
        Connecting,
        Playing,
        Failed,
    }

    // Matches the desktop default (MTile.Desktop/Program.cs).
    private static readonly string[] StunUrls = { "stun:stun.l.google.com:19302" };

    private const int ConnectTimeoutMs = 30_000;

    private Game _game;
    private MTile.NetSetup _net;
    private DotNetObjectReference<Index> _selfRef;

    private Phase _phase = Phase.Menu;
    private string _offerBlob = "";
    private string _answerBlob = "";
    private string _pastedOffer = "";
    private string _pastedAnswer = "";
    private string _error = "";

    private bool _ticking;
    private bool _sendBase64;   // latched if byte[] -> JS marshaling ever fails
    private int _connectEpoch;  // bumped to cancel an armed connect timeout

    // ── lobby actions ───────────────────────────────────────────────────────────

    private void StartSolo()
    {
        _net = null;
        StartTicking();
    }

    private async Task StartHost()
    {
        try
        {
            await EnsureRtcAsync();
            // Created before the channel can open so any packet that beats us just queues.
            _net = new MTile.NetSetup { LocalPlayerIndex = 0, Send = SendBytes };

            _error = "";
            _phase = Phase.HostGathering;
            StateHasChanged();

            _offerBlob = await JsRuntime.InvokeAsync<string>("mtileRtc.createOffer", (object)StunUrls);
            _phase = Phase.HostShowOffer;
        }
        catch (Exception ex)
        {
            Fail("Could not create an offer.\n" + ex);
        }
        StateHasChanged();
    }

    private async Task HostConnect()
    {
        var blob = (_pastedAnswer ?? "").Trim();
        if (blob.Length == 0)
        {
            _error = "Paste the answer blob from your opponent first.";
            return;
        }

        try
        {
            _error = "";
            _phase = Phase.Connecting;
            StateHasChanged();

            await JsRuntime.InvokeVoidAsync("mtileRtc.acceptAnswer", blob);
            ArmConnectTimeout();
        }
        catch (Exception ex)
        {
            Fail("Could not apply that answer.\n" + ex);
            StateHasChanged();
        }
    }

    private void ChooseJoin()
    {
        _error = "";
        _phase = Phase.JoinPasteOffer;
    }

    private async Task JoinCreateAnswer()
    {
        var blob = (_pastedOffer ?? "").Trim();
        if (blob.Length == 0)
        {
            _error = "Paste the offer blob from the host first.";
            return;
        }

        try
        {
            await EnsureRtcAsync();
            _net = new MTile.NetSetup { LocalPlayerIndex = 1, Send = SendBytes };

            _error = "";
            _answerBlob = await JsRuntime.InvokeAsync<string>(
                "mtileRtc.acceptOfferCreateAnswer", blob, (object)StunUrls);

            _phase = Phase.JoinShowAnswer;
            ArmConnectTimeout();
        }
        catch (Exception ex)
        {
            Fail("Could not answer that offer.\n" + ex);
        }
        StateHasChanged();
    }

    private void BackToMenu()
    {
        _net = null;
        _error = "";
        _offerBlob = _answerBlob = _pastedOffer = _pastedAnswer = "";
        _connectEpoch++;
        _phase = Phase.Menu;
        _ = JsRuntime.InvokeVoidAsync("mtileRtc.close");
    }

    private void Copy(string text) => _ = JsRuntime.InvokeVoidAsync("mtileCopy", text ?? "");

    // ── transport callbacks (from mtileRtc.js) ──────────────────────────────────

    [JSInvokable]
    public void OnRtcOpen()
    {
        _connectEpoch++;   // disarm the connect timeout
        StartTicking();
    }

    // Normal path: .NET 6+ marshals a JS Uint8Array straight into byte[].
    [JSInvokable]
    public void OnRtcMessage(byte[] bytes) => _net?.Deliver(bytes);

    // Fallback the JS side latches onto if the byte[] marshaling above ever throws.
    [JSInvokable]
    public void OnRtcMessageB64(string base64)
    {
        if (_net == null || string.IsNullOrEmpty(base64)) return;
        _net.Deliver(Convert.FromBase64String(base64));
    }

    [JSInvokable]
    public void OnRtcState(string state)
    {
        if (_phase == Phase.Playing || _phase == Phase.Failed) return;
        if (state is "failed" or "disconnected" or "closed")
        {
            Fail($"Peer connection {state}. The blobs may be stale — reload and try again.");
            StateHasChanged();
        }
    }

    // ── game loop ───────────────────────────────────────────────────────────────

    // Called every animation frame from tickJS. Returning false stops the rAF loop.
    [JSInvokable]
    public bool TickDotNet()
    {
        try
        {
            if (_game == null)
            {
                _game = _net != null ? new MTile.Game1(_net) : new MTile.Game1();
                _game.Run();
            }

            _game.Tick();
            return true;
        }
        catch (Exception ex)
        {
            _ticking = false;
            Fail(ex.ToString());
            StateHasChanged();
            return false;
        }
    }

    private void StartTicking()
    {
        if (_ticking) return;
        _ticking = true;

        _phase = Phase.Playing;
        StateHasChanged();

        _selfRef ??= DotNetObjectReference.Create(this);
        _ = JsRuntime.InvokeVoidAsync("initRenderJS", _selfRef);
    }

    // ── plumbing ────────────────────────────────────────────────────────────────

    // Per-frame hot path, so prefer synchronous in-process interop. byte[] normally arrives
    // in JS as a Uint8Array; if that marshaling is unavailable we latch to base64 (packets
    // are ~100 bytes at 60 Hz, so the encode cost is noise either way).
    private void SendBytes(byte[] bytes)
    {
        if (bytes == null) return;

        if (JsRuntime is IJSInProcessRuntime sync)
        {
            if (!_sendBase64)
            {
                try { sync.InvokeVoid("mtileRtc.send", bytes); return; }
                catch (Exception ex)
                {
                    Console.WriteLine("mtileRtc.send(byte[]) failed, falling back to base64: " + ex.Message);
                    _sendBase64 = true;
                }
            }
            sync.InvokeVoid("mtileRtc.send", Convert.ToBase64String(bytes));
        }
        else
        {
            _ = JsRuntime.InvokeVoidAsync("mtileRtc.send", Convert.ToBase64String(bytes));
        }
    }

    private async Task EnsureRtcAsync()
    {
        if (_selfRef == null)
        {
            _selfRef = DotNetObjectReference.Create(this);
            await JsRuntime.InvokeVoidAsync("mtileRtc.init", _selfRef);
        }
    }

    // Mirrors the desktop console flow's 30 s open timeout.
    private void ArmConnectTimeout()
    {
        int epoch = ++_connectEpoch;
        _ = TimeoutAsync(epoch);
    }

    private async Task TimeoutAsync(int epoch)
    {
        await Task.Delay(ConnectTimeoutMs);
        if (epoch != _connectEpoch || _phase == Phase.Playing || _phase == Phase.Failed) return;

        Fail("Timed out after 30 s waiting for the data channel to open.");
        StateHasChanged();
    }

    private void Fail(string message)
    {
        _error = message;
        _phase = Phase.Failed;
    }

    public void Dispose() => _selfRef?.Dispose();
}
