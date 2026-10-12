"use strict";

// afterAgentResponse: mirror every final chat answer to the authorized Telegram chat via the AgentWatcher outbox.
const fs = require("node:fs");
const path = require("node:path");
const crypto = require("node:crypto");

const RECENT_SEND_MS = 120_000;
// Older "submitted" progress files are leftovers from interrupted runs, not a bridge task in flight.
const RUNNING_BRIDGE_TASK_MS = 60 * 60_000;
const dataRoot = path.join(process.env.LOCALAPPDATA ?? "", "ArsanGazERP", "AgentWatcherData");
const inbox = path.join(dataRoot, "CopilotInbox");

function readJson(file) {
  try {
    return JSON.parse(fs.readFileSync(file, "utf8").replace(/^\uFEFF/, ""));
  } catch {
    return null;
  }
}

function modifiedWithin(file, milliseconds) {
  try {
    return Date.now() - fs.statSync(file).mtimeMs < milliseconds;
  } catch {
    return false;
  }
}

// Telegram requests already reach Telegram as completion reports (IDE chat or bridge.js agent).
function isAlreadyReported() {
  let names = [];
  try {
    names = fs.readdirSync(inbox);
  } catch {
    return false;
  }
  for (const name of names) {
    if (name.endsWith(".completion.json") && modifiedWithin(path.join(inbox, name), RECENT_SEND_MS)) return true;
    if (name.endsWith(".progress.json") && modifiedWithin(path.join(inbox, name), RUNNING_BRIDGE_TASK_MS)) {
      const progress = readJson(path.join(inbox, name));
      const id = name.slice(0, -".progress.json".length);
      if (progress?.stage === "submitted" && !names.includes(`${id}.completion.json`)) return true;
    }
  }
  return false;
}

// On Windows the hook stdin arrives as UTF-8 that was decoded with the ANSI code page and re-encoded
// ("ü" -> "Ã¼"). This machine uses Windows-1254 (Turkish); 1252 is kept as a fallback.
const MOJIBAKE_CODE_PAGES = ["windows-1254", "windows-1252"];
const UTF8_LEAD = /[\u00c2-\u00f4\u011e\u011f\u0130\u0131\u015e\u015f]/;

function reverseCodePage(label) {
  const decoded = new TextDecoder(label).decode(Uint8Array.from({ length: 256 }, (_, index) => index));
  return new Map([...decoded].map((character, index) => [character.codePointAt(0), index]));
}

function tryReverse(text, byteOf) {
  const bytes = [];
  for (const character of text) {
    const value = byteOf.get(character.codePointAt(0));
    if (value === undefined) return null;
    bytes.push(value);
  }
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(Uint8Array.from(bytes));
  } catch {
    return null;
  }
}

// Undo only when every character maps back to one byte and the bytes form valid UTF-8 that differs from the input;
// correct Turkish text never satisfies both, so it is left untouched.
function repairMojibake(text) {
  if (!UTF8_LEAD.test(text)) return { text, repaired: false };
  for (const label of MOJIBAKE_CODE_PAGES) {
    const repaired = tryReverse(text, reverseCodePage(label));
    if (repaired !== null && repaired !== text) return { text: repaired, repaired: true, codePage: label };
  }
  return { text, repaired: false };
}

function log(message) {
  try {
    fs.mkdirSync(path.join(dataRoot, "Logs"), { recursive: true });
    fs.appendFileSync(path.join(dataRoot, "Logs", "cursor-chat-hook.log"), `${new Date().toISOString()} ${message}\n`, "utf8");
  } catch {
    // Logging must not block the hook.
  }
}

function main(raw) {
  // The server Cursor agent (Linux) has no AgentWatcher data folder; its answers reach Telegram through the queue.
  if (!process.env.LOCALAPPDATA) return;
  const input = JSON.parse(raw.replace(/^\uFEFF/, "").trim() || "{}");
  const decoded = repairMojibake(String(input.text ?? input.response ?? input.message ?? "").trim());
  const text = decoded.text;
  const skipped = !text ? "empty" : isAlreadyReported() ? "already-reported" : "";
  log(`fired length=${text.length} repaired=${decoded.repaired}${decoded.codePage ? " codePage=" + decoded.codePage : ""}`
    + (skipped ? " skipped=" + skipped : ""));
  if (skipped) return;

  const settings = readJson(path.join(dataRoot, "settings.json"));
  const chatId = Number(settings?.TelegramChatId);
  if (!Number.isSafeInteger(chatId) || chatId === 0) return;

  const outbox = path.join(dataRoot, "TelegramOutbox");
  fs.mkdirSync(outbox, { recursive: true });
  const id = "cursor-chat-" + crypto.randomUUID().replaceAll("-", "");
  const now = new Date().toISOString();
  const item = {
    Id: id,
    Kind: "telegram-copilot-direct-response",
    TaskId: id,
    ChatId: chatId,
    Report: "💻 Cursor sohbeti:\n\n" + text,
    CreatedUtc: now,
    NextAttemptUtc: now,
    Attempts: 0,
    DeliveredChunkCount: 0,
    Receipts: [],
    LastError: null,
  };
  const destination = path.join(outbox, id + ".json");
  fs.writeFileSync(destination + ".tmp", JSON.stringify(item, null, 2), "utf8");
  fs.renameSync(destination + ".tmp", destination);
}

if (require.main !== module) {
  module.exports = { repairMojibake };
} else {
  let raw = "";
  process.stdin.setEncoding("utf8");
  process.stdin.on("data", (chunk) => { raw += chunk; });
  process.stdin.on("end", () => {
    try {
      main(raw);
    } catch (error) {
      process.stderr.write(`telegram-final-response: ${error.message}\n`);
    }
    process.stdout.write("{}\n");
  });
}
