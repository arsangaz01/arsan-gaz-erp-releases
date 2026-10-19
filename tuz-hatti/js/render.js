/** Dünya çizimi. Arazi bir kez üretilir, sis her karede küçük bir ızgaradan büyütülür. */

import { faction } from "./balance.js";
import { MAP_H, MAP_W, canSeeEntity, cellExplored, cellVisible, entityDef } from "./sim.js";

export function drawWorld(ctx, state, view) {
  ensureTerrain(view, state);
  ctx.setTransform(view.zoom, 0, 0, view.zoom, -view.camX * view.zoom, -view.camY * view.zoom);
  ctx.drawImage(view.terrain, 0, 0, MAP_W, MAP_H);
  drawDecor(ctx, state);
  drawNodes(ctx, state);
  drawEntities(ctx, state, view);
  drawFog(ctx, state, view);
  drawBarsAndRings(ctx, state, view);
  drawGhost(ctx, state, view);
  drawFx(ctx, view);
  if (view.box) drawBox(ctx, view.box);
}

function ensureTerrain(view, state) {
  if (view.terrain && view.terrainSeed === state.seed) return;
  const scale = 0.5;
  const c = document.createElement("canvas");
  c.width = Math.floor(MAP_W * scale);
  c.height = Math.floor(MAP_H * scale);
  const g = c.getContext("2d");
  g.scale(scale, scale);
  g.fillStyle = "#e7d3ae";
  g.fillRect(0, 0, MAP_W, MAP_H);

  const rng = mulberry(state.seed + 17);
  for (let i = 0; i < 28; i++) {
    const x = rng() * MAP_W;
    const y = rng() * MAP_H;
    const r = 40 + rng() * 120;
    g.fillStyle = rng() > 0.5 ? "rgba(255,248,230,0.28)" : "rgba(176,142,96,0.18)";
    g.beginPath();
    g.ellipse(x, y, r, r * (0.55 + rng() * 0.3), rng() * 3, 0, Math.PI * 2);
    g.fill();
  }

  g.strokeStyle = "rgba(120,86,48,0.18)";
  g.lineWidth = 2;
  for (let i = 0; i < 18; i++) {
    const x = rng() * MAP_W;
    const y = rng() * MAP_H;
    g.beginPath();
    g.moveTo(x, y);
    g.lineTo(x + 30 + rng() * 50, y + (rng() - 0.5) * 20);
    g.stroke();
  }

  for (const rock of state.obstacles) drawRock(g, rock.x, rock.y, rock.r);

  const grd = g.createLinearGradient(0, 0, 0, MAP_H);
  grd.addColorStop(0, "rgba(90,60,30,0.05)");
  grd.addColorStop(0.5, "rgba(255,255,255,0)");
  grd.addColorStop(1, "rgba(70,48,24,0.08)");
  g.fillStyle = grd;
  g.fillRect(0, 0, MAP_W, MAP_H);

  view.terrain = c;
  view.terrainSeed = state.seed;
}

function drawRock(g, x, y, r) {
  g.fillStyle = "rgba(70,52,36,0.25)";
  g.beginPath();
  g.ellipse(x + 4, y + r * 0.35, r * 0.9, r * 0.35, 0, 0, Math.PI * 2);
  g.fill();
  g.fillStyle = "#b7a48b";
  g.beginPath();
  g.moveTo(x - r, y + r * 0.15);
  g.lineTo(x - r * 0.2, y - r * 0.85);
  g.lineTo(x + r * 0.7, y - r * 0.2);
  g.lineTo(x + r * 0.45, y + r * 0.35);
  g.closePath();
  g.fill();
  g.fillStyle = "#8d7b66";
  g.beginPath();
  g.moveTo(x - r * 0.15, y + r * 0.2);
  g.lineTo(x + r * 0.15, y - r * 0.45);
  g.lineTo(x + r * 0.85, y + r * 0.05);
  g.closePath();
  g.fill();
}

function drawDecor(ctx, state) {
  for (const d of state.decor) {
    if (d.kind === "bush") {
      ctx.fillStyle = "rgba(90,110,62,0.55)";
      ctx.beginPath();
      ctx.arc(d.x, d.y, d.r * 0.45, 0, Math.PI * 2);
      ctx.arc(d.x + d.r * 0.35, d.y + 2, d.r * 0.32, 0, Math.PI * 2);
      ctx.fill();
    }
  }
}

function drawNodes(ctx, state) {
  for (const n of state.nodes) {
    const left = n.amount / n.max;
    ctx.fillStyle = left > 0 ? "#1f8f86" : "#8a8174";
    ctx.beginPath();
    ctx.moveTo(n.x, n.y - 16);
    ctx.lineTo(n.x + 12, n.y - 2);
    ctx.lineTo(n.x + 6, n.y + 14);
    ctx.lineTo(n.x - 8, n.y + 12);
    ctx.lineTo(n.x - 14, n.y - 2);
    ctx.closePath();
    ctx.fill();
    ctx.fillStyle = left > 0 ? "#b7fff4" : "#d9d3c8";
    ctx.beginPath();
    ctx.moveTo(n.x, n.y - 8);
    ctx.lineTo(n.x + 5, n.y);
    ctx.lineTo(n.x, n.y + 7);
    ctx.lineTo(n.x - 5, n.y);
    ctx.closePath();
    ctx.fill();
  }
}

function drawEntities(ctx, state, view) {
  const list = [...state.entities].sort((a, b) => a.y - b.y);
  for (const e of list) {
    if (e.hp <= 0) continue;
    if (e.owner !== 0 && !canSeeEntity(state, 0, e)) continue;
    const colors = faction(state.players[e.owner].faction).colors;
    const def = entityDef(state, e);
    ctx.save();
    ctx.translate(e.x, e.y);
    ctx.globalAlpha = e.isBuilding && !e.constructed ? 0.85 : 1;
    if (e.isBuilding) drawBuilding(ctx, e, def, colors);
    else drawUnit(ctx, e, def, colors);
    ctx.restore();
  }
}

function drawBuilding(ctx, e, def, colors) {
  ctx.fillStyle = "rgba(40,28,16,0.18)";
  ctx.beginPath();
  ctx.ellipse(3, def.radius * 0.45, def.radius * 0.95, def.radius * 0.38, 0, 0, Math.PI * 2);
  ctx.fill();
  const shape = def.shape;
  if (shape === "hq") {
    roundPoly(ctx, 0, 0, def.radius, 8, colors.dark);
    roundPoly(ctx, 0, -4, def.radius * 0.62, 6, colors.main);
    ctx.fillStyle = colors.cloth;
    ctx.fillRect(-6, -def.radius - 8, 12, 16);
  } else if (shape === "barracks") {
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-def.radius, -def.radius * 0.7, def.radius * 2, def.radius * 1.35);
    ctx.fillStyle = colors.main;
    ctx.beginPath();
    ctx.moveTo(-def.radius - 4, -def.radius * 0.7);
    ctx.lineTo(0, -def.radius - 8);
    ctx.lineTo(def.radius + 4, -def.radius * 0.7);
    ctx.closePath();
    ctx.fill();
  } else if (shape === "shop") {
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-def.radius, -def.radius * 0.55, def.radius * 2, def.radius * 1.2);
    ctx.fillStyle = colors.cloth;
    ctx.fillRect(-def.radius * 0.7, -def.radius * 0.2, def.radius * 0.5, def.radius * 0.7);
    ctx.fillStyle = colors.main;
    ctx.fillRect(def.radius * 0.1, -def.radius - 2, 8, def.radius * 0.7);
  } else if (shape === "tower") {
    ctx.fillStyle = colors.dark;
    ctx.beginPath();
    ctx.arc(0, 0, def.radius, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = colors.cloth;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(0, 4);
    ctx.lineTo(Math.cos(e.angle) * (def.radius + 10), Math.sin(e.angle) * (def.radius + 10));
    ctx.stroke();
    ctx.fillStyle = colors.main;
    ctx.beginPath();
    ctx.arc(0, 0, 6, 0, Math.PI * 2);
    ctx.fill();
  } else if (shape === "depot") {
    ctx.fillStyle = colors.main;
    ctx.fillRect(-def.radius, -def.radius * 0.45, def.radius * 2, def.radius);
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-def.radius, -def.radius * 0.45, def.radius * 2, 8);
  }
  if (!e.constructed) {
    ctx.strokeStyle = colors.cloth;
    ctx.lineWidth = 2;
    ctx.setLineDash([4, 3]);
    ctx.strokeRect(-def.radius, -def.radius, def.radius * 2, def.radius * 2);
    ctx.setLineDash([]);
    ctx.fillStyle = "rgba(20,14,8,0.7)";
    ctx.fillRect(-16, -3, 32, 6);
    ctx.fillStyle = colors.cloth;
    ctx.fillRect(-16, -3, 32 * Math.max(0, Math.min(1, e.progress)), 6);
  }
}

function drawUnit(ctx, e, def, colors) {
  ctx.rotate(e.angle);
  ctx.fillStyle = "rgba(40,28,16,0.2)";
  ctx.beginPath();
  ctx.ellipse(2, 8, def.radius * 0.8, def.radius * 0.35, 0, 0, Math.PI * 2);
  ctx.fill();
  const shape = def.shape;
  if (shape === "worker") {
    ctx.fillStyle = colors.main;
    ctx.beginPath();
    ctx.arc(0, 0, 7, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = colors.dark;
    ctx.fillRect(4, -4, 12, 8);
    if (e.carry > 0) {
      ctx.fillStyle = "#1f8f86";
      ctx.fillRect(8, -8, 6, 6);
    }
  } else if (shape === "spear") {
    ctx.fillStyle = colors.cloth;
    ctx.fillRect(-4, -5, 10, 10);
    ctx.strokeStyle = colors.dark;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(2, 0);
    ctx.lineTo(16, 0);
    ctx.stroke();
  } else if (shape === "tank") {
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-14, -9, 28, 18);
    ctx.fillStyle = colors.main;
    ctx.beginPath();
    ctx.arc(2, 0, 7, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillRect(6, -2, 14, 4);
  } else if (shape === "lance") {
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-10, -5, 18, 10);
    ctx.fillStyle = colors.cloth;
    ctx.fillRect(6, -1.5, 16, 3);
  } else if (shape === "scout") {
    ctx.fillStyle = colors.cloth;
    ctx.beginPath();
    ctx.moveTo(12, 0);
    ctx.lineTo(-8, 7);
    ctx.lineTo(-4, 0);
    ctx.lineTo(-8, -7);
    ctx.closePath();
    ctx.fill();
  } else if (shape === "buggy") {
    ctx.fillStyle = colors.ink;
    ctx.beginPath();
    ctx.arc(-6, 7, 3.5, 0, Math.PI * 2);
    ctx.arc(6, 7, 3.5, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = colors.cloth;
    ctx.beginPath();
    ctx.moveTo(12, 0);
    ctx.lineTo(-8, 6);
    ctx.lineTo(-8, -6);
    ctx.closePath();
    ctx.fill();
  } else if (shape === "mortar") {
    ctx.fillStyle = colors.dark;
    ctx.fillRect(-10, -8, 18, 16);
    ctx.strokeStyle = colors.cloth;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(16, -8);
    ctx.stroke();
  }
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
    const a = vis[i] ? 0 : exp[i] ? 128 : 228;
    const o = i * 4;
    data[o] = 22;
    data[o + 1] = 16;
    data[o + 2] = 12;
    data[o + 3] = a;
  }
  const g = view.fogCanvas.getContext("2d");
  g.putImageData(view.fogImg, 0, 0);
  ctx.imageSmoothingEnabled = false;
  ctx.drawImage(view.fogCanvas, 0, 0, MAP_W, MAP_H);
  ctx.imageSmoothingEnabled = true;
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
      ctx.strokeStyle = "#fff8ea";
      ctx.lineWidth = 2 / view.zoom;
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
    ctx.fillStyle = "rgba(20,12,8,0.75)";
    ctx.fillRect(x, y, w, 4);
    ctx.fillStyle = e.hp / e.maxHp > 0.35 ? "#d6e27a" : "#e07a4a";
    ctx.fillRect(x, y, w * Math.max(0, e.hp / e.maxHp), 4);
  }

  if (view.rally) {
    ctx.strokeStyle = "#fff8ea";
    ctx.lineWidth = 1.5 / view.zoom;
    ctx.beginPath();
    ctx.moveTo(view.rally.x - 6, view.rally.y);
    ctx.lineTo(view.rally.x + 6, view.rally.y);
    ctx.moveTo(view.rally.x, view.rally.y - 8);
    ctx.lineTo(view.rally.x, view.rally.y + 6);
    ctx.stroke();
  }
}

function drawGhost(ctx, state, view) {
  const ghost = view.ghost;
  if (!ghost) return;
  const def = faction(state.players[0].faction).buildings[ghost.kind];
  if (!def) return;
  ctx.save();
  ctx.translate(ghost.x, ghost.y);
  ctx.globalAlpha = 0.55;
  ctx.fillStyle = ghost.ok ? "#d6e27a" : "#e07a4a";
  ctx.beginPath();
  ctx.arc(0, 0, def.radius, 0, Math.PI * 2);
  ctx.fill();
  ctx.restore();
}

function drawFx(ctx, view) {
  for (const fx of view.fx) {
    const t = 1 - fx.life / fx.max;
    ctx.globalAlpha = Math.max(0, fx.life / fx.max);
    if (fx.type === "shot") {
      ctx.strokeStyle = fx.heavy ? "#f2d2bc" : "#fff4d2";
      ctx.lineWidth = (fx.heavy ? 2.4 : 1.4) / view.zoom;
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
  ctx.fillStyle = "rgba(255,248,234,0.12)";
  ctx.strokeStyle = "#fff8ea";
  ctx.lineWidth = 1.5;
  ctx.fillRect(x, y, w, h);
  ctx.strokeRect(x, y, w, h);
}

export function drawMinimap(canvas, state, view) {
  const ctx = canvas.getContext("2d");
  const w = canvas.width;
  const h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.fillStyle = "#cbb892";
  ctx.fillRect(0, 0, w, h);
  const sx = w / MAP_W;
  const sy = h / MAP_H;
  ctx.fillStyle = "rgba(22,16,12,0.72)";
  const cellW = (MAP_W / state.cols) * sx;
  const cellH = (MAP_H / state.rows) * sy;
  for (let cy = 0; cy < state.rows; cy++) {
    for (let cx = 0; cx < state.cols; cx++) {
      const i = cy * state.cols + cx;
      if (state.visible[0][i]) continue;
      ctx.globalAlpha = state.explored[0][i] ? 0.45 : 0.85;
      ctx.fillRect(cx * cellW, cy * cellH, cellW + 0.5, cellH + 0.5);
    }
  }
  ctx.globalAlpha = 1;
  for (const n of state.nodes) {
    if (!cellExplored(state, 0, n.x, n.y) || n.amount <= 0) continue;
    ctx.fillStyle = "#1f8f86";
    ctx.fillRect(n.x * sx - 2, n.y * sy - 2, 4, 4);
  }
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    if (e.owner !== 0 && !canSeeEntity(state, 0, e)) continue;
    ctx.fillStyle = faction(state.players[e.owner].faction).colors.main;
    const r = e.isBuilding ? 3 : 1.6;
    ctx.fillRect(e.x * sx - r / 2, e.y * sy - r / 2, r, r);
  }
  const viewW = canvas.clientWidth ? (view.canvasWidth / view.zoom) * sx : 20;
  const viewH = (view.canvasHeight / view.zoom) * sy;
  ctx.strokeStyle = "#fff8ea";
  ctx.lineWidth = 1;
  ctx.strokeRect(view.camX * sx, view.camY * sy, viewW, viewH);
}

function roundPoly(ctx, x, y, r, sides, color) {
  ctx.fillStyle = color;
  ctx.beginPath();
  for (let i = 0; i < sides; i++) {
    const a = (i / sides) * Math.PI * 2 - Math.PI / 2;
    const px = x + Math.cos(a) * r;
    const py = y + Math.sin(a) * r * 0.82;
    if (i === 0) ctx.moveTo(px, py);
    else ctx.lineTo(px, py);
  }
  ctx.closePath();
  ctx.fill();
}

function mulberry(seed) {
  let a = seed >>> 0;
  return function rng() {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}
