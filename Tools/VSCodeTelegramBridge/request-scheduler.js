"use strict";

const fs = require("node:fs/promises");
const path = require("node:path");
const { acquireDispatchLock, releaseDispatchLock, writeJsonAtomically } = require("./bridge-io");
const { createCompletionEnvelope } = require("./completion-contract");

const REQUEST_NAME = /^([A-Za-z0-9_-]{1,80})\.request\.json$/;
const TELEGRAM_TASK = /^telegram-(?:direct-)?\d+$/;
const REQUEST_MAX_BYTES = 65_536;
const UNCERTAIN_SUBMISSION_MAX_AGE_MS = 30 * 60_000;

async function readOptionalJson(filePath) {
  try {
    return JSON.parse(await fs.readFile(filePath, "utf8"));
  } catch (error) {
    if (error.code === "ENOENT") return undefined;
    throw error;
  }
}

async function assertInboxRecoverable(directories) {
  const entries = await Promise.all(directories.map(async (directory) => ({
    directory,
    names: await fs.readdir(directory),
  })));
  const pendingRequestIds = new Set(entries.flatMap(({ names }) => names
    .map((name) => REQUEST_NAME.exec(name)?.[1])
    .filter(Boolean)));

  for (const { directory, names } of entries) {
    const ids = new Set(names.map((name) =>
      /^([A-Za-z0-9_-]{1,80})\.(?:dispatch|ack)\.json$/.exec(name)?.[1]).filter(Boolean));
    for (const id of ids) {
      const intent = await readOptionalJson(path.join(directory, `${id}.dispatch.json`));
      const acknowledgement = await readOptionalJson(path.join(directory, `${id}.ack.json`));
      if (!intent && acknowledgement?.status === "submitted" && !pendingRequestIds.has(id)) continue;
      if (!intent && acknowledgement?.status === "failed"
        && typeof acknowledgement.error === "string" && acknowledgement.error.trim()
        && typeof acknowledgement.updatedUtc === "string"
        && Number.isFinite(Date.parse(acknowledgement.updatedUtc))) continue;
      const completion = await readOptionalJson(path.join(directory, `${id}.completion.json`));
      if (completion?.id === id && completion.status === "completed"
        && typeof completion.report === "string" && completion.report.trim()) {
        if (!intent) continue;
        if (typeof completion.updatedUtc !== "string") {
          throw new Error(`Copilot dispatch ${id} has no completion timestamp; dispatch is paused.`);
        }

        createCompletionEnvelope(completion.id, completion.taskId, completion.report, completion.updatedUtc);
        if (intent.id === id && intent.taskId === completion.taskId
          && typeof intent.createdUtc === "string"
          && Date.parse(completion.updatedUtc) >= Date.parse(intent.createdUtc)) continue;
      }
      throw new Error(`Copilot delivery is unresolved (${id}); dispatch is paused to prevent duplicate execution.`);
    }
  }
}

async function unlinkIfPresent(filePath) {
  try {
    await fs.unlink(filePath);
  } catch (error) {
    if (error.code !== "ENOENT") throw error;
  }
}

async function removeRequestCopies(directories, id) {
  for (const directory of directories) {
    await unlinkIfPresent(path.join(directory, `${id}.request.json`));
  }
}

function isUncertainStaleRecord(id, interrupted, acknowledgement) {
  return interrupted?.id === id
    && interrupted.status === "interrupted"
    && interrupted.errorCode === "uncertain_stale"
    && acknowledgement?.status === "failed"
    && acknowledgement.errorCode === "uncertain_stale"
    && typeof acknowledgement.error === "string"
    && acknowledgement.error.trim().length > 0
    && typeof acknowledgement.updatedUtc === "string"
    && Number.isFinite(Date.parse(acknowledgement.updatedUtc));
}

async function recoverStaleSubmissions(directories, now = Date.now()) {
  const uniqueDirectories = [...new Set(directories)];
  const entries = await Promise.all(uniqueDirectories.map(async (directory) => ({
    directory,
    names: await fs.readdir(directory),
  })));

  for (const { directory, names } of entries) {
    for (const name of names) {
      const match = /^([A-Za-z0-9_-]{1,80})\.interrupted\.json$/.exec(name);
      if (!match) continue;

      const id = match[1];
      const acknowledgementPath = path.join(directory, `${id}.ack.json`);
      const acknowledgement = await readOptionalJson(acknowledgementPath);
      if (acknowledgement?.status !== "failed"
        || acknowledgement.errorCode !== "uncertain_stale") continue;
      const interrupted = await readOptionalJson(path.join(directory, name));
      if (isUncertainStaleRecord(id, interrupted, acknowledgement)) {
        await unlinkIfPresent(path.join(directory, `${id}.dispatch.json`));
        await removeRequestCopies(uniqueDirectories, id);
      }
    }
  }

  for (const { directory, names } of entries) {
    for (const name of names) {
      const match = /^([A-Za-z0-9_-]{1,80})\.dispatch\.json$/.exec(name);
      if (!match) continue;

      const id = match[1];
      const dispatchPath = path.join(directory, name);
      const acknowledgementPath = path.join(directory, `${id}.ack.json`);
      const interruptedPath = path.join(directory, `${id}.interrupted.json`);
      const [dispatch, acknowledgement, interrupted] = await Promise.all([
        readOptionalJson(dispatchPath),
        readOptionalJson(acknowledgementPath),
        readOptionalJson(interruptedPath),
      ]);
      if (isUncertainStaleRecord(id, interrupted, acknowledgement)) {
        await unlinkIfPresent(dispatchPath);
        await removeRequestCopies(uniqueDirectories, id);
        continue;
      }

      if (dispatch?.id !== id
        || dispatch.status !== "dispatching"
        || (acknowledgement !== undefined && acknowledgement.status !== "submitted")
        || typeof dispatch.createdUtc !== "string") continue;

      const createdAt = Date.parse(dispatch.createdUtc);
      if (!Number.isFinite(createdAt) || now - createdAt < UNCERTAIN_SUBMISSION_MAX_AGE_MS) continue;

      const completion = await readOptionalJson(path.join(directory, `${id}.completion.json`));
      if (completion?.id === id && completion.status === "completed"
        && typeof completion.report === "string" && completion.report.trim()) {
        if (typeof completion.updatedUtc !== "string") {
          throw new Error(`Copilot dispatch ${id} has no completion timestamp; dispatch is paused.`);
        }

        createCompletionEnvelope(completion.id, completion.taskId, completion.report, completion.updatedUtc);
        if (dispatch.taskId === completion.taskId
          && Date.parse(completion.updatedUtc) >= createdAt) continue;
      }

      const errorCode = "uncertain_stale";
      const error = "Copilot isteğinin sonucu 30 dakika içinde doğrulanamadı. "
        + "Yinelenen çalıştırmayı önlemek için eski teslim yeniden gönderilmeden sonlandırıldı; yeni görevler devam ediyor.";
      const updatedUtc = new Date(now).toISOString();
      await writeJsonAtomically(interruptedPath, {
        ...dispatch,
        status: "interrupted",
        errorCode,
        error,
        updatedUtc,
      });
      await writeJsonAtomically(acknowledgementPath, {
        status: "failed",
        errorCode,
        error,
        updatedUtc,
      });
      await unlinkIfPresent(dispatchPath);
      await removeRequestCopies(uniqueDirectories, id);
    }
  }
}

async function acquireInboxLocks(directories) {
  const locks = [];
  try {
    for (const directory of [...new Set(directories)].sort()) {
      await fs.mkdir(directory, { recursive: true });
      const lock = await acquireDispatchLock(directory);
      if (!lock) {
        await releaseInboxLocks(locks);
        return null;
      }
      locks.push({ directory, lock });
    }
    return locks;
  } catch (error) {
    await releaseInboxLocks(locks);
    throw error;
  }
}

async function releaseInboxLocks(locks) {
  const results = await Promise.allSettled(
    locks.map(({ directory, lock }) => releaseDispatchLock(directory, lock)),
  );
  const errors = results.filter((result) => result.status === "rejected").map((result) => result.reason);
  if (errors.length > 0) throw new AggregateError(errors, "Inbox dispatch locks could not be released.");
}

async function nextInboxRequest(directories) {
  const candidates = [];
  for (const directory of directories) {
    const names = await fs.readdir(directory);
    for (const name of names) {
      const match = REQUEST_NAME.exec(name);
      if (!match) continue;
      const requestPath = path.join(directory, name);
      let stat;
      let request;
      try {
        stat = await fs.stat(requestPath);
        if (!stat.isFile()) continue;
        if (stat.size <= REQUEST_MAX_BYTES) {
          try {
            request = JSON.parse(await fs.readFile(requestPath, "utf8"));
          } catch (error) {
            if (!(error instanceof SyntaxError)) throw error;
          }
        }
      } catch (error) {
        if (error.code === "ENOENT") continue;
        throw error;
      }
      const id = match[1];
      const taskId = request?.taskId ?? request?.TaskId ?? id;
      const createdAt = Date.parse(request?.createdUtc ?? request?.CreatedUtc);
      candidates.push({
        directory,
        requestPath,
        priority: TELEGRAM_TASK.test(taskId) || /^telegram-direct-\d+$/.test(id) ? 0 : 1,
        createdAt: Number.isFinite(createdAt) ? createdAt : stat.mtimeMs,
      });
    }
  }
  candidates.sort((left, right) => left.priority - right.priority
    || left.createdAt - right.createdAt
    || left.requestPath.localeCompare(right.requestPath));
  return candidates[0];
}

module.exports = {
  acquireInboxLocks,
  releaseInboxLocks,
  nextInboxRequest,
  assertInboxRecoverable,
  recoverStaleSubmissions,
};
