"use strict";

// Pure helpers of the @TuptakipArsanbot service (telegram-bot.js): command parsing, chat authorization, confirmation
// codes, release-script output parsing and Telegram message splitting. No I/O, so everything here is unit tested.

const crypto = require("node:crypto");

const CONFIRM_TTL_MS = 15 * 60_000;
const TELEGRAM_TEXT_LIMIT = 4000;

const COMMANDS = new Set(["start", "yardim", "durum", "yayin", "yayin_onay", "geri_al", "apk", "apk_onay", "iptal", "yeni"]);

// Per confirmation kind: what is missing and which command starts it again.
const CONFIRM_KINDS = {
  yayin: { missing: "Bekleyen yayın onayı yok.", start: "/yayin" },
  geri_al: { missing: "Bekleyen geri alma onayı yok.", start: "/geri_al" },
  apk: { missing: "Bekleyen APK onayı yok.", start: "/apk" },
};

const HELP_TEXT = [
  "🛢 ArsanTüpTakip botu — yalnızca ArsanTüpTakip sistemi için (ArsanGaz ERP'ye dokunmaz).",
  "",
  "Komutlar:",
  "/durum — API sağlığı, veritabanı, tablo kayıt sayıları, son yedek, yayındaki sürüm, ajan durumu",
  "/yayin — yayınlanmayı bekleyen değişiklikleri gösterir ve bir onay kodu verir",
  "/yayin_onay <kod> — sunucuda derler + test eder, main'e birleştirir ve kurar; sağlık kontrolü geçmezse otomatik geri döner",
  "/geri_al — bir önceki kuruluma dönmek için onay kodu verir; /geri_al <kod> ile uygulanır",
  "/apk — main'deki mobil uygulamanın imzalı APK'sını gösterir ve bir onay kodu verir",
  "/apk_onay <kod> — APK'yı GitHub'dan indirir, imza/paket/sürümü doğrular ve arsantuptakip.com/tuptakip/indir'e koyar",
  "/iptal — sürmekte olan geliştirme isteğini durdurur",
  "/yeni — Cursor ajanıyla yeni bir konuşma başlatır (önceki bağlam unutulur)",
  "/yardim — bu liste",
  "",
  "Komut olmayan her mesaj (metin, fotoğraf, belge, video) ArsanTüpTakip için bir geliştirme isteği olarak sunucudaki",
  "Cursor ajanına gider. Ajan yalnızca Tools/ArsanTupTakip altında değişiklik yapar; sonuç /yayin ile yayına alınır.",
].join("\n");

// Turkish letters are folded so "/yayın_onay" and "/YAYIN" work like "/yayin_onay".
function foldTurkish(text) {
  return String(text)
    .replace(/[İI]/g, "i").replace(/ı/g, "i")
    .replace(/[Şş]/g, "s").replace(/[Çç]/g, "c").replace(/[Ğğ]/g, "g").replace(/[Üü]/g, "u").replace(/[Öö]/g, "o")
    .toLowerCase();
}

// Returns { command, args } for "/komut[@bot] arg..." or null for a free text message.
function parseCommand(text, botUsername = "") {
  if (typeof text !== "string") return null;
  const match = /^\/([^\s@]+)(?:@(\S+))?(?:\s+([\s\S]*))?$/.exec(text.trim());
  if (!match) return null;
  if (match[2] && botUsername && match[2].toLowerCase() !== botUsername.toLowerCase()) return null;
  const command = foldTurkish(match[1]);
  const args = (match[3] ?? "").trim().split(/\s+/).filter(Boolean);
  return { command, args, known: COMMANDS.has(command) };
}

// Only the configured private chat may use the bot; anything else is ignored.
function isAuthorized(message, allowedChatId) {
  const allowed = String(allowedChatId ?? "").trim();
  if (!/^-?\d+$/.test(allowed)) return false;
  const chatId = message?.chat?.id;
  return chatId !== undefined && chatId !== null && String(chatId) === allowed;
}

function createConfirmation(kind, sha, now = Date.now(), random = crypto.randomInt) {
  return { kind, sha, code: String(random(100000, 1000000)), expiresMs: now + CONFIRM_TTL_MS };
}

// Returns { ok: true, sha } or { ok: false, error } in Turkish.
function checkConfirmation(pending, kind, code, now = Date.now()) {
  const info = CONFIRM_KINDS[kind] ?? CONFIRM_KINDS.yayin;
  if (!pending || pending.kind !== kind) {
    return { ok: false, error: `${info.missing} Önce ${info.start} yazın.` };
  }
  if (now > pending.expiresMs) {
    return { ok: false, error: `Onay kodunun süresi doldu. Yeniden ${info.start} yazın.` };
  }
  if (!/^\d{6}$/.test(String(code ?? "")) || String(code) !== pending.code) {
    return { ok: false, error: "Onay kodu yanlış." };
  }
  return { ok: true, sha: pending.sha };
}

// arsantuptakip-release prints "@@KEY=value" trailer lines for the bot; they are removed from the user text.
function parseTrailers(output) {
  const trailers = {};
  const lines = [];
  for (const line of String(output ?? "").split("\n")) {
    const match = /^@@([A-Z_]+)=(.*)$/.exec(line);
    if (match) trailers[match[1]] = match[2].trim();
    else lines.push(line);
  }
  return { text: lines.join("\n").trim(), trailers };
}

function splitMessage(text, limit = TELEGRAM_TEXT_LIMIT) {
  const value = String(text ?? "").trim() || "(boş)";
  const parts = [];
  let rest = value;
  while (rest.length > limit) {
    let cut = rest.lastIndexOf("\n", limit);
    if (cut < limit / 2) cut = limit;
    parts.push(rest.slice(0, cut).trimEnd());
    rest = rest.slice(cut).replace(/^\n/, "");
  }
  if (rest.trim()) parts.push(rest);
  return parts;
}

function shortSha(sha) {
  return typeof sha === "string" && /^[0-9a-f]{7,40}$/.test(sha) ? sha.slice(0, 7) : "bilinmiyor";
}

function formatProgress(id, steps, startedMs, now = Date.now()) {
  const minutes = Math.max(0, Math.round((now - startedMs) / 60_000));
  const recent = (Array.isArray(steps) ? steps : []).slice(-6).map((step) => `• ${String(step).slice(0, 120)}`);
  return [`🛠 Cursor ajanı çalışıyor (${id}, ${minutes} dk)`, ...(recent.length ? ["", ...recent] : []), "", "Durdurmak için /iptal yazın."].join("\n");
}

// Headline for the result of "arsantuptakip-release apk <sha>" (exit 7: the signed APK is still being built on GitHub).
function apkResultHeader(code, trailers = {}) {
  if (code === 0) return trailers.APK_BUILD ? `✅ APK yayında: ${trailers.APK_VERSION ?? "?"} (build ${trailers.APK_BUILD}).` : "✅ APK zaten güncel.";
  if (code === 7) return "⏳ İmzalı APK henüz hazır değil.";
  return `❌ APK yayınlanamadı (çıkış ${code}); yayındaki APK değişmedi.`;
}

module.exports = {
  CONFIRM_TTL_MS, HELP_TEXT, TELEGRAM_TEXT_LIMIT,
  foldTurkish, parseCommand, isAuthorized, createConfirmation, checkConfirmation, parseTrailers, splitMessage, shortSha, formatProgress,
  apkResultHeader,
};
