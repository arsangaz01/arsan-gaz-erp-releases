"use strict";

const { randomBytes } = require("node:crypto");

function buildCopilotPrompt(request) {
  if (typeof request.text !== "string" || !request.text.trim()) {
    throw new Error("Telegram istegi metni bos veya gecersiz.");
  }
  const correlationMarker = `<!-- arsan-gaz-telegram-correlation:${randomBytes(24).toString("hex")} -->`;
  return {
    prompt: `${request.text}\n\n${correlationMarker}`,
    correlationMarker,
  };
}

module.exports = { buildCopilotPrompt };
