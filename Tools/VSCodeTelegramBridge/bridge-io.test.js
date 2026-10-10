"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const manifest = require("./package.json");
const {
  acquireDispatchLock,
  releaseDispatchLock,
  writeJsonAtomically,
} = require("./bridge-io");
const { buildCopilotPrompt } = require("./prompt-builder");
const { waitForClipboardText } = require("./response-capture");

test("bridge activates after startup even if workspaceContains scanning times out", () => {
  assert.ok(manifest.activationEvents.includes("onStartupFinished"));
});

async function withTempDirectory(run) {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "arsangaz-bridge-"));
  try {
    await run(directory);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
}

test("atomic JSON write replaces content and removes its temporary file", async () => {
  await withTempDirectory(async (directory) => {
    const filePath = path.join(directory, "result.json");
    await fs.writeFile(filePath, '{"status":"old"}', "utf8");

    await writeJsonAtomically(filePath, { status: "new" });

    assert.deepEqual(JSON.parse(await fs.readFile(filePath, "utf8")), { status: "new" });
    assert.deepEqual(await fs.readdir(directory), ["result.json"]);
  });
});

test("atomic JSON write cleans up its temporary file when rename fails", async () => {
  await withTempDirectory(async (directory) => {
    const blockedPath = path.join(directory, "blocked.json");
    await fs.mkdir(blockedPath);

    await assert.rejects(writeJsonAtomically(blockedPath, { status: "failed" }));
    assert.deepEqual(await fs.readdir(directory), ["blocked.json"]);
  });
});

test("a live dispatch lock is not removed just because it is older than one minute", async () => {
  await withTempDirectory(async (directory) => {
    const lockPath = path.join(directory, "bridge-dispatch.lock");
    await fs.writeFile(lockPath, JSON.stringify({ pid: process.pid }), "utf8");
    const oldTime = new Date(Date.now() - 2 * 60_000);
    await fs.utimes(lockPath, oldTime, oldTime);

    assert.equal(await acquireDispatchLock(directory), null);
    assert.equal(JSON.parse(await fs.readFile(lockPath, "utf8")).pid, process.pid);
  });
});

test("a running owner keeps the dispatch lock even after the maximum stale age", async () => {
  await withTempDirectory(async (directory) => {
    const lockPath = path.join(directory, "bridge-dispatch.lock");
    await fs.writeFile(lockPath, JSON.stringify({ pid: process.pid }), "utf8");
    const oldTime = new Date(Date.now() - 2 * 60 * 60_000);
    await fs.utimes(lockPath, oldTime, oldTime);

    assert.equal(await acquireDispatchLock(directory), null);
    assert.equal(JSON.parse(await fs.readFile(lockPath, "utf8")).pid, process.pid);
  });
});

test("a dispatch lock owned by a dead process can be recovered", async () => {
  await withTempDirectory(async (directory) => {
    const lockPath = path.join(directory, "bridge-dispatch.lock");
    await fs.writeFile(lockPath, JSON.stringify({ pid: 2_147_483_647 }), "utf8");

    const lock = await acquireDispatchLock(directory);
    assert.ok(lock);
    assert.equal(JSON.parse(await fs.readFile(lockPath, "utf8")).pid, process.pid);
    await releaseDispatchLock(directory, lock);
    await assert.rejects(fs.access(lockPath), { code: "ENOENT" });
  });
});

test("releasing a lock does not remove a replacement lock owned by another process", async () => {
  await withTempDirectory(async (directory) => {
    const lockPath = path.join(directory, "bridge-dispatch.lock");
    const lock = await acquireDispatchLock(directory);
    assert.ok(lock);
    await fs.writeFile(lockPath, JSON.stringify({ pid: 2_147_483_647, ownerToken: "replacement" }), "utf8");

    await releaseDispatchLock(directory, lock);

    assert.equal(JSON.parse(await fs.readFile(lockPath, "utf8")).ownerToken, "replacement");
  });
});

test("clipboard capture waits for the delayed final response and preserves it until stable", async () => {
  let clipboard = "bridge marker";
  let reads = 0;
  let elapsed = 0;
  const report = await waitForClipboardText(
    async () => {
      reads += 1;
      if (reads === 3) {
        clipboard = "Final answer";
      }
      return clipboard;
    },
    {
      marker: "bridge marker",
      previousText: "previous clipboard",
      pollIntervalMs: 1,
      stableMs: 3,
      now: () => elapsed,
      delay: async (milliseconds) => {
        elapsed += milliseconds;
      },
    },
  );

  assert.equal(report, "Final answer");
  assert.ok(reads >= 6);
});

test("clipboard capture rejects unchanged markers and empty contents", async () => {
  for (const value of ["bridge marker", "  "]) {
    const report = await waitForClipboardText(
      async () => value,
      {
        marker: "bridge marker",
        pollIntervalMs: 1,
        stableMs: 1,
        shouldContinue: (() => {
          let polls = 0;
          return () => polls++ < 3;
        })(),
      },
    );

    assert.equal(report, null);
  }
});

test("clipboard capture accepts a final response matching the previous clipboard contents", async () => {
  const report = await waitForClipboardText(
    async () => "previous clipboard",
    {
      marker: "bridge marker",
      pollIntervalMs: 1,
      stableMs: 2,
    },
  );

  assert.equal(report, "previous clipboard");
});

test("Copilot prompt preserves Telegram text and carries an isolated correlation marker", () => {
  const message = " \nKullanıcının birebir mesajı.\nSon satır  \n";
  const result = buildCopilotPrompt({
    id: "telegram-1",
    taskId: "task-1",
    text: message,
    context: "Önceki Telegram mesajları Copilot'a gönderilmemeli.",
  });

  assert.equal(result.prompt.startsWith(message), true);
  assert.match(result.correlationMarker, /^<!-- arsan-gaz-telegram-correlation:[a-f0-9]{48} -->$/);
  assert.equal(result.prompt.slice(message.length), `\n\n${result.correlationMarker}`);
});

test("clipboard capture preserves the exact final response, including boundary whitespace", async () => {
  const expected = " \nCopilot yanıtı.\nSon satır  \n";
  let elapsed = 0;
  const report = await waitForClipboardText(
    async () => expected,
    {
      marker: "bridge marker",
      pollIntervalMs: 1,
      stableMs: 2,
      now: () => elapsed,
      delay: async (milliseconds) => {
        elapsed += milliseconds;
      },
    },
  );

  assert.equal(report, expected);
});

test("clipboard capture allows the final response to arrive after the old 30-second timeout", async () => {
  let clipboard = "bridge marker";
  let elapsed = 0;
  const report = await waitForClipboardText(
    async () => {
      if (elapsed >= 31_000) {
        clipboard = "Delayed final answer";
      }
      return clipboard;
    },
    {
      marker: "bridge marker",
      pollIntervalMs: 1000,
      stableMs: 2,
      now: () => elapsed,
      delay: async (milliseconds) => {
        elapsed += milliseconds;
      },
    },
  );

  assert.equal(report, "Delayed final answer");
  assert.ok(elapsed >= 32_000);
});

test("clipboard capture stops only when the bridge is explicitly closing", async () => {
  let polls = 0;
  const report = await waitForClipboardText(
    async () => {
      polls += 1;
      return "bridge marker";
    },
    {
      marker: "bridge marker",
      pollIntervalMs: 1,
      shouldContinue: () => polls < 3,
      delay: async () => {},
    },
  );

  assert.equal(report, null);
  assert.equal(polls, 3);
});

test("clipboard capture restarts its stability window when the response changes", async () => {
  let clipboard = "first answer";
  let reads = 0;
  let elapsed = 0;
  const report = await waitForClipboardText(
    async () => {
      reads += 1;
      if (reads === 3) {
        clipboard = "final answer";
      }
      return clipboard;
    },
    {
      marker: "bridge marker",
      previousText: "previous clipboard",
      pollIntervalMs: 1,
      stableMs: 3,
      now: () => elapsed,
      delay: async (milliseconds) => {
        elapsed += milliseconds;
      },
    },
  );

  assert.equal(report, "final answer");
  assert.ok(reads >= 6);
});
