# Local HTTP API

The API is for native applications and game mods running on the same computer. It listens on `http://127.0.0.1:17896/api/v1`. There is no network-wide listener or browser-page access.

## Connect

In **Integrations**, enable **Allow app and game controls**. Use **Copy connection** to obtain the local connection JSON, or read `%LOCALAPPDATA%\HapticBridge\integration.json`. Keep this file private. It contains a randomly generated token, not an account login.

Every request, including status queries and stop, requires:

```http
Authorization: Bearer <local token>
```

POST requests also need `Content-Type: application/json` and a JSON object body. Use HTTP/1.1 with `Content-Length`; chunked bodies are not supported. The server rejects `Origin` headers and unexpected Host headers. Do not expose the token to a web page, embed it in a mod, or publish it in a repository.

## Discover devices

| Request | Response |
|---|---|
| `GET /status` | API/app versions, whether integrations are enabled, test status, current session, devices, and video diagnostics |
| `GET /devices` | `devices` array |
| `GET /capabilities` | Control range, pattern names, and API routes |

Device objects include `id`, `name`, `connected`, `firmware`, `battery`, and `motors`. Output objects in `motors` include `channel`, `function`, `label`, `nativeMax`, `min`, `max`, `step`, `acknowledgedLevel`, and `active`. Device `functions` flags advertise `depth`, `position`, `stroke`, and `reverse`. Discover IDs and channel numbers at runtime; never assume a particular model or connection order. An acknowledged level is the last accepted command, not measured motor speed. A level of `-1` means output is unconfirmed.

The status response's `video` object reports `phase`, `connected`, `playbackMode` (`timed` or `live`), `videoRooms`, `fecMessages`, `legacyMessages`, `candidateCues`, `acceptedCues`, `unrecognizedMessages`, `lastCueKind`, and `lastCueAt`. Its `transport` object contains aggregate received/filtered message counts for troubleshooting. `videoOutputEnabled` separately reports whether the user has enabled video output; `enabled` refers to app/game integrations. These are session counters and decoding status, not proof of physical output. They include no pairing credentials, payloads, or remote channel identifiers.

`videoPresence` is the last explicit **Check website** result, or `null` if no check is available for this session. It contains aggregate receiver and browser-peer counts for authorized legacy website/video channels. Reading status does not contact the remote service. Modern FEC-only clients are not represented by these legacy presence counts.

`videoReferencePresence` similarly caches the manual **Check old SDK route** occupancy result. This diagnostic does not change transport routing.

## Own a session

Only one external integration owns the motors at a time. Opening a session stops existing local playback. Manual control or Stop all can revoke the session. If the app returns 409, report that control is unavailable and wait for the user; do not continuously reclaim it.

POST /session

```json
{"name":"Example game","ttlMs":3000}
```

The response contains `sessionId`, `ttlMs`, and a recommended `heartbeatMs`. A name must contain 1–80 characters. `ttlMs` defaults to 3000 and accepts 500–10000.

All following POST requests except `/stop` include `sessionId`:

| Request | Body fields | Behavior |
|---|---|---|
| `/heartbeat` | `sessionId` | Renews the session |
| `/release` | `sessionId` | Stops its owned motors and releases control |
| `/stop` | `{}` | Stops all outputs and pauses integrations; no session is required |

Valid output requests also renew the session. Keep a heartbeat running during long patterns. Expiry stops owned motors even if a requested output duration was longer. A transport failure can prevent a stop acknowledgement; check device connection state.

## Set motor levels

`POST /motor` controls one motor:

```json
{
  "sessionId":"<session ID>",
  "deviceId":"<discovered device ID>",
  "motor":0,
  "level":8,
  "durationMs":1000,
  "transitionMs":150
}
```

`POST /frame` controls several motors in one request:

```json
{
  "sessionId":"<session ID>",
  "durationMs":1000,
  "transitionMs":150,
  "outputs":[
    {"deviceId":"<first device ID>","motor":0,"level":8},
    {"deviceId":"<second device ID>","motor":1,"level":12}
  ]
}
```

The response contains `accepted`, the number of `outputs`, and `ttlMs`.

| Field | Accepted values |
|---|---|
| `level` | Required integer, normalized range 0–20; zero stops immediately |
| `motor` | Required discovered channel, currently 0–9; not every device exposes every channel |
| `durationMs` | Integer 0–600000; default 1000; zero holds until replaced, stopped, or lease expiry |
| `transitionMs` | Integer 0–10000; default 0; smooths nonzero steady changes |
| `outputs` | 1–16 entries; no duplicate device/channel pair |

The entire frame is validated before output begins. Bluetooth writes are ordered per device and are not a hardware-atomic operation. An I/O failure can leave some commands accepted and others unconfirmed. Pending motor, frame and pattern requests stay bound to the connections validated for that request. A disconnection or replacement returns 409; the old request cannot start the replacement. Earlier commands in a partial frame may already have applied. Refresh devices and prepare a new request after resolving the conflict; do not replay the old event. Omitted motors retain their previous job; send explicit zeros to stop them. Send current desired state at a measured rate instead of queuing obsolete game events.

## Play a pattern

`POST /pattern` accepts `sessionId`, a discovered `deviceId`, `motor`, and `pattern`:

```json
{
  "sessionId":"<session ID>",
  "deviceId":"<discovered device ID>",
  "motor":0,
  "pattern":{
    "version":1,
    "name":"Pulse",
    "mode":"Pulse",
    "low":0,
    "high":12,
    "periodMs":1000,
    "durationMs":3000,
    "dutyPercent":50,
    "phaseDegrees":0,
    "transitionMs":0,
    "repeat":true,
    "interpolate":false,
    "frames":[]
  }
}
```

Modes are `Steady`, `Pulse`, `Wave`, `Ramp`, and `Custom`. Level range is 0–20 with `low <= high`. `periodMs` is 200–600000; pattern `durationMs` is 0–3600000; `dutyPercent` is 1–99; `phaseDegrees` is 0–359. `repeat: false` makes the cycle hold its endpoint until duration or stop. Timing is best effort and depends on Bluetooth latency.

Omitted pattern fields use: `version: 1`, `name: "My pattern"`, `mode: "Pulse"`, `low: 0`, `high: 3`, `periodMs: 2000`, `durationMs: 10000`, `dutyPercent: 50`, `phaseDegrees: 0`, `transitionMs: 0`, `repeat: true`, `interpolate: false`, and an empty `frames` array. Pattern `transitionMs` accepts 0–10000.

For `Custom`, supply 2–512 ordered frames such as `[{"ms":0,"level":0},{"ms":500,"level":12},{"ms":1000,"level":0}]`. The first time must be zero, adjacent times must be at least 100 ms apart, times must fit within `periodMs`, and each level must be 0–20. Set `curve` to `step`, `linear`, or `smooth`. Smooth eases between points without exceeding their levels. When `curve` is omitted, the older `interpolate` flag still chooses linear or stepped transitions.

Optional `stopAtMs` sets a deadline on the shared pattern clock (0–3600000; default 0 means none). This lets motors joining a synchronized pattern stop at the original ending time. `durationMs` remains the duration measured from each motor's start. The earlier limit wins when both are set.

## Errors and limits

Errors use the HTTP status code and `{"error":"description"}`. Typical statuses:

| Status | Meaning |
|---|---|
| 400 | Invalid JSON, fields, channel, or pattern |
| 401 | Missing or invalid bearer token |
| 403 | Browser Origin or unexpected Host header |
| 404 | Unknown route or device |
| 405 | Method other than GET or POST |
| 409 | Integration paused, session unavailable/revoked, testing active, or device connection changed |
| 413 / 431 | Body over 64 KiB / headers over 8 KiB |
| 415 | POST content type is not JSON |
| 500 / 503 | Output failure / app closing |

Connections close after one response. The server allows up to 16 clients and applies a five-second request timeout. Use bounded client timeouts. On errors, cancel queued output and release the session when possible; the lease is the final stop fallback if the client exits.

The server deadline and server shutdown cancel requests still waiting to dispatch or start output. An admitted stop still finishes. Already accepted output keeps its requested duration and session lease. A caller timing out or disconnecting earlier does not prove its request was rejected; avoid blindly retrying an old event.

## Motion settings

`POST /function` requires the current `sessionId`, a discovered `deviceId`, and a `function`. It stops playback on that device before applying the setting. Other devices remain unchanged. Discover the device's `functions` flags first.

| `function` | Fields | Behavior |
|---|---|---|
| `reverse` | No value | Toggles rotation direction; absolute direction is not reported by hardware |
| `depth` | `value`: 0–3 | Sets Solace depth |
| `stroke` | `value`: lower endpoint; `high`: upper endpoint | Solace Pro range, 0–100, at least 20 apart; applies on the next thrusting start |
| `position` | `value`: 0–100 | Solace Pro point move; stops after the documented 300 ms movement interval |

Successful responses contain `accepted: true` and `playbackStopped: true`. Zero position moves to an endpoint. Use `/stop` to stop motion. Point moves do not automatically resume speed patterns. The existing `/motor`, `/frame` and `/pattern` endpoints also control non-vibration outputs: use their discovered channel and the common 0–20 scale. `nativeMax` reports physical command resolution; pumping maps that scale to levels 0–3.
