#!/bin/bash
# ArsanGazERP sunucu yayın betiği (/usr/local/sbin/arsangaz-release, root). Telefondan onaylanan değişiklikleri yayınlar:
#   pending                 cursor/server-agent'ta bekleyenler, değişen bileşenler, CI durumu
#   publish <sha> [force]   dalı main'e birleştirir, main'i push eder, MobileApi'yi test örneğinde dener ve prod'a alır
#   apk <sha>               main'deki mobil uygulamanın imzalı APK'sını GitHub Actions'tan alıp /indir'e koyar
#   rollback                prod MobileApi'yi bir önceki sürüme döndürür
#   status                  sunucudaki sürümler ve yedekler
#   stage <sha>             (yalnızca komut satırı) derleyip sadece test örneğinde dener
#   selftest-rollback       (yalnızca komut satırı) test örneğinde başarısız sağlık kontrolü + otomatik geri dönüş
#   --spool                 ServerAgent'ın istek klasörünü işler (arsangaz-release-runner.service)
# ServerAgent (kullanıcı arsangaz) bu betiği doğrudan çalıştıramaz; yalnızca istek klasörüne beyaz listedeki bir eylemi
# yazar, root'ta çalışan arsangaz-release.path/-runner.service işi yapar. Derleme ayrı ve yetkisiz arsangazbuild
# kullanıcısıyla yapılır; GitHub yazma anahtarı yalnızca root'ta durur.
set -euo pipefail
umask 022

REPO_URL=git@github.com:arsangaz01/arsan-gaz-erp-kaynak.git
WORK_BRANCH=cursor/server-agent
BASE=/var/lib/arsangaz-release
REPO=$BASE/repo
BUILD=$BASE/build
STATE=$BASE/state
LOGDIR=/var/log/arsangaz-release
KEYDIR=/etc/arsangaz/release
CONFIG=/etc/arsangaz/release.env
SPOOL=/var/lib/arsangaz-data/prod/ArsanGazERP/Release
SDK=/opt/dotnet-sdk
BUILD_USER=arsangazbuild
PROD_UNIT=arsangaz-mobileapi-prod
PROD_DIR=/opt/arsangaz/mobileapi
PROD_PORT=5077
STAGING_UNIT=arsangaz-mobileapi-staging
STAGING_DIR=/opt/arsangaz/mobileapi-staging
STAGING_PORT=5078
STAGING_DATA=/var/lib/arsangaz-data/staging
STAGING_ENV=/etc/arsangaz/mobileapi-staging.env
APK_DIR=/var/lib/arsangaz-data/prod/ArsanGazERP/MobileApi
APK_ARTIFACT=arsangaz-mobile-apk
# SHA-256 of the signing certificate of the APK installed on the phones (PC debug.keystore, verified 2026-10-12).
APK_CERT_SHA256=80ae16deb437fd9e1c7b26b3b38563977078c97a98a17f6983b7d5f19dd90153
GH_REPO=arsangaz01/arsan-gaz-erp-kaynak
CI_WORKFLOW=ci.yml
MOBILEAPI_PATHS=(Tools/MobileApi Data Models Services Tools/ErpMcpServer global.json Directory.Build.props)
DESKTOP_EXCLUDE='^(Tools/(MobileApp|MobileApi|ServerAgent|CursorTelegramBridge|PgHarness)/|CloudAgentWorker/|docs/|\.github/|.*\.md$)'

GITHUB_ACTIONS_TOKEN=""
RELEASE_TEST_BUNDLE=""
if [ -f "$CONFIG" ]; then
  GITHUB_ACTIONS_TOKEN=$(grep -E '^GITHUB_ACTIONS_TOKEN=' "$CONFIG" | tail -n1 | cut -d= -f2- || true)
  RELEASE_TEST_BUNDLE=$(grep -E '^RELEASE_TEST_BUNDLE=' "$CONFIG" | tail -n1 | cut -d= -f2- || true)
fi
export GIT_SSH_COMMAND="ssh -i $KEYDIR/github_release -o IdentitiesOnly=yes -o UserKnownHostsFile=$KEYDIR/known_hosts -o StrictHostKeyChecking=yes -o BatchMode=yes"
STAMP=$(date +%Y%m%d-%H%M%S)

say() { printf '%s\n' "$*"; }
trailer() { printf '@@%s=%s\n' "$1" "$2"; }
die() { say "❌ $*"; exit "${2:-1}"; }
git_r() { git -C "$REPO" -c core.hooksPath=/dev/null -c advice.detachedHead=false "$@"; }
short() { printf '%s' "${1:0:7}"; }
need_root() { [ "$(id -u)" = 0 ] || die "root olarak çalışmalı (sudo)." 2; }

# ---------------------------------------------------------------- git
ensure_repo() {
  if [ ! -d "$REPO/.git" ]; then
    install -d -m 0700 "$REPO"
    git init -q -b main "$REPO"
    git_r remote add origin "$REPO_URL"
    git_r config user.name "Arsan Gaz Yayin (sunucu)"
    git_r config user.email "release@arsangaz.local"
  fi
}

fetch() {
  ensure_repo
  local out
  if [ -n "$RELEASE_TEST_BUNDLE" ]; then
    out=$(git_r fetch -q --prune "$RELEASE_TEST_BUNDLE" '+refs/heads/*:refs/remotes/origin/*' 2>&1) \
      || die "Test paketi okunamadı: $(tail -n1 <<<"$out")"
    return 0
  fi
  [ -f "$KEYDIR/github_release" ] || die "Yayın anahtarı yok; install-release.sh çalıştırılmamış." 4
  if ! out=$(timeout 120 git_r fetch -q --prune origin '+refs/heads/main:refs/remotes/origin/main' \
      "+refs/heads/$WORK_BRANCH:refs/remotes/origin/$WORK_BRANCH" 2>&1); then
    # The work branch may not exist yet; retry with main alone before giving up.
    if ! out=$(timeout 120 git_r fetch -q --prune origin '+refs/heads/main:refs/remotes/origin/main' 2>&1); then
      if grep -qiE 'denied|publickey|not found|could not read' <<<"$out"; then
        die "GitHub'a bağlanılamadı: yayın anahtarı (arsangaz-release) GitHub'a henüz eklenmemiş olabilir. ($(tail -n1 <<<"$out"))" 4
      fi
      die "GitHub'dan güncelleme alınamadı: $(tail -n1 <<<"$out")" 4
    fi
  fi
}

rev() { git_r rev-parse -q --verify "$1^{commit}" 2>/dev/null || true; }

changed_files() { git_r diff --name-only "$1" "$2" -- 2>/dev/null || true; }

touches_mobileapi() { ! git_r diff --quiet "$1" "$2" -- "${MOBILEAPI_PATHS[@]}"; }
touches_mobileapp() { ! git_r diff --quiet "$1" "$2" -- Tools/MobileApp; }
touches_desktop() { changed_files "$1" "$2" | grep -vE "$DESKTOP_EXCLUDE" | grep -q .; }
touches_server_agents() { ! git_r diff --quiet "$1" "$2" -- Tools/ServerAgent Tools/CursorTelegramBridge; }

deployed_sha() { cat "$1/.release-sha" 2>/dev/null || true; }

# ---------------------------------------------------------------- GitHub API (Actions read, optional write)
gh_api() {
  local method=$1 path=$2; shift 2
  [ -n "$GITHUB_ACTIONS_TOKEN" ] || return 9
  curl -fsS --max-time 30 -X "$method" -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28" \
    -H "Authorization: Bearer $GITHUB_ACTIONS_TOKEN" "$@" "https://api.github.com/repos/$GH_REPO$path"
}

# Prints "<state> <summary>" for the newest CI run of a commit: success|failure|pending|none|unknown.
ci_status() {
  local sha=$1 json
  if [ -z "$GITHUB_ACTIONS_TOKEN" ]; then echo "unknown GitHub okuma anahtarı (GITHUB_ACTIONS_TOKEN) girilmemiş"; return; fi
  if ! json=$(gh_api GET "/actions/workflows/$CI_WORKFLOW/runs?head_sha=$sha&per_page=10" 2>/dev/null); then
    echo "unknown GitHub API'ye ulaşılamadı veya anahtar yetkisiz"; return
  fi
  python3 - "$json" <<'PY'
import json, sys
runs = json.loads(sys.argv[1]).get("workflow_runs", [])
if not runs:
    print("none bu commit için CI çalışması yok"); sys.exit()
done = [r for r in runs if r["status"] == "completed"]
if any(r["conclusion"] == "success" for r in done):
    print("success başarılı"); sys.exit()
if any(r["status"] != "completed" for r in runs):
    print("pending sürüyor"); sys.exit()
print("failure " + (done[0].get("conclusion") or "başarısız") + " — " + done[0]["html_url"])
PY
}

ci_jobs() {
  local sha=$1 runs id jobs
  [ -n "$GITHUB_ACTIONS_TOKEN" ] || return 0
  runs=$(gh_api GET "/actions/workflows/$CI_WORKFLOW/runs?head_sha=$sha&per_page=1" 2>/dev/null) || return 0
  id=$(python3 -c 'import json,sys; r=json.loads(sys.argv[1]).get("workflow_runs",[]); print(r[0]["id"] if r else "")' "$runs")
  [ -n "$id" ] || return 0
  jobs=$(gh_api GET "/actions/runs/$id/jobs?per_page=30" 2>/dev/null) || return 0
  python3 - "$jobs" <<'PY'
import json, sys
icons = {"success": "✅", "failure": "❌", "skipped": "⏭", "cancelled": "⛔", None: "⏳"}
jobs = [j for j in json.loads(sys.argv[1]).get("jobs", []) if j["name"] != "changes"]
print("   " + ", ".join(f'{j["name"]} {icons.get(j["conclusion"], j["conclusion"])}' for j in jobs))
PY
}

# ---------------------------------------------------------------- build + deploy
build_mobileapi() {
  local sha=$1 out=$2
  id "$BUILD_USER" >/dev/null 2>&1 || die "$BUILD_USER kullanıcısı yok; install-release.sh çalıştırılmamış." 5
  [ -x "$SDK/dotnet" ] || die ".NET SDK ($SDK) yok; install-release.sh çalıştırılmamış." 5
  rm -rf "$BUILD/src" "$BUILD/out"
  install -d -m 0750 -o "$BUILD_USER" -g "$BUILD_USER" "$BUILD" "$BUILD/src" "$BUILD/out" "$BUILD/home"
  git_r archive "$sha" | tar -x -C "$BUILD/src"
  chown -R "$BUILD_USER:$BUILD_USER" "$BUILD/src"
  say "🔨 MobileApi derleniyor ($(short "$sha"))..."
  if ! timeout 1500 runuser -u "$BUILD_USER" -- env -i HOME="$BUILD/home" PATH="$SDK:/usr/bin:/bin" DOTNET_ROOT="$SDK" \
      DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 NUGET_PACKAGES="$BUILD/home/.nuget/packages" \
      "$SDK/dotnet" publish "$BUILD/src/Tools/MobileApi/ArsanGazERP.MobileApi.csproj" -p:ArsanLinux=true -c Release \
      -o "$BUILD/out" -nologo -v q -clp:ErrorsOnly > "$LOGDIR/build-$STAMP.log" 2>&1; then
    say "Derleme hatası (son satırlar):"; grep -E 'error' "$LOGDIR/build-$STAMP.log" | tail -n 8 | sed -E 's#/var/lib/arsangaz-release/build/src/##'
    return 1
  fi
  [ -f "$BUILD/out/ArsanGazERP.MobileApi.dll" ] || { say "Derleme çıktısı eksik."; return 1; }
  rm -rf "$out"; mkdir -p "$out"
  cp -a "$BUILD/out/." "$out/"
  printf '%s\n' "$sha" > "$out/.release-sha"
  chown -R root:root "$out"; chmod -R u=rwX,go=rX "$out"
}

# Health: service active, /api/merhaba answers as MobileApi, /panel 200, /api/ozet requires auth, no startup failure.
health() {
  local unit=$1 port=$2 since=$3 i code
  for i in $(seq 1 30); do
    sleep 2
    systemctl is-active --quiet "$unit" || continue
    curl -fsS --max-time 5 "http://127.0.0.1:$port/api/merhaba" 2>/dev/null | grep -q '"ArsanGazERP.MobileApi"' || continue
    [ "$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$port/panel")" = 200 ] || continue
    code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 5 "http://127.0.0.1:$port/api/ozet")
    [ "$code" = 401 ] || { say "   /api/ozet beklenen 401 yerine $code döndü"; return 1; }
    if journalctl -u "$unit" --since "$since" --no-pager -o cat 2>/dev/null | grep -qE 'şeması hazırlanamadı|Mobil sunucu durdu|Unhandled exception'; then
      say "   Başlangıç günlüğünde hata var:"; journalctl -u "$unit" --since "$since" --no-pager -o cat | grep -E 'hazırlanamadı|durdu|Unhandled' | tail -n 3 | sed 's/^/   /'
      return 1
    fi
    return 0
  done
  say "   60 sn içinde sağlıklı yanıt gelmedi."
  return 1
}

prune_backups() { ls -dt "$1".bak-2* 2>/dev/null | tail -n +4 | xargs -r rm -rf; }

# Moves $new into $dir (old one -> $dir.bak-STAMP), restarts, health-checks, and on failure puts the old build back.
# Returns 0 ok, 2 rolled back, 3 rollback failed too. SIMULATE_FAIL=1 forces a failed check (refused for production).
swap_in() {
  local unit=$1 dir=$2 port=$3 new=$4 simulate=${5:-0} backup since
  [ "$simulate" = 1 ] && [ "$unit" = "$PROD_UNIT" ] && die "Prod'da hata benzetimi yapılmaz." 2
  backup="$dir.bak-$STAMP"
  since=$(date '+%Y-%m-%d %H:%M:%S')
  [ -d "$dir" ] && mv "$dir" "$backup"
  mv "$new" "$dir"
  systemctl restart "$unit"
  if [ "$simulate" != 1 ] && health "$unit" "$port" "$since"; then
    prune_backups "$dir"
    return 0
  fi
  [ "$simulate" = 1 ] && say "   (benzetim: sağlık kontrolü bilerek başarısız sayıldı)"
  say "↩️ $unit sağlık kontrolünden geçemedi; önceki sürüme dönülüyor."
  systemctl stop "$unit" || true
  rm -rf "$dir.failed-"*; mv "$dir" "$dir.failed-$STAMP"
  if [ ! -d "$backup" ]; then say "   Önceki sürüm yok; geri dönülemedi."; return 3; fi
  mv "$backup" "$dir"
  since=$(date '+%Y-%m-%d %H:%M:%S')
  systemctl start "$unit"
  if health "$unit" "$port" "$since"; then say "   Önceki sürüm ($(short "$(deployed_sha "$dir")")) yeniden çalışıyor."; return 2; fi
  say "   ⚠️ Önceki sürüm de sağlıklı açılmadı!"
  return 3
}

refresh_staging_db() {
  local password
  password=$(grep -oP '(?<=Password=)[^;]+' "$STAGING_ENV")
  timeout 120 runuser -u postgres -- psql -qAt -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS arsangaz_staging WITH (FORCE)" \
    -c "CREATE DATABASE arsangaz_staging OWNER arsangaz_staging" >/dev/null
  # Read-only dump of production into the throw-away staging database; never wait long for a production lock.
  timeout 300 runuser -u postgres -- pg_dump --lock-wait-timeout=60s --no-owner --no-privileges -d arsangaz \
    | PGPASSWORD="$password" timeout 300 psql -h 127.0.0.1 -U arsangaz_staging -d arsangaz_staging -qAt -v ON_ERROR_STOP=1 >/dev/null
}

stage() {
  local sha=$1 simulate=${2:-0} incoming rc=0
  say "🧪 Test örneği hazırlanıyor (127.0.0.1:$STAGING_PORT, prod verisinin kopyası olan ayrı veritabanı)..."
  refresh_staging_db || { say "Test veritabanı hazırlanamadı."; return 1; }
  systemctl stop "$STAGING_UNIT" 2>/dev/null || true
  rm -rf "$STAGING_DATA"; install -d -m 0700 -o arsangaz -g arsangaz "$STAGING_DATA"
  incoming=/opt/arsangaz/.incoming-staging-$STAMP
  cp -a "$BUILD/release" "$incoming"
  swap_in "$STAGING_UNIT" "$STAGING_DIR" "$STAGING_PORT" "$incoming" "$simulate" || rc=$?
  systemctl stop "$STAGING_UNIT" || true
  return $rc
}

release_mobileapi() {
  local sha=$1 rc=0 incoming
  build_mobileapi "$sha" "$BUILD/release" || return 1
  stage "$sha" || { say "❌ Test örneği başarısız; prod'a dokunulmadı."; return 1; }
  say "✅ Test örneği sağlıklı (/panel 200, /api 401)."
  incoming=/opt/arsangaz/.incoming-prod-$STAMP
  cp -a "$BUILD/release" "$incoming"
  swap_in "$PROD_UNIT" "$PROD_DIR" "$PROD_PORT" "$incoming" || rc=$?
  case $rc in
    0) say "🌐 MobileApi/web sitesi yayında: $(short "$sha") (önceki sürüm yedekte)." ;;
    2) say "⚠️ Yeni sürüm prod'da sağlık kontrolünden geçemedi; otomatik olarak önceki sürüme dönüldü."; return 2 ;;
    *) say "🚨 Prod MobileApi sağlıklı değil ve geri dönüş başarısız! Sunucuya bakılmalı."; return 3 ;;
  esac
  local public; public=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 https://arsantuptakip.com/panel || true)
  say "   Genel adres arsantuptakip.com/panel: HTTP $public"
}

# ---------------------------------------------------------------- actions
components_text() {
  local from=$1 to=$2 any=0
  if touches_mobileapi "$from" "$to"; then say "• MobileApi / web sitesi (sunucuda hemen yayınlanır)"; any=1; fi
  if touches_desktop "$from" "$to"; then say "• Masaüstü ERP / AgentWatcher (PC açılınca AutoSync uygular)"; any=1; fi
  if touches_mobileapp "$from" "$to"; then say "• Mobil uygulama (GitHub'da imzalı APK derlenip /indir'e konur)"; any=1; fi
  if touches_server_agents "$from" "$to"; then say "• Sunucu ajanı / Cursor köprüsü (otomatik yayınlanmaz; PC'den Deploy-Server.ps1)"; any=1; fi
  [ $any = 1 ] || say "• Yalnızca belge/yapılandırma"
}

act_pending() {
  fetch
  local main br target mb ahead state summary deployed
  main=$(rev origin/main); br=$(rev "origin/$WORK_BRANCH")
  [ -n "$main" ] || die "origin/main okunamadı."
  deployed=$(deployed_sha "$PROD_DIR")
  if [ -n "$br" ] && ! git_r merge-base --is-ancestor "$br" "$main"; then
    target=$br
    ahead=$(git_r rev-list --count "$main..$br")
    say "📦 $WORK_BRANCH dalında yayınlanmayı bekleyen $ahead commit var (main: $(short "$main")):"
    git_r log --no-merges --format='• %h %s' -n 15 "$main..$br"
    [ "$ahead" -gt 15 ] && say "• ..."
    mb=$(git_r merge-base "$main" "$br")
    say ""; say "Değişen bileşenler:"; components_text "$mb" "$br"
    if git_r merge-base --is-ancestor "$main" "$br"; then
      say "Birleştirme: hızlı ileri (main doğrudan bu commit'e alınır)."
    elif git_r merge-tree --write-tree "$main" "$br" >/dev/null 2>&1; then
      say "Birleştirme: main ilerlemiş; çakışmasız birleştirme commit'i oluşturulacak."
    else
      say "⚠️ main ile ÇAKIŞMA var; yayın yapılamaz. Bilgisayar açılınca çözülmeli veya ajana çakışmayı çözdürün."
      trailer CONFLICT 1
    fi
    trailer PENDING 1
  else
    target=$main
    say "📦 $WORK_BRANCH dalında bekleyen değişiklik yok. main: $(short "$main") $(git_r log -1 --format=%s "$main")"
    trailer PENDING 0
  fi
  if [ -z "$deployed" ]; then
    say "MobileApi (sunucu): sürümü bilinmiyor; yayında yeniden derlenip kurulur."; trailer MOBILEAPI 1
  elif touches_mobileapi "$deployed" "$target"; then
    say "MobileApi (sunucu): $(short "$deployed") çalışıyor; yayınlanacak yeni değişiklik var."; trailer MOBILEAPI 1
  else
    say "MobileApi (sunucu): $(short "$deployed") güncel."; trailer MOBILEAPI 0
  fi
  read -r state summary <<<"$(ci_status "$target")"
  say "CI ($(short "$target")): $summary"
  ci_jobs "$target"
  trailer TARGET "$target"
  trailer CI "$state"
}

act_publish() {
  local want=$1 force=${2:-} main br target newmain ci summary rc=0 oldmain
  [[ "$want" =~ ^[0-9a-f]{7,40}$ ]] || die "Geçersiz commit kimliği." 2
  fetch
  main=$(rev origin/main); br=$(rev "origin/$WORK_BRANCH"); oldmain=$main
  if [ -n "$br" ] && [[ "$br" == "$want"* ]]; then target=$br
  elif [[ "$main" == "$want"* ]]; then target=$main
  else die "Onaylanan commit ($want) artık dalın ucu değil; dal değişmiş olabilir. /yayin ile yeniden bakın." 6
  fi
  read -r ci summary <<<"$(ci_status "$target")"
  if [ "$ci" != success ]; then
    if [ "$force" != force ]; then die "CI durumu: $summary. Yayın durduruldu. Yine de yayınlamak için onay komutunun sonuna 'zorla' yazın." 7; fi
    say "⚠️ CI durumu: $summary — 'zorla' ile devam ediliyor."
  else
    say "✅ CI başarılı ($(short "$target"))."
  fi

  newmain=$main
  if [ "$target" = "$br" ] && ! git_r merge-base --is-ancestor "$br" "$main"; then
    if git_r merge-base --is-ancestor "$main" "$br"; then
      newmain=$br; say "🔀 main hızlı ileri alınıyor: $(short "$main") → $(short "$br")"
    else
      git_r checkout -q --detach "$main"
      if ! git_r merge -q --no-ff -m "Yayin: $WORK_BRANCH main ile birlestirildi (telefon onayi)" "$br" >/dev/null 2>&1; then
        local files; files=$(git_r diff --name-only --diff-filter=U | head -n 10)
        git_r merge --abort || true
        die "Birleştirme çakıştı; hiçbir şey push edilmedi. Çakışan dosyalar: $(tr '\n' ' ' <<<"$files")" 3
      fi
      newmain=$(git_r rev-parse HEAD); say "🔀 Birleştirme commit'i oluşturuldu: $(short "$newmain")"
    fi
    if [ -n "$RELEASE_TEST_BUNDLE" ]; then
      say "🧪 TEST MODU: main GitHub'a push edilmedi (yerel birleştirme ile devam)."
    else
      local out
      out=$(timeout 120 git_r push -q origin "$newmain:refs/heads/main" 2>&1) \
        || die "main GitHub'a gönderilemedi (yayın anahtarının yazma izni var mı?): $(tail -n1 <<<"$out")" 4
      say "⬆️ main GitHub'a gönderildi: $(short "$newmain")"
    fi
    git_r update-ref refs/remotes/origin/main "$newmain"
  fi

  local deployed; deployed=$(deployed_sha "$PROD_DIR")
  if [ -z "$deployed" ] || touches_mobileapi "$deployed" "$newmain"; then
    release_mobileapi "$newmain" || rc=$?
  else
    say "🌐 MobileApi/web sitesi değişmedi; sunucuda $(short "$deployed") çalışmaya devam ediyor."
  fi
  if touches_desktop "$oldmain" "$newmain"; then
    say "🖥 Masaüstü ERP / AgentWatcher değişiklikleri main'de; bilgisayar açılınca AutoSync otomatik derleyip uygular."
  fi
  if touches_server_agents "$oldmain" "$newmain"; then
    say "ℹ️ Sunucu ajanı / Cursor köprüsü değişti; bunlar otomatik yayınlanmaz (PC'den Deploy-Server.ps1)."
  fi
  local apk_sha; apk_sha=$(cat "$STATE/apk-sha" 2>/dev/null || true)
  if { [ -n "$apk_sha" ] && touches_mobileapp "$apk_sha" "$newmain"; } || { [ -z "$apk_sha" ] && touches_mobileapp "$oldmain" "$newmain"; }; then
    say "📱 Mobil uygulama değişti; imzalı APK GitHub'da hazırlanıyor, hazır olunca ayrıca bildirilecek."
    trailer APK needed
  else
    trailer APK none
  fi
  trailer MAIN "$newmain"
  return $rc
}

# Downloads the newest signed APK built on main whose Tools/MobileApp equals the given commit's and publishes it.
act_apk() {
  local sha=$1
  [[ "$sha" =~ ^([0-9a-f]{40})?$ ]] || die "Geçersiz commit kimliği." 2
  [ -n "$GITHUB_ACTIONS_TOKEN" ] || { say "📱 APK alınamıyor: GITHUB_ACTIONS_TOKEN girilmemiş."; trailer APK_STATE blocked; return 0; }
  fetch
  [ -n "$sha" ] || sha=$(rev origin/main)
  if [ "$(cat "$STATE/apk-sha" 2>/dev/null)" != "" ] && git_r diff --quiet "$(cat "$STATE/apk-sha")" "$sha" -- Tools/MobileApp 2>/dev/null; then
    say "📱 Yayındaki APK main ile güncel."; trailer APK_STATE published; return 0
  fi
  local runs pick
  runs=$(gh_api GET "/actions/workflows/$CI_WORKFLOW/runs?branch=main&per_page=30") || { say "GitHub API okunamadı."; trailer APK_STATE waiting; return 0; }
  pick=$(python3 - "$runs" <<'PY'
import json, sys
for r in json.loads(sys.argv[1]).get("workflow_runs", []):
    print(r["id"], r["head_sha"], r["status"], r.get("conclusion") or "-")
PY
)
  local id head status conclusion waiting=0 found=""
  while read -r id head status conclusion; do
    [ -n "$id" ] || continue
    git_r cat-file -e "$head^{commit}" 2>/dev/null || continue
    git_r diff --quiet "$head" "$sha" -- Tools/MobileApp || continue
    if [ "$status" != completed ]; then waiting=1; continue; fi
    [ "$conclusion" = success ] || continue
    local arts url
    arts=$(gh_api GET "/actions/runs/$id/artifacts") || continue
    url=$(python3 -c 'import json,sys; a=[x for x in json.loads(sys.argv[1]).get("artifacts",[]) if x["name"]==sys.argv[2] and not x["expired"]]; print(a[0]["archive_download_url"] if a else "")' "$arts" "$APK_ARTIFACT")
    [ -n "$url" ] && { found="$id $url"; break; }
  done <<<"$pick"
  if [ -z "$found" ]; then
    if [ $waiting = 1 ]; then say "📱 APK derlemesi GitHub'da sürüyor."; trailer APK_STATE waiting; return 0; fi
    local mark="$STATE/apk-dispatched-$sha"
    if [ -f "$mark" ]; then say "📱 APK derlemesi istendi, henüz başlamadı."; trailer APK_STATE waiting; return 0; fi
    if gh_api POST "/actions/workflows/$CI_WORKFLOW/dispatches" -d '{"ref":"main","inputs":{"android_release":"true"}}' >/dev/null 2>&1; then
      touch "$mark"; say "📱 GitHub'da imzalı APK derlemesi başlatıldı."; trailer APK_STATE waiting; return 0
    fi
    say "📱 Bu sürüm için APK yok ve derleme başlatılamadı (anahtarın Actions yazma izni yok olabilir)."; trailer APK_STATE failed; return 0
  fi

  local tmp=$BUILD/apk
  rm -rf "$tmp"; install -d -m 0700 "$tmp"
  curl -fsSL --max-time 300 -H "Authorization: Bearer $GITHUB_ACTIONS_TOKEN" -o "$tmp/a.zip" "${found#* }" \
    || { say "APK indirilemedi."; trailer APK_STATE failed; return 0; }
  (cd "$tmp" && unzip -q a.zip) || { say "APK arşivi açılamadı."; trailer APK_STATE failed; return 0; }
  [ -f "$tmp/ArsanGazERP-Mobil.apk" ] && [ -f "$tmp/apk-info.json" ] || { say "Arşivde APK veya apk-info.json yok."; trailer APK_STATE failed; return 0; }
  local name code cert current
  read -r name code cert <<<"$(python3 -c 'import json,sys; d=json.load(open(sys.argv[1])); print(d["versionName"], int(d["versionCode"]), d["certSha256"].lower())' "$tmp/apk-info.json")"
  if command -v apksigner >/dev/null 2>&1; then
    cert=$(apksigner verify --print-certs "$tmp/ArsanGazERP-Mobil.apk" 2>/dev/null | grep -m1 'certificate SHA-256 digest' | awk '{print $NF}')
  fi
  [ "$cert" = "$APK_CERT_SHA256" ] || { say "❌ APK imza sertifikası telefondakiyle aynı değil; yayınlanmadı."; trailer APK_STATE failed; return 0; }
  current=$(python3 -c 'import json,sys; print(int(json.load(open(sys.argv[1]))["versionCode"]))' "$APK_DIR/ArsanGazERP-Mobil.apk.meta.json" 2>/dev/null || echo 0)
  if [ "$code" -le "$current" ]; then
    say "❌ APK sürüm numarası ($code) yayındakinden ($current) büyük değil; telefonlar güncellemeyi almaz. Tools/MobileApp/ArsanGazMobile.csproj içinde ApplicationVersion artırılmalı."
    trailer APK_STATE failed; return 0
  fi
  [ -f "$APK_DIR/ArsanGazERP-Mobil.apk" ] && cp -p "$APK_DIR/ArsanGazERP-Mobil.apk" "$APK_DIR/ArsanGazERP-Mobil.apk.bak-$STAMP"
  install -m 0600 -o arsangaz -g arsangaz "$tmp/ArsanGazERP-Mobil.apk" "$APK_DIR/ArsanGazERP-Mobil.apk.upload"
  printf '{"versionName":"%s","versionCode":%d}\n' "$name" "$code" > "$tmp/meta"
  install -m 0600 -o arsangaz -g arsangaz "$tmp/meta" "$APK_DIR/ArsanGazERP-Mobil.apk.meta.json.upload"
  mv -f "$APK_DIR/ArsanGazERP-Mobil.apk.upload" "$APK_DIR/ArsanGazERP-Mobil.apk"
  mv -f "$APK_DIR/ArsanGazERP-Mobil.apk.meta.json.upload" "$APK_DIR/ArsanGazERP-Mobil.apk.meta.json"
  ls -t "$APK_DIR"/ArsanGazERP-Mobil.apk.bak-2* 2>/dev/null | tail -n +4 | xargs -r rm -f
  printf '%s\n' "$sha" > "$STATE/apk-sha"
  rm -rf "$tmp"
  say "📱 Yeni APK yayında: $name (build $code). Telefonlar uygulamayı açınca güncelleme önerir; arsantuptakip.com/indir"
  trailer APK_STATE published
}

act_rollback() {
  local current backup since
  backup=$(ls -dt "$PROD_DIR".bak-2* 2>/dev/null | head -n1 || true)
  [ -n "$backup" ] || die "Geri dönülecek yedek yok."
  current=$(deployed_sha "$PROD_DIR")
  say "↩️ MobileApi geri alınıyor: $(short "${current:-bilinmiyor}") → $(short "$(deployed_sha "$backup")") ($(basename "$backup"))"
  systemctl stop "$PROD_UNIT"
  mv "$PROD_DIR" "$PROD_DIR.rolledback-$STAMP"
  mv "$backup" "$PROD_DIR"
  since=$(date '+%Y-%m-%d %H:%M:%S')
  systemctl start "$PROD_UNIT"
  if health "$PROD_UNIT" "$PROD_PORT" "$since"; then
    ls -dt "$PROD_DIR".rolledback-* 2>/dev/null | tail -n +3 | xargs -r rm -rf
    say "✅ Geri alındı; web sitesi ve mobil API önceki sürümle çalışıyor."
  else
    say "⚠️ Önceki sürüm sağlıklı açılmadı; geri alınan sürüm geri yükleniyor."
    systemctl stop "$PROD_UNIT"; mv "$PROD_DIR" "$backup"; mv "$PROD_DIR.rolledback-$STAMP" "$PROD_DIR"; systemctl start "$PROD_UNIT"
    return 3
  fi
}

act_status() {
  local b
  say "MobileApi prod: $(short "$(deployed_sha "$PROD_DIR")")$( [ -z "$(deployed_sha "$PROD_DIR")" ] && echo 'bilinmiyor') · $(systemctl is-active "$PROD_UNIT")"
  for b in $(ls -dt "$PROD_DIR".bak-2* 2>/dev/null); do say "  yedek $(basename "$b"): $(short "$(deployed_sha "$b")")$( [ -z "$(deployed_sha "$b")" ] && echo 'sürüm bilinmiyor')"; done
  say "APK: $(cat "$APK_DIR/ArsanGazERP-Mobil.apk.meta.json" 2>/dev/null || echo yok)"
  say "Son yayın günlüğü: $(ls -t "$LOGDIR"/*.log 2>/dev/null | head -n1 || echo yok)"
  [ -n "$RELEASE_TEST_BUNDLE" ] && say "🧪 TEST MODU açık (push yapılmaz)."
  return 0
}

act_stage() {
  local want=$1 sha
  fetch; sha=$(rev "$want"); [ -n "$sha" ] || sha=$(rev "origin/$want"); [ -n "$sha" ] || die "Commit bulunamadı: $want"
  build_mobileapi "$sha" "$BUILD/release" && stage "$sha" && say "✅ Test örneği sağlıklı: $(short "$sha")"
}

act_selftest_rollback() {
  [ -d "$STAGING_DIR" ] || die "Önce 'stage main' ile test örneğine sağlıklı bir sürüm kurun."
  local before rc=0 incoming
  before=$(deployed_sha "$STAGING_DIR")
  incoming=/opt/arsangaz/.incoming-staging-$STAMP
  cp -a "$STAGING_DIR" "$incoming"; printf 'simulated-%s\n' "$STAMP" > "$incoming/.release-sha"
  refresh_staging_db
  swap_in "$STAGING_UNIT" "$STAGING_DIR" "$STAGING_PORT" "$incoming" 1 || rc=$?
  local after; after=$(deployed_sha "$STAGING_DIR")
  systemctl stop "$STAGING_UNIT" || true
  if [ $rc = 2 ] && [ "$after" = "$before" ]; then say "✅ Otomatik geri dönüş çalıştı (test örneği yine $(short "$before"))."; return 0; fi
  say "❌ Geri dönüş testi başarısız (kod $rc, sürüm $after)."; return 1
}

run_action() {
  local action=$1; shift
  case "$action" in
    pending) act_pending ;;
    publish) act_publish "${1:-}" "${2:-}" ;;
    apk) act_apk "${1:-}" ;;
    rollback) act_rollback ;;
    status) act_status ;;
    stage) act_stage "${1:-origin/main}" ;;
    selftest-rollback) act_selftest_rollback ;;
    *) die "Bilinmeyen eylem: $action" 2 ;;
  esac
}

# ---------------------------------------------------------------- spool (requests written by ServerAgent)
spool() {
  local req id action a1 a2 out code
  install -d -m 0700 -o arsangaz -g arsangaz "$SPOOL" "$SPOOL/requests" "$SPOOL/results"
  for req in $(ls -tr "$SPOOL"/requests/*.req 2>/dev/null); do
    id=$(basename "$req" .req)
    [[ "$id" =~ ^[0-9]{14}-[a-z]+-[0-9a-f]{8}$ ]] || { rm -f "$req"; continue; }
    action=$(grep -m1 -E '^action=' "$req" | cut -d= -f2 || true)
    a1=$(grep -m1 -E '^arg1=' "$req" | cut -d= -f2 || true)
    a2=$(grep -m1 -E '^arg2=' "$req" | cut -d= -f2 || true)
    rm -f "$req"
    out="$SPOOL/results/$id.out"
    case "$action" in pending|publish|apk|rollback|status) ;; *) printf 'Eylem reddedildi.\n' > "$out"; code=2 ;; esac
    [[ "$a1" =~ ^[0-9a-f]{0,40}$ ]] && [[ "$a2" =~ ^(force)?$ ]] || { printf 'Parametre reddedildi.\n' > "$out"; code=2; }
    if [ -z "${code:-}" ]; then
      STAMP=$(date +%Y%m%d-%H%M%S)
      set +e
      ( set -e; run_action "$action" $a1 $a2 ) > "$out" 2>&1
      code=$?
      set -e
      { echo "=== $(date -Is) $id ($action $a1 $a2) çıkış $code"; cat "$out"; } >> "$LOGDIR/spool.log"
    fi
    chown arsangaz:arsangaz "$out"; chmod 600 "$out"
    printf '%s\n' "$code" > "$SPOOL/results/$id.done.tmp"
    chown arsangaz:arsangaz "$SPOOL/results/$id.done.tmp"; chmod 600 "$SPOOL/results/$id.done.tmp"
    mv -f "$SPOOL/results/$id.done.tmp" "$SPOOL/results/$id.done"
    unset code
  done
  find "$SPOOL/results" -type f -mtime +7 -delete 2>/dev/null || true
}

main() {
  need_root
  # The build user must be able to traverse $BASE to reach $BUILD; everything else below it stays root-only.
  install -d -m 0711 "$BASE"
  install -d -m 0700 "$STATE" "$LOGDIR"
  exec 9>"$BASE/.lock"
  flock -w 7200 9 || die "Başka bir yayın işlemi sürüyor."
  if [ "${1:-}" = --spool ]; then spool; return; fi
  [ $# -ge 1 ] || { sed -n '2,13p' "$0"; exit 2; }
  run_action "$@" 2>&1 | tee -a "$LOGDIR/$STAMP-$1.log"
  return "${PIPESTATUS[0]}"
}

main "$@"
