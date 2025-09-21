#!/usr/bin/env bash
set -Eeuo pipefail
ROOT="/srv/bots/spiffyos"
LOGDIR="$ROOT/logs"
BOTLOG="$LOGDIR/bot-$(date -u +%Y%m%d).log"
OVERLAYLOG="$LOGDIR/overlay-$(date -u +%Y%m%d).log"
UNIT="spiffybot.service"

echo
echo "== Paths =="
echo "ROOT=$ROOT"
echo "LOGDIR=$LOGDIR"
echo "BOTLOG=$BOTLOG"
echo "OVERLAYLOG=$OVERLAYLOG"

echo
echo "== Services =="
if systemctl is-active --quiet "$UNIT"; then
  echo "✔ $UNIT active"
else
  echo "✖ $UNIT not active"
fi

echo
echo "== Duplicate process check =="
COUNT="$(pgrep -fc 'dotnet .*SpiffyOS\.Bot\.dll' || true)"
if [[ "$COUNT" == "0" ]]; then
  echo "✖ no bot process"
elif [[ "$COUNT" == "1" ]]; then
  echo "✔ single bot process"
else
  echo "✖ DUPLICATE BOT PROCESSES ($COUNT):"
  pgrep -fa 'dotnet .*SpiffyOS\.Bot\.dll' || true
fi

echo
echo "== Recent log sanity (bot) =="
if [[ -f "$BOTLOG" ]]; then
  grep -E 'CommandRouter loaded|Application started|Bot heartbeat OK' "$BOTLOG" | tail -n 5 || true
else
  echo "(no bot log yet)"
fi

echo
echo "== Conclusion =="
if systemctl is-active --quiet "$UNIT" && [[ "$COUNT" == "1" ]]; then
  echo "✔ Healthy: service active and single instance"
else
  echo "✖ Attention needed (see sections above)"
fi

