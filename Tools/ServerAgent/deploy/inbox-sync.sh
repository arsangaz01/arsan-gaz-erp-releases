#!/bin/bash
# Server half of the PC <-> server Cursor queue sync (PC half: Tools/CursorIdeBridge/Sync-ServerInbox.ps1).
# Runs as the arsangaz user over SSH. Contents travel base64-encoded so Turkish text survives any console code page.
set -euo pipefail
DATA=${ARSANGAZ_DATA_DIR:-/var/lib/arsangaz-data/prod}
INBOX="$DATA/ArsanGazERP/AgentWatcherData/CopilotInbox"
STATUS="$DATA/ArsanGazERP/ServerAgent/status.json"
umask 077
mkdir -p "$INBOX"

valid_id() { [[ "${1:-}" =~ ^[A-Za-z0-9_-]{1,80}$ ]] || { echo "invalid id" >&2; exit 3; }; }
is_done() {
  [ -e "$INBOX/$1.completion.json" ] && return 0
  [ -e "$INBOX/$1.ack.json" ] && grep -q '"failed"' "$INBOX/$1.ack.json" && return 0
  return 1
}

case "${1:-}" in
  poll)
    now=$(date -u +%Y-%m-%dT%H:%M:%SZ)
    printf '{"updatedUtc":"%s","host":"%s"}\n' "$now" "${2:-pc}" > "$INBOX/pc-sync-status.json.tmp"
    mv -f "$INBOX/pc-sync-status.json.tmp" "$INBOX/pc-sync-status.json"
    if [ -f "$STATUS" ] && grep -q '"telegram": true' "$STATUS"; then echo "TELEGRAM 1"; else echo "TELEGRAM 0"; fi
    for request in "$INBOX"/*.request.json; do
      [ -e "$request" ] || continue
      id=$(basename "$request" .request.json)
      is_done "$id" && continue
      # Claimed by the server Cursor agent (cursor-inbox.sh) while the PC was off; the server reports it.
      [ -e "$INBOX/$id.server-claim" ] && continue
      [[ "$id" == servertest-* ]] && continue
      if [ -e "$INBOX/$id.pc-forwarded" ]; then echo "OPEN $id"; else echo "NEW $id"; fi
    done
    ;;
  get)
    valid_id "${2:-}"
    base64 -w0 "$INBOX/$2.request.json"
    ;;
  file)
    # The path arrives base64-encoded (UTF-8) so non-ASCII file names survive the Windows ssh command line.
    path=$(realpath -e -- "$(printf '%s' "${2:-}" | base64 -d)")
    case "$path" in
      "$DATA"/*) base64 -w0 "$path" ;;
      *) echo "outside data dir" >&2; exit 3 ;;
    esac
    ;;
  mark)
    # Same lock as cursor-inbox.sh claim: prints MARKED when the PC owns the request, TAKEN when the server does.
    valid_id "${2:-}"
    exec 9>"$INBOX/.claim.lock"
    flock -w 10 9 || { echo "lock busy" >&2; exit 6; }
    if [ -e "$INBOX/$2.server-claim" ]; then echo "TAKEN"; exit 0; fi
    date -u +%Y-%m-%dT%H:%M:%SZ > "$INBOX/$2.pc-forwarded"
    echo "MARKED"
    ;;
  notified)
    valid_id "${2:-}"
    date -u +%Y-%m-%dT%H:%M:%SZ > "$INBOX/$2.mobile-notified"
    ;;
  put)
    valid_id "${2:-}"
    case "${3:-}" in completion|ack|progress|ide-claim) ;; *) echo "invalid kind" >&2; exit 3 ;; esac
    target="$INBOX/$2.$3.json"
    tr -d '\r\n' | base64 -d > "$target.tmp"
    [ -s "$target.tmp" ] || { rm -f "$target.tmp"; echo "empty content" >&2; exit 4; }
    mv -f "$target.tmp" "$target"
    ;;
  *)
    echo "usage: inbox-sync.sh poll|get|file|mark|notified|put" >&2
    exit 2
    ;;
esac
