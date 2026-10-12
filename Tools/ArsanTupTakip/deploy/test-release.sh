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

# ---- mobile APK: a fake GitHub artifact with a real binary AndroidManifest.xml
cat > "$T/mkapk.py" <<'PY'
import json, struct, sys, zipfile
out, pkg, name, code, cert = sys.argv[1], sys.argv[2], sys.argv[3], int(sys.argv[4]), sys.argv[5]
NONE = 0xFFFFFFFF
strs = ["manifest", "package", "versionCode", "versionName", pkg, name, "android", "http://schemas.android.com/apk/res/android"]
offsets, blob = [], b""
for s in strs:
    offsets.append(len(blob))
    blob += struct.pack("<H", len(s)) + s.encode("utf-16-le") + b"\0\0"
blob += b"\0" * (-len(blob) % 4)
start = 28 + 4 * len(strs)
pool = struct.pack("<HHIIIIII", 1, 28, start + len(blob), len(strs), 0, 0, start, 0) + b"".join(struct.pack("<I", o) for o in offsets) + blob
ids = [0, 0, 0x0101021B, 0x0101021C]
resmap = struct.pack("<HHI", 0x0180, 8, 8 + 4 * len(ids)) + b"".join(struct.pack("<I", i) for i in ids)
attrs = [(NONE, 1, 4, 0x03, 4), (7, 2, NONE, 0x10, code), (7, 3, 5, 0x03, 5)]
body = b"".join(struct.pack("<IIIHBBI", ns, n, raw, 8, 0, t, v) for ns, n, raw, t, v in attrs)
ext = struct.pack("<IIHHHHHH", NONE, 0, 20, 20, len(attrs), 0, 0, 0)
element = struct.pack("<HHIII", 0x0102, 16, 16 + len(ext) + len(body), 1, NONE) + ext + body
xml = pool + resmap + element
xml = struct.pack("<HHI", 3, 8, 8 + len(xml)) + xml
apk = out + ".apk"
with zipfile.ZipFile(apk, "w") as z:
    z.writestr("AndroidManifest.xml", xml)
    z.writestr("classes.dex", b"dex\n")
with zipfile.ZipFile(out, "w") as z:
    z.write(apk, "ArsanTupTakip-Mobil.apk")
    z.writestr("apk-info.json", json.dumps({"package": pkg, "versionName": name, "versionCode": code, "certSha256": cert, "sha": "x"}))
PY
CERT=80ae16deb437fd9e1c7b26b3b38563977078c97a98a17f6983b7d5f19dd90153
csproj() { # <code> <name>
  local f="$T/dev/Tools/ArsanTupTakip/mobile/ArsanTupTakip.Mobile/ArsanTupTakip.Mobile.csproj"
  mkdir -p "$(dirname "$f")"
  printf '<Project>\n<ApplicationDisplayVersion>%s</ApplicationDisplayVersion>\n<ApplicationVersion>%s</ApplicationVersion>\n</Project>\n' "$2" "$1" > "$f"
  git -C "$T/dev" add -A && git -C "$T/dev" commit -qm "mobil $2" && git -C "$T/dev" push -q "$T/origin.git" main
}
published() { python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["versionCode"], d["versionName"], d["package"])' "$T/base/data/ArsanTupTakip-Mobil.apk.meta.json" 2>/dev/null || true; }
git -C "$T/dev" pull -q "$T/origin.git" main
csproj 1 1.0.0
python3 "$T/mkapk.py" "$T/art1.zip" com.arsantuptakip.mobil 1.0.0 1 "$CERT"

run apkinfo "$T/art1.zip.apk"; [ "$OUT" = "$(printf 'com.arsantuptakip.mobil\t1.0.0\t1')" ] || fail "apkinfo paket/sürüm"
ok "APK ikili manifestinden paket adı ve sürüm okunur"
run status; grep -q 'APK: yayında değil' <<<"$OUT" || fail "status APK yok"
run apkcheck; [ $RC = 0 ] && [ "$(trailer BLOCKED)" = 1 ] && [ -z "$(trailer TARGET)" ] || fail "artefakt yokken BLOCKED=1"
ok "imzalı APK artefaktı yokken /apk yayın hedefi vermez"
export SANDBOX_APK_ZIP="$T/art1.zip"
run apkcheck; [ "$(trailer TARGET)" = "$(origin_main)" ] || fail "artefakt varken hedef main olmalı"
grep -q 'build 1' <<<"$OUT" || fail "apkcheck sürümü yazmalı"
ok "/apk main'deki sürümü ve hazır APK'yı gösterir"
run apk 'abc;rm'; [ $RC = 2 ] || fail "geçersiz sha"; ok "apk: geçersiz commit kimliği reddedilir"
run apk "$(origin_main)"; [ $RC = 0 ] && [ "$(trailer APK_BUILD)" = 1 ] || fail "apk yayını"
[ "$(published)" = "1 1.0.0 com.arsantuptakip.mobil" ] && cmp -s "$T/base/data/ArsanTupTakip-Mobil.apk" "$T/art1.zip.apk" || fail "APK ve meta yerinde olmalı"
ok "APK doğrulanıp veri klasörüne meta ile konur"
run apkcheck; [ "$(trailer NOTHING)" = 1 ] || fail "aynı sürümde NOTHING=1"
run apk "$(origin_main)"; [ $RC = 0 ] && grep -q 'zaten' <<<"$OUT" || fail "aynı sürüm yeniden yayınlanmamalı"
ok "yayındaki sürüm yeniden yayınlanmaz"
run status; grep -q 'APK: 1.0.0 (build 1' <<<"$OUT" || fail "status APK satırı"; ok "status yayındaki APK'yı yazar"

csproj 2 1.0.1
python3 "$T/mkapk.py" "$T/art2.zip" com.arsangaz.baska 1.0.1 2 "$CERT"
export SANDBOX_APK_ZIP="$T/art2.zip"
run apk "$(origin_main)"; [ $RC = 1 ] && grep -q 'paket adı' <<<"$OUT" || fail "yanlış paket reddedilmeli"
python3 "$T/mkapk.py" "$T/art3.zip" com.arsantuptakip.mobil 1.0.1 2 "$(printf 'f%.0s' {1..64})"
export SANDBOX_APK_ZIP="$T/art3.zip"
run apk "$(origin_main)"; [ $RC = 1 ] && grep -q 'sertifika' <<<"$OUT" || fail "yanlış sertifika reddedilmeli"
python3 "$T/mkapk.py" "$T/art4.zip" com.arsantuptakip.mobil 1.0.0 1 "$CERT"
export SANDBOX_APK_ZIP="$T/art4.zip"
run apk "$(origin_main)"; [ $RC = 1 ] && grep -q 'csproj' <<<"$OUT" || fail "csproj ile uyuşmayan sürüm reddedilmeli"
[ "$(published)" = "1 1.0.0 com.arsantuptakip.mobil" ] || fail "reddedilen APK yayındakini değiştirmemeli"
ok "yanlış paket, yanlış sertifika veya uyuşmayan sürüm yayınlanmaz"
python3 "$T/mkapk.py" "$T/art5.zip" com.arsantuptakip.mobil 1.0.1 2 "$CERT"
export SANDBOX_APK_ZIP="$T/art5.zip"
run apk "$(origin_main)"; [ $RC = 0 ] && [ "$(published)" = "2 1.0.1 com.arsantuptakip.mobil" ] || fail "yeni sürüm yayını"
ls "$T/base/data/"ArsanTupTakip-Mobil.apk.bak-* >/dev/null 2>&1 || fail "önceki APK yedeklenmeli"
ok "yeni build yayınlanır, önceki APK yedeklenir"
unset SANDBOX_APK_ZIP

echo "TEST OK ($pass kontrol)"
