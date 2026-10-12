#!/bin/bash
# Idempotent setup for phone-approved releases (arsangaz-release). Usage: sudo bash install-release.sh
# Never overwrites the release deploy key, the staging DB password or the GitHub token; prints the key's public half.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
SDK=/opt/dotnet-sdk
SPOOL=/var/lib/arsangaz-data/prod/ArsanGazERP/Release

# .NET SDK matching global.json (10.0.4xx). Production services keep using /usr/bin/dotnet (same 10.0 runtime).
if [ ! -x $SDK/dotnet ] || ! $SDK/dotnet --list-sdks | grep -q '^10\.0\.4'; then
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  bash /tmp/dotnet-install.sh --channel 10.0.4xx --install-dir $SDK --no-path
  rm -f /tmp/dotnet-install.sh
fi
chmod -R go+rX $SDK

need=()
for pkg in unzip python3 apksigner; do dpkg -s "$pkg" >/dev/null 2>&1 || need+=("$pkg"); done
if [ ${#need[@]} -gt 0 ]; then
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq --no-install-recommends "${need[@]}" >/dev/null || echo "UYARI: ${need[*]} kurulamadi"
fi

# Unprivileged build user: no secrets, no sudo, no access to the release key.
if ! id arsangazbuild >/dev/null 2>&1; then
  useradd --system --home-dir /var/lib/arsangaz-release/build/home --shell /usr/sbin/nologin --user-group arsangazbuild
fi
install -d -m 0711 -o root -g root /var/lib/arsangaz-release
install -d -m 0750 -o arsangazbuild -g arsangazbuild /var/lib/arsangaz-release/build /var/lib/arsangaz-release/build/home
install -d -m 0700 -o root -g root /var/lib/arsangaz-release/state /var/log/arsangaz-release
install -d -m 0700 -o arsangaz -g arsangaz "$SPOOL" "$SPOOL/requests" "$SPOOL/results"

# GitHub deploy key with write access, used only by root for fetch + push of main.
install -d -m 0700 -o root -g root /etc/arsangaz/release
[ -f /etc/arsangaz/release/github_release ] || ssh-keygen -q -t ed25519 -N "" -C "arsangaz-release@oracle" -f /etc/arsangaz/release/github_release
printf 'github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl\n' > /etc/arsangaz/release/known_hosts
chmod 600 /etc/arsangaz/release/github_release
if [ ! -f /etc/arsangaz/release.env ]; then
  umask 077
  printf '# arsangaz-release. GITHUB_ACTIONS_TOKEN yalnizca Set-ServerAgentValue.ps1 ile yazilir.\n' > /etc/arsangaz/release.env
  umask 022
fi
chown root:root /etc/arsangaz/release.env; chmod 600 /etc/arsangaz/release.env

# Staging database role (throw-away copy of production for the release test instance).
if [ ! -f /etc/arsangaz/mobileapi-staging.env ]; then
  password=$(openssl rand -hex 24)
  umask 077
  cat > /etc/arsangaz/mobileapi-staging.env <<EOF
# Yayin testi: prod'un kopyasi olan arsangaz_staging veritabani. Prod veritabanina baglanmaz.
ARSANGAZ_PG_CONNECTION=Host=127.0.0.1;Port=5432;Database=arsangaz_staging;Username=arsangaz_staging;Password=$password
EOF
  umask 022
fi
chmod 600 /etc/arsangaz/mobileapi-staging.env
password=$(grep -oP '(?<=Password=)[^;]+' /etc/arsangaz/mobileapi-staging.env)
runuser -u postgres -- psql -qAt -v ON_ERROR_STOP=1 -v pw="$password" <<'SQL' >/dev/null
SELECT format('CREATE ROLE arsangaz_staging LOGIN PASSWORD %L', :'pw') WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'arsangaz_staging') \gexec
SELECT format('ALTER ROLE arsangaz_staging LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE PASSWORD %L', :'pw') \gexec
SQL
install -d -m 0700 -o arsangaz -g arsangaz /var/lib/arsangaz-data/staging

install -m 0755 -o root -g root "$HERE/arsangaz-release.sh" /usr/local/sbin/arsangaz-release
for unit in arsangaz-mobileapi-staging.service arsangaz-release.path arsangaz-release-runner.service; do
  install -m 0644 -o root -g root "$HERE/$unit" /etc/systemd/system/$unit
done
# The old SQLite-era test unit uses production's port and (via a drop-in) the production database; keep it unusable.
old=/etc/systemd/system/arsangaz-mobileapi.service
if [ -f "$old" ] && [ ! -L "$old" ] && ! systemctl is-active --quiet arsangaz-mobileapi.service; then
  install -d -m 0700 /var/lib/arsangaz-release/state/disabled-units
  mv "$old" /var/lib/arsangaz-release/state/disabled-units/
  [ -d "$old.d" ] && mv "$old.d" /var/lib/arsangaz-release/state/disabled-units/
  systemctl daemon-reload
  systemctl mask arsangaz-mobileapi.service >/dev/null 2>&1 || true
fi
systemctl daemon-reload
systemctl enable --now arsangaz-release.path >/dev/null
echo "install-release: OK"
echo "RELEASE_KEY_PUBLIC: $(cat /etc/arsangaz/release/github_release.pub)"
