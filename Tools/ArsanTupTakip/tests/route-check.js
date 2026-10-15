// Compares the URLs called by panel.js and the mobile ApiClient with the routes mapped on the server.
const fs = require("fs");
const path = require("path");
const root = path.join(__dirname, "..");
const walk = (dir) => fs.readdirSync(dir, { withFileTypes: true }).flatMap((d) =>
  d.isDirectory() ? (["bin", "obj"].includes(d.name) ? [] : walk(path.join(dir, d.name))) : [path.join(dir, d.name)]);

const routes = [];
for (const file of walk(root + "/src/ArsanTupTakip.Api").filter((f) => f.endsWith(".cs"))) {
  const text = fs.readFileSync(file, "utf8");
  const panel = /RouteGroupBuilder|api\.Map/.test(text);
  for (const m of text.matchAll(/(\w+)\.Map(Get|Post|Put|Delete)\(\s*"([^"]+)"/g)) {
    let route = m[3];
    if (m[1] === "api") route = "/panel/api" + route;
    else if (m[1] === "group" || m[1] === "g") route = "?" + route;
    routes.push({ method: m[2].toUpperCase(), route, file: path.basename(file) });
  }
}
const toRegex = (r) => new RegExp("^" + r.replace(/[.]/g, "\\.").replace(/\{[^}]+\}/g, "[^/]+") + "$");
const compiled = routes.map((r) => ({ ...r, re: toRegex(r.route) }));
const match = (url) => compiled.filter((r) => r.re.test(url) || (r.route.startsWith("?") && toRegex(r.route.slice(1)).test(url.replace(/^\/[^/]+(\/[^/]+)?/, ""))));

const norm = (u) => u.split("?")[0].replace(/\$\{[^}]+\}/g, "X").replace(/\{[^}]+\}/g, "X").replace(/"\s*\+\s*[\w.]+\s*\+\s*"/g, "X").replace(/\/+$/, "");
const missing = [];
const js = fs.readFileSync(root + "/src/ArsanTupTakip.Api/WebPanel/panel.js", "utf8");
const panelUrls = new Set([...js.matchAll(/["'`](\/panel\/api\/[^"'`]*)["'`]/g)].map((m) => norm(m[1])));
for (const u of panelUrls) if (!match(u.replace(/X$/, "1").replace(/X/g, "1")).length && !match(u + "1").length) missing.push("panel  " + u);

const mobileFiles = walk(root + "/mobile/ArsanTupTakip.Mobile").filter((f) => f.endsWith(".cs"));
const mobileUrls = new Set();
for (const f of mobileFiles) {
  const text = fs.readFileSync(f, "utf8");
  for (const m of text.matchAll(/\$?"(\/api\/[^"]*)"/g)) mobileUrls.add(norm(m[1]));
}
for (const u of mobileUrls) if (!match(u.replace(/X/g, "1")).length && !match(u + "1").length && !match(u + "/1").length) missing.push("mobil  " + u);

console.log(`sunucu rotası: ${routes.length}, panel çağrısı: ${panelUrls.size}, mobil çağrısı: ${mobileUrls.size}`);
console.log(missing.length ? "EŞLEŞMEYEN:\n" + missing.join("\n") : "Tüm çağrılar sunucuda tanımlı.");
