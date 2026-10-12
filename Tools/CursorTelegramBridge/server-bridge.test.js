"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { describeReleaseStatus, SERVER_RULES } = require("./server-bridge");

const status = {
  updatedAt: "2026-10-12T16:40:00+03:00",
  lastAction: "apk",
  lastExitCode: 0,
  main: { sha: "0311907a910a61b2427165c97d5d3971209609b2", subject: "Sefer bazlı tüp takibi" },
  workBranch: { name: "cursor/server-agent", commitsWaitingForYayin: 0 },
  mobileApi: { deployedSha: "0311907a910a61b2427165c97d5d3971209609b2", service: "active", mainNotYetDeployed: false },
  apk: { versionName: "1.7.6", versionCode: 24, builtFromSha: "0311907a910a61b2427165c97d5d3971209609b2", mainNotYetPublished: false },
};

test("release status names what is live", () => {
  const text = describeReleaseStatus(status, new Date("2026-10-12T16:45:00+03:00"));
  assert.match(text, /5 dk önce/);
  assert.match(text, /main: 0311907 Sefer bazlı tüp takibi/);
  assert.match(text, /MobileApi \(prod\): 0311907 \(active\) — main ile güncel/);
  assert.match(text, /APK \(\/indir\): 1\.7\.6 \(build 24\) — main ile güncel/);
  assert.match(text, /yayın bekleyen commit yok/);
});

test("release status flags parts that are not published yet", () => {
  const text = describeReleaseStatus({
    ...status,
    lastExitCode: 126,
    workBranch: { name: "cursor/server-agent", commitsWaitingForYayin: 2 },
    apk: { versionName: "1.7.5", versionCode: 23, mainNotYetPublished: true },
  });
  assert.match(text, /çıkış kodu 126/);
  assert.match(text, /2 commit \/yayin onayı bekliyor/);
  assert.match(text, /1\.7\.5 \(build 23\) — main'deki mobil uygulama değişiklikleri henüz APK olarak yayında DEĞİL/);
});

test("missing status file is reported, not guessed", () => {
  assert.match(describeReleaseStatus(null), /okunamadı/);
});

test("rules cover panel updates and the PostgreSQL schema", () => {
  const rules = SERVER_RULES.join("\n");
  assert.match(rules, /PostgresSchema\.cs/);
  assert.match(rules, /panel\.html/);
  assert.match(rules, /henüz yayında DEĞİL/);
});
