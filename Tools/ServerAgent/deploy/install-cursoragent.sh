#!/bin/bash
# Idempotent setup for the server Cursor agent (user cursoragent, service arsangaz-cursoragent).
# Usage (from the uploaded deploy folder): sudo bash install-cursoragent.sh
# Never overwrites the Cursor key or the GitHub deploy key; prints the deploy key's public half.
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
USER_NAME=cursoragent
HOME_DIR=/home/$USER_NAME

if ! id "$USER_NAME" >/dev/null 2>&1; then
  useradd --create-home --home-dir "$HOME_DIR" --shell /bin/bash --user-group "$USER_NAME"
fi
# No sudo/adm/systemd-journal groups, no password login.
for group in sudo adm systemd-journal arsangaz; do gpasswd -d "$USER_NAME" "$group" >/dev/null 2>&1 || true; done
passwd -l "$USER_NAME" >/dev/null
chmod 0750 "$HOME_DIR"

install -d -m 0755 -o root -g root /opt/arsangaz-cursoragent /opt/arsangaz-cursoragent/bin
install -m 0755 -o root -g root "$HERE/cursor-inbox.sh" /opt/arsangaz-cursoragent/bin/cursor-inbox.sh
install -d -m 0755 -o "$USER_NAME" -g "$USER_NAME" /var/lib/arsangaz-cursoragent

# The only privilege: run the queue helper as arsangaz (no root, no other command).
sudoers=$(mktemp)
cat > "$sudoers" <<'EOF'
# ArsanGazERP sunucu Cursor ajani: yalnizca Cursor kuyrugu yardimcisini arsangaz olarak calistirabilir.
Defaults:cursoragent !log_allowed, !pam_session, !lecture
cursoragent ALL=(arsangaz) NOPASSWD: /opt/arsangaz-cursoragent/bin/cursor-inbox.sh
EOF
visudo -cqf "$sudoers"
install -m 0440 -o root -g root "$sudoers" /etc/sudoers.d/arsangaz-cursoragent
rm -f "$sudoers"

if [ ! -f /etc/arsangaz/cursoragent.env ]; then
  umask 077
  printf '# Sunucu Cursor ajani. CURSOR_API_KEY yalnizca Tools/ServerAgent/Set-ServerAgentValue.ps1 ile yazilir.\n' > /etc/arsangaz/cursoragent.env
fi
chown "$USER_NAME:$USER_NAME" /etc/arsangaz/cursoragent.env
chmod 600 /etc/arsangaz/cursoragent.env

# GitHub deploy key (repository-scoped) and pinned GitHub host key.
sudo -u "$USER_NAME" -H bash -s <<'EOF'
set -euo pipefail
umask 077
mkdir -p ~/.ssh
[ -f ~/.ssh/github_deploy ] || ssh-keygen -q -t ed25519 -N "" -C "arsangaz-cursoragent@oracle" -f ~/.ssh/github_deploy
cat > ~/.ssh/config <<'CFG'
Host github.com
  User git
  IdentityFile ~/.ssh/github_deploy
  IdentitiesOnly yes
  StrictHostKeyChecking yes
CFG
cat > ~/.ssh/known_hosts <<'KH'
github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl
KH
git config --global init.defaultBranch main
git config --global pull.ff only
EOF

install -m 0644 -o root -g root "$HERE/arsangaz-cursoragent.service" /etc/systemd/system/arsangaz-cursoragent.service
systemctl daemon-reload
echo "install-cursoragent: OK"
echo "DEPLOY_KEY_PUBLIC: $(cat $HOME_DIR/.ssh/github_deploy.pub)"
