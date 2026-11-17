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
| `GET /status` | API/app versions, whether integrations are enabled, test status, current session, and devices |
| `GET /devices` | `devices` array |
| `GET /capabilities` | Native range, pattern names, and API routes |

Device objects include `id`, `name`, `connected`, `firmware`, `battery`, and `motors`. Motor objects include `channel`, `min`, `max`, `step`, `acknowledgedLevel`, and `active`. Discover IDs and channel numbers at runtime; never assume a particular model or connection order. An acknowledged level is the last accepted command, not measured motor speed. A level of `-1` means output is unconfirmed.

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
| `level` | Required integer, native range 0–20; zero stops immediately |
| `motor` | Required discovered channel, currently 0, 1, 2, or 3 |
| `durationMs` | Integer 0–600000; default 1000; zero holds until replaced, stopped, or lease expiry |
| `transitionMs` | Integer 0–10000; default 0; smooths nonzero steady changes |
| `outputs` | 1–16 entries; no duplicate device/channel pair |

The entire frame is validated before output begins. Bluetooth writes are ordered per device and are not a hardware-atomic operation. An I/O failure can leave some commands accepted and others unconfirmed. Omitted motors retain their previous job; send explicit zeros to stop them. Send current desired state at a measured rate instead of queuing obsolete game events.

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

For `Custom`, supply 2–512 ordered frames such as `[{"ms":0,"level":0},{"ms":500,"level":12},{"ms":1000,"level":0}]`. The first time must be zero, adjacent times must be at least 100 ms apart, times must fit within `periodMs`, and each level must be 0–20. `interpolate` chooses gradual or stepped transitions.

## Errors and limits

Errors use the HTTP status code and `{"error":"description"}`. Typical statuses:

| Status | Meaning |
|---|---|
| 400 | Invalid JSON, fields, channel, or pattern |
| 401 | Missing or invalid bearer token |
| 403 | Browser Origin or unexpected Host header |
| 404 | Unknown route or device |
| 405 | Method other than GET or POST |
| 409 | Integration paused, session unavailable/revoked, testing active, or device disconnected |
| 413 / 431 | Body over 64 KiB / headers over 8 KiB |
| 415 | POST content type is not JSON |
| 500 / 503 | Output failure / app closing |

Connections close after one response. The server allows up to 16 clients and applies a five-second request timeout. Use bounded client timeouts. On errors, cancel queued output and release the session when possible; the lease is the final stop fallback if the client exits.
