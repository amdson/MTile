# Firebase setup for room-code signaling

One-time, ~5 minutes, free Spark tier (no credit card). The game works without this
(solo + manual blob exchange); this enables the Host/Join room-code buttons.

1. **Create the project** — [console.firebase.google.com](https://console.firebase.google.com)
   → Add project (any name, Analytics off is fine).
2. **Register a web app** — Project overview → the `</>` (Web) icon → register (no
   hosting checkbox needed yet). Copy the `firebaseConfig = { ... }` object it shows.
3. **Paste the config** into [wwwroot/firebase-config.js](wwwroot/firebase-config.js),
   replacing the `PASTE_*` placeholders. Commit it — it's a public identifier, not a
   secret; the security rules are the access control.
4. **Create the Firestore database** — Build → Firestore Database → Create database →
   **production mode** (any location).
5. **Set the rules** — Firestore → Rules tab → replace the contents with
   [firestore.rules](firestore.rules) → Publish.
6. **TTL cleanup** *(recommended)* — Firestore → TTL → Create policy: collection
   `rooms`, field `expireAt`. Stale rooms then self-delete after ~1 h. (Without this
   everything still works; dead room docs just accumulate — they're ~4 KB each.)

Verify: reload the game page — the menu now shows **Host** and **Join** next to Solo.
Host shows a 5-char code + "Copy invite link" (`?room=CODE` deep link that pre-fills
the join screen on the other machine).

Later (plan Phase 3): the same project can serve the published game via Firebase
Hosting (`firebase init hosting` + `firebase deploy`).
