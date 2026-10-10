"use strict";

const DEFAULT_POLL_INTERVAL_MS = 100;
const DEFAULT_STABLE_MS = 300;

async function waitForClipboardText(readText, {
  marker,
  pollIntervalMs = DEFAULT_POLL_INTERVAL_MS,
  stableMs = DEFAULT_STABLE_MS,
  delay = (milliseconds) => new Promise((resolve) => setTimeout(resolve, milliseconds)),
  now = () => Date.now(),
  shouldContinue = () => true,
}) {
  let candidate = null;
  let candidateSince = 0;

  while (shouldContinue()) {
    const current = await readText();
    if (!shouldContinue()) return null;

    if (!current.trim() || current === marker) {
      candidate = null;
      candidateSince = 0;
    } else if (current !== candidate) {
      candidate = current;
      candidateSince = now();
    } else if (now() - candidateSince >= stableMs) {
      return candidate;
    }

    await delay(pollIntervalMs);
  }

  return null;
}

module.exports = { waitForClipboardText };
