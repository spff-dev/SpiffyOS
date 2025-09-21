#!/usr/bin/env bash
# Requires sudo for systemctl. Run as:  sudo ./deploy.sh
set -Eeuo pipefail

UNIT="spiffybot.service"
ROOT="/srv/bots/spiffyos"
PUB_BOT="$ROOT/publish/bot"
PUB_OVERLAY="$ROOT/publish/overlay"
DATA_DIR="$ROOT/data"

# ----- colors -----
if [[ -t 1 ]] && command -v tput >/dev/null 2>&1 && [[ -z "${NO_COLOR:-}" ]]; then
  BOLD="$(tput bold)"; DIM="$(tput dim)"; RESET="$(tput sgr0)"
  RED="$(tput setaf 1)"; GREEN="$(tput setaf 2)"; YELLOW="$(tput setaf 3)"
  BLUE="$(tput setaf 4)"; MAGENTA="$(tput setaf 5)"; CYAN="$(tput setaf 6)"
else
  BOLD=""; DIM=""; RESET=""; RED=""; GREEN=""; YELLOW=""; BLUE=""; MAGENTA=""; CYAN=""
fi
TICK="✔"; CROSS="✖"; ARROW="➜"

info(){  echo -e "${CYAN}[deploy]${RESET} $*"; }
warn(){  echo -e "${YELLOW}[deploy]${RESET} $*" >&2; }
ok(){    echo -e "${GREEN}[deploy]${RESET} $*"; }
err(){   echo -e "${RED}[deploy]${RESET} $*" >&2; }
title(){ echo -e "\n${BOLD}${MAGENTA}== $* ==${RESET}"; }

kill_strays() {
  local pids
  pids="$(pgrep -f 'dotnet .*SpiffyOS\.Bot\.dll' || true)"
  if [[ -n "${pids}" ]]; then
    warn "Killing stray bot processes: ${pids}"
    kill ${pids} || true
    sleep 0.5
    pids="$(pgrep -f 'dotnet .*SpiffyOS\.Bot\.dll' || true)"
    if [[ -n "${pids}" ]]; then
      warn "Force killing remaining: ${pids}"
      kill -9 ${pids} || true
    fi
  fi
}

ensure_single_instance() {
  local count
  count="$(pgrep -fc 'dotnet .*SpiffyOS\.Bot\.dll' || true)"
  info "Running bot processes: ${count}"
  if [[ "${count}" != "1" ]]; then
    err "Expected exactly 1 bot process, got ${count}"
    pgrep -fa 'dotnet .*SpiffyOS\.Bot\.dll' || true
    exit 1
  fi
}

ensure_dirs() {
  # Derive service user/group from unit (fallback to 'spiff')
  local user group
  user="$(systemctl show -p User --value "${UNIT}" 2>/dev/null || echo spiff)"
  group="$(systemctl show -p Group --value "${UNIT}" 2>/dev/null || echo "$user")"
  [[ -z "$group" ]] && group="$user"

  for d in "$ROOT/config" "$ROOT/secrets" "$ROOT/logs" "$DATA_DIR"; do
    mkdir -p "$d"
    chown -R "$user:$group" "$d" || true
  done
  ok "Folders ensured (owned by ${user}:${group}): config, secrets, logs, data"
}

trap 'err "Deploy failed"; journalctl -u "${UNIT}" -n 80 --no-pager -o cat || true' ERR

title "Context"
info "Repo:   ${ROOT}"
info "Commit: $(git rev-parse --short HEAD)"
info "Config: ${ROOT}/config"
info "Tokens: ${ROOT}/secrets"
info "Logs:   ${ROOT}/logs"
info "Data:   ${DATA_DIR}"

title "Prepare"
info "${ARROW} Stopping service..."
systemctl stop "${UNIT}" || true

info "${ARROW} Killing any stray processes..."
kill_strays

info "${ARROW} Ensuring directories..."
ensure_dirs

title "Build & Publish"
info "${ARROW} dotnet restore"
dotnet restore

info "${ARROW} Publishing Bot → ${PUB_BOT}"
dotnet publish src/SpiffyOS.Bot -c Release -o "${PUB_BOT}"

info "${ARROW} Publishing Overlay → ${PUB_OVERLAY}"
dotnet publish src/SpiffyOS.Overlay -c Release -o "${PUB_OVERLAY}"

title "Start"
info "${ARROW} systemd reload & start"
systemctl daemon-reload || true
systemctl start "${UNIT}"

sleep 1
STATE="$(systemctl is-active "${UNIT}" || true)"
info "Bot service: ${STATE}"
if [[ "${STATE}" != "active" ]]; then
  err "Service failed to start. Recent journal:"
  journalctl -u "${UNIT}" -n 80 --no-pager -o cat || true
  exit 1
fi

ensure_single_instance

# Optional: quick overlay check if you have Nginx wired
if [[ -n "${OVERLAY_URL:-}" ]]; then
  info "${ARROW} Overlay check GET ${OVERLAY_URL}"
  curl -s -o /dev/null -w "${GREEN}[deploy]${RESET} Overlay HTTP %{http_code}\n" "${OVERLAY_URL}" || true
fi

ok "${TICK} Deploy complete."
