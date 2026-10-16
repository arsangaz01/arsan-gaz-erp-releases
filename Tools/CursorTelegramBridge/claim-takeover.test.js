"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const { claimTakeoverReason, IDE_CLAIM_START_GRACE_MS } = require("./bridge.js");

const now = Date.parse("2026-10-10T19:00:00Z");
const iso = (offsetMs) => new Date(now + offsetMs).toISOString();
const liveIde = { pid: 2580, updatedUtc: iso(-3000) };

test("fresh claim by the live IDE watcher stays with the IDE chat", () => {
  const claim = { pid: 2580, claimedUtc: iso(-30_000) };
  assert.equal(claimTakeoverReason({ claim, started: false, ideStatus: liveIde, nowMs: now }), null);
});

test("claim the IDE chat never started is taken over after the grace period", () => {
  const claim = { pid: 2580, claimedUtc: iso(-IDE_CLAIM_START_GRACE_MS) };
  assert.match(claimTakeoverReason({ claim, started: false, ideStatus: liveIde, nowMs: now }), /3 dakika/);
});

test("claim is taken over when the IDE watcher heartbeat is stale", () => {
  const claim = { pid: 2580, claimedUtc: iso(-10_000) };
  const staleIde = { pid: 2580, updatedUtc: iso(-120_000) };
  assert.match(claimTakeoverReason({ claim, started: false, ideStatus: staleIde, nowMs: now }), /çalışmıyor/);
});

test("claim made by a previous watcher process is taken over", () => {
  const claim = { pid: 1111, claimedUtc: iso(-10_000) };
  assert.match(claimTakeoverReason({ claim, started: false, ideStatus: liveIde, nowMs: now }), /kapanmış/);
});

test("request the IDE chat has started is never taken over", () => {
  const claim = { pid: 1111, claimedUtc: iso(-3_600_000) };
  assert.equal(claimTakeoverReason({ claim, started: true, ideStatus: null, nowMs: now }), null);
});

test("claim without pid is taken over immediately", () => {
  const claim = { claimedUtc: iso(-10_000) };
  assert.match(claimTakeoverReason({ claim, started: false, ideStatus: liveIde, nowMs: now }), /pid eksik/);
});
