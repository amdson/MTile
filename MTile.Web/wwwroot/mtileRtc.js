// Browser WebRTC transport for MTile's rollback netcode.
//
// Mirrors MTile.Rtc/RtcConnection.cs on the desktop side and is wire-compatible with it:
//   • one RTCPeerConnection + one data channel named "mtile", {ordered:false, maxRetransmits:0};
//   • non-trickle signaling — ICE gathering completes BEFORE the blob is produced, so every
//     candidate is embedded in the SDP and the blob is the whole handshake;
//   • blob format = base64(UTF8(JSON {"type":"offer"|"answer","sdp":"..."})), single line.
//
// Deliberately free of DOM coupling: the four entry points are plain async functions, so a
// future signaling server can drive them programmatically instead of a copy/paste lobby.
//
// C# callbacks (on the DotNetObjectReference handed to init):
//   OnRtcOpen()                  — data channel open, safe to start the game loop
//   OnRtcMessage(byte[])         — one InputCodec packet (falls back to OnRtcMessageB64)
//   OnRtcMessageB64(string)      — base64 fallback if byte[] interop is unavailable
//   OnRtcState(string)           — RTCPeerConnection.connectionState transitions
(function () {
    'use strict';

    var DEFAULT_STUN = ['stun:stun.l.google.com:19302'];
    var ICE_TIMEOUT_MS = 5000;

    var pc = null;
    var channel = null;
    var dotnetRef = null;
    var sawFirstMessage = false;
    var inboundBase64 = false;

    // ── blob codec ───────────────────────────────────────────────────────────────
    function bytesToBase64(bytes) {
        var bin = '';
        // Chunked so a large SDP never blows the argument limit of String.fromCharCode.
        for (var i = 0; i < bytes.length; i += 0x8000)
            bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
        return btoa(bin);
    }

    function base64ToBytes(b64) {
        var bin = atob(b64);
        var bytes = new Uint8Array(bin.length);
        for (var i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
        return bytes;
    }

    function encodeBlob(desc) {
        var json = JSON.stringify({ type: desc.type, sdp: desc.sdp });
        return bytesToBase64(new TextEncoder().encode(json));
    }

    function decodeBlob(blob) {
        // Strip every whitespace char — base64 has none, and copy/paste through a textarea
        // happily introduces newlines.
        var clean = String(blob || '').replace(/\s+/g, '');
        if (clean.length === 0) throw new Error('empty signaling blob');
        var json = JSON.parse(new TextDecoder().decode(base64ToBytes(clean)));
        return { type: String(json.type || '').toLowerCase(), sdp: json.sdp };
    }

    // ── .NET callbacks ───────────────────────────────────────────────────────────
    function invokeAsync(name, arg) {
        if (!dotnetRef) return;
        try {
            var p = (arg === undefined)
                ? dotnetRef.invokeMethodAsync(name)
                : dotnetRef.invokeMethodAsync(name, arg);
            if (p && p.catch) p.catch(function (e) { console.error('mtileRtc: ' + name + ' threw', e); });
        } catch (e) {
            console.error('mtileRtc: ' + name + ' failed', e);
        }
    }

    function deliver(data) {
        if (!dotnetRef) return;
        var bytes = null;
        if (data instanceof ArrayBuffer) bytes = new Uint8Array(data);
        else if (data instanceof Uint8Array) bytes = data;
        if (bytes === null) { console.warn('mtileRtc: dropped non-binary message'); return; }

        if (!inboundBase64) {
            try {
                dotnetRef.invokeMethod('OnRtcMessage', bytes);
                sawFirstMessage = true;
                return;
            } catch (e) {
                if (sawFirstMessage) { console.error('mtileRtc: OnRtcMessage threw', e); return; }
                // First packet failed — assume byte[] marshaling isn't available under sync
                // interop on this runtime and latch to the base64 path for the whole session.
                console.warn('mtileRtc: byte[] interop failed, falling back to base64', e);
                inboundBase64 = true;
            }
        }
        try {
            dotnetRef.invokeMethod('OnRtcMessageB64', bytesToBase64(bytes));
            sawFirstMessage = true;
        } catch (e) {
            console.error('mtileRtc: OnRtcMessageB64 threw', e);
        }
    }

    // ── connection plumbing ──────────────────────────────────────────────────────
    function bindChannel(dc) {
        channel = dc;
        dc.binaryType = 'arraybuffer';
        dc.onopen = function () { invokeAsync('OnRtcOpen'); };
        dc.onclose = function () { invokeAsync('OnRtcState', 'closed'); };
        dc.onerror = function (e) { console.error('mtileRtc: data channel error', e); invokeAsync('OnRtcState', 'failed'); };
        dc.onmessage = function (ev) { deliver(ev.data); };
        // The answerer can receive an already-open channel; onopen would never fire.
        if (dc.readyState === 'open') invokeAsync('OnRtcOpen');
    }

    function makePeerConnection(stunUrls) {
        var urls = stunUrls;
        if (typeof urls === 'string') urls = [urls];
        if (!urls || !urls.length) urls = DEFAULT_STUN;

        var p = new RTCPeerConnection({ iceServers: [{ urls: urls }] });
        p.onconnectionstatechange = function () { invokeAsync('OnRtcState', p.connectionState); };
        return p;
    }

    // Non-trickle: block until every candidate is in localDescription, or give up after
    // ICE_TIMEOUT_MS and ship whatever gathered (host candidates alone work on a LAN).
    function waitForIce(p, timeoutMs) {
        return new Promise(function (resolve) {
            if (p.iceGatheringState === 'complete') { resolve(); return; }

            var done = false;
            var timer = null;
            function finish() {
                if (done) return;
                done = true;
                if (timer !== null) clearTimeout(timer);
                p.removeEventListener('icegatheringstatechange', onChange);
                p.removeEventListener('icecandidate', onCandidate);
                resolve();
            }
            function onChange() { if (p.iceGatheringState === 'complete') finish(); }
            // Some browsers signal the end of gathering only via the null candidate.
            function onCandidate(ev) { if (!ev.candidate) finish(); }

            p.addEventListener('icegatheringstatechange', onChange);
            p.addEventListener('icecandidate', onCandidate);
            timer = setTimeout(finish, timeoutMs);
        });
    }

    // ── public surface ───────────────────────────────────────────────────────────
    window.mtileRtc = {
        init: function (ref) {
            dotnetRef = ref;
        },

        // Host: build the channel first (it must exist before createOffer so the SDP
        // carries the SCTP m-line), then gather and return the offer blob.
        createOffer: async function (stunUrls) {
            pc = makePeerConnection(stunUrls);
            bindChannel(pc.createDataChannel('mtile', { ordered: false, maxRetransmits: 0 }));

            var offer = await pc.createOffer();
            await pc.setLocalDescription(offer);
            await waitForIce(pc, ICE_TIMEOUT_MS);
            return encodeBlob(pc.localDescription);
        },

        // Joiner: apply the host's offer and return the answer blob to paste back.
        acceptOfferCreateAnswer: async function (offerBlob, stunUrls) {
            var offer = decodeBlob(offerBlob);
            pc = makePeerConnection(stunUrls);
            pc.ondatachannel = function (ev) { bindChannel(ev.channel); };

            await pc.setRemoteDescription(offer);
            var answer = await pc.createAnswer();
            await pc.setLocalDescription(answer);
            await waitForIce(pc, ICE_TIMEOUT_MS);
            return encodeBlob(pc.localDescription);
        },

        // Host: finalize with the joiner's answer.
        acceptAnswer: async function (answerBlob) {
            if (!pc) throw new Error('no peer connection — createOffer first');
            await pc.setRemoteDescription(decodeBlob(answerBlob));
        },

        // Accepts a Uint8Array (the normal .NET byte[] marshaling), a plain number array,
        // an ArrayBuffer, or a base64 string — whichever the interop layer hands us.
        send: function (data) {
            if (!channel || channel.readyState !== 'open') return;
            var bytes = null;
            if (data instanceof Uint8Array) bytes = data;
            else if (data instanceof ArrayBuffer) bytes = new Uint8Array(data);
            else if (Array.isArray(data)) bytes = new Uint8Array(data);
            else if (typeof data === 'string') bytes = base64ToBytes(data);
            if (bytes === null) throw new Error('mtileRtc.send: unsupported payload type');

            try { channel.send(bytes); }
            catch (e) { console.warn('mtileRtc: send failed', e); }
        },

        isOpen: function () { return !!channel && channel.readyState === 'open'; },

        close: function () {
            try { if (channel) channel.close(); } catch (e) { /* ignore */ }
            try { if (pc) pc.close(); } catch (e) { /* ignore */ }
            channel = null;
            pc = null;
        }
    };
})();
