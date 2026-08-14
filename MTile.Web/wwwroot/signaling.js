// Firestore signaling for browser-vs-browser PvP: turns the manual blob exchange
// into a short room code (the rtcpvp pattern — the room doc id IS the code).
//
// Room doc schema (collection "rooms", enforced by MTile.Web/firestore.rules):
//   rooms/{CODE} = { offer: <blob>, createdAt: serverTimestamp, expireAt: now+1h }
//   the joiner later adds { answer: <blob> }
// The blobs are mtileRtc.js's non-trickle base64 SDP blobs, unchanged — this file
// only moves them; mtileRtc.js still owns the RTCPeerConnection.
//
// Firebase is loaded lazily via dynamic import from the gstatic CDN, so the page
// works fully offline (solo / manual mode) when the CDN or config is unavailable.
// Config lives in firebase-config.js; isConfigured() gates the lobby UI.
(function () {
    'use strict';

    var FIREBASE = 'https://www.gstatic.com/firebasejs/11.0.1/';
    var ROOM_TTL_MS = 3600 * 1000;
    var DEFAULT_WAIT_MS = 300000;

    // Unambiguous alphabet: no 0/O, 1/I/L.
    var ALPHABET = 'ABCDEFGHJKMNPQRSTUVWXYZ23456789';
    var CODE_LEN = 5;

    var fb = null;            // lazily-built { db, doc, getDoc, ... } bundle
    var pendingCancel = null; // rejects an in-flight waitForAnswer

    function normalizeCode(code) {
        return String(code || '').trim().toUpperCase();
    }

    function makeCode() {
        var out = '';
        var rand = new Uint32Array(CODE_LEN);
        crypto.getRandomValues(rand);
        for (var i = 0; i < CODE_LEN; i++) out += ALPHABET[rand[i] % ALPHABET.length];
        return out;
    }

    async function loadConfig() {
        var mod = await import('./firebase-config.js');
        return mod.firebaseConfig;
    }

    function configured(cfg) {
        return !!(cfg && cfg.apiKey && cfg.apiKey.indexOf('PASTE') !== 0);
    }

    async function ensureFirebase() {
        if (fb) return fb;
        var cfg = await loadConfig();
        if (!configured(cfg)) throw new Error('firebase-config.js is not filled in');

        var appMod = await import(FIREBASE + 'firebase-app.js');
        var fsMod = await import(FIREBASE + 'firebase-firestore.js');
        var app = appMod.initializeApp(cfg);
        fb = {
            db: fsMod.getFirestore(app),
            doc: fsMod.doc,
            getDoc: fsMod.getDoc,
            setDoc: fsMod.setDoc,
            updateDoc: fsMod.updateDoc,
            onSnapshot: fsMod.onSnapshot,
            serverTimestamp: fsMod.serverTimestamp,
            Timestamp: fsMod.Timestamp,
        };
        return fb;
    }

    window.mtileSignal = {
        isConfigured: async function () {
            try { return configured(await loadConfig()); }
            catch (e) { console.warn('mtileSignal: config check failed', e); return false; }
        },

        // Host: allocate a fresh code and publish the offer. Returns the code.
        createRoom: async function (offerBlob) {
            var f = await ensureFirebase();
            for (var attempt = 0; attempt < 5; attempt++) {
                var code = makeCode();
                var ref = f.doc(f.db, 'rooms', code);
                var snap = await f.getDoc(ref);
                if (snap.exists()) continue;   // collision — roll again
                await f.setDoc(ref, {
                    offer: String(offerBlob),
                    createdAt: f.serverTimestamp(),
                    expireAt: f.Timestamp.fromMillis(Date.now() + ROOM_TTL_MS),
                });
                return code;
            }
            throw new Error('could not allocate a room code');
        },

        // Host: resolve with the answer blob once the joiner posts it.
        // cancel() (or the timeout) rejects; either way the snapshot listener detaches.
        waitForAnswer: async function (code, timeoutMs) {
            var f = await ensureFirebase();
            var ref = f.doc(f.db, 'rooms', normalizeCode(code));
            return new Promise(function (resolve, reject) {
                var done = false, timer = null, unsub = null;
                function finish(fn, arg) {
                    if (done) return;
                    done = true;
                    if (timer !== null) clearTimeout(timer);
                    if (unsub) unsub();
                    if (pendingCancel === cancelFn) pendingCancel = null;
                    fn(arg);
                }
                function cancelFn() { finish(reject, new Error('cancelled')); }
                pendingCancel = cancelFn;

                unsub = f.onSnapshot(ref, function (snap) {
                    var d = snap.data();
                    if (d && d.answer) finish(resolve, String(d.answer));
                }, function (err) { finish(reject, err); });
                timer = setTimeout(function () {
                    finish(reject, new Error('Timed out waiting for an opponent to join.'));
                }, timeoutMs || DEFAULT_WAIT_MS);
            });
        },

        // Joiner: look up the host's offer blob by code.
        fetchOffer: async function (code) {
            var f = await ensureFirebase();
            var clean = normalizeCode(code);
            var snap = await f.getDoc(f.doc(f.db, 'rooms', clean));
            if (!snap.exists()) throw new Error('Room "' + clean + '" not found — check the code.');
            var d = snap.data();
            if (d.answer) throw new Error('Room "' + clean + '" already has a second player.');
            return String(d.offer);
        },

        // Joiner: publish the answer blob back to the room.
        postAnswer: async function (code, answerBlob) {
            var f = await ensureFirebase();
            await f.updateDoc(f.doc(f.db, 'rooms', normalizeCode(code)), { answer: String(answerBlob) });
        },

        cancel: function () {
            if (pendingCancel) pendingCancel();
        },
    };
})();
