using System;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.Xna.Framework;

namespace MTile.Web.Pages;

// Host page + lobby for browser two-player play (versus and co-op).
//
// The lobby is a small state machine over wwwroot/mtileRtc.js (the browser twin of
// MTile.Rtc/RtcConnection.cs). It hands Game1 a NetSetup — the transport-agnostic seam the
// rollback session already speaks — so nothing in the sim knows a browser is involved.
//
// Two signaling paths move the same non-trickle SDP blobs:
//   Room code (wwwroot/signaling.js, Firestore; only offered when firebase-config.js is
//   filled in):
//     Host : createOffer -> createRoom -> show code -> waitForAnswer -> acceptAnswer -> open
//     Join : enter code  -> fetchOffer -> acceptOfferCreateAnswer -> postAnswer -> open
//   Manual copy/paste fallback (no server; also what the smoke harness drives):
//     Host : createOffer -> show blob -> paste peer's answer -> acceptAnswer -> channel open
//     Join : paste offer -> acceptOfferCreateAnswer -> show blob -> wait -> channel open
//   Solo : no NetSetup, Game1's parameterless ctor
//
// Stage choice: the host (or solo player) picks from LobbyStages; Game1.StageOverride
// carries it. The host's offer blob embeds {stage, build} (mtileRtc.createOffer), so a
// joiner sees the level — and a version mismatch — before answering. Once the data channel
// opens, both sides run a resend-until-acked LobbyHello {stage, buildId} handshake
// (Phase.Handshaking) and only then StartTicking; hello/ack packets are peeled off in
// Route by their 0xFE tag so RollbackSession never sees them.
//
// The render loop (initRenderJS -> rAF -> TickDotNet) only starts once we reach Playing, so
// the game is never constructed behind the lobby overlay.
public partial class Index : IDisposable
{
    private enum Phase
    {
        Menu,
        HostGathering,
        HostShowCode,
        HostShowOffer,
        JoinEnterCode,
        JoinPasteOffer,
        JoinShowAnswer,
        Connecting,
        Handshaking,
        Playing,
        Failed,
    }

    // Matches the desktop default (MTile.Desktop/Program.cs).
    private static readonly string[] StunUrls = { "stun:stun.l.google.com:19302" };

    private const int ConnectTimeoutMs = 30_000;

    // How long a host sits on "waiting for opponent" before giving up. Generous — the
    // opponent is a human finding the code in chat, not a machine.
    private const int AnswerWaitTimeoutMs = 300_000;

    private Game _game;
    private MTile.NetSetup _net;
    private DotNetObjectReference<Index> _selfRef;

    private Phase _phase = Phase.Menu;
    private string _offerBlob = "";
    private string _answerBlob = "";
    private string _pastedOffer = "";
    private string _pastedAnswer = "";
    private string _roomCode = "";
    private string _enteredCode = "";
    private bool _signalReady;   // firebase-config.js filled in -> room-code UI offered
    private string _error = "";

    private string JoinUrl => Nav.BaseUri + "?room=" + _roomCode;

    // ── stage selection + handshake state ──
    private string _stage = LobbyStages.All[0].Name;   // host/solo choice; joiner adopts the host's
    private LobbyStage StageInfo => LobbyStages.Find(_stage);
    private bool IsCoop => StageInfo?.Coop ?? false;
    private string StageLabel => StageInfo?.Label ?? _stage;
    private string PeerNoun => IsCoop ? "partner" : "opponent";
    private bool _isHost;
    private bool _stageKnown;        // joiner: the host's stage has been read from the offer/hello
    private bool _peerHelloSeen;     // got the peer's hello (and its build matched)
    private bool _helloAcked;        // the peer acked ours
    private string _failTitle = "Connection failed";
    private const int HelloResendMs = 150;

    // Disconnect overlay (once Playing). _peerLost = failed/closed (terminal);
    // _peerInterrupted = "disconnected", which WebRTC can recover from on its own.
    private bool _peerLost;
    private bool _peerInterrupted;
    private bool _overlayDismissed;

    private bool _ticking;
    private bool _sendBase64;   // latched if byte[] -> JS marshaling ever fails
    private int _connectEpoch;  // bumped to cancel an armed connect timeout

    // Boot-freeze UX. Game construction runs synchronously inside one render tick and
    // blocks the main thread for ~10 s on the interpreted WASM runtime, so the page
    // must paint the "Loading…" overlay BEFORE that tick — _paintedOnce skips the very
    // first rAF callback (rAF fires before the frame paints; constructing there would
    // freeze the tab with the lobby still on screen). _bootDone flips the overlay off.
    private bool _paintedOnce;
    private bool _bootDone;

    // ── lifecycle ───────────────────────────────────────────────────────────────

    // ?room=CODE deep link (the host's "Copy invite link") pre-fills the join screen.
    protected override void OnInitialized()
    {
        var query = new Uri(Nav.Uri).Query;
        foreach (var part in query.TrimStart('?').Split('&'))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2 && kv[0] == "room" && kv[1].Length > 0)
            {
                _enteredCode = Uri.UnescapeDataString(kv[1]).Trim().ToUpperInvariant();
                _phase = Phase.JoinEnterCode;
            }
            // ?stage=NAME preselects a lobby stage (handy for links and the smoke harness).
            if (kv.Length == 2 && kv[0] == "stage" && LobbyStages.Find(Uri.UnescapeDataString(kv[1])) is { } pick)
                _stage = pick.Name;
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        try { _signalReady = await JsRuntime.InvokeAsync<bool>("mtileSignal.isConfigured"); }
        catch { _signalReady = false; }
        StateHasChanged();
    }

    // ── lobby actions ───────────────────────────────────────────────────────────

    private void StartSolo()
    {
        _net = null;
        // ?qpbench=1 runs the corrector-QP microbenchmark once the game loop is up and
        // prints to the console. A URL flag rather than the in-game F8 hotkey because
        // Chrome owns the function keys as browser shortcuts, so a headless driver cannot
        // deliver them to the canvas — see MTile.Web/smoke/qp_bench.js.
        if (Nav.Uri.Contains("qpbench", StringComparison.OrdinalIgnoreCase))
            QpBench.RunOnStartup = true;
        StartTicking();
    }

    // Room-code host: publish the offer to Firestore, then sit on the code until the
    // joiner posts an answer (or the wait times out / the user cancels).
    private async Task StartHost()
    {
        try
        {
            await EnsureRtcAsync();
            _net = new MTile.NetSetup { LocalPlayerIndex = 0, Send = SendBytes };
            _isHost = true;

            _error = "";
            _phase = Phase.HostGathering;
            StateHasChanged();

            _offerBlob = await JsRuntime.InvokeAsync<string>(
                "mtileRtc.createOffer", (object)StunUrls, _stage, LobbyHello.BuildId);
            _roomCode = await JsRuntime.InvokeAsync<string>("mtileSignal.createRoom", _offerBlob);
            _phase = Phase.HostShowCode;
            StateHasChanged();

            int epoch = _connectEpoch;
            var answer = await JsRuntime.InvokeAsync<string>(
                "mtileSignal.waitForAnswer", _roomCode, AnswerWaitTimeoutMs);
            if (epoch != _connectEpoch || _phase != Phase.HostShowCode) return;   // cancelled

            _phase = Phase.Connecting;
            StateHasChanged();
            await JsRuntime.InvokeVoidAsync("mtileRtc.acceptAnswer", answer);
            ArmConnectTimeout();
        }
        catch (Exception ex)
        {
            if (_phase == Phase.Menu) return;   // BackToMenu rejected the wait — expected
            Fail("Hosting failed.\n" + ex);
        }
        StateHasChanged();
    }

    private void ChooseJoinCode()
    {
        _error = "";
        _phase = Phase.JoinEnterCode;
    }

    // Room-code join: code -> offer from Firestore -> answer back to Firestore, then
    // both sides race to open the data channel. Lookup errors (bad code, full room)
    // return to the code prompt instead of the terminal Failed screen.
    private async Task JoinWithCode()
    {
        var code = (_enteredCode ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0)
        {
            _error = "Enter the room code from the host.";
            return;
        }

        try
        {
            _error = "";
            _phase = Phase.Connecting;
            StateHasChanged();

            await EnsureRtcAsync();
            _net = new MTile.NetSetup { LocalPlayerIndex = 1, Send = SendBytes };

            var offer = await JsRuntime.InvokeAsync<string>("mtileSignal.fetchOffer", code);
            if (!await ReadOfferMetaAsync(offer))
            {
                _net = null;
                if (_phase != Phase.Failed) _phase = Phase.JoinEnterCode;
                StateHasChanged();
                return;
            }
            _answerBlob = await JsRuntime.InvokeAsync<string>(
                "mtileRtc.acceptOfferCreateAnswer", offer, (object)StunUrls);
            await JsRuntime.InvokeVoidAsync("mtileSignal.postAnswer", code, _answerBlob);
            ArmConnectTimeout();
        }
        catch (Exception ex)
        {
            _net = null;
            _ = JsRuntime.InvokeVoidAsync("mtileRtc.close");
            _error = "Could not join: " + ex.Message;
            _phase = Phase.JoinEnterCode;
        }
        StateHasChanged();
    }

    // Manual copy/paste fallback (no server) — also what the smoke harness drives.
    private async Task StartHostManual()
    {
        try
        {
            await EnsureRtcAsync();
            // Created before the channel can open so any packet that beats us just queues.
            _net = new MTile.NetSetup { LocalPlayerIndex = 0, Send = SendBytes };
            _isHost = true;

            _error = "";
            _phase = Phase.HostGathering;
            StateHasChanged();

            _offerBlob = await JsRuntime.InvokeAsync<string>(
                "mtileRtc.createOffer", (object)StunUrls, _stage, LobbyHello.BuildId);
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
            _error = $"Paste the answer blob from your {PeerNoun} first.";
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

    private void ChooseJoinManual()
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
            if (!await ReadOfferMetaAsync(blob)) { _net = null; StateHasChanged(); return; }
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
        _roomCode = _enteredCode = "";
        _isHost = _stageKnown = _peerHelloSeen = _helloAcked = false;
        _connectEpoch++;
        _phase = Phase.Menu;
        _ = JsRuntime.InvokeVoidAsync("mtileSignal.cancel");   // rejects a pending waitForAnswer
        _ = JsRuntime.InvokeVoidAsync("mtileRtc.close");
    }

    private void Copy(string text) => _ = JsRuntime.InvokeVoidAsync("mtileCopy", text ?? "");

    // ── transport callbacks (from mtileRtc.js) ──────────────────────────────────

    // Channel open: don't start yet — first agree on stage + build (LobbyHello).
    [JSInvokable]
    public void OnRtcOpen()
    {
        if (_net == null || _phase is Phase.Handshaking or Phase.Playing or Phase.Failed) return;
        _phase = Phase.Handshaking;
        ArmConnectTimeout("Timed out after 30 s agreeing on match settings with the other player.");
        _ = HelloLoopAsync(_connectEpoch);
        TryFinishHandshake();
        StateHasChanged();
    }

    // Unreliable channel, so resend until the peer acks (or we leave the handshake).
    private async Task HelloLoopAsync(int epoch)
    {
        while (!_helloAcked && epoch == _connectEpoch && _phase == Phase.Handshaking)
        {
            SendBytes(LobbyHello.EncodeHello(_stage, LobbyHello.BuildId));
            await Task.Delay(HelloResendMs);
        }
    }

    // Normal path: .NET 6+ marshals a JS Uint8Array straight into byte[].
    [JSInvokable]
    public void OnRtcMessage(byte[] bytes) => Route(bytes);

    // Fallback the JS side latches onto if the byte[] marshaling above ever throws.
    [JSInvokable]
    public void OnRtcMessageB64(string base64)
    {
        if (string.IsNullOrEmpty(base64)) return;
        Route(Convert.FromBase64String(base64));
    }

    // Lobby packets (0xFE tag) stop here; everything else is an InputPacket for the session.
    private void Route(byte[] bytes)
    {
        if (_net == null || bytes == null) return;
        if (!LobbyHello.IsLobbyPacket(bytes)) { _net.Deliver(bytes); return; }

        if (LobbyHello.IsAck(bytes))
        {
            _helloAcked = true;
            TryFinishHandshake();
            return;
        }
        if (!LobbyHello.TryDecodeHello(bytes, out var stage, out var build)) return;

        // Ack every hello, even after we started: our earlier ack may have been dropped and
        // the peer is still waiting on it.
        SendBytes(LobbyHello.EncodeAck());
        if (_peerHelloSeen || _phase is Phase.Playing or Phase.Failed) return;

        if (build != LobbyHello.BuildId)
        {
            // Make sure the peer hears our build too so it shows the same message.
            for (int i = 0; i < 3; i++) SendBytes(LobbyHello.EncodeHello(_stage, LobbyHello.BuildId));
            FailVersions(build);
            StateHasChanged();
            return;
        }
        if (!_isHost)
        {
            // The host's choice is authoritative (it already matches the offer's normally).
            if (stage.Length > 0) _stage = stage;
            _stageKnown = true;
        }
        _peerHelloSeen = true;
        TryFinishHandshake();
    }

    private void TryFinishHandshake()
    {
        if (_phase != Phase.Handshaking || !_peerHelloSeen || !_helloAcked) return;
        _connectEpoch++;   // disarm the handshake timeout + stop the hello loop
        StartTicking();
    }

    [JSInvokable]
    public void OnRtcState(string state)
    {
        if (_phase == Phase.Failed) return;
        if (_phase == Phase.Playing)
        {
            // Mid-match: the rollback session would just stall silently, so say why.
            if (_net == null) return;
            if (state is "failed" or "closed") _peerLost = true;
            else if (state == "disconnected") _peerInterrupted = true;
            else if (state == "connected") _peerInterrupted = false;
            StateHasChanged();
            return;
        }
        if (state is "failed" or "disconnected" or "closed")
        {
            Fail($"Peer connection {state}. The blobs may be stale — reload and try again.");
            StateHasChanged();
        }
    }

    // Joiner: learn the host's stage + build from the offer before answering it. Returns
    // false (with _error set, or the UI switched to Failed) when we must not continue.
    private async Task<bool> ReadOfferMetaAsync(string offerBlob)
    {
        string meta;
        try { meta = await JsRuntime.InvokeAsync<string>("mtileRtc.readOfferMeta", offerBlob); }
        catch (Exception ex)
        {
            _error = "That doesn't look like a host offer: " + ex.Message;
            return false;
        }
        meta ??= "";
        int nl = meta.IndexOf('\n');
        string stage = nl < 0 ? "" : meta.Substring(0, nl);
        string build = nl < 0 ? "" : meta.Substring(nl + 1);
        if (build != LobbyHello.BuildId)
        {
            FailVersions(build.Length == 0 ? "(older build)" : build);
            return false;
        }
        if (stage.Length > 0) _stage = stage;
        _stageKnown = true;
        return true;
    }

    private void FailVersions(string theirs)
    {
        _connectEpoch++;
        _failTitle = "Different versions";
        Fail("You and the other player are running different builds of MTile, so the match "
           + "would desync. Both of you reload the page (a hard refresh, Ctrl/Cmd+Shift+R, "
           + "if it keeps happening) to get the latest version, then try again.\n\n"
           + $"yours: {LobbyHello.BuildId}   theirs: {theirs}");
    }

    private void DismissOverlay() => _overlayDismissed = true;

    // ── game loop ───────────────────────────────────────────────────────────────

    // Called every animation frame from tickJS. Returning false stops the rAF loop.
    [JSInvokable]
    public bool TickDotNet()
    {
        try
        {
            // Let the loading overlay reach the screen before the boot freeze.
            if (!_paintedOnce) { _paintedOnce = true; return true; }

            if (_game == null)
            {
                var g = _net != null ? new MTile.Game1(_net) : new MTile.Game1();
                g.StageOverride = _stage;
                _game = g;
                _game.Run();
                _bootDone = true;
                StateHasChanged();
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
    private void ArmConnectTimeout(string message = "Timed out after 30 s waiting for the data channel to open.")
    {
        int epoch = ++_connectEpoch;
        _ = TimeoutAsync(epoch, message);
    }

    private async Task TimeoutAsync(int epoch, string message)
    {
        await Task.Delay(ConnectTimeoutMs);
        if (epoch != _connectEpoch || _phase == Phase.Playing || _phase == Phase.Failed) return;

        Fail(message);
        StateHasChanged();
    }

    private void Fail(string message)
    {
        _error = message;
        _phase = Phase.Failed;
    }

    public void Dispose() => _selfRef?.Dispose();
}
