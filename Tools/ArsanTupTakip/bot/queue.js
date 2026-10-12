"use strict";

// File queue between arsantuptakip-bot (user tuptakipbot) and arsantuptakip-agent (user tuptakipagent). Both users are in
// the group tuptakipq; the queue root is setgid 2770 so every file stays group readable/writable (services run UMask 0007).
//   <root>/requests/<id>/request.json   written last by the bot: the request is complete
//   <root>/requests/<id>/attachments/   Telegram files downloaded by the bot
//   <root>/requests/<id>/claim          created exclusively by the agent when it starts the request
//   <root>/requests/<id>/progress.json  agent progress (tool steps)
//   <root>/requests/<id>/result.json    agent final report { status, report }
//   <root>/requests/<id>/cancel         bot: user typed /iptal
//   <root>/requests/<id>/delivered      bot: final report sent to Telegram
//   <root>/control/new-conversation     bot: /yeni (timestamp); agent starts a fresh Cursor conversation
//   <root>/agent-status.json            agent status for /durum (no secrets)
//   <root>/outgoing/agent-branch.bundle agent commits for arsantuptakip-release (pending/publish)
//   <root>/notices/<name>.txt           one-off message (server scripts, admin); the bot sends it verbatim and deletes it

const fs = require("node:fs/promises");
const path = require("node:path");

const ID_PATTERN = /^[a-z0-9][a-z0-9-]{0,80}$/;
const NOTICE_PATTERN = /^[a-z0-9][a-z0-9-]{0,80}\.txt$/;
const NOTICE_MAX_CHARS = 16_000;
const KEEP_DELIVERED_MS = 7 * 24 * 60 * 60_000;

function assertId(id) {
  if (!ID_PATTERN.test(String(id))) throw new Error(`Geçersiz istek kimliği: ${id}`);
  return id;
}

const requestsDir = (root) => path.join(root, "requests");
const requestDir = (root, id) => path.join(requestsDir(root), assertId(id));

async function exists(file) {
  return fs.access(file).then(() => true, () => false);
}

async function readJson(file) {
  try {
    return JSON.parse((await fs.readFile(file, "utf8")).replace(/^\uFEFF/, ""));
  } catch {
    return null;
  }
}

async function writeJsonAtomically(file, value, mode = 0o660) {
  const temporary = `${file}.${process.pid}.${Date.now()}.tmp`;
  await fs.writeFile(temporary, JSON.stringify(value, null, 2), { encoding: "utf8", mode });
  await fs.chmod(temporary, mode);
  await fs.rename(temporary, file);
}

async function ensureLayout(root) {
  for (const directory of [requestsDir(root), path.join(root, "control"), path.join(root, "outgoing"), path.join(root, "notices")]) {
    await fs.mkdir(directory, { recursive: true, mode: 0o2770 });
  }
}

// Creates the request folder; the caller downloads attachments into attachmentsDir and then calls commitRequest.
async function beginRequest(root, id) {
  const directory = requestDir(root, id);
  await fs.mkdir(path.join(directory, "attachments"), { recursive: true, mode: 0o2770 });
  return { directory, attachmentsDir: path.join(directory, "attachments") };
}

async function commitRequest(root, id, request) {
  await writeJsonAtomically(path.join(requestDir(root, id), "request.json"), { ...request, id, createdUtc: request.createdUtc ?? new Date().toISOString() });
}

async function listIds(root) {
  const entries = await fs.readdir(requestsDir(root), { withFileTypes: true }).catch(() => []);
  return entries.filter((entry) => entry.isDirectory() && ID_PATTERN.test(entry.name)).map((entry) => entry.name).sort();
}

async function requestState(root, id) {
  const directory = requestDir(root, id);
  const [hasRequest, claimed, hasResult, delivered, cancelled] = await Promise.all(
    ["request.json", "claim", "result.json", "delivered", "cancel"].map((name) => exists(path.join(directory, name))));
  return { id, hasRequest, claimed, hasResult, delivered, cancelled };
}

// Requests waiting for the agent, oldest first.
async function listPending(root) {
  const result = [];
  for (const id of await listIds(root)) {
    const state = await requestState(root, id);
    if (state.hasRequest && !state.claimed && !state.hasResult) result.push(id);
  }
  return result;
}

// Exclusive claim: only one agent process can start a request.
async function claim(root, id, owner = "agent") {
  try {
    await fs.writeFile(path.join(requestDir(root, id), "claim"), JSON.stringify({ owner, pid: process.pid, utc: new Date().toISOString() }), { flag: "wx", mode: 0o660 });
    return true;
  } catch (error) {
    if (error.code === "EEXIST") return false;
    throw error;
  }
}

const readRequest = (root, id) => readJson(path.join(requestDir(root, id), "request.json"));
const readProgress = (root, id) => readJson(path.join(requestDir(root, id), "progress.json"));
const readResult = (root, id) => readJson(path.join(requestDir(root, id), "result.json"));
const writeProgress = (root, id, progress) => writeJsonAtomically(path.join(requestDir(root, id), "progress.json"), { ...progress, updatedUtc: new Date().toISOString() });
const writeResult = (root, id, result) => writeJsonAtomically(path.join(requestDir(root, id), "result.json"), { ...result, id, updatedUtc: new Date().toISOString() });

async function cancel(root, id) {
  await fs.writeFile(path.join(requestDir(root, id), "cancel"), new Date().toISOString(), { mode: 0o660 });
}

const isCancelled = (root, id) => exists(path.join(requestDir(root, id), "cancel"));

async function markDelivered(root, id) {
  await fs.writeFile(path.join(requestDir(root, id), "delivered"), new Date().toISOString(), { mode: 0o660 });
}

// Requests claimed by the agent that have no result yet (running, or interrupted by a restart).
async function listActive(root) {
  const result = [];
  for (const id of await listIds(root)) {
    const state = await requestState(root, id);
    if (state.claimed && !state.hasResult) result.push(id);
  }
  return result;
}

// Results the bot has not sent yet.
async function listUndelivered(root) {
  const result = [];
  for (const id of await listIds(root)) {
    const state = await requestState(root, id);
    if (state.hasResult && !state.delivered) result.push(id);
  }
  return result;
}

async function cleanup(root, now = Date.now(), keepMs = KEEP_DELIVERED_MS) {
  let removed = 0;
  for (const id of await listIds(root)) {
    const marker = path.join(requestDir(root, id), "delivered");
    const stat = await fs.stat(marker).catch(() => null);
    if (stat && now - stat.mtimeMs > keepMs) {
      await fs.rm(requestDir(root, id), { recursive: true, force: true });
      removed += 1;
    }
  }
  return removed;
}

async function requestNewConversation(root, now = new Date()) {
  await fs.writeFile(path.join(root, "control", "new-conversation"), now.toISOString(), { mode: 0o660 });
}

async function readNewConversationMarker(root) {
  return (await fs.readFile(path.join(root, "control", "new-conversation"), "utf8").catch(() => "")).trim();
}

const noticeFile = (root, name) => {
  if (!NOTICE_PATTERN.test(String(name))) throw new Error(`Geçersiz bildirim adı: ${name}`);
  return path.join(root, "notices", name);
};

// Written under a temporary name first, so the bot never sends half a file.
async function writeNotice(root, name, text) {
  const file = noticeFile(root, name);
  const temporary = path.join(root, "notices", `.${name}.${process.pid}.tmp`);
  await fs.writeFile(temporary, String(text), { encoding: "utf8", mode: 0o660 });
  await fs.rename(temporary, file);
}

async function listNotices(root) {
  const names = await fs.readdir(path.join(root, "notices")).catch(() => []);
  return names.filter((name) => NOTICE_PATTERN.test(name)).sort();
}

async function readNotice(root, name) {
  const text = await fs.readFile(noticeFile(root, name), "utf8").catch(() => "");
  return text.replace(/^\uFEFF/, "").trim().slice(0, NOTICE_MAX_CHARS);
}

async function removeNotice(root, name) {
  await fs.rm(noticeFile(root, name), { force: true });
}

module.exports = {
  writeNotice, listNotices, readNotice, removeNotice,
  ID_PATTERN, assertId, ensureLayout, beginRequest, commitRequest, listIds, listPending, listActive, listUndelivered, requestState,
  claim, readRequest, readProgress, readResult, writeProgress, writeResult, cancel, isCancelled, markDelivered, cleanup,
  requestNewConversation, readNewConversationMarker, readJson, writeJsonAtomically, requestDir,
};
