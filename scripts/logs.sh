#!/usr/bin/env bash
# Simple log helper for SpiffyOS
# Usage:
#   scripts/logs.sh bot                 # tail latest bot file log (default -n 200)
#   scripts/logs.sh overlay             # tail overlay (file if present, else journal)
#   scripts/logs.sh journal             # follow bot journal (stdout/stderr)
#   scripts/logs.sh journal-overlay     # follow overlay journal
#   scripts/logs.sh recent              # show recent interesting bot lines
# Options:
#   -n N              number of lines (default 200)
#   -f, --filter REGEX  grep filter (live)

set -Eeuo pipefail

SCRIPT_DIR="$(cd -- "$(dirname "$0")" >/dev/null 2>&1 && pwd -P)"
ROOT="${SPIFFYOS_ROOT:-"$(cd "$SCRIPT_DIR/.." && pwd)"}"
LOGDIR="${SPIFFYOS_LOGS:-"$ROOT/logs"}"

MODE="${1:-bot}"
shift || true

N=200
FILTER=""
while (( "$#" )); do
  case "$1" in
    -n) N="$2"; shift 2 ;;
    -f|--filter) FILTER="$2"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; echo "Try: $(basename "$0") {bot|overlay|journal|journal-overlay|recent} [-n N] [--filter 'regex']" >&2; exit 2 ;;
  esac
done

apply_filter() {
  if [[ -n "$FILTER" ]]; then grep --line-buffered -E "$FILTER"; else cat; fi
}

latest_file() {
  ls -1t "$LOGDIR"/"$1"-*.log 2>/dev/null | head -n1 || true
}

case "$MODE" in
  bot)
    FILE="$(latest_file bot)"
    if [[ -n "$FILE" ]]; then
      tail -n "$N" -F "$FILE" | apply_filter
    else
      echo "No bot file logs found in $LOGDIR" >&2
      echo "Try: $0 journal" >&2
      exit 1
    fi
    ;;
  overlay)
    FILE="$(latest_file overlay)"
    if [[ -n "$FILE" ]]; then
      tail -n "$N" -F "$FILE" | apply_filter
    else
      echo "No overlay file logs in $LOGDIR — falling back to journalctl…" >&2
      journalctl -u spiffyos-overlay -n "$N" -f --output=cat | apply_filter
    fi
    ;;
  journal)
    journalctl -u spiffyos-bot -n "$N" -f --output=cat | apply_filter
    ;;
  journal-overlay)
    journalctl -u spiffyos-overlay -n "$N" -f --output=cat | apply_filter
    ;;
  recent)
    FILE="$(latest_file bot)"
    if [[ -z "$FILE" ]]; then echo "No bot file logs." >&2; exit 1; fi
    grep -E "EventSub create OK|Command used|Follow|Sub|Redemption|Raid|Bits|heartbeat" "$FILE" | tail -n "$N"
    ;;
  *)
    echo "Usage: $(basename "$0") {bot|overlay|journal|journal-overlay|recent} [-n N] [--filter 'regex']" >&2
    exit 2
    ;;
esac
