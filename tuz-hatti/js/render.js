/** Dünya çizimi. Araç ve üs görselleri üstten, magenta zeminden ayrılmış özgün plakalardır. */

import { faction } from "./balance.js";
import { MAP_H, MAP_W, canSeeEntity, cellExplored, cellVisible, entityDef } from "./sim.js";

export const SPRITE_SRC = {
  abrams: "assets/abrams.png",
  type99: "assets/type99.png",
  ch2: "assets/ch2.png",
  leo2: "assets/leo2.png",
  t90: "assets/t90.png",
  t64: "assets/t64.png",
  altay: "assets/altay.png",
  karrar: "assets/karrar.png",
  apache: "assets/apache.png",
  gj11: "assets/gj11.png",
  f35: "assets/f35.png",
  typhoon: "assets/typhoon.png",
  lancet: "assets/lancet.png",
  fpv: "assets/fpv.png",
  kizilelma: "assets/kizilelma.png",
  shahed: "assets/shahed.png",
  truck: "assets/truck.png",
  infantry: "assets/infantry.png",
  spg: "assets/spg.png",
  atgm: "assets/atgm.png",
  ifv: "assets/ifv.png",
  hq: "assets/hq.png",
  barracks: "assets/barracks.png",
  motor: "assets/motor.png",
  sam: "assets/sam.png",
  depot: "assets/depot.png",
  strip: "assets/strip.png",
  fuel: "assets/fuel.png",
  terrain: "assets/terrain.jpg",
};

const images = {};
const tintCache = {};

function preloadSprites() {
  if (typeof Image === "undefined") return;
  for (const [key, src] of Object.entries(SPRITE_SRC)) {
    const img = new Image();
    img.src = src;
    images[key] = img;
  }
}

preloadSprites();

function gfxOf(view) {
  return (
    view.gfx || {
      terrainScale: 0.5,
      shadows: true,
      decor: true,
      smoothing: true,
      fogSmooth: true,
      nodeScale: 1,
    }
  );
}

export function drawWorld(ctx, state, view) {
  const gfx = gfxOf(view);
  ensureTerrain(view, state, gfx);
  ctx.imageSmoothingEnabled = !!gfx.smoothing;
  ctx.setTransform(view.zoom, 0, 0, view.zoom, -view.camX * view.zoom, -view.camY * view.zoom);
  ctx.drawImage(view.terrain, 0, 0, MAP_W, MAP_H);
  if (gfx.decor) drawDecor(ctx, state);
  drawNodes(ctx, state, gfx);
  drawEntities(ctx, state, view, gfx);
  drawFog(ctx, state, view);
  drawBarsAndRings(ctx, state, view);
  drawGhost(ctx, state, view);
  drawFx(ctx, view);
  if (view.box) drawBox(ctx, view.box);
}

function ensureTerrain(view, state, gfx) {
  const photo = images.terrain;
  const photoReady = !!(photo && photo.complete && photo.naturalWidth);
  if (photo && !photo.complete && !view.terrainHook) {
    view.terrainHook = true;
    photo.addEventListener("load", () => {
      view.terrain = null;
    });
  }
  const scale = gfx.terrainScale || 0.5;
  if (view.terrain && view.terrainSeed === state.seed && view.terrainPhoto === photoReady && view.terrainScale === scale) return;
  const c = document.createElement("canvas");
  c.width = Math.floor(MAP_W * scale);
  c.height = Math.floor(MAP_H * scale);
  const g = c.getContext("2d");
  g.scale(scale, scale);
  if (photoReady) {
    g.drawImage(photo, 0, 0, MAP_W, MAP_H);
  } else {
    g.fillStyle = "#6d6248";
    g.fillRect(0, 0, MAP_W, MAP_H);
  }
  const shade = g.createLinearGradient(0, 0, 0, MAP_H);
  shade.addColorStop(0, "rgba(12,16,10,0.18)");
  shade.addColorStop(0.5, "rgba(0,0,0,0)");
  shade.addColorStop(1, "rgba(8,10,8,0.22)");
  g.fillStyle = shade;
  g.fillRect(0, 0, MAP_W, MAP_H);
  for (const rock of state.obstacles) {
    g.fillStyle = "rgba(20,16,12,0.45)";
    g.beginPath();
    g.ellipse(rock.x, rock.y, rock.r * 0.9, rock.r * 0.55, 0, 0, Math.PI * 2);
    g.fill();
    g.strokeStyle = "rgba(40,32,24,0.8)";
    g.lineWidth = 2;
    g.stroke();
  }
  view.terrain = c;
  view.terrainSeed = state.seed;
  view.terrainPhoto = photoReady;
  view.terrainScale = scale;
}

function drawDecor(ctx, state) {
  for (const d of state.decor) {
    if (d.kind !== "bush") continue;
    ctx.fillStyle = "rgba(48,62,34,0.35)";
    ctx.beginPath();
    ctx.ellipse(d.x, d.y, d.r * 0.8, d.r * 0.45, 0, 0, Math.PI * 2);
    ctx.fill();
  }
}

function drawNodes(ctx, state, gfx) {
  for (const n of state.nodes) {
    const left = n.amount / n.max;
    const size = 54 * (gfx.nodeScale || 1);
    ctx.save();
    ctx.translate(n.x, n.y);
    ctx.globalAlpha = left > 0 ? 1 : 0.4;
    if (!drawSprite(ctx, "fuel", size, null)) {
      ctx.fillStyle = left > 0 ? "#8a7040" : "#666";
      ctx.beginPath();
      ctx.arc(0, 0, 14, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.restore();
  }
}

function drawEntities(ctx, state, view, gfx) {
  const list = state.entities.filter((e) => e.hp > 0);
  list.sort((a, b) => {
    const layer = (e) => (e.isBuilding ? 0 : entityDef(state, e).air ? 2 : 1);
    return layer(a) - layer(b) || a.y - b.y;
  });
  for (const e of list) {
    if (e.owner !== 0 && !canSeeEntity(state, 0, e)) continue;
    const colors = faction(state.players[e.owner].faction).colors;
    const def = entityDef(state, e);
    ctx.save();
    ctx.translate(e.x, e.y);
    ctx.globalAlpha = e.isBuilding && !e.constructed ? 0.72 : 1;
    if (e.isBuilding) drawBuilding(ctx, e, def, colors, gfx);
    else drawUnit(ctx, e, def, colors, gfx);
    ctx.restore();
  }
}

function drawBuilding(ctx, e, def, colors, gfx) {
  const size = def.radius * 3.05;
  if (gfx.shadows) {
    ctx.fillStyle = "rgba(0,0,0,0.28)";
    ctx.beginPath();
    ctx.ellipse(2, size * 0.18, size * 0.34, size * 0.12, 0, 0, Math.PI * 2);
    ctx.fill();
  }
  const tint = colors.wash;
  if (!drawSprite(ctx, def.sprite, size, tint)) {
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-def.radius, -def.radius * 0.7, def.radius * 2, def.radius * 1.4);
  }
  if (!e.constructed) {
    ctx.strokeStyle = "#e6dcc8";
    ctx.lineWidth = 1.5;
    ctx.setLineDash([5, 4]);
    ctx.strokeRect(-def.radius, -def.radius, def.radius * 2, def.radius * 2);
    ctx.setLineDash([]);
    ctx.fillStyle = "rgba(8,10,8,0.75)";
    ctx.fillRect(-18, -3, 36, 5);
    ctx.fillStyle = "#d7c48a";
    ctx.fillRect(-18, -3, 36 * Math.max(0, Math.min(1, e.progress)), 5);
  }
}

function drawUnit(ctx, e, def, colors, gfx) {
  const size = def.radius * (def.air ? 4.4 : 3.55);
  if (gfx.shadows) {
    ctx.fillStyle = def.air ? "rgba(0,0,0,0.22)" : "rgba(0,0,0,0.35)";
    ctx.beginPath();
    ctx.ellipse(def.air ? 8 : 3, def.air ? 14 : 7, size * 0.22, size * 0.09, 0, 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.rotate(e.angle);
  const tint = def.wash ? colors.wash : null;
  if (!drawSprite(ctx, def.sprite, size, tint)) {
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-def.radius, -def.radius * 0.55, def.radius * 2, def.radius * 1.1);
    ctx.fillStyle = colors.cloth;
    ctx.fillRect(0, -2, def.radius + 6, 3);
  }
  if (e.carry > 0) {
    ctx.fillStyle = "#c6a15a";
    ctx.fillRect(def.radius * 0.2, -def.radius * 0.9, 7, 5);
  }
}

function ready(img) {
  return img && img.complete && img.naturalWidth > 0;
}

function drawSprite(ctx, key, size, tint) {
  const img = images[key];
  if (!ready(img)) return false;
  const source = tint ? tinted(key, tint) : img;
  if (!source) return false;
  ctx.drawImage(source, -size / 2, -size / 2, size, size);
  return true;
}

function tinted(key, tint) {
  const img = images[key];
  if (!ready(img)) return null;
  const id = key + "|" + tint;
  if (tintCache[id]) return tintCache[id];
  const c = document.createElement("canvas");
  c.width = img.naturalWidth;
  c.height = img.naturalHeight;
  const g = c.getContext("2d");
  g.drawImage(img, 0, 0);
  g.globalCompositeOperation = "source-atop";
  g.fillStyle = tint;
  g.fillRect(0, 0, c.width, c.height);
  tintCache[id] = c;
  return c;
}

function drawFog(ctx, state, view) {
  const { cols, rows } = state;
  if (!view.fogCanvas) {
    view.fogCanvas = document.createElement("canvas");
    view.fogCanvas.width = cols;
    view.fogCanvas.height = rows;
    view.fogImg = new ImageData(cols, rows);
  }
  const data = view.fogImg.data;
  const vis = state.visible[0];
  const exp = state.explored[0];
  for (let i = 0; i < cols * rows; i++) {
    const a = vis[i] ? 0 : exp[i] ? 118 : 210;
    const o = i * 4;
    data[o] = 8;
    data[o + 1] = 12;
    data[o + 2] = 10;
    data[o + 3] = a;
  }
  const g = view.fogCanvas.getContext("2d");
  g.putImageData(view.fogImg, 0, 0);
  ctx.imageSmoothingEnabled = gfxOf(view).fogSmooth !== false;
  ctx.drawImage(view.fogCanvas, 0, 0, MAP_W, MAP_H);
}

function drawBarsAndRings(ctx, state, view) {
  const selected = view.selected;
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    const own = e.owner === 0;
    const seen = own || canSeeEntity(state, 0, e);
    if (!seen) continue;
    const visibleNow = own || cellVisible(state, 0, e.x, e.y) || (e.isBuilding && cellExplored(state, 0, e.x, e.y));
    if (!visibleNow && !own) continue;
    const isSel = selected.has(e.id);
    if (isSel) {
      ctx.strokeStyle = "#e7d7a4";
      ctx.lineWidth = 1.6 / view.zoom;
      ctx.beginPath();
      ctx.arc(e.x, e.y, e.radius + 6, 0, Math.PI * 2);
      ctx.stroke();
    }
    const showBar = isSel || e.hp < e.maxHp - 0.5;
    if (!showBar) continue;
    if (!own && e.isBuilding && !cellVisible(state, 0, e.x, e.y)) continue;
    const w = Math.max(18, e.radius * 2);
    const x = e.x - w / 2;
    const y = e.y - e.radius - 10;
    ctx.fillStyle = "rgba(8,10,8,0.8)";
    ctx.fillRect(x, y, w, 3);
    ctx.fillStyle = e.hp / e.maxHp > 0.35 ? "#b7c48a" : "#c45c3c";
    ctx.fillRect(x, y, w * Math.max(0, e.hp / e.maxHp), 3);
  }

  if (view.rally) {
    ctx.strokeStyle = "#e7d7a4";
    ctx.lineWidth = 1.4 / view.zoom;
    ctx.beginPath();
    ctx.moveTo(view.rally.x - 7, view.rally.y);
    ctx.lineTo(view.rally.x + 7, view.rally.y);
    ctx.moveTo(view.rally.x, view.rally.y - 9);
    ctx.lineTo(view.rally.x, view.rally.y + 5);
    ctx.stroke();
  }
}

function drawGhost(ctx, state, view) {
  const ghost = view.ghost;
  if (!ghost) return;
  const fac = faction(state.players[0].faction);
  const def = fac.buildings[ghost.kind];
  if (!def) return;
  ctx.save();
  ctx.translate(ghost.x, ghost.y);
  ctx.globalAlpha = 0.5;
  drawSprite(ctx, def.sprite, def.radius * 3.05, null);
  ctx.globalAlpha = 0.9;
  ctx.strokeStyle = ghost.ok ? "#b7c48a" : "#c45c3c";
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.arc(0, 0, def.radius, 0, Math.PI * 2);
  ctx.stroke();
  ctx.restore();
}

function drawFx(ctx, view) {
  for (const fx of view.fx) {
    const t = 1 - fx.life / fx.max;
    ctx.globalAlpha = Math.max(0, fx.life / fx.max);
    if (fx.type === "shot") {
      ctx.strokeStyle = fx.air ? "#d5e4f2" : fx.heavy ? "#e4d2b4" : "#f2e6c8";
      ctx.lineWidth = (fx.heavy ? 2.2 : 1.3) / view.zoom;
      ctx.beginPath();
      ctx.moveTo(fx.x1, fx.y1);
      ctx.lineTo(fx.x1 + (fx.x2 - fx.x1) * Math.min(1, t * 1.4), fx.y1 + (fx.y2 - fx.y1) * Math.min(1, t * 1.4));
      ctx.stroke();
    } else if (fx.type === "puff") {
      ctx.fillStyle = fx.color;
      ctx.beginPath();
      ctx.arc(fx.x, fx.y, 4 + t * 14, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.globalAlpha = 1;
  }
}

function drawBox(ctx, box) {
  const x = Math.min(box.x1, box.x2);
  const y = Math.min(box.y1, box.y2);
  const w = Math.abs(box.x2 - box.x1);
  const h = Math.abs(box.y2 - box.y1);
  ctx.fillStyle = "rgba(214, 196, 140, 0.12)";
  ctx.strokeStyle = "#e7d7a4";
  ctx.lineWidth = 1.25;
  ctx.fillRect(x, y, w, h);
  ctx.strokeRect(x, y, w, h);
}

export function drawMinimap(canvas, state, view) {
  const ctx = canvas.getContext("2d");
  const w = canvas.width;
  const h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = "#3a4030";
  ctx.fillRect(0, 0, w, h);
  const sx = w / MAP_W;
  const sy = h / MAP_H;
  ctx.fillStyle = "rgba(6,10,8,0.78)";
  const cellW = (MAP_W / state.cols) * sx;
  const cellH = (MAP_H / state.rows) * sy;
  for (let cy = 0; cy < state.rows; cy++) {
    for (let cx = 0; cx < state.cols; cx++) {
      const i = cy * state.cols + cx;
      if (state.visible[0][i]) continue;
      ctx.globalAlpha = state.explored[0][i] ? 0.45 : 0.88;
      ctx.fillRect(cx * cellW, cy * cellH, cellW + 0.5, cellH + 0.5);
    }
  }
  ctx.globalAlpha = 1;
  for (const n of state.nodes) {
    if (!cellExplored(state, 0, n.x, n.y) || n.amount <= 0) continue;
    ctx.fillStyle = "#c6a15a";
    ctx.fillRect(n.x * sx - 2, n.y * sy - 2, 4, 4);
  }
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    if (e.owner !== 0 && !canSeeEntity(state, 0, e)) continue;
    ctx.fillStyle = faction(state.players[e.owner].faction).colors.main;
    const r = e.isBuilding ? 3.2 : 1.7;
    ctx.fillRect(e.x * sx - r / 2, e.y * sy - r / 2, r, r);
  }
  const viewW = canvas.clientWidth ? (view.canvasWidth / view.zoom) * sx : 20;
  const viewH = (view.canvasHeight / view.zoom) * sy;
  ctx.strokeStyle = "#e7d7a4";
  ctx.lineWidth = 1;
  ctx.strokeRect(view.camX * sx, view.camY * sy, viewW, viewH);
}
