"use strict";

const fs = require("node:fs/promises");
const path = require("node:path");
const { parseCopilotTimestamp } = require("./session-timestamp");

const TURN_END_SETTLE_MS = 3_000;

function consumeEvent(state, event) {
  if (event.agentId !== undefined && event.agentId !== null) return null;
  if (state.turnEndedAt !== null && event.type !== "session.task_complete") {
    state.final = null;
    state.turnEndedAt = null;
  }
  const data = event.data ?? {};
  if (event.type === "user.message") {
    state.final = null;
    state.turnEndedAt = null;
    if (data.source === "autopilot" || data.isAutopilotContinuation || data.delivery === "steering") {
      return null;
    }
    if (typeof data.content === "string" && data.content.includes(state.correlationMarker)) state.matched = true;
    else if (state.matched) throw new Error("Telegram istegi bitmeden baska bir Copilot oturum istegi basladi; sonuc sahipligi belirsiz.");
  }
  if (!state.matched) return null;
  if (event.type === "assistant.turn_start") {
    state.final = null;
    state.turnEndedAt = null;
  }
  if (event.type === "assistant.message") {
    state.final = (data.phase === "final_answer" || data.phase == null)
      && !data.toolRequests?.length
      && typeof data.content === "string"
      && data.content.trim()
      ? data.content
      : null;
    state.turnEndedAt = null;
  }
  if (event.type === "session.task_complete") {
    const report = data.summary || state.final;
    if (typeof report !== "string" || !report.trim()) throw new Error("Copilot tamamlandi ancak nihai rapor bulunamadi.");
    return report;
  }
  if (event.type === "session.error") throw new Error("Copilot oturum hatasi; VS Code sohbetini kontrol edin.");
  if (event.type === "tool.execution_start" || event.type === "tool.execution_complete") {
    state.final = null;
    state.turnEndedAt = null;
  }
  if (event.type === "assistant.turn_end" && state.final) {
    state.turnEndedAt = parseCopilotTimestamp(event.timestamp);
  }
  return null;
}

async function createTerminalObserver(directory, correlationMarker) {
  if (typeof correlationMarker !== "string"
    || !/^<!-- arsan-gaz-telegram-correlation:[0-9a-f]{48} -->$/.test(correlationMarker)) {
    throw new Error("Telegram istegi korelasyon belirteci gecersiz.");
  }
  const authorizationPath = path.join(path.dirname(directory), "copilot-session-report.json");
  const authorization = JSON.parse(await fs.readFile(authorizationPath, "utf8"));
  if (!/^[0-9a-f]{8}(?:-[0-9a-f]{4}){3}-[0-9a-f]{12}$/i.test(authorization.sessionId)
    || !Number.isFinite(parseCopilotTimestamp(authorization.notBeforeUtc))
    || !process.env.USERPROFILE) throw new Error("Copilot oturum izleme yetkisi gecersiz.");
  const eventsPath = path.join(process.env.USERPROFILE, ".copilot", "session-state", authorization.sessionId, "events.jsonl");
  const initial = await fs.stat(eventsPath);
  const notBefore = parseCopilotTimestamp(authorization.notBeforeUtc);
  let discardBaselineTail = false;
  if (initial.size > 0) {
    const handle = await fs.open(eventsPath, "r");
    try {
      const last = Buffer.alloc(1);
      await handle.read(last, 0, 1, initial.size - 1);
      discardBaselineTail = last[0] !== 10;
    } finally {
      await handle.close();
    }
  }
  let offset = initial.size;
  let partial = Buffer.alloc(0);
  const state = { correlationMarker, matched: false, final: null, turnEndedAt: null };
  return async () => {
    const currentAuthorization = JSON.parse(await fs.readFile(authorizationPath, "utf8"));
    if (currentAuthorization.sessionId !== authorization.sessionId
      || parseCopilotTimestamp(currentAuthorization.notBeforeUtc) !== notBefore) {
      throw new Error("Copilot oturum izleme yetkisi degisti; sonuc sahipligi belirsiz.");
    }
    const handle = await fs.open(eventsPath, "r");
    try {
      const current = await handle.stat();
      if (current.size < offset || current.ino !== initial.ino) throw new Error("Copilot oturum kaydi degisti; sonuc sahipligi belirsiz.");
      const buffer = Buffer.alloc(65_536);
      const { bytesRead } = await handle.read(buffer, 0, buffer.length, offset);
      offset += bytesRead;
      const content = Buffer.concat([partial, buffer.subarray(0, bytesRead)]);
      let start = 0;
      for (let end = content.indexOf(10); end !== -1; end = content.indexOf(10, start)) {
        const line = content.subarray(start, end).toString("utf8").trim();
        start = end + 1;
        if (discardBaselineTail) {
          discardBaselineTail = false;
          continue;
        }
        if (!line) continue;
        const event = JSON.parse(line);
        const timestamp = parseCopilotTimestamp(event.timestamp);
        if (!Number.isFinite(timestamp)) throw new Error("Copilot oturum olayi zaman bilgisi gecersiz.");
        if (timestamp < notBefore) continue;
        const report = consumeEvent(state, event);
        if (report !== null) return report;
      }
      partial = content.subarray(start);
      if (partial.length > 4 * 1024 * 1024) throw new Error("Copilot oturum olayi boyut sinirini asti.");
      const latest = await handle.stat();
      if (state.final
        && state.turnEndedAt !== null
        && Date.now() - state.turnEndedAt >= TURN_END_SETTLE_MS
        && partial.length === 0
        && offset === latest.size) {
        return state.final;
      }
      return null;
    } finally {
      await handle.close();
    }
  };
}

module.exports = { consumeEvent, createTerminalObserver };
