#!/usr/bin/env bash
set -Eeuo pipefail

# --- helpers ---------------------------------------------------------------
log()   { printf "\033[1;36m[deploy]\033[0m %s\n" "$*"; }
fail()  { printf "\033[1;31m[deploy:ERR]\033[0m %s\n" "$*" >&2; exit 1; }

# --- preflight -------------------------------------------------------------
cd "$(dirname "$0")"
ROOT="$(pwd)"

[[ -d .git ]] || fail "This doesn't look like the repo root: $ROOT"
[[ -f src/SpiffyOS.Bot/SpiffyOS.Bot.csproj ]] || fail "Missing src/SpiffyOS.Bot project."
[[ -f src/SpiffyOS.Overlay/SpiffyOS.Overlay.csproj ]] || fail "Missing src/SpiffyOS.Overlay project."
[[ -f .env ]] || fail "Missing .env in $ROOT"
[ -d scripts ] && chmod +x scripts/*.sh 2>/dev/null || true


# Load env (SPIFFYOS_* and Twitch__* vars)
set -a; . "$ROOT/.env"; set +a

: "${SPIFFYOS_CONFIG:?SPIFFYOS_CONFIG not set}"
: "${SPIFFYOS_TOKENS:?SPIFFYOS_TOKENS not set}"
: "${SPIFFYOS_LOGS:?SPIFFYOS_LOGS not set}"

BOT_OUT="$ROOT/publish/bot"
OVR_OUT="$ROOT/publish/overlay"

log "Repo: $(git rev-parse --show-toplevel)"
log "Commit: $(git rev-parse --short HEAD)"
log "Config: $SPIFFYOS_CONFIG"
log "Tokens: $SPIFFYOS_TOKENS"
log "Logs:   $SPIFFYOS_LOGS"

# --- build/publish ---------------------------------------------------------
log "Restoring..."
dotnet restore

log "Publishing Bot → $BOT_OUT"
dotnet publish src/SpiffyOS.Bot -c Release -o "$BOT_OUT"

log "Publishing Overlay → $OVR_OUT"
dotnet publish src/SpiffyOS.Overlay -c Release -o "$OVR_OUT"

# --- restart services ------------------------------------------------------
log "Restarting services..."
sudo systemctl daemon-reload || true
sudo systemctl restart spiffyos-bot
sudo systemctl restart spiffyos-overlay

sleep 1

BOT_STATUS="$(systemctl is-active spiffyos-bot || true)"
OVR_STATUS="$(systemctl is-active spiffyos-overlay || true)"
log "Bot service:     $BOT_STATUS"
log "Overlay service: $OVR_STATUS"

[[ "$BOT_STATUS" == "active" ]] || fail "spiffyos-bot is not active"
[[ "$OVR_STATUS" == "active" ]] || fail "spiffyos-overlay is not active"

# --- quick health checks ---------------------------------------------------
# tail latest bot log
BOT_LOG="$(ls -1t "$SPIFFYOS_LOGS"/bot-*.log 2>/dev/null | head -1 || true)"
if [[ -n "$BOT_LOG" ]]; then
  log "Last 20 lines of $BOT_LOG"
  tail -n 20 "$BOT_LOG" || true
else
  log "No bot log files found yet."
fi

# overlay health
if [[ -n "${SPIFFYOS_OVERLAY_TOKEN:-}" ]]; then
  OVR_URL="https://spff.dev/overlay/?k=${SPIFFYOS_OVERLAY_TOKEN}"
  CODE="$(curl -s -o /dev/null -w '%{http_code}' "$OVR_URL" || true)"
  log "Overlay check GET /overlay/ → $CODE"
  [[ "$CODE" == "200" ]] || log "Heads-up: expected 200 with token; got $CODE"
else
  log "SPIFFYOS_OVERLAY_TOKEN not set; skipping overlay HTTP check."
fi

log "Deploy complete."
