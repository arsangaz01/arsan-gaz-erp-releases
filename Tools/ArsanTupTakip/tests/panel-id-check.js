// Every $("id") used by panel.js must exist in panel.html (or be created by the script itself).
const fs = require("fs");
const dir = __dirname + "/../src/ArsanTupTakip.Api/WebPanel/";
const js = fs.readFileSync(dir + "panel.js", "utf8");
const html = fs.readFileSync(dir + "panel.html", "utf8");
const ids = new Set([...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]));
const created = new Set([...js.matchAll(/\bid:\s*["'`]([^"'`]+)["'`]/g)].map((m) => m[1]));
const used = new Set([...js.matchAll(/\$\(\s*["'`]([^"'`$]+)["'`]\s*\)/g)].map((m) => m[1]));
const missing = [...used].filter((id) => !ids.has(id) && !created.has(id)).sort();
const duplicates = [...html.matchAll(/\sid="([^"]+)"/g)].map((m) => m[1]).filter((id, i, all) => all.indexOf(id) !== i);
console.log(`panel.html id: ${ids.size}, panel.js kullanılan id: ${used.size}`);
console.log(missing.length ? "HTML'de olmayan id: " + missing.join(", ") : "Tüm id'ler HTML'de var.");
console.log(duplicates.length ? "Tekrarlanan id: " + [...new Set(duplicates)].join(", ") : "Tekrarlanan id yok.");
process.exitCode = missing.length || duplicates.length ? 1 : 0;
