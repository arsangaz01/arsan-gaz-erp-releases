#!/usr/bin/env bash
# ArsanTüpTakip kurulum doğrulaması (root, salt okunur). Kurulumdan önce ve sonra çalıştırılıp çıktılar karşılaştırılır.
# Gizli bilgi yazdırmaz.
set -uo pipefail

echo "== Servisler"
for s in arsangaz-serveragent arsangaz-cursoragent arsangaz-mobileapi-prod cloudflared postgresql arsantuptakip-api arsantuptakip-backup.timer; do
    printf '%-28s %s\n' "$s" "$(systemctl is-active "$s" 2>/dev/null)"
done

echo "== Mevcut sistem (HTTP durum kodları)"
code() { curl -s -o /dev/null -w '%{http_code}' --max-time 15 "$1"; }
echo "127.0.0.1:5077/panel            $(code http://127.0.0.1:5077/panel)"
echo "https://arsantuptakip.com/      $(code https://arsantuptakip.com/)"
echo "https://arsantuptakip.com/panel $(code https://arsantuptakip.com/panel)"
echo "https://arsantuptakip.com/tuptakip/panel $(code https://arsantuptakip.com/tuptakip/panel)"
echo "cloudflared config sha256       $(sha256sum /etc/cloudflared/config.yml | cut -c1-16)"
if /usr/local/sbin/arsangaz-release pending >/dev/null 2>&1; then echo "arsangaz-release pending        çalışıyor (çıkış 0)"; else echo "arsangaz-release pending        HATA (çıkış $?)"; fi

echo "== ArsanTüpTakip (yalnızca yerel)"
if systemctl list-unit-files arsantuptakip-api.service >/dev/null 2>&1 && [ -f /etc/systemd/system/arsantuptakip-api.service ]; then
    echo "dinlenen adres                  $(ss -ltnH 'sport = :5080' | awk '{print $4}' | paste -sd' ')"
    echo "/tuptakip/api/merhaba           $(curl -s --max-time 5 http://127.0.0.1:5080/tuptakip/api/merhaba)"
    echo "/tuptakip/panel                 $(code http://127.0.0.1:5080/tuptakip/panel)"
    echo "/tuptakip/panel/app.js          $(code http://127.0.0.1:5080/tuptakip/panel/app.js)"
    echo "/tuptakip/api/ozet (eşleşmesiz) $(code http://127.0.0.1:5080/tuptakip/api/ozet)"
    echo "/tuptakip/panel/api/ben (oturumsuz) $(code http://127.0.0.1:5080/tuptakip/panel/api/ben)"
    echo "panel giriş (hesap yok)         $(curl -s --max-time 10 -H 'Content-Type: application/json' -d '{"kullanici":"yok","sifre":"yok"}' http://127.0.0.1:5080/tuptakip/panel/giris)"
    if [ -f /var/lib/arsantuptakip/web-panel-users.json ]; then
        echo "panel kullanıcı sayısı          $(python3 -c 'import json;print(len(json.load(open("/var/lib/arsantuptakip/web-panel-users.json"))))')"
    else
        echo "panel kullanıcı sayısı          0 (dosya yok)"
    fi
fi

echo "== Veritabanı yalıtımı"
psql_q() { runuser -u postgres -- psql -X -At -d postgres -c "$1"; }
echo "rol (süper/createdb/createrole) $(psql_q "SELECT rolsuper||'/'||rolcreatedb||'/'||rolcreaterole FROM pg_roles WHERE rolname='arsantuptakip_app'")"
echo "arsangaz CONNECT yetkisi        $(psql_q "SELECT has_database_privilege('arsantuptakip_app','arsangaz','CONNECT')")"
echo "arsantuptakip PUBLIC erişimi    $(psql_q "SELECT COALESCE((SELECT datacl::text FROM pg_database WHERE datname='arsantuptakip'),'-')" | grep -c '=c' | sed 's/^0$/yok/;s/^[1-9].*/VAR/')"
if [ -f /etc/arsantuptakip/api.env ]; then
    conn=$(sed -n 's/^TUPTAKIP_PG_CONNECTION="\(.*\)"$/\1/p' /etc/arsantuptakip/api.env)
    pw=$(sed -n 's/.*Password=\([0-9a-f]*\).*/\1/p' <<<"$conn")
    for target in arsantuptakip arsangaz arsangaz_staging postgres; do
        if out=$(PGPASSWORD="$pw" psql -X -At -h 127.0.0.1 -U arsantuptakip_app -d "$target" -c 'SELECT 1' 2>&1); then
            echo "bağlantı -> $target: BAŞARILI"
        else
            echo "bağlantı -> $target: REDDEDİLDİ ($(head -n1 <<<"$out" | sed 's/.*FATAL: *//' | cut -c1-70))"
        fi
    done
    echo "tuptakip şeması tabloları       $(PGPASSWORD="$pw" psql -X -At -h 127.0.0.1 -U arsantuptakip_app -d arsantuptakip -c "SELECT count(*) FROM information_schema.tables WHERE table_schema='tuptakip'" 2>&1)"
    unset pw conn
fi
