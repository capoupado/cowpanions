# Cowpanion server — acceptance checklist

Every criterion from `docs/cowpanion-server-plan.md` S0–S3. Test names refer to
`server/test/protocol.test.js` and `server/test/server.test.js`; `npm test` on Windows /
Node 24.16 ran them (30 pass, 1 skipped) on 2026-09-07. Nobody in this session has VPS access,
so anything touching Apache, systemd, DNS, certbot or the public network is `manual: to verify`
with the command in `deploy/RUNBOOK.md` (section numbers below).

Legend: `automated: pass (test)` · `manual: to verify (RUNBOOK §)` · `not verifiable here`.

## S0 — Reachable socket

- [ ] wscat connects from outside and echoes — **manual: to verify** (RUNBOOK S0.1). Note: v1 does
      not echo arbitrary frames; `ping`→`pong` is the echo. The Node side of it is
      **automated: pass** (`S1: pasture code lowercased… unknown message types ignored` covers ping/pong).
- [ ] Connection survives 5 minutes fully idle — **manual: to verify** (RUNBOOK S0.2). Note: per
      protocol the *server* evicts after 60s of silence, so the meaningful check is that Apache's
      `ProxyTimeout 300` does not cut a heart-beating client for 5+ minutes.
- [ ] `systemctl restart cowpanion` recovers; status clean — **manual: to verify** (RUNBOOK S0.3).
- [ ] Not reachable on :8787 from outside — **manual: to verify** (RUNBOOK S0.4). Bind to
      `127.0.0.1` is **automated: pass** (every test connects to 127.0.0.1; `DEFAULTS.host`).
- [ ] http:// redirects to https://; wss only — **manual: to verify** (RUNBOOK S0.5).
- [ ] Logs land in `journalctl -u cowpanion` — **manual: to verify** (RUNBOOK S0.6). Single-line
      JSON on stdout is **automated: pass** (`S2: log output never contains chat text` parses every line).

## S1 — Presence

- [x] Three clients in one pasture see all three — **automated: pass** (`S1: three clients in one pasture each see all three in presence`).
- [x] Killed client removed within 60s — **automated: pass** (`S1: a member leaving (socket drop) or sending bye…` for TCP close; `S1: silent client is evicted after the eviction window…` for silent death, with short timers). Production values 60s/10s sweep are `DEFAULTS` in `src/server.js`; real-time wall-clock check is **manual: to verify** (RUNBOOK S1.2).
- [x] Same clientId twice → one member, first socket closed — **automated: pass** (`S1: same clientId connecting twice…`).
- [x] Two pasture codes isolated — **automated: pass** (`S1: two pasture codes are fully isolated`).
- [x] 25th member → 4001 — **automated: pass** (`S1: 25th member gets close 4001; 13 members…`).
- [x] 13 members → 12 entries + overflow 1 — **automated: pass** (same test; also asserts the recipient is always in its own list).
- [x] `chat` before `hello` → 4004 — **automated: pass** (`S1: chat before hello closes with 4004; hello deadline…`, also covers the 10s deadline with a short timer).
- [x] Malformed JSON, 5KB frame, binary frame close cleanly, process survives — **automated: pass** (`S1: malformed JSON, 5KB frame, binary frame each close 4004…`; a 70KB frame is also checked).
- [x] Extra (from the brief): version mismatch → `error version` + 4000; invalid pasture → `error pasture_invalid` + close; bad clientId → 4004; variant/displayName substitution; unknown `t` ignored — **automated: pass** (`S1: hello rejections…`, `S1: hello validation…`, `S1: displayName sanitised…`, `S1: pasture code lowercased…`).
- [x] Unsolicited presence every 60s — **automated: pass** (`S1: unsolicited presence refresh arrives on the interval`, short timer).

## S2 — Chat

- [x] Message reaches all three incl. sender with `fromId`, `name`, `ts` — **automated: pass** (`S2: chat from one client arrives at all three including the sender`).
- [x] 500-char message → 140 graphemes, no broken emoji/combining mark — **automated: pass** (`S2: 500-char message truncates to 140 graphemes…` unit; `S2: 500-char and mid-emoji messages arrive truncated…` end-to-end).
- [x] Message ending mid-emoji truncates to a valid string — **automated: pass** (`S2: a message ending mid-emoji sequence truncates to a valid string`).
- [x] 10 messages in 1s → 3 relayed, connection survives — **automated: pass** (`S2: 10 messages in 1s -> 3 relayed…`, real production bucket values).
- [x] Sustained flooding → 4002 — **automated: pass** (`S2: sustained flooding closes with 4002`, plus unit `S2: token bucket…abuse after 20 drops`).
- [x] `journalctl | grep <phrase>` returns nothing — **automated: pass** on the log sink (`S2: log output never contains chat text`); the journald end of it is **manual: to verify** (RUNBOOK S2.6).
- [x] Control chars, ZWJ, 200-newline message neutralised — **automated: pass** (`S2: control chars, zero-width chars, bidi controls and 200 newlines are neutralised`; `S2: legitimate ZWJ emoji sequences survive normalisation` guards the other direction).

## S3 — Hardening

- [x] Banned clientId cannot join; clean id can — **automated: pass** (`S3: banned clientId gets error+4003…`; also: ban applied while connected evicts at next sweep; unban re-admits). Hashing is SHA-256 in `src/bans.js`. CLI exercised by hand against a live server (ban/list/unban/usage error).
- [ ] `systemctl stop` → 1001, exit < 5s, no orphan — **automated: pass** for the in-process shutdown path (`S3: shutdown closes every client with 1001 and completes within 5s`). The real-signal test (`S3: real process handles SIGTERM…`) is **skipped on Windows** (no SIGTERM handlers) and WSL has no Node, so it did not run here — **manual: to verify** (RUNBOOK S3.2).
- [ ] Apache access logs older than 7 days gone — **manual: to verify** (RUNBOOK S3.3). Config: `deploy/logrotate-apache-cows` (`daily`, `rotate 6`, `maxage 7`).
- [ ] 200 rapid connection attempts throttled; existing members unaffected — per-IP cap (HTTP 429 on the 5th concurrent connection, XFF-aware) is **automated: pass** (`S3: per-IP connection cap…`). fail2ban deliberately not installed (owner decision 2026-09-07); the per-IP cap is the only throttle. **manual: to verify** on the VPS per RUNBOOK S3.4.
- [ ] 24-hour soak, flat memory, no fd leak, no unhandled rejections — **manual: to verify** (RUNBOOK S3.5). **not verifiable here**.
- [ ] `/healthz` unreachable from outside — **manual: to verify** (RUNBOOK S3.6). Localhost `/healthz` with uptime/pastures/members and 404 elsewhere is **automated: pass** (`S3: /healthz reports counts…`).
- [x] Structured JSON logs, IPs truncated /24 and /48 — **automated: pass** (`S3: IPs are truncated to /24 and /48`; `S3: per-IP connection cap…` asserts the full IP never appears).
- [x] `npm audit` clean, deps pinned, lockfile committed — **automated: pass** (`npm audit`: 0 vulnerabilities; `ws` pinned `8.21.3`; `package-lock.json` present).
- [x] `PRIVACY.md` — written (`server/PRIVACY.md`). Shipping it with the client is the client track's job.
- [ ] X-Forwarded-For actually arrives from Apache (not a plan criterion, but the per-IP cap depends on it) — **manual: to verify** (RUNBOOK S3.4b).

## Decisions where the plan left room

- Per-IP cap rejects the 5th connection at the HTTP upgrade with **429** rather than a WebSocket
  close code, so no socket state is allocated for it; clients treat it as a failed connect.
- Eviction and replacement close with **1000** (`idle` / `replaced by new connection`) — the
  protocol defines no dedicated code and 1000 makes a live client reconnect after backoff.
- Frames > 4096 bytes close **4004**; frames > 16 KB are refused earlier by `ws` with 1009 as a
  hard backstop.
- `hello` without a `pasture` field is `pasture_invalid` (the default `commons` is a client-side
  default per the protocol).
- ZWJ is stripped unless it joins two pictographs, so stray joiners are neutralised while family /
  profession emoji survive. Unicode tag characters (subdivision flags such as England) are stripped.
- A ban issued while the member is connected is enforced at the next sweep (≤ 10s), not only on
  the next `hello`.
