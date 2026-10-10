"use strict";

const fs = require("node:fs/promises");
const path = require("node:path");
const { parseCopilotTimestamp } = require("./session-timestamp");

const READ_BYTES = 65_536;
const TURN_END_SETTLE_MS = 3_000;
const observers = new Map();

function updateReadiness(state, event, notBefore) {
  const timestamp = parseCopilotTimestamp(event.timestamp);
  if (!Number.isFinite(timestamp)) throw new Error("Copilot session event timestamp is invalid.");
  if (timestamp < notBefore || event.agentId != null) return;
  if (state.terminalAt !== null && event.type !== "session.task_complete") {
    state.idle = false;
    state.terminalAt = null;
    state.finalAnswer = false;
  }
  if (event.type === "session.task_complete") {
    state.idle = true;
    state.terminalAt = null;
    state.finalAnswer = false;
  } else if (event.type === "assistant.turn_start") {
    state.idle = false;
    state.terminalAt = null;
    state.finalAnswer = false;
  } else if (event.type === "assistant.turn_end") {
    if (state.finalAnswer) {
      state.idle = true;
      state.terminalAt = timestamp;
      state.finalAnswer = false;
    }
  } else if ([
    "user.message",
    "assistant.message",
    "session.error",
    "tool.execution_start",
    "tool.execution_complete",
  ].includes(event.type)) {
    state.idle = false;
    state.terminalAt = null;
    const toolRequests = event.data?.toolRequests;
    state.finalAnswer = event.type === "assistant.message"
      && (event.data?.phase === "final_answer" || event.data?.phase == null)
      && (toolRequests == null || (Array.isArray(toolRequests) && toolRequests.length === 0));
  }
}

async function isAuthorizedSessionIdle(directory) {
  const authorizationPath = path.join(path.dirname(directory), "copilot-session-report.json");
  const authorization = JSON.parse(await fs.readFile(authorizationPath, "utf8"));
  const notBefore = parseCopilotTimestamp(authorization.notBeforeUtc);
  if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(authorization.sessionId)
    || !Number.isFinite(notBefore) || !process.env.USERPROFILE) {
    throw new Error("Copilot session authorization is invalid; dispatch is paused.");
  }
  const eventsPath = path.join(process.env.USERPROFILE, ".copilot", "session-state",
    authorization.sessionId, "events.jsonl");
  const key = `${eventsPath}:${notBefore}`;
  let observer = observers.get(authorizationPath);
  const handle = await fs.open(eventsPath, "r");
  try {
    const stat = await handle.stat();
    if (!observer || observer.key !== key) {
      const offset = Math.max(0, stat.size - READ_BYTES);
      observer = {
        key, offset, ino: stat.ino, partial: Buffer.alloc(0), idle: false,
        terminalAt: null, finalAnswer: false, discardFirstLine: offset > 0,
      };
      observers.set(authorizationPath, observer);
    }
    if (stat.ino !== observer.ino || stat.size < observer.offset) {
      observers.delete(authorizationPath);
      throw new Error("Copilot session log changed; dispatch is paused.");
    }
    const buffer = Buffer.alloc(READ_BYTES);
    const { bytesRead } = await handle.read(buffer, 0, buffer.length, observer.offset);
    observer.offset += bytesRead;
    const content = Buffer.concat([observer.partial, buffer.subarray(0, bytesRead)]);
    let start = 0;
    try {
      for (let end = content.indexOf(10); end !== -1; end = content.indexOf(10, start)) {
        const line = content.subarray(start, end).toString("utf8").trim();
        start = end + 1;
        if (observer.discardFirstLine) {
          observer.discardFirstLine = false;
          continue;
        }
        if (line) updateReadiness(observer, JSON.parse(line), notBefore);
      }
      observer.partial = content.subarray(start);
      if (observer.partial.length > 4 * 1024 * 1024) {
        throw new Error("Copilot session event exceeds the size limit; dispatch is paused.");
      }
    } catch (error) {
      observers.delete(authorizationPath);
      throw error;
    }
    // An unfinished event or unread backlog may contain a new user turn.
    const latest = await handle.stat();
    const settled = observer.terminalAt === null
      || Date.now() - observer.terminalAt >= TURN_END_SETTLE_MS;
    return observer.idle && settled && observer.partial.length === 0 && observer.offset === latest.size;
  } finally {
    await handle.close();
  }
}

module.exports = { isAuthorizedSessionIdle, updateReadiness };
