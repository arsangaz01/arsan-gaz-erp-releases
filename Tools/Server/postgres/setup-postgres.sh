#!/usr/bin/env bash
# Arsan Gaz ERP - PostgreSQL kurulumu (Ubuntu 24.04, Oracle Cloud).
# Tekrar calistirilabilir: mevcut parola ve veritabani korunur.
# Kullanim: sudo bash setup-postgres.sh [TAILSCALE_IP]
set -euo pipefail

TS_IP="${1:-$(ip -4 -o addr show tailscale0 | awk '{print $4}' | cut -d/ -f1)}"
[[ -n "$TS_IP" ]] || { echo "Tailscale IP bulunamadi" >&2; exit 1; }
APP_ROLE=arsangaz_app
APP_DB=arsangaz
ENV_FILE=/etc/arsangaz/postgres.env

export DEBIAN_FRONTEND=noninteractive
if ! command -v psql >/dev/null; then
  apt-get update -qq
  apt-get install -y -qq postgresql postgresql-contrib >/dev/null
fi
PG_VER=$(ls /etc/postgresql | sort -V | tail -1)
CONF_DIR=/etc/postgresql/$PG_VER/main

# Tailscale adresi acilista PostgreSQL'den sonra gelebilir; yerel olmayan adrese baglanmaya izin ver.
echo 'net.ipv4.ip_nonlocal_bind = 1' > /etc/sysctl.d/60-arsangaz-postgres.conf
sysctl -q -p /etc/sysctl.d/60-arsangaz-postgres.conf

cat > "$CONF_DIR/conf.d/arsangaz.conf" <<EOF
listen_addresses = '127.0.0.1,$TS_IP'
port = 5432
password_encryption = scram-sha-256
max_connections = 100
shared_buffers = 512MB
effective_cache_size = 4GB
timezone = 'UTC'
log_timezone = 'UTC'
ssl = on
EOF

cat > "$CONF_DIR/pg_hba.conf" <<'EOF'
# Arsan Gaz ERP: yalnizca yerel soket, localhost ve Tailscale (100.64.0.0/10), scram-sha-256.
local   all             postgres                                peer
local   all             all                                     peer
host    all             all             127.0.0.1/32            scram-sha-256
host    all             all             ::1/128                 scram-sha-256
host    all             all             100.64.0.0/10           scram-sha-256
EOF

mkdir -p /etc/systemd/system/postgresql@.service.d
cat > /etc/systemd/system/postgresql@.service.d/arsangaz.conf <<'EOF'
[Unit]
After=tailscaled.service network-online.target
Wants=network-online.target
EOF
systemctl daemon-reload
systemctl enable --now postgresql >/dev/null
systemctl restart "postgresql@$PG_VER-main"

# Uygulama rolu ve parola (yalnizca ilk kurulumda uretilir, ekrana yazilmaz).
install -d -m 0750 -o root -g arsangaz /etc/arsangaz
if [[ ! -s "$ENV_FILE" ]]; then
  PASS=$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 40)
  umask 077
  cat > "$ENV_FILE" <<EOF
# PostgreSQL baglantisi (gizli). Yalnizca arsangaz kullanicisi okuyabilir.
ARSANGAZ_PG_CONNECTION=Host=127.0.0.1;Port=5432;Database=$APP_DB;Username=$APP_ROLE;Password=$PASS
EOF
  chown arsangaz:arsangaz "$ENV_FILE"; chmod 0600 "$ENV_FILE"
fi
PASS=$(sed -n 's/.*Password=\([^;]*\).*/\1/p' "$ENV_FILE")

# Parola komut satirina degil stdin'e yazilir (ps ciktisinda gorunmez).
sudo -u postgres psql -v ON_ERROR_STOP=1 -q <<SQL
SELECT 'CREATE ROLE $APP_ROLE LOGIN' WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$APP_ROLE') \gexec
ALTER ROLE $APP_ROLE WITH LOGIN PASSWORD '$PASS' NOSUPERUSER NOCREATEDB NOCREATEROLE;
SQL
if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='$APP_DB'" | grep -q 1; then
  # C siralamasi SQLite BINARY ile ayni sonuclari verir (ISO tarih metinleri, ORDER BY); C.UTF-8 ctype lower/ILIKE icin Unicode.
  sudo -u postgres psql -v ON_ERROR_STOP=1 -q -c "CREATE DATABASE $APP_DB OWNER $APP_ROLE ENCODING 'UTF8' LC_COLLATE 'C' LC_CTYPE 'C.UTF-8' TEMPLATE template0"
fi
sudo -u postgres psql -v ON_ERROR_STOP=1 -q -d "$APP_DB" <<SQL
REVOKE ALL ON DATABASE $APP_DB FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE $APP_DB TO $APP_ROLE;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
CREATE SCHEMA IF NOT EXISTS erp AUTHORIZATION $APP_ROLE;
CREATE SCHEMA IF NOT EXISTS crm AUTHORIZATION $APP_ROLE;
ALTER ROLE $APP_ROLE IN DATABASE $APP_DB SET search_path = erp, public;
SQL

# Guvenlik duvari: 5432 yalnizca tailscale0 uzerinden. Kalici kurallar /etc/iptables/rules.v4 icinde;
# netfilter-persistent save kullanilmaz (Tailscale'in calisma zamani zincirlerini dosyaya yazardi).
RULE='-A INPUT -i tailscale0 -p tcp -m tcp --dport 5432 -j ACCEPT'
if ! grep -qF -- "$RULE" /etc/iptables/rules.v4; then
  cp /etc/iptables/rules.v4 "/etc/iptables/rules.v4.bak-$(date +%Y%m%d%H%M%S)"
  sed -i "/--dport 22 -j ACCEPT/a $RULE" /etc/iptables/rules.v4
fi
iptables -C INPUT -i tailscale0 -p tcp -m tcp --dport 5432 -j ACCEPT 2>/dev/null || {
  POS=$(iptables -L INPUT --line-numbers | awk '/REJECT/ {print $1; exit}')
  iptables -I INPUT "${POS:-1}" -i tailscale0 -p tcp -m tcp --dport 5432 -j ACCEPT
}
iptables-restore --test < /etc/iptables/rules.v4

echo "PostgreSQL $PG_VER hazir: 127.0.0.1 ve $TS_IP:5432, veritabani $APP_DB (semalar erp, crm), rol $APP_ROLE."
