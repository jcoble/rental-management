#!/usr/bin/env bash
#
# Fire a Rental Command voice deep link at a connected Android device/emulator —
# simulating exactly what Google Assistant (App Actions) sends after it matches a
# spoken phrase. Use this to prove the voice → deep link → app bridge without
# needing Assistant/Play Store review (TSK-26).
#
# Usage:
#   ./scripts/voice-test.sh                 # runs through all the sample commands
#   ./scripts/voice-test.sh scan            # a single named command
#   ./scripts/voice-test.sh log-expense     # with sample params
#   ./scripts/voice-test.sh "rentalcommand://voice/log-expense?amount=40&category=plumbing&property=123%20Main"
#
# Requires: a running app (flutter run) and `adb` on PATH with one device.
set -euo pipefail

PKG="com.rentalcommand.rental_command"

fire() {
  local uri="$1"
  echo "→ $uri"
  adb shell am start -W -a android.intent.action.VIEW -d "$uri" "$PKG" \
    | grep -E "Status|Error|Warning" || true
  echo
}

# Map a short name to a representative deep link (with sample params).
resolve() {
  case "$1" in
    scan)        echo "rentalcommand://voice/scan" ;;
    log-expense) echo "rentalcommand://voice/log-expense?amount=40&category=plumbing&property=123%20Main" ;;
    overdue)     echo "rentalcommand://voice/overdue-rent" ;;
    work-orders) echo "rentalcommand://voice/work-orders?unit=4" ;;
    rentalcommand://*) echo "$1" ;;   # already a full URI
    *)           echo "" ;;
  esac
}

if [[ $# -eq 0 ]]; then
  echo "Firing all sample voice commands (2s apart)…"
  echo
  for name in scan log-expense overdue work-orders; do
    fire "$(resolve "$name")"
    sleep 2
  done
  exit 0
fi

uri="$(resolve "$1")"
if [[ -z "$uri" ]]; then
  echo "Unknown command '$1'. Try: scan | log-expense | overdue | work-orders" >&2
  echo "…or pass a full rentalcommand://voice/... URI." >&2
  exit 1
fi
fire "$uri"
