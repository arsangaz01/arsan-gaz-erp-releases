"use strict";

// Exposes the read-only ERP MCP server (Tools/ErpMcpServer) to the Cursor agent as SDK custom tools.
// MCP server tools fail closed on autoReview runs; custom tools run in this process, so the agent can still use them.

const { spawn } = require("node:child_process");
const fsSync = require("node:fs");
const path = require("node:path");
const { maskPii } = require("./pii-mask");

const CALL_TIMEOUT_MS = 30_000;
const PROTOCOL_VERSION = "2025-03-26";

function serverPath(projectRoot) {
  return path.join(projectRoot, "Tools", "ErpMcpServer", "bin", "Release", "net10.0", "ArsanGazERP.ErpMcpServer.exe");
}

class ErpMcpClient {
  constructor(command, log = () => undefined) {
    this.command = command;
    this.log = log;
    this.child = null;
    this.ready = null;
    this.nextId = 1;
    this.pending = new Map();
    this.buffer = "";
  }

  start() {
    if (this.ready) return this.ready;
    const child = spawn(this.command, [], { stdio: ["pipe", "pipe", "pipe"], windowsHide: true });
    this.child = child;
    child.stdout.setEncoding("utf8");
    child.stdout.on("data", (chunk) => this.onData(chunk));
    child.stderr.on("data", () => undefined);
    const fail = (error) => {
      if (this.child !== child) return;
      this.child = null;
      this.ready = null;
      for (const { reject, timer } of this.pending.values()) {
        clearTimeout(timer);
        reject(error);
      }
      this.pending.clear();
    };
    child.stdin.on("error", (error) => fail(error));
    child.on("error", (error) => fail(error));
    child.on("exit", (code) => fail(new Error(`ERP MCP sunucusu kapandı (kod ${code}).`)));
    this.ready = (async () => {
      await this.request("initialize", {
        protocolVersion: PROTOCOL_VERSION,
        capabilities: {},
        clientInfo: { name: "arsangaz-cursor-bridge", version: "1.0.0" },
      });
      this.notify("notifications/initialized");
    })();
    this.ready.catch(() => fail(new Error("ERP MCP sunucusu başlatılamadı.")));
    return this.ready;
  }

  onData(chunk) {
    this.buffer += chunk;
    let newline;
    while ((newline = this.buffer.indexOf("\n")) >= 0) {
      const line = this.buffer.slice(0, newline).trim();
      this.buffer = this.buffer.slice(newline + 1);
      if (!line) continue;
      let message;
      try {
        message = JSON.parse(line);
      } catch {
        continue;
      }
      const waiter = this.pending.get(message.id);
      if (!waiter) continue;
      this.pending.delete(message.id);
      clearTimeout(waiter.timer);
      if (message.error) waiter.reject(new Error(message.error.message ?? "MCP hatası"));
      else waiter.resolve(message.result);
    }
  }

  request(method, params) {
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error(`ERP MCP isteği zaman aşımına uğradı (${method}).`));
      }, CALL_TIMEOUT_MS);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", id, method, params })}\n`);
    });
  }

  notify(method, params) {
    this.child?.stdin.write(`${JSON.stringify({ jsonrpc: "2.0", method, ...(params ? { params } : {}) })}\n`);
  }

  async listTools() {
    await this.start();
    return (await this.request("tools/list", {})).tools ?? [];
  }

  async callTool(name, args) {
    await this.start();
    return this.request("tools/call", { name, arguments: args ?? {} });
  }

  stop() {
    const child = this.child;
    this.child = null;
    this.ready = null;
    child?.kill();
  }
}

function maskResult(result) {
  const content = (result?.content ?? []).map((block) =>
    block.type === "text" ? { type: "text", text: maskPii(block.text) } : block);
  return { content, isError: result?.isError === true };
}

// Returns SDK custom tools, or {} when the server is missing or unusable (the agent then works without ERP tools).
async function loadErpTools(projectRoot, log = () => undefined) {
  const command = serverPath(projectRoot);
  if (!fsSync.existsSync(command)) {
    log(`ERP MCP sunucusu bulunamadı: ${command}`);
    return { tools: {}, client: null };
  }
  const client = new ErpMcpClient(command, log);
  let descriptors;
  try {
    descriptors = await client.listTools();
  } catch (error) {
    log(`ERP MCP araçları yüklenemedi: ${error?.message ?? error}`);
    client.stop();
    return { tools: {}, client: null };
  }
  const tools = {};
  for (const descriptor of descriptors) {
    tools[descriptor.name] = {
      description: `${descriptor.description ?? ""} (Arsan Gaz ERP, salt okunur; kişisel veriler maskelidir.)`.trim(),
      inputSchema: descriptor.inputSchema ?? { type: "object", properties: {} },
      annotations: { title: descriptor.title ?? descriptor.name, readOnlyHint: true, destructiveHint: false, openWorldHint: false },
      execute: async (args) => {
        try {
          return maskResult(await client.callTool(descriptor.name, args));
        } catch (error) {
          return { content: [{ type: "text", text: `ERP aracı çalışmadı: ${error?.message ?? error}` }], isError: true };
        }
      },
    };
  }
  return { tools, client };
}

module.exports = { ErpMcpClient, loadErpTools, maskResult, serverPath };
