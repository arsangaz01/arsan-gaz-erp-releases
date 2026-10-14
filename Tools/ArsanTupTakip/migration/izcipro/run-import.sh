#!/bin/bash
# İzciPro aktarımı (sunucuda, sudo yetkili kullanıcıyla).
#   run-import.sh <paket-klasörü> [veritabanı] [--reset]
# Paket klasörü Export-IzciPro.ps1 çıktısı + bu klasördeki SQL dosyalarıdır. Önce veritabanının tam yedeği alınır,
# izcipro arşiv şeması yeniden yüklenir, ardından transform.sql tek işlemde çalışır ve validate.sql raporu basılır.
set -euo pipefail
PKG="$(cd "$1" && pwd)"
DB="${2:-arsantuptakip}"
RESET="${3:-}"
BACKUP_DIR=/var/backups/arsantuptakip
STAMP=$(date +%Y%m%d-%H%M%S)

psql_db() { sudo -u postgres psql -X -v ON_ERROR_STOP=1 -d "$DB" "$@"; }

sudo mkdir -p "$BACKUP_DIR"
echo "== Yedek: $BACKUP_DIR/$DB-$STAMP.dump"
sudo -u postgres pg_dump -Fc "$DB" | sudo tee "$BACKUP_DIR/$DB-$STAMP.dump" > /dev/null
sudo chmod 600 "$BACKUP_DIR/$DB-$STAMP.dump"

if [ "$RESET" = "--reset" ]; then
    echo "== Mevcut tüp verisi siliniyor (reset.sql)"
    psql_db -f "$PKG/reset.sql"
fi

echo "== izcipro arşiv şeması yükleniyor"
sudo chmod -R a+rX "$PKG"
(cd "$PKG" && psql_db -q -f schema.sql)

echo "== Aktarım (transform.sql)"
time psql_db -f "$PKG/transform.sql"

echo "== Doğrulama (validate.sql)"
psql_db -f "$PKG/validate.sql"
