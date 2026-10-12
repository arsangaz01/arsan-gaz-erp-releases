#!/bin/bash
# Tests cursor-inbox.sh (server Cursor agent) against inbox-sync.sh (PC sync) in a temporary data folder:
# claim/mark exclusivity (also under concurrent calls), PC heartbeat age, servertest ids, claim-only writes.
# Usage: bash test-cursor-queue.sh [folder with both scripts]   (never touches the production queue)
set -uo pipefail
HERE=${1:-$(cd "$(dirname "$0")" && pwd)}
export ARSANGAZ_DATA_DIR=$(mktemp -d)
trap 'rm -rf "$ARSANGAZ_DATA_DIR"' EXIT
INBOX="$ARSANGAZ_DATA_DIR/ArsanGazERP/AgentWatcherData/CopilotInbox"
ATT="$ARSANGAZ_DATA_DIR/ArsanGazERP/AgentWatcherData/mobile-attachments"
mkdir -p "$INBOX" "$ATT" "$ARSANGAZ_DATA_DIR/ArsanGazERP/ServerAgent"
SRV="bash $HERE/cursor-inbox.sh"
PC="bash $HERE/inbox-sync.sh"
failures=0
check() { if [ "$2" = "$3" ]; then echo "ok   $1"; else echo "FAIL $1: expected '$3', got '$2'"; failures=$((failures + 1)); fi; }
request() { printf '{"Id":"%s","Text":"test","TaskId":"%s"%s}' "$1" "$1" "${2:-}" > "$INBOX/$1.request.json"; }

# PC heartbeat: never synced, then fresh via poll, then 5 minutes old.
check "PC age before any sync" "$($SRV list | head -n1)" "PC -1"
$PC poll test-pc > /dev/null
age=$($SRV list | head -n1 | cut -d' ' -f2)
check "PC age right after poll is small" "$([ "$age" -ge 0 ] && [ "$age" -lt 5 ] && echo yes)" "yes"
printf '{"updatedUtc":"%s","host":"pc"}\n' "$(date -u -d '-5 min' +%Y-%m-%dT%H:%M:%SZ)" > "$INBOX/pc-sync-status.json"
age=$($SRV list | head -n1 | cut -d' ' -f2)
check "PC age after 5 minutes offline" "$([ "$age" -ge 299 ] && [ "$age" -lt 310 ] && echo yes)" "yes"

# Server claims first: PC mark is refused and poll no longer offers it.
request a1
check "a1 listed as claimable" "$($SRV list | grep -c '^REQ a1 ')" "1"
check "server claims a1" "$($SRV claim a1)" "CLAIMED"
check "PC mark of a1 refused" "$($PC mark a1)" "TAKEN"
check "PC poll hides a1" "$($PC poll pc | grep -c ' a1$')" "0"
check "a1 listed as MINE" "$($SRV list | grep -c '^MINE a1$')" "1"
check "second claim of a1" "$($SRV claim a1)" "TAKEN"
check "a1 not forwarded to PC" "$([ -e "$INBOX/a1.pc-forwarded" ] && echo yes || echo no)" "no"

# PC marks first: server claim is refused and the request is not listed.
request b1
check "PC marks b1" "$($PC mark b1)" "MARKED"
check "server claim of b1 refused" "$($SRV claim b1)" "TAKEN"
check "b1 not listed for server" "$($SRV list | grep -c ' b1')" "0"
check "server cannot write results for b1" "$(printf '{}' | base64 -w0 | $SRV put b1 completion 2>/dev/null; echo $?)" "5"
check "server cannot read b1" "$($SRV get b1 > /dev/null 2>&1; echo $?)" "5"

# Results, release, done requests.
check "server writes a1 completion" "$(printf '{"id":"a1","taskId":"a1","status":"completed","report":"tamam"}' | base64 -w0 | $SRV put a1 completion; echo $?)" "0"
check "invalid JSON rejected" "$(printf 'x' | base64 -w0 | $SRV put a1 progress 2>/dev/null; echo $?)" "4"
check "done a1 no longer listed" "$($SRV list | grep -c ' a1')" "0"
check "done a1 cannot be released" "$($SRV release a1)" "KEPT"
request c1
$SRV claim c1 > /dev/null
check "unfinished c1 released" "$($SRV release c1)" "RELEASED"
check "released c1 claimable again" "$($SRV list | grep -c '^REQ c1 ')" "1"

# Attachments: only listed files inside the attachment folders.
printf 'ek' > "$ATT/doc.txt"; printf 'gizli' > "$INBOX/../settings-copy.json"
request d1 ",\"Attachments\":[{\"LocalPath\":\"$ATT/doc.txt\",\"FileName\":\"doc.txt\"}]"
$SRV claim d1 > /dev/null
check "listed attachment readable" "$($SRV file d1 "$(printf '%s' "$ATT/doc.txt" | base64 -w0)" | base64 -d)" "ek"
check "unlisted file refused" "$($SRV file d1 "$(printf '%s' "$INBOX/../settings-copy.json" | base64 -w0)" > /dev/null 2>&1; echo $?)" "3"

# servertest ids are never offered to the PC.
request servertest-1
check "PC poll hides servertest ids" "$($PC poll pc | grep -c servertest)" "0"
check "server lists servertest ids" "$($SRV list | grep -c '^REQ servertest-1 ')" "1"

# Concurrent claim vs mark: exactly one side wins every time.
double=0; none=0
for i in $(seq 1 40); do
  request "race$i"
  $SRV claim "race$i" > "$ARSANGAZ_DATA_DIR/s$i" & $PC mark "race$i" > "$ARSANGAZ_DATA_DIR/p$i" & wait
  s=$(cat "$ARSANGAZ_DATA_DIR/s$i"); p=$(cat "$ARSANGAZ_DATA_DIR/p$i")
  if [ "$s" = CLAIMED ] && [ "$p" = MARKED ]; then double=$((double + 1)); fi
  if [ "$s" != CLAIMED ] && [ "$p" != MARKED ]; then none=$((none + 1)); fi
done
check "40 concurrent races: no double processing" "$double" "0"
check "40 concurrent races: always one owner" "$none" "0"

echo "failures=$failures"
exit $failures
