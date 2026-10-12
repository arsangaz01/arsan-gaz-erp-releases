"use strict";

// ArsanTüpTakip server Cursor agent (service arsantuptakip-agent, user tuptakipagent). Takes development requests that
// @TuptakipArsanbot (telegram-bot.js) writes to the shared queue, runs them with the Cursor SDK in its own clone and writes
// the report back; the bot delivers it to Telegram. Modelled on Tools/CursorTelegramBridge/server-bridge.js but independent:
// own user, clone, branch, queue and key file. The clone's origin is the read-only mirror kept by arsantuptakip-release
// (no GitHub key here); commits on the work branch are handed to the release script as a git bundle.

const fs = require("node:fs/promises");
const fsSync = require("node:fs");
const path = require("node:path");
const { execFile } = require("node:child_process");
const queue = require("./queue");
const { WORK_BRANCH, outOfScope, buildPrompt, describeToolCall } = require("./agent-rules");
const { framesForAttachments } = require("./video-frames");

const env = process.env;
const workspace = env.TUPTAKIP_AGENT_WORKSPACE || "/home/tuptakipagent/arsan-gaz-erp";
const stateDir = env.TUPTAKIP_AGENT_STATE_DIR || "/var/lib/arsantuptakip-agent";
const queueRoot = env.TUPTAKIP_QUEUE_DIR || "/var/lib/arsantuptakip-queue";
const mirror = env.TUPTAKIP_AGENT_MIRROR || "/var/lib/arsantuptakip-release/mirror.git";
const model = env.TUPTAKIP_AGENT_MODEL || "composer-2.5";
const autoReview = env.TUPTAKIP_AGENT_AUTO_REVIEW !== "0";
const bundlePath = path.join(queueRoot, "outgoing", "agent-branch.bundle");

const POLL_INTERVAL_MS = 5000;
const CANCEL_POLL_MS = 5000;
const RUN_MAX_MS = 60 * 60_000;
const CONVERSATION_IDLE_MS = 3 * 60 * 60_000;
const REQUEST_MAX_LENGTH = 4096;
const IMAGE_EXTENSIONS = new Map([[".jpg", "image/jpeg"], [".jpeg", "image/jpeg"], [".png", "image/png"], [".webp", "image/webp"]]);

const PRE_PUSH_HOOK = `#!/bin/sh
# ArsanTupTakip sunucu ajani: bu klondan push yapilmaz; commit'ler arsantuptakip-release ile yayinlanir.
echo "push kapali: degisiklikler /yayin ile yayinlanir" >&2
exit 1
`;

let sdk = null;
const loadSdk = () => (sdk ??= require("@cursor/sdk"));

let stopping = false;
let processing = false;
let lastReason = null;
let keyCheck = { key: null, ok: false, detail: null };
const status = { ready: false, reason: "başlıyor", current: null, lastResult: null, model };

function log(message) {
  process.stdout.write(`${new Date().toISOString()} ${message}\n`);
}

async function writeStatus() {
  await queue.writeJsonAtomically(path.join(queueRoot, "agent-status.json"), { ...status, updatedUtc: new Date().toISOString() })
    .catch((error) => log(`Durum dosyası yazılamadı: ${error.message}`));
}

function git(args, options = {}) {
  return new Promise((resolve, reject) => {
    execFile("git", args, { cwd: options.cwd ?? workspace, timeout: options.timeoutMs ?? 120_000, maxBuffer: 32 * 1024 * 1024 }, (error, stdout, stderr) => {
      if (error) reject(new Error(String(stderr || error.message).trim().split("\n").pop()));
      else resolve(stdout.trim());
    });
  });
}

async function ensureWorkspace() {
  if (fsSync.existsSync(path.join(workspace, ".git"))) return null;
  const mirrorReady = await git(["--git-dir", mirror, "rev-parse", "--verify", "-q", "refs/heads/main"], { cwd: "/" }).then(() => true, () => false);
  if (!mirrorReady) return "Depo aynası henüz hazır değil (arsantuptakip-release sync)";
  try {
    await git(["clone", "--quiet", mirror, workspace], { cwd: path.dirname(workspace), timeoutMs: 15 * 60_000 });
    await git(["config", "user.name", "ArsanTupTakip Sunucu Cursor Ajani"]);
    await git(["config", "user.email", "tuptakipagent@arsantuptakip.local"]);
    await fs.writeFile(path.join(workspace, ".git", "hooks", "pre-push"), PRE_PUSH_HOOK, { mode: 0o755 });
    await git(["checkout", "-q", "-B", WORK_BRANCH, "origin/main"]);
    log(`Depo klonlandı: ${workspace} (dal ${WORK_BRANCH})`);
    return null;
  } catch (error) {
    await fs.rm(workspace, { recursive: true, force: true }).catch(() => undefined);
    return "Depo klonlanamadı: " + error.message;
  }
}

// Fast-forwards the work branch to origin/main when it has no unpublished commits and no local edits.
async function refreshWorkspace() {
  try {
    await git(["fetch", "--quiet", "origin", "--prune"]);
    if ((await git(["status", "--porcelain", "--untracked-files=no"])) !== "") return "çalışma klasöründe kaydedilmemiş değişiklik var; güncellenmedi";
    await git(["merge-base", "--is-ancestor", "HEAD", "origin/main"]).then(
      () => git(["merge", "--quiet", "--ff-only", "origin/main"]),
      () => undefined);
    return null;
  } catch (error) {
    return "depo güncellenemedi: " + error.message;
  }
}

async function unpublishedCommits() {
  const list = await git(["log", "--format=%h %s", "origin/main..HEAD"]).catch(() => "");
  return list ? list.split("\n") : [];
}

async function checkKey() {
  const key = env.CURSOR_API_KEY?.trim();
  if (!key) return "CURSOR_API_KEY eksik (/etc/arsantuptakip/agent.env)";
  if (keyCheck.key !== key) {
    keyCheck = { key, ok: false, detail: null };
    const { Cursor, AuthenticationError } = loadSdk();
    try {
      await Cursor.me({ apiKey: key });
      keyCheck.ok = true;
      log("Cursor anahtarı doğrulandı.");
    } catch (error) {
      keyCheck.detail = error instanceof AuthenticationError ? "CURSOR_API_KEY geçersiz veya süresi dolmuş" : null;
      keyCheck.ok = !keyCheck.detail;
      if (!keyCheck.detail) log(`Cursor anahtarı şimdi doğrulanamadı: ${error.message}`);
    }
  }
  return keyCheck.ok ? null : keyCheck.detail;
}

async function openAgent() {
  const { Agent, AgentNotFoundError } = loadSdk();
  const statePath = path.join(stateDir, "agent-telegram.json");
  const state = (await queue.readJson(statePath)) ?? {};
  const marker = await queue.readNewConversationMarker(queueRoot);
  const options = { model: { id: model }, local: { cwd: workspace, autoReview, settingSources: ["project"] } };
  let agent = null;
  if (state.agentId && state.model === model && state.marker === marker
      && Date.now() - Date.parse(state.lastUsedUtc ?? 0) < CONVERSATION_IDLE_MS) {
    try {
      agent = await Agent.resume(state.agentId, options);
    } catch (error) {
      if (!(error instanceof AgentNotFoundError)) throw error;
    }
  }
  if (!agent) {
    agent = await Agent.create({ ...options, name: "ArsanTupTakip Telegram (sunucu)" });
    log(`Yeni Cursor konuşması açıldı: ${agent.agentId}`);
  }
  await queue.writeJsonAtomically(statePath, { agentId: agent.agentId, model, marker, lastUsedUtc: new Date().toISOString() }, 0o600);
  return agent;
}

async function loadImages(paths, maxBytes = 12 * 1024 * 1024) {
  const images = [];
  let total = 0;
  for (const file of paths) {
    const mimeType = IMAGE_EXTENSIONS.get(path.extname(file).toLowerCase());
    if (!mimeType) continue;
    const data = await fs.readFile(file).catch(() => null);
    if (!data || total + data.length > maxBytes) continue;
    total += data.length;
    images.push({ data: data.toString("base64"), mimeType });
  }
  return images;
}

async function send(agent, text, imagePaths) {
  const images = await loadImages(imagePaths);
  if (images.length === 0) return agent.send(text);
  try {
    return await agent.send({ text, images });
  } catch (error) {
    if (error instanceof loadSdk().AuthenticationError) throw error;
    log(`Görselli gönderim başarısız, yalnızca metinle deneniyor: ${error?.message ?? error}`);
    return agent.send(text);
  }
}

// Hands the work branch to arsantuptakip-release; out-of-scope commits are not handed over.
async function exportBranch(startHead) {
  const commits = await unpublishedCommits();
  if (commits.length === 0) {
    await fs.rm(bundlePath, { force: true });
    return "";
  }
  const files = (await git(["diff", "--name-only", "origin/main...HEAD"])).split("\n").filter(Boolean);
  const outside = outOfScope(files);
  const changedNow = startHead && (await git(["rev-parse", "HEAD"])) !== startHead;
  if (outside.length > 0) {
    await fs.rm(bundlePath, { force: true });
    return `\n\n⛔ Otomatik kontrol: ${WORK_BRANCH} dalındaki commit'ler Tools/ArsanTupTakip dışındaki dosyalara dokunuyor `
      + `(${outside.slice(0, 5).join(", ")}${outside.length > 5 ? " ..." : ""}). Bu değişiklikler yayına alınamaz; ajandan geri almasını isteyin.`;
  }
  const temporary = `${bundlePath}.${process.pid}.tmp`;
  await git(["bundle", "create", "--quiet", temporary, `refs/heads/${WORK_BRANCH}`, "^origin/main"]);
  await fs.chmod(temporary, 0o660);
  await fs.rename(temporary, bundlePath);
  return `\n\n📦 Yayın bekleyen ${commits.length} commit (${changedNow ? "bu istekte yeni commit var" : "bu istekte yeni commit yok"}). `
    + "Yayınlamak için /yayin yazın.";
}

async function processRequest(id) {
  const nowIso = () => new Date().toISOString();
  let agent;
  let cancelWatch;
  status.current = { id, startedUtc: nowIso() };
  await writeStatus();
  log(`İstek işleniyor: ${id}`);
  try {
    if (await queue.isCancelled(queueRoot, id)) {
      await queue.writeResult(queueRoot, id, { status: "cancelled", report: "⏹ İş başlamadan durduruldu." });
      return;
    }
    const request = await queue.readRequest(queueRoot, id);
    const text = typeof request?.text === "string" ? request.text.slice(0, REQUEST_MAX_LENGTH) : "";
    if (!text.trim()) throw new Error("İstek metni boş.");
    const attachmentsDir = path.join(queue.requestDir(queueRoot, id), "attachments");
    const attachmentPaths = (await fs.readdir(attachmentsDir).catch(() => [])).filter((name) => !name.endsWith(".frames"))
      .map((name) => path.join(attachmentsDir, name));
    const video = await framesForAttachments(attachmentPaths);
    const workspaceNote = await refreshWorkspace();
    const pending = await unpublishedCommits();
    const pendingNote = pending.length > 0
      ? `Not: ${WORK_BRANCH} dalında henüz yayınlanmamış ${pending.length} commit var (${pending.slice(0, 5).join("; ")}). Bunların üzerine çalış.`
      : "";
    const startHead = await git(["rev-parse", "HEAD"]).catch(() => null);

    agent = await openAgent();
    const prompt = buildPrompt({ text, attachmentPaths, attachmentNote: video.note, workspaceNote: workspaceNote ?? "", pendingNote });
    const run = await send(agent, prompt, [...attachmentPaths, ...video.images]);
    await queue.writeProgress(queueRoot, id, { stage: "working", steps: [] });
    log(`İstek Cursor ajanına gönderildi: ${id} (agent ${agent.agentId}, run ${run.id})`);

    let cancelRequested = false;
    let timedOut = false;
    const startedMs = Date.now();
    cancelWatch = setInterval(async () => {
      if (cancelRequested) return;
      timedOut = Date.now() - startedMs > RUN_MAX_MS;
      if (!timedOut && !(await queue.isCancelled(queueRoot, id).catch(() => false))) return;
      cancelRequested = true;
      log(timedOut ? `Süre sınırı aşıldı: ${id}` : `Durdurma isteği alındı: ${id}`);
      run.cancel().catch((error) => log(`Durdurma başarısız (${id}): ${error?.message ?? error}`));
    }, CANCEL_POLL_MS);

    let streamedText = "";
    const steps = [];
    const seen = new Set();
    for await (const message of run.stream()) {
      if (message.type === "tool_call" && message.status === "running" && !seen.has(message.call_id)) {
        seen.add(message.call_id);
        steps.push(describeToolCall(message.name, message.args));
        if (steps.length > 40) steps.shift();
        await queue.writeProgress(queueRoot, id, { stage: "working", steps }).catch(() => undefined);
        continue;
      }
      if (message.type !== "assistant") continue;
      for (const block of message.message?.content ?? []) {
        if (block.type === "text") streamedText += block.text;
      }
    }
    const result = await run.wait();
    clearInterval(cancelWatch);
    const handover = await exportBranch(startHead).catch((error) => `\n\n⚠️ Commit'ler yayın kuyruğuna alınamadı: ${error.message}`);
    if (cancelRequested || result.status === "cancelled") {
      const report = (timedOut ? `⏱ İş ${RUN_MAX_MS / 60_000} dakikayı aştığı için durduruldu.` : "⏹ İş kullanıcı isteğiyle durduruldu.") + handover;
      await queue.writeResult(queueRoot, id, { status: "cancelled", report });
      status.lastResult = { id, status: "cancelled", updatedUtc: nowIso() };
      return;
    }
    if (result.status !== "finished") throw new Error(`Cursor ajanı işi tamamlayamadı (${result.error?.message ?? result.status}).`);
    const dirty = await git(["status", "--porcelain", "--untracked-files=no"]).catch(() => "");
    const report = ((result.result || streamedText).trim() || "Cursor ajanı işi bitirdi ancak metin raporu üretmedi.") + handover
      + (dirty ? "\n\n⚠️ Çalışma klasöründe commit edilmemiş değişiklik kaldı; bunlar yayına alınmaz." : "");
    await queue.writeResult(queueRoot, id, { status: "completed", report });
    status.lastResult = { id, status: "completed", updatedUtc: nowIso() };
    log(`Cursor yanıtı tamamlandı: ${id} (${result.durationMs ?? "?"} ms)`);
  } catch (error) {
    const sdkModule = sdk;
    const detail = sdkModule && error instanceof sdkModule.AuthenticationError
      ? "Sunucudaki Cursor anahtarı (CURSOR_API_KEY) geçersiz veya süresi dolmuş."
      : (error?.message ?? String(error));
    log(`İstek başarısız (${id}): ${detail}`);
    status.lastResult = { id, status: "failed", updatedUtc: nowIso() };
    await queue.writeResult(queueRoot, id, { status: "failed", report: `❌ Sunucudaki Cursor ajanı isteği tamamlayamadı: ${detail}` })
      .catch((writeError) => log(`Hata sonucu yazılamadı (${id}): ${writeError.message}`));
  } finally {
    clearInterval(cancelWatch);
    status.current = null;
    if (agent) await agent[Symbol.asyncDispose]?.().catch(() => undefined);
    await writeStatus();
  }
}

// A claim left over from a previous process was interrupted; it is reported, not re-run.
async function recoverInterrupted() {
  for (const id of await queue.listActive(queueRoot)) {
    await queue.writeResult(queueRoot, id, {
      status: "failed",
      report: "⚠️ Sunucudaki Cursor ajanı bu iş sürerken yeniden başladı; sonuç belirsiz ve iş otomatik tekrar çalıştırılmadı. Gerekirse isteği yeniden gönderin.",
    }).catch((error) => log(`Yarım kalan iş kapatılamadı (${id}): ${error.message}`));
    log(`Yarım kalan iş belirsiz olarak kapatıldı: ${id}`);
  }
}

async function readiness() {
  const keyProblem = await checkKey();
  if (keyProblem) return keyProblem;
  return ensureWorkspace();
}

async function poll() {
  if (processing || stopping) return;
  processing = true;
  try {
    const problem = await readiness();
    status.ready = !problem;
    status.reason = problem;
    if (problem !== lastReason) {
      log(problem ? `Bekliyor: ${problem}` : "Hazır: ArsanTüpTakip geliştirme isteklerini işleyecek.");
      lastReason = problem;
    }
    if (problem) {
      for (const id of await queue.listPending(queueRoot)) {
        if (await queue.claim(queueRoot, id)) {
          await queue.writeResult(queueRoot, id, { status: "failed", report: `❌ Sunucudaki Cursor ajanı hazır değil: ${problem}` });
        }
      }
      return;
    }
    for (const id of await queue.listPending(queueRoot)) {
      if (stopping) break;
      if (!(await queue.claim(queueRoot, id))) continue;
      await processRequest(id);
      break;
    }
  } catch (error) {
    status.reason = "Kuyruk okunamadı: " + error.message;
    if (status.reason !== lastReason) log(status.reason);
    lastReason = status.reason;
  } finally {
    processing = false;
    await writeStatus();
  }
}

async function main() {
  if (process.argv[2] === "--status") {
    console.log(JSON.stringify({ reason: await readiness(), workspace, stateDir, queueRoot, model }, null, 2));
    return;
  }
  await fs.mkdir(stateDir, { recursive: true });
  await queue.ensureLayout(queueRoot);
  log(`ArsanTüpTakip Cursor ajanı başladı (pid ${process.pid}, depo ${workspace}, model ${model}).`);
  await recoverInterrupted();
  const timer = setInterval(() => poll().catch((error) => log(`Döngü hatası: ${error.message}`)), POLL_INTERVAL_MS);
  poll();
  const shutdown = () => {
    stopping = true;
    clearInterval(timer);
    log("ArsanTüpTakip Cursor ajanı kapanıyor.");
    setTimeout(() => process.exit(0), processing ? 3000 : 0);
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

if (require.main === module) {
  main().catch((error) => {
    log(`ArsanTüpTakip Cursor ajanı hatası: ${error?.stack ?? error}`);
    process.exitCode = 1;
  });
}

module.exports = { PRE_PUSH_HOOK };
