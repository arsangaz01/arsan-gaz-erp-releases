import assert from "node:assert/strict";
import test from "node:test";
import worker from "../src/index.ts";
import { runScheduledRelay, WATCHER_OFFLINE_AFTER_MS } from "../src/telegram-relay.ts";

const token = "local-test-token";
const chatId = 4242;

function createKv(initial = {}) {
  const values = new Map(Object.entries(initial));
  return {
    values,
    async get(key) {
      return values.has(key) ? values.get(key) : null;
    },
    async put(key, value) {
      values.set(key, value);
    },
  };
}

function createEnv(state, ai = async () => ({ response: "Bulut yanıtı" })) {
  return {
    AGENT_API_TOKEN: token,
    AI: { run: ai },
    STATE: state,
    TELEGRAM_BOT_TOKEN: "bot-token",
    TELEGRAM_CHAT_ID: String(chatId),
    BRIEFING_HOUR: "25",
  };
}

function mockTelegram(t, updates) {
  const calls = [];
  const originalFetch = globalThis.fetch;
  t.after(() => {
    globalThis.fetch = originalFetch;
  });
  globalThis.fetch = async (input, init) => {
    const url = String(input);
    const method = url.split("/").pop();
    const body = init?.body ? JSON.parse(init.body) : {};
    calls.push({ method, body });
    if (method === "getUpdates") {
      return Response.json({ ok: true, result: body.offset ? [] : updates });
    }
    if (method === "sendMessage") {
      return Response.json({ ok: true, result: {} });
    }
    throw new Error(`Unexpected fetch: ${url}`);
  };
  return calls;
}

function message(updateId, text, dateMs, fromChat = chatId) {
  return { update_id: updateId, message: { message_id: updateId, date: Math.floor(dateMs / 1000), chat: { id: fromChat }, text } };
}

test("relay stays silent while the Watcher heartbeat is fresh", async (t) => {
  const now = Date.now();
  const state = createKv({ "watcher:lastSeen": String(now - 60_000) });
  const calls = mockTelegram(t, [message(1, "merhaba", now)]);

  await runScheduledRelay(createEnv(state), now);

  assert.equal(calls.length, 0);
});

test("offline relay answers chat, defers commands, skips stale or foreign messages and confirms the batch", async (t) => {
  const now = Date.now();
  const lastSeen = now - WATCHER_OFFLINE_AFTER_MS - 60_000;
  const state = createKv({ "watcher:lastSeen": String(lastSeen) });
  const calls = mockTelegram(t, [
    message(10, "eski mesaj", lastSeen - 5_000),
    message(11, "/emir testleri çalıştır", now - 30_000),
    message(12, "havale nasıl yapılır", now - 20_000),
    message(13, "/durum", now - 10_000, 999),
  ]);
  let aiPrompt = "";

  await runScheduledRelay(createEnv(state, async (_model, input) => {
    aiPrompt = input.messages[1].content;
    return { response: "Havale bankadan yapılır." };
  }), now);

  const sent = calls.filter((call) => call.method === "sendMessage").map((call) => call.body.text);
  assert.equal(sent.length, 2, "only the two fresh messages from the allowed chat are answered");
  assert.match(sent[0], /Emriniz kaydedildi/);
  assert.match(sent[1], /Havale bankadan yapılır/);
  assert.equal(aiPrompt, "havale nasıl yapılır", "'havale' must not be treated as a weather question");

  const deferred = JSON.parse(state.values.get("telegram:deferred"));
  assert.deepEqual(deferred.map((item) => [item.updateId, item.text]), [[11, "/emir testleri çalıştır"]]);

  const confirm = calls.filter((call) => call.method === "getUpdates").at(-1);
  assert.equal(confirm.body.offset, 14);
});

test("a failed reply still confirms the batch so messages are not answered twice", async (t) => {
  const now = Date.now();
  const state = createKv({ "watcher:lastSeen": "0" });
  const calls = mockTelegram(t, [message(20, "selam", now)]);

  await runScheduledRelay(createEnv(state, async () => {
    throw new Error("AI down");
  }), now);

  assert.equal(calls.filter((call) => call.method === "getUpdates").at(-1).body.offset, 21);
});

test("Watcher heartbeat returns deferred commands and ack removes them", async () => {
  const state = createKv({
    "telegram:deferred": JSON.stringify([
      { updateId: 5, chatId, text: "/emir a", receivedUtc: "x" },
      { updateId: 6, chatId, text: "/emir b", receivedUtc: "y" },
    ]),
  });
  const env = createEnv(state);
  const authorized = { Authorization: `Bearer ${token}`, "Content-Type": "application/json" };

  const heartbeat = await worker.fetch(
    new Request("https://worker.test/watcher/heartbeat", { method: "POST", headers: authorized, body: "{}" }),
    env,
    {},
  );
  const heartbeatBody = await heartbeat.json();
  assert.equal(heartbeat.status, 200);
  assert.equal(heartbeatBody.relayConfigured, true);
  assert.deepEqual(heartbeatBody.deferred.map((item) => item.updateId), [5, 6]);
  assert.ok(Number(state.values.get("watcher:lastSeen")) > 0);

  const ack = await worker.fetch(
    new Request("https://worker.test/watcher/ack", { method: "POST", headers: authorized, body: JSON.stringify({ updateIds: [5] }) }),
    env,
    {},
  );
  assert.deepEqual(await ack.json(), { remaining: 1 });

  const unauthorized = await worker.fetch(
    new Request("https://worker.test/watcher/heartbeat", { method: "POST", body: "{}" }),
    env,
    {},
  );
  assert.equal(unauthorized.status, 401);
});
