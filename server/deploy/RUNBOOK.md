# Cowpanion pasture server — Runbook

Everything here runs on the VPS as root (or with `sudo`) unless it says "from your PC".
`<repo>` is the git URL of this monorepo. The service user is `cowpanion`, the checkout is
`/opt/cowpanion`, the working directory is `/opt/cowpanion/server`.

## 0. Prerequisites on the VPS

```bash
apt update && apt install -y git apache2 certbot fail2ban
# Node.js >= 22 (Debian's packaged node is too old). NodeSource 22 LTS:
curl -fsSL https://deb.nodesource.com/setup_22.x | bash - && apt install -y nodejs
node -v   # must print v22.13 or newer (v22.5–22.12 need --experimental-sqlite, see the unit file)
```

## 1. DNS

Create an **A record** `cows.carlospoupado.com` → the VPS IPv4 (and an AAAA record if the VPS
has IPv6). Wait until it resolves from your PC:

```powershell
Resolve-DnsName cows.carlospoupado.com      # or: nslookup cows.carlospoupado.com
```

## 2. Install (first time)

```bash
git clone <repo> /tmp/cowpanion-bootstrap
sudo COWPANION_REPO=<repo> bash /tmp/cowpanion-bootstrap/server/deploy/install.sh
```

The script is idempotent. It creates the user, clones to `/opt/cowpanion`, runs
`npm ci --omit=dev`, installs the systemd unit, Apache vhost, logrotate rule and fail2ban jail,
enables the Apache modules and starts the service. The `:443` vhost stays dormant until the
certificate exists.

## 3. TLS certificate

```bash
certbot certonly --webroot -w /var/www/html -d cows.carlospoupado.com \
    --deploy-hook "systemctl reload apache2"
systemctl reload apache2
certbot renew --dry-run
```

`certonly --webroot` does not touch the vhost. If you prefer `certbot --apache`, it will edit
`/etc/apache2/sites-available/cows.conf` (or add a `cows-le-ssl.conf`); keep the proxy block.

## 4. Redeploy (after every server change)

```bash
sudo bash /opt/cowpanion/server/deploy/install.sh     # git pull --ff-only, npm ci, restart
journalctl -u cowpanion -n 20 --no-pager
```

A restart drops every client; they reconnect with jittered backoff. That is the designed
behaviour.

## 5. Moderation

The chat log is never stored, so identify a cow by its `clientId`: it appears in the client's
own presence list and in the server's `join` log lines (`journalctl -u cowpanion | grep join`).

```bash
cd /opt/cowpanion/server
sudo -u cowpanion env COWPANION_DATA=/opt/cowpanion/server/data node bin/cowpanion-ban.js ban   <clientId>
sudo -u cowpanion env COWPANION_DATA=/opt/cowpanion/server/data node bin/cowpanion-ban.js unban <clientId>
sudo -u cowpanion env COWPANION_DATA=/opt/cowpanion/server/data node bin/cowpanion-ban.js list
```

A ban takes effect immediately: the next `hello` is refused with 4003, and a currently
connected member is disconnected at the next eviction sweep (≤ 10s). Only the SHA-256 of the
id is stored.

## 6. Test tools from your PC

```powershell
npm install -g wscat          # or use `npx wscat`
npx wscat -c wss://cows.carlospoupado.com/ws
```

Once connected, paste a hello (one line) then whatever you want to test:

```json
{"t":"hello","protocolVersion":1,"clientId":"00000000000000000000000000000001","pasture":"test-1","displayName":"one","variant":"brown"}
{"t":"ping"}
{"t":"chat","text":"morning"}
{"t":"bye"}
```

Note the server closes any connection that does not send a valid `hello` within 10 seconds, so
have the line ready before connecting. The clientId must be 32 hex characters.

---

## Acceptance checks

Each item names the criterion from `docs/cowpanion-server-plan.md` and the exact command.

### S0 — Reachable socket

**S0.1 wscat connects from outside and the server answers.** (v1 does not echo arbitrary text —
a frame that is not a `hello` closes with 4004 by design; use `ping`/`pong` as the echo.)
```powershell
npx wscat -c wss://cows.carlospoupado.com/ws
# paste the hello line, expect {"t":"welcome",...} then {"t":"presence",...}
# send {"t":"ping"}, expect {"t":"pong"}
```

**S0.2 Connection survives 5 minutes fully idle.** Not literally possible in v1: the server evicts a
client after 60s of silence per protocol. The equivalent check is that the *proxy* does not cut the
connection before the server does, i.e. `ProxyTimeout 300` is honoured:
```powershell
npx wscat -c wss://cows.carlospoupado.com/ws   # send hello, then send NOTHING
# Expect the close after ~60–70s to carry code 1000 reason "idle" (server eviction).
# A close after ~60s with code 1006 / no code means Apache cut it: check ProxyTimeout.
```
And the positive check: with a hello followed by `{"t":"ping"}` every 20s (the client's real
behaviour), the connection must stay up for 5+ minutes. Easiest with the desktop client running
and `journalctl -u cowpanion -f` showing no `leave`/`evict` for it.

**S0.3 Restart recovers.**
```bash
systemctl restart cowpanion && sleep 2 && systemctl status cowpanion --no-pager
```

**S0.4 :8787 not reachable from outside.** From your PC:
```powershell
nmap -p 8787 cows.carlospoupado.com          # expect closed/filtered
curl.exe -m 5 http://cows.carlospoupado.com:8787/healthz   # expect connection failure
```
On the VPS: `ss -ltnp | grep 8787` must show `127.0.0.1:8787` only.

**S0.5 http redirects to https; wss is the only transport.**
```powershell
curl.exe -sI http://cows.carlospoupado.com/ws | Select-String "301|Location"
npx wscat -c ws://cows.carlospoupado.com/ws   # expect failure (301, not an upgrade)
curl.exe -sI https://cows.carlospoupado.com/  | Select-String "403|Strict-Transport"
```

**S0.6 Logs land in journald.**
```bash
journalctl -u cowpanion -n 20 --no-pager     # single-line JSON, "event":"listening" present
```

### S1 — Presence

**S1.1 Three wscat clients see all three in presence.** Open three terminals, connect each with
a different `clientId` (…0001, …0002, …0003) and the same `pasture`. Each terminal's latest
`presence` must list three members with `overflow: 0`.

**S1.2 Killing a client removes it within 60s.** Close one terminal window (Ctrl+C or kill the
process). The other two receive a `presence` with two members — immediately if the TCP close
reached the server, otherwise at eviction (≤ 60s + 10s sweep).

**S1.3 Duplicate clientId replaces.** Connect a fourth terminal with clientId …0001. The first
terminal closes with code 1000 ("replaced by new connection"); presence still shows three.

**S1.4 Pasture isolation.** Connect two clients with `pasture` `"a-1"` and `"b-1"`. Each sees only
itself in presence; a `chat` in one never appears in the other.

**S1.5 25th member → 4001.** Scripted. 25 connections from one PC would trip the per-IP cap (4)
first, so run this **on the VPS**, talking to the server directly with a distinct
`X-Forwarded-For` per connection (the server trusts that header because only Apache can reach it):
```bash
cd /opt/cowpanion/server && node -e "const {WebSocket}=require('ws');for(let i=1;i<=25;i++){const w=new WebSocket('ws://127.0.0.1:8787/ws',{headers:{'x-forwarded-for':'10.99.0.'+i}});w.on('open',()=>w.send(JSON.stringify({t:'hello',protocolVersion:1,clientId:i.toString(16).padStart(32,'0'),pasture:'cap-test',displayName:'m'+i})));w.on('close',c=>console.log(i,'closed',c));w.on('message',d=>{const m=JSON.parse(d);if(m.t==='presence'&&i===1)console.log('members',m.members.length,'overflow',m.overflow)})};setTimeout(()=>process.exit(0),8000)"
```
Expect `25 closed 4001`, and the last presence line to read `members 12 overflow 12`.
**S1.6 13 members → 12 + overflow 1.** Change `i<=25` to `i<=13`; expect `members 12 overflow 1`.

**S1.7 chat before hello → 4004.** Connect with wscat, send `{"t":"chat","text":"x"}` first.
Expect `Disconnected (code: 4004, reason: "expected hello")`.

**S1.8 Malformed JSON / 5KB frame / binary frame close cleanly.** With wscat: send `{nope` →
4004. Send a 5000-character line → 4004. Binary needs a script:
```powershell
node -e "const {WebSocket}=require('ws');const w=new WebSocket('wss://cows.carlospoupado.com/ws');w.on('open',()=>w.send(Buffer.from('{\"t\":\"ping\"}'),{binary:true}));w.on('close',c=>console.log('closed',c))"
```
Then `systemctl status cowpanion` — still active, no restart counter increase.

### S2 — Chat

**S2.1 Chat reaches all three including sender.** Three terminals as in S1.1; send
`{"t":"chat","text":"morning"}` from one; all three print a `chat` with `fromId`, `name`, `ts`.

**S2.2 / S2.3 500-char message truncated to 140 graphemes; no broken emoji.** Send a chat whose
`text` is 500 characters including flags/family emoji. The relayed text renders without a stray
half-flag or replacement character.

**S2.4 10 messages in 1s → 3 relayed.** In wscat paste 10 chat lines quickly; exactly 3 come back
and the connection stays open.

**S2.5 Sustained flood → 4002.** Paste 25+ chat lines within a few seconds; expect
`{"t":"error","code":"rate_limited"}` then close 4002.

**S2.6 Chat text never in logs.**
```bash
journalctl -u cowpanion | grep -c "<phrase you sent>"    # expect 0
journalctl -u cowpanion | grep '"event":"chat"' | tail -1   # only pasture, fromId, bytes
```

**S2.7 Control chars / ZWJ / 200 newlines neutralised.** Send `{"t":"chat","text":"a\u0007b\u200Bc\n\n\n\nd"}`
(wscat passes JSON escapes through). Relayed text is `abc d`.

### S3 — Hardening

**S3.1 Banned clientId cannot join, clean one can.**
```bash
sudo -u cowpanion env COWPANION_DATA=/opt/cowpanion/server/data node /opt/cowpanion/server/bin/cowpanion-ban.js ban 00000000000000000000000000000001
```
wscat hello with …0001 → `error banned` + close 4003. Hello with …0002 → welcome. Unban afterwards.

**S3.2 systemctl stop → 1001 within 5s, no orphan.**
```bash
# with a wscat client connected:
time systemctl stop cowpanion          # wscat shows code 1001; time < 5s
pgrep -u cowpanion node || echo "no orphan"
systemctl start cowpanion
```

**S3.3 Access logs older than 7 days are gone.**
```bash
logrotate -d /etc/logrotate.d/apache-cows         # dry run, no errors
logrotate -f /etc/logrotate.d/apache-cows         # force one rotation
ls -la /var/log/apache2/cows/                     # access.log + access.log-YYYYMMDD; never more than 6 dated files
```
Re-check after a week: `find /var/log/apache2/cows -mtime +7` prints nothing.

**S3.4 200 rapid connection attempts from one IP are throttled.** From your PC:
```powershell
1..200 | ForEach-Object { curl.exe -s -o NUL -m 3 -H "Upgrade: websocket" -H "Connection: Upgrade" -H "Sec-WebSocket-Version: 13" -H "Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==" https://cows.carlospoupado.com/ws }
```
On the VPS: `fail2ban-client status cowpanion` shows your IP banned (bantime 600s). A wscat client
on a *different* network (e.g. phone hotspot) is unaffected. Unban with
`fail2ban-client set cowpanion unbanip <your ip>`. Also check the per-IP cap: a 5th concurrent
connection from the same IP is refused with HTTP 429 (`journalctl -u cowpanion | grep ip_cap`).

**S3.4b X-Forwarded-For is being passed by Apache** (otherwise every client counts as 127.0.0.1
and the cap of 4 applies to everyone together):
```bash
journalctl -u cowpanion | grep '"event":"join"' | tail -3    # "ip" must be the client's /24, NOT "127.0.0.0/24"
```

**S3.5 24h soak: flat memory, no fd leak.** Run 5 desktop clients for a day, then:
```bash
systemctl show cowpanion -p MemoryCurrent; ls /proc/$(systemctl show -p MainPID --value cowpanion)/fd | wc -l
journalctl -u cowpanion --since "-24h" | grep -c -E "uncaught|unhandled_rejection"   # expect 0
```
Compare memory/fd counts at hour 1 and hour 24; they should be within noise.

**S3.6 /healthz unreachable from outside.**
```powershell
curl.exe -m 5 https://cows.carlospoupado.com/healthz     # expect 403 (Apache denies everything but /ws)
```
```bash
curl -s http://127.0.0.1:8787/healthz        # on the VPS: {"ok":true,"uptimeSec":..,"pastures":..,"members":..}
```

## Troubleshooting

| Symptom | Check |
| --- | --- |
| clients reconnect every ~60s | `ProxyTimeout 300` present in the active vhost (`apache2ctl -S`, `apache2ctl -t -D DUMP_INCLUDES`) |
| everyone gets 429 after 4 clients | S3.4b — X-Forwarded-For missing; `ProxyAddHeaders On` in the vhost |
| `node:sqlite` error at start | Node < 22.13: add `--experimental-sqlite` to `ExecStart` |
| `EACCES data/bans.sqlite` | `chown -R cowpanion:cowpanion /opt/cowpanion/server/data` |
| service flaps | `journalctl -u cowpanion -e` — the last `"level":"error"` line says why |
