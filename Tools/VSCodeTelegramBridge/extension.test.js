"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const os = require("node:os");

const captureCommands = [
  "workbench.action.chat.focusInput",
  "chat.action.focus",
  "workbench.action.chat.copyFinalResponse",
];

test("/ek is rejected instead of being dispatched as an independent request", () => {
  const rejectUnsupportedActiveWorkAddition = loadCapture({}, "rejectUnsupportedActiveWorkAddition");
  for (const text of ["/ek devam et", "/ek@arsangaz_bot ek bilgi"]) {
    assert.throws(() => rejectUnsupportedActiveWorkAddition(text), /ayrı bir istek olarak gönderilmedi/);
  }
  assert.doesNotThrow(() => rejectUnsupportedActiveWorkAddition("/ekip ayrı bir ekip analizi"));
  assert.doesNotThrow(() => rejectUnsupportedActiveWorkAddition("Bu metinde /ek komutu geçiyor."));
});

test("completion envelopes preserve both bridge request and AgentWatcher job IDs", () => {
  const createCompletionEnvelope = loadCapture({}, "createCompletionEnvelope");
  const report = "Copilot completed with terminal evidence.";
  const completion = createCompletionEnvelope("request-17", "telegram-42", report);

  assert.equal(completion.id, "request-17");
  assert.equal(completion.taskId, "telegram-42");
  assert.equal(completion.status, "completed");
  assert.equal(completion.report, report);
  assert.throws(() => createCompletionEnvelope("request-17", "../other-task", report), /task ID/);
  assert.throws(() => createCompletionEnvelope("request-17", "telegram-42", " "), /empty/);
});

test("bridge heartbeat declares the exact extension and protocol versions", () => {
  const heartbeatMetadata = loadCapture({}, "bridgeHeartbeatMetadata");
  const metadata = heartbeatMetadata("0.2.12");
  assert.equal(metadata.extensionVersion, "0.2.12");
  assert.equal(metadata.protocolVersion, 2);
  assert.equal(metadata.dispatchBlocked, false);
  assert.equal(heartbeatMetadata("0.2.17", true).dispatchBlocked, true);
});

test("identical background errors are rate-limited while later repeats remain reportable", () => {
  const vscode = { logs: [] };
  const reportBackgroundError = loadCapture(vscode,
    "(output = { appendLine: message => vscode.logs.push(message) }, reportBackgroundError)");
  assert.equal(reportBackgroundError("inbox scan", new Error("unresolved delivery"), 1_000), true);
  assert.equal(reportBackgroundError("inbox scan", new Error("unresolved delivery"), 2_000), false);
  assert.equal(reportBackgroundError("inbox scan", new Error("unresolved delivery"), 62_000), true);
  assert.equal(vscode.logs.length, 2);
});

function loadCapture(vscode, functionName = "copyCurrentFinalResponse", overrides = {}) {
  const module = { exports: {} };
  const source = fs.readFileSync(path.join(__dirname, "extension.js"), "utf8");
  vm.runInNewContext(`${source}\nmodule.exports.capture = ${functionName};`, {
    module,
    require: (id) => id === "vscode" ? vscode : Object.hasOwn(overrides, id) ? overrides[id] : require(id),
    process,
    Buffer,
    setTimeout,
    setInterval,
    clearInterval,
  });
  return module.exports.capture;
}

async function withDispatchFixture(run) {
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), "arsangaz-dispatch-"));
  const previousAppData = process.env.LOCALAPPDATA;
  process.env.LOCALAPPDATA = path.join(root, "local");
  const projectRoot = path.join(root, "project");
  const directory = path.join(process.env.LOCALAPPDATA, "ArsanGazERP", "AgentWatcherData", "CopilotInbox");
  try {
    await fs.promises.mkdir(directory, { recursive: true });
    await fs.promises.mkdir(path.join(projectRoot, "Tools", "AgentWatcherData", "CopilotInbox"), { recursive: true });
    await fs.promises.writeFile(path.join(projectRoot, "ArsanGazERP.csproj"), "");
    await fs.promises.writeFile(path.join(path.dirname(directory), "settings.json"), JSON.stringify({ CodingBridge: "VS Code" }));
    const requestPath = path.join(directory, "telegram-direct-42.request.json");
    await fs.promises.writeFile(requestPath, JSON.stringify({
      id: "telegram-direct-42", taskId: "telegram-direct-42",
      text: "harmless synthetic request", createdUtc: new Date().toISOString(),
    }));
    const vscode = {
      workspace: { workspaceFolders: [{ uri: { fsPath: projectRoot } }] },
      lm: { selectChatModels: async () => [{ id: "synthetic", family: "gpt-6-luna", vendor: "copilot" }] },
      commands: {}, Uri: { file: (filePath) => filePath },
    };
    await run({ directory, requestPath, vscode });
  } finally {
    if (previousAppData === undefined) delete process.env.LOCALAPPDATA;
    else process.env.LOCALAPPDATA = previousAppData;
    await fs.promises.rm(root, { recursive: true, force: true });
  }
}

test("polling leaves requests to the Cursor bridge when AgentWatcher selects Cursor", async () => {
  await withDispatchFixture(async ({ directory, requestPath, vscode }) => {
    await fs.promises.writeFile(path.join(path.dirname(directory), "settings.json"), "\uFEFF" + JSON.stringify({ CodingBridge: "Cursor" }));
    let submitted = 0;
    vscode.commands.executeCommand = async () => { submitted += 1; return {}; };
    const poll = loadCapture(vscode,
      "(output = { appendLine() {} }, pollInbox)", {
        "./session-readiness": { isAuthorizedSessionIdle: async () => true },
        "./terminal-observer": { createTerminalObserver: async () => async () => "synthetic result" },
      });
    await poll();
    assert.equal(submitted, 0);
    await fs.promises.access(requestPath);
    await assert.rejects(fs.promises.access(path.join(directory, "telegram-direct-42.ack.json")), { code: "ENOENT" });
  });
});

test("polling preserves pending work during manual activity and resets lifecycle locks", async () => {
  await withDispatchFixture(async ({ directory, requestPath, vscode }) => {
    let idle = false;
    let submitted = 0;
    vscode.commands.executeCommand = async () => { submitted += 1; return {}; };
    const poll = loadCapture(vscode,
      "(output = { appendLine() {} }, pollInbox)", {
        "./session-readiness": { isAuthorizedSessionIdle: async () => idle },
        "./terminal-observer": { createTerminalObserver: async () => async () => "synthetic result" },
      });
    await poll();
    assert.equal(submitted, 0);
    await fs.promises.access(requestPath);
    await assert.rejects(fs.promises.access(path.join(directory, "bridge-dispatch.lock")), { code: "ENOENT" });
    idle = true;
    await poll();
    assert.equal(submitted, 1);
    const completion = JSON.parse(await fs.promises.readFile(path.join(directory, "telegram-direct-42.completion.json")));
    assert.equal(completion.report, "synthetic result");
    await assert.rejects(fs.promises.access(requestPath), { code: "ENOENT" });
    await assert.rejects(fs.promises.access(path.join(directory, "bridge-dispatch.lock")), { code: "ENOENT" });
  });
});

test("submission intent precedes Chat command and submitted acknowledgment follows its return", async () => {
  await withDispatchFixture(async ({ directory, requestPath, vscode }) => {
    vscode.commands.executeCommand = async () => {
      await fs.promises.access(path.join(directory, "telegram-direct-42.dispatch.json"));
      await assert.rejects(fs.promises.access(path.join(directory, "telegram-direct-42.ack.json")), { code: "ENOENT" });
      return {};
    };
    const processRequest = loadCapture(vscode,
      "(output = { appendLine() {} }, processRequest)", {
        "./terminal-observer": { createTerminalObserver: async () => async () => "synthetic result" },
      });
    await processRequest(directory, requestPath);
    assert.equal(JSON.parse(await fs.promises.readFile(path.join(directory, "telegram-direct-42.ack.json"))).status, "submitted");
  });
});

test("ambiguous Chat submission failure preserves intent and request without a failed acknowledgment", async () => {
  await withDispatchFixture(async ({ directory, requestPath, vscode }) => {
    const io = require("./bridge-io");
    vscode.commands.executeCommand = async () => { throw new Error("synthetic submission failure"); };
    const processRequest = loadCapture(vscode,
      "(output = { appendLine() {} }, processRequest)", {
        "./terminal-observer": { createTerminalObserver: async () => async () => null },
        "./bridge-io": {
          ...io, writeJsonAtomically: async (filePath, value) => {
            if (filePath.endsWith(".ack.json")) throw new Error("synthetic persistence failure");
            await io.writeJsonAtomically(filePath, value);
          },
        },
      });
    await processRequest(directory, requestPath);
    await fs.promises.access(requestPath);
    await fs.promises.access(path.join(directory, "telegram-direct-42.dispatch.json"));
    await assert.rejects(fs.promises.access(path.join(directory, "telegram-direct-42.ack.json")), { code: "ENOENT" });
    await assert.rejects(require("./request-scheduler").assertInboxRecoverable([directory]), /unresolved/);
  });
});

test("observer failure after accepted submission cannot release the recovery gate", async () => {
  await withDispatchFixture(async ({ directory, requestPath, vscode }) => {
    vscode.commands.executeCommand = async () => ({});
    const processRequest = loadCapture(vscode,
      "(output = { appendLine() {} }, processRequest)", {
        "./terminal-observer": { createTerminalObserver: async () => async () => { throw new Error("synthetic observation failure"); } },
      });
    await processRequest(directory, requestPath);
    await fs.promises.access(requestPath);
    assert.equal(JSON.parse(await fs.promises.readFile(path.join(directory, "telegram-direct-42.ack.json"))).status, "submitted");
    await assert.rejects(require("./request-scheduler").assertInboxRecoverable([directory]), /unresolved/);
  });
});

test("progress write failure does not overwrite an independently recorded completion", async () => {
  await withDispatchFixture(async ({ directory, requestPath, vscode }) => {
    const io = require("./bridge-io");
    vscode.commands.executeCommand = async () => ({});
    const processRequest = loadCapture(vscode,
      "(output = { appendLine() {} }, processRequest)", {
        "./terminal-observer": { createTerminalObserver: async () => async () => "synthetic result" },
        "./bridge-io": {
          ...io, writeJsonAtomically: async (filePath, value) => {
            if (filePath.endsWith(".progress.json") && value.status === "completed") {
              throw new Error("synthetic progress failure");
            }
            await io.writeJsonAtomically(filePath, value);
          },
        },
      });
    await processRequest(directory, requestPath);
    assert.equal(JSON.parse(await fs.promises.readFile(path.join(directory, "telegram-direct-42.ack.json"))).status, "submitted");
    await require("./request-scheduler").assertInboxRecoverable([directory]);
    await assert.rejects(fs.promises.access(requestPath), { code: "ENOENT" });
  });
});

test("pre-submission validation failure preserves the request when failure persistence fails", async () => {
  await withDispatchFixture(async ({ directory, requestPath, vscode }) => {
    await fs.promises.writeFile(requestPath, "{}");
    const processRequest = loadCapture(vscode,
      "(output = { appendLine() {} }, processRequest)", {
        "./bridge-io": { writeJsonAtomically: async () => { throw new Error("synthetic persistence failure"); } },
      });
    await processRequest(directory, requestPath);
    await fs.promises.access(requestPath);
    await assert.rejects(fs.promises.access(path.join(directory, "telegram-direct-42.dispatch.json")), { code: "ENOENT" });
  });
});

test("attachments use the matching inbox root and require exact actual size", async () => {
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), "telegram-bridge-files-"));
  try {
    const inbox = path.join(root, "AgentWatcherData", "CopilotInbox");
    const directory = path.join(path.dirname(inbox), "telegram-attachments");
    await fs.promises.mkdir(directory, { recursive: true });
    const file = path.join(directory, "123.pdf");
    await fs.promises.writeFile(file, "test");
    const validate = loadCapture({}, "validateAttachments");
    const result = await validate([{ localPath: file, size: 4 }], inbox);
    assert.equal(result.length, 1);
    assert.equal(result[0].localPath, await fs.promises.realpath(file));
    for (const size of [undefined, 0, 3, 5, "4", NaN, Infinity]) {
      await assert.rejects(validate([{ localPath: file, size }], inbox), /boyutu/);
    }
    await assert.rejects(validate(Array(11).fill({ localPath: file, size: 4 }), inbox), /listesi/);
    const outside = path.join(root, "outside.pdf");
    await fs.promises.writeFile(outside, "test");
    await assert.rejects(validate([{ localPath: outside, size: 4 }], inbox), /izinli/);
    await fs.promises.writeFile(file, "");
    await assert.rejects(validate([{ localPath: file, size: 0 }], inbox), /boyutu/);
  } finally {
    await fs.promises.rm(root, { recursive: true, force: true });
  }
});

test("attachments reject a redirected directory", async () => {
  const root = await fs.promises.mkdtemp(path.join(os.tmpdir(), "telegram-bridge-files-"));
  try {
    const inbox = path.join(root, "data", "CopilotInbox");
    const directory = path.join(root, "data", "telegram-attachments");
    const target = path.join(root, "target");
    await fs.promises.mkdir(path.dirname(inbox), { recursive: true });
    await fs.promises.mkdir(target);
    await fs.promises.writeFile(path.join(target, "123.pdf"), "test");
    await fs.promises.symlink(target, directory, process.platform === "win32" ? "junction" : "dir");
    const validate = loadCapture({}, "validateAttachments");
    await assert.rejects(validate([{ localPath: path.join(directory, "123.pdf"), size: 4 }], inbox), /yonlendiriliyor/);
  } finally {
    await fs.promises.rm(root, { recursive: true, force: true });
  }
});

test("finishing a card in VS Code releases the Telegram interaction wait", async () => {
  const wait = loadCapture({}, "waitForInteractionResponse");
  const result = await wait(os.tmpdir(), "unused-test-id", async () => "exact terminal report");
  assert.equal(result.terminalReport, "exact terminal report");
});

test("capture focuses the latest response instead of the input or a previously selected answer", async () => {
  const calls = [];
  const expected = " \nCurrent answer  \n";
  let clipboard = "User clipboard";
  let focused = "old-response";
  const capture = loadCapture({
    commands: {
      getCommands: async () => captureCommands,
      executeCommand: async (id) => {
        calls.push(id);
        if (id === captureCommands[0]) focused = "input";
        if (id === captureCommands[1] && focused === "input") focused = "latest-response";
        if (id === captureCommands[2] && focused === "latest-response") clipboard = expected;
      },
    },
    env: {
      clipboard: {
        readText: async () => clipboard,
        writeText: async (value) => { clipboard = value; },
      },
    },
  });

  assert.equal(await capture(), expected);
  assert.deepEqual(calls, captureCommands);
  assert.equal(clipboard, "User clipboard");
});

test("capture rejects missing focus commands before changing the user clipboard", async () => {
  let writes = 0;
  const capture = loadCapture({
    commands: {
      getCommands: async () => [captureCommands[2]],
      executeCommand: async () => { throw new Error("must not execute"); },
    },
    env: {
      clipboard: {
        readText: async () => "User clipboard",
        writeText: async () => { writes += 1; },
      },
    },
  });

  await assert.rejects(capture(), /VS Code/);
  assert.equal(writes, 0);
});

test("capture restores the clipboard and propagates a command failure", async () => {
  let clipboard = "User clipboard";
  const capture = loadCapture({
    commands: {
      getCommands: async () => captureCommands,
      executeCommand: async () => { throw new Error("focus failed"); },
    },
    env: {
      clipboard: {
        readText: async () => clipboard,
        writeText: async (value) => { clipboard = value; },
      },
    },
  });

  await assert.rejects(capture(), /focus failed/);
  assert.equal(clipboard, "User clipboard");
});

test("capture does not overwrite a concurrent user clipboard change", async () => {
  let clipboard = "User clipboard";
  const capture = loadCapture({
    commands: {
      getCommands: async () => captureCommands,
      executeCommand: async () => {
        clipboard = "New user clipboard";
        throw new Error("focus failed");
      },
    },
    env: {
      clipboard: {
        readText: async () => clipboard,
        writeText: async (value) => { clipboard = value; },
      },
    },
  });

  await assert.rejects(capture(), /focus failed/);
  assert.equal(clipboard, "New user clipboard");
});
