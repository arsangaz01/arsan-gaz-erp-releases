"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { randomUUID } = require("node:crypto");
const { isAuthorizedSessionIdle } = require("./session-readiness");

async function withSession(run) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "arsangaz-readiness-"));
  const previousProfile = process.env.USERPROFILE;
  process.env.USERPROFILE = root;
  const sessionId = randomUUID();
  const directory = path.join(root, "data", "CopilotInbox");
  const eventsPath = path.join(root, ".copilot", "session-state", sessionId, "events.jsonl");
  const authorizationPath = path.join(path.dirname(directory), "copilot-session-report.json");
  try {
    await fs.mkdir(path.dirname(eventsPath), { recursive: true });
    await fs.mkdir(directory, { recursive: true });
    await fs.writeFile(authorizationPath, JSON.stringify({
      sessionId, notBeforeUtc: "2026-10-07T00:00:00Z",
    }));
    await fs.writeFile(eventsPath, "");
    const append = (type, extra = {}) => fs.appendFile(eventsPath, JSON.stringify({
      type, timestamp: "2026-10-07T08:00:00Z", ...extra,
    }) + "\n");
    await run({ directory, eventsPath, authorizationPath, append });
  } finally {
    if (previousProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = previousProfile;
    await fs.rm(root, { recursive: true, force: true });
  }
}

test("manual work and candidate finals remain busy until root task completion", async () => {
  await withSession(async ({ directory, append }) => {
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await append("user.message");
    await append("assistant.message", {
      data: { content: "candidate", phase: "final_answer", toolRequests: [] },
    });
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await append("session.task_complete", { agentId: "child" });
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await append("session.task_complete");
    assert.equal(await isAuthorizedSessionIdle(directory), true);
    await append("user.message", { data: { content: "new work" } });
    assert.equal(await isAuthorizedSessionIdle(directory), false);
  });
});

test("settled root final-answer turn ends establish readiness but tool turns do not", async () => {
  await withSession(async ({ directory, append }) => {
    await append("assistant.turn_start");
    await append("assistant.message", {
      data: { content: "tool request", phase: "final_answer", toolRequests: [{}] },
    });
    await append("assistant.turn_end");
    assert.equal(await isAuthorizedSessionIdle(directory), false);

    await append("assistant.turn_start");
    await append("assistant.message", {
      data: { content: "final response", phase: "final_answer", toolRequests: [] },
    });
    await append("assistant.turn_end", { timestamp: new Date().toISOString() });
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await new Promise(resolve => setTimeout(resolve, 3_100));
    assert.equal(await isAuthorizedSessionIdle(directory), true);

    await append("session.unrecognized_event");
    assert.equal(await isAuthorizedSessionIdle(directory), false);

    await append("tool.execution_start");
    assert.equal(await isAuthorizedSessionIdle(directory), false);
  });
});

test("assistant turn end accepts an unphased final message only after the settle interval", async () => {
  await withSession(async ({ directory, append }) => {
    await append("assistant.turn_start");
    await append("assistant.message", {
      data: { content: "final response without a phase", toolRequests: [] },
    });
    await append("assistant.turn_end", { timestamp: new Date().toISOString() });
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await new Promise(resolve => setTimeout(resolve, 3_100));
    assert.equal(await isAuthorizedSessionIdle(directory), true);
    await append("user.message", { data: { content: "new work" } });
    assert.equal(await isAuthorizedSessionIdle(directory), false);
  });
});

test("day-first UTC event timestamps after the authorization boundary establish readiness", async () => {
  await withSession(async ({ directory, append }) => {
    await append("session.task_complete", {
      timestamp: "9.10.2026 15:13:47",
    });
    assert.equal(await isAuthorizedSessionIdle(directory), true);
  });
});

test("startup uses bounded tail and does not dispatch while incremental backlog is unread", async () => {
  await withSession(async ({ directory, eventsPath, append }) => {
    await fs.writeFile(eventsPath, (JSON.stringify({
      type: "assistant.message", timestamp: "2026-10-06T08:00:00Z",
    }) + "\n").repeat(20_000));
    await append("session.task_complete");
    assert.equal(await isAuthorizedSessionIdle(directory), true);
    await fs.appendFile(eventsPath, JSON.stringify({
      type: "assistant.message", timestamp: "2026-10-07T08:00:00Z",
      data: { content: "x".repeat(90_000) },
    }) + "\n");
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await append("session.task_complete");
    assert.equal(await isAuthorizedSessionIdle(directory), true);
  });
});

test("split UTF-8 user events block dispatch even after a prior completion", async () => {
  await withSession(async ({ directory, eventsPath, append }) => {
    await append("session.task_complete");
    assert.equal(await isAuthorizedSessionIdle(directory), true);
    const bytes = Buffer.from(JSON.stringify({
      type: "user.message", timestamp: "2026-10-07T08:00:00Z", data: { content: "çalış" },
    }) + "\n");
    const split = bytes.indexOf(Buffer.from("ç")) + 1;
    await fs.appendFile(eventsPath, bytes.subarray(0, split));
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await fs.appendFile(eventsPath, bytes.subarray(split));
    assert.equal(await isAuthorizedSessionIdle(directory), false);
  });
});

test("changed authorization cannot reuse the previous session's idle state", async () => {
  await withSession(async ({ directory, authorizationPath, append }) => {
    await append("session.task_complete");
    assert.equal(await isAuthorizedSessionIdle(directory), true);
    await fs.writeFile(authorizationPath, JSON.stringify({
      sessionId: randomUUID(), notBeforeUtc: "2026-10-07T00:00:00Z",
    }));
    await assert.rejects(isAuthorizedSessionIdle(directory), { code: "ENOENT" });
  });
});

test("truncation and malformed events pause rather than retaining an idle decision", async () => {
  await withSession(async ({ directory, eventsPath, append }) => {
    await append("session.task_complete");
    assert.equal(await isAuthorizedSessionIdle(directory), true);
    await fs.writeFile(eventsPath, "");
    await assert.rejects(isAuthorizedSessionIdle(directory), /log changed/);
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await fs.appendFile(eventsPath, "{bad}\n");
    await assert.rejects(isAuthorizedSessionIdle(directory), SyntaxError);
    await fs.writeFile(eventsPath, "");
    assert.equal(await isAuthorizedSessionIdle(directory), false);
  });
});

test("pre-authorization completions and session errors do not establish idle", async () => {
  await withSession(async ({ directory, append }) => {
    await append("session.task_complete", { timestamp: "2026-10-06T08:00:00Z" });
    assert.equal(await isAuthorizedSessionIdle(directory), false);
    await append("session.task_complete");
    assert.equal(await isAuthorizedSessionIdle(directory), true);
    await append("session.error");
    assert.equal(await isAuthorizedSessionIdle(directory), false);
  });
});
