# AudioBit Remote Control Protocol (WebSocket JSON)

## 1) Scope

This document defines the realtime protocol between:

- AudioBit Desktop (PC, source of truth)
- Relay server (Railway)
- Remote clients (Android, iOS, Web)

Protocol goals:

- low latency control (`100-200 ms` end-to-end target)
- multi-remote synchronization
- small JSON messages
- reliable ordering and conflict handling

---

## 2) Common Envelope (All Messages)

Every message uses this envelope:

```json
{
  "v": 1,
  "t": "state",
  "sid": "AB7K2Q",
  "seq": 1182,
  "rev": 401,
  "ts": 1762334123456,
  "cid": "c_9f2a",
  "rid": "r_ios_01",
  "d": {}
}
```

### Envelope fields

- `v` (number): protocol version.
- `t` (string): message type.
- `sid` (string): relay session id.
- `seq` (number): sender-local sequence counter.
- `rev` (number): authoritative PC state revision. Only PC increments this.
- `ts` (number): unix timestamp (ms).
- `cid` (string, optional): command correlation id.
- `rid` (string, optional): remote client id assigned by relay.
- `d` (object): payload.

### Ordering rules

- Clients must apply only newest `rev`.
- If `rev` is older than currently rendered UI state, drop the message.
- `seq` is only for sender stream ordering/debugging.

---

## 3) Message Types

### 3.1 Handshake / Session

- `hello_pc`: PC authenticates and binds session.
- `hello_remote`: remote authenticates and joins session.
- `hello_ok`: relay confirmation.
- `session_status`: relay online/offline status updates.

#### `hello_pc`

```json
{
  "v": 1,
  "t": "hello_pc",
  "sid": "AB7K2Q",
  "seq": 1,
  "ts": 1762334123000,
  "d": {
    "pc_id": "pc_main",
    "auth": "jwt-or-token"
  }
}
```

#### `hello_remote`

```json
{
  "v": 1,
  "t": "hello_remote",
  "sid": "AB7K2Q",
  "seq": 1,
  "ts": 1762334123010,
  "d": {
    "remote_name": "iPhone 15",
    "auth": "jwt-or-token"
  }
}
```

#### `hello_ok`

```json
{
  "v": 1,
  "t": "hello_ok",
  "sid": "AB7K2Q",
  "seq": 2,
  "ts": 1762334123020,
  "rid": "r_ios_01",
  "d": {
    "role": "remote",
    "pc_online": 1
  }
}
```

#### `session_status`

```json
{
  "v": 1,
  "t": "session_status",
  "sid": "AB7K2Q",
  "seq": 40,
  "ts": 1762334129000,
  "d": {
    "pc_online": 0,
    "reason": "pc_disconnected"
  }
}
```

---

### 3.2 Full State (`state`) - PC -> remotes

Use `t = "state"` for full authoritative snapshot.

```json
{
  "v": 1,
  "t": "state",
  "sid": "AB7K2Q",
  "seq": 1182,
  "rev": 401,
  "ts": 1762334123456,
  "d": {
    "m": { "v": 67, "mu": 0 },
    "mic": { "mu": 1 },
    "def": { "out": "o2", "in": "i1" },
    "out": [
      { "id": "o1", "n": "Headphones (USB DAC)" },
      { "id": "o2", "n": "Speakers (Realtek)" }
    ],
    "in": [
      { "id": "i1", "n": "Shure MV7" },
      { "id": "i2", "n": "Webcam Mic" }
    ],
    "apps": [
      {
        "id": "a_spotify",
        "pid": 14320,
        "n": "Spotify",
        "v": 80,
        "mu": 0,
        "out": "o2",
        "in": "",
        "pk": 214
      },
      {
        "id": "a_discord",
        "pid": 11764,
        "n": "Discord",
        "v": 65,
        "mu": 0,
        "out": "",
        "in": "i1",
        "pk": 41
      }
    ]
  }
}
```

### `state.d` fields

- `m`: master playback state
- `m.v`: master volume `0..100`
- `m.mu`: master mute `0|1`
- `mic.mu`: default microphone mute `0|1`
- `def.out`: current default output device id
- `def.in`: current default input device id
- `out`: available output devices
- `in`: available input devices
- `apps`: active app sessions

### `apps[]` fields

- `id`: stable app/session id used by commands
- `pid`: process id (diagnostic; app may have multiple related pids)
- `n`: app name
- `v`: app volume `0..100`
- `mu`: app mute `0|1`
- `out`: per-app output device id (`""` = system default)
- `in`: per-app input device id (`""` = system default / not routed)
- `pk`: current peak meter `0..1000` integer

---

### 3.3 Level Update (`lvl`) - PC -> remotes

Lightweight frequent meter message for smooth animation.

```json
{
  "v": 1,
  "t": "lvl",
  "sid": "AB7K2Q",
  "seq": 1199,
  "rev": 401,
  "ts": 1762334123520,
  "d": {
    "dt": 50,
    "a": [
      ["a_spotify", 356],
      ["a_discord", 18]
    ]
  }
}
```

### `lvl.d` fields

- `dt`: interval in milliseconds for this sample batch.
- `a`: array of `[app_id, peak]` pairs.
- `peak`: integer `0..1000`.

Notes:

- Include only active/changed apps to keep payload small.
- Recommended send rate: `20 Hz` (`50 ms`) for meters.

---

### 3.4 Device Refresh (`devices`) - PC -> remotes

Optional targeted message when only device inventory/default changes.

```json
{
  "v": 1,
  "t": "devices",
  "sid": "AB7K2Q",
  "seq": 1203,
  "rev": 402,
  "ts": 1762334123600,
  "d": {
    "def": { "out": "o1", "in": "i2" },
    "out": [
      { "id": "o1", "n": "Headphones (USB DAC)" },
      { "id": "o2", "n": "Speakers (Realtek)" }
    ],
    "in": [
      { "id": "i1", "n": "Shure MV7" },
      { "id": "i2", "n": "Webcam Mic" }
    ]
  }
}
```

---

## 4) Commands (`cmd`) - remotes -> PC

All control requests use `t = "cmd"` and must include `cid`.

Generic format:

```json
{
  "v": 1,
  "t": "cmd",
  "sid": "AB7K2Q",
  "seq": 55,
  "cid": "c_1001",
  "ts": 1762334123650,
  "d": {
    "op": "set_master_volume"
  }
}
```

## 5) Full Command List (all required ops)

### 5.1 `set_master_volume`

```json
{ "op": "set_master_volume", "v": 72 }
```

- `v`: `0..100`.

### 5.2 `mute_master`

```json
{ "op": "mute_master", "mu": 1 }
```

- `mu`: `0|1`.

### 5.3 `mute_mic`

```json
{ "op": "mute_mic", "mu": 1 }
```

- `mu`: `0|1`.

### 5.4 `set_app_volume`

```json
{ "op": "set_app_volume", "app": "a_spotify", "v": 80 }
```

- `app`: app id.
- `v`: `0..100`.

### 5.5 `mute_app`

```json
{ "op": "mute_app", "app": "a_spotify", "mu": 1 }
```

- `app`: app id.
- `mu`: `0|1`.

### 5.6 `set_app_output_device`

```json
{ "op": "set_app_output_device", "app": "a_spotify", "out": "o2" }
```

- `out`: output device id, or `""` for system default.

### 5.7 `set_app_input_device`

```json
{ "op": "set_app_input_device", "app": "a_discord", "in": "i1" }
```

- `in`: input device id, or `""` for system default.

### 5.8 `set_output_device`

```json
{ "op": "set_output_device", "out": "o1" }
```

- sets global Windows default output.

### 5.9 `set_input_device`

```json
{ "op": "set_input_device", "in": "i2" }
```

- sets global Windows default input.

### 5.10 `play_soundboard_clip`

```json
{ "op": "play_soundboard_clip", "clip": "airhorn_01", "gain": 85 }
```

- `clip`: soundboard clip id.
- `gain`: optional `0..100`, default server/PC policy if omitted.

---

## 6) Command Result / Ack (`cmd_result`) - PC -> remotes

Every command should get an explicit result for UX reliability.

```json
{
  "v": 1,
  "t": "cmd_result",
  "sid": "AB7K2Q",
  "seq": 1210,
  "rev": 405,
  "cid": "c_1001",
  "ts": 1762334123700,
  "d": { "ok": 1, "err": "" }
}
```

Failure example:

```json
{
  "v": 1,
  "t": "cmd_result",
  "sid": "AB7K2Q",
  "seq": 1211,
  "rev": 405,
  "cid": "c_1002",
  "ts": 1762334123710,
  "d": { "ok": 0, "err": "invalid_device" }
}
```

Recommended error codes:

- `invalid_arg`
- `invalid_device`
- `app_not_found`
- `not_supported`
- `pc_offline`
- `rate_limited`
- `internal_error`

---

## 7) Edge / Operational Messages

### 7.1 Generic Error (`err`)

```json
{
  "v": 1,
  "t": "err",
  "sid": "AB7K2Q",
  "seq": 90,
  "ts": 1762334125000,
  "cid": "c_1004",
  "d": {
    "code": "unauthorized",
    "msg": "token expired"
  }
}
```

### 7.2 Heartbeat (`ping` / `pong`)

```json
{ "v": 1, "t": "ping", "sid": "AB7K2Q", "seq": 500, "ts": 1762334128000, "d": {} }
```

```json
{ "v": 1, "t": "pong", "sid": "AB7K2Q", "seq": 900, "ts": 1762334128001, "d": {} }
```

### 7.3 State Resync Request (`resync`)

Client can request a full state snapshot:

```json
{
  "v": 1,
  "t": "resync",
  "sid": "AB7K2Q",
  "seq": 77,
  "ts": 1762334128800,
  "d": {}
}
```

Relay/PC response should be immediate `state`.

---

## 8) Relay Session Model

Session key: `sid` (session id).

Server-side state per session:

- `session_id`
- `pc_conn` (single authoritative PC WebSocket)
- `remote_conns` (0..N remote sockets)
- `last_state` (cached latest full state JSON)
- `last_rev` (latest PC revision)
- `created_at`, `last_seen`
- `status` (`online|offline`)

Rules:

- only one active PC connection per `sid`.
- many remotes can attach to same `sid`.
- relay forwards commands to PC; relay does not modify mixer state.
- on remote join, relay sends cached `state` immediately.
- when PC disconnects, relay publishes `session_status` with `pc_online=0`.

---

## 9) Multi-Remote Consistency

- PC is source of truth.
- any successful command must be reflected by new `rev`.
- remotes update UI from incoming authoritative messages, not optimistic local state only.
- if multiple remotes issue conflicting commands, last applied command on PC wins and broadcasts updated `rev`.

---

## 10) Latency + Payload Optimization

- `lvl` at `20 Hz` (`50 ms`) with changed apps only.
- `state` on significant changes with coalescing (`25-40 ms` batch window).
- full `state` safety snapshot every `1 s` to prevent drift.
- compact keys (`mu`, `pk`, `out`, `in`) and integer ranges.
- permessage-deflate enabled on WebSocket.
- remote slider commands throttled to `20-25 Hz`.

---

## 11) App/Device Identifier Rules

- device ids (`o*`, `i*`) should be stable aliases mapped to native Windows endpoint ids.
- app id should be stable for routing target semantics; prefer logical app key instead of raw pid alone.
- `pid` is informational and can change across launches.
- empty app route device (`""`) means "System Default".

---

## 12) Example End-to-End Flow

1. Remote sends:
```json
{
  "v": 1,
  "t": "cmd",
  "sid": "AB7K2Q",
  "seq": 120,
  "cid": "c_2001",
  "ts": 1762334130000,
  "d": { "op": "set_app_output_device", "app": "a_spotify", "out": "o2" }
}
```

2. PC applies route, then emits:
```json
{
  "v": 1,
  "t": "cmd_result",
  "sid": "AB7K2Q",
  "seq": 2200,
  "rev": 550,
  "cid": "c_2001",
  "ts": 1762334130080,
  "d": { "ok": 1, "err": "" }
}
```

3. PC broadcasts updated `state` (rev `550`) to all remotes.

---

This specification is the baseline contract for desktop, relay, mobile, and web implementations.
