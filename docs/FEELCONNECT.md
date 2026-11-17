# FeelConnect integration

The client pairs with a FeelConnect QR token or connection code, receives the player's control messages and maps their intensity to selected local vibration motors. It does not inspect the video, use its audio, or require a HapticBridge account.

## Supported messages

| Message | Interpretation |
| --- | --- |
| `website:device_speed` | Immediate strength from 0 to 100 percent |
| Version 2 percent updates | Latest percent value; the array does not identify separate hardware motors |
| Version 3 play subtitles | Timed cue levels 0–4, converted to 0–100 percent |
| Version 3 stop or replacement playback | Stops or replaces the current timeline, including seeking |
| `website:tip` | Finite constant or pattern output, with bounded duration and optional message-ID deduplication |
| Publisher leave or timeout | Stops the publisher's active output |
| `rooms_changed` | Refreshes authorized subscriptions |

The decoder bounds message sizes and cue counts and ignores messages from unauthorized rooms. A new timeline replaces the preceding timeline; it does not append a stale seek position. Immediate outputs expire if the source disappears without sending a stop. Local motor control remains limited to the hardware's actual integer levels, so a percentage is mapped to a device level rather than creating additional physical resolution.

## Pairing and transport

The client exchanges a pairing value with `api.feel-app.com`, obtains authorized rooms and uses a WebSocket connection to `fec.feelme.com`. Pairing accepts the recognized `feelapp` and `feelvr` authorization/connect schemes or a raw pairing value. These links do not select arbitrary network destinations. HTTP redirects are disabled, responses are bounded, and session credentials are kept in memory.

The client reports online/offline presence and device display names to the service. It does not store the pairing token, media, a browser history or video screenshots. See **Privacy** for local diagnostic files and source-specific data flows.

## Compatibility limits

Player versions and services can change their messages. A successful connection proves that pairing and transport work, not that the current video provides any interactive cues. Watch the connection and received-message indicators, then check that selected motors are connected. A session that disconnects or expires requires deliberate reconnecting; output does not automatically resume.

The source is an independent protocol implementation. It contains no redistributed mobile application or decompiled application code. Offline tests cover message decoding and stopping behavior; live service and device compatibility need separate verification.
