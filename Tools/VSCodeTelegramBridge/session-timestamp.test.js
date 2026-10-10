"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const { parseCopilotTimestamp } = require("./session-timestamp");

test("parses ISO timestamps and Turkish day-first timestamps as UTC", () => {
  assert.equal(
    parseCopilotTimestamp("2026-10-09T15:13:47.000Z"),
    Date.parse("2026-10-09T15:13:47.000Z"),
  );
  assert.equal(
    parseCopilotTimestamp("9.10.2026 15:13:47"),
    Date.parse("2026-10-09T15:13:47.000Z"),
  );
  assert.equal(
    parseCopilotTimestamp("09.10.2026, 15:13:47.125"),
    Date.parse("2026-10-09T15:13:47.125Z"),
  );
});

test("rejects invalid localized timestamps instead of normalizing calendar overflow", () => {
  assert.ok(Number.isNaN(parseCopilotTimestamp("32.10.2026 15:13:47")));
  assert.ok(Number.isNaN(parseCopilotTimestamp("9.13.2026 15:13:47")));
  assert.ok(Number.isNaN(parseCopilotTimestamp("not a timestamp")));
  assert.ok(Number.isNaN(parseCopilotTimestamp(undefined)));
});
