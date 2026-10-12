"use strict";

// @TuptakipArsanbot (service arsantuptakip-bot, user tuptakipbot): the only getUpdates consumer of the ArsanTüpTakip bot.
// Independent of ArsanGaz ERP's bot and services. Accepts messages only from the configured chat:
//   commands  -> /durum, /yayin, /yayin_onay, /geri_al, /apk, /apk_onay, /iptal, /yeni, /yardim (release actions through
//                sudo /usr/local/sbin/arsantuptakip-release, which validates its arguments itself)
//   other     -> development request in the shared queue (queue.js) for arsantuptakip-agent (dev-agent.js);
//                progress and the final report come back through this bot.
// Notices dropped into <queue>/notices/*.txt (server scripts, admin) are sent verbatim and then deleted.
// Secrets come only from the environment (EnvironmentFile=/etc/arsantuptakip/telegram.env) and are never logged.

const fs = require("node:fs/promises");
const path = require("node:path");
const { execFile } = require("node:child_process");
const queue = require("./queue");
const {
  HELP_TEXT, parseCommand, isAuthorized, createConfirmation, checkConfirmation, parseTrailers, splitMessage, shortSha, formatProgress,
  apkResultHeader,
} = require("./commands");

const env = process.env;
const token = env.TUPTAKIP_TELEGRAM_BOT_TOKEN?.trim() ?? "";
const allowedChatId = env.TUPTAKIP_TELEGRAM_CHAT_ID?.trim() ?? "";
const queueRoot = env.TUPTAKIP_QUEUE_DIR || "/var/lib/arsantuptakip-queue";
const stateDir = env.TUPTAKIP_BOT_STATE_DIR || "/var/lib/arsantuptakip-bot";
const releaseCommand = (env.TUPTAKIP_RELEASE_CMD || "sudo -n /usr/local/sbin/arsantuptakip-release").split(" ").filter(Boolean);

const MAX_FILE_BYTES = 20 * 1024 * 1024;
const PROGRESS_EDIT_MS = 20_000;
const DELIVERY_POLL_MS = 3000;
const STALE_MESSAGE_SEC = 30 * 60;
const MEDIA_GROUP_WAIT_MS = 2500;

let stopping = false;
let botUsername = "";
let releaseBusy = null;
let state = { offset: 0, helloSent: false, confirmation: null, progress: {} };
const mediaGroups = new Map();

function log(message) {
  process.stdout.write(`${new Date().toISOString()} ${message}\n`);
}

async function loadState() {
  state = { ...state, ...((await queue.readJson(path.join(stateDir, "state.json"))) ?? {}) };
}

async function saveState() {
  await queue.writeJsonAtomically(path.join(stateDir, "state.json"), state, 0o600).catch((error) => log(`Durum kaydedilemedi: ${error.message}`));
}

// ---------------------------------------------------------------- Telegram API
async function telegram(method, body, timeoutMs = 30_000) {
  const response = await fetch(`https://api.telegram.org/bot${token}/${method}`, {
    method: "POST",
    headers: { "Content-Type": "application/json; charset=utf-8" },
    body: JSON.stringify(body ?? {}),
    signal: AbortSignal.timeout(timeoutMs),
  });
  const data = await response.json().catch(() => ({ ok: false, description: `HTTP ${response.status}` }));
  if (!data.ok) {
    const error = new Error(`Telegram ${method}: ${data.description ?? response.status}`);
    error.code = data.error_code ?? response.status;
    throw error;
  }
  return data.result;
}

let output = async (text) => {
  let last = null;
  for (const part of splitMessage(text)) {
    last = await telegram("sendMessage", { chat_id: allowedChatId, text: part, disable_web_page_preview: true });
  }
  return last;
};

async function reply(text) {
  try {
    return await output(text);
  } catch (error) {
    log(`Mesaj gönderilemedi: ${error.message}`);
    return null;
  }
}

async function downloadFile(fileId, target) {
  const file = await telegram("getFile", { file_id: fileId });
  if (!file?.file_path) throw new Error("dosya yolu alınamadı");
  if (file.file_size && file.file_size > MAX_FILE_BYTES) throw new Error("dosya 20 MB'tan büyük");
  const response = await fetch(`https://api.telegram.org/file/bot${token}/${file.file_path}`, { signal: AbortSignal.timeout(120_000) });
  if (!response.ok) throw new Error(`indirme HTTP ${response.status}`);
  const data = Buffer.from(await response.arrayBuffer());
  if (data.length > MAX_FILE_BYTES) throw new Error("dosya 20 MB'tan büyük");
  await fs.writeFile(target, data, { mode: 0o660 });
}

// ---------------------------------------------------------------- release script
function runRelease(args, timeoutMs = 45 * 60_000) {
  return new Promise((resolve) => {
    execFile(releaseCommand[0], [...releaseCommand.slice(1), ...args], { timeout: timeoutMs, maxBuffer: 4 * 1024 * 1024 }, (error, stdout, stderr) => {
      const code = error ? (typeof error.code === "number" ? error.code : 1) : 0;
      const text = String(stdout || "") + (code !== 0 && stderr ? `\n${String(stderr).trim().split("\n").slice(-3).join("\n")}` : "");
      resolve({ code, ...parseTrailers(text) });
    });
  });
}

async function withRelease(name, action) {
  if (releaseBusy) {
    await reply(`⏳ Başka bir işlem sürüyor (${releaseBusy}); bitince tekrar deneyin.`);
    return;
  }
  releaseBusy = name;
  try {
    await action();
  } finally {
    releaseBusy = null;
  }
}

// ---------------------------------------------------------------- commands
async function agentStatusText() {
  const agent = await queue.readJson(path.join(queueRoot, "agent-status.json"));
  const pending = await queue.listPending(queueRoot).catch(() => []);
  if (!agent) return "🤖 Cursor ajanı: durum bilgisi yok (servis çalışmıyor olabilir).";
  const ageSec = Math.round((Date.now() - Date.parse(agent.updatedUtc)) / 1000);
  const fresh = Number.isFinite(ageSec) && ageSec < 120;
  const lines = [`🤖 Cursor ajanı (${agent.model ?? "?"}): ${!fresh ? `yanıt vermiyor (son durum ${Number.isFinite(ageSec) ? Math.round(ageSec / 60) : "?"} dk önce)` : agent.ready ? "hazır" : `bekliyor — ${agent.reason ?? "?"}`}`];
  if (agent.current) lines.push(`   Şu an çalışıyor: ${agent.current.id}`);
  if (pending.length > 0) lines.push(`   Sırada ${pending.length} istek var.`);
  if (agent.lastResult) lines.push(`   Son iş: ${agent.lastResult.id} → ${agent.lastResult.status}`);
  return lines.join("\n");
}

async function commandDurum() {
  const result = await runRelease(["status"], 60_000);
  const header = result.code === 0 ? "" : `⚠️ Durum betiği hata verdi (çıkış ${result.code}).\n`;
  await reply(`📊 ArsanTüpTakip durumu\n\n${header}${result.text}\n\n${await agentStatusText()}`);
}

async function commandYayin() {
  await withRelease("yayın kontrolü", async () => {
    await reply("🔎 Yayın bekleyen değişikliklere bakılıyor...");
    const result = await runRelease(["pending"], 10 * 60_000);
    const sha = result.trailers.TARGET;
    const blocked = result.code !== 0 || result.trailers.BLOCKED === "1" || result.trailers.CONFLICT === "1" || !/^[0-9a-f]{40}$/.test(sha ?? "");
    if (blocked || result.trailers.NOTHING === "1") {
      state.confirmation = null;
      await saveState();
      await reply(result.text + (blocked ? "\n\n⛔ Yayın şu an yapılamaz." : ""));
      return;
    }
    state.confirmation = createConfirmation("yayin", sha);
    await saveState();
    await reply(`${result.text}\n\nOnaylıyor musunuz? Yayınlamak için 15 dakika içinde şunu yazın:\n/yayin_onay ${state.confirmation.code}`);
  });
}

async function commandYayinOnay(args) {
  const check = checkConfirmation(state.confirmation, "yayin", args[0]);
  if (!check.ok) {
    await reply(`❌ ${check.error}`);
    return;
  }
  await withRelease("yayın", async () => {
    state.confirmation = null;
    await saveState();
    await reply(`🚀 Yayın başladı (${shortSha(check.sha)}): sunucuda derleme + testler, main'e birleştirme, kurulum ve sağlık kontrolü. Birkaç dakika sürebilir.`);
    const result = await runRelease(["publish", check.sha]);
    const icon = result.code === 0 ? "✅ Yayın tamamlandı." : result.code === 2 ? "↩️ Yeni sürüm sağlık kontrolünden geçemedi; önceki sürüme dönüldü." : `❌ Yayın başarısız (çıkış ${result.code}).`;
    await reply(`${icon}\n\n${result.text}`);
  });
}

async function commandGeriAl(args) {
  if (args.length === 0) {
    await withRelease("geri alma kontrolü", async () => {
      const result = await runRelease(["previous"], 60_000);
      const sha = result.trailers.PREVIOUS;
      if (result.code !== 0 || !sha) {
        state.confirmation = null;
        await saveState();
        await reply(`${result.text}\n\n⛔ Geri alınacak önceki kurulum yok.`);
        return;
      }
      state.confirmation = createConfirmation("geri_al", sha);
      await saveState();
      await reply(`${result.text}\n\nOnaylıyor musunuz? Önceki kuruluma dönmek için 15 dakika içinde şunu yazın:\n/geri_al ${state.confirmation.code}`);
    });
    return;
  }
  const check = checkConfirmation(state.confirmation, "geri_al", args[0]);
  if (!check.ok) {
    await reply(`❌ ${check.error}`);
    return;
  }
  await withRelease("geri alma", async () => {
    state.confirmation = null;
    await saveState();
    await reply("↩️ Önceki kuruluma dönülüyor...");
    const result = await runRelease(["rollback"], 10 * 60_000);
    await reply(`${result.code === 0 ? "✅ Geri alındı." : `❌ Geri alma başarısız (çıkış ${result.code}).`}\n\n${result.text}`);
  });
}

async function commandApk() {
  await withRelease("APK kontrolü", async () => {
    await reply("🔎 main'deki mobil uygulamanın imzalı APK'sına bakılıyor...");
    const result = await runRelease(["apkcheck"], 5 * 60_000);
    const sha = result.trailers.TARGET;
    if (result.code !== 0 || !/^[0-9a-f]{40}$/.test(sha ?? "")) {
      state.confirmation = null;
      await saveState();
      await reply(result.text + (result.code !== 0 ? `\n\n⛔ APK kontrolü başarısız (çıkış ${result.code}).` : ""));
      return;
    }
    state.confirmation = createConfirmation("apk", sha);
    await saveState();
    await reply(`${result.text}\n\nOnaylıyor musunuz? APK'yı yayınlamak için 15 dakika içinde şunu yazın:\n/apk_onay ${state.confirmation.code}`);
  });
}

async function commandApkOnay(args) {
  const check = checkConfirmation(state.confirmation, "apk", args[0]);
  if (!check.ok) {
    await reply(`❌ ${check.error}`);
    return;
  }
  await withRelease("APK yayını", async () => {
    state.confirmation = null;
    await saveState();
    await reply(`📱 APK yayını başladı (${shortSha(check.sha)}): GitHub'dan indirme, imza/paket/sürüm doğrulaması, /tuptakip/indir.`);
    const result = await runRelease(["apk", check.sha], 15 * 60_000);
    await reply(`${apkResultHeader(result.code, result.trailers)}\n\n${result.text}`);
  });
}

async function commandIptal() {
  const active = [...await queue.listActive(queueRoot), ...await queue.listPending(queueRoot)];
  if (active.length === 0) {
    await reply("Durdurulacak bir geliştirme isteği yok.");
    return;
  }
  for (const id of active) await queue.cancel(queueRoot, id);
  await reply(`⏹ Durdurma isteği gönderildi: ${active.join(", ")}`);
}

async function commandYeni() {
  await queue.requestNewConversation(queueRoot);
  await reply("🆕 Bir sonraki geliştirme isteği yeni bir Cursor konuşmasıyla başlayacak (önceki bağlam kullanılmayacak).");
}

async function handleCommand(parsed) {
  switch (parsed.command) {
    case "start":
    case "yardim": return reply(HELP_TEXT);
    case "durum": return commandDurum();
    case "yayin": return commandYayin();
    case "yayin_onay": return commandYayinOnay(parsed.args);
    case "geri_al": return commandGeriAl(parsed.args);
    case "apk": return commandApk();
    case "apk_onay": return commandApkOnay(parsed.args);
    case "iptal": return commandIptal();
    case "yeni": return commandYeni();
    default: return reply(`Bilinmeyen komut: /${parsed.command}\n\n${HELP_TEXT}`);
  }
}

// ---------------------------------------------------------------- development requests
function attachmentsOf(message) {
  const list = [];
  if (Array.isArray(message.photo) && message.photo.length > 0) {
    const largest = message.photo[message.photo.length - 1];
    list.push({ fileId: largest.file_id, name: `foto-${message.message_id}.jpg`, size: largest.file_size });
  }
  if (message.document) list.push({ fileId: message.document.file_id, name: message.document.file_name || `belge-${message.message_id}`, size: message.document.file_size });
  if (message.video) list.push({ fileId: message.video.file_id, name: message.video.file_name || `video-${message.message_id}.mp4`, size: message.video.file_size });
  if (message.video_note) list.push({ fileId: message.video_note.file_id, name: `video-${message.message_id}.mp4`, size: message.video_note.file_size });
  if (message.animation) list.push({ fileId: message.animation.file_id, name: `animasyon-${message.message_id}.mp4`, size: message.animation.file_size });
  return list;
}

const safeName = (name, index) => `${index + 1}-${path.basename(String(name)).replace(/[^\p{L}\p{N}._-]/gu, "_").slice(0, 100)}`;

async function syncMirror() {
  const result = await runRelease(["sync"], 5 * 60_000);
  return result.code === 0 ? null : (result.text.split("\n").pop() || `çıkış ${result.code}`);
}

async function createDevRequest(messages) {
  const first = messages[0];
  const id = `tg-${first.message_id}-${Date.now().toString(36)}`;
  const text = messages.map((message) => message.text ?? message.caption ?? "").filter((value) => value.trim()).join("\n\n").trim()
    || "(Metin yok; ekteki dosyalara bak ve ne istendiğini anlamaya çalış, emin değilsen sor.)";
  const { attachmentsDir } = await queue.beginRequest(queueRoot, id);
  const notes = [];
  let index = 0;
  for (const attachment of messages.flatMap(attachmentsOf)) {
    const name = safeName(attachment.name, index++);
    if (attachment.size && attachment.size > MAX_FILE_BYTES) {
      notes.push(`${attachment.name}: 20 MB'tan büyük, Telegram botları indiremez.`);
      continue;
    }
    try {
      await downloadFile(attachment.fileId, path.join(attachmentsDir, name));
    } catch (error) {
      notes.push(`${attachment.name}: indirilemedi (${error.message}).`);
    }
  }
  const syncProblem = await syncMirror().catch((error) => error.message);
  if (syncProblem) log(`Depo aynası güncellenemedi: ${syncProblem}`);
  const fullText = notes.length > 0 ? `${text}\n\n(Bot notu: ${notes.join(" ")})` : text;
  await queue.commitRequest(queueRoot, id, { text: fullText.slice(0, 4096), source: "telegram", messageId: first.message_id });
  const pending = (await queue.listPending(queueRoot)).length;
  const busy = (await queue.listActive(queueRoot)).length > 0;
  await reply(`📝 Geliştirme isteği alındı (${id}).${notes.length ? `\n⚠️ ${notes.join(" ")}` : ""}${busy || pending > 1 ? `\nSırada ${pending} istek var; öncekiler bitince başlayacak.` : "\nSunucudaki Cursor ajanı başlıyor..."}`);
  log(`Geliştirme isteği kuyruğa yazıldı: ${id}`);
}

// ---------------------------------------------------------------- updates
async function handleMessage(message) {
  if (!isAuthorized(message, allowedChatId)) {
    log(`Yetkisiz sohbetten mesaj yok sayıldı (tür ${message?.chat?.type ?? "?"}).`);
    return;
  }
  if (message.date && Date.now() / 1000 - message.date > STALE_MESSAGE_SEC) {
    log(`Eski mesaj atlandı (${message.message_id}).`);
    return;
  }
  const parsed = parseCommand(message.text ?? "", botUsername);
  if (parsed) {
    log(`Komut: /${parsed.command}`);
    await handleCommand(parsed);
    return;
  }
  if (message.media_group_id) {
    const group = mediaGroups.get(message.media_group_id) ?? { messages: [], timer: null };
    group.messages.push(message);
    clearTimeout(group.timer);
    group.timer = setTimeout(() => {
      mediaGroups.delete(message.media_group_id);
      createDevRequest(group.messages).catch((error) => reply(`❌ İstek kaydedilemedi: ${error.message}`));
    }, MEDIA_GROUP_WAIT_MS);
    mediaGroups.set(message.media_group_id, group);
    return;
  }
  if (!message.text && attachmentsOf(message).length === 0 && !message.caption) {
    await reply("Bu mesaj türü desteklenmiyor. Metin, fotoğraf, belge veya video gönderin. /yardim");
    return;
  }
  await createDevRequest([message]);
}

async function pollUpdates() {
  let conflictLogged = false;
  while (!stopping) {
    try {
      const updates = await telegram("getUpdates", { offset: state.offset, timeout: 50, allowed_updates: ["message"] }, 70_000);
      conflictLogged = false;
      for (const update of updates) {
        state.offset = update.update_id + 1;
        await saveState();
        // Not awaited: a release can take minutes and /durum must still answer meanwhile.
        if (update.message) handleMessage(update.message).catch((error) => log(`Mesaj işlenemedi: ${error.message}`));
      }
    } catch (error) {
      if (stopping) break;
      if (error.code === 409) {
        if (!conflictLogged) log("409 Conflict: bu botu başka bir süreç de dinliyor; 30 sn sonra yeniden denenecek.");
        conflictLogged = true;
        await sleep(30_000);
      } else {
        log(`getUpdates hatası: ${error.message}`);
        await sleep(error.code === 401 ? 300_000 : 5000);
      }
    }
  }
}

// ---------------------------------------------------------------- delivery of agent progress and results
async function deliver() {
  for (const id of await queue.listActive(queueRoot)) {
    const progress = await queue.readProgress(queueRoot, id);
    if (!progress) continue;
    const entry = state.progress[id] ?? { startedMs: Date.now(), messageId: null, lastText: "", lastEditMs: 0 };
    const text = formatProgress(id, progress.steps, entry.startedMs);
    if (text === entry.lastText || Date.now() - entry.lastEditMs < PROGRESS_EDIT_MS) continue;
    try {
      if (entry.messageId) {
        await telegram("editMessageText", { chat_id: allowedChatId, message_id: entry.messageId, text, disable_web_page_preview: true });
      } else {
        const sent = await output(text);
        entry.messageId = sent?.message_id ?? null;
      }
    } catch (error) {
      if (!/not modified/i.test(error.message)) log(`İlerleme mesajı gönderilemedi (${id}): ${error.message}`);
    }
    entry.lastText = text;
    entry.lastEditMs = Date.now();
    state.progress[id] = entry;
    await saveState();
  }
  for (const id of await queue.listUndelivered(queueRoot)) {
    const result = await queue.readResult(queueRoot, id);
    const icon = result?.status === "completed" ? "✅" : result?.status === "cancelled" ? "⏹" : "❌";
    const sent = await reply(`${icon} ArsanTüpTakip Cursor ajanı (${id}):\n\n${result?.report ?? "Rapor okunamadı."}`);
    if (!sent) continue;
    await queue.markDelivered(queueRoot, id);
    delete state.progress[id];
    await saveState();
  }
  for (const name of await queue.listNotices(queueRoot)) {
    const text = await queue.readNotice(queueRoot, name);
    if (text && !(await reply(text))) continue;
    await queue.removeNotice(queueRoot, name);
    log(`Bildirim gönderildi: ${name}`);
  }
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms));

async function deliveryLoop() {
  let lastCleanup = 0;
  while (!stopping) {
    await deliver().catch((error) => log(`Teslim döngüsü hatası: ${error.message}`));
    if (Date.now() - lastCleanup > 3600_000) {
      lastCleanup = Date.now();
      await queue.cleanup(queueRoot).catch(() => undefined);
    }
    await sleep(DELIVERY_POLL_MS);
  }
}

const HELLO_TEXT = [
  "👋 Merhaba! @TuptakipArsanbot hazır.",
  "Bu bot yalnızca ArsanTüpTakip sistemi içindir; ArsanGaz ERP botu aynen çalışmaya devam ediyor.",
  "",
  HELP_TEXT.split("\n").slice(2).join("\n"),
].join("\n");

// ---------------------------------------------------------------- main
async function simulate(text) {
  output = async (value) => {
    process.stdout.write(`----- bot yanıtı -----\n${value}\n`);
    return { message_id: 0 };
  };
  const parsed = parseCommand(text);
  if (!parsed) throw new Error("Benzetim yalnızca komutlar içindir (ör. /durum).");
  await handleCommand(parsed);
}

async function main() {
  const argv = process.argv.slice(2);
  await fs.mkdir(stateDir, { recursive: true });
  await loadState();
  if (argv[0] === "--simulate") {
    await simulate(argv.slice(1).join(" "));
    return;
  }
  if (!token) throw new Error("TUPTAKIP_TELEGRAM_BOT_TOKEN eksik (/etc/arsantuptakip/telegram.env).");
  if (!/^-?\d+$/.test(allowedChatId)) throw new Error("TUPTAKIP_TELEGRAM_CHAT_ID eksik veya geçersiz (/etc/arsantuptakip/telegram.env).");
  await queue.ensureLayout(queueRoot);
  const me = await telegram("getMe");
  botUsername = me.username ?? "";
  const webhook = await telegram("getWebhookInfo").catch(() => null);
  if (webhook?.url) {
    await telegram("deleteWebhook", { drop_pending_updates: false });
    log("Bota tanımlı webhook kaldırıldı (bot yalnızca getUpdates ile dinler).");
  }
  log(`ArsanTüpTakip botu başladı (@${botUsername}, pid ${process.pid}).`);
  if (!state.offset) {
    // First start: messages sent before the bot existed are skipped instead of being answered in bulk.
    const backlog = await telegram("getUpdates", { offset: -1, timeout: 0 }).catch(() => []);
    if (backlog.length > 0) {
      state.offset = backlog[backlog.length - 1].update_id + 1;
      await saveState();
      log("Bot ilk kez başladı; önceki mesajlar atlandı.");
    }
  }
  if (!state.helloSent) {
    if (await reply(HELLO_TEXT)) {
      state.helloSent = true;
      await saveState();
      log("Hazır mesajı gönderildi.");
    }
  }
  const shutdown = () => {
    stopping = true;
    log("ArsanTüpTakip botu kapanıyor.");
    setTimeout(() => process.exit(0), 1000);
  };
  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
  await Promise.all([pollUpdates(), deliveryLoop()]);
}

if (require.main === module) {
  main().catch((error) => {
    log(`ArsanTüpTakip botu hatası: ${error?.message ?? error}`);
    process.exitCode = 1;
  });
}

module.exports = { attachmentsOf, safeName, HELLO_TEXT };
