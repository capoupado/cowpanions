# Cowpanion Pasture Server — Build Plan / Agent Brief

The multiplayer backend for Cowpanion. Tracks who is online in a pasture and relays chat.
Nothing else.

**Target:** existing Debian VPS, Apache in front, Node.js LTS, systemd.
**Wire contract:** `cowpanion-protocol.md`. That document is authoritative — implement it
exactly, do not extend it.

---

## How to use this document

Implement one phase per task. Report acceptance criteria as pass/fail with evidence.

This track runs **in parallel** with the desktop client track (`cowpanion-client-plan.md`).
The only coupling is that client P3 cannot be verified until server S2 is deployed. Nothing
in S0–S2 needs the client to exist — `wscat` is the test harness.

---

## Scope discipline

This server relays presence and text. It is not a game server, an account system, or a
platform. The entire thing should land in **under 600 lines**. If it grows past that,
something has been added that belongs in the protocol document as a rejected idea.

Explicitly out of scope, permanently: accounts, logins, OAuth, message history, message
persistence, cow position sync, a web UI, an admin panel, Docker orchestration, horizontal
scaling, Redis, a message queue, telemetry, metrics dashboards.

---

## Stack

| Concern | Decision |
| --- | --- |
| Runtime | Node.js current LTS — check with `node -v` and pin the major in `package.json` `engines` |
| WebSocket | `ws` |
| HTTP | Node's built-in `http`. Express only if a `/healthz` endpoint makes it earn its place |
| State | In-memory `Map`. Restarts drop everyone; clients reconnect. This is fine |
| Persistence | SQLite (`node:sqlite` or `better-sqlite3`) for the ban list only |
| Process | systemd, dedicated unprivileged user, listening on `127.0.0.1` only |
| TLS | Apache + certbot. Node never sees a certificate |
| Tests | `node:test`, driven through a real `ws` client against a server on an ephemeral port |

Dependency count should be 1–2. Every package added here is something you'll be patching at
2am for a cow app.

---

## Layout

```
/opt/cowpanion/
  package.json
  src/
    server.js          # http + ws bootstrap, signal handling
    pasture.js         # Pasture class: members, join/leave, broadcast
    registry.js        # pasture lookup, caps
    protocol.js        # parse, validate, normalise, serialise — protocol.md lives here
    ratelimit.js       # token bucket
    bans.js            # SQLite ban list
    log.js             # structured stdout, journald captures it
  test/
  data/
    bans.sqlite
```

`protocol.js` must be the **only** place that knows message shapes. Validation happens on
the way in, once, and everything downstream works with trusted objects.

---

## Phases

### S0 — Reachable socket

The riskiest part of this track isn't the server, it's the Apache WebSocket tunnel. Prove it
before writing any logic.

- `src/server.js`: HTTP server on `127.0.0.1:8787`, `ws` attached at path `/ws`, echoes any
  text frame back.
- Apache vhost for `cows.carlospoupado.com` with certbot TLS and `mod_proxy_wstunnel`.
- systemd unit, enabled, running as user `cowpanion`.

DNS: an A record for `cows.carlospoupado.com` pointing at the VPS. Use a dedicated subdomain
rather than a path on an existing vhost — it keeps the proxy config isolated from anything
already running.

Apache config:

```apache
<VirtualHost *:443>
    ServerName cows.carlospoupado.com

    ProxyPreserveHost On
    ProxyTimeout 300

    RewriteEngine On
    RewriteCond %{HTTP:Upgrade} websocket [NC]
    RewriteCond %{HTTP:Connection} upgrade [NC]
    RewriteRule ^/ws$ ws://127.0.0.1:8787/ws [P,L]

    ProxyPass        /ws  ws://127.0.0.1:8787/ws
    ProxyPassReverse /ws  ws://127.0.0.1:8787/ws

    Header always set Strict-Transport-Security "max-age=31536000"
</VirtualHost>
```

`a2enmod proxy proxy_http proxy_wstunnel rewrite headers` first.

**`ProxyTimeout 300` is the one that will waste your afternoon if you skip it.** The default
is 60 seconds, matching your 60-second eviction window almost exactly, so idle connections
get torn down by the proxy at roughly the moment the server would have evicted them — and
the resulting reconnect loop looks exactly like a server bug.

systemd unit at `/etc/systemd/system/cowpanion.service`:

```ini
[Unit]
Description=Cowpanion pasture server
After=network.target

[Service]
Type=simple
User=cowpanion
WorkingDirectory=/opt/cowpanion
ExecStart=/usr/bin/node src/server.js
Restart=always
RestartSec=3
Environment=NODE_ENV=production
Environment=COWPANION_PORT=8787
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/opt/cowpanion/data

[Install]
WantedBy=multi-user.target
```

**Acceptance criteria**

- [ ] `wscat -c wss://cows.carlospoupado.com/ws` connects from a machine outside the VPS and
      echoes.
- [ ] Connection survives **5 minutes fully idle** without being dropped.
- [ ] `systemctl restart cowpanion` brings it back automatically; `systemctl status` clean.
- [ ] Server is not reachable on `:8787` from outside the VPS (`nmap` or curl from your PC).
- [ ] Plain `http://` redirects to `https://`; `wss://` is the only working transport.
- [ ] Logs land in `journalctl -u cowpanion`.

---

### S1 — Presence

- `hello` handling: full validation per protocol, `welcome` reply, join a pasture.
- `Pasture` with member map keyed by `clientId`. A reconnecting `clientId` **replaces** its
  old entry and the old socket is closed — never two cows for one person.
- Broadcast `presence` on join, leave, and every 60s.
- `ping`/`pong`, 60s eviction sweep, `bye` handling.
- `MAX_MEMBERS` 24 → close 4001. `VISIBLE_CAP` 12 → `overflow` count.
- Reject anything before `hello`, and enforce the 10s `hello` deadline.

**Acceptance criteria**

- [ ] Three `wscat` clients in one pasture: each sees all three in `presence`.
- [ ] Killing one client's terminal removes it from the others' `presence` within 60s.
- [ ] Same `clientId` connecting twice yields one member and closes the first socket.
- [ ] Two different pasture codes are fully isolated — no cross-talk in either direction.
- [ ] 25th member gets close 4001.
- [ ] 13 members: `presence` carries 12 entries and `overflow: 1`.
- [ ] A connection that sends `chat` before `hello` is closed with 4004.
- [ ] Malformed JSON, a 5KB frame, and a binary frame each close cleanly without taking the
      process down.

---

### S2 — Chat

Client P3 unblocks when this is deployed.

- `chat` validation: normalise, strip control and zero-width characters, collapse
  whitespace, grapheme-safe truncate at 140.
- Token bucket per connection: capacity 3, refill 1 per 3s. Silent drop over limit; close
  4002 after 20 drops in 60s.
- Relay to all members including sender, with `fromId`, `name`, `ts`.
- **Never log message text**, at any log level. Log `fromId` and byte length only.

**Acceptance criteria**

- [ ] Message from one client arrives at all three, sender included.
- [ ] A 500-character message arrives truncated to 140 with no broken emoji or combining mark.
- [ ] A message ending mid-emoji sequence truncates to a valid string.
- [ ] Sending 10 messages in 1 second: 3 relayed, rest dropped, connection survives.
- [ ] Sustained flooding closes with 4002.
- [ ] `journalctl -u cowpanion | grep <a phrase you sent>` returns nothing.
- [ ] Control characters, zero-width joiners, and a 200-newline message are all neutralised.

---

### S3 — Hardening

- Ban list in SQLite keyed by **hashed** `clientId`; banned join closes 4003.
- Connection cap per source IP (default 4) to stop one machine opening hundreds.
- Graceful shutdown on `SIGTERM`: broadcast close 1001, drain, exit within 5s.
- Structured single-line JSON logs to stdout. Never log message text or full IPs — truncate
  IPv4 to /24 and IPv6 to /48.
- `logrotate` for Apache access logs, **7-day retention**, per the protocol's privacy rules.
- `/healthz` on localhost only: uptime, pasture count, member count. No message data.
- `fail2ban` jail or Apache `mod_ratelimit` on the vhost for connection-attempt floods.
- `npm audit` clean, dependencies pinned, `package-lock.json` committed.
- A one-page `PRIVACY.md` stating what is and isn't stored, shipped with the client too.

**Acceptance criteria**

- [ ] A banned `clientId` cannot join; its clean clientId still can.
- [ ] `systemctl stop` disconnects clients with 1001 and exits in under 5s, no orphan process.
- [ ] Apache access logs older than 7 days are gone after a rotation cycle.
- [ ] 200 rapid connection attempts from one IP are throttled; existing members unaffected.
- [ ] 24-hour soak with 5 clients: flat memory, no descriptor leak, no unhandled rejections.
- [ ] `/healthz` unreachable from outside the VPS.

---

## Operational notes

**Deploy** is `git pull && npm ci --omit=dev && systemctl restart cowpanion`. A restart drops
every client and they all reconnect with jittered backoff — that's the designed behaviour,
and it's why the backoff jitter in the protocol is mandatory rather than nice-to-have. Don't
build zero-downtime deployment for this.

**Capacity** is trivially within reach of any VPS. 24 idle WebSockets with a 20-second
heartbeat is background noise; the limits in the protocol exist for user experience and
moderation surface, not for server load.

**Monitoring**: `Restart=always` plus a weekly glance at `journalctl` is proportionate. If
the server is down, clients silently run local-only, so an outage is invisible rather than
an incident.

**The real risk isn't technical.** The moment someone outside your circle has a pasture code,
you're relaying anonymous text onto other people's screens with no moderation tooling beyond
a manual ban list. Keep pasture codes private, don't publish `commons`, and if you ever want
this public, build the reporting flow *before* announcing it, not after the first incident.
