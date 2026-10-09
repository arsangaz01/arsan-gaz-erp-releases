"use strict";

const vscode = require("vscode");
const fs = require("node:fs/promises");
const fsSync = require("node:fs");
const path = require("node:path");
const { writeJsonAtomically } = require("./bridge-io");
const {
  acquireInboxLocks,
  releaseInboxLocks,
  nextInboxRequest,
  assertInboxRecoverable,
  recoverStaleSubmissions,
} = require("./request-scheduler");
const { isAuthorizedSessionIdle } = require("./session-readiness");
const { waitForClipboardText } = require("./response-capture");
const { buildCopilotPrompt } = require("./prompt-builder");
const { createCompletionEnvelope } = require("./completion-contract");
const { createTerminalObserver } = require("./terminal-observer");

const CHAT_COMMAND = "workbench.action.chat.open";
const FOCUS_CHAT_INPUT_COMMAND = "workbench.action.chat.focusInput";
const FOCUS_LATEST_RESPONSE_COMMAND = "chat.action.focus";
const COPY_FINAL_RESPONSE_COMMAND = "workbench.action.chat.copyFinalResponse";
const POLL_INTERVAL_MS = 1500;
const HEARTBEAT_INTERVAL_MS = 5000;
const REQUEST_MAX_LENGTH = 4096;
const REQUEST_MAX_BYTES = 65_536;
const INTERACTION_POLL_MS = 500;
const FINAL_RESPONSE_POLL_MS = 1500;
const BRIDGE_PROTOCOL_VERSION = 2;
const BACKGROUND_ERROR_REPEAT_INTERVAL_MS = 60_000;
const BACKGROUND_ERROR_CACHE_LIMIT = 128;

let pollTimer;
let heartbeatTimer;
let extensionVersion = "unknown";
let processing = false;
let dispatchBlocked = true;
let statusBarItem;
let output;
let stopping = false;
const backgroundErrorTimes = new Map();

function ensureActive() {
  if (stopping) throw new Error("Telegram köprüsü kapatıldı; bekleme iptal edildi.");
}

function rejectUnsupportedActiveWorkAddition(text) {
  if (/^\/ek(?:@[A-Za-z0-9_]+)?(?:\s|$)/i.test(text)) {
    throw new Error("Telegram /ek etkin çalışma eklemesi VS Code Copilot Chat'te güvenli biçimde desteklenmiyor; ayrı bir istek olarak gönderilmedi.");
  }
}

async function validateAttachments(attachments, directory) {
  if (!Array.isArray(attachments) || attachments.length > 10) {
    throw new Error("Telegram ek listesi geçersiz.");
  }
  if (attachments.length === 0) return [];
  const configuredRoot = path.resolve(path.dirname(directory), "telegram-attachments");
  const root = await fs.realpath(configuredRoot);
  if (root.toLowerCase() !== configuredRoot.toLowerCase()) throw new Error("Telegram ek klasoru baska bir konuma yonlendiriliyor.");
  const validated = [];
  for (const attachment of attachments) {
    if (!attachment || typeof attachment !== "object" || Array.isArray(attachment)) {
      throw new Error("Telegram ek kaydi gecersiz.");
    }
    const localPath = attachment.localPath ?? attachment.LocalPath;
    if (typeof localPath !== "string" || !path.isAbsolute(localPath)) {
      throw new Error("Telegram ek yolu geçersiz.");
    }
    const actual = await fs.realpath(localPath);
    const relative = path.relative(root, actual);
    if (relative.startsWith("..") || path.isAbsolute(relative)
      || !/\.(jpg|jpeg|png|webp|pdf|txt|csv|docx|xlsx)$/i.test(actual)) {
      throw new Error("Telegram eki izinli dosya klasöründe veya desteklenen türde değil.");
    }
    const stat = await fs.stat(actual);
    if (!stat.isFile() || stat.size === 0 || stat.size > 20 * 1024 * 1024) {
      throw new Error("Telegram ek boyutu geçersiz.");
    }
    const declaredSize = attachment.size ?? attachment.Size;
    if (!Number.isSafeInteger(declaredSize) || declaredSize !== stat.size) {
      throw new Error("Telegram ek boyutu kayıtla eşleşmiyor.");
    }
    validated.push({ localPath: actual });
  }
  return validated;
}

function reportBackgroundError(operation, error, now = Date.now()) {
  const detail = error?.message ?? String(error);
  const key = `${operation}\u0000${detail}`;
  const previous = backgroundErrorTimes.get(key);
  if (previous !== undefined && now >= previous
    && now - previous < BACKGROUND_ERROR_REPEAT_INTERVAL_MS) return false;

  backgroundErrorTimes.delete(key);
  backgroundErrorTimes.set(key, now);
  if (backgroundErrorTimes.size > BACKGROUND_ERROR_CACHE_LIMIT) {
    backgroundErrorTimes.delete(backgroundErrorTimes.keys().next().value);
  }
  output.appendLine(`${operation} başarısız: ${detail}`);
  return true;
}

function findProjectRoot() {
  for (const folder of vscode.workspace.workspaceFolders ?? []) {
    const projectRoot = locateProjectRoot(folder.uri.fsPath, 2);
    if (projectRoot) {
      return projectRoot;
    }
  }

  return null;
}

function existsSync(filePath) {
  try {
    fsSync.accessSync(filePath);
    return true;
  } catch {
    return false;
  }
}

function locateProjectRoot(workspacePath, maxDepth) {
  const pending = [{ directory: workspacePath, depth: 0 }];
  const visited = new Set();
  while (pending.length > 0) {
    const current = pending.shift();
    if (!current || visited.has(current.directory)) {
      continue;
    }

    visited.add(current.directory);
    if (
      existsSync(path.join(current.directory, "ArsanGazERP.csproj")) ||
      existsSync(path.join(current.directory, "ArsanGazERP.sln"))
    ) {
      return current.directory;
    }

    if (current.depth >= maxDepth) {
      continue;
    }

    let entries = [];
    try {
      entries = fsSync.readdirSync(current.directory, { withFileTypes: true });
    } catch {
      continue;
    }

    for (const entry of entries) {
      if (entry.isDirectory()) {
        pending.push({
          directory: path.join(current.directory, entry.name),
          depth: current.depth + 1,
        });
      }
    }
  }

  return null;
}

function inboxDirectories(projectRoot) {
  const roots = [];
  const localAppData = process.env.LOCALAPPDATA;
  if (localAppData) {
    roots.push(path.join(localAppData, "ArsanGazERP", "AgentWatcherData", "CopilotInbox"));
  }
  if (projectRoot) {
    roots.push(path.join(projectRoot, "Tools", "AgentWatcherData", "CopilotInbox"));
  }

  return [...new Set(roots.map((value) => path.resolve(value)))];
}

async function isVsCodeBridgeSelected(projectRoot) {
  for (const inbox of inboxDirectories(projectRoot)) {
    try {
      const raw = await fs.readFile(path.join(path.dirname(inbox), "settings.json"), "utf8");
      const selected = JSON.parse(raw.replace(/^\uFEFF/, "")).CodingBridge;
      return String(selected ?? "Cursor").trim().toLowerCase() === "vs code";
    } catch (error) {
      if (error.code !== "ENOENT") return false;
    }
  }
  return false;
}

async function writeHeartbeat() {
  const projectRoot = findProjectRoot();
  if (projectRoot && !(await isVsCodeBridgeSelected(projectRoot))) {
    statusBarItem.text = "$(debug-disconnect) Telegram: Cursor seçili";
    return;
  }
  const commandIds = await vscode.commands.getCommands(false);
  const hasChatCommand = commandIds.includes(CHAT_COMMAND);
  const ready = Boolean(projectRoot && hasChatCommand);
  const reason = !projectRoot
    ? "VS Code'da ArsanGazERP.csproj içeren proje klasörü açık değil."
    : !hasChatCommand
      ? "VS Code Chat komutu kullanılamıyor; VS Code/Copilot Chat'i etkinleştirin."
      : null;
  const status = {
    ready,
    ...bridgeHeartbeatMetadata(extensionVersion, dispatchBlocked),
    updatedUtc: new Date().toISOString(),
    reason,
    vscodeProcessId: Number(process.env.VSCODE_PID) || process.ppid,
  };

  for (const directory of projectRoot ? inboxDirectories(projectRoot) : []) {
    try {
      await fs.mkdir(directory, { recursive: true });
      await writeJsonAtomically(path.join(directory, "bridge-status.json"), status);
    } catch (error) {
      output.appendLine(`Köprü durum dosyası yazılamadı (${error.code ?? error.name}).`);
    }
  }

  statusBarItem.text = !ready
    ? "$(warning) Telegram → VS Code Chat bekliyor"
    : dispatchBlocked
      ? "$(warning) Telegram → VS Code Chat duraklatıldı"
      : "$(radio-tower) Telegram → VS Code Chat hazır";
  statusBarItem.tooltip = reason
    ?? (dispatchBlocked
      ? "Belirsiz önceki teslim güvenle sonlandırılana kadar yeni istek gönderilmez."
      : "İzinli Telegram istekleri açık VS Code Chat'e yönlendirilir.");
}

function bridgeHeartbeatMetadata(version = extensionVersion, isDispatchBlocked = false) {
  return {
    extensionVersion: version,
    protocolVersion: BRIDGE_PROTOCOL_VERSION,
    dispatchBlocked: isDispatchBlocked,
  };
}

async function openChatView() {
  if (!findProjectRoot()) {
    return;
  }

  try {
    await vscode.commands.executeCommand(CHAT_COMMAND);
  } catch (error) {
    reportBackgroundError("Copilot Chat görünümü açılamadı", error);
  }
}

async function writeAcknowledgement(directory, id, status, error) {
  const acknowledgement = {
    status,
    error: error ?? null,
    updatedUtc: new Date().toISOString(),
  };
  await writeJsonAtomically(path.join(directory, `${id}.ack.json`), acknowledgement);
}

async function copyFinalResponse() {
  const report = await copyCurrentFinalResponse();
  if (!report) {
    ensureActive();
    throw new Error("Copilot yanıtı beklenirken pano metni alınamadı.");
  }
  return report;
}

async function copyCurrentFinalResponse() {
  const commands = await vscode.commands.getCommands(false);
  if (![FOCUS_CHAT_INPUT_COMMAND, FOCUS_LATEST_RESPONSE_COMMAND, COPY_FINAL_RESPONSE_COMMAND]
    .every((command) => commands.includes(command))) {
    throw new Error("VS Code bu sürümde son Copilot yanıtını seçme ve dışa aktarma komutlarını sunmuyor.");
  }

  const previousClipboard = await vscode.env.clipboard.readText();
  const marker = `ArsanGazERP bridge clipboard marker ${Date.now()} ${Math.random()}`;
  await vscode.env.clipboard.writeText(marker);
  let copiedText = null;
  try {
    // Copy uses the focused response, not the newest response automatically.
    await vscode.commands.executeCommand(FOCUS_CHAT_INPUT_COMMAND);
    await vscode.commands.executeCommand(FOCUS_LATEST_RESPONSE_COMMAND);
    await vscode.commands.executeCommand(COPY_FINAL_RESPONSE_COMMAND);
    copiedText = await waitForClipboardText(
      () => vscode.env.clipboard.readText(),
      { marker, shouldContinue: () => !stopping },
    );
    return copiedText;
  } finally {
    const currentClipboard = await vscode.env.clipboard.readText();
    if (currentClipboard === marker || (copiedText !== null && currentClipboard === copiedText)) {
      await vscode.env.clipboard.writeText(previousClipboard);
    }
  }
}

async function waitForFinalResponseAfterInteraction(observeTerminal) {
  while (true) {
    ensureActive();
    const report = await observeTerminal();
    if (report !== null) return report;
    await new Promise((resolve) => setTimeout(resolve, FINAL_RESPONSE_POLL_MS));
  }

}

async function writeProgress(directory, id, stage) {
  await writeJsonAtomically(path.join(directory, `${id}.progress.json`), {
    id,
    stage,
    updatedUtc: new Date().toISOString(),
  });
}

async function submitCopilotRequest(prompt, attachments = []) {
  const models = await vscode.lm.selectChatModels({ vendor: "copilot", family: "gpt-6-luna" });
  if (models.length === 0 || models.some(model => /claude|anthropic/i.test(`${model.id} ${model.family} ${model.vendor}`))) {
    throw new Error("Doğrulanmış Claude dışı gpt-6-luna modeli bulunamadı; otomatik model geçişi yapılmadı.");
  }
  while (true) {
    ensureActive();
    try {
      return await vscode.commands.executeCommand(CHAT_COMMAND, {
        query: prompt,
        mode: "agent",
        isPartialQuery: false,
        blockOnResponse: true,
        modelSelector: { id: models[0].id },
        attachFiles: attachments.map(attachment => vscode.Uri.file(attachment.localPath ?? attachment.LocalPath)),
      });
    } catch (error) {
      if (
        !String(error.message ?? error).includes("Timed out waiting for default agent")
      ) {
        throw error;
      }

      await new Promise((resolve) => setTimeout(resolve, 5000));
    }
  }

}

function getPendingInteraction(result, id) {
  if (!result || result.type !== "confirmation" || typeof result.kind !== "string") {
    return null;
  }

  const interaction = {
    id,
    createdUtc: new Date().toISOString(),
    kind: result.kind,
    vscodeProcessId: Number(process.env.VSCODE_PID) || process.ppid,
  };
  if (typeof result.toolId === "string") {
    interaction.toolId = result.toolId;
  }
  if (typeof result.title === "string") {
    interaction.title = result.title;
  }
  if (result.data !== undefined) {
    interaction.data = result.data;
  }
  if (Array.isArray(result.questions)) {
    interaction.questions = result.questions;
  }

  const sessionResource = result.sessionResource ?? result.context?.sessionResource;
  if (sessionResource && typeof sessionResource === "object") {
    interaction.sessionResource = sessionResource;
  }
  return interaction;
}

async function waitForInteractionResponse(directory, id, observeTerminal) {
  const responsePath = path.join(directory, `${id}.response.json`);
  while (true) {
    ensureActive();
    const report = await observeTerminal();
    if (report !== null) return { terminalReport: report };
    try {
      const raw = await fs.readFile(responsePath, "utf8");
      if (Buffer.byteLength(raw, "utf8") > 32_768) {
        throw new Error("Telegram yanıtı köprü boyut sınırını aşıyor.");
      }

      const response = JSON.parse(raw);
      if (response.id !== id || typeof response.action !== "string") {
        throw new Error("Telegram etkileşim yanıtı geçersiz.");
      }
      return response;
    } catch (error) {
      if (error.code !== "ENOENT") {
        throw error;
      }
    }

    await new Promise((resolve) => setTimeout(resolve, INTERACTION_POLL_MS));
  }

}

async function applyInteractionResponse(directory, interaction, response) {
  const action = response.action;
  if (
    (interaction.kind === "toolInvocation" || interaction.kind === "toolPostApproval") &&
    (!interaction.sessionResource || typeof interaction.sessionResource !== "object")
  ) {
    throw new Error("Bekleyen Copilot onayı hedef sohbet oturumuna bağlanamadı; onayı VS Code'da verin.");
  }
  const sessionContext = interaction.sessionResource
    ? { sessionResource: interaction.sessionResource }
    : undefined;

  if (interaction.kind === "toolInvocation") {
    if (action !== "approve" && action !== "reject") {
      throw new Error("Araç onayı için geçersiz Telegram yanıtı.");
    }
    const commands = await vscode.commands.getCommands(false);
    const command = action === "approve" ? "workbench.action.chat.acceptTool" : "workbench.action.chat.skipTool";
    if (!commands.includes(command)) {
      throw new Error(`VS Code araç onayı komutu kullanılamıyor: ${command}`);
    }
    await vscode.commands.executeCommand(
      command,
      sessionContext,
    );
  } else if (interaction.kind === "toolPostApproval") {
    if (action !== "approve" && action !== "reject") {
      throw new Error("Araç sonrası onay için geçersiz Telegram yanıtı.");
    }
    const commands = await vscode.commands.getCommands(false);
    const command = action === "approve"
      ? "workbench.action.chat.acceptToolPostExecution"
      : "workbench.action.chat.skipToolPostExecution";
    if (!commands.includes(command)) {
      throw new Error(`VS Code araç sonrası onay komutu kullanılamıyor: ${command}`);
    }
    await vscode.commands.executeCommand(
      command,
      sessionContext,
    );
  } else if (interaction.kind === "questionCarousel") {
    if (action !== "answered" || response.applied !== true) {
      throw new Error("Soru kartı yanıtı VS Code arayüzüne uygulanmadı.");
    }
  } else if (interaction.kind === "elicitation") {
    throw new Error("Elicitation formu güvenli biçimde eşleştirilip yanıtlanamadı; VS Code'da tamamlayın.");
  } else {
    throw new Error(`Desteklenmeyen Copilot onay türü: ${interaction.kind}`);
  }

  await writeJsonAtomically(path.join(directory, `${interaction.id}.interaction-result.json`), {
    id: interaction.id,
    status: "responded",
    action,
    updatedUtc: new Date().toISOString(),
  });
}

async function processRequest(directory, requestPath) {
  const match = /^([A-Za-z0-9_-]{1,80})\.request\.json$/.exec(path.basename(requestPath));
  if (!match) {
    return;
  }

  const id = match[1];
  const acknowledgementPath = path.join(directory, `${id}.ack.json`);
  if (existsSync(acknowledgementPath)) {
    await assertInboxRecoverable([directory]);
    await fs.unlink(requestPath);
    return;
  }

  let terminalRecorded = false;
  let submissionAttempted = false;
  try {
    const raw = await fs.readFile(requestPath, "utf8");
    if (Buffer.byteLength(raw, "utf8") > REQUEST_MAX_BYTES) {
      throw new Error("Telegram isteği köprü boyut sınırını aşıyor.");
    }
    const request = JSON.parse(raw);
    const requestId = request.id ?? request.Id;
    const taskId = request.taskId ?? request.TaskId;
    const text = request.text ?? request.Text;
    const attachments = request.attachments ?? request.Attachments ?? [];
    const createdUtc = request.createdUtc ?? request.CreatedUtc;
    const createdAt = Date.parse(createdUtc);

    if (
      requestId !== id ||
      (taskId !== undefined && taskId !== null && !/^[A-Za-z0-9_-]{1,80}$/.test(taskId)) ||
      typeof text !== "string" ||
      text.trim().length === 0 ||
      text.length > REQUEST_MAX_LENGTH
    ) {
      throw new Error("Telegram isteği geçersiz veya çok uzun.");
    }
    rejectUnsupportedActiveWorkAddition(text);
    if (!Number.isFinite(createdAt)) {
      throw new Error("Telegram isteğinin oluşturulma zamanı geçersiz.");
    }
    const validatedAttachments = await validateAttachments(attachments, directory);

    const projectRoot = findProjectRoot();
    if (!projectRoot) {
      throw new Error("VS Code'da ArsanGazERP.csproj içeren proje klasörü açık değil.");
    }

    const { prompt, correlationMarker } = buildCopilotPrompt({
      id,
      taskId: taskId ?? id,
      text,
    });
    const observeTerminal = await createTerminalObserver(directory, correlationMarker);
    await writeJsonAtomically(path.join(directory, `${id}.dispatch.json`), {
      id, taskId: taskId ?? id, status: "dispatching", createdUtc: new Date().toISOString(),
    });
    output.appendLine(`Telegram isteği Copilot Chat'e gönderiliyor: ${id}`);
    submissionAttempted = true;
    const result = await submitCopilotRequest(prompt, validatedAttachments);
    await writeAcknowledgement(directory, id, "submitted");
    await writeProgress(directory, id, "submitted");
    output.appendLine(`Telegram isteği Copilot Chat'e gönderildi: ${id}`);
    const interaction = getPendingInteraction(result, id);
    if (!interaction) {
      const report = await waitForFinalResponseAfterInteraction(observeTerminal);
      await writeJsonAtomically(path.join(directory, `${id}.completion.json`), {
        ...createCompletionEnvelope(id, taskId ?? id, report),
      });
      terminalRecorded = true;
      await writeProgress(directory, id, "completed");
      output.appendLine(`Copilot yanıtı Gözcü raporu için alındı: ${id}`);
      return;
    }

    await writeJsonAtomically(path.join(directory, `${id}.interaction.json`), interaction);
    await writeProgress(directory, id, "waitingInteraction");
    output.appendLine(`Copilot etkileşimi Telegram yanıtını bekliyor: ${id} (${interaction.kind})`);
    const response = await waitForInteractionResponse(directory, id, observeTerminal);
    let report = response.terminalReport;
    if (report === undefined) {
      await applyInteractionResponse(directory, interaction, response);
      output.appendLine(`Telegram Copilot etkileşimi VS Code'a uygulandı: ${id} (${response.action})`);
      await writeProgress(directory, id, "resuming");
      report = await waitForFinalResponseAfterInteraction(observeTerminal);
    }
    await writeJsonAtomically(path.join(directory, `${id}.completion.json`), {
      ...createCompletionEnvelope(id, taskId ?? id, report),
    });
    terminalRecorded = true;
    await writeProgress(directory, id, "completed");
    output.appendLine(`Copilot etkileşim sonrası yanıtı Gözcü raporu için alındı: ${id}`);
  } catch (error) {
    if (!submissionAttempted && !terminalRecorded) {
      try {
        await writeAcknowledgement(directory, id, "failed", error.message ?? String(error));
        terminalRecorded = true;
      } catch (writeError) {
        output.appendLine(`Teslim hatası onayı yazılamadı (${writeError.code ?? writeError.name}).`);
      }
    }
    if (submissionAttempted && !terminalRecorded) {
      output.appendLine(`Copilot teslim sonucu belirsiz (${id}); istek korunuyor ve sonraki gönderimler durduruluyor.`);
    }
    output.appendLine(`Telegram isteği gönderilemedi (${id}): ${error.message ?? error}`);
  } finally {
    if (terminalRecorded) {
      await fs.unlink(requestPath).catch((error) => {
        if (error.code !== "ENOENT") throw error;
      });
    }
  }
}

async function pollInbox() {
  const projectRoot = findProjectRoot();
  if (processing || !projectRoot || !(await isVsCodeBridgeSelected(projectRoot))) {
    return;
  }

  processing = true;
  let locks;
  try {
    const directories = inboxDirectories(projectRoot);
    locks = await acquireInboxLocks(directories);
    if (!locks) return;
    dispatchBlocked = true;
    try {
      await recoverStaleSubmissions(directories);
      await assertInboxRecoverable(directories);
      dispatchBlocked = false;
    } catch (error) {
      dispatchBlocked = true;
      throw error;
    }
    const request = await nextInboxRequest(directories);
    if (request && await isAuthorizedSessionIdle(request.directory)) {
      dispatchBlocked = true;
      try {
        await processRequest(request.directory, request.requestPath);
        await recoverStaleSubmissions(directories);
        await assertInboxRecoverable(directories);
        dispatchBlocked = false;
      } catch (error) {
        dispatchBlocked = true;
        throw error;
      }
    }
  } finally {
    try {
      if (locks) await releaseInboxLocks(locks);
    } finally {
      processing = false;
    }
  }
}

function scheduleHeartbeat() {
  void writeHeartbeat().catch((error) => reportBackgroundError("Köprü heartbeat yazımı", error));
}

function schedulePoll() {
  void pollInbox().catch((error) => reportBackgroundError("Köprü gelen kutusu taraması", error));
}

function activate(context) {
  stopping = false;
  extensionVersion = context.extension.packageJSON.version;
  output = vscode.window.createOutputChannel("Arsan Gaz Telegram Copilot Bridge");
  statusBarItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 20);
  statusBarItem.command = "arsangaz.telegramCopilotBridge.status";
  statusBarItem.show();

  const statusCommand = vscode.commands.registerCommand(
    "arsangaz.telegramCopilotBridge.status",
    async () => {
      await writeHeartbeat();
      const root = findProjectRoot();
      vscode.window.showInformationMessage(
        root
          ? "Telegram → VS Code Chat köprüsü etkin. /emir ve düz metin istekleri açık Chat görünümüne gönderilir."
          : "Telegram → VS Code Chat bekliyor. ArsanGazERP.csproj içeren klasörü VS Code'da açın.",
      );
    },
  );

  context.subscriptions.push(output, statusBarItem, statusCommand);
  context.subscriptions.push(
    vscode.workspace.onDidChangeWorkspaceFolders(() => {
      scheduleHeartbeat();
      schedulePoll();
      void openChatView();
    }),
  );
  output.appendLine("Arsan Gaz Telegram Copilot Bridge etkinleştirildi.");
  void openChatView();
  scheduleHeartbeat();
  schedulePoll();
  heartbeatTimer = setInterval(scheduleHeartbeat, HEARTBEAT_INTERVAL_MS);
  pollTimer = setInterval(schedulePoll, POLL_INTERVAL_MS);
}

function deactivate() {
  stopping = true;
  clearInterval(pollTimer);
  clearInterval(heartbeatTimer);
}

module.exports = {
  activate,
  deactivate,
  rejectUnsupportedActiveWorkAddition,
  createCompletionEnvelope,
  bridgeHeartbeatMetadata,
};
