/** Tek dosyalık sayfa üretir. Telefonda sunucu olmadan açılabilsin diye import kullanmaz. */

import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const order = ["balance.js", "sim.js", "ai.js", "audio.js", "gfx.js", "render.js", "main.js"];

function stripModules(source) {
  return source
    .replace(/^\s*import\s[\s\S]*?from\s+["'][^"']+["'];?/gm, "")
    .replace(/\bexport\s+(?=(async\s+)?function\b|const\b|let\b|var\b|class\b)/g, "");
}

const js = order
  .map((name) => stripModules(fs.readFileSync(path.join(root, "js", name), "utf8")))
  .join("\n")
  .replace(/assets\/[a-z0-9_-]+\.(png|jpg)/g, (match) => {
    const file = path.join(root, match);
    if (!fs.existsSync(file)) return match;
    const mime = match.endsWith(".png") ? "image/png" : "image/jpeg";
    return `data:${mime};base64,${fs.readFileSync(file).toString("base64")}`;
  });
const css = fs.readFileSync(path.join(root, "css", "game.css"), "utf8");
let html = fs.readFileSync(path.join(root, "index.html"), "utf8");
html = html.replace('<link rel="stylesheet" href="css/game.css" />', `<style>\n${css}\n</style>`);
html = html.replace(
  '<script type="module" src="js/main.js"></script>',
  `<script>\n${js}\n</script>`,
);

const out = path.join(root, "oyna.html");
fs.writeFileSync(out, html);
if (/\bimport\s|\bexport\s/.test(js)) {
  console.error("pakette import veya export kaldı");
  process.exit(1);
}
console.log("oyna.html", html.length);
