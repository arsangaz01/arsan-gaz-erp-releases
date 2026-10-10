"use strict";

function createCompletionEnvelope(id, taskId, report, updatedUtc = new Date().toISOString()) {
  if (typeof id !== "string" || !/^[A-Za-z0-9_-]{1,80}$/.test(id)) {
    throw new TypeError("Completion request ID is invalid.");
  }
  if (typeof taskId !== "string" || !/^[A-Za-z0-9_-]{1,80}$/.test(taskId)) {
    throw new TypeError("Completion task ID is invalid.");
  }
  if (typeof report !== "string" || report.trim().length === 0) {
    throw new TypeError("Completion report is empty.");
  }
  if (typeof updatedUtc !== "string" || !Number.isFinite(Date.parse(updatedUtc))) {
    throw new TypeError("Completion timestamp is invalid.");
  }

  return {
    id,
    taskId,
    status: "completed",
    report,
    updatedUtc,
  };
}

module.exports = { createCompletionEnvelope };
