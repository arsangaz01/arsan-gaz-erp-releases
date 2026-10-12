#!/bin/bash
# Sandbox tests for arsangaz-release.sh. The script is sourced with ARSANGAZ_RELEASE_SANDBOX=1 (the real token is
# never read) and GitHub, the download and the server paths are replaced with fakes of realistic size: multi-MB
# GitHub API JSON (one argument may be at most 128 KiB) and a ~40 MB APK artifact. No root, no network.
# Usage: bash Tools/ServerAgent/deploy/test-release.sh   (server: after Deploy-Server.ps1 -Component release; CI: sunucu)
set -euo pipefail
HERE=$(cd "$(dirname "$0")" && pwd)
bash -n "$HERE/arsangaz-release.sh"
export ARSANGAZ_RELEASE_SANDBOX=1
# shellcheck source=arsangaz-release.sh
source "$HERE/arsangaz-release.sh"

T=$(mktemp -d)
WWW_PID=""
cleanup() { [ -n "$WWW_PID" ] && kill "$WWW_PID" 2>/dev/null; rm -rf "$T"; }
trap cleanup EXIT

REPO=$T/repo; STATE=$T/state; BUILD=$T/build; APK_DIR=$T/apk; LOGDIR=$T/log; PROD_DIR=$T/prod
PUBLIC_STATUS=$T/public/status.json
APK_OWNER=$(id -un); APK_GROUP=$(id -gn)
GITHUB_ACTIONS_TOKEN=sandbox-token
mkdir -p "$STATE" "$BUILD" "$APK_DIR" "$LOGDIR" "$PROD_DIR" "$T/bin" "$T/www/api"

# Fake apksigner (the real one would reject the random test APK) reporting the expected certificate.
printf '#!/bin/sh\necho "Signer #1 certificate SHA-256 digest: %s"\n' "$APK_CERT_SHA256" > "$T/bin/apksigner"
chmod +x "$T/bin/apksigner"
export PATH="$T/bin:$PATH"

# Fake prod MobileApi: /panel and /api/guncelleme served from files.
PROD_PORT=$(python3 -c 'import socket; s=socket.socket(); s.bind(("127.0.0.1",0)); print(s.getsockname()[1]); s.close()')
PUBLIC_URL=http://127.0.0.1:$PROD_PORT
echo ok > "$T/www/panel"
offer() { printf '{"apiVersion":"1.11.0","publishedMobileVersion":"%s","publishedMobileBuild":%s,"apkReady":true}\n' "$2" "$1" > "$T/www/api/guncelleme"; }
offer 24 1.7.6
(cd "$T/www" && exec python3 -m http.server "$PROD_PORT" --bind 127.0.0.1 >/dev/null 2>&1) &
WWW_PID=$!
for _ in $(seq 1 50); do curl -fs -o /dev/null "http://127.0.0.1:$PROD_PORT/panel" && break; sleep 0.1; done

# Repository: c1 = mobile 1.7.6 (24), c2 = mobile 1.7.7 (25).
git init -q -b main "$REPO"
gc() { git -C "$REPO" -c user.name=t -c user.email=t@t "$@"; }
csproj() { mkdir -p "$REPO/Tools/MobileApp" "$REPO/Tools/MobileApi"; printf '<Project><PropertyGroup>\n<ApplicationDisplayVersion>%s</ApplicationDisplayVersion>\n<ApplicationVersion>%s</ApplicationVersion>\n</PropertyGroup></Project>\n' "$2" "$1" > "$REPO/$MOBILE_CSPROJ"; }
csproj 24 1.7.6; echo api > "$REPO/Tools/MobileApi/a.cs"; gc add -A; gc commit -qm c1; C1=$(gc rev-parse HEAD)
csproj 25 1.7.7; gc commit -qam c2; C2=$(gc rev-parse HEAD)
gc update-ref refs/remotes/origin/main "$C2"
printf '%s\n' "$C2" > "$PROD_DIR/.release-sha"
fetch() { :; }

# GitHub API fakes. runs: 30 runs padded like real responses (repository/head_commit objects) to several MB.
make_runs() { # <status> <conclusion>
  python3 - "$C2" "$1" "$2" > "$T/runs.json" <<'PY'
import json, sys, random
sha, status, conclusion = sys.argv[1:4]
pad = {"description": "x" * 180_000, "topics": ["arsan"] * 2000}
runs = [{"id": 1001, "head_sha": sha, "status": status, "conclusion": None if status != "completed" else conclusion,
         "html_url": "https://github.invalid/run/1001", "repository": pad, "head_repository": pad}]
for i in range(29):
    runs.append({"id": 2000 + i, "head_sha": "%040x" % random.getrandbits(160), "status": "completed",
                 "conclusion": "success", "html_url": "u", "repository": pad, "head_repository": pad})
print(json.dumps({"total_count": len(runs), "workflow_runs": runs}))
PY
}
python3 - > "$T/jobs.json" <<'PY'
import json
steps = [{"name": "s%d" % i, "log": "y" * 4000} for i in range(400)]
print(json.dumps({"jobs": [{"name": "changes", "conclusion": "success", "steps": steps},
                           {"name": "masaustu", "conclusion": "success", "steps": steps},
                           {"name": "sunucu", "conclusion": "failure", "steps": steps}]}))
PY
python3 - > "$T/artifacts.json" <<'PY'
import json
arts = [{"name": "other-%d" % i, "expired": False, "archive_download_url": "x", "pad": "z" * 20_000} for i in range(100)]
arts.append({"name": "arsangaz-mobile-apk", "expired": False, "archive_download_url": "https://artifact.invalid/a.zip"})
print(json.dumps({"artifacts": arts}))
PY
make_artifact() { # <versionCode> <versionName>
  head -c 40000000 /dev/urandom > "$T/ArsanGazERP-Mobil.apk"
  python3 - "$T" "$1" "$2" "$APK_CERT_SHA256" <<'PY'
import json, sys, zipfile
t, code, name, cert = sys.argv[1:5]
with zipfile.ZipFile(t + "/artifact.zip", "w", zipfile.ZIP_STORED) as z:
    z.write(t + "/ArsanGazERP-Mobil.apk", "ArsanGazERP-Mobil.apk")
    z.writestr("apk-info.json", json.dumps({"versionName": name, "versionCode": int(code), "certSha256": cert}))
PY
}
gh_api() {
  case "$2" in
    */runs\?*) cat "$T/runs.json" ;;
    */jobs*) cat "$T/jobs.json" ;;
    */artifacts) cat "$T/artifacts.json" ;;
    */dispatches) echo dispatched >> "$T/dispatch.log" ;;
    *) return 22 ;;
  esac
}
DOWNLOAD_FAILS=0
curl() {
  local a out="" url=""
  for a in "$@"; do [ "$url" = next ] && { out=$a; url=""; continue; }; [ "$a" = -o ] && url=next; done
  if [ "${*: -1}" = https://artifact.invalid/a.zip ]; then
    [ "$DOWNLOAD_FAILS" = 1 ] && return 22
    cp "$T/artifact.zip" "$out"; return 0
  fi
  command curl "$@"
}

fails=0
check() { # <name> <expected substring> <output>
  if grep -qF -- "$2" <<<"$3"; then echo "PASS $1"; else echo "FAIL $1 (beklenen: $2)"; tail -n 6 <<<"$3" | sed 's/^/   /'; fails=$((fails + 1)); fi
}
refute() {
  if grep -qF -- "$2" <<<"$3"; then echo "FAIL $1 (olmamalı: $2)"; fails=$((fails + 1)); else echo "PASS $1"; fi
}
# Like the spool runner: the action runs with set -e in a subshell that is not part of an || list (that would disable -e).
run() {
  local rc
  set +e
  ( set -e; "$@" ) > "$T/run.out" 2>&1
  rc=$?
  set -e
  cat "$T/run.out" >> "$T/all.out"
  cat "$T/run.out"
  [ "$rc" -eq 0 ] || echo "@@EXIT=$rc"
}

make_runs completed success
make_artifact 25 1.7.7
echo "runs.json $(du -h "$T/runs.json" | cut -f1), jobs.json $(du -h "$T/jobs.json" | cut -f1), artifact $(du -h "$T/artifact.zip" | cut -f1)"

out=$(run ci_status "$C2"); check "ci_status reads multi-MB runs JSON" "success" "$out"
out=$(run ci_jobs "$C2"); check "ci_jobs lists jobs from a large response" "masaustu ✅, sunucu ❌" "$out"
check "apk_expected reads ApplicationVersion" "25 1.7.7" "$(apk_expected "$C2")"
check "apk_expected for an older commit" "24 1.7.6" "$(apk_expected "$C1")"
check "apk_expected without the csproj" "0 -" "$(apk_expected 0000000000000000000000000000000000000000)"
check "apk_current reads what phones are offered" "24 1.7.6" "$(apk_current)"

out=$(run act_verify "${C2:0:7}")
check "verify: main pushed" "✅ main GitHub'da" "$out"
check "verify: MobileApi live and /panel 200" "✅ Web sitesi/MobileApi prod" "$out"
check "verify: missing APK is reported" "❌ Telefon APK (/indir): 1.7.6 (build 24)" "$out"
check "verify: not verified while the APK is missing" "@@VERIFIED=0" "$out"
check "verify: names the missing part" "@@MISSING=apk" "$out"

make_runs in_progress ""
out=$(run act_apk "$C2"); check "apk: CI still running -> waiting" "@@APK_STATE=waiting" "$out"
make_runs completed success
DOWNLOAD_FAILS=1
out=$(run act_apk "$C2"); check "apk: failed download -> retry" "@@APK_STATE=retry" "$out"
DOWNLOAD_FAILS=0

out=$(run act_apkwatch)
check "apkwatch: unapproved mobile change is held" "@@APK_STATE=held" "$out"
refute "apkwatch: held APK is not published" '"versionCode":25' "$(cat "$APK_DIR/ArsanGazERP-Mobil.apk.meta.json" 2>/dev/null)"
echo "$C2" > "$STATE/approved-sha"
out=$(run act_apkwatch)
check "apkwatch: notices main is ahead of /indir" "@@APK_BEHIND=1" "$out"
check "apkwatch: publishes the 40 MB APK" "@@APK_STATE=published" "$out"
check "apk meta written" '"versionCode":25' "$(cat "$APK_DIR/ArsanGazERP-Mobil.apk.meta.json")"
check "apk file published intact" "$(sha256sum < "$T/ArsanGazERP-Mobil.apk")" "$(sha256sum < "$APK_DIR/ArsanGazERP-Mobil.apk")"
check "apk-sha recorded" "$C2" "$(cat "$STATE/apk-sha")"

offer 25 1.7.7
out=$(run act_verify "$C2"); check "verify: everything live -> VERIFIED=1" "@@VERIFIED=1" "$out"
out=$(run act_apkwatch); check "apkwatch: nothing to do when /indir is current" "@@APK_BEHIND=0" "$out"

rm -f "$STATE/apk-sha"
out=$(run act_apk "$C2"); check "apk: same versionCode is refused" "ApplicationVersion artırılmalı" "$out"
check "apk: refusal is a final failure" "@@APK_STATE=failed" "$out"

echo api2 > "$REPO/Tools/MobileApi/a.cs"; gc commit -qam c3; C3=$(gc rev-parse HEAD); gc update-ref refs/remotes/origin/main "$C3"
out=$(run act_verify "$C3"); check "verify: undeployed MobileApi change is reported" "@@MISSING=web" "$out"
gc update-ref refs/remotes/origin/main "$C2"
out=$(run act_verify "$C3"); check "verify: main not pushed is reported" "@@MISSING=main,web" "$out"

out=$(run write_public_status apkwatch 0); check "public status is valid JSON" '"versionCode": 25' "$(cat "$PUBLIC_STATUS")"
refute "no argv overflow in any action" "Argument list too long" "$(cat "$T/all.out")"
refute "no shell errors in any action" "command not found" "$(cat "$T/all.out")"

echo
if [ "$fails" -eq 0 ]; then echo "RELEASE TEST OK"; else echo "RELEASE TEST FAILED ($fails)"; exit 1; fi
