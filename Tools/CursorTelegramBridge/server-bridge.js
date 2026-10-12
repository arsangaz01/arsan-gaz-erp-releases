"use strict";

// Server (Linux) counterpart of bridge.js: while the office PC is off, the arsangaz-cursoragent service (user
// cursoragent) takes Cursor requests from the production queue through cursor-inbox.sh, runs them with the Cursor SDK
// in a clone of the private repository and writes the result back; ServerAgent/MobileApi deliver it to Telegram.
// Paths come from the environment (Tools/ServerAgent/deploy/arsangaz-cursoragent.service).

const fs = require("node:fs/promises");
const fsSync = require("node:fs");
const path = require("node:path");
const { execFile } = require("node:child_process");
const { Agent, Cursor, AgentNotFoundError, AuthenticationError } = require("@cursor/sdk");
const { buildPrompt, describeToolCall } = require("./bridge");
const { maskPii } = require("./pii-mask");
const { createInboxClient, isPcOnline, pickClaimable, DEFAULT_WAIT_MINUTES } = require("./server-inbox");

const env = process.env;
const workspace = env.CURSOR_AGENT_WORKSPACE || "/home/cursoragent/arsan-gaz-erp";
const stateDir = env.CURSOR_AGENT_STATE_DIR || "/var/lib/arsangaz-cursoragent";
const repoUrl = env.CURSOR_AGENT_REPO || "git@github.com:arsangaz01/arsan-gaz-erp-kaynak.git";
const workBranch = env.CURSOR_AGENT_BRANCH || "cursor/server-agent";
const waitMinutes = Number(env.CURSOR_AGENT_WAIT_MINUTES ?? DEFAULT_WAIT_MINUTES);
const model = env.CURSOR_BRIDGE_MODEL || "composer-2.5";
const autoReview = env.CURSOR_AGENT_AUTO_REVIEW !== "0";
const inbox = createInboxClient({
  helper: env.CURSOR_AGENT_INBOX_HELPER || "/opt/arsangaz-cursoragent/bin/cursor-inbox.sh",
  useSudo: env.CURSOR_AGENT_NO_SUDO !== "1",
});

const POLL_INTERVAL_MS = 15_000;
const CLONE_RETRY_MS = 10 * 60_000;
const CANCEL_POLL_MS = 5000;
const RUN_MAX_MS = 60 * 60_000;
// A Telegram follow-up ("evet") continues the previous conversation; after a long pause a new one starts.
const CONVERSATION_IDLE_MS = 3 * 60 * 60_000;
const REQUEST_MAX_LENGTH = 4096;
const CONTEXT_MAX_LENGTH = 40_000;
const TEST_ID_PREFIX = "servertest-";
const REPORT_HEADER = "☁️ Sunucudaki Cursor ajanı (bilgisayar kapalıyken):\n\n";

const SERVER_RULES = [
  "Ortam: Oracle Linux sunucusu, kullanıcı cursoragent (sudo yok). Bilgisayar kapalı olduğu için istek sunucudaki Cursor ajanına verildi.",
  "Çalışma klasörü özel deponun sunucu klonudur. Prod veritabanına, sunucu gizli dosyalarına ve müşteri verisine erişimin yok;",
  "ERP verisi sorularında kullanıcıya Telegram'daki /ozet, /musteri, /tup gibi sunucu komutlarını öner.",
  ".cursor/rules içindeki Windows/PowerShell, Send-Telegram.ps1 ve ERP'yi kapatıp yayınlama talimatları bu sunucuda geçerli değil;",
  "son mesajın otomatik olarak Telegram'a gider.",
  `Kod değişikliğini yalnızca ${workBranch} (veya cursor/server-<konu>) dalına commit edip 'git push origin <dal>' ile gönderebilirsin;`,
  "main, cursor/telegram-cursor-agent-bridge ve releases'e asla push etme. Bilgisayar açılınca kullanıcı inceleyip birleştirir.",
  "Prod'a deploy, servis yeniden başlatma, silme, ödeme, müşterilere e-posta/WhatsApp gibi geri alınamaz işleri yapma;",
  "bunlar gerekiyorsa ne yapacağını yaz ve 'Onaylıyor musun? Evet/Hayır yazın' diye sor.",
];

let stopping = false;
let processing = false;
let lastReason = null;
let lastClone = 0;
let keyCheck = { key: null, ok: false, detail: null };
let recovered = false;
const status = { ready: false, reason: "başlıyor", pcOnline: null, current: null, lastResult: null };

function log(message) {
  process.stdout.write(`${new Date().toISOString()} ${message}\n`);
}

async function writeJsonAtomically(filePath, value, mode = 0o600) {
  const temporaryPath = `${filePath}.${process.pid}.tmp`;
  await fs.writeFile(temporaryPath, JSON.stringify(value, null, 2), { encoding: "utf8", mode });
  // The service UMask (0027) would otherwise hide status.json from ServerAgent (user arsangaz).
  await fs.chmod(temporaryPath, mode);
  await fs.rename(temporaryPath, filePath);
}

async function readJson(filePath) {
  try {
    return JSON.parse((await fs.readFile(filePath, "utf8")).replace(/^\uFEFF/, ""));
  } catch {
    return null;
  }
}

// ServerAgent shows this in /durum, so it must never contain secrets.
async function writeStatus() {
  await writeJsonAtomically(path.join(stateDir, "status.json"), {
    ...status, model, workspace, updatedUtc: new Date().toISOString(),
  }, 0o644).catch((error) => log(`Durum dosyası yazılamadı: ${error.message}`));
}

function git(args, options = {}) {
  return new Promise((resolve, reject) => {
    execFile("git", args, { cwd: options.cwd ?? workspace, timeout: options.timeoutMs ?? 120_000 }, (error, stdout, stderr) => {
      if (error) reject(new Error(String(stderr || error.message).trim().split("\n").pop()));
      else resolve(stdout.trim());
    });
  });
}

const PRE_PUSH_HOOK = `#!/bin/sh
# Server Cursor agent: push only to the private origin and only to cursor/server-* branches.
remote_url="$2"
case "$remote_url" in *arsangaz01/arsan-gaz-erp-kaynak*) ;; *) echo "push yalnizca ozel origin deposuna" >&2; exit 1 ;; esac
while read local_ref local_sha remote_ref remote_sha; do
  case "$remote_ref" in refs/heads/cursor/server-*) ;; *) echo "push yalnizca cursor/server-* dallarina: $remote_ref" >&2; exit 1 ;; esac
done
exit 0
`;

async function ensureWorkspace() {
  if (fsSync.existsSync(path.join(workspace, ".git"))) return true;
  if (Date.now() - lastClone < CLONE_RETRY_MS) return false;
  lastClone = Date.now();
  try {
    await git(["clone", "--quiet", repoUrl, workspace], { cwd: path.dirname(workspace), timeoutMs: 15 * 60_000 });
    await git(["config", "user.name", "Arsan Gaz Sunucu Cursor Ajani"]);
    await git(["config", "user.email", "cursoragent@arsangaz.local"]);
    await git(["config", "push.default", "current"]);
    await fs.writeFile(path.join(workspace, ".git", "hooks", "pre-push"), PRE_PUSH_HOOK, { mode: 0o755 });
    await git(["checkout", "-q", "-B", workBranch, "origin/main"]);
    log(`Depo klonlandı: ${workspace} (dal ${workBranch})`);
    return true;
  } catch (error) {
    await fs.rm(workspace, { recursive: true, force: true }).catch(() => undefined);
    status.reason = "Depo klonlanamadı; GitHub deploy key henüz eklenmemiş olabilir: " + error.message;
    return false;
  }
}

// Fast-forwards the work branch to origin/main when it has no unmerged agent commits and no local edits.
async function refreshWorkspace() {
  try {
    await git(["fetch", "--quiet", "origin", "--prune"]);
    if ((await git(["status", "--porcelain"])) !== "") return "çalışma klasöründe kaydedilmemiş değişiklik var; güncellenmedi";
    await git(["merge-base", "--is-ancestor", "HEAD", "origin/main"]).then(
      () => git(["merge", "--quiet", "--ff-only", "origin/main"]),
      () => undefined);
    return null;
  } catch (error) {
    return "depo güncellenemedi: " + error.message;
  }
}

async function checkKey() {
  const key = env.CURSOR_API_KEY?.trim();
  if (!key) return "CURSOR_API_KEY eksik";
  if (keyCheck.key !== key) {
    keyCheck = { key, ok: false, detail: null };
    try {
      const me = await Cursor.me({ apiKey: key });
      keyCheck.ok = true;
      log(`Cursor anahtarı doğrulandı${me?.email ? ` (${me.email})` : ""}.`);
    } catch (error) {
      keyCheck.detail = error instanceof AuthenticationError ? "CURSOR_API_KEY geçersiz veya süresi dolmuş" : null;
      // Network errors must not block processing; the run itself reports them.
      keyCheck.ok = !keyCheck.detail;
      if (!keyCheck.detail) log(`Cursor anahtarı şimdi doğrulanamadı: ${error.message}`);
    }
  }
  return keyCheck.ok ? null : keyCheck.detail;
}

async function readiness() {
  const keyProblem = await checkKey();
  if (keyProblem) return keyProblem;
  if (!(await ensureWorkspace())) {
    return status.reason?.startsWith("Depo klonlanamadı") ? status.reason : "Depo klonu bekleniyor (GitHub deploy key)";
  }
  return null;
}

function validateRequest(id, request) {
  const pick = (name) => request[name] ?? request[name.charAt(0).toUpperCase() + name.slice(1)];
  const text = pick("text");
  const taskId = pick("taskId") ?? id;
  if (pick("id") !== id || typeof taskId !== "string" || taskId.length > 200) throw new Error("İstek kimliği geçersiz.");
  if (typeof text !== "string" || !text.trim() || text.length > REQUEST_MAX_LENGTH) throw new Error("İstek metni boş veya 4096 karakterden uzun.");
  const attachments = pick("attachments") ?? [];
  if (!Array.isArray(attachments) || attachments.length > 10) throw new Error("Ek listesi geçersiz.");
  const context = pick("context");
  return {
    taskId,
    text,
    context: typeof context === "string" ? maskPii(context.slice(0, CONTEXT_MAX_LENGTH)) : "",
    attachments: attachments.map((item) => ({ localPath: item.LocalPath ?? item.localPath, fileName: item.FileName ?? item.fileName })),
    channel: id.startsWith("jarvischat-") ? "jarvis" : "telegram",
  };
}

async function fetchAttachments(id, attachments) {
  if (attachments.length === 0) return [];
  const directory = path.join(stateDir, "attachments", id);
  await fs.mkdir(directory, { recursive: true, mode: 0o700 });
  const paths = [];
  for (const [index, attachment] of attachments.entries()) {
    const name = `${index + 1}-${path.basename(String(attachment.fileName || attachment.localPath || "ek")).replace(/[^\p{L}\p{N}._-]/gu, "_")}`;
    const target = path.join(directory, name);
    await fs.writeFile(target, await inbox.getFile(id, String(attachment.localPath)), { mode: 0o600 });
    paths.push(target);
  }
  return paths;
}

async function openAgent(channel) {
  const statePath = path.join(stateDir, `agent-${channel}.json`);
  const state = (await readJson(statePath)) ?? {};
  const options = { model: { id: model }, local: { cwd: workspace, autoReview, settingSources: ["project"] } };
  let agent = null;
  if (state.agentId && state.model === model && Date.now() - Date.parse(state.lastUsedUtc ?? 0) < CONVERSATION_IDLE_MS) {
    try {
      agent = await Agent.resume(state.agentId, options);
    } catch (error) {
      if (!(error instanceof AgentNotFoundError)) throw error;
    }
  }
  if (!agent) {
    agent = await Agent.create({ ...options, name: channel === "jarvis" ? "Arsan Gaz Jarvis (sunucu)" : "Arsan Gaz Telegram (sunucu)" });
    log(`Yeni Cursor konuşması açıldı (${channel}): ${agent.agentId}`);
  }
  await writeJsonAtomically(statePath, { agentId: agent.agentId, model, lastUsedUtc: new Date().toISOString() });
  return agent;
}

async function appendJournal(id, text, report) {
  const entry = `\n## ${new Date().toISOString().slice(0, 16).replace("T", " ")} - sunucu Cursor ajanı - ${id}\n`
    + `**İstek:** ${text}\n\n**Rapor:** ${report}\n`;
  await fs.appendFile(path.join(stateDir, "cursor-journal.md"), entry, "utf8").catch(() => undefined);
}

function describeError(error) {
  if (error instanceof AuthenticationError) {
    return "Sunucudaki Cursor anahtarı (CURSOR_API_KEY) geçersiz veya süresi dolmuş. PC'den Set-ServerAgentValue.ps1 -Key CURSOR_API_KEY ile yenileyin.";
  }
  return error?.message ?? String(error);
}

async function processRequest(id, reason) {
  const nowIso = () => new Date().toISOString();
  let submitted = false;
  let agent;
  let cancelWatch;
  let text = "";
  status.current = { id, startedUtc: nowIso(), reason };
  await writeStatus();
  log(`İstek sunucuda işleniyor (${reason}): ${id}`);
  try {
    if (await inbox.isCancelled(id)) {
      await inbox.put(id, "completion", { id, taskId: id, status: "cancelled", report: "⏹ İş başlamadan durduruldu.", updatedUtc: nowIso() });
      return;
    }
    const request = validateRequest(id, JSON.parse((await inbox.getRequest(id)).replace(/^\uFEFF/, "")));
    text = request.text;
    const attachmentPaths = await fetchAttachments(id, request.attachments);
    const workspaceNote = await refreshWorkspace();
    if (workspaceNote) log(`Depo notu (${id}): ${workspaceNote}`);

    agent = await openAgent(request.channel);
    const prompt = [...SERVER_RULES, ...(workspaceNote ? [`Not: ${workspaceNote}.`] : []), "",
      buildPrompt(request.text, request.context, attachmentPaths, request.channel)].join("\n");
    const run = await agent.send(prompt);
    submitted = true;
    await inbox.put(id, "ack", { status: "submitted", error: null, updatedUtc: nowIso() });
    await inbox.put(id, "progress", { id, stage: "submitted", steps: [], updatedUtc: nowIso() });
    log(`İstek Cursor ajanına gönderildi: ${id} (agent ${agent.agentId}, run ${run.id})`);

    let cancelRequested = false;
    let timedOut = false;
    const startedMs = Date.now();
    cancelWatch = setInterval(async () => {
      if (cancelRequested) return;
      timedOut = Date.now() - startedMs > RUN_MAX_MS;
      if (!timedOut && !(await inbox.isCancelled(id).catch(() => false))) return;
      cancelRequested = true;
      log(timedOut ? `Süre sınırı aşıldı: ${id}` : `Durdurma isteği alındı: ${id}`);
      run.cancel().catch((error) => log(`Durdurma başarısız (${id}): ${error?.message ?? error}`));
    }, CANCEL_POLL_MS);

    let streamedText = "";
    const steps = [];
    const seenCalls = new Set();
    for await (const message of run.stream()) {
      if (message.type === "tool_call" && message.status === "running" && !seenCalls.has(message.call_id)) {
        seenCalls.add(message.call_id);
        steps.push(describeToolCall(message.name, message.args));
        if (steps.length > 40) steps.shift();
        await inbox.put(id, "progress", { id, stage: "working", steps, updatedUtc: nowIso() }).catch(() => undefined);
        continue;
      }
      if (message.type !== "assistant") continue;
      for (const block of message.message?.content ?? []) {
        if (block.type === "text") streamedText += block.text;
      }
    }
    const result = await run.wait();
    clearInterval(cancelWatch);
    if (cancelRequested || result.status === "cancelled") {
      const report = REPORT_HEADER + (timedOut ? `⏱ İş ${RUN_MAX_MS / 60_000} dakikayı aştığı için durduruldu.` : "⏹ İş kullanıcı isteğiyle durduruldu.");
      await inbox.put(id, "completion", { id, taskId: request.taskId, status: "cancelled", report, updatedUtc: nowIso() });
      await inbox.put(id, "progress", { id, stage: "cancelled", steps, updatedUtc: nowIso() }).catch(() => undefined);
      status.lastResult = { id, status: "cancelled", updatedUtc: nowIso() };
      await appendJournal(id, text, report);
      return;
    }
    if (result.status !== "finished") throw new Error(`Cursor ajanı işi tamamlayamadı (${result.error?.message ?? result.status}).`);
    const report = REPORT_HEADER + ((result.result || streamedText).trim() || "Cursor ajanı işi bitirdi ancak metin raporu üretmedi.");
    await inbox.put(id, "completion", { id, taskId: request.taskId, status: "completed", report, updatedUtc: nowIso() });
    await inbox.put(id, "progress", { id, stage: "completed", steps, updatedUtc: nowIso() }).catch(() => undefined);
    status.lastResult = { id, status: "completed", updatedUtc: nowIso() };
    await appendJournal(id, text, report);
    log(`Cursor yanıtı tamamlandı: ${id} (${result.durationMs ?? "?"} ms)`);
  } catch (error) {
    const detail = describeError(error);
    log(`İstek başarısız (${id}, gönderildi=${submitted}): ${detail}`);
    status.lastResult = { id, status: "failed", updatedUtc: nowIso() };
    await inbox.put(id, "ack", { status: "failed", error: "Sunucudaki Cursor ajanı: " + detail, updatedUtc: nowIso() })
      .catch((putError) => log(`Hata sonucu yazılamadı (${id}): ${putError.message}`));
  } finally {
    clearInterval(cancelWatch);
    status.current = null;
    await fs.rm(path.join(stateDir, "attachments", id), { recursive: true, force: true }).catch(() => undefined);
    if (agent) await agent[Symbol.asyncDispose]?.().catch(() => undefined);
    await writeStatus();
  }
}

// A claim left over from a previous process was interrupted; like bridge.js it is reported, not re-run.
async function recoverInterrupted(mine) {
  for (const id of mine) {
    await inbox.put(id, "ack", {
      status: "failed",
      error: "Sunucudaki Cursor ajanı bu iş sürerken yeniden başladı; sonuç belirsiz ve iş otomatik tekrar çalıştırılmadı. Gerekirse isteği yeniden gönderin.",
      updatedUtc: new Date().toISOString(),
    }).catch((error) => log(`Yarım kalan iş kapatılamadı (${id}): ${error.message}`));
    log(`Yarım kalan sunucu işi belirsiz olarak kapatıldı: ${id}`);
  }
}

async function poll() {
  if (processing || stopping) return;
  processing = true;
  try {
    const problem = await readiness();
    status.ready = !problem;
    status.reason = problem;
    if (problem !== lastReason) {
      log(problem ? `Bekliyor: ${problem}` : "Hazır: bilgisayar kapalıyken Cursor isteklerini işleyecek.");
      lastReason = problem;
    }
    if (problem) return;

    const list = await inbox.list();
    status.pcOnline = isPcOnline(list.pcAgeSec);
    if (!recovered) {
      recovered = true;
      await recoverInterrupted(list.mine);
    }
    const tests = list.requests.filter((request) => request.id.startsWith(TEST_ID_PREFIX)).map((request) => ({ ...request, reason: "sunucu testi" }));
    for (const candidate of [...tests, ...pickClaimable({ ...list, requests: list.requests.filter((r) => !r.id.startsWith(TEST_ID_PREFIX)) }, waitMinutes)]) {
      if (stopping) break;
      const claim = await inbox.claim(candidate.id);
      if (claim !== "CLAIMED") continue;
      await processRequest(candidate.id, candidate.reason);
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
    console.log(JSON.stringify({ reason: await readiness(), workspace, stateDir, model, waitMinutes }, null, 2));
    return;
  }
  await fs.mkdir(stateDir, { recursive: true });
  log(`Sunucu Cursor ajanı başladı (pid ${process.pid}, depo ${workspace}, bekleme ${waitMinutes} dk, model ${model}).`);
  const timer = setInterval(() => poll().catch((error) => log(`Döngü hatası: ${error.message}`)), POLL_INTERVAL_MS);
  poll();
  const shutdown = () => {
    stopping = true;
    clearInterval(timer);
    log("Sunucu Cursor ajanı kapanıyor.");
    setTimeout(() => process.exit(0), processing ? 3000 : 0);
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

if (require.main === module) {
  main().catch((error) => {
    log(`Sunucu Cursor ajanı hatası: ${error?.stack ?? error}`);
    process.exitCode = 1;
  });
}

module.exports = { validateRequest, PRE_PUSH_HOOK, SERVER_RULES };
