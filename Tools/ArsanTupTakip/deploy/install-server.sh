#!/usr/bin/env bash
# ArsanTüpTakip sunucu kurulumu / güncellemesi (root, tekrar çalıştırılabilir).
#   sudo bash install-server.sh <paket-klasörü>
# Paket klasöründe api/ (dotnet publish çıktısı, linux-arm64) ve deploy/ dosyaları bulunur (Deploy-ArsanTupTakip.ps1 hazırlar).
# ArsanGazERP'nin hiçbir servisine, veritabanına, cloudflared ayarına veya arsangaz-release düzenine dokunmaz.
# Yönetici hesabı OLUŞTURMAZ; giriş/bağlantılar ayrıca yapılacak (README).
set -euo pipefail

pkg=${1:?paket klasörü gerekli}
user=arsantuptakip
role=arsantuptakip_app
db=arsantuptakip
app_dir=/opt/arsantuptakip/api
data_dir=/var/lib/arsantuptakip
conf_dir=/etc/arsantuptakip
env_file=$conf_dir/api.env
port=5080
health="http://127.0.0.1:$port/tuptakip/api/merhaba"

[ "$(id -u)" -eq 0 ] || { echo "root olarak çalıştırın" >&2; exit 1; }
[ -f "$pkg/api/ArsanTupTakip.Api.dll" ] || { echo "$pkg/api/ArsanTupTakip.Api.dll yok" >&2; exit 1; }
/usr/bin/dotnet --list-runtimes | grep -q '^Microsoft.AspNetCore.App 10\.' || { echo "ASP.NET Core 10 çalışma zamanı yok" >&2; exit 1; }
if ss -ltnH "sport = :$port" | grep -q . && ! systemctl is-active --quiet arsantuptakip-api; then
    echo "$port portu başka bir süreç tarafından kullanılıyor; kurulum durduruldu." >&2
    exit 1
fi

echo "== Linux kullanıcısı ve klasörler"
if ! id "$user" >/dev/null 2>&1; then
    useradd --system --home-dir "$data_dir" --no-create-home --shell /usr/sbin/nologin "$user"
fi
install -d -m 755 -o root -g root /opt/arsantuptakip
install -d -m 700 -o "$user" -g "$user" "$data_dir"
install -d -m 700 -o root -g root "$conf_dir"
install -d -m 700 -o root -g root /var/backups/arsantuptakip /var/lib/arsantuptakip-backup

echo "== Veritabanı rolü ve veritabanı"
if [ -f "$env_file" ] && grep -q '^TUPTAKIP_PG_CONNECTION=' "$env_file"; then
    password=$(sed -n 's/^TUPTAKIP_PG_CONNECTION=.*Password=\([0-9a-f]*\).*/\1/p' "$env_file")
fi
if [ -z "${password:-}" ]; then
    password=$(openssl rand -hex 24)
fi
sql=$(mktemp)
chmod 600 "$sql"
cat > "$sql" <<SQL
\set ON_ERROR_STOP on
SELECT format('CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION CONNECTION LIMIT 20', '$role')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$role') \gexec
ALTER ROLE $role WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 20 PASSWORD '$password';
SELECT format('CREATE DATABASE %I OWNER %I ENCODING ''UTF8'' TEMPLATE template0', '$db', '$role')
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '$db') \gexec
REVOKE ALL ON DATABASE $db FROM PUBLIC;
GRANT CONNECT, TEMPORARY, CREATE ON DATABASE $db TO $role;
SQL
chown postgres "$sql"
runuser -u postgres -- psql -X -q -d postgres -f "$sql"
rm -f "$sql"

umask 077
tmp_env=$(mktemp)
cat > "$tmp_env" <<ENV
# ArsanTüpTakip API ortamı (root:root 600). Veritabanı şifresi yalnızca burada durur.
TUPTAKIP_PG_CONNECTION="Host=127.0.0.1;Port=5432;Database=$db;Username=$role;Password=$password"
TZ=Europe/Istanbul
ENV
install -m 600 -o root -g root "$tmp_env" "$env_file"
rm -f "$tmp_env"
unset password

echo "== pg_hba: yalnızca $role için kurallar (yalnızca 127.0.0.1 -> $db)"
hba=$(runuser -u postgres -- psql -X -At -d postgres -c 'SHOW hba_file')
marker="# ArsanTupTakip BEGIN"
if ! grep -qF "$marker" "$hba"; then
    backup="$hba.arsantuptakip-$(date +%Y%m%d%H%M%S).bak"
    cp -p "$hba" "$backup"
    block=$(mktemp)
    cat > "$block" <<HBA
$marker (Tools/ArsanTupTakip/deploy/install-server.sh) - yalnızca bu rolü etkiler
host    $db    $role    127.0.0.1/32    scram-sha-256
local   all    $role                    reject
host    all    $role    0.0.0.0/0       reject
host    all    $role    ::/0            reject
# ArsanTupTakip END
HBA
    first=$(grep -nvE '^\s*(#|$)' "$hba" | head -n 1 | cut -d: -f1)
    merged=$(mktemp)
    awk -v n="${first:-0}" -v f="$block" 'NR == n { while ((getline line < f) > 0) print line } { print } END { if (n == 0) while ((getline line < f) > 0) print line }' "$hba" > "$merged"
    cat "$merged" > "$hba"
    rm -f "$block" "$merged"
    errors=$(runuser -u postgres -- psql -X -At -d postgres -c 'SELECT count(*) FROM pg_hba_file_rules WHERE error IS NOT NULL')
    if [ "$errors" != "0" ]; then
        cp -p "$backup" "$hba"
        echo "pg_hba doğrulaması başarısız; yedek geri yüklendi, kurulum durduruldu." >&2
        exit 1
    fi
    runuser -u postgres -- psql -X -q -d postgres -c 'SELECT pg_reload_conf()' >/dev/null
    echo "pg_hba güncellendi (yedek: $backup)"
else
    echo "pg_hba kuralları zaten var"
fi

echo "== Uygulama dosyaları"
rm -rf "$app_dir.new"
cp -a "$pkg/api" "$app_dir.new"
chown -R root:root "$app_dir.new"
chmod -R u=rwX,go=rX "$app_dir.new"
had_previous=false
if [ -d "$app_dir" ]; then
    rm -rf "$app_dir.prev"
    mv "$app_dir" "$app_dir.prev"
    had_previous=true
fi
mv "$app_dir.new" "$app_dir"

echo "== systemd servisi"
install -m 644 -o root -g root "$pkg/deploy/arsantuptakip-api.service" /etc/systemd/system/arsantuptakip-api.service
systemctl daemon-reload
systemctl enable arsantuptakip-api >/dev/null 2>&1
systemctl restart arsantuptakip-api

ok=false
for _ in $(seq 1 30); do
    if curl -fsS --max-time 3 "$health" >/dev/null 2>&1; then ok=true; break; fi
    sleep 1
done
if [ "$ok" != true ]; then
    echo "Sağlık kontrolü başarısız ($health)." >&2
    journalctl -u arsantuptakip-api -n 30 --no-pager >&2 || true
    if [ "$had_previous" = true ]; then
        rm -rf "$app_dir"
        mv "$app_dir.prev" "$app_dir"
        systemctl restart arsantuptakip-api || true
        echo "Önceki sürüme geri dönüldü." >&2
    fi
    exit 1
fi
echo "Servis çalışıyor: $(curl -fsS "$health")"

echo "== Gece yedeği"
install -m 750 -o root -g root "$pkg/deploy/arsantuptakip-backup" /usr/local/sbin/arsantuptakip-backup
install -m 644 -o root -g root "$pkg/deploy/arsantuptakip-backup.service" /etc/systemd/system/arsantuptakip-backup.service
install -m 644 -o root -g root "$pkg/deploy/arsantuptakip-backup.timer" /etc/systemd/system/arsantuptakip-backup.timer
systemctl daemon-reload
systemctl enable --now arsantuptakip-backup.timer >/dev/null 2>&1
echo "Kurulum tamam."
