"use strict";

const assert = require("node:assert/strict");
const test = require("node:test");
const { describeToolCall } = require("./bridge.js");

test("file reads show only the file name", () => {
  assert.equal(describeToolCall("read", { path: "C:\\ArsanGazERP\\Tools\\MobileApp\\ArsanGazMobile.csproj" }),
    "📄 Okuyor: ArsanGazMobile.csproj");
});

test("glob searches show the pattern", () => {
  assert.equal(describeToolCall("glob", { globPattern: "**/*.csproj" }), "🔎 Arıyor: **/*.csproj");
});

test("shell commands are shortened", () => {
  const line = describeToolCall("shell", { command: "dotnet build ".repeat(20) });
  assert.match(line, /^🖥 Komut çalıştırıyor: dotnet build/);
  assert.ok(line.length < 100);
});

test("unknown tools fall back to their name", () => {
  assert.equal(describeToolCall("mcp_custom", {}), "⚙ mcp_custom");
});
