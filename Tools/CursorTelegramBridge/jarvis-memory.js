"use strict";

// Jarvis long-term memory: one markdown file per fact under AgentWatcherData\jarvis-memory.
// Forgotten facts are moved to _unutulan instead of being deleted.

const fs = require("node:fs/promises");
const path = require("node:path");
const { maskPii } = require("./pii-mask");

const MAX_FACTS = 80;
const MAX_FACT_LENGTH = 400;
const FORGOTTEN_FOLDER = "_unutulan";

function memoryDirectory(dataRoot) {
  return path.join(dataRoot, "jarvis-memory");
}

function slug(text) {
  const ascii = text.toLocaleLowerCase("tr-TR")
    .replace(/ç/g, "c").replace(/ğ/g, "g").replace(/ı/g, "i").replace(/ö/g, "o").replace(/ş/g, "s").replace(/ü/g, "u")
    .replace(/[^a-z0-9]+/g, "-").replace(/^-+|-+$/g, "");
  return ascii.slice(0, 40) || "bilgi";
}

async function listFacts(dataRoot) {
  const directory = memoryDirectory(dataRoot);
  const names = await fs.readdir(directory).catch(() => []);
  const facts = [];
  for (const name of names.filter((entry) => entry.endsWith(".md")).sort()) {
    const text = (await fs.readFile(path.join(directory, name), "utf8").catch(() => "")).trim();
    if (text) facts.push({ key: name.slice(0, -3), text });
  }
  return facts;
}

async function rememberFact(dataRoot, fact) {
  const text = maskPii(String(fact ?? "").replace(/\s+/g, " ").trim());
  if (!text) throw new Error("Kaydedilecek bilgi boş.");
  if (text.length > MAX_FACT_LENGTH) throw new Error(`Bilgi en fazla ${MAX_FACT_LENGTH} karakter olabilir; kısaltın.`);
  const facts = await listFacts(dataRoot);
  if (facts.some((existing) => existing.text.toLocaleLowerCase("tr-TR") === text.toLocaleLowerCase("tr-TR"))) {
    return "Bu bilgi zaten bellekte.";
  }
  if (facts.length >= MAX_FACTS) throw new Error(`Bellek dolu (${MAX_FACTS} bilgi); önce eski bir bilgiyi unutun.`);
  const directory = memoryDirectory(dataRoot);
  await fs.mkdir(directory, { recursive: true });
  const stamp = new Date().toISOString().replace(/[-:T]/g, "").slice(0, 14);
  const key = `${stamp}-${slug(text)}`;
  await fs.writeFile(path.join(directory, `${key}.md`), `${text}\n`, "utf8");
  return `Kaydedildi (anahtar: ${key}).`;
}

async function forgetFact(dataRoot, key) {
  const name = String(key ?? "").trim();
  if (!/^[A-Za-z0-9-]+$/.test(name)) throw new Error("Anahtar geçersiz.");
  const directory = memoryDirectory(dataRoot);
  const source = path.join(directory, `${name}.md`);
  const targetDirectory = path.join(directory, FORGOTTEN_FOLDER);
  await fs.mkdir(targetDirectory, { recursive: true });
  let target = path.join(targetDirectory, `${name}.md`);
  try {
    await fs.access(target);
    target = path.join(targetDirectory, `${name}-${Date.now()}.md`);
  } catch {
    /* target free */
  }
  try {
    await fs.rename(source, target);
  } catch (error) {
    if (error.code === "ENOENT") throw new Error("Bu anahtarla bir bilgi yok.");
    throw error;
  }
  return "Unutuldu.";
}

async function buildMemoryContext(dataRoot) {
  const facts = await listFacts(dataRoot);
  if (facts.length === 0) return "";
  return facts.map((fact) => `- [${fact.key}] ${maskPii(fact.text)}`).join("\n");
}

function textResult(text, isError = false) {
  return { content: [{ type: "text", text }], isError };
}

function memoryTools(dataRoot) {
  const run = (action) => async (args) => {
    try {
      return textResult(await action(args ?? {}));
    } catch (error) {
      return textResult(error?.message ?? String(error), true);
    }
  };
  return {
    jarvis_hafiza_kaydet: {
      description: "Kullanıcı açıkça 'hatırla', 'not et' veya 'unutma' dediğinde kalıcı bir bilgiyi Jarvis belleğine kaydeder. "
        + "Tek cümlelik, kendi başına anlaşılır bir bilgi yaz. Şifre, API anahtarı veya kişisel kimlik numarası kaydetme.",
      inputSchema: {
        type: "object",
        properties: { bilgi: { type: "string", description: "Kaydedilecek bilgi (en fazla 400 karakter)" } },
        required: ["bilgi"],
      },
      annotations: { title: "Jarvis belleğine kaydet", readOnlyHint: false, destructiveHint: false, openWorldHint: false },
      execute: run((args) => rememberFact(dataRoot, args.bilgi)),
    },
    jarvis_hafiza_unut: {
      description: "Kullanıcı bir bilginin unutulmasını istediğinde, bellek listesindeki anahtarıyla o bilgiyi bellekten çıkarır.",
      inputSchema: {
        type: "object",
        properties: { anahtar: { type: "string", description: "Bellek listesinde köşeli parantez içindeki anahtar" } },
        required: ["anahtar"],
      },
      annotations: { title: "Jarvis belleğinden çıkar", readOnlyHint: false, destructiveHint: false, openWorldHint: false },
      execute: run((args) => forgetFact(dataRoot, args.anahtar)),
    },
  };
}

module.exports = { buildMemoryContext, forgetFact, listFacts, memoryDirectory, memoryTools, rememberFact };
