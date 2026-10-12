"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const { createInboxClient, parseList, pickClaimable, serverTakeReason, PC_OFFLINE_AFTER_SEC } = require("./server-inbox");

test("list output is parsed into PC age, claimable requests and the server's own claims", () => {
  const list = parseList("PC 12\nREQ telegram-direct-1 30\nMINE mobile-2\nREQ bad/id 5\ngarbage\n");
  assert.deepEqual(list, { pcAgeSec: 12, requests: [{ id: "telegram-direct-1", ageSec: 30 }], mine: ["mobile-2"] });
});

test("PC online with a fresh request: the PC keeps it", () => {
  assert.equal(serverTakeReason({ pcAgeSec: 10, ageSec: 20 }), null);
});

test("PC offline (stale sync heartbeat): the server takes it", () => {
  assert.equal(serverTakeReason({ pcAgeSec: PC_OFFLINE_AFTER_SEC, ageSec: 5 }), "bilgisayar kapalı");
  assert.match(serverTakeReason({ pcAgeSec: -1, ageSec: 5 }), /hiç/);
});

test("PC online but the request waited longer than the limit: the server takes it", () => {
  assert.match(serverTakeReason({ pcAgeSec: 10, ageSec: 600, waitMinutes: 10 }), /10 dakikadır/);
  assert.equal(serverTakeReason({ pcAgeSec: 10, ageSec: 599, waitMinutes: 10 }), null);
  assert.equal(serverTakeReason({ pcAgeSec: 10, ageSec: 99_999, waitMinutes: 0 }), null);
});

test("claimable requests are oldest first and fresh ones stay with an online PC", () => {
  const picked = pickClaimable({ pcAgeSec: 5, requests: [{ id: "a", ageSec: 700 }, { id: "b", ageSec: 30 }, { id: "c", ageSec: 900 }] }, 10);
  assert.deepEqual(picked.map((request) => request.id), ["c", "a"]);
  assert.equal(pickClaimable({ pcAgeSec: 400, requests: [{ id: "x", ageSec: 1 }] }).length, 1);
});

// Simulates cursor-inbox.sh (server claim) and inbox-sync.sh mark (PC) sharing one lock and marker files.
function fakeQueue() {
  const files = new Set(["r1.request.json", "r2.request.json"]);
  const calls = [];
  const run = async (command, args, input) => {
    calls.push({ command, args, input });
    const [op, id, kind] = args.slice(args.indexOf("/helper") + 1);
    if (op === "claim") {
      if (!files.has(`${id}.request.json`)) return "GONE\n";
      if (files.has(`${id}.pc-forwarded`) || files.has(`${id}.server-claim`)) return "TAKEN\n";
      files.add(`${id}.server-claim`);
      return "CLAIMED\n";
    }
    if (op === "put") {
      if (!files.has(`${id}.server-claim`)) throw new Error("not claimed by the server");
      files.add(`${id}.${kind}.json`);
      return "";
    }
    throw new Error(`unexpected ${op}`);
  };
  const pcMark = (id) => (files.has(`${id}.server-claim`) ? "TAKEN" : (files.add(`${id}.pc-forwarded`), "MARKED"));
  return { files, calls, run, pcMark };
}

test("a request goes to exactly one side: server claim first, then the PC is refused", async () => {
  const queue = fakeQueue();
  const client = createInboxClient({ helper: "/helper", run: queue.run });
  assert.equal(await client.claim("r1"), "CLAIMED");
  assert.equal(queue.pcMark("r1"), "TAKEN");
  assert.equal(await client.claim("r1"), "TAKEN");
  assert.deepEqual(queue.calls[0].args.slice(0, 4), ["-n", "-u", "arsangaz", "/helper"]);
});

test("a request the PC marked first is never claimed by the server", async () => {
  const queue = fakeQueue();
  const client = createInboxClient({ helper: "/helper", run: queue.run });
  assert.equal(queue.pcMark("r2"), "MARKED");
  assert.equal(await client.claim("r2"), "TAKEN");
  await assert.rejects(client.put("r2", "completion", { status: "completed" }), /not claimed/);
  assert.equal(await client.claim("missing"), "GONE");
});

test("results are sent base64-encoded JSON for claimed requests only", async () => {
  const queue = fakeQueue();
  const client = createInboxClient({ helper: "/helper", run: queue.run });
  await client.claim("r1");
  await client.put("r1", "completion", { id: "r1", report: "Türkçe rapor ✓" });
  const sent = queue.calls.at(-1).input;
  assert.deepEqual(JSON.parse(Buffer.from(sent, "base64").toString("utf8")), { id: "r1", report: "Türkçe rapor ✓" });
  assert.ok(queue.files.has("r1.completion.json"));
});
