"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { outOfScope, buildPrompt, describeToolCall, AGENT_RULES, WORK_BRANCH } = require("./agent-rules");
const { attachmentsOf, safeName, HELLO_TEXT } = require("./telegram-bot");

test("Tools/ArsanTupTakip dışındaki dosyalar kapsam dışıdır", () => {
  assert.deepEqual(outOfScope(["Tools/ArsanTupTakip/src/a.cs", "Tools/ArsanTupTakip/README.md"]), []);
  assert.deepEqual(outOfScope(["Tools/ArsanTupTakip/x", "Tools/MobileApi/Program.cs", ".github/workflows/ci.yml", "Tools/ArsanTupTakipX/a"]),
    ["Tools/MobileApi/Program.cs", ".github/workflows/ci.yml", "Tools/ArsanTupTakipX/a"]);
  assert.deepEqual(outOfScope(["", "  "]), []);
});

test("ajan talimatları kapsamı, dalı ve yayın kuralını içerir", () => {
  const rules = AGENT_RULES.join("\n");
  assert.match(rules, /yalnızca Tools\/ArsanTupTakip\/\*\*/);
  assert.match(rules, /DEĞİŞTİRME/);
  assert.ok(rules.includes(WORK_BRANCH));
  assert.match(rules, /git push YAPMA/);
  assert.match(rules, /\/yayin/);
  const prompt = buildPrompt({ text: "filtre ekle", attachmentPaths: ["/q/1-foto.jpg"], pendingNote: "Not: 2 commit" });
  assert.match(prompt, /Kullanıcının isteği:\nfiltre ekle$/);
  assert.match(prompt, /- \/q\/1-foto.jpg/);
  assert.match(prompt, /Not: 2 commit/);
});

test("araç çağrıları kısa Türkçe satıra çevrilir", () => {
  assert.equal(describeToolCall("read_file", { path: "Tools/ArsanTupTakip/src/a.cs" }), "📄 Okuyor: a.cs");
  assert.match(describeToolCall("run_terminal_cmd", { command: "dotnet build" }), /^🖥 Komut çalıştırıyor: dotnet build/);
  assert.equal(describeToolCall("todo_write", {}), "🗂 Planı güncelliyor");
});

test("Telegram ekleri ve dosya adları", () => {
  const list = attachmentsOf({
    message_id: 7,
    photo: [{ file_id: "small", file_size: 10 }, { file_id: "big", file_size: 100 }],
    document: { file_id: "doc", file_name: "../rapor.pdf" },
    video: { file_id: "vid" },
  });
  assert.deepEqual(list.map((item) => item.fileId), ["big", "doc", "vid"]);
  assert.equal(safeName("../../etc/passwd", 0), "1-passwd");
  assert.equal(safeName("tüp listesi (1).png", 1), "2-tüp_listesi__1_.png");
  assert.match(HELLO_TEXT, /hazır/);
  assert.match(HELLO_TEXT, /\/durum/);
});
