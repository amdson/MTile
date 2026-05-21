using System;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SIPSorcery.Net;

namespace MTile.Rtc;

// Pure-C# WebRTC peer link for the desktop host (GGPO_PLAN §C, SIPSorcery variant).
// Wraps one RTCPeerConnection + a single unreliable/unordered DataChannel and exposes
// the minimal surface the netcode needs: connect (offer/answer), Send(bytes), and an
// OnBytes callback. The byte payload is an InputCodec-encoded InputPacket.
//
// Signaling is non-trickle: CreateOffer/CreateAnswer wait for ICE gathering to finish,
// then return the full session description (candidates included) as a base64 blob you
// can copy/paste or push through any side channel (Firestore, a lobby server, stdin).
// STUN servers enable NAT traversal; pass none for pure LAN/loopback.
public sealed class RtcConnection : IDisposable
{
    private readonly RTCPeerConnection _pc;
    private RTCDataChannel _channel;
    private readonly TaskCompletionSource<bool> _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Raised once the data channel is open and Send/OnBytes are live.
    public event Action OnOpened;
    // Raised for every datachannel message — the raw bytes of one InputPacket.
    public event Action<byte[]> OnBytes;
    // Connection-state transitions (connected / failed / closed) for UI + teardown.
    public event Action<RTCPeerConnectionState> OnStateChanged;

    public RTCPeerConnectionState State => _pc.connectionState;
    public bool IsOpen => _channel != null && _channel.readyState == RTCDataChannelState.open;
    public Task Opened => _opened.Task;

    public RtcConnection(params string[] stunUrls)
    {
        var config = new RTCConfiguration { iceServers = new System.Collections.Generic.List<RTCIceServer>() };
        foreach (var url in stunUrls)
            config.iceServers.Add(new RTCIceServer { urls = url });

        _pc = new RTCPeerConnection(config);
        _pc.onconnectionstatechange += s => OnStateChanged?.Invoke(s);
        // The answerer receives the channel the offerer created.
        _pc.ondatachannel += dc => Bind(dc);
    }

    // ── Caller (offerer) ─────────────────────────────────────────────────────────
    // Create the data channel + offer, gather ICE, return the offer blob to hand to
    // the peer. Unreliable + unordered so a dropped packet never head-of-line-blocks
    // the newest input (the redundancy window covers loss).
    public async Task<string> CreateOfferAsync(int iceTimeoutMs = 5000)
    {
        var dc = await _pc.createDataChannel("mtile",
            new RTCDataChannelInit { ordered = false, maxRetransmits = 0 });
        Bind(dc);

        var offer = _pc.createOffer();
        await _pc.setLocalDescription(offer);
        await WaitForIceGatheringAsync(iceTimeoutMs);
        return Encode(_pc.localDescription);
    }

    // ── Callee (answerer) ─────────────────────────────────────────────────────────
    // Apply the peer's offer, create + gather an answer, return the answer blob.
    public async Task<string> CreateAnswerAsync(string offerBlob, int iceTimeoutMs = 5000)
    {
        var result = _pc.setRemoteDescription(Decode(offerBlob));
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"setRemoteDescription(offer) failed: {result}");

        var answer = _pc.createAnswer();
        await _pc.setLocalDescription(answer);
        await WaitForIceGatheringAsync(iceTimeoutMs);
        return Encode(_pc.localDescription);
    }

    // Offerer finalizes the handshake with the peer's answer.
    public void AcceptAnswer(string answerBlob)
    {
        var result = _pc.setRemoteDescription(Decode(answerBlob));
        if (result != SetDescriptionResultEnum.OK)
            throw new InvalidOperationException($"setRemoteDescription(answer) failed: {result}");
    }

    public void Send(byte[] bytes)
    {
        if (IsOpen) _channel.send(bytes);
    }

    private void Bind(RTCDataChannel dc)
    {
        _channel = dc;
        dc.onmessage += (_, _, data) => OnBytes?.Invoke(data);
        // The data channel's onopen event proved unreliable across the SCTP handshake
        // (it can fire before we bind, or not at all), but readyState always settles to
        // `open` shortly after the peer connection reaches `connected`. So mark readiness
        // off readyState — via the event when it does fire, and via a short poll otherwise.
        dc.onopen += MarkOpened;
        if (dc.readyState == RTCDataChannelState.open) MarkOpened();
        else _ = PollOpenAsync();
    }

    private async Task PollOpenAsync()
    {
        while (!_opened.Task.IsCompleted)
        {
            if (_channel != null && _channel.readyState == RTCDataChannelState.open) { MarkOpened(); return; }
            if (_pc.connectionState is RTCPeerConnectionState.failed or RTCPeerConnectionState.closed) return;
            await Task.Delay(50);
        }
    }

    private void MarkOpened()
    {
        if (_opened.TrySetResult(true)) OnOpened?.Invoke();
    }

    private async Task WaitForIceGatheringAsync(int timeoutMs)
    {
        if (_pc.iceGatheringState == RTCIceGatheringState.complete) return;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(RTCIceGatheringState s)
        {
            if (s == RTCIceGatheringState.complete) tcs.TrySetResult(true);
        }
        _pc.onicegatheringstatechange += Handler;
        try
        {
            if (_pc.iceGatheringState == RTCIceGatheringState.complete) return;
            await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
        }
        finally { _pc.onicegatheringstatechange -= Handler; }
    }

    // The (post-ICE-gathering) local description is a {type, sdp} pair; JSON it then
    // base64 so it survives copy/paste and any text channel. localDescription is an
    // RTCSessionDescription (parsed SDP) — serialize its text form.
    private static string Encode(RTCSessionDescription desc)
    {
        var json = JsonSerializer.Serialize(new Sdp { type = desc.type.ToString(), sdp = desc.sdp.ToString() });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static RTCSessionDescriptionInit Decode(string blob)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(blob));
        var sdp  = JsonSerializer.Deserialize<Sdp>(json);
        return new RTCSessionDescriptionInit
        {
            type = Enum.Parse<RTCSdpType>(sdp.type, ignoreCase: true),
            sdp  = sdp.sdp,
        };
    }

    private sealed class Sdp { public string type { get; set; } public string sdp { get; set; } }

    public void Dispose() => _pc?.Close("disposed");
}
