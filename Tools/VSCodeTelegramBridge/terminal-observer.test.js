"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const os = require("node:os");
const path = require("node:path");
const test = require("node:test");
const { consumeEvent, createTerminalObserver } = require("./terminal-observer");

const event = (type, data = {}) => ({ type, data, timestamp: new Date().toISOString() });
const correlationMarker = "<!-- arsan-gaz-telegram-correlation:0123456789abcdef0123456789abcdef0123456789abcdef -->";
const state = () => ({ correlationMarker, matched: false, final: null, turnEndedAt: null });

test("final answer text is not terminal until a root task completion or settled turn end", () => {
  const current = state();
  assert.equal(consumeEvent(current, event("session.task_complete", { summary: "old reply" })), null);
  consumeEvent(current, event("user.message", { content: current.correlationMarker }));
  for (let index = 0; index < 50; index++) {
    assert.equal(consumeEvent(current, event("assistant.message", { content: "still working", phase: "final_answer" })), null);
  }
  assert.equal(consumeEvent(current, event("session.task_complete", { summary: "done" })), "done");
});

test("turn-end fallback requires a matching root final answer with no tool requests", () => {
  const current = state();
  consumeEvent(current, event("user.message", { content: current.correlationMarker }));
  consumeEvent(current, event("assistant.message", {
    content: "tool request",
    phase: "final_answer",
    toolRequests: [{}],
  }));
  consumeEvent(current, event("assistant.turn_end"));
  assert.equal(current.turnEndedAt, null);

  consumeEvent(current, event("assistant.message", {
    content: "final response",
    phase: "final_answer",
    toolRequests: [],
  }));
  consumeEvent(current, event("assistant.turn_end"));
  assert.equal(current.final, "final response");
  assert.ok(Number.isFinite(current.turnEndedAt));

  consumeEvent(current, event("assistant.message", {
    content: "final response without a phase",
    toolRequests: [],
  }));
  consumeEvent(current, event("assistant.turn_end"));
  assert.equal(current.final, "final response without a phase");
  assert.ok(Number.isFinite(current.turnEndedAt));

  consumeEvent(current, event("assistant.turn_start"));
  assert.equal(current.final, null);
  assert.equal(current.turnEndedAt, null);
});

test("observer correlates localized UTC timestamps and phase-less final messages", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "telegram-terminal-local-time-"));
  const oldProfile = process.env.USERPROFILE;
  try {
    process.env.USERPROFILE = root;
    const id = "12345678-1234-1234-1234-123456789012";
    const dataRoot = path.join(root, "data");
    const inbox = path.join(dataRoot, "CopilotInbox");
    const eventsDirectory = path.join(root, ".copilot", "session-state", id);
    await fs.mkdir(inbox, { recursive: true });
    await fs.mkdir(eventsDirectory, { recursive: true });
    await fs.writeFile(path.join(dataRoot, "copilot-session-report.json"),
      JSON.stringify({ sessionId: id, notBeforeUtc: "2026-10-04T12:09:13.304Z" }));
    const events = path.join(eventsDirectory, "events.jsonl");
    await fs.writeFile(events, "");
    const observe = await createTerminalObserver(inbox, correlationMarker);
    await fs.appendFile(events, JSON.stringify({
      ...event("user.message", { content: correlationMarker }),
      timestamp: "9.10.2026 15:13:47",
    }) + "\n");
    await fs.appendFile(events, JSON.stringify({
      ...event("assistant.message", { content: "Copilot yanıtı", toolRequests: [] }),
      timestamp: "9.10.2026 15:13:48",
    }) + "\n");
    await fs.appendFile(events, JSON.stringify({
      ...event("session.task_complete", { summary: "verified completion" }),
      timestamp: "9.10.2026 15:13:49",
    }) + "\n");

    assert.equal(await observe(), "verified completion");
  } finally {
    if (oldProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = oldProfile;
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("any root event after a candidate turn end invalidates fallback completion", () => {
  const current = state();
  consumeEvent(current, event("user.message", { content: current.correlationMarker }));
  consumeEvent(current, event("assistant.message", {
    content: "candidate",
    phase: "final_answer",
    toolRequests: [],
  }));
  consumeEvent(current, event("assistant.turn_end"));
  assert.equal(current.final, "candidate");
  assert.notEqual(current.turnEndedAt, null);

  consumeEvent(current, event("session.unrecognized_event"));
  assert.equal(current.final, null);
  assert.equal(current.turnEndedAt, null);
});

test("root task completion supersedes held final and excludes subagent results", () => {
  const current = state();
  consumeEvent(current, event("user.message", { content: current.correlationMarker }));
  assert.equal(consumeEvent(current, { ...event("session.task_complete", { summary: "child" }), agentId: "child" }), null);
  consumeEvent(current, event("assistant.message", { content: "held", phase: "final_answer" }));
  assert.equal(consumeEvent(current, event("session.task_complete", { summary: "root" })), "root");
});

test("autopilot/steering clears stale final and waits for a new terminal answer", () => {
  for (const data of [{ source: "autopilot" }, { isAutopilotContinuation: true }, { delivery: "steering" }]) {
    const current = state();
    consumeEvent(current, event("user.message", { content: current.correlationMarker }));
    consumeEvent(current, event("assistant.message", { content: "old final", phase: "final_answer" }));
    consumeEvent(current, event("user.message", data));
    assert.throws(() => consumeEvent(current, event("session.task_complete")), /rapor/);
  }
});

test("tool output and another user request cannot masquerade as completion", () => {
  const current = state();
  consumeEvent(current, event("user.message", { content: current.correlationMarker }));
  consumeEvent(current, event("assistant.message", { content: "tool", phase: "final_answer", toolRequests: [{}] }));
  assert.throws(() => consumeEvent(current, event("session.task_complete")), /rapor/);
  assert.throws(() => consumeEvent(current, event("user.message", { content: "different task" })), /belirsiz/);
});

test("completion is ignored until a user message contains the request correlation marker", () => {
  const current = state();
  consumeEvent(current, event("user.message", { content: "exact Telegram request" }));
  assert.equal(consumeEvent(current, event("session.task_complete", { summary: "unmatched" })), null);
  consumeEvent(current, event("user.message", { content: `formatted request\n${current.correlationMarker}` }));
  assert.equal(consumeEvent(current, event("session.task_complete", { summary: "matched" })), "matched");
});

test("authorized observer ignores history and reconstructs split UTF-8 events", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "telegram-terminal-"));
  const oldProfile = process.env.USERPROFILE;
  try {
    process.env.USERPROFILE = root;
    const id = "12345678-1234-1234-1234-123456789012";
    const dataRoot = path.join(root, "data");
    const inbox = path.join(dataRoot, "CopilotInbox");
    const eventsDirectory = path.join(root, ".copilot", "session-state", id);
    await fs.mkdir(inbox, { recursive: true });
    await fs.mkdir(eventsDirectory, { recursive: true });
    await fs.writeFile(path.join(dataRoot, "copilot-session-report.json"),
      JSON.stringify({ sessionId: id, notBeforeUtc: new Date().toISOString() }));
    const events = path.join(eventsDirectory, "events.jsonl");
    await fs.writeFile(events, JSON.stringify(event("session.task_complete", { summary: "history" })) + "\n");
    const observe = await createTerminalObserver(inbox, correlationMarker);
    assert.equal(await observe(), null);
    await fs.appendFile(events, JSON.stringify(event("user.message", { content: correlationMarker })) + "\n");
    const terminal = Buffer.from(JSON.stringify(event("session.task_complete", { summary: "Türkçe sonuç" })) + "\n");
    const split = terminal.indexOf(Buffer.from("ü")) + 1;
    await fs.appendFile(events, terminal.subarray(0, split));
    assert.equal(await observe(), null);
    await fs.appendFile(events, terminal.subarray(split));
    assert.equal(await observe(), "Türkçe sonuç");
    await fs.writeFile(path.join(dataRoot, "copilot-session-report.json"),
      JSON.stringify({ sessionId: id, notBeforeUtc: "2000-01-01T00:00:00Z" }));
    await assert.rejects(observe(), /yetkisi degisti/);
  } finally {
    if (oldProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = oldProfile;
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("observer waits for a settled root turn end after the matching final answer", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "telegram-terminal-"));
  const oldProfile = process.env.USERPROFILE;
  try {
    process.env.USERPROFILE = root;
    const id = "12345678-1234-1234-1234-123456789012";
    const dataRoot = path.join(root, "data");
    const inbox = path.join(dataRoot, "CopilotInbox");
    const eventsDirectory = path.join(root, ".copilot", "session-state", id);
    await fs.mkdir(inbox, { recursive: true });
    await fs.mkdir(eventsDirectory, { recursive: true });
    await fs.writeFile(path.join(dataRoot, "copilot-session-report.json"),
      JSON.stringify({ sessionId: id, notBeforeUtc: "2000-01-01T00:00:00Z" }));
    const events = path.join(eventsDirectory, "events.jsonl");
    await fs.writeFile(events, "");
    const observe = await createTerminalObserver(inbox, correlationMarker);
    await fs.appendFile(events,
      JSON.stringify(event("user.message", { content: correlationMarker })) + "\n"
        + JSON.stringify(event("assistant.message", {
          content: "Güvenli son yanıt",
          phase: "final_answer",
          toolRequests: [],
        })) + "\n"
        + JSON.stringify(event("assistant.turn_end")) + "\n");
    assert.equal(await observe(), null);
    await new Promise(resolve => setTimeout(resolve, 3_100));
    assert.equal(await observe(), "Güvenli son yanıt");
  } finally {
    if (oldProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = oldProfile;
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("observer skips an unfinished historical line and rejects truncation", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "telegram-terminal-"));
  const oldProfile = process.env.USERPROFILE;
  try {
    process.env.USERPROFILE = root;
    const id = "12345678-1234-1234-1234-123456789012";
    const inbox = path.join(root, "data", "CopilotInbox");
    const eventsDirectory = path.join(root, ".copilot", "session-state", id);
    const authorizationPath = path.join(root, "data", "copilot-session-report.json");
    await fs.mkdir(inbox, { recursive: true });
    await fs.mkdir(eventsDirectory, { recursive: true });
    await fs.writeFile(authorizationPath, JSON.stringify({ sessionId: id, notBeforeUtc: "invalid" }));
    await assert.rejects(createTerminalObserver(inbox, correlationMarker), /yetkisi gecersiz/);
    await fs.writeFile(authorizationPath, JSON.stringify({ sessionId: id, notBeforeUtc: new Date().toISOString() }));
    const events = path.join(eventsDirectory, "events.jsonl");
    await fs.writeFile(events, '{"type":"historical');
    const observe = await createTerminalObserver(inbox, correlationMarker);
    await fs.appendFile(events, '"}\n'
      + JSON.stringify({ ...event("user.message", { content: correlationMarker }), timestamp: "2000-01-01T00:00:00Z" }) + "\n"
      + JSON.stringify(event("session.task_complete", { summary: "unmatched" })) + "\n");
    assert.equal(await observe(), null);
    await fs.appendFile(events, JSON.stringify(event("user.message", { content: correlationMarker })) + "\n"
      + JSON.stringify(event("session.task_complete", { summary: "correct" })) + "\n");
    assert.equal(await observe(), "correct");
    await fs.truncate(events, 0);
    await assert.rejects(observe(), /kaydi degisti/);
  } finally {
    if (oldProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = oldProfile;
    await fs.rm(root, { recursive: true, force: true });
  }
});

test("observer does not consume completion events from an unauthorized session directory", async () => {
  const root = await fs.mkdtemp(path.join(os.tmpdir(), "telegram-terminal-"));
  const oldProfile = process.env.USERPROFILE;
  try {
    process.env.USERPROFILE = root;
    const authorizedId = "047243b4-126e-4d3a-9511-99d9315a6bad";
    const otherSessionId = "12345678-1234-1234-1234-123456789012";
    const inbox = path.join(root, "data", "CopilotInbox");
    const authorizedDirectory = path.join(root, ".copilot", "session-state", authorizedId);
    const otherSessionDirectory = path.join(root, ".copilot", "session-state", otherSessionId);
    await fs.mkdir(inbox, { recursive: true });
    await fs.mkdir(authorizedDirectory, { recursive: true });
    await fs.mkdir(otherSessionDirectory, { recursive: true });
    await fs.writeFile(
      path.join(root, "data", "copilot-session-report.json"),
      JSON.stringify({ sessionId: authorizedId, notBeforeUtc: "2000-01-01T00:00:00Z" }),
    );
    const authorizedEvents = path.join(authorizedDirectory, "events.jsonl");
    const otherSessionEvents = path.join(otherSessionDirectory, "events.jsonl");
    await fs.writeFile(authorizedEvents, "");
    await fs.writeFile(
      otherSessionEvents,
      JSON.stringify(event("user.message", { content: correlationMarker })) + "\n"
        + JSON.stringify(event("session.task_complete", { summary: "other-session result" })) + "\n",
    );

    const observe = await createTerminalObserver(inbox, correlationMarker);
    assert.equal(await observe(), null);
    await fs.appendFile(
      otherSessionEvents,
      JSON.stringify(event("user.message", { content: correlationMarker })) + "\n"
        + JSON.stringify(event("session.task_complete", { summary: "other-session result" })) + "\n",
    );
    assert.equal(await observe(), null);
    assert.equal(await fs.readFile(authorizedEvents, "utf8"), "");
  } finally {
    if (oldProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = oldProfile;
    await fs.rm(root, { recursive: true, force: true });
  }
});
