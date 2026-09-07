# Cowpanion website

Static announcement page for Cowpanion (`index.html`, `privacy.html`, `style.css`, `assets/`). No build step, no scripts, no external requests; the cow animations are CSS `steps()` over the real sprite sheets copied from `client/assets/sprites/cow/` (re-encoded losslessly to ~5 KB each).

Deployed by `server/deploy/install.sh`, which copies this folder to `/var/www/cows` on the VPS; `server/deploy/nginx-cows.conf` serves it as the root of `https://cows.carlospoupado.com/` alongside the `/ws` WebSocket proxy.

`privacy.html` mirrors `client/src/Cowpanion.App/PRIVACY.md`; keep them in sync when the statement changes.
