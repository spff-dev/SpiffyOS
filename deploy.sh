#!/usr/bin/env bash
# Requires sudo for systemctl. Run as:  sudo ./deploy.sh
set -Eeuo pipefail

UNIT="spiffybot.service"
ROOT="/srv/bots/spiffyos"
PUB_BOT="$ROOT/publish/bot"
PUB_OVERLAY="$ROOT/publish/overlay"

log(){ echo "[deploy] $*"; }

kill_strays() {
  local pids
  pids="$(pgrep -f 'dotnet .*SpiffyOS\.Bot\.dll' || true)"
  if [[ -n "${pids}" ]]; then
    log "Killing stray bot processes: ${pids}"
    kill ${pids} || true
    sleep 0.5
    pids="$(pgrep -f 'dotnet .*SpiffyOS\.Bot\.dll' || true)"
    if [[ -n "${pids}" ]]; then
      log "Force killing remaining: ${pids}"
      kill -9 ${pids} || true
    fi
  fi
}

ensure_single_instance() {
  local count
  count="$(pgrep -fc 'dotnet .*SpiffyOS\.Bot\.dll' || true)"
  log "Running bot processes: ${count}"
  if [[ "${count}" != "1" ]]; then
    log "ERROR: expected exactly 1 bot process, got ${count}"
    pgrep -fa 'dotnet .*SpiffyOS\.Bot\.dll' || true
    exit 1
  fi
}

log "Repo:   ${ROOT}"
log "Commit: $(git rev-parse --short HEAD)"
log "Config: ${ROOT}/config"
log "Tokens: ${ROOT}/secrets"
log "Logs:   ${ROOT}/logs"

log "Stopping service..."
systemctl stop "${UNIT}" || true

log "Killing any stray processes..."
kill_strays

log "Restoring..."
dotnet restore

log "Publishing Bot → ${PUB_BOT}"
dotnet publish src/SpiffyOS.Bot -c Release -o "${PUB_BOT}"

log "Publishing Overlay → ${PUB_OVERLAY}"
dotnet publish src/SpiffyOS.Overlay -c Release -o "${PUB_OVERLAY}"

log "Starting service..."
systemctl daemon-reload || true
systemctl start "${UNIT}"

sleep 1
STATE="$(systemctl is-active "${UNIT}" || true)"
log "Bot service: ${STATE}"
if [[ "${STATE}" != "active" ]]; then
  log "Service failed to start. Recent journal:"
  journalctl -u "${UNIT}" -n 80 --no-pager -o cat || true
  exit 1
fi

ensure_single_instance

# Optional: quick overlay check if you have Nginx wired
if [[ -n "${OVERLAY_URL:-}" ]]; then
  log "Overlay check GET ${OVERLAY_URL}"
  curl -s -o /dev/null -w "[deploy] Overlay HTTP %{http_code}\n" "${OVERLAY_URL}" || true
fi

log "Deploy complete."
