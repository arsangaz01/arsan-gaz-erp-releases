"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const {
  parseCommand, isAuthorized, createConfirmation, checkConfirmation, parseTrailers, splitMessage, shortSha, foldTurkish, formatProgress,
} = require("./commands");

test("komutlar ve argümanlar ayrıştırılır", () => {
  assert.deepEqual(parseCommand("/durum"), { command: "durum", args: [], known: true });
  assert.deepEqual(parseCommand("  /yayin_onay 123456  "), { command: "yayin_onay", args: ["123456"], known: true });
  assert.deepEqual(parseCommand("/geri_al@TuptakipArsanbot 654321", "TuptakipArsanbot"), { command: "geri_al", args: ["654321"], known: true });
  assert.equal(parseCommand("/durum@BaskaBot", "TuptakipArsanbot"), null);
  assert.equal(parseCommand("tüp sayfasına filtre ekle"), null);
  assert.equal(parseCommand(""), null);
  assert.equal(parseCommand(undefined), null);
  assert.equal(parseCommand("/bilinmeyen").known, false);
});

test("Türkçe harfli ve büyük harfli komutlar tanınır", () => {
  assert.equal(parseCommand("/yayın_onay 111111").command, "yayin_onay");
  assert.equal(parseCommand("/YAYIN").command, "yayin");
  assert.equal(parseCommand("/Yardım").command, "yardim");
  assert.equal(parseCommand("/GERİ_AL").command, "geri_al");
  assert.equal(foldTurkish("ŞÇĞÜÖİı"), "scguoii");
});

test("yalnızca yetkili sohbet kabul edilir", () => {
  assert.equal(isAuthorized({ chat: { id: 12345, type: "private" } }, "12345"), true);
  assert.equal(isAuthorized({ chat: { id: -100777, type: "supergroup" } }, "-100777"), true);
  assert.equal(isAuthorized({ chat: { id: 99999, type: "private" } }, "12345"), false);
  assert.equal(isAuthorized({ chat: { id: 12345 } }, ""), false);
  assert.equal(isAuthorized({ chat: { id: 12345 } }, "abc"), false);
  assert.equal(isAuthorized({}, "12345"), false);
  assert.equal(isAuthorized(null, "12345"), false);
});

test("onay kodu doğru tür, kod ve süreyle çalışır", () => {
  const now = 1_000_000;
  const sha = "a".repeat(40);
  const pending = createConfirmation("yayin", sha, now, () => 482913);
  assert.equal(pending.code, "482913");
  assert.deepEqual(checkConfirmation(pending, "yayin", "482913", now + 1000), { ok: true, sha });
  assert.equal(checkConfirmation(pending, "yayin", "000000", now).ok, false);
  assert.equal(checkConfirmation(pending, "yayin", "48291", now).ok, false);
  assert.equal(checkConfirmation(pending, "yayin", undefined, now).ok, false);
  assert.match(checkConfirmation(pending, "yayin", "482913", now + 16 * 60_000).error, /süresi doldu/);
  assert.match(checkConfirmation(pending, "geri_al", "482913", now).error, /geri alma/);
  assert.match(checkConfirmation(null, "yayin", "482913", now).error, /\/yayin/);
});

test("gerçek rastgele kod 6 hanelidir", () => {
  for (let i = 0; i < 50; i++) assert.match(createConfirmation("yayin", "b".repeat(40)).code, /^\d{6}$/);
});

test("yayın betiği çıktısındaki @@ satırları ayrılır", () => {
  const { text, trailers } = parseTrailers("📦 1 commit\n• abc test\n@@TARGET=0123456789abcdef0123456789abcdef01234567\n@@PENDING=1\n");
  assert.equal(text, "📦 1 commit\n• abc test");
  assert.equal(trailers.TARGET, "0123456789abcdef0123456789abcdef01234567");
  assert.equal(trailers.PENDING, "1");
});

test("uzun mesajlar Telegram sınırına göre bölünür", () => {
  const long = Array.from({ length: 300 }, (_, i) => `satır ${i} ${"x".repeat(30)}`).join("\n");
  const parts = splitMessage(long, 1000);
  assert.ok(parts.length > 1);
  assert.ok(parts.every((part) => part.length <= 1000));
  assert.equal(parts.join("\n").replace(/\n+/g, "\n"), long);
  assert.deepEqual(splitMessage(""), ["(boş)"]);
  assert.equal(splitMessage("y".repeat(2500), 1000).length, 3);
});

test("kısa sha ve ilerleme metni", () => {
  assert.equal(shortSha("0123456789abcdef"), "0123456");
  assert.equal(shortSha(""), "bilinmiyor");
  assert.equal(shortSha("../etc"), "bilinmiyor");
  const text = formatProgress("tg-1", ["a", "b"], 0, 5 * 60_000);
  assert.match(text, /tg-1, 5 dk/);
  assert.match(text, /• b/);
  assert.match(text, /\/iptal/);
});
