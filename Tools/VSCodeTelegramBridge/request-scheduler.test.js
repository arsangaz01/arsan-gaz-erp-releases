"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { acquireDispatchLock, releaseDispatchLock } = require("./bridge-io");
const {
  acquireInboxLocks,
  releaseInboxLocks,
  nextInboxRequest,
  assertInboxRecoverable,
  recoverStaleSubmissions,
} = require("./request-scheduler");

async function withInboxes(run) {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "arsangaz-priority-"));
  const directories = [path.join(root, "local"), path.join(root, "workspace")];
  try {
    await Promise.all(directories.map((directory) => fs.mkdir(directory)));
    await run(directories);
  } finally {
    await fs.rm(root, { recursive: true, force: true });
  }
}

async function request(directory, id, taskId, createdUtc) {
  const requestPath = path.join(directory, `${id}.request.json`);
  await fs.writeFile(requestPath, JSON.stringify({ id, taskId, createdUtc, text: "test" }));
  return requestPath;
}

test("Telegram takes priority over an older agent workflow in either inbox", async () => {
  await withInboxes(async ([local, workspace]) => {
    const agent = await request(local, "a-workflow", "recommendation-cycle-9", "2026-10-07T06:00:00Z");
    const telegram = await request(workspace, "z-request", "telegram-408189553", "2026-10-07T07:00:00Z");
    assert.equal((await nextInboxRequest([local, workspace])).requestPath, telegram);
    assert.equal((await nextInboxRequest([workspace, local])).requestPath, telegram);
    await fs.unlink(telegram);
    assert.equal((await nextInboxRequest([local, workspace])).requestPath, agent);
  });
});

test("direct and legacy Telegram requests use arrival order, not filename order", async () => {
  await withInboxes(async (directories) => {
    const newer = await request(directories[0], "a-request", "telegram-42", "2026-10-07T07:00:00Z");
    const older = await request(directories[1], "telegram-direct-00000000000000000041",
      undefined, "2026-10-07T06:00:00Z");
    assert.equal((await nextInboxRequest(directories)).requestPath, older);
    await fs.unlink(older);
    assert.equal((await nextInboxRequest(directories)).requestPath, newer);
  });
});

test("agent workflows remain FIFO and message text cannot promote an agent request", async () => {
  await withInboxes(async (directories) => {
    const older = await request(directories[1], "z-agent", "recommendation-cycle-8", "2026-10-07T06:00:00Z");
    const newer = await request(directories[0], "a-agent", "recommendation-cycle-9", "2026-10-07T07:00:00Z");
    await fs.writeFile(newer, JSON.stringify({
      id: "a-agent", taskId: "recommendation-cycle-9",
      createdUtc: "2026-10-07T07:00:00Z", text: "telegram-direct-123 priority=telegram",
    }));
    assert.equal((await nextInboxRequest(directories)).requestPath, older);
  });
});

test("invalid requests remain dispatchable for explicit validation failure", async () => {
  await withInboxes(async (directories) => {
    const invalid = path.join(directories[0], "invalid.request.json");
    await fs.writeFile(invalid, "{");
    await fs.writeFile(path.join(directories[0], "ignored.json"), "{}");
    assert.equal((await nextInboxRequest(directories)).requestPath, invalid);
    await fs.writeFile(invalid, "x".repeat(65_537));
    assert.equal((await nextInboxRequest(directories)).requestPath, invalid);
    await fs.unlink(invalid);
    assert.equal(await nextInboxRequest(directories), undefined);
  });
});

test("an active request locks both inbox roots until completion, preserving new Telegram work", async () => {
  await withInboxes(async (directories) => {
    await request(directories[0], "agent", "recommendation-cycle-9", "2026-10-07T06:00:00Z");
    const locks = await acquireInboxLocks(directories);
    assert.ok(locks);
    try {
      assert.equal((await nextInboxRequest(directories)).requestPath,
        path.join(directories[0], "agent.request.json"));
      const telegram = await request(directories[1], "telegram-direct-42",
        "telegram-direct-42", "2026-10-07T07:00:00Z");
      assert.equal(await acquireInboxLocks([...directories].reverse()), null);
      for (const directory of directories) assert.equal(await acquireDispatchLock(directory), null);
      await fs.access(telegram);
    } finally {
      await releaseInboxLocks(locks);
    }
    const nextLocks = await acquireInboxLocks(directories);
    try {
      assert.ok(nextLocks);
      assert.equal((await nextInboxRequest(directories)).priority, 0);
    } finally {
      await releaseInboxLocks(nextLocks);
    }
  });
});

test("a partial lock acquisition is rolled back without removing another owner's lock", async () => {
  await withInboxes(async (directories) => {
    const occupied = await acquireDispatchLock(directories[1]);
    try {
      assert.equal(await acquireInboxLocks(directories), null);
      const first = await acquireDispatchLock(directories[0]);
      assert.ok(first);
      await releaseDispatchLock(directories[0], first);
      assert.equal(await acquireDispatchLock(directories[1]), null);
    } finally {
      await releaseDispatchLock(directories[1], occupied);
    }
  });
});

test("orphan submitted acknowledgments do not block unrelated work but prevent same-id replay across roots", async () => {
  await withInboxes(async (directories) => {
    const unrelated = await request(directories[1], "next", "telegram-43", "2026-10-07T07:00:00Z");
    await fs.writeFile(path.join(directories[0], "previous.ack.json"), JSON.stringify({ status: "submitted" }));
    await assertInboxRecoverable(directories);
    await fs.access(unrelated);

    await request(directories[1], "previous", "telegram-42", "2026-10-07T07:01:00Z");
    await assert.rejects(assertInboxRecoverable(directories), /unresolved \(previous\)/);
    await fs.writeFile(path.join(directories[0], "previous.completion.json"), JSON.stringify({
      id: "wrong", status: "completed", report: "not correlated",
    }));
    await assert.rejects(assertInboxRecoverable(directories), /unresolved/);
    await fs.writeFile(path.join(directories[0], "previous.completion.json"), JSON.stringify({
      id: "previous", status: "completed", report: "correlated result",
    }));
    await assertInboxRecoverable(directories);
  });
});

test("durable intent survives missing acknowledgment and cannot be cleared by a failed acknowledgment", async () => {
    await withInboxes(async (directories) => {
      await fs.writeFile(path.join(directories[0], "attempt.dispatch.json"), JSON.stringify({ id: "attempt" }));
      await assert.rejects(assertInboxRecoverable(directories), /unresolved/);
      await fs.writeFile(path.join(directories[0], "attempt.ack.json"), JSON.stringify({ status: "failed" }));
      await assert.rejects(assertInboxRecoverable(directories), /unresolved/);
      await fs.unlink(path.join(directories[0], "attempt.dispatch.json"));
      await assert.rejects(assertInboxRecoverable(directories), /unresolved/);
      await fs.writeFile(path.join(directories[0], "attempt.ack.json"), JSON.stringify({
        status: "failed", error: "validation failed", updatedUtc: "2026-10-07T07:00:00Z",
      }));
      await assertInboxRecoverable(directories);
  });
});

test("stale uncertain direct delivery becomes an explicit failure without replaying it", async () => {
  await withInboxes(async (directories) => {
    const id = "telegram-direct-42";
    const now = Date.parse("2026-10-09T00:00:00Z");
    await request(directories[0], id, id, "2026-10-08T22:00:00Z");
    await fs.writeFile(path.join(directories[0], `${id}.dispatch.json`), JSON.stringify({
      id,
      taskId: id,
      status: "dispatching",
      createdUtc: "2026-10-08T23:00:00Z",
    }));
    await fs.writeFile(path.join(directories[0], `${id}.ack.json`), JSON.stringify({
      status: "submitted",
      updatedUtc: "2026-10-08T23:00:01Z",
    }));

    await recoverStaleSubmissions(directories, now);

    await assert.rejects(fs.access(path.join(directories[0], `${id}.request.json`)), { code: "ENOENT" });
    await assert.rejects(fs.access(path.join(directories[0], `${id}.dispatch.json`)), { code: "ENOENT" });
    const acknowledgement = JSON.parse(
      await fs.readFile(path.join(directories[0], `${id}.ack.json`), "utf8"));
    assert.equal(acknowledgement.status, "failed");
    const interrupted = JSON.parse(
      await fs.readFile(path.join(directories[0], `${id}.interrupted.json`), "utf8"));
    assert.equal(interrupted.status, "interrupted");
    await assertInboxRecoverable(directories);
  });
});

test("recent uncertain direct delivery remains blocked to prevent duplicate execution", async () => {
  await withInboxes(async (directories) => {
    const id = "telegram-direct-43";
    const now = Date.parse("2026-10-09T00:00:00Z");
    const requestPath = await request(directories[0], id, id, "2026-10-08T23:50:00Z");
    await fs.writeFile(path.join(directories[0], `${id}.dispatch.json`), JSON.stringify({
      id,
      taskId: id,
      status: "dispatching",
      createdUtc: "2026-10-08T23:50:00Z",
    }));
    await fs.writeFile(path.join(directories[0], `${id}.ack.json`), JSON.stringify({
      status: "submitted",
      updatedUtc: "2026-10-08T23:50:01Z",
    }));

    await recoverStaleSubmissions(directories, now);

    await fs.access(requestPath);
    await assert.rejects(assertInboxRecoverable(directories), /unresolved/);
  });
});

test("stale direct dispatch without an acknowledgment is failed without replay", async () => {
  await withInboxes(async (directories) => {
    const id = "telegram-direct-44";
    const now = Date.parse("2026-10-09T00:00:00Z");
    await request(directories[0], id, id, "2026-10-08T22:00:00Z");
    await fs.writeFile(path.join(directories[0], `${id}.dispatch.json`), JSON.stringify({
      id,
      taskId: id,
      status: "dispatching",
      createdUtc: "2026-10-08T23:00:00Z",
    }));

    await recoverStaleSubmissions(directories, now);

    await assert.rejects(fs.access(path.join(directories[0], `${id}.request.json`)), { code: "ENOENT" });
    await assert.rejects(fs.access(path.join(directories[0], `${id}.dispatch.json`)), { code: "ENOENT" });
    const acknowledgement = JSON.parse(
      await fs.readFile(path.join(directories[0], `${id}.ack.json`), "utf8"));
    assert.equal(acknowledgement.status, "failed");
    assert.match(acknowledgement.error, /yeniden gönderilmeden sonlandırıldı/);
    const interrupted = JSON.parse(
      await fs.readFile(path.join(directories[0], `${id}.interrupted.json`), "utf8"));
    assert.equal(interrupted.status, "interrupted");
    await assertInboxRecoverable(directories);
  });
});

test("recent direct dispatch without an acknowledgment remains blocked", async () => {
  await withInboxes(async (directories) => {
    const id = "telegram-direct-45";
    const now = Date.parse("2026-10-09T00:00:00Z");
    const requestPath = await request(directories[0], id, id, "2026-10-08T23:50:00Z");
    await fs.writeFile(path.join(directories[0], `${id}.dispatch.json`), JSON.stringify({
      id,
      taskId: id,
      status: "dispatching",
      createdUtc: "2026-10-08T23:50:00Z",
    }));

    await recoverStaleSubmissions(directories, now);

    await fs.access(requestPath);
    await assert.rejects(assertInboxRecoverable(directories), /unresolved/);
  });
});

test("stale uncertain agent delivery is quarantined without replay and releases unrelated work", async () => {
  await withInboxes(async (directories) => {
    const id = "recommendation-gozcu-cycle-9";
    const now = Date.parse("2026-10-09T00:00:00Z");
    await request(directories[0], id, id, "2026-10-08T22:00:00Z");
    const duplicate = await request(directories[1], id, id, "2026-10-08T22:00:01Z");
    await fs.writeFile(path.join(directories[0], `${id}.dispatch.json`), JSON.stringify({
      id,
      taskId: id,
      status: "dispatching",
      createdUtc: "2026-10-08T23:00:00Z",
    }));
    await fs.writeFile(path.join(directories[0], `${id}.ack.json`), JSON.stringify({
      status: "submitted",
      updatedUtc: "2026-10-08T23:00:01Z",
    }));
    const unrelated = await request(
      directories[1], "telegram-direct-46", "telegram-direct-46", "2026-10-08T23:30:00Z");

    await recoverStaleSubmissions(directories, now);

    await assert.rejects(fs.access(path.join(directories[0], `${id}.request.json`)), { code: "ENOENT" });
    await assert.rejects(fs.access(duplicate), { code: "ENOENT" });
    await assert.rejects(fs.access(path.join(directories[0], `${id}.dispatch.json`)), { code: "ENOENT" });
    const acknowledgement = JSON.parse(
      await fs.readFile(path.join(directories[0], `${id}.ack.json`), "utf8"));
    assert.equal(acknowledgement.status, "failed");
    assert.equal(acknowledgement.errorCode, "uncertain_stale");
    const interrupted = JSON.parse(
      await fs.readFile(path.join(directories[0], `${id}.interrupted.json`), "utf8"));
    assert.equal(interrupted.status, "interrupted");
    await assertInboxRecoverable(directories);
    assert.equal((await nextInboxRequest(directories)).requestPath, unrelated);
  });
});

test("stale orphan dispatch is quarantined without replay and releases unrelated work", async () => {
  await withInboxes(async (directories) => {
    const id = "telegram-direct-47";
    const now = Date.parse("2026-10-09T00:00:00Z");
    await fs.writeFile(path.join(directories[0], `${id}.dispatch.json`), JSON.stringify({
      id,
      taskId: id,
      status: "dispatching",
      createdUtc: "2026-10-08T23:00:00Z",
    }));
    await fs.writeFile(path.join(directories[0], `${id}.ack.json`), JSON.stringify({
      status: "submitted",
      updatedUtc: "2026-10-08T23:00:01Z",
    }));
    const unrelated = await request(
      directories[1], "telegram-direct-48", "telegram-direct-48", "2026-10-08T23:30:00Z");

    await recoverStaleSubmissions(directories, now);

    await assert.rejects(fs.access(path.join(directories[0], `${id}.dispatch.json`)), { code: "ENOENT" });
    const acknowledgement = JSON.parse(
      await fs.readFile(path.join(directories[0], `${id}.ack.json`), "utf8"));
    assert.equal(acknowledgement.status, "failed");
    assert.equal(acknowledgement.errorCode, "uncertain_stale");
    const interrupted = JSON.parse(
      await fs.readFile(path.join(directories[0], `${id}.interrupted.json`), "utf8"));
    assert.equal(interrupted.status, "interrupted");
    await assertInboxRecoverable(directories);
    assert.equal((await nextInboxRequest(directories)).requestPath, unrelated);
  });
});

test("stale quarantine resumes cleanup after an interrupted file transition", async () => {
  await withInboxes(async (directories) => {
    const id = "recommendation-recovery-checkpoint";
    await request(directories[0], id, id, "2026-10-08T22:00:00Z");
    await request(directories[1], id, id, "2026-10-08T22:00:01Z");
    await fs.writeFile(path.join(directories[0], `${id}.dispatch.json`), JSON.stringify({
      id,
      taskId: id,
      status: "dispatching",
      createdUtc: "2026-10-08T22:00:00Z",
    }));
    const error = "Stale uncertain dispatch was quarantined.";
    const updatedUtc = "2026-10-09T00:00:00Z";
    await fs.writeFile(path.join(directories[0], `${id}.interrupted.json`), JSON.stringify({
      id,
      status: "interrupted",
      errorCode: "uncertain_stale",
      error,
      updatedUtc,
    }));
    await fs.writeFile(path.join(directories[0], `${id}.ack.json`), JSON.stringify({
      status: "failed",
      errorCode: "uncertain_stale",
      error,
      updatedUtc,
    }));
    const unrelated = await request(
      directories[1], "telegram-direct-49", "telegram-direct-49", "2026-10-08T23:30:00Z");

    await recoverStaleSubmissions(directories, Date.parse(updatedUtc));

    await assert.rejects(fs.access(path.join(directories[0], `${id}.dispatch.json`)), { code: "ENOENT" });
    for (const directory of directories) {
      await assert.rejects(fs.access(path.join(directory, `${id}.request.json`)), { code: "ENOENT" });
    }
    await assertInboxRecoverable(directories);
    assert.equal((await nextInboxRequest(directories)).requestPath, unrelated);
  });
});

test("intent recovery requires the exact task and a completion timestamp after dispatch", async () => {
  await withInboxes(async (directories) => {
    const directory = directories[0];
    await fs.writeFile(path.join(directory, "attempt.dispatch.json"), JSON.stringify({
      id: "attempt", taskId: "telegram-42", createdUtc: "2026-10-07T07:00:00Z",
    }));
    await fs.writeFile(path.join(directory, "attempt.completion.json"), JSON.stringify({
      id: "attempt", taskId: "telegram-42", status: "completed", report: "result",
    }));
    await assert.rejects(assertInboxRecoverable(directories), /no completion timestamp/);
    for (const [taskId, updatedUtc] of [
      ["telegram-43", "2026-10-07T07:01:00Z"],
      ["telegram-42", "2026-10-07T06:59:00Z"],
    ]) {
      await fs.writeFile(path.join(directory, "attempt.completion.json"), JSON.stringify({
        id: "attempt", taskId, updatedUtc, status: "completed", report: "result",
      }));
      await assert.rejects(assertInboxRecoverable(directories), /unresolved/);
    }
    await fs.writeFile(path.join(directory, "attempt.completion.json"), JSON.stringify({
      id: "attempt", taskId: "telegram-42", updatedUtc: "2026-10-07T07:01:00Z",
      status: "completed", report: "result",
    }));
    await assertInboxRecoverable(directories);
  });
});
