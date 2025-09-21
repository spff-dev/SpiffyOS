#!/usr/bin/env bash
# Usage: sudo scripts/botctl.sh {status|start|stop|restart|logs|ps|kill-strays}
set -Eeuo pipefail
UNIT="spiffybot.service"

case "${1:-}" in
  status)       systemctl status "$UNIT" --no-pager -l ;;
  start)        systemctl start "$UNIT" ;;
  stop)         systemctl stop "$UNIT" ;;
  restart)      systemctl restart "$UNIT" ;;
  logs)         journalctl -u "$UNIT" -n 200 -f -o cat ;;
  ps)           pgrep -fa 'dotnet .*SpiffyOS\.Bot\.dll' || echo "no bot process" ;;
  kill-strays)  pkill -f 'dotnet .*SpiffyOS\.Bot\.dll' || true ;;
  *)
    echo "usage: sudo $0 {status|start|stop|restart|logs|ps|kill-strays}"
    exit 1
  ;;
esac
