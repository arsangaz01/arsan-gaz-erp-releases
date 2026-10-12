"use strict";

// Client for /opt/arsangaz-cursoragent/bin/cursor-inbox.sh (Tools/ServerAgent/deploy/cursor-inbox.sh), the only way
// the server Cursor agent (user cursoragent) touches the production queue. The decisions are pure functions so they
// can be tested without the server.

const { execFile } = require("node:child_process");

// ServerCommands.IsPcOnline and inbox-sync.sh use the same 90 s window; the PC sync runs every 15 s.
const PC_OFFLINE_AFTER_SEC = 90;
const DEFAULT_WAIT_MINUTES = 10;

function parseList(output) {
  const result = { pcAgeSec: -1, requests: [], mine: [] };
  for (const line of String(output).split(/\r?\n/)) {
    let match;
    if ((match = /^PC (-?\d+)$/.exec(line))) result.pcAgeSec = Number(match[1]);
    else if ((match = /^REQ ([A-Za-z0-9_-]{1,80}) (\d+)$/.exec(line))) result.requests.push({ id: match[1], ageSec: Number(match[2]) });
    else if ((match = /^MINE ([A-Za-z0-9_-]{1,80})$/.exec(line))) result.mine.push(match[1]);
  }
  return result;
}

function isPcOnline(pcAgeSec) {
  return pcAgeSec >= 0 && pcAgeSec < PC_OFFLINE_AFTER_SEC;
}

// Returns why the server should take the request, or null to leave it to the PC.
function serverTakeReason({ pcAgeSec, ageSec, waitMinutes = DEFAULT_WAIT_MINUTES }) {
  if (!isPcOnline(pcAgeSec)) return pcAgeSec < 0 ? "bilgisayar hiç eşitleme yapmadı" : "bilgisayar kapalı";
  if (waitMinutes > 0 && ageSec >= waitMinutes * 60) return `istek ${waitMinutes} dakikadır sahipsiz bekliyor`;
  return null;
}

// Oldest first, phone/Jarvis requests are not prioritised: the server handles one request at a time.
function pickClaimable(list, waitMinutes = DEFAULT_WAIT_MINUTES) {
  return list.requests
    .map((request) => ({ ...request, reason: serverTakeReason({ pcAgeSec: list.pcAgeSec, ageSec: request.ageSec, waitMinutes }) }))
    .filter((request) => request.reason)
    .sort((left, right) => right.ageSec - left.ageSec);
}

function createInboxClient({ helper, useSudo = true, run = defaultRun }) {
  const call = (args, input) => run(useSudo ? "sudo" : helper, useSudo ? ["-n", "-u", "arsangaz", helper, ...args] : args, input)
    .catch((error) => { throw new Error(`cursor-inbox ${args[0]} başarısız: ${error.message}`); });
  const b64 = (text) => Buffer.from(text, "utf8").toString("base64");
  return {
    async list() { return parseList(await call(["list"])); },
    async claim(id) {
      const output = (await call(["claim", id])).trim();
      if (!["CLAIMED", "TAKEN", "GONE"].includes(output)) throw new Error(`Beklenmeyen sahiplenme yanıtı: ${output}`);
      return output;
    },
    async release(id) { return (await call(["release", id])).trim(); },
    async getRequest(id) { return Buffer.from((await call(["get", id])).trim(), "base64").toString("utf8"); },
    async getFile(id, serverPath) { return Buffer.from((await call(["file", id, b64(serverPath)])).trim(), "base64"); },
    async isCancelled(id) { return (await call(["cancelled", id])).trim() === "1"; },
    async put(id, kind, value) { await call(["put", id, kind], b64(JSON.stringify(value, null, 2))); },
  };
}

function defaultRun(command, args, input) {
  return new Promise((resolve, reject) => {
    const child = execFile(command, args, { timeout: 60_000, maxBuffer: 64 * 1024 * 1024 }, (error, stdout, stderr) => {
      if (error) {
        reject(new Error(String(stderr || error.message).trim().split("\n").pop()));
        return;
      }
      resolve(stdout);
    });
    if (input !== undefined) child.stdin.end(input);
  });
}

module.exports = { createInboxClient, isPcOnline, parseList, pickClaimable, serverTakeReason, DEFAULT_WAIT_MINUTES, PC_OFFLINE_AFTER_SEC };
