#!/bin/bash
# One-time / idempotent server layout for production MobileApi, ServerAgent and the Cloudflare tunnel.
# Usage (from the uploaded deploy folder): sudo bash install-server.sh
# Does not start the tunnel and never overwrites existing secrets.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
PROD=/var/lib/arsangaz-data/prod

install -d -m 0750 -o arsangaz -g arsangaz /var/lib/arsangaz-data
install -d -m 0700 -o arsangaz -g arsangaz "$PROD" "$PROD/ArsanGazERP" "$PROD/ArsanGazERP/MobileApi" \
  "$PROD/ArsanGazERP/AgentWatcherData" "$PROD/ArsanGazERP/AgentWatcherData/CopilotInbox" \
  "$PROD/ArsanGazERP/AgentWatcherData/TelegramOutbox" "$PROD/ArsanGazERP/ServerAgent"

install -d -m 0755 -o root -g root /opt/arsangaz/bin
install -m 0755 -o root -g root "$HERE/inbox-sync.sh" /opt/arsangaz/bin/inbox-sync.sh
install -m 0700 -o root -g root "$HERE/set-agent-value.sh" /opt/arsangaz/bin/set-agent-value.sh

if [ ! -f /etc/arsangaz/serveragent.env ]; then
  umask 077
  cat > /etc/arsangaz/serveragent.env <<'EOF'
# ArsanGazERP sunucu ajanı. Gizli değerleri Tools/ServerAgent/Set-ServerAgentValue.ps1 ile yazın; elle düzenlemeyin.
# Telegram dinleme (getUpdates) yalnızca Switch-TelegramListener.ps1 ile açılır: aynı anda tek dinleyici olabilir.
SERVERAGENT_TELEGRAM_POLLING=0
SERVERAGENT_CAMPAIGNS=0
SERVERAGENT_OUTBOX=1
SERVERAGENT_REMINDERS=1
CLOUD_RELAY_URL=https://arsangaz-erp-cloud-agent.sewser-01.workers.dev
EOF
  chown root:root /etc/arsangaz/serveragent.env
  chmod 600 /etc/arsangaz/serveragent.env
fi

install -m 0644 -o root -g root "$HERE/arsangaz-mobileapi-prod.service" /etc/systemd/system/arsangaz-mobileapi-prod.service
install -m 0644 -o root -g root "$HERE/arsangaz-serveragent.service" /etc/systemd/system/arsangaz-serveragent.service
install -m 0644 -o root -g root "$HERE/cloudflared.service" /etc/systemd/system/cloudflared.service
install -d -m 0700 -o root -g root /etc/cloudflared
install -m 0600 -o root -g root "$HERE/cloudflared-config.yml" /etc/cloudflared/config.yml
systemctl daemon-reload
echo "install-server: OK"
