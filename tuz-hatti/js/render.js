/** Dünya çizimi. Araç ve üs görselleri üstten, magenta zeminden ayrılmış özgün plakalardır. */

import { faction } from "./balance.js";
import { canSeeEntity, cellExplored, cellVisible, entityDef } from "./sim.js";

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
  ship: "assets/ship.png",
  terrain: "assets/terrain.jpg",
  "map-tur": "assets/map-tur.jpg",
  "map-usa": "assets/map-usa.jpg",
  "map-cin": "assets/map-cin.jpg",
  "map-rus": "assets/map-rus.jpg",
  "map-alm": "assets/map-alm.jpg",
  "map-ing": "assets/map-ing.jpg",
  "map-ukr": "assets/map-ukr.jpg",
  "map-irn": "assets/map-irn.jpg",
};

const NODE_TINT = {
  maden: "#b08958",
  petrol: "#3a2c22",
  komur: "#141414",
  gaz: "#8ec8d4",
  tahil: "#d2b85a",
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
  ctx.drawImage(view.terrain, 0, 0, state.mapW, state.mapH);
  if (gfx.decor) drawDecor(ctx, state);
  drawNodes(ctx, state, gfx);
  drawGroundMarks(ctx, state, view);
  drawEntities(ctx, state, view, gfx);
  drawFog(ctx, state, view);
  drawBarsAndRings(ctx, state, view);
  drawGhost(ctx, state, view);
  drawFx(ctx, view);
  if (view.box) drawBox(ctx, view.box);
}

function ensureTerrain(view, state, gfx) {
  const key = state.terrainKey || "terrain";
  const photo = images[key] || images.terrain;
  const photoReady = !!(photo && photo.complete && photo.naturalWidth);
  if (photo && !photo.complete && !photo.dataset.hooked) {
    photo.dataset.hooked = "1";
    photo.addEventListener("load", () => {
      view.terrain = null;
    });
  }
  const scale = gfx.terrainScale || 0.5;
  if (
    view.terrain &&
    view.terrainKey === key &&
    view.terrainSeed === state.seed &&
    view.terrainPhoto === photoReady &&
    view.terrainScale === scale
  ) {
    return;
  }
  const c = document.createElement("canvas");
  c.width = Math.max(1, Math.floor(state.mapW * scale));
  c.height = Math.max(1, Math.floor(state.mapH * scale));
  const g = c.getContext("2d");
  g.scale(scale, scale);
  if (photoReady) {
    g.drawImage(photo, 0, 0, state.mapW, state.mapH);
  } else {
    g.fillStyle = "#6d6248";
    g.fillRect(0, 0, state.mapW, state.mapH);
  }
  const shade = g.createLinearGradient(0, 0, 0, state.mapH);
  shade.addColorStop(0, "rgba(12,16,10,0.18)");
  shade.addColorStop(0.5, "rgba(0,0,0,0)");
  shade.addColorStop(1, "rgba(8,10,8,0.22)");
  g.fillStyle = shade;
  g.fillRect(0, 0, state.mapW, state.mapH);
  for (const rock of state.obstacles) paintObstacle(g, rock);
  view.terrain = c;
  view.terrainKey = key;
  view.terrainSeed = state.seed;
  view.terrainPhoto = photoReady;
  view.terrainScale = scale;
}

function paintObstacle(g, rock) {
  if (rock.kind === "water") {
    g.fillStyle = "rgba(28, 78, 112, 0.5)";
    g.strokeStyle = "rgba(190, 220, 230, 0.45)";
  } else if (rock.kind === "forest") {
    g.fillStyle = "rgba(22, 52, 28, 0.42)";
    g.strokeStyle = "rgba(16, 36, 18, 0.55)";
  } else {
    g.fillStyle = "rgba(62, 54, 46, 0.28)";
    g.strokeStyle = "rgba(210, 190, 160, 0.28)";
  }
  g.beginPath();
  g.ellipse(rock.x, rock.y, rock.r * 0.92, rock.r * 0.58, 0, 0, Math.PI * 2);
  g.fill();
  g.lineWidth = 1.5;
  g.stroke();
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
    const size = 36 * (gfx.nodeScale || 1);
    ctx.save();
    ctx.translate(n.x, n.y);
    ctx.globalAlpha = left > 0 ? 1 : 0.4;
    if (!drawSprite(ctx, "fuel", size, NODE_TINT[n.kind] || null)) {
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
    if (e.owner !== viewer(view) && !canSeeEntity(state, viewer(view), e)) continue;
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
  const size = def.radius * 2.65;
  if (gfx.shadows) {
    ctx.fillStyle = "rgba(0,0,0,0.28)";
    ctx.beginPath();
    ctx.ellipse(2, size * 0.18, size * 0.34, size * 0.12, 0, 0, Math.PI * 2);
    ctx.fill();
  }
  const grow = e.constructed ? 1 : Math.max(0.2, Math.min(1, e.progress));
  ctx.save();
  ctx.scale(1, grow);
  ctx.translate(0, (1 - grow) * def.radius * 0.4);
  const tint = colors.wash;
  if (!drawSprite(ctx, def.sprite, size, tint)) {
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-def.radius, -def.radius * 0.7, def.radius * 2, def.radius * 1.4);
  }
  ctx.restore();
  if (!e.constructed) {
    ctx.strokeStyle = "rgba(214, 196, 140, 0.9)";
    ctx.lineWidth = 1.6;
    ctx.strokeRect(-def.radius, -def.radius * 0.7, def.radius * 2, def.radius * 1.4);
    ctx.fillStyle = "#d7e8b0";
    ctx.font = "13px sans-serif";
    ctx.textAlign = "center";
    ctx.fillText(`İnşa %${Math.floor(e.progress * 100)}`, 0, -def.radius - 14);
    ctx.fillStyle = "rgba(8,10,8,0.75)";
    ctx.fillRect(-18, def.radius * 0.2, 36, 5);
    ctx.fillStyle = "#d7c48a";
    ctx.fillRect(-18, def.radius * 0.2, 36 * grow, 5);
  }
}

function drawUnit(ctx, e, def, colors, gfx) {
  const size = def.radius * (def.air ? 3.15 : def.worker ? 2.45 : def.naval ? 2.7 : 2.15);
  if (gfx.decor && e.plan && (e.plan.kind === "move" || e.plan.kind === "attackMove") && !def.air) {
    ctx.translate(0, Math.sin((e.x + e.y) * 0.35) * 1.1);
  }
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

function viewer(view) {
  return view.viewer ?? 0;
}

function drawFog(ctx, state, view) {
  const { cols, rows } = state;
  const who = viewer(view);
  if (!view.fogCanvas || view.fogCanvas.width !== cols || view.fogCanvas.height !== rows) {
    view.fogCanvas = document.createElement("canvas");
    view.fogCanvas.width = cols;
    view.fogCanvas.height = rows;
    view.fogImg = new ImageData(cols, rows);
  }
  const data = view.fogImg.data;
  const vis = state.visible[who];
  const exp = state.explored[who];
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
  ctx.drawImage(view.fogCanvas, 0, 0, state.mapW, state.mapH);
}

function drawOrderLine(ctx, x1, y1, x2, y2, color) {
  ctx.strokeStyle = color;
  ctx.lineWidth = 1.6;
  ctx.setLineDash([7, 5]);
  ctx.beginPath();
  ctx.moveTo(x1, y1);
  ctx.lineTo(x2, y2);
  ctx.stroke();
  ctx.setLineDash([]);
}

function drawFlag(ctx, x, y, color) {
  ctx.strokeStyle = color;
  ctx.fillStyle = color;
  ctx.lineWidth = 1.5;
  ctx.beginPath();
  ctx.moveTo(x, y);
  ctx.lineTo(x, y - 16);
  ctx.stroke();
  ctx.beginPath();
  ctx.moveTo(x, y - 16);
  ctx.lineTo(x + 10, y - 12);
  ctx.lineTo(x, y - 8);
  ctx.fill();
}

function drawGroundMarks(ctx, state, view) {
  const who = viewer(view);
  for (const e of state.entities) {
    if (e.hp <= 0 || !view.selected?.has(e.id)) continue;
    if (e.owner !== who && !canSeeEntity(state, who, e)) continue;
    const rx = e.radius * (e.isBuilding ? 1.25 : 1.05);
    ctx.strokeStyle = e.owner === who ? "rgba(126, 196, 92, 0.95)" : "rgba(196, 92, 60, 0.95)";
    ctx.lineWidth = 2.2;
    ctx.beginPath();
    ctx.ellipse(e.x, e.y + rx * 0.08, rx, rx * 0.4, 0, 0, Math.PI * 2);
    ctx.stroke();
    const arm = Math.max(6, rx * 0.55);
    ctx.beginPath();
    ctx.moveTo(e.x - rx, e.y - arm);
    ctx.lineTo(e.x - rx, e.y - rx * 0.15);
    ctx.lineTo(e.x - rx + arm, e.y - rx * 0.15);
    ctx.moveTo(e.x + rx, e.y - arm);
    ctx.lineTo(e.x + rx, e.y - rx * 0.15);
    ctx.lineTo(e.x + rx - arm, e.y - rx * 0.15);
    ctx.moveTo(e.x - rx, e.y + arm);
    ctx.lineTo(e.x - rx, e.y + rx * 0.35);
    ctx.lineTo(e.x - rx + arm, e.y + rx * 0.35);
    ctx.moveTo(e.x + rx, e.y + arm);
    ctx.lineTo(e.x + rx, e.y + rx * 0.35);
    ctx.lineTo(e.x + rx - arm, e.y + rx * 0.35);
    ctx.stroke();
  }
  for (const e of state.entities) {
    if (e.hp <= 0 || e.owner !== who || !view.selected?.has(e.id)) continue;
    if (e.isBuilding && e.constructed && e.rallyX) {
      drawOrderLine(ctx, e.x, e.y, e.rallyX, e.rallyY, "#7ec45c");
      drawFlag(ctx, e.rallyX, e.rallyY, "#7ec45c");
    }
    if (!e.isBuilding && (e.plan?.kind === "move" || e.plan?.kind === "attackMove") && e.plan.x) {
      drawOrderLine(ctx, e.x, e.y, e.plan.x, e.plan.y, "rgba(214, 196, 140, 0.85)");
    }
  }
  for (const mark of view.scorches || []) {
    ctx.fillStyle = "rgba(20, 16, 12, 0.45)";
    ctx.beginPath();
    ctx.ellipse(mark.x, mark.y, 14, 7, 0, 0, Math.PI * 2);
    ctx.fill();
  }
  const ping = view.ping;
  if (ping && ping.life > 0) {
    const t = ping.life / ping.max;
    ctx.strokeStyle = `rgba(188, 214, 120, ${t})`;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(ping.x, ping.y, 6 + (1 - t) * 16, 0, Math.PI * 2);
    ctx.moveTo(ping.x - 16, ping.y);
    ctx.lineTo(ping.x - 5, ping.y);
    ctx.moveTo(ping.x + 5, ping.y);
    ctx.lineTo(ping.x + 16, ping.y);
    ctx.moveTo(ping.x, ping.y - 16);
    ctx.lineTo(ping.x, ping.y - 5);
    ctx.moveTo(ping.x, ping.y + 5);
    ctx.lineTo(ping.x, ping.y + 16);
    ctx.stroke();
  }
}

function drawBarsAndRings(ctx, state, view) {
  const selected = view.selected;
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    const who = viewer(view);
    const own = e.owner === who;
    const seen = own || canSeeEntity(state, who, e);
    if (!seen) continue;
    const visibleNow = own || cellVisible(state, who, e.x, e.y) || (e.isBuilding && cellExplored(state, who, e.x, e.y));
    if (!visibleNow && !own) continue;
    const isSel = selected.has(e.id);
    const showBar = isSel || e.hp < e.maxHp - 0.5;
    if (!showBar) continue;
    if (!own && e.isBuilding && !cellVisible(state, who, e.x, e.y)) continue;
    const w = Math.max(18, e.radius * 2);
    const x = e.x - w / 2;
    const y = e.y - e.radius - 10;
    ctx.fillStyle = "rgba(8,10,8,0.8)";
    ctx.fillRect(x, y, w, 3);
    ctx.fillStyle = e.hp / e.maxHp > 0.35 ? "#b7c48a" : "#c45c3c";
    ctx.fillRect(x, y, w * Math.max(0, e.hp / e.maxHp), 3);
  }

  if (view.rally) {
    drawOrderLine(ctx, view.rally.fromX, view.rally.fromY, view.rally.x, view.rally.y, "#7ec45c");
    drawFlag(ctx, view.rally.x, view.rally.y, "#7ec45c");
  }
}

function drawGhost(ctx, state, view) {
  const ghost = view.ghost;
  if (!ghost) return;
  const fac = faction(state.players[viewer(view)].faction);
  const def = fac.buildings[ghost.kind];
  if (!def) return;
  const worker = state.entities.find((e) => e.id === ghost.workerId);
  if (worker) drawOrderLine(ctx, worker.x, worker.y, ghost.x, ghost.y, ghost.ok ? "#b7c48a" : "#c45c3c");
  const anchor = state.entities.find((e) => e.owner === viewer(view) && e.kind === "ocak" && e.hp > 0);
  if (anchor) {
    ctx.fillStyle = ghost.ok ? "rgba(143, 191, 106, 0.55)" : "rgba(196, 92, 60, 0.45)";
    for (let x = anchor.x - 280; x <= anchor.x + 280; x += 52) {
      for (let y = anchor.y - 280; y <= anchor.y + 280; y += 52) {
        if (Math.hypot(x - anchor.x, y - anchor.y) > 290) continue;
        ctx.fillRect(x - 1.5, y - 1.5, 3, 3);
      }
    }
  }
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
      ctx.strokeStyle = fx.air ? "#f4fbff" : fx.heavy ? "#ffe08a" : "#fff4cf";
      ctx.lineWidth = (fx.heavy ? 2.8 : 1.8) / view.zoom;
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
  const who = viewer(view);
  const sx = w / state.mapW;
  const sy = h / state.mapH;
  ctx.fillStyle = "rgba(6,10,8,0.78)";
  const cellW = (state.mapW / state.cols) * sx;
  const cellH = (state.mapH / state.rows) * sy;
  for (let cy = 0; cy < state.rows; cy++) {
    for (let cx = 0; cx < state.cols; cx++) {
      const i = cy * state.cols + cx;
      if (state.visible[who][i]) continue;
      ctx.globalAlpha = state.explored[who][i] ? 0.45 : 0.88;
      ctx.fillRect(cx * cellW, cy * cellH, cellW + 0.5, cellH + 0.5);
    }
  }
  ctx.globalAlpha = 1;
  for (const n of state.nodes) {
    if (!cellExplored(state, who, n.x, n.y) || n.amount <= 0) continue;
    ctx.fillStyle = "#c6a15a";
    ctx.fillRect(n.x * sx - 2, n.y * sy - 2, 4, 4);
  }
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    if (e.owner !== who && !canSeeEntity(state, who, e)) continue;
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
