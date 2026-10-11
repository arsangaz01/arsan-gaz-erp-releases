"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const { maskPii } = require("./pii-mask");
const { buildMemoryContext, forgetFact, listFacts, memoryDirectory, rememberFact } = require("./jarvis-memory");
const { maskResult } = require("./erp-tools");
const { buildPrompt, describeToolCall } = require("./bridge");

test("maskPii hides phones, e-mails and labelled ids but keeps barcodes", () => {
  const masked = maskPii("Ali tel: 0532 123 45 67, ali.veli@firma.com.tr, TC: 12345678901, VKN 1234567890, barkod 5321234567, 0212-555-12-34");
  assert.doesNotMatch(masked, /123 45 67|ali\.veli@|12345678901|1234567890,|555-12-34/);
  assert.match(masked, /tel: \*+67/);
  assert.match(masked, /a\*\*\*@firma\.com\.tr/);
  assert.match(masked, /TC: \*+01/);
  assert.match(masked, /barkod 5321234567/);
  assert.match(masked, /\*{9}34/);
});

test("maskPii leaves dates and quantities alone", () => {
  const text = "Test: 11.10.2026 · kapasite 40 L · Son 30 günde 12 hareket";
  assert.equal(maskPii(text), text);
});

test("maskResult masks text blocks and keeps the error flag", () => {
  const result = maskResult({ content: [{ type: "text", text: "tel: 05321234567" }], isError: false });
  assert.deepEqual(result, { content: [{ type: "text", text: "tel: *********67" }], isError: false });
});

test("Jarvis memory stores, lists and forgets facts without deleting files", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "jarvis-memory-"));
  try {
    assert.match(await rememberFact(root, "Patron sabah 8'de özet ister."), /Kaydedildi/);
    assert.equal(await rememberFact(root, "patron sabah 8'de özet ister."), "Bu bilgi zaten bellekte.");
    const [fact] = await listFacts(root);
    assert.match(await buildMemoryContext(root), new RegExp(`\\[${fact.key}\\] Patron sabah`));
    assert.equal(await forgetFact(root, fact.key), "Unutuldu.");
    assert.equal(await buildMemoryContext(root), "");
    await fs.access(path.join(memoryDirectory(root), "_unutulan", `${fact.key}.md`));
    await assert.rejects(forgetFact(root, "..\\settings"), /Anahtar geçersiz/);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("Jarvis prompt includes memory and tool guidance; Telegram prompt does not", () => {
  const jarvis = buildPrompt("merhaba", "", [], "jarvis", "- [k1] Depo Gebze'de.");
  assert.match(jarvis, /Jarvis belleği/);
  assert.match(jarvis, /\[k1\] Depo Gebze'de\./);
  assert.match(jarvis, /erp_ozet/);
  const telegram = buildPrompt("merhaba", "", [], "telegram", "- [k1] Depo Gebze'de.");
  assert.doesNotMatch(telegram, /Jarvis belleği|erp_ozet/);
});

test("describeToolCall labels ERP and memory tools", () => {
  assert.equal(describeToolCall("erp_ozet", {}), "📊 ERP'ye bakıyor: erp_ozet");
  assert.equal(describeToolCall("mcp", { toolName: "crm_takip_listesi" }), "📊 ERP'ye bakıyor: crm_takip_listesi");
  assert.equal(describeToolCall("jarvis_hafiza_kaydet", { bilgi: "x" }), "🧠 Belleği güncelliyor");
});
