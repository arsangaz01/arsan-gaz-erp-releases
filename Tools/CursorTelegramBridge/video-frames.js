"use strict";

// Turns video attachments (Telegram videos, video notes, animations, phone screen recordings) into images the Cursor
// agents can see: timestamped 4x2 contact sheets plus a few larger key frames (scene changes and the last frame).
// Used by bridge.js (PC), server-bridge.js (Linux) and, through the CLI, Tools/CursorIdeBridge/Start-Request.ps1.
// Frames are written to "<video>.frames/" next to the original; the original is never modified.

const fs = require("node:fs/promises");
const fsSync = require("node:fs");
const path = require("node:path");
const { execFile } = require("node:child_process");

const VERSION = 1;
const VIDEO_EXTENSIONS = new Set([".mp4", ".mov", ".m4v", ".webm", ".mkv", ".3gp", ".avi"]);
const DEFAULTS = {
  maxInputBytes: 200 * 1024 * 1024,
  timeoutMs: 120_000,
  sheetWidth: 360,
  columns: 4,
  rows: 2,
  maxSheets: 4,
  keyFrameMaxSide: 1280,
  maxSceneFrames: 3,
  sceneThreshold: 0.25,
};
const FRAMES_DIR_SUFFIX = ".frames";
const FRAMES_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;

function isVideo(filePath, mimeType) {
  if (typeof mimeType === "string" && mimeType.toLowerCase().startsWith("video/")) return true;
  return VIDEO_EXTENSIONS.has(path.extname(String(filePath ?? "")).toLowerCase());
}

let cachedFfmpeg;

function candidateFfmpegPaths(env = process.env, platform = process.platform) {
  const exe = platform === "win32" ? "ffmpeg.exe" : "ffmpeg";
  const candidates = [];
  if (env.FFMPEG_PATH) candidates.push(env.FFMPEG_PATH);
  for (const directory of String(env.PATH ?? env.Path ?? "").split(path.delimiter)) {
    if (directory) candidates.push(path.join(directory.replace(/^"|"$/g, ""), exe));
  }
  if (platform === "win32") {
    const locals = [env.LOCALAPPDATA, env.USERPROFILE && path.join(env.USERPROFILE, "AppData", "Local")].filter(Boolean);
    for (const local of [...new Set(locals)]) {
      candidates.push(path.join(local, "Microsoft", "WinGet", "Links", exe));
      const packages = path.join(local, "Microsoft", "WinGet", "Packages");
      for (const pkg of safeReaddir(packages).filter((name) => /^Gyan\.FFmpeg/i.test(name)).sort().reverse()) {
        for (const build of safeReaddir(path.join(packages, pkg)).filter((name) => /^ffmpeg-/i.test(name)).sort().reverse()) {
          candidates.push(path.join(packages, pkg, build, "bin", exe));
        }
      }
    }
    if (env.ProgramFiles) candidates.push(path.join(env.ProgramFiles, "ffmpeg", "bin", exe));
  } else {
    candidates.push("/usr/bin/ffmpeg", "/usr/local/bin/ffmpeg");
  }
  return candidates;
}

function safeReaddir(directory) {
  try {
    return fsSync.readdirSync(directory);
  } catch {
    return [];
  }
}

function findFfmpeg(env = process.env, platform = process.platform) {
  if (env === process.env && cachedFfmpeg !== undefined) return cachedFfmpeg;
  let found = null;
  for (const candidate of candidateFfmpegPaths(env, platform)) {
    try {
      if (fsSync.statSync(candidate).isFile()) {
        found = candidate;
        break;
      }
    } catch {
      // Try the next location.
    }
  }
  if (env === process.env) cachedFfmpeg = found;
  return found;
}

function run(ffmpeg, args, timeoutMs) {
  return new Promise((resolve, reject) => {
    execFile(ffmpeg, args, { timeout: timeoutMs, maxBuffer: 16 * 1024 * 1024, windowsHide: true }, (error, stdout, stderr) => {
      if (error && error.killed) reject(new Error(`ffmpeg ${Math.round(timeoutMs / 1000)} saniyede bitmedi`));
      else resolve({ code: error ? (error.code ?? 1) : 0, stdout: String(stdout), stderr: String(stderr) });
    });
  });
}

function parseProbe(stderr) {
  const duration = /Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)/.exec(stderr);
  const video = /Stream #\S+.*?Video:.*?\b(\d{2,5})x(\d{2,5})\b/.exec(stderr);
  const rotation = /rotation of (-?\d+(?:\.\d+)?) degrees|rotate\s*:\s*(-?\d+)/.exec(stderr);
  let width = video ? Number(video[1]) : null;
  let height = video ? Number(video[2]) : null;
  const angle = rotation ? Math.abs(Number(rotation[1] ?? rotation[2])) % 180 : 0;
  if (angle === 90 && width && height) [width, height] = [height, width];
  return {
    durationSec: duration ? Number(duration[1]) * 3600 + Number(duration[2]) * 60 + Number(duration[3]) : null,
    width,
    height,
    hasVideo: Boolean(video),
  };
}

// ~16 frames for short clips, ~24 otherwise, never more than the sheets can hold.
function planSheets(durationSec, options = DEFAULTS) {
  const perSheet = options.columns * options.rows;
  const duration = durationSec && durationSec > 0 ? durationSec : 10;
  const wanted = Math.min(options.maxSheets * perSheet, duration < 8 ? 16 : 24);
  const frames = Math.max(1, Math.min(wanted, Math.ceil(duration * 10)));
  const fps = frames / duration;
  const timestamps = Array.from({ length: frames }, (_, index) => index / fps);
  const sheets = [];
  for (let start = 0; start < frames; start += perSheet) sheets.push(timestamps.slice(start, start + perSheet));
  return { fps, frames, perSheet, sheets };
}

function formatTime(seconds) {
  const total = Math.max(0, seconds);
  const minutes = Math.floor(total / 60);
  const rest = total - minutes * 60;
  return `${String(minutes).padStart(2, "0")}:${rest.toFixed(1).padStart(4, "0")}`;
}

function findFontFile(platform = process.platform) {
  const candidates = platform === "win32"
    ? [path.join(process.env.WINDIR ?? "C:\\Windows", "Fonts", "arial.ttf"), path.join(process.env.WINDIR ?? "C:\\Windows", "Fonts", "segoeui.ttf")]
    : ["/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
      "/usr/share/fonts/dejavu-sans-fonts/DejaVuSans.ttf", "/usr/share/fonts/liberation-sans/LiberationSans-Regular.ttf"];
  return candidates.find((candidate) => fsSync.existsSync(candidate)) ?? null;
}

function drawTextFilter(fontFile) {
  const font = fontFile ? `fontfile='${fontFile.replace(/\\/g, "/").replace(/:/g, "\\:")}':` : "";
  return `drawtext=${font}text='%{pts\\:hms}':x=4:y=4:fontsize=16:fontcolor=white:box=1:boxcolor=black@0.65:boxborderw=3`;
}

async function listPngs(directory, prefix) {
  return (await fs.readdir(directory)).filter((name) => name.startsWith(prefix) && name.endsWith(".png")).sort()
    .map((name) => path.join(directory, name));
}

async function makeSheets(ffmpeg, videoPath, outDir, plan, options) {
  const tile = `tile=${options.columns}x${options.rows}:padding=4:margin=4:color=white`;
  const scale = `scale=${options.sheetWidth}:-2`;
  const base = ["-hide_banner", "-loglevel", "error", "-y", "-i", videoPath, "-an"];
  const output = ["-frames:v", String(plan.sheets.length), "-start_number", "1", path.join(outDir, "sheet-%02d.png")];
  const font = findFontFile();
  let result = await run(ffmpeg, [...base, "-vf", `fps=${plan.fps.toFixed(6)},${scale},${drawTextFilter(font)},${tile}`, ...output], options.timeoutMs);
  let labelled = result.code === 0;
  if (!labelled) {
    for (const file of await listPngs(outDir, "sheet-")) await fs.rm(file, { force: true });
    result = await run(ffmpeg, [...base, "-vf", `fps=${plan.fps.toFixed(6)},${scale},${tile}`, ...output], options.timeoutMs);
    if (result.code !== 0) throw new Error("ffmpeg kare çıkaramadı: " + lastLine(result.stderr));
  }
  const files = await listPngs(outDir, "sheet-");
  if (files.length === 0) throw new Error("ffmpeg hiç kare üretmedi");
  return files.map((file, index) => ({ path: file, timestamps: plan.sheets[index] ?? [], labelled }));
}

async function makeKeyFrames(ffmpeg, videoPath, outDir, durationSec, options) {
  const scale = `scale='min(iw,${options.keyFrameMaxSide})':'min(ih,${options.keyFrameMaxSide})':force_original_aspect_ratio=decrease:force_divisible_by=2`;
  const frames = [];
  const scene = await run(ffmpeg, ["-hide_banner", "-y", "-i", videoPath, "-an",
    "-vf", `select='gt(scene,${options.sceneThreshold})',showinfo,${scale}`, "-fps_mode", "vfr",
    "-frames:v", String(options.maxSceneFrames), "-start_number", "1", path.join(outDir, "key-%02d.png")], options.timeoutMs);
  if (scene.code === 0) {
    const times = [...scene.stderr.matchAll(/\bpts_time:\s*(-?\d+(?:\.\d+)?)/g)].map((match) => Number(match[1]));
    for (const [index, file] of (await listPngs(outDir, "key-")).entries()) {
      frames.push({ path: file, timeSec: times[index] ?? null, kind: "sahne değişimi" });
    }
  }
  if (frames.length === 0 && durationSec) {
    const middle = path.join(outDir, "key-mid.png");
    const result = await run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-ss", (durationSec / 2).toFixed(2), "-i", videoPath,
      "-an", "-vf", scale, "-frames:v", "1", middle], options.timeoutMs);
    if (result.code === 0 && fsSync.existsSync(middle)) frames.push({ path: middle, timeSec: durationSec / 2, kind: "orta kare" });
  }
  const last = path.join(outDir, "key-last.png");
  const result = await run(ffmpeg, ["-hide_banner", "-loglevel", "error", "-y", "-sseof", "-1", "-i", videoPath, "-an",
    "-vf", scale, "-update", "1", last], options.timeoutMs);
  if (result.code === 0 && fsSync.existsSync(last)) {
    const lastTime = durationSec ? Math.max(0, durationSec - 0.1) : null;
    const duplicate = frames.some((frame) => frame.timeSec != null && lastTime != null && Math.abs(frame.timeSec - lastTime) < 1);
    if (duplicate) await fs.rm(last, { force: true });
    else frames.push({ path: last, timeSec: lastTime, kind: "son kare" });
  }
  return frames;
}

function lastLine(text) {
  return String(text).trim().split(/\r?\n/).pop()?.slice(0, 300) || "bilinmeyen hata";
}

// Returns the manifest: { source, durationSec, width, height, sheets: [{path, timestamps}], keyFrames: [{path, timeSec, kind}] }.
async function extractFrames(videoPath, overrides = {}) {
  const options = { ...DEFAULTS, ...overrides };
  const ffmpeg = options.ffmpegPath ?? findFfmpeg();
  if (!ffmpeg) throw new Error("ffmpeg bulunamadı (FFMPEG_PATH, PATH, winget veya /usr/bin/ffmpeg)");
  const stat = await fs.stat(videoPath);
  if (!stat.isFile() || stat.size === 0) throw new Error("video dosyası boş veya bulunamadı");
  if (stat.size > options.maxInputBytes) {
    throw new Error(`video ${Math.round(stat.size / 1048576)} MB; kare çıkarma sınırı ${Math.round(options.maxInputBytes / 1048576)} MB`);
  }
  const outDir = options.outDir ?? videoPath + FRAMES_DIR_SUFFIX;
  const manifestPath = path.join(outDir, "manifest.json");
  const cached = await readManifest(manifestPath);
  if (cached && cached.version === VERSION && cached.sourceSize === stat.size && cached.sourceMtimeMs === stat.mtimeMs
    && [...cached.sheets, ...cached.keyFrames].every((frame) => fsSync.existsSync(frame.path))) {
    return cached;
  }
  await fs.rm(outDir, { recursive: true, force: true });
  await fs.mkdir(outDir, { recursive: true });
  try {
    const probe = parseProbe((await run(ffmpeg, ["-hide_banner", "-i", videoPath], options.timeoutMs)).stderr);
    if (!probe.hasVideo) throw new Error("dosyada görüntü akışı yok");
    const plan = planSheets(probe.durationSec, options);
    const sheets = await makeSheets(ffmpeg, videoPath, outDir, plan, options);
    const keyFrames = await makeKeyFrames(ffmpeg, videoPath, outDir, probe.durationSec, options).catch(() => []);
    const manifest = {
      version: VERSION,
      source: videoPath,
      sourceSize: stat.size,
      sourceMtimeMs: stat.mtimeMs,
      durationSec: probe.durationSec,
      width: probe.width,
      height: probe.height,
      columns: options.columns,
      rows: options.rows,
      sheets,
      keyFrames,
      createdUtc: new Date().toISOString(),
    };
    await fs.writeFile(manifestPath, JSON.stringify(manifest, null, 2), "utf8");
    return manifest;
  } catch (error) {
    await fs.rm(outDir, { recursive: true, force: true }).catch(() => undefined);
    throw error;
  }
}

async function readManifest(manifestPath) {
  try {
    return JSON.parse(await fs.readFile(manifestPath, "utf8"));
  } catch {
    return null;
  }
}

function describeFrames(manifest, displayName = path.basename(manifest.source)) {
  const size = manifest.width && manifest.height ? `, ${manifest.width}x${manifest.height}` : "";
  const duration = manifest.durationSec != null ? `${manifest.durationSec.toFixed(1)} sn` : "süre bilinmiyor";
  const lines = [`Video ${displayName} (${duration}${size}): videodan çıkarılan kareler (zaman damgalı).`];
  for (const sheet of manifest.sheets) {
    lines.push(`- ${sheet.path} — ${manifest.columns}x${manifest.rows} kare, soldan sağa ve yukarıdan aşağı: `
      + sheet.timestamps.map(formatTime).join(", "));
  }
  for (const frame of manifest.keyFrames) {
    lines.push(`- ${frame.path} — büyük kare (${frame.kind}${frame.timeSec != null ? `, ${formatTime(frame.timeSec)}` : ""})`);
  }
  return lines.join("\n");
}

// Processes every video attachment; failures become a note instead of failing the request.
async function framesForAttachments(attachments, options = {}) {
  const notes = [];
  const images = [];
  const maxImages = options.maxImages ?? 12;
  for (const attachment of attachments) {
    const file = typeof attachment === "string" ? attachment : attachment.path;
    const mimeType = typeof attachment === "string" ? null : attachment.mimeType;
    const displayName = (typeof attachment === "string" ? null : attachment.displayName) ?? path.basename(file);
    if (!isVideo(file, mimeType)) continue;
    try {
      const manifest = await extractFrames(file, options);
      notes.push(describeFrames(manifest, displayName));
      for (const frame of [...manifest.sheets, ...manifest.keyFrames]) {
        if (images.length < maxImages) images.push(frame.path);
      }
    } catch (error) {
      notes.push(`Video ${displayName}: kareler çıkarılamadı (${error.message}). Videoyu açamıyorsan bunu kullanıcıya söyle.`);
    }
    if (options.cleanup !== false) await cleanupFrameDirs(path.dirname(file)).catch(() => undefined);
  }
  return { note: notes.join("\n\n"), images };
}

// Inline images for the Cursor SDK; frames beyond the byte budget stay available by path only.
async function loadImages(paths, maxBytes = 12 * 1024 * 1024) {
  const images = [];
  let total = 0;
  for (const file of paths) {
    const data = await fs.readFile(file);
    if (total + data.length > maxBytes) break;
    total += data.length;
    images.push({ data: data.toString("base64"), mimeType: "image/png" });
  }
  return images;
}

async function cleanupFrameDirs(directory, maxAgeMs = FRAMES_MAX_AGE_MS, now = Date.now()) {
  let removed = 0;
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    if (!entry.isDirectory() || !entry.name.endsWith(FRAMES_DIR_SUFFIX)) continue;
    const full = path.join(directory, entry.name);
    const stat = await fs.stat(full).catch(() => null);
    if (stat && now - stat.mtimeMs > maxAgeMs) {
      await fs.rm(full, { recursive: true, force: true });
      removed++;
    }
  }
  return removed;
}

async function main(argv) {
  const files = argv.filter((arg) => !arg.startsWith("--"));
  const json = argv.includes("--json");
  if (files.length === 0) {
    process.stderr.write("kullanım: node video-frames.js [--json] <video> [video...]\n");
    return 2;
  }
  const results = [];
  let failed = 0;
  for (const file of files) {
    try {
      const manifest = await extractFrames(path.resolve(file));
      results.push(json ? manifest : describeFrames(manifest));
    } catch (error) {
      failed++;
      results.push(json ? { source: file, error: error.message } : `Video ${path.basename(file)}: kareler çıkarılamadı (${error.message}).`);
    }
    await cleanupFrameDirs(path.dirname(path.resolve(file))).catch(() => undefined);
  }
  process.stdout.write((json ? JSON.stringify(results, null, 2) : results.join("\n\n")) + "\n");
  return failed > 0 ? 1 : 0;
}

if (require.main === module) {
  main(process.argv.slice(2)).then((code) => { process.exitCode = code; }, (error) => {
    process.stderr.write(`${error?.stack ?? error}\n`);
    process.exitCode = 1;
  });
}

module.exports = {
  isVideo, findFfmpeg, candidateFfmpegPaths, parseProbe, planSheets, formatTime, extractFrames, describeFrames,
  framesForAttachments, loadImages, cleanupFrameDirs, FRAMES_DIR_SUFFIX,
};
