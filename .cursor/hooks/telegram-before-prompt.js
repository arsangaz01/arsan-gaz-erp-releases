"use strict";

// beforeSubmitPrompt: if Telegram IDE watch is dead or inbox has unclaimed work, inject a short mandatory
// reconnect instruction so the agent fixes the link before answering the user (sessionStart context is unreliable).
const fs = require("node:fs");
const path = require("node:path");

const dataRoot = path.join(process.env.LOCALAPPDATA ?? "", "ArsanGazERP", "AgentWatcherData");
const inbox = path.join(dataRoot, "CopilotInbox");
const statusPath = path.join(inbox, "ide-session-status.json");

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8").replace(/^\uFEFF/, ""));
  } catch {
    return null;
  }
}

function ideWatchHealthy() {
  const status = readJson(statusPath);
  if (!status?.updatedUtc || !status.pid) return false;
  const ageMs = Date.now() - Date.parse(status.updatedUtc);
  if (!Number.isFinite(ageMs) || ageMs > 15_000) return false;
  try {
    process.kill(status.pid, 0);
    return true;
  } catch {
    return false;
  }
}

function listPendingIds() {
  let names = [];
  try {
    names = fs.readdirSync(inbox);
  } catch {
    return [];
  }
  const pending = [];
  for (const name of names) {
    if (!name.endsWith(".request.json")) continue;
    const id = name.slice(0, -".request.json".length);
    if (names.includes(`${id}.completion.json`)) continue;
    if (names.includes(`${id}.dispatch.json`)) continue;
    pending.push(id);
  }
  return pending.sort();
}

function main() {
  let input = "";
  try {
    input = fs.readFileSync(0, "utf8");
  } catch {
    // empty stdin
  }
  void input;

  const healthy = ideWatchHealthy();
  const pending = listPendingIds();
  // Always remind: Cursor restart kills ide-watch; agents must reconnect before answering.
  const parts = [
    "TELEGRAM (her tur, cevap yazmadan önce): ide-session-status.json heartbeat ≤15 sn ve pid canlı mı bak. "
      + (healthy
        ? "Şu an canlı görünüyor; yine de pending varsa Start-Request ile al."
        : "ÖLÜ/ESKİ — hemen Tools\\CursorIdeBridge\\ide-watch.ps1 arka planda başlat "
          + "(notify AGENT_LOOP_WAKE_ide_(request|report)|IDE_WATCH_STARTED), AgentWatcher yoksa başlat. "
          + "Kullanıcı sorusundan önce bağlantıyı kur; 'bağlantı yok' deyip bırakma."),
  ];

  if (pending.length > 0) {
    parts.push(
      "TELEGRAM BEKLEYEN İSTEK: " + pending.slice(0, 5).join(", ")
        + (pending.length > 5 ? ` (+${pending.length - 5})` : "")
        + ". Hemen Start-Request.ps1 -Id <id> ile sırayla al (STARTED işle, TAKEN dokunma).",
    );
  }

  process.stdout.write(JSON.stringify({ continue: true, additional_context: parts.join("\n") }));
}

main();
