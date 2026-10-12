"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const queue = require("./queue");

async function tempRoot() {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "tuptakip-queue-"));
  await queue.ensureLayout(root);
  return root;
}

test("istek yaşam döngüsü: oluştur, sahiplen, ilerleme, sonuç, teslim", async () => {
  const root = await tempRoot();
  const { attachmentsDir } = await queue.beginRequest(root, "tg-1-a");
  await fs.writeFile(path.join(attachmentsDir, "1-foto.jpg"), "x");
  assert.deepEqual(await queue.listPending(root), [], "request.json yazılmadan istek hazır sayılmaz");
  await queue.commitRequest(root, "tg-1-a", { text: "filtre ekle" });
  assert.deepEqual(await queue.listPending(root), ["tg-1-a"]);
  assert.equal((await queue.readRequest(root, "tg-1-a")).text, "filtre ekle");

  assert.equal(await queue.claim(root, "tg-1-a"), true);
  assert.equal(await queue.claim(root, "tg-1-a"), false, "ikinci sahiplenme reddedilir");
  assert.deepEqual(await queue.listPending(root), []);
  assert.deepEqual(await queue.listActive(root), ["tg-1-a"]);

  await queue.writeProgress(root, "tg-1-a", { stage: "working", steps: ["📄 Okuyor"] });
  assert.deepEqual((await queue.readProgress(root, "tg-1-a")).steps, ["📄 Okuyor"]);
  await queue.writeResult(root, "tg-1-a", { status: "completed", report: "tamam" });
  assert.deepEqual(await queue.listActive(root), []);
  assert.deepEqual(await queue.listUndelivered(root), ["tg-1-a"]);
  await queue.markDelivered(root, "tg-1-a");
  assert.deepEqual(await queue.listUndelivered(root), []);

  assert.equal(await queue.cleanup(root, Date.now()), 0, "yeni teslim edilen silinmez");
  assert.equal(await queue.cleanup(root, Date.now() + 8 * 24 * 3600_000), 1);
  assert.deepEqual(await queue.listIds(root), []);
});

test("iptal ve yeni konuşma işaretleri", async () => {
  const root = await tempRoot();
  await queue.beginRequest(root, "tg-2-b");
  await queue.commitRequest(root, "tg-2-b", { text: "x" });
  assert.equal(await queue.isCancelled(root, "tg-2-b"), false);
  await queue.cancel(root, "tg-2-b");
  assert.equal(await queue.isCancelled(root, "tg-2-b"), true);
  assert.equal(await queue.readNewConversationMarker(root), "");
  await queue.requestNewConversation(root, new Date("2026-10-12T10:00:00Z"));
  assert.equal(await queue.readNewConversationMarker(root), "2026-10-12T10:00:00.000Z");
});

test("geçersiz istek kimlikleri reddedilir", async () => {
  const root = await tempRoot();
  for (const bad of ["../x", "a/b", "", "Büyük", ".gizli"]) {
    await assert.rejects(() => queue.beginRequest(root, bad), /Geçersiz/);
  }
});
