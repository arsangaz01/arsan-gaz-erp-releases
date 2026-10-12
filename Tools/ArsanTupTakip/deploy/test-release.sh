#!/bin/bash
# arsantuptakip-release benzetim testi: geçici git depoları ve sahte derleme/servis ile yayın, kapsam kontrolü, çakışma,
# derleme hatası, sağlık kontrolü başarısızlığında otomatik geri dönüş ve /geri_al. Root, systemd veya GitHub gerekmez.
#   bash deploy/test-release.sh
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
SCRIPT="$HERE/arsantuptakip-release"
T=$(mktemp -d)
trap 'rm -rf "$T"' EXIT
export ARSANTUPTAKIP_RELEASE_SANDBOX=1 SANDBOX_ORIGIN="$T/origin.git" SANDBOX_BASE="$T/base" SANDBOX_BUNDLE="$T/agent.bundle" SANDBOX_APP_PARENT="$T/opt"
export GIT_AUTHOR_NAME=test GIT_AUTHOR_EMAIL=t@t GIT_COMMITTER_NAME=test GIT_COMMITTER_EMAIL=t@t
mkdir -p "$T/opt"
pass=0
ok() { pass=$((pass + 1)); printf '  ✓ %s\n' "$1"; }
fail() { printf '  ✗ %s\n' "$1" >&2; printf '%s\n' "${OUT:-}" >&2; exit 1; }
run() { set +e; OUT=$(bash "$SCRIPT" "$@" 2>&1); RC=$?; set -e; }
trailer() { sed -n "s/^@@$1=//p" <<<"$OUT"; }
deployed() { cat "$T/opt/api/.release-sha" 2>/dev/null || true; }
origin_main() { git -C "$T/origin.git" rev-parse refs/heads/main; }

git init -q --bare -b main "$T/origin.git"
git init -q -b main "$T/dev"
mkdir -p "$T/dev/Tools/ArsanTupTakip/src" "$T/dev/Tools/MobileApi"
echo v1 > "$T/dev/Tools/ArsanTupTakip/src/app.txt"
echo erp > "$T/dev/Tools/MobileApi/erp.txt"
git -C "$T/dev" add -A && git -C "$T/dev" commit -qm ilk && git -C "$T/dev" push -q "$T/origin.git" main

# Agent clone reads the mirror, like dev-agent.js.
agent_commit() { # <file> <content> <message>
  mkdir -p "$(dirname "$T/agent/$1")"
  echo "$2" > "$T/agent/$1"
  git -C "$T/agent" add -A && git -C "$T/agent" commit -qm "$3"
  git -C "$T/agent" bundle create -q "$T/agent.bundle" refs/heads/cursor/arsantuptakip-agent ^origin/main
}

echo "arsantuptakip-release benzetim testi"
run bogus; [ $RC = 2 ] || fail "bilinmeyen eylem 2 ile çıkmalı"; ok "bilinmeyen eylem reddedilir"
run publish 'abc;rm'; [ $RC = 2 ] || fail "geçersiz sha 2 ile çıkmalı"; ok "geçersiz commit kimliği reddedilir"

run pending; [ $RC = 0 ] || fail "pending (ilk)"
[ "$(trailer TARGET)" = "$(origin_main)" ] && [ -z "$(trailer NOTHING)" ] || fail "ilk hedef main olmalı"
ok "ilk kurulum: hedef main"
run publish "$(trailer TARGET)"; [ $RC = 0 ] || fail "ilk publish"
[ "$(deployed)" = "$(origin_main)" ] || fail "kurulan commit main olmalı"
grep -q 'restart arsantuptakip-api' "$T/base/systemctl.log" || fail "servis yeniden başlatılmalı"
ok "main kuruldu ve servis yeniden başlatıldı"
run pending; [ "$(trailer NOTHING)" = 1 ] || fail "yeni değişiklik yokken NOTHING=1"; ok "değişiklik yoksa yayınlanacak bir şey yok"

git clone -q "$T/base/mirror.git" "$T/agent"
git -C "$T/agent" checkout -q -B cursor/arsantuptakip-agent origin/main
agent_commit Tools/ArsanTupTakip/src/app.txt v2 "ajan: v2"
run pending; target=$(trailer TARGET)
[ "$target" = "$(git -C "$T/agent" rev-parse HEAD)" ] && [ -z "$(trailer BLOCKED)" ] || fail "ajan dalı hedef olmalı"
ok "ajan commit'i paketten içeri alındı"
run publish "$target"; [ $RC = 0 ] || fail "ajan publish"
[ "$(origin_main)" = "$target" ] && [ "$(deployed)" = "$target" ] || fail "main hızlı ileri alınıp kurulmalı"
[ ! -f "$T/agent.bundle" ] || fail "yayınlanan paket silinmeli"
[ "$(git -C "$T/base/mirror.git" rev-parse main)" = "$target" ] || fail "ayna güncellenmeli"
ok "ajan dalı main'e alındı, GitHub'a gönderildi, kuruldu"

git -C "$T/agent" fetch -q origin && git -C "$T/agent" merge -q --ff-only origin/main
agent_commit Tools/MobileApi/erp.txt bozuk "ajan: kapsam dışı"
run pending; [ "$(trailer BLOCKED)" = 1 ] || fail "kapsam dışı BLOCKED=1"; ok "kapsam dışı dosya yayın kontrolünde engellenir"
before=$(origin_main)
run publish "$(git -C "$T/agent" rev-parse HEAD)"; [ $RC = 3 ] || fail "kapsam dışı publish 3 ile çıkmalı"
[ "$(origin_main)" = "$before" ] || fail "main değişmemeli"; ok "kapsam dışı yayın reddedilir, main değişmez"
git -C "$T/agent" reset -q --hard origin/main

agent_commit Tools/ArsanTupTakip/SANDBOX_BUILD_FAIL x "ajan: derlenmeyen"
run pending; run publish "$(trailer TARGET)"; [ $RC = 1 ] || fail "derleme hatası 1 ile çıkmalı"
[ "$(origin_main)" = "$before" ] && [ "$(deployed)" = "$before" ] || fail "derleme hatasında main ve kurulum değişmemeli"
ok "derleme/test hatasında main'e birleştirilmez, kurulum değişmez"
git -C "$T/agent" reset -q --hard origin/main

# main moved on (someone pushed to main): non-fast-forward merge commit.
git -C "$T/dev" pull -q "$T/origin.git" main
echo not > "$T/dev/README-ana.txt" && git -C "$T/dev" add -A && git -C "$T/dev" commit -qm "main ilerledi" && git -C "$T/dev" push -q "$T/origin.git" main
agent_commit Tools/ArsanTupTakip/src/app.txt v3 "ajan: v3"
run pending; target=$(trailer TARGET); grep -q 'birleştirme commit' <<<"$OUT" || fail "birleştirme commit'i bildirilmeli"
run publish "$target"; [ $RC = 0 ] || fail "birleştirmeli publish"
git -C "$T/origin.git" merge-base --is-ancestor "$target" main || fail "ajan commit'i main'de olmalı"
[ "$(git -C "$T/origin.git" rev-list --parents -n1 main | wc -w)" = 3 ] || fail "birleştirme commit'i olmalı"
[ "$(deployed)" = "$(origin_main)" ] || fail "birleştirilmiş main kurulmalı"
ok "main ilerlemişse birleştirme commit'i oluşturulup kurulur"
good=$(origin_main)

run previous; [ $RC = 0 ] && [ -n "$(trailer PREVIOUS)" ] || fail "previous"
prev=$(trailer PREVIOUS)
run rollback; [ $RC = 0 ] || fail "rollback"
[ "$(deployed)" = "$prev" ] || fail "rollback önceki commit'e dönmeli"
run rollback; [ "$(deployed)" = "$good" ] || fail "ikinci rollback geri almayı geri almalı"
ok "/geri_al önceki kuruluma döner ve tekrar uygulanabilir"

git -C "$T/agent" fetch -q origin && git -C "$T/agent" merge -q --ff-only origin/main
agent_commit Tools/ArsanTupTakip/SANDBOX_HEALTH_FAIL x "ajan: açılmayan sürüm"
run pending; run publish "$(trailer TARGET)"; [ $RC = 2 ] || fail "sağlık hatası 2 ile çıkmalı"
[ "$(deployed)" = "$good" ] || fail "önceki kuruluma dönülmeli"
ok "sağlık kontrolü geçmezse önceki kuruluma otomatik dönülür"

run publish "$(git -C "$T/dev" rev-parse HEAD)"; [ $RC = 6 ] || fail "eski hedef 6 ile çıkmalı"; ok "artık hedef olmayan commit reddedilir"

run status; [ $RC = 0 ] && grep -qE "Yayındaki commit: ${good:0:7}( |$)" <<<"$OUT" && ! grep -qE '[0-9a-f]{40}' <<<"$OUT" || fail "status"
ok "status yayındaki commit'i yazar"

echo "TEST OK ($pass kontrol)"
