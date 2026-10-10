"use strict";

const fs = require("node:fs/promises");
const path = require("node:path");
const { randomUUID } = require("node:crypto");

const LOCK_MAX_AGE_MS = 90 * 60_000;

async function writeJsonAtomically(filePath, value) {
  const temporaryPath = `${filePath}.${process.pid}.${Date.now()}.${Math.random().toString(16).slice(2)}.tmp`;
  try {
    await fs.writeFile(temporaryPath, JSON.stringify(value), { encoding: "utf8" });
    await fs.rename(temporaryPath, filePath);
  } finally {
    await fs.unlink(temporaryPath).catch((error) => {
      if (error.code !== "ENOENT") {
        throw error;
      }
    });
  }
}

function isProcessAlive(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
}

async function acquireDispatchLock(directory) {
  const lockPath = path.join(directory, "bridge-dispatch.lock");

  for (let attempt = 0; attempt < 2; attempt += 1) {
    let lock;
    try {
      lock = await fs.open(lockPath, "wx");
    } catch (error) {
      if (error.code !== "EEXIST") {
        throw error;
      }
    }

    if (lock) {
      const ownerToken = randomUUID();
      try {
        await lock.writeFile(JSON.stringify({
          pid: process.pid,
          ownerToken,
          createdUtc: new Date().toISOString(),
        }));
        lock.ownerToken = ownerToken;
        return lock;
      } catch (error) {
        await lock.close().catch(() => undefined);
        await fs.unlink(lockPath).catch(() => undefined);
        throw error;
      }
    }

    try {
      const [details, contents] = await Promise.all([
        fs.stat(lockPath),
        fs.readFile(lockPath, "utf8"),
      ]);
      const ageMs = Date.now() - details.mtimeMs;
      let ownerPid = null;
      try {
        const owner = JSON.parse(contents);
        if (Number.isInteger(owner.pid) && owner.pid > 0) {
          ownerPid = owner.pid;
        }
      } catch {
        // A fresh lock may be observed between exclusive creation and owner write.
      }

      if (ownerPid !== null && isProcessAlive(ownerPid)) {
        return null;
      }
      if (ownerPid === null && ageMs <= LOCK_MAX_AGE_MS) {
        return null;
      }

      await fs.unlink(lockPath);
    } catch (statError) {
      if (statError.code === "ENOENT") {
        continue;
      }
      throw statError;
    }
  }

  return null;
}

async function releaseDispatchLock(directory, lock) {
  const lockPath = path.join(directory, "bridge-dispatch.lock");
  try {
    const contents = await fs.readFile(lockPath, "utf8");
    if (JSON.parse(contents).ownerToken === lock.ownerToken) {
      await fs.unlink(lockPath);
    }
  } catch (error) {
    if (error.code !== "ENOENT" && !(error instanceof SyntaxError)) {
      throw error;
    }
  } finally {
    await lock.close();
  }
}

module.exports = {
  acquireDispatchLock,
  isProcessAlive,
  releaseDispatchLock,
  writeJsonAtomically,
};
