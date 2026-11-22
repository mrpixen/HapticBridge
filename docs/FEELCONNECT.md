# FeelConnect integration

The client pairs with a FeelConnect QR token or connection code, receives the player's control messages and maps their intensity to selected local vibration motors. It does not inspect the video, use its audio, or require a HapticBridge account.

## Supported messages

| Message | Interpretation |
| --- | --- |
| `website:device_speed` | Immediate strength from 0 to 100 percent |
| `device:position` with `device_percent` | Current SDK strength from 0 to 100 percent, checked against the paired target user when specified |
| Version 2 percent updates | Ordered percentage samples, sent to each selected motor |
| Version 3 play subtitles | Timed cue levels 0–4, converted to 0–100 percent |
| Version 3 stop or replacement playback | Stops or replaces the current timeline, including seeking |
| `room:play` / `room:stop` | Current SDK envelopes for version 3 playback and stopping |
| `website:tip` | Finite constant or pattern output, with bounded duration and optional message-ID deduplication |
| Publisher leave or timeout | Stops the publisher's active output |
| `rooms_changed` | Refreshes authorized subscriptions |

The decoder bounds message sizes and cue counts and ignores messages from unauthorized rooms. A new timeline replaces the preceding timeline; it does not append a stale seek position. Sparse timed batches retain their last level while awaiting the next batch. Outputs expire if the source disappears without sending a stop. Local motor control remains limited to the hardware's actual integer levels, so a percentage is mapped to a device level rather than creating additional physical resolution.

## Pairing and transport

The client exchanges a pairing value with `api.feel-app.com`, obtains authorized rooms and connects to `fec.feelme.com` over WebSocket and `ps.pndsn.com` over HTTPS. Authorized video rooms accept cues on either transport. Matching version 3 batches are deduplicated when their producer, room, timestamp and cue data match; stop commands always pass. Website tip routing follows its separate service migration flag. Legacy presence uses a random `mobile-client` session identifier and advertises the selected playback mode. Subscription changes preserve the exact message cursor, and stale legacy messages are rejected.

After an authorized video-room join or peer join, the client sends session metadata through a separate bounded worker. A slow metadata request cannot block cue decoding or output stops.

If initial pairing finds no video room, the client checks again after its subscriptions are ready. This recovers a room created during pairing even if its change notification was missed. It does not discover channels outside the authenticated session.

Guest tokens with valid issue and expiry times renew around the midpoint of their lifetime. Renewal replaces the FEC connection while preserving website presence and the legacy subscription. Output clears during the swap and waits for fresh cues. The previous expiry remains binding until the new connection authenticates and its room joins succeed. Failed exchanges have at most three attempts, at least 30 seconds apart; rejection or expiry disconnects and pauses output.

Pairing accepts the recognized `feelapp` and `feelvr` authorization/connect schemes or a raw pairing value. These links do not select arbitrary network destinations. HTTP redirects are disabled, responses are bounded, and session credentials are kept in memory. PubNub subscriptions are restricted to channels supplied by the QR-authenticated API session and their presence channels. The client uses the public subscribe routing identifier only; it has no publish, partner or secret key and performs no channel discovery or history lookup.

The client reports online/offline presence and refreshes device display names when Bluetooth connections change. It shows the paired website's name from the service. It does not store the pairing token, website name, media, a browser history or video screenshots. See **Privacy** for local diagnostic files and source-specific data flows.

## Compatibility limits

The optional **Settings → Browser helper → Save helper…** exports a small browser extension for pages whose interactive integration reads the wrong video clock. Extract the ZIP and follow its README. Edge supports loading the extracted folder through its extension developer page; Firefox's temporary development installation ends when Firefox restarts. Normal Firefox distribution requires signing. Saving the helper does not install it or change browser settings. Enable it deliberately on the relevant tab; it does not repair failed service pairing or guarantee live playback.

**Playback settings → Playback mode** offers **Timed cues** (default, version 3) and **Live levels (legacy)** (version 2). Live levels avoids the legacy sender's clock lookup and timed batching. **Apply mode** stops output and reconnects with a fresh receiver identity using the current pairing held in memory. Select **Enable output** afterward. A rejected or expired pairing needs a fresh QR code.

Live levels still requires the website to send playback updates. It does not fix a stalled video clock. Network delay affects each update, and a gap longer than five seconds stops output, including a long hold in a sparse track. Timed cues is preferable when the player supports it. Neither mode changes the authorized channel scope or the available motor intensity range.

Version 2 batches have no per-sample timestamps. Each fully validated batch is submitted in order as the device acknowledges writes; no timing is invented between samples. These batches bypass smoothing so intermediate peaks are not collapsed into their final value. Bluetooth timing determines whether a very short peak is perceptible.

Each device has a maximum of 1,000 pending samples. Overload discards the oldest pending batch remainders; samples waiting more than one second expire. Expired remainders trigger a zero before newer work continues, and an otherwise idle output expires after five seconds. Stop, disarm, disconnect and control takeover cancel pending output. Batches received while output is off or pairing is still completing are not replayed when output becomes ready.

Connection details count fully validated live batches, their positive samples and mixed zero/nonzero batches, with the last batch's size, maximum and final value. These aggregate numbers describe incoming traffic without recording message contents or session identifiers.

Player versions and services can change their messages. **Paired** confirms the service connection. The intensity status distinguishes an active vibration cue, zero intensity and expired updates. If output stays at zero, expand **Connection details** to check for a video channel and incoming cues, then check that the selected motors are connected. A session that disconnects or expires requires deliberate reconnecting; output does not automatically resume.

Unexpected legacy disconnects retain their cause: timeout, service rejection, unsupported response or connection loss. These messages contain no request URLs, tokens or response contents.

**Check website** reads presence on this session's authorized legacy channels and reports receiver/browser counts. It does not subscribe to other channels or expose peer identifiers. Modern FEC-only clients do not appear in this legacy check.

**Check old SDK route** checks occupancy for the same channels on the routing namespace exported by the published legacy SDK. This is a manual diagnostic; it does not change the receiver's route or start playback.

The source is an independent protocol implementation. It contains no redistributed mobile application or decompiled application code. Offline tests cover message decoding and stopping behavior; live service and device compatibility need separate verification.

## Technical references

Protocol behavior is checked against FeelTechnology's [integration and migration guide](https://developer.feeltechnology.com/docs/feeljs/feel_apps_api_docs.html), its [public legacy SDK](https://api.feel-app.com/static/feeljs/1.2.1/feel.min.js), the [current SDK source](https://github.com/FeelRobotics/feel-apps-api), and PubNub's [presence lifecycle](https://www.pubnub.com/docs/sdks/rest-api/presence-introduction). Legacy PubNub presence and current FEC connectivity are separate compatibility requirements.
