"use strict";

// Definition-of-done check for the server Cursor agent. Pure functions over the request text and the git diff of
// the run, so a missing PostgreSQL table, an un-bumped mobile version or a requested-but-untouched component is
// appended to the Telegram reply instead of being discovered after the release.

const COMPONENTS = [
  { key: "mobil", label: "Mobil uygulama", ask: /\b(mobil|telefon|apk|android)/iu, paths: /^Tools\/MobileApp\// },
  { key: "web", label: "Web paneli/sitesi", ask: /\b(web|panel|site)/iu, paths: /^Tools\/MobileApi\/(WebPanel\/|WebPanelEndpoints\.cs$)/ },
  { key: "masaustu", label: "ERP masaüstü", ask: /(masaüstü|masaustu|desktop|erp ekran|bilgisayardaki erp)/iu, paths: /^(Views|Modules|Repositories)\/|^[^/]+\.xaml(\.cs)?$/ },
  { key: "watcher", label: "AgentWatcher", ask: /(watcher|gözcü|gozcu)/iu, paths: /^Tools\/AgentWatcher\// },
  { key: "sunucu", label: "Sunucu (MobileApi/ServerAgent)", ask: /\b(sunucu|server|api)/iu, paths: /^Tools\/(MobileApi|ServerAgent)\// },
];

const POSTGRES_SCHEMA = "Data/PostgresSchema.cs";
const MOBILE_CSPROJ = "Tools/MobileApp/ArsanGazMobile.csproj";
const SCHEMA_CHANGE = /DbSet<|CREATE TABLE|ADD COLUMN|\.ToTable\(/i;

const isDoc = (file) => /\.md$/i.test(file) || file.startsWith("docs/");

function requestedComponents(text) {
  return COMPONENTS.filter((component) => component.ask.test(String(text ?? ""))).map((component) => component.key);
}

function changedComponents(files) {
  return COMPONENTS.filter((component) => files.some((file) => !isDoc(file) && component.paths.test(file))).map((component) => component.key);
}

// Added lines of a unified diff, grouped by the file they belong to.
function addedLines(diff) {
  const result = new Map();
  let current = null;
  for (const line of String(diff ?? "").split(/\r?\n/)) {
    const header = /^\+\+\+ (?:b\/)?(.+)$/.exec(line);
    if (header) {
      current = header[1] === "/dev/null" ? null : header[1];
      continue;
    }
    if (current && line.startsWith("+") && !line.startsWith("+++")) {
      if (!result.has(current)) result.set(current, []);
      result.get(current).push(line.slice(1));
    }
  }
  return result;
}

function doneCheck({ requestText, files, diff }) {
  const changed = (files ?? []).filter(Boolean);
  if (changed.length === 0) return { warnings: [], checklist: [] };
  const added = addedLines(diff);
  const warnings = [];

  const schemaFiles = [...added.entries()]
    .filter(([file, lines]) => file !== POSTGRES_SCHEMA && file.endsWith(".cs") && lines.some((line) => SCHEMA_CHANGE.test(line)))
    .map(([file]) => file);
  if (schemaFiles.length > 0 && !changed.includes(POSTGRES_SCHEMA)) {
    warnings.push(`Yeni tablo/kolon eklenmiş (${schemaFiles.join(", ")}) ama ${POSTGRES_SCHEMA} değişmemiş: prod PostgreSQL'de tablo oluşmaz.`);
  }

  if (changed.some((file) => file.startsWith("Tools/MobileApp/") && !isDoc(file))
      && !(added.get(MOBILE_CSPROJ) ?? []).some((line) => /<ApplicationVersion>/.test(line))) {
    warnings.push(`Mobil uygulama değişmiş ama ${MOBILE_CSPROJ} içinde ApplicationVersion artırılmamış: telefonlar güncellemeyi almaz.`);
  }

  const requested = requestedComponents(requestText);
  const touched = changedComponents(changed);
  const checklist = COMPONENTS
    .filter((component) => requested.includes(component.key) || touched.includes(component.key))
    .map((component) => {
      if (touched.includes(component.key)) return `✅ ${component.label}: dosyaları değişti`;
      return `❌ ${component.label}: istekte geçiyor ama bu işte dosyası değişmedi`;
    });
  for (const component of COMPONENTS) {
    if (requested.includes(component.key) && !touched.includes(component.key)) {
      warnings.push(`İstekte "${component.label}" geçiyor ama bu işte o bileşenin dosyaları değişmedi; yapılmadıysa yanıtta açıkça belirtilmeli.`);
    }
  }
  return { warnings, checklist };
}

function formatDoneCheck(result) {
  if (result.warnings.length === 0 && result.checklist.length === 0) return "";
  const lines = ["", "", "🔎 Otomatik kontrol (git farkına göre):", ...result.checklist];
  if (result.warnings.length > 0) lines.push(...result.warnings.map((warning) => `⚠️ ${warning}`));
  return lines.join("\n");
}

module.exports = { doneCheck, formatDoneCheck, requestedComponents, changedComponents, addedLines };
