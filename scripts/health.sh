#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LOGDIR="${SPIFFYOS_LOGS:-$ROOT/logs}"
BOTLOG="$(ls -1 "$LOGDIR"/bot-*.log 2>/dev/null | tail -n1 || true)"
OVERLAYLOG="$(ls -1 "$LOGDIR"/overlay-*.log 2>/dev/null | tail -n1 || true)"

red()  { printf "\033[31m%s\033[0m\n" "$*"; }
grn()  { printf "\033[32m%s\033[0m\n" "$*"; }
ylw()  { printf "\033[33m%s\033[0m\n" "$*"; }
hdr()  { printf "\n\033[36m== %s ==\033[0m\n" "$*"; }

ok()   { grn "✔ $*"; }
bad()  { red "✖ $*"; exit 1; }

hdr "Paths"
echo "ROOT=$ROOT"
echo "LOGDIR=$LOGDIR"
[ -n "$BOTLOG" ] && echo "BOTLOG=$BOTLOG" || ylw "No bot log files found."
[ -n "$OVERLAYLOG" ] && echo "OVERLAYLOG=$OVERLAYLOG" || ylw "No overlay log files found."

hdr "Services"
if command -v systemctl >/dev/null 2>&1; then
  systemctl is-active --quiet spiffybot.service    && ok "spiffybot.service active"    || bad "spiffybot.service not active"
  systemctl is-active --quiet spiffyoverlay.service && ok "spiffyoverlay.service active" || ylw "spiffyoverlay.service not active (overlay optional)"
else
  ylw "systemctl not found (skipping service checks)"
fi

hdr "Recent log sanity (bot)"
[ -n "$BOTLOG" ] || bad "No bot log present"
grep -q "CommandRouter loaded" "$BOTLOG" && ok "Commands loaded" || bad "No 'CommandRouter loaded' line"
grep -q "Application started" "$BOTLOG"  && ok "Application started" || bad "No 'Application started' line"
grep -q "Bot heartbeat OK" "$BOTLOG"     && ok "Heartbeat present" || bad "No heartbeat lines seen"

hdr "EventSub subscriptions"
need=("channel.chat.message v1" "channel.follow v2" "channel.subscribe v1" "channel.subscription.message v1" "channel.channel_points_custom_reward_redemption.add v1" "channel.cheer v1" "channel.raid v1")
missing=0
for n in "${need[@]}"; do
  if grep -q "EventSub create OK: $n" "$BOTLOG"; then ok "$n"; else ylw "Missing: $n"; missing=$((missing+1)); fi
done
[ $missing -le 2 ] && ok "EventSub looks good (<=2 optional missing)" || ylw "EventSub: several missing (may be by design)"

hdr "Overlay (optional)"
if [ -n "${OVERLAY_URL:-}" ]; then
  code=$(curl -s -o /dev/null -w "%{http_code}" ${OVERLAY_TOKEN:+-H "Authorization: Bearer $OVERLAY_TOKEN"} "$OVERLAY_URL")
  [ "$code" = "200" ] && ok "Overlay reachable ($code)" || ylw "Overlay check returned $code"
else
  ylw "Set OVERLAY_URL to test overlay (and OVERLAY_TOKEN if protected)."
fi

hdr "Conclusion"
ok "Health checks completed."
