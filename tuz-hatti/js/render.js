/** Dünya çizimi. Birlik ve üsler haritanın perspektifiyle aynı izometrik gövdelerdir. */

import { faction } from "./balance.js";
import { GROUND_A, GROUND_B, applyGround, drawIso, screenPoint, worldPoint } from "./iso.js";
import { canSeeEntity, cellExplored, cellVisible, entityDef } from "./sim.js";

export const SPRITE_SRC = {
  "iso-tank-sw": "assets/iso-tank-sw.png",
  "iso-truck-sw": "assets/iso-truck-sw.png",
  "iso-infantry-sw": "assets/iso-infantry-sw.png",
  "iso-hq-iso": "assets/iso-hq-iso.png",
  "iso-barracks-iso": "assets/iso-barracks-iso.png",
  "iso-shop-iso": "assets/iso-shop-iso.png",
  "iso-jet-sw": "assets/iso-jet-sw.png",
  "iso-heli-sw": "assets/iso-heli-sw.png",
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
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.fillStyle = "#2c2820";
  ctx.fillRect(0, 0, ctx.canvas.width, ctx.canvas.height);
  ctx.save();
  applyGround(ctx, view);
  ctx.imageSmoothingEnabled = !!gfx.smoothing;
  ctx.drawImage(view.terrain, 0, 0, state.mapW, state.mapH);
  if (gfx.decor) drawDecor(ctx, state);
  drawContacts(ctx, state, view);
  drawGroundFx(ctx, view);
  drawGroundMarks(ctx, state, view);
  drawFog(ctx, state, view);
  drawGhostPlan(ctx, state, view);
  if (view.box) drawBox(ctx, view.box);
  ctx.restore();
  drawUpright(ctx, state, view, gfx);
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
    g.fillStyle = "rgba(92, 68, 36, 0.16)";
    g.fillRect(0, 0, state.mapW, state.mapH);
  } else {
    g.fillStyle = "#6d6248";
    g.fillRect(0, 0, state.mapW, state.mapH);
  }
  const shade = g.createLinearGradient(0, 0, 0, state.mapH);
  shade.addColorStop(0, "rgba(12,16,10,0.08)");
  shade.addColorStop(0.5, "rgba(0,0,0,0)");
  shade.addColorStop(1, "rgba(8,10,8,0.1)");
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
    ctx.fillStyle = "rgba(74, 108, 58, 0.22)";
    ctx.beginPath();
    ctx.ellipse(d.x, d.y, d.r * 0.42, d.r * 0.2, 0, 0, Math.PI * 2);
    ctx.fill();
  }
}

function drawUpright(ctx, state, view, gfx) {
  drawTrails(ctx, view);
  for (const rock of state.obstacles) {
    if (rock.kind === "water") continue;
    withModel(ctx, view, rock.x, rock.y, () => {
      const forest = rock.kind === "forest";
      drawIso(ctx, {
        form: forest ? "tree" : "peak",
        radius: rock.r * (forest ? 0.42 : 0.55),
        angle: 0.4,
        paint: forest ? "#2f6a38" : "#8a8074",
        dark: "#3a3228",
        cloth: "#d9d3c6",
        detail: !!gfx.decor,
        grow: 1,
      });
    });
  }
  for (const n of state.nodes) {
    const left = n.amount / n.max;
    withModel(ctx, view, n.x, n.y, () => {
      ctx.globalAlpha = left > 0 ? 1 : 0.35;
      drawIso(ctx, {
        form: "node",
        key: n.kind,
        radius: 16 * (gfx.nodeScale || 1),
        angle: 0.5,
        paint: NODE_TINT[n.kind] || "#8a7040",
        dark: "#2a241c",
        cloth: "#d7d2c4",
        detail: !!gfx.decor,
        grow: 0.45 + 0.55 * left,
      });
      ctx.globalAlpha = 1;
    });
  }
  drawEntities(ctx, state, view, gfx);
  drawGhostModel(ctx, state, view);
  drawBars(ctx, state, view);
  drawFx(ctx, view);
}

function withModel(ctx, view, x, y, draw) {
  const p = screenPoint(view, x, y);
  ctx.save();
  ctx.setTransform(view.zoom, 0, 0, view.zoom, p.x, p.y);
  draw();
  ctx.restore();
}

function drawEntities(ctx, state, view, gfx) {
  const list = state.entities.filter((e) => e.hp > 0);
  list.sort((a, b) => {
    const layer = (e) => (e.isBuilding ? 0 : entityDef(state, e).air ? 2 : 1);
    return layer(a) - layer(b) || a.x + a.y - (b.x + b.y);
  });
  for (const e of list) {
    if (e.owner !== viewer(view) && !canSeeEntity(state, viewer(view), e)) continue;
    const colors = faction(state.players[e.owner].faction).colors;
    const def = entityDef(state, e);
    withModel(ctx, view, e.x, e.y, () => {
      ctx.globalAlpha = e.isBuilding && !e.constructed ? 0.72 : 1;
      if (e.isBuilding) drawBuilding(ctx, e, def, colors, gfx);
      else drawUnit(ctx, e, def, colors, gfx, state, view);
      if (view.selected?.has(e.id)) {
        const ring = def.radius * (e.isBuilding ? 1.7 : 2.4);
        ctx.strokeStyle = e.owner === viewer(view) ? "rgba(126, 196, 92, 0.95)" : "rgba(196, 92, 60, 0.95)";
        ctx.lineWidth = 2.2;
        ctx.beginPath();
        ctx.ellipse(0, 2, ring, Math.max(5, ring * 0.2), 0, 0, Math.PI * 2);
        ctx.stroke();
      }
    });
  }
}

function angDiff(a, b) {
  let d = a - b;
  while (d > Math.PI) d -= Math.PI * 2;
  while (d < -Math.PI) d += Math.PI * 2;
  return d;
}

function drawContacts(ctx, state, view) {
  const who = viewer(view);
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    if (e.owner !== who && !canSeeEntity(state, who, e)) continue;
    const def = entityDef(state, e);
    if (def.air) {
      ctx.fillStyle = "rgba(0,0,0,0.28)";
      ctx.beginPath();
      ctx.ellipse(e.x, e.y, e.radius * 1.1, e.radius * 0.55, 0, 0, Math.PI * 2);
      ctx.fill();
      continue;
    }
    if (e.isBuilding || (e.drive || 0) < 0.45) continue;
    const bx = e.x - Math.cos(e.angle) * e.radius * 0.85;
    const by = e.y - Math.sin(e.angle) * e.radius * 0.85;
    ctx.strokeStyle = "rgba(36, 28, 18, 0.45)";
    ctx.lineWidth = e.radius > 14 ? 2.2 : 1.3;
    ctx.beginPath();
    ctx.moveTo(e.x, e.y);
    ctx.lineTo(bx, by);
    ctx.stroke();
  }
}

function footShadow(ctx, width) {
  ctx.fillStyle = "rgba(20, 16, 12, 0.32)";
  ctx.beginPath();
  ctx.ellipse(0, 1, width * 0.26, Math.max(3, width * 0.04), 0, 0, Math.PI * 2);
  ctx.fill();
}

function drawGroundFx(ctx, view) {
  for (const fx of view.fx || []) {
    if (fx.type !== "puff") continue;
    const t = 1 - fx.life / fx.max;
    ctx.globalAlpha = Math.max(0, (fx.life / fx.max) * 0.75);
    ctx.fillStyle = fx.color;
    ctx.beginPath();
    ctx.ellipse(fx.x, fx.y, 4 + t * 12, 2.4 + t * 6, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.globalAlpha = 1;
  }
}

const BUILDING_PHOTO = {
  hq: "iso-hq-iso",
  barracks: "iso-barracks-iso",
  shop: "iso-shop-iso",
  strip: "iso-shop-iso",
  depot: "iso-barracks-iso",
  bunker: "iso-hq-iso",
  yard: "iso-shop-iso",
};

const BUILDING_SPAN = {
  hq: 3.15,
  barracks: 3.55,
  shop: 3.35,
  strip: 3.7,
  depot: 3.2,
  bunker: 2.7,
  yard: 3.3,
};

const BUILDING_SINK = {
  hq: 0.18,
  barracks: 0.16,
  shop: 0.18,
  strip: 0.18,
  depot: 0.16,
  bunker: 0.18,
  yard: 0.18,
};

const TANK_FRAMES = [{ key: "iso-tank-sw", ang: Math.PI * 0.78 }];

const UNIT_FRAMES = {
  tank: TANK_FRAMES,
  sph: TANK_FRAMES,
  mlrs: TANK_FRAMES,
  ifv: TANK_FRAMES,
  atgm: TANK_FRAMES,
  truck: [{ key: "iso-truck-sw", ang: Math.PI * 0.7 }],
  infantry: [{ key: "iso-infantry-sw", ang: Math.PI * 0.75 }],
  heli: [{ key: "iso-heli-sw", ang: Math.PI * 0.62 }],
  jet: [{ key: "iso-jet-sw", ang: Math.PI * 0.5 }],
  wing: [{ key: "iso-jet-sw", ang: Math.PI * 0.5 }],
  drone: [{ key: "iso-jet-sw", ang: Math.PI * 0.5 }],
  quad: [{ key: "iso-heli-sw", ang: Math.PI * 0.62 }],
};

const UNIT_SPAN = {
  tank: 5.6,
  sph: 5.4,
  mlrs: 5.3,
  ifv: 5.1,
  atgm: 4.8,
  truck: 5.15,
  infantry: 3.7,
  heli: 6.2,
  jet: 6.4,
  wing: 5.8,
  drone: 5.2,
  quad: 4.4,
};

function screenHeading(angle) {
  const c = Math.cos(angle);
  const s = Math.sin(angle);
  return Math.atan2((c + s) * GROUND_B, (c - s) * GROUND_A);
}

function pickFrame(frames, heading, hold) {
  let best = frames[0];
  let bestFlip = false;
  let bestD = 99;
  let holdD = null;
  for (const frame of frames) {
    for (const flip of [false, true]) {
      const ang = flip ? Math.PI - frame.ang : frame.ang;
      const d = Math.abs(angDiff(heading, ang));
      if (hold && hold.key === frame.key && hold.flip === flip) holdD = d;
      if (d < bestD) {
        bestD = d;
        best = frame;
        bestFlip = flip;
      }
    }
  }
  if (hold && holdD != null && holdD < bestD + 0.55) {
    best = frames.find((frame) => frame.key === hold.key) || best;
    bestFlip = hold.flip;
  }
  return { key: best.key, flip: bestFlip, ang: bestFlip ? Math.PI - best.ang : best.ang };
}

function ready(img) {
  return img && img.complete && img.naturalWidth > 0;
}

function drawAnchored(ctx, key, width, opt = {}) {
  const img = images[key];
  if (!ready(img)) return false;
  const aspect = img.naturalWidth / Math.max(1, img.naturalHeight);
  const dw = width;
  const dh = width / aspect;
  const foot = opt.foot != null ? opt.foot : dh * (opt.sink ?? 0.04);
  ctx.save();
  if (opt.lift) ctx.translate(0, -opt.lift);
  if (opt.flip) ctx.scale(-1, 1);
  if (opt.clipGround) {
    ctx.beginPath();
    ctx.rect(-dw * 1.4, -dh * 4, dw * 2.8, dh * 4);
    ctx.clip();
  }
  if (opt.grow != null && opt.grow < 0.995) {
    const top = -dh + foot;
    ctx.beginPath();
    ctx.rect(-dw / 2 - 2, top + dh * (1 - opt.grow), dw + 4, dh * opt.grow + 4);
    ctx.clip();
  }
  ctx.drawImage(img, -dw / 2, -dh + foot, dw, dh);
  ctx.restore();
  return true;
}

function paintStructure(ctx, def, colors, gfx, grow) {
  const shape = def.shape || "depot";
  const span = def.radius * (BUILDING_SPAN[shape] || 3.1);
  const key = BUILDING_PHOTO[shape];
  if (key && images[key]) {
    footShadow(ctx, span * 0.92);
    if (drawAnchored(ctx, key, span, { grow, sink: BUILDING_SINK[shape] || 0.32, clipGround: true })) return span * 0.62;
  }
  drawIso(ctx, {
    form: shape,
    radius: def.radius * 1.15,
    angle: 0.55,
    paint: colors.main,
    dark: colors.dark,
    cloth: colors.cloth,
    detail: gfx.decor !== false,
    grow,
  });
  return def.radius * 1.5;
}

function drawBuilding(ctx, e, def, colors, gfx) {
  const grow = e.constructed ? 1 : Math.max(0.18, Math.min(1, e.progress));
  if ((e.kick || 0) > 0.02) e.kick *= 0.84;
  const span = paintStructure(ctx, def, colors, gfx, grow);
  if ((e.kick || 0) > 0.2) {
    ctx.fillStyle = `rgba(255, 214, 120, ${e.kick})`;
    ctx.beginPath();
    ctx.arc(def.radius * 0.15, -span * 0.28, 7 + e.kick * 8, 0, Math.PI * 2);
    ctx.fill();
  }
  if (!e.constructed) {
    ctx.fillStyle = "#d7e8b0";
    ctx.font = "13px sans-serif";
    ctx.textAlign = "center";
    ctx.fillText(`İnşa %${Math.floor(e.progress * 100)}`, 0, -span * 0.72);
    ctx.fillStyle = "rgba(8,10,8,0.75)";
    ctx.fillRect(-22, span * 0.08, 44, 5);
    ctx.fillStyle = "#d7c48a";
    ctx.fillRect(-22, span * 0.08, 44 * grow, 5);
  }
}

function unitForm(def) {
  if (def.air) {
    if (def.sprite === "apache") return "heli";
    if (def.sprite === "f35" || def.sprite === "typhoon") return "jet";
    if (def.sprite === "gj11" || def.sprite === "kizilelma" || def.sprite === "shahed") return "wing";
    if (def.sprite === "fpv") return "quad";
    return "drone";
  }
  if (def.naval) return "ship";
  if (def.worker) return "truck";
  if (def.sprite === "infantry") return "infantry";
  if (def.sprite === "ifv") return "ifv";
  if (def.sprite === "atgm") return "atgm";
  if (def.sprite === "spg") {
    const name = def.name || "";
    if (/HIMARS|PHL|Tornado/i.test(name)) return "mlrs";
    return "sph";
  }
  return "tank";
}

function poseOf(gfx, e, aim, dt, articulated) {
  const map = gfx._motion || (gfx._motion = new Map());
  const prev = map.get(e.id) || { x: e.x, y: e.y, track: e.id, turret: e.angle, hull: e.angle };
  const dist = Math.hypot(e.x - prev.x, e.y - prev.y);
  let turret = prev.turret;
  if (articulated) {
    const gap = angDiff(aim, turret);
    const step = Math.max(0.016, dt || 0.016) * 3.1;
    turret += Math.abs(gap) < step ? gap : Math.sign(gap) * step;
  } else {
    turret = e.angle;
  }
  const pose = {
    x: e.x,
    y: e.y,
    track: prev.track + dist,
    turret,
    hull: e.angle,
    prevHull: prev.hull,
    dist,
    frame: prev.frame,
  };
  map.set(e.id, pose);
  return pose;
}

function noteTrail(view, e, def, dist) {
  if (!def.air || dist < 1.2) return;
  const map = view._trails || (view._trails = new Map());
  const pts = map.get(e.id) || [];
  const last = pts[pts.length - 1];
  if (!last || Math.hypot(e.x - last.x, e.y - last.y) > 10) pts.push({ x: e.x, y: e.y });
  while (pts.length > 18) pts.shift();
  map.set(e.id, pts);
}

function drawTrails(ctx, view) {
  const trails = view._trails;
  if (!trails) return;
  ctx.save();
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  ctx.lineCap = "round";
  ctx.strokeStyle = "rgba(226, 230, 232, 0.42)";
  ctx.lineWidth = Math.max(1.4, view.zoom * 1.6);
  for (const pts of trails.values()) {
    if (pts.length < 2) continue;
    ctx.beginPath();
    pts.forEach((p, index) => {
      const s = screenPoint(view, p.x, p.y);
      s.y -= 26 * view.zoom;
      if (index) ctx.lineTo(s.x, s.y);
      else ctx.moveTo(s.x, s.y);
    });
    ctx.stroke();
  }
  ctx.restore();
}

function drawUnit(ctx, e, def, colors, gfx, state, view) {
  let aim = e.angle;
  if (e.plan?.targetId) {
    const target = state.entities.find((item) => item.id === e.plan.targetId);
    if (target) aim = Math.atan2(target.y - e.y, target.x - e.x);
  }
  const form = unitForm(def);
  const articulated = form === "tank" || form === "sph" || form === "ifv" || form === "atgm" || form === "mlrs";
  const pose = poseOf(gfx, e, aim, view.dt || 0.016, articulated);
  noteTrail(view, e, def, pose.dist);
  const walking = def.sprite === "infantry" && pose.dist > 0.15;
  const phase = def.air ? state.time * 16 + e.id : walking ? state.time * 8 + e.id : pose.track * 0.22;
  const bank = def.air ? Math.max(-0.4, Math.min(0.4, angDiff(e.angle, pose.prevHull) * -8)) : 0;
  const kick = e.kick || 0;
  if (kick > 0.02) e.kick = kick * 0.82;
  const lift = def.air ? 36 + Math.sin(state.time * 2.2 + e.id) * 1.4 : 0;
  const frames = UNIT_FRAMES[form];
  if (frames) {
    const shown = articulated ? pose.turret : e.angle;
    const heading = screenHeading(shown);
    const picked = pickFrame(frames, heading, pose.frame);
    pose.frame = { key: picked.key, flip: picked.flip };
    const span = def.radius * (UNIT_SPAN[form] || 5);
    ctx.save();
    if (!def.air) footShadow(ctx, span * 0.9);
    if (kick > 0) ctx.translate(-Math.cos(heading) * kick * 7, -Math.sin(heading) * kick * 3);
    const drawn = drawAnchored(ctx, picked.key, span, {
      flip: picked.flip,
      lift,
      sink: def.air ? 0.02 : 0.07,
      clipGround: !def.air,
    });
    if (drawn && (def.damage || 0) > 0 && kick > 0.2) {
      ctx.fillStyle = kick > 0.65 ? "#fff1c4" : "rgba(255, 170, 60, 0.9)";
      ctx.beginPath();
      ctx.arc(Math.cos(heading) * span * 0.42, Math.sin(heading) * span * 0.16 - span * 0.12, span * 0.06, 0, Math.PI * 2);
      ctx.fill();
    }
    ctx.restore();
    if (drawn) return;
  }
  drawIso(ctx, {
    form,
    key: def.sprite,
    name: def.name,
    radius: def.radius * (def.air ? 2.15 : def.naval ? 1.9 : def.worker ? 1.85 : def.sprite === "infantry" ? 1.7 : 2.05),
    angle: e.angle,
    aim: articulated ? pose.turret : aim,
    paint: colors.main,
    dark: colors.dark,
    cloth: colors.cloth,
    detail: gfx.decor !== false,
    phase,
    lift,
    carry: e.carry > 0,
    bank,
    grow: 1,
    kick,
    drive: e.drive || (pose.dist > 0.4 ? 1 : 0),
  });
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

function drawBars(ctx, state, view) {
  ctx.setTransform(1, 0, 0, 1, 0, 0);
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
    const p = screenPoint(view, e.x, e.y);
    const w = Math.max(22, e.radius * view.zoom * 1.4);
    const x = p.x - w / 2;
    const y = p.y - e.radius * view.zoom * (e.isBuilding ? 1.8 : 1.35);
    ctx.fillStyle = "rgba(8,10,8,0.8)";
    ctx.fillRect(x, y, w, 4);
    ctx.fillStyle = e.hp / e.maxHp > 0.35 ? "#b7c48a" : "#c45c3c";
    ctx.fillRect(x, y, w * Math.max(0, e.hp / e.maxHp), 4);
  }
}

function drawGhostPlan(ctx, state, view) {
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
  ctx.strokeStyle = ghost.ok ? "#b7c48a" : "#c45c3c";
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.arc(ghost.x, ghost.y, def.radius, 0, Math.PI * 2);
  ctx.stroke();
  if (view.rally) {
    drawOrderLine(ctx, view.rally.fromX, view.rally.fromY, view.rally.x, view.rally.y, "#7ec45c");
    drawFlag(ctx, view.rally.x, view.rally.y, "#7ec45c");
  }
}

function drawGhostModel(ctx, state, view) {
  const ghost = view.ghost;
  if (!ghost) return;
  const fac = faction(state.players[viewer(view)].faction);
  const def = fac.buildings[ghost.kind];
  if (!def) return;
  withModel(ctx, view, ghost.x, ghost.y, () => {
    ctx.globalAlpha = 0.55;
    paintStructure(ctx, def, fac.colors, { shadows: true, decor: false }, 0.92);
    ctx.globalAlpha = 1;
  });
}

function drawFx(ctx, view) {
  ctx.setTransform(1, 0, 0, 1, 0, 0);
  for (const fx of view.fx) {
    const t = 1 - fx.life / fx.max;
    ctx.globalAlpha = Math.max(0, fx.life / fx.max);
    if (fx.type === "shot") {
      const a = screenPoint(view, fx.x1, fx.y1);
      const b = screenPoint(view, fx.x2, fx.y2);
      a.y -= fx.air ? 26 : 16;
      const k = Math.min(1, t * 1.2);
      const x = a.x + (b.x - a.x) * k;
      const y = a.y + (b.y - a.y) * k;
      ctx.strokeStyle = fx.heavy ? "rgba(255, 170, 60, 0.55)" : "rgba(255, 244, 200, 0.45)";
      ctx.lineWidth = fx.heavy ? 8 : 5;
      ctx.beginPath();
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(x, y);
      ctx.stroke();
      ctx.strokeStyle = fx.air ? "#f7fbff" : fx.heavy ? "#ffe39a" : "#fffaf0";
      ctx.lineWidth = fx.heavy ? 2.6 : 1.7;
      ctx.beginPath();
      ctx.moveTo(a.x, a.y);
      ctx.lineTo(x, y);
      ctx.stroke();
      ctx.fillStyle = "#fff6d0";
      ctx.beginPath();
      ctx.arc(x, y, fx.heavy ? 5 : 3.2, 0, Math.PI * 2);
      ctx.fill();
    } else if (fx.type === "flash") {
      const p = screenPoint(view, fx.x, fx.y);
      p.y -= fx.air ? 24 : 16;
      ctx.fillStyle = fx.heavy ? "rgba(255, 186, 70, 0.95)" : "rgba(255, 244, 214, 0.95)";
      ctx.beginPath();
      ctx.arc(p.x, p.y, (7 + t * 14) * Math.min(view.zoom, 1.5), 0, Math.PI * 2);
      ctx.fill();
    } else if (fx.type === "hit") {
      const p = screenPoint(view, fx.x, fx.y);
      const radius = (5 + t * 26) * Math.min(view.zoom, 1.7);
      ctx.strokeStyle = fx.heavy ? "#ffb15a" : "#ffe6b0";
      ctx.lineWidth = 2.2;
      ctx.beginPath();
      ctx.arc(p.x, p.y, radius, 0, Math.PI * 2);
      ctx.stroke();
      ctx.fillStyle = fx.heavy ? "rgba(255, 140, 50, 0.35)" : "rgba(255, 220, 140, 0.3)";
      ctx.beginPath();
      ctx.arc(p.x, p.y, radius * 0.45, 0, Math.PI * 2);
      ctx.fill();
    } else if (fx.type === "boom") {
      const p = screenPoint(view, fx.x, fx.y);
      const z = Math.min(view.zoom, 2);
      const radius = (14 + t * (fx.heavy ? 78 : 42)) * z;
      const rise = t * 18 * z;
      ctx.fillStyle = `rgba(40, 32, 28, ${0.45 * (1 - t)})`;
      ctx.beginPath();
      ctx.arc(p.x + 6 * z, p.y - rise - radius * 0.2, radius * 0.55, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = `rgba(255, 120, 30, ${0.85 * (1 - t)})`;
      ctx.beginPath();
      ctx.arc(p.x, p.y - rise * 0.4, radius * 0.48, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = `rgba(255, 220, 120, ${0.95 * (1 - t)})`;
      ctx.beginPath();
      ctx.arc(p.x, p.y - rise * 0.25, radius * 0.22, 0, Math.PI * 2);
      ctx.fill();
      ctx.fillStyle = "#fff6d2";
      ctx.globalAlpha = Math.max(0, (1 - t) * 0.9);
      ctx.beginPath();
      ctx.arc(p.x, p.y - rise * 0.15, radius * 0.1, 0, Math.PI * 2);
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
  const viewW = view.canvasWidth / view.zoom;
  const viewH = view.canvasHeight / view.zoom;
  const corners = [
    [view.camX, view.camY],
    [view.camX + viewW, view.camY],
    [view.camX + viewW, view.camY + viewH],
    [view.camX, view.camY + viewH],
  ].map(([px, py]) => worldPoint(px, py));
  ctx.strokeStyle = "#e7d7a4";
  ctx.lineWidth = 1;
  ctx.beginPath();
  corners.forEach((corner, index) => {
    const x = corner.x * sx;
    const y = corner.y * sy;
    if (index) ctx.lineTo(x, y);
    else ctx.moveTo(x, y);
  });
  ctx.closePath();
  ctx.stroke();
}
