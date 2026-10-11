#!/bin/bash
# Stores one value in /etc/arsangaz/serveragent.env (0600 root). The value is read from standard input so it never
# appears in process arguments, shell history or logs. Called by Tools/ServerAgent/Set-ServerAgentValue.ps1 via sudo.
set -euo pipefail
ENV_FILE=/etc/arsangaz/serveragent.env
KEYS="TELEGRAM_BOT_TOKEN TELEGRAM_CHAT_ID CLOUD_RELAY_URL CLOUD_RELAY_TOKEN SERVERAGENT_TELEGRAM_POLLING SERVERAGENT_CAMPAIGNS"

if [ "${1:-}" = "--status" ]; then
  for key in $KEYS; do
    value=$(grep -E "^$key=" "$ENV_FILE" | tail -n1 | cut -d= -f2- || true)
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
  SERVERAGENT_*) [[ "$value" =~ ^[01]$ ]] || { echo "flag must be 0 or 1" >&2; exit 4; } ;;
esac

tmp=$(mktemp /etc/arsangaz/.serveragent.env.XXXXXX)
chmod 600 "$tmp"
grep -vE "^$key=" "$ENV_FILE" > "$tmp" || true
printf '%s=%s\n' "$key" "$value" >> "$tmp"
mv -f "$tmp" "$ENV_FILE"
chown root:root "$ENV_FILE"
chmod 600 "$ENV_FILE"
echo "$key saved"
