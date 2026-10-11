#!/usr/bin/env bash
# ArsanGazERP PostgreSQL yedekleri (sunucuda root/sudo ile çalıştırılır; tekrar çalıştırılabilir).
#   Gece:    her gün 03:15 (Europe/Istanbul) pg_dump -Fc, 30 gün saklanır  -> /var/backups/arsangaz/nightly
#   Saatlik: her saat başı pg_dump -Fc, 2 gün saklanır                       -> /var/backups/arsangaz/hourly
# Yedekler yalnızca bu sunucudadır; sunucu dışı kopya (ör. PC'ye veya buluta) ayrıca kararlaştırılacak.
# Geri yükleme örneği (boş bir veritabanına):
#   sudo -u postgres createdb -O arsangaz_app -T template0 --lc-collate=C --lc-ctype=C.UTF-8 arsangaz_restore
#   sudo -u postgres pg_restore -d arsangaz_restore --no-owner --role=arsangaz_app /var/backups/arsangaz/nightly/<dosya>.dump
set -euo pipefail

install -d -m 0700 -o postgres -g postgres /var/backups/arsangaz /var/backups/arsangaz/nightly /var/backups/arsangaz/hourly

cat > /usr/local/sbin/arsangaz-pg-backup <<'EOF'
#!/usr/bin/env bash
# Kullanım: arsangaz-pg-backup nightly|hourly   (postgres kullanıcısı olarak çalışır)
set -euo pipefail
kind=${1:?nightly veya hourly}
case "$kind" in
  nightly) keep_minutes=$((30 * 24 * 60)) ;;
  hourly)  keep_minutes=$((2 * 24 * 60)) ;;
  *) echo "geçersiz tür: $kind" >&2; exit 2 ;;
esac
dir=/var/backups/arsangaz/$kind
stamp=$(date -u +%Y%m%dT%H%M%SZ)
target=$dir/arsangaz-$stamp.dump
umask 077
pg_dump --format=custom --compress=6 --file="$target.tmp" arsangaz
pg_restore --list "$target.tmp" > /dev/null
mv "$target.tmp" "$target"
find "$dir" -maxdepth 1 -name 'arsangaz-*.dump' -type f -mmin +"$keep_minutes" -delete
find "$dir" -maxdepth 1 -name 'arsangaz-*.dump.tmp' -type f -mmin +120 -delete
echo "yedek tamam: $target ($(stat -c %s "$target") bayt), $(find "$dir" -maxdepth 1 -name 'arsangaz-*.dump' | wc -l) dosya saklanıyor"
EOF
chmod 0755 /usr/local/sbin/arsangaz-pg-backup

cat > /etc/systemd/system/arsangaz-pg-backup@.service <<'EOF'
[Unit]
Description=ArsanGazERP PostgreSQL yedeği (%i)
After=postgresql.service
Requires=postgresql.service

[Service]
Type=oneshot
User=postgres
Group=postgres
ExecStart=/usr/local/sbin/arsangaz-pg-backup %i
Nice=10
IOSchedulingClass=best-effort
IOSchedulingPriority=7
EOF

cat > /etc/systemd/system/arsangaz-pg-backup-nightly.timer <<'EOF'
[Unit]
Description=ArsanGazERP PostgreSQL gece yedeği (30 gün)

[Timer]
OnCalendar=*-*-* 03:15:00 Europe/Istanbul
Persistent=true
RandomizedDelaySec=5min
Unit=arsangaz-pg-backup@nightly.service

[Install]
WantedBy=timers.target
EOF

cat > /etc/systemd/system/arsangaz-pg-backup-hourly.timer <<'EOF'
[Unit]
Description=ArsanGazERP PostgreSQL saatlik yedek (2 gün)

[Timer]
OnCalendar=*-*-* *:05:00
Persistent=true
Unit=arsangaz-pg-backup@hourly.service

[Install]
WantedBy=timers.target
EOF

systemctl daemon-reload
systemctl enable --now arsangaz-pg-backup-nightly.timer arsangaz-pg-backup-hourly.timer
systemctl list-timers 'arsangaz-pg-backup-*' --no-pager
