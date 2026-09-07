# Cowpanion Wire Protocol v1

The contract between the Cowpanion desktop client and the Cowpanion pasture server.
Both sides implement this document. Neither side may change it unilaterally.

**Status:** v1. Any change to message shapes, field names, or limits requires bumping
`protocolVersion` and updating this file in the same commit.

---

## Transport

- WebSocket over TLS only: `wss://cows.carlospoupado.com/ws`
- Text frames, UTF-8, **exactly one JSON object per frame**. No frame batching, no binary
  frames, no fragmented application messages.
- Max frame size **4096 bytes**. The server closes a connection that exceeds it (code 4004)
  rather than trying to parse it.
- Every message has a string type tag `t`. Unknown `t` values are **ignored, not fatal** —
  this is what lets v1 clients survive a server that has started sending v2 additions.

---

## Identity

- `clientId`: 128-bit random hex string (32 chars), generated once by the client on first
  run and persisted in its config. Pseudonymous. Not derived from hostname, username, MAC,
  or anything else identifying — generate it from a CSPRNG and never regenerate it unless
  the user explicitly resets.
- `displayName`: user-chosen, 1–16 characters after sanitisation. Server strips control
  characters and collapses whitespace. Empty after sanitisation → server substitutes
  `"cow"`.
- No accounts, no passwords, no email, no tokens. Pasture membership is the only access
  control, and it is a shared secret, not authentication. Treat every pasture as public to
  anyone who has the code.

---

## Pastures

A pasture is a named room. Clients join exactly one.

- Default pasture code is `commons`, so a fresh install connects with zero configuration.
- Codes are 1–32 chars, `[a-z0-9-]` only, lowercased by the server.
- `MAX_MEMBERS` per pasture: **24**. A 25th join is rejected with close code 4001.
- `VISIBLE_CAP`: **12**. The server sends full member details for at most 12 members and
  reports the rest as a count in `presence.overflow`. Clients render up to 12 cows plus an
  overflow indicator.

The caps exist because a screen edge full of cows stops being charming somewhere around a
dozen, and a chat with 24 anonymous participants stops being readable well before that.

---

## Client → server

### `hello` — first frame after connect, mandatory

```json
{ "t": "hello", "protocolVersion": 1, "clientId": "a3f1…", "pasture": "commons",
  "displayName": "Carlos", "variant": "cow" }
```

Must be the first frame. The server closes with 4004 if any other message arrives first, or
if `hello` does not arrive within **10 seconds** of the socket opening.

If `protocolVersion` does not match the server's, the server replies `error` with code
`version` and closes with 4000. It does **not** attempt to downgrade.

### `chat`

```json
{ "t": "chat", "text": "morning" }
```

- `text`: 1–140 characters after the server normalises it. Longer is **truncated, not
  rejected** — truncation is grapheme-cluster-safe, so it never splits an emoji or a
  combining mark.
- Server strips control characters and zero-width characters, collapses runs of whitespace,
  and rejects an empty result silently.
- Rate limit: token bucket, **capacity 3, refill 1 token per 3 seconds**. Over-limit
  messages are dropped silently and the client receives no error — a chatty client should
  not be able to make the server chatty. Sustained abuse (20 dropped messages in 60s) closes
  with 4002.

### `ping`

```json
{ "t": "ping" }
```

Sent every **20 seconds**. Application-level rather than WebSocket ping frames, because
intermediate proxies handle control frames inconsistently and this must survive Apache's
tunnel. See the server plan's `ProxyTimeout` note.

### `bye`

```json
{ "t": "bye" }
```

Courtesy message before a clean shutdown so other members see the cow leave immediately
rather than after eviction. Optional — the server must handle its absence.

---

## Server → client

### `welcome` — reply to a valid `hello`

```json
{ "t": "welcome", "protocolVersion": 1, "yourId": "a3f1…", "pasture": "commons",
  "visibleCap": 12, "serverTime": 1757260800000 }
```

`yourId` echoes the client's own id so the client can identify its own cow without trusting
its local config. `serverTime` is Unix ms, used only to sanity-check clock skew on `chat.ts`.

### `presence` — sent on join, on any membership change, and at least every 60s

```json
{ "t": "presence",
  "members": [ { "id": "a3f1…", "name": "Carlos", "variant": "cow" } ],
  "overflow": 0 }
```

**This is the full membership list, not a delta.** The client replaces its member set
wholesale. Idempotent, self-healing after a missed message, and it means no client can drift
out of sync — worth far more than the bytes a delta protocol would save at this scale.

The list always includes the recipient. Order is not meaningful and may change between
messages; clients must not derive cow identity or position from list index.

### `chat`

```json
{ "t": "chat", "fromId": "a3f1…", "name": "Carlos", "text": "morning", "ts": 1757260800000 }
```

Echoed to **all** members including the sender, so the sender's own bubble is confirmed by
the server rather than rendered optimistically. `name` is included per-message so a client
can render a bubble for a member it hasn't seen in a `presence` yet.

### `error`

```json
{ "t": "error", "code": "version", "message": "protocol version mismatch" }
```

Codes: `version`, `pasture_full`, `pasture_invalid`, `malformed`, `rate_limited`, `banned`.
`message` is diagnostic text for logs, never shown to the user verbatim.

### `pong`

```json
{ "t": "pong" }
```

---

## Close codes

| Code | Meaning | Client behaviour |
| --- | --- | --- |
| 1000 | Normal | Reconnect after backoff |
| 1001 | Server going away / restart | Reconnect after backoff |
| 4000 | Protocol version mismatch | **Stop reconnecting.** Log once, run local-only, surface a tray hint |
| 4001 | Pasture full | Reconnect with long backoff (5 min) |
| 4002 | Rate-limit abuse | Reconnect with long backoff (5 min) |
| 4003 | Banned | **Stop reconnecting.** Do not retry this pasture until config changes |
| 4004 | Malformed / oversized frame | Reconnect after backoff; log the offending frame locally |

4000 and 4003 are terminal. Everything else retries. A client that reconnect-loops against a
terminal close is a bug, and it's the specific bug that turns a hobby server into an
accidental DDoS target.

---

## Timing and reconnection

| Parameter | Value |
| --- | --- |
| `hello` deadline after connect | 10s |
| Client heartbeat interval | 20s |
| Server eviction after silence | 60s |
| Unsolicited `presence` refresh | 60s |
| Reconnect backoff | 2s, ×2 up to 60s ceiling |
| Backoff jitter | ±25%, mandatory |

Jitter is not optional. Without it, a server restart brings every client back in the same
millisecond, forever, in a synchronised thundering herd — which is a funny failure mode for
this particular app and still a real one.

---

## What is deliberately not in this protocol

- **Cow positions.** Each client simulates every cow locally. Your cow is in a different
  place on your friend's screen, and that is correct — their monitor is a different width.
  Syncing positions would turn this into a real-time networked game requiring interpolation
  and jitter buffers, in exchange for nothing a user would notice.
- **Message history.** A client that reconnects sees no backlog. Cows are ambient, not a
  chat log.
- **Delivery guarantees.** No acks, no sequence numbers, no retransmission. A dropped chat
  message is a dropped chat message.
- **Typing indicators, reactions, DMs, presence status.** Not in v1. Do not add them without
  bumping the version.

---

## Privacy constraints (binding on both sides)

Chat text combined with an IP address is personal data under GDPR, and you are established
in the EU.

- The server does **not** persist chat messages. Ever. Not to disk, not to SQLite, not to a
  log file — including at debug log level.
- Access logs retain IP addresses for a **maximum of 7 days**, then rotate out.
- The only persisted state is the pasture registry and the ban list, and a ban list entry
  stores a hashed `clientId`, not an IP and not a name.
- The client sends nothing beyond the fields specified here. No hostname, no OS version, no
  usage counters, no crash telemetry.

This is a design constraint, not legal advice — if the app ever goes public, get someone who
does this for a living to look at it.
