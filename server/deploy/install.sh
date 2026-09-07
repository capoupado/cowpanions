#!/usr/bin/env bash
# Cowpanion pasture server — idempotent install / deploy script for the Debian VPS.
#
# First install:   sudo COWPANION_REPO=<git url> bash install.sh
# Redeploy:        sudo bash /opt/cowpanion/server/deploy/install.sh
#
# What it does (every step is safe to re-run):
#   1. creates the unprivileged `cowpanion` system user
#   2. clones or fast-forwards the monorepo at /opt/cowpanion
#   3. npm ci --omit=dev inside /opt/cowpanion/server
#   4. installs the systemd unit, Apache vhost, logrotate rule and fail2ban jail
#   5. enables Apache modules + site, reloads Apache, enables and (re)starts the service
# It does NOT obtain the TLS certificate — see RUNBOOK.md for the certbot step.
set -euo pipefail

APP_DIR=/opt/cowpanion
SERVER_DIR="$APP_DIR/server"
DEPLOY_DIR="$SERVER_DIR/deploy"
DOMAIN=cows.carlospoupado.com
BRANCH="${COWPANION_BRANCH:-main}"
REPO_URL="${COWPANION_REPO:-}"
SERVICE_USER=cowpanion

log()  { printf '\n==> %s\n' "$*"; }
warn() { printf 'WARNING: %s\n' "$*" >&2; }
die()  { printf 'ERROR: %s\n' "$*" >&2; exit 1; }

[ "$(id -u)" -eq 0 ] || die "run as root (sudo)"
command -v git  >/dev/null || die "git is not installed"
command -v node >/dev/null || die "node is not installed (need Node.js >= 22, see RUNBOOK.md)"
command -v npm  >/dev/null || die "npm is not installed"
command -v apache2ctl >/dev/null || die "apache2 is not installed"

NODE_MAJOR="$(node -v | sed -E 's/^v([0-9]+).*/\1/')"
[ "$NODE_MAJOR" -ge 22 ] || die "Node.js $(node -v) is too old; need >= 22"
NODE_MINOR="$(node -v | sed -E 's/^v[0-9]+\.([0-9]+).*/\1/')"
if [ "$NODE_MAJOR" -eq 22 ] && [ "$NODE_MINOR" -lt 13 ]; then
  warn "Node $(node -v) needs --experimental-sqlite; edit ExecStart in the unit (see comment there)"
fi

# 1. user ---------------------------------------------------------------------
log "user $SERVICE_USER"
if ! id -u "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --home-dir "$APP_DIR" --no-create-home --shell /usr/sbin/nologin "$SERVICE_USER"
fi

# 2. code ---------------------------------------------------------------------
log "code at $APP_DIR (branch $BRANCH)"
if [ -d "$APP_DIR/.git" ]; then
  git -C "$APP_DIR" fetch --prune origin
  git -C "$APP_DIR" checkout -q "$BRANCH"
  git -C "$APP_DIR" pull --ff-only origin "$BRANCH"
else
  [ -n "$REPO_URL" ] || die "no checkout at $APP_DIR; set COWPANION_REPO=<git url> for the first install"
  git clone --branch "$BRANCH" "$REPO_URL" "$APP_DIR"
fi
[ -f "$SERVER_DIR/package.json" ] || die "$SERVER_DIR/package.json missing — wrong repo or branch?"
git -C "$APP_DIR" log -1 --format='   deployed commit %h %s'

# 3. dependencies -------------------------------------------------------------
log "npm ci --omit=dev"
(cd "$SERVER_DIR" && npm ci --omit=dev --no-audit --no-fund)

# data dir is the only writable path for the service
install -d -m 750 -o "$SERVICE_USER" -g "$SERVICE_USER" "$SERVER_DIR/data"

# 4. config files -------------------------------------------------------------
log "systemd unit"
install -m 644 "$DEPLOY_DIR/cowpanion.service" /etc/systemd/system/cowpanion.service
systemctl daemon-reload

log "apache vhost"
install -d -m 750 -o root -g adm /var/log/apache2/cows
install -d -m 755 /var/www/html
install -m 644 "$DEPLOY_DIR/apache-cows.conf" /etc/apache2/sites-available/cows.conf
a2enmod -q proxy proxy_http proxy_wstunnel rewrite headers ssl
a2ensite -q cows.conf
apache2ctl configtest
systemctl reload apache2

log "logrotate (7-day retention for the vhost access log)"
install -m 644 "$DEPLOY_DIR/logrotate-apache-cows" /etc/logrotate.d/apache-cows
logrotate -d /etc/logrotate.d/apache-cows >/dev/null 2>&1 || warn "logrotate dry run reported a problem: run 'logrotate -d /etc/logrotate.d/apache-cows'"

if command -v fail2ban-client >/dev/null; then
  log "fail2ban jail"
  install -m 644 "$DEPLOY_DIR/fail2ban-cowpanion.conf" /etc/fail2ban/jail.d/cowpanion.conf
  systemctl reload fail2ban || systemctl restart fail2ban
else
  warn "fail2ban is not installed; skipping the jail (apt install fail2ban, then re-run)"
fi

# 5. service ------------------------------------------------------------------
log "service"
systemctl enable -q cowpanion
systemctl restart cowpanion
sleep 1
systemctl --no-pager --lines=5 status cowpanion || die "cowpanion failed to start; see journalctl -u cowpanion"

# -----------------------------------------------------------------------------
if [ ! -f "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ]; then
  cat <<EOF

No TLS certificate yet, so the :443 vhost is inactive. Once DNS for $DOMAIN points here:
   apt install certbot
   certbot certonly --webroot -w /var/www/html -d $DOMAIN --deploy-hook "systemctl reload apache2"
   systemctl reload apache2
EOF
fi
log "done — see $DEPLOY_DIR/RUNBOOK.md for verification steps"
