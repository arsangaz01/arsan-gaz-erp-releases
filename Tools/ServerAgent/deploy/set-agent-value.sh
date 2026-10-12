#!/bin/bash
# Stores one value in /etc/arsangaz/serveragent.env (0600 root), or CURSOR_API_KEY in /etc/arsangaz/cursoragent.env
# (0600 cursoragent, kept away from the server agent's secrets). The value is read from standard input so it never
# appears in process arguments, shell history or logs. Called by Tools/ServerAgent/Set-ServerAgentValue.ps1 via sudo.
set -euo pipefail
ENV_FILE=/etc/arsangaz/serveragent.env
CURSOR_ENV_FILE=/etc/arsangaz/cursoragent.env
KEYS="TELEGRAM_BOT_TOKEN TELEGRAM_CHAT_ID CLOUD_RELAY_URL CLOUD_RELAY_TOKEN SERVERAGENT_TELEGRAM_POLLING SERVERAGENT_CAMPAIGNS CURSOR_API_KEY"

file_for() { if [ "$1" = CURSOR_API_KEY ]; then echo "$CURSOR_ENV_FILE"; else echo "$ENV_FILE"; fi; }

if [ "${1:-}" = "--status" ]; then
  for key in $KEYS; do
    file=$(file_for "$key")
    value=$( { [ -f "$file" ] && grep -E "^$key=" "$file"; } | tail -n1 | cut -d= -f2- || true)
    case "$key" in
      SERVERAGENT_*) echo "$key=${value:-0}" ;;
      *) if [ -n "$value" ]; then echo "$key=set"; else echo "$key=empty"; fi ;;
    esac
  done
  exit 0
fi

key=${1:-}
case " $KEYS " in *" $key "*) ;; *) echo "unknown key" >&2; exit 2 ;; esac
IFS= read -r value || true
value=${value%$'\r'}
case "$key" in
  TELEGRAM_BOT_TOKEN) [[ "$value" =~ ^[0-9]{5,15}:[A-Za-z0-9_-]{30,60}$ ]] || { echo "token format invalid" >&2; exit 4; } ;;
  TELEGRAM_CHAT_ID) [[ "$value" =~ ^-?[0-9]{3,20}$ ]] || { echo "chat id invalid" >&2; exit 4; } ;;
  CLOUD_RELAY_URL) [[ "$value" =~ ^https://[A-Za-z0-9.-]+$ ]] || { echo "url invalid" >&2; exit 4; } ;;
  CLOUD_RELAY_TOKEN) [[ "$value" =~ ^[A-Za-z0-9._~+/=-]{16,512}$ ]] || { echo "relay token invalid" >&2; exit 4; } ;;
  # SDK-minted user keys are crsr_ + 64 hex; older dashboard keys used key_.
  CURSOR_API_KEY) [[ "$value" =~ ^(crsr|key)_[A-Za-z0-9_-]{20,250}$ ]] || { echo "cursor key format invalid (crsr_... bekleniyor)" >&2; exit 4; } ;;
  SERVERAGENT_*) [[ "$value" =~ ^[01]$ ]] || { echo "flag must be 0 or 1" >&2; exit 4; } ;;
esac

file=$(file_for "$key")
owner=root
[ "$key" = CURSOR_API_KEY ] && owner=cursoragent
id "$owner" >/dev/null 2>&1 || { echo "user $owner missing (run install-cursoragent.sh)" >&2; exit 5; }
touch "$file"
tmp=$(mktemp "$(dirname "$file")/.$(basename "$file").XXXXXX")
chmod 600 "$tmp"
grep -vE "^$key=" "$file" > "$tmp" || true
printf '%s=%s\n' "$key" "$value" >> "$tmp"
mv -f "$tmp" "$file"
chown "$owner:$owner" "$file"
chmod 600 "$file"
echo "$key saved"
