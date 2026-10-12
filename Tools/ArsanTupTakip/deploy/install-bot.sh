#!/bin/bash
# ArsanTüpTakip Telegram botu + sunucu Cursor ajanı + yayın betiği kurulumu (root, tekrar çalıştırılabilir).
#   sudo bash install-bot.sh <paket-klasörü>     (paket: app/ [npm ci yapılmış], deploy/; Deploy-ArsanTupTakipBot.ps1 hazırlar)
# ArsanGazERP'nin servislerini, botunu, arsangaz-release düzenini değiştirmez. /etc/arsangaz'dan yalnızca iki değeri
# root'tan root'a kopyalar (yazdırmadan): izinli Telegram sohbet kimliği ve CURSOR_API_KEY.
# Gizli değer yazdırmaz; yalnızca yayın anahtarının açık (public) yarısını yazar (GitHub deploy key olarak eklenir).
set -euo pipefail
pkg=${1:?paket klasörü gerekli}
[ "$(id -u)" -eq 0 ] || { echo "root olarak çalıştırın" >&2; exit 1; }
[ -f "$pkg/app/telegram-bot.js" ] && [ -d "$pkg/app/node_modules/@cursor/sdk" ] || { echo "$pkg/app eksik (npm ci yapılmamış?)" >&2; exit 1; }
STAMP=$(date +%Y%m%d-%H%M%S)
conf=/etc/arsantuptakip

echo "== Kullanıcılar ve grup"
getent group tuptakipq >/dev/null || groupadd --system tuptakipq
id tuptakipbot >/dev/null 2>&1 || useradd --system --home-dir /var/lib/arsantuptakip-bot --no-create-home --shell /usr/sbin/nologin --user-group tuptakipbot
id tuptakipbuild >/dev/null 2>&1 || useradd --system --home-dir /var/lib/arsantuptakip-release/buildhome --no-create-home --shell /usr/sbin/nologin --user-group tuptakipbuild
id tuptakipagent >/dev/null 2>&1 || useradd --create-home --home-dir /home/tuptakipagent --shell /bin/bash --user-group tuptakipagent
passwd -l tuptakipagent >/dev/null
for user in tuptakipbot tuptakipagent tuptakipbuild; do
  for group in sudo adm systemd-journal arsangaz cursoragent; do gpasswd -d "$user" "$group" >/dev/null 2>&1 || true; done
done
usermod -aG tuptakipq tuptakipbot
usermod -aG tuptakipq tuptakipagent
chmod 0750 /home/tuptakipagent

echo "== Klasörler"
install -d -m 0700 -o tuptakipbot -g tuptakipbot /var/lib/arsantuptakip-bot
install -d -m 2770 -o root -g tuptakipq /var/lib/arsantuptakip-queue
for sub in requests control outgoing; do install -d -m 2770 -o root -g tuptakipq "/var/lib/arsantuptakip-queue/$sub"; done
install -d -m 0700 -o tuptakipagent -g tuptakipagent /var/lib/arsantuptakip-agent
install -d -m 0711 -o root -g root /var/lib/arsantuptakip-release
install -d -m 0755 -o root -g root /opt/arsantuptakip-bot
install -d -m 0700 -o root -g root "$conf" "$conf/release"

echo "== Yayın betiği ve sudo izni"
install -m 0755 -o root -g root "$pkg/deploy/arsantuptakip-release" /usr/local/sbin/arsantuptakip-release
sudoers=$(mktemp)
cat > "$sudoers" <<'EOF'
# ArsanTupTakip botu: yalnizca ArsanTupTakip yayin betigini root olarak calistirabilir (betik argumanlari kendisi dogrular).
Defaults:tuptakipbot !log_allowed, !pam_session, !lecture
tuptakipbot ALL=(root) NOPASSWD: /usr/local/sbin/arsantuptakip-release status, /usr/local/sbin/arsantuptakip-release sync, /usr/local/sbin/arsantuptakip-release pending, /usr/local/sbin/arsantuptakip-release previous, /usr/local/sbin/arsantuptakip-release rollback, /usr/local/sbin/arsantuptakip-release publish *
EOF
visudo -cqf "$sudoers"
install -m 0440 -o root -g root "$sudoers" /etc/sudoers.d/arsantuptakip-bot
rm -f "$sudoers"

echo "== Ayar dosyaları (değerler yazdırılmaz)"
tg=$conf/telegram.env
[ -f "$tg" ] && grep -q '^TUPTAKIP_TELEGRAM_BOT_TOKEN=.' "$tg" || { echo "$tg içinde TUPTAKIP_TELEGRAM_BOT_TOKEN yok" >&2; exit 1; }
if ! grep -q '^TUPTAKIP_TELEGRAM_CHAT_ID=' "$tg"; then
  chat=$(sed -n 's/^TELEGRAM_CHAT_ID=//p' /etc/arsangaz/serveragent.env | tail -n1 | tr -d "\"' \r")
  [[ "$chat" =~ ^-?[0-9]+$ ]] || { echo "Mevcut botun sohbet kimliği okunamadı (biçim beklenmiyor)" >&2; exit 1; }
  [ -z "$(tail -c1 "$tg")" ] || printf '\n' >> "$tg"
  printf 'TUPTAKIP_TELEGRAM_CHAT_ID=%s\n' "$chat" >> "$tg"
  unset chat
  echo "telegram.env: izinli sohbet kimliği mevcut bottan kopyalandı"
fi
chown root:root "$tg"; chmod 600 "$tg"
agent_env=$conf/agent.env
if ! grep -q '^CURSOR_API_KEY=.' "$agent_env" 2>/dev/null; then
  line=$(grep -E '^CURSOR_API_KEY=.' /etc/arsangaz/cursoragent.env | tail -n1 || true)
  [ -n "$line" ] || { echo "/etc/arsangaz/cursoragent.env içinde CURSOR_API_KEY yok" >&2; exit 1; }
  ( umask 077; printf '# ArsanTupTakip sunucu Cursor ajani (root:root 600, systemd okur).\n%s\n' "$line" > "$agent_env" )
  unset line
  echo "agent.env: CURSOR_API_KEY kopyalandı"
fi
chown root:root "$agent_env"; chmod 600 "$agent_env"

echo "== GitHub yayın anahtarı (yalnızca root)"
[ -f "$conf/release/github_release" ] || ssh-keygen -q -t ed25519 -N "" -C "arsantuptakip-release@oracle" -f "$conf/release/github_release"
cat > "$conf/release/known_hosts" <<'KH'
github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl
KH
chmod 600 "$conf/release/github_release" "$conf/release/known_hosts"

echo "== Ajan git ayarları"
runuser -u tuptakipagent -- bash -c '
  git config --global --replace-all safe.directory /var/lib/arsantuptakip-release/mirror.git
  git config --global init.defaultBranch main
  git config --global pull.ff only'

echo "== Uygulama"
if [ -d /opt/arsantuptakip-bot/app ]; then mv /opt/arsantuptakip-bot/app "/opt/arsantuptakip-bot/app.bak-$STAMP"; fi
cp -a "$pkg/app" /opt/arsantuptakip-bot/app
chown -R root:root /opt/arsantuptakip-bot/app
chmod -R u=rwX,go=rX /opt/arsantuptakip-bot/app
{ ls -dt /opt/arsantuptakip-bot/app.bak-* 2>/dev/null || true; } | tail -n +4 | xargs -r rm -rf

echo "== systemd"
install -m 0644 -o root -g root "$pkg/deploy/arsantuptakip-bot.service" /etc/systemd/system/arsantuptakip-bot.service
install -m 0644 -o root -g root "$pkg/deploy/arsantuptakip-agent.service" /etc/systemd/system/arsantuptakip-agent.service
systemctl daemon-reload

echo "== Depo aynası"
if /usr/local/sbin/arsantuptakip-release sync; then echo "ayna hazır"; else echo "UYARI: ayna hazırlanamadı (yayın anahtarı GitHub'a eklenmemiş olabilir); ajan bekler."; fi

systemctl enable arsantuptakip-agent arsantuptakip-bot >/dev/null 2>&1
systemctl restart arsantuptakip-agent arsantuptakip-bot
sleep 3
echo "arsantuptakip-bot:   $(systemctl is-active arsantuptakip-bot)"
echo "arsantuptakip-agent: $(systemctl is-active arsantuptakip-agent)"
echo "DEPLOY_KEY_PUBLIC: $(cat "$conf/release/github_release.pub")"
echo "install-bot: OK"
