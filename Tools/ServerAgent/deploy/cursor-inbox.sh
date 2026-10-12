#!/bin/bash
# The server Cursor agent's only door into the production Cursor queue. The cursoragent user runs it as arsangaz
# through "sudo -n -u arsangaz" (/etc/sudoers.d/arsangaz-cursoragent) and never reads the data directory itself:
# it sees queued requests, the attachments of requests it claimed, and writes results only for those requests.
# "claim" here and "mark" in inbox-sync.sh (PC half) share one lock, so every request goes to exactly one side.
set -euo pipefail
DATA=${ARSANGAZ_DATA_DIR:-/var/lib/arsangaz-data/prod}
ROOT="$DATA/ArsanGazERP"
INBOX="$ROOT/AgentWatcherData/CopilotInbox"
umask 077
[ -d "$INBOX" ] || { echo "inbox missing" >&2; exit 3; }

valid_id() { [[ "${1:-}" =~ ^[A-Za-z0-9_-]{1,80}$ ]] || { echo "invalid id" >&2; exit 3; }; }
is_done() {
  [ -e "$INBOX/$1.completion.json" ] && return 0
  [ -e "$INBOX/$1.ack.json" ] && grep -q '"failed"' "$INBOX/$1.ack.json" && return 0
  return 1
}
require_claim() { [ -e "$INBOX/$1.server-claim" ] || { echo "not claimed by the server" >&2; exit 5; }; }
lock() { exec 9>"$INBOX/.claim.lock"; flock -w 10 9 || { echo "lock busy" >&2; exit 6; }; }
age_of() { echo $(( $(date +%s) - $(stat -c %Y "$1") )); }

case "${1:-}" in
  list)
    # "PC <seconds since the PC sync heartbeat, -1 if never>", then claimable requests and the server's own claims.
    pc_age=-1
    if [ -f "$INBOX/pc-sync-status.json" ]; then
      updated=$(jq -r '.updatedUtc // empty' "$INBOX/pc-sync-status.json" 2>/dev/null || true)
      if [ -n "$updated" ] && seen=$(date -u -d "$updated" +%s 2>/dev/null); then pc_age=$(( $(date +%s) - seen )); fi
    fi
    echo "PC $pc_age"
    for request in "$INBOX"/*.request.json; do
      [ -e "$request" ] || continue
      id=$(basename "$request" .request.json)
      [[ "$id" =~ ^[A-Za-z0-9_-]{1,80}$ ]] || continue
      is_done "$id" && continue
      if [ -e "$INBOX/$id.server-claim" ]; then echo "MINE $id"; continue; fi
      [ -e "$INBOX/$id.pc-forwarded" ] && continue
      echo "REQ $id $(age_of "$request")"
    done
    ;;
  claim)
    valid_id "${2:-}"
    lock
    if [ ! -e "$INBOX/$2.request.json" ]; then echo "GONE"; exit 0; fi
    if is_done "$2" || [ -e "$INBOX/$2.pc-forwarded" ] || [ -e "$INBOX/$2.server-claim" ]; then echo "TAKEN"; exit 0; fi
    printf '{"host":"%s","claimedUtc":"%s"}\n' "$(hostname)" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$INBOX/$2.server-claim"
    echo "CLAIMED"
    ;;
  release)
    # Gives a claimed request back (e.g. the workspace could not be prepared) as long as no result was written.
    valid_id "${2:-}"
    lock
    if [ -e "$INBOX/$2.server-claim" ] && ! is_done "$2" && [ ! -e "$INBOX/$2.ack.json" ]; then
      rm -f "$INBOX/$2.server-claim" "$INBOX/$2.progress.json"
      echo "RELEASED"
    else
      echo "KEPT"
    fi
    ;;
  get)
    valid_id "${2:-}"
    require_claim "$2"
    base64 -w0 "$INBOX/$2.request.json"
    ;;
  file)
    # Only attachments listed in the claimed request, and only from the two attachment folders.
    valid_id "${2:-}"
    require_claim "$2"
    wanted=$(printf '%s' "${3:-}" | base64 -d)
    listed=$(jq -r '(.Attachments // .attachments // [])[] | (.LocalPath // .localPath // empty)' "$INBOX/$2.request.json")
    grep -Fxq -- "$wanted" <<< "$listed" || { echo "not an attachment of this request" >&2; exit 3; }
    path=$(realpath -e -- "$wanted")
    case "$path" in
      "$ROOT/AgentWatcher/telegram-attachments/"*|"$ROOT/AgentWatcherData/mobile-attachments/"*|"$ROOT/AgentWatcherData/telegram-attachments/"*)
        base64 -w0 "$path" ;;
      *) echo "outside attachment folders" >&2; exit 3 ;;
    esac
    ;;
  cancelled)
    valid_id "${2:-}"
    if [ -e "$INBOX/$2.cancel.json" ]; then echo 1; else echo 0; fi
    ;;
  put)
    valid_id "${2:-}"
    require_claim "$2"
    case "${3:-}" in completion|ack|progress) ;; *) echo "invalid kind" >&2; exit 3 ;; esac
    target="$INBOX/$2.$3.json"
    tr -d '\r\n' | base64 -d > "$target.tmp"
    [ -s "$target.tmp" ] || { rm -f "$target.tmp"; echo "empty content" >&2; exit 4; }
    jq -e . "$target.tmp" > /dev/null || { rm -f "$target.tmp"; echo "not json" >&2; exit 4; }
    mv -f "$target.tmp" "$target"
    ;;
  *)
    echo "usage: cursor-inbox.sh list|claim|release|get|file|cancelled|put" >&2
    exit 2
    ;;
esac
