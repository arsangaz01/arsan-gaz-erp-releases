"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs/promises");
const fsSync = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { execFileSync } = require("node:child_process");
const frames = require("./video-frames");

const ffmpeg = frames.findFfmpeg();
const skip = ffmpeg ? false : "ffmpeg yok";

test("video detection by MIME type and extension", () => {
  assert.equal(frames.isVideo("C:/a/408189719.mp4"), true);
  assert.equal(frames.isVideo("/x/clip.MOV"), true);
  assert.equal(frames.isVideo("/x/clip.webm"), true);
  assert.equal(frames.isVideo("/x/dosya.bin", "video/mp4"), true);
  assert.equal(frames.isVideo("/x/foto.jpg", "image/jpeg"), false);
  assert.equal(frames.isVideo("/x/ses.ogg"), false);
});

test("sheet plan keeps 16-24 frames spread over the duration", () => {
  const short = frames.planSheets(6, { columns: 4, rows: 2, maxSheets: 4 });
  assert.equal(short.frames, 16);
  assert.equal(short.sheets.length, 2);
  const long = frames.planSheets(600, { columns: 4, rows: 2, maxSheets: 4 });
  assert.equal(long.frames, 24);
  assert.equal(long.sheets.length, 3);
  assert.ok(long.sheets.at(-1).at(-1) < 600);
  const tiny = frames.planSheets(0.5, { columns: 4, rows: 2, maxSheets: 4 });
  assert.equal(tiny.frames, 5);
  assert.equal(frames.planSheets(null, { columns: 4, rows: 2, maxSheets: 4 }).frames, 24);
});

test("probe parsing reads duration and rotated size", () => {
  const probe = frames.parseProbe([
    "  Duration: 00:01:19.80, start: 0.000000, bitrate: 1339 kb/s",
    "  Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv), 1280x592, 1200 kb/s, 30 fps",
    "      displaymatrix: rotation of -90.00 degrees",
  ].join("\n"));
  assert.equal(probe.durationSec, 79.8);
  assert.deepEqual([probe.width, probe.height], [592, 1280]);
  assert.equal(frames.parseProbe("Stream #0:0: Audio: aac").hasVideo, false);
});

test("ffmpeg discovery honours FFMPEG_PATH and the winget package folder", () => {
  const env = { FFMPEG_PATH: "/opt/x/ffmpeg", PATH: "" };
  assert.equal(frames.candidateFfmpegPaths(env, "linux")[0], "/opt/x/ffmpeg");
  assert.ok(frames.candidateFfmpegPaths({ PATH: "" }, "linux").includes("/usr/bin/ffmpeg"));
  assert.ok(frames.candidateFfmpegPaths({ PATH: "", LOCALAPPDATA: "C:\\L" }, "win32")
    .some((candidate) => candidate.includes(path.join("WinGet", "Links"))));
  assert.equal(frames.findFfmpeg({ FFMPEG_PATH: path.join(os.tmpdir(), "yok", "ffmpeg"), PATH: "" }, "win32"), null);
});

test("frames, manifest, prompt note and cache from a generated video", { skip, timeout: 120_000 }, async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "video-frames-"));
  try {
    const video = path.join(directory, "test.mp4");
    execFileSync(ffmpeg, ["-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc=duration=6:size=320x240:rate=10",
      "-pix_fmt", "yuv420p", video], { windowsHide: true });
    const manifest = await frames.extractFrames(video);
    assert.ok(Math.abs(manifest.durationSec - 6) < 0.2);
    assert.deepEqual([manifest.width, manifest.height], [320, 240]);
    assert.equal(manifest.sheets.length, 2);
    assert.equal(manifest.sheets[0].timestamps.length, 8);
    assert.ok(manifest.keyFrames.some((frame) => frame.kind === "son kare"));
    for (const frame of [...manifest.sheets, ...manifest.keyFrames]) {
      const header = (await fs.readFile(frame.path)).subarray(0, 8);
      assert.deepEqual([...header], [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
      assert.equal(path.dirname(frame.path), video + frames.FRAMES_DIR_SUFFIX);
    }
    assert.ok(fsSync.existsSync(video), "original video stays");

    const again = await frames.extractFrames(video);
    assert.equal(again.createdUtc, manifest.createdUtc, "second call reuses the manifest");

    const result = await frames.framesForAttachments([video, path.join(directory, "not-a-video.txt")]);
    assert.match(result.note, /videodan çıkarılan kareler \(zaman damgalı\)/);
    assert.match(result.note, /00:00\.0/);
    assert.equal(result.images.length, manifest.sheets.length + manifest.keyFrames.length);
    const images = await frames.loadImages(result.images);
    assert.equal(images[0].mimeType, "image/png");
    assert.ok(images[0].data.length > 100);
    assert.equal((await frames.loadImages(result.images, 1)).length, 0);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
});

test("broken or oversized videos become a note, not a failure", { skip, timeout: 60_000 }, async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "video-frames-"));
  try {
    const broken = path.join(directory, "bozuk.mp4");
    await fs.writeFile(broken, "bu bir video değil");
    const result = await frames.framesForAttachments([broken]);
    assert.match(result.note, /kareler çıkarılamadı/);
    assert.equal(result.images.length, 0);
    assert.equal(fsSync.existsSync(broken + frames.FRAMES_DIR_SUFFIX), false);
    await assert.rejects(frames.extractFrames(broken, { maxInputBytes: 4 }), /sınırı/);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
});

test("bridges send frames inline and fall back to text", async () => {
  const { buildPrompt, sendWithImages } = require("./bridge");
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "video-frames-"));
  try {
    const png = path.join(directory, "sheet-01.png");
    await fs.writeFile(png, Buffer.from([0x89, 0x50, 0x4e, 0x47]));
    const sent = [];
    const agent = { send: async (message) => { sent.push(message); return { id: "run" }; } };
    await sendWithImages(agent, "metin", [png], () => undefined);
    assert.equal(sent[0].text, "metin");
    assert.deepEqual(sent[0].images, [{ data: "iVBORw==", mimeType: "image/png" }]);
    await sendWithImages(agent, "yalnız metin", [], () => undefined);
    assert.equal(sent[1], "yalnız metin");

    const fallback = [];
    const picky = { send: async (message) => {
      if (typeof message !== "string") throw new Error("images unsupported");
      fallback.push(message);
      return { id: "run" };
    } };
    await sendWithImages(picky, "metin", [png], () => undefined);
    assert.deepEqual(fallback, ["metin"]);

    const prompt = buildPrompt("hata var", "", ["/a/1-video.mp4"], "telegram", "", "Video 1-video.mp4 (6.0 sn): videodan çıkarılan kareler");
    assert.match(prompt, /Ek notları/);
    assert.ok(prompt.indexOf("videodan çıkarılan kareler") < prompt.indexOf("Kullanıcının isteği"));
    assert.doesNotMatch(buildPrompt("x", "", [], "telegram"), /Ek notları/);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
});

test("old frame folders are cleaned up", async () => {
  const directory = await fs.mkdtemp(path.join(os.tmpdir(), "video-frames-"));
  try {
    await fs.mkdir(path.join(directory, "a.mp4.frames"));
    await fs.mkdir(path.join(directory, "keep"));
    assert.equal(await frames.cleanupFrameDirs(directory, 1000, Date.now() + 10_000), 1);
    assert.equal(fsSync.existsSync(path.join(directory, "keep")), true);
    assert.equal(await frames.cleanupFrameDirs(directory), 0);
  } finally {
    await fs.rm(directory, { recursive: true, force: true });
  }
});
