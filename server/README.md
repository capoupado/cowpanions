# Cowpanion pasture server

WebSocket relay for presence and chat, protocol v1 (`../docs/cowpanion-protocol.md`).
Node.js >= 22, one dependency (`ws`), SQLite via `node:sqlite` for the ban list only.

```bash
npm ci                      # install
npm start                   # 127.0.0.1:8787, data in ./data
npm test                    # node:test, real ws clients on an ephemeral port (~3s)
npm run ban -- list         # ban | unban <clientId> | list  (bin/cowpanion-ban.js)
```

Environment: `COWPANION_PORT` (8787), `COWPANION_DATA` (./data), `COWPANION_LOG_LEVEL` (info),
`COWPANION_IP_CAP` (4). Health: `curl http://127.0.0.1:8787/healthz`.

Layout: `src/protocol.js` is the only module that knows message shapes; `server.js` owns the
connection lifecycle; `pasture.js`/`registry.js` hold the in-memory rooms; `ratelimit.js`,
`bans.js`, `log.js` do what they say. `deploy/` holds the VPS files and `RUNBOOK.md`.
`ACCEPTANCE.md` tracks every plan criterion; `PRIVACY.md` states what is stored.
