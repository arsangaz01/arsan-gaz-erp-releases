/** İzometrik gövdeler. Fotoğraf kopyası değil; kamuoyundaki gövde, kule ve kanat oranlarının özgün çizimi. */

const IX = 0.9;
const IY = 0.48;

const TANKS = {
  abrams: { L: 2.62, W: 1.22, H: 0.46, tL: 1.02, tW: 0.8, tH: 0.3, tX: 0.16, gun: 1.72, wheels: 7 },
  leo2: { L: 2.5, W: 1.18, H: 0.44, tL: 0.92, tW: 0.76, tH: 0.3, tX: 0.04, gun: 1.68, wheels: 7 },
  ch2: { L: 2.58, W: 1.24, H: 0.5, tL: 1.08, tW: 0.86, tH: 0.36, tX: 0.02, gun: 1.98, wheels: 6 },
  t90: { L: 2.16, W: 1.08, H: 0.38, tL: 0.64, tW: 0.7, tH: 0.26, tX: 0.02, gun: 1.38, wheels: 6, era: 1 },
  t64: { L: 2.04, W: 1.02, H: 0.36, tL: 0.56, tW: 0.64, tH: 0.22, tX: 0, gun: 1.32, wheels: 6 },
  type99: { L: 2.4, W: 1.14, H: 0.42, tL: 0.9, tW: 0.74, tH: 0.28, tX: 0.2, gun: 1.5, wheels: 6, wedge: 1 },
  altay: { L: 2.46, W: 1.16, H: 0.42, tL: 0.88, tW: 0.72, tH: 0.28, tX: 0.1, gun: 1.58, wheels: 7 },
  karrar: { L: 2.12, W: 1.06, H: 0.38, tL: 0.62, tW: 0.68, tH: 0.26, tX: 0, gun: 1.36, wheels: 6, era: 1 },
};

function shade(hex, amt) {
  const raw = String(hex).trim();
  let r;
  let g;
  let b;
  if (raw.startsWith("rgb")) {
    const parts = raw.match(/\d+/g);
    if (!parts) return hex;
    r = Number(parts[0]);
    g = Number(parts[1]);
    b = Number(parts[2]);
  } else {
    const n = Number.parseInt(raw.replace("#", ""), 16);
    if (!Number.isFinite(n)) return hex;
    r = (n >> 16) & 255;
    g = (n >> 8) & 255;
    b = n & 255;
  }
  const toward = amt < 0 ? 0 : 255;
  const k = Math.min(1, Math.abs(amt));
  r = Math.round(r + (toward - r) * k);
  g = Math.round(g + (toward - g) * k);
  b = Math.round(b + (toward - b) * k);
  return `rgb(${r},${g},${b})`;
}

/* Kamera modeli: sapma 0, eğim 37.5°. Zemin yalnızca dikeyde sin(eğim) kadar sıkışır,
   yükseklik cos(eğim) ile görünür. Harita döndürülmez; fotoğraf ve model aynı bakışta kalır. */
const CAMERA_PITCH = (37.5 * Math.PI) / 180;
export const GROUND_A = 1;
export const GROUND_B = Math.sin(CAMERA_PITCH);
const RISE = Math.cos(CAMERA_PITCH);

function project(x, y, z, yaw) {
  const c = Math.cos(yaw);
  const s = Math.sin(yaw);
  const wx = x * c - y * s;
  const wy = x * s + y * c;
  return [wx * GROUND_A, wy * GROUND_B - z * RISE];
}

export function groundPoint(x, y) {
  return { x: x * GROUND_A, y: y * GROUND_B };
}

export function worldPoint(px, py) {
  return { x: px / GROUND_A, y: py / GROUND_B };
}

export function groundSpan(mapW, mapH) {
  const pts = [groundPoint(0, 0), groundPoint(mapW, 0), groundPoint(0, mapH), groundPoint(mapW, mapH)];
  let minX = Infinity;
  let minY = Infinity;
  let maxX = -Infinity;
  let maxY = -Infinity;
  for (const p of pts) {
    minX = Math.min(minX, p.x);
    minY = Math.min(minY, p.y);
    maxX = Math.max(maxX, p.x);
    maxY = Math.max(maxY, p.y);
  }
  return { minX, minY, maxX, maxY };
}

export function applyGround(ctx, view) {
  const z = view.zoom;
  ctx.setTransform(GROUND_A * z, 0, 0, GROUND_B * z, -view.camX * z, -view.camY * z);
}

export function screenPoint(view, x, y) {
  const g = groundPoint(x, y);
  return {
    x: (g.x - view.camX) * view.zoom,
    y: (g.y - view.camY) * view.zoom,
  };
}

function depthOf(x, y, z, yaw) {
  const c = Math.cos(yaw);
  const s = Math.sin(yaw);
  const wy = x * s + y * c;
  return wy + z * 0.65;
}

function lit(nx, ny, nz, spin, yaw) {
  const c = Math.cos(spin);
  const s = Math.sin(spin);
  const sx = nx * c - ny * s;
  const sy = nx * s + ny * c;
  const c2 = Math.cos(yaw);
  const s2 = Math.sin(yaw);
  const rx = sx * c2 - sy * s2;
  const ry = sx * s2 + sy * c2;
  const face = -ry * 0.82 + nz * 0.7;
  const light = -rx * 0.45 - ry * 0.4 + nz * 0.85;
  return { face, light };
}

function addBox(faces, cx, cy, cz, sx, sy, sz, spin, yaw, color) {
  const hx = sx / 2;
  const hy = sy / 2;
  const hz = sz / 2;
  const raw = [
    [-hx, -hy, -hz],
    [hx, -hy, -hz],
    [hx, hy, -hz],
    [-hx, hy, -hz],
    [-hx, -hy, hz],
    [hx, -hy, hz],
    [hx, hy, hz],
    [-hx, hy, hz],
  ];
  const c = Math.cos(spin);
  const s = Math.sin(spin);
  const corners = raw.map(([x, y, z]) => [cx + x * c - y * s, cy + x * s + y * c, cz + z]);
  const quads = [
    { n: [0, 0, 1], idx: [4, 5, 6, 7], bias: 0.38 },
    { n: [0, 0, -1], idx: [0, 3, 2, 1], bias: -0.5 },
    { n: [1, 0, 0], idx: [1, 2, 6, 5], bias: -0.18 },
    { n: [-1, 0, 0], idx: [0, 4, 7, 3], bias: -0.28 },
    { n: [0, 1, 0], idx: [2, 3, 7, 6], bias: -0.34 },
    { n: [0, -1, 0], idx: [0, 1, 5, 4], bias: -0.12 },
  ];
  for (const quad of quads) {
    const sun = lit(quad.n[0], quad.n[1], quad.n[2], spin, yaw);
    let depth = 0;
    const pts = quad.idx.map((i) => {
      depth += depthOf(corners[i][0], corners[i][1], corners[i][2], yaw);
      return project(corners[i][0], corners[i][1], corners[i][2], yaw);
    });
    if (screenArea(pts) <= 0) continue;
    faces.push({
      pts,
      depth,
      color: shade(color, Math.max(-0.58, Math.min(0.46, quad.bias * 0.8 + sun.light * 0.4))),
    });
  }
}

function screenArea(pts) {
  let area = 0;
  for (let i = 0; i < pts.length; i++) {
    const p = pts[i];
    const q = pts[(i + 1) % pts.length];
    area += p[0] * q[1] - q[0] * p[1];
  }
  return area;
}

let camoCanvas = null;

function camoPattern(ctx) {
  if (!camoCanvas && typeof document !== "undefined") {
    camoCanvas = document.createElement("canvas");
    camoCanvas.width = 72;
    camoCanvas.height = 72;
    const g = camoCanvas.getContext("2d");
    g.fillStyle = "#6a6248";
    g.fillRect(0, 0, 72, 72);
    const blobs = [
      ["#4e5840", 8, 10, 22],
      ["#3e4634", 40, 18, 18],
      ["#7a7058", 18, 44, 20],
      ["#2e3428", 50, 46, 16],
      ["#5c5344", 30, 8, 10],
    ];
    for (const [color, x, y, r] of blobs) {
      g.fillStyle = color;
      g.beginPath();
      g.ellipse(x, y, r, r * 0.62, 0.4, 0, Math.PI * 2);
      g.fill();
    }
    g.strokeStyle = "rgba(20,16,12,0.35)";
    g.lineWidth = 1;
    for (let i = 0; i < 6; i++) {
      g.beginPath();
      g.moveTo(0, 8 + i * 12);
      g.lineTo(72, 4 + i * 12);
      g.stroke();
    }
  }
  if (!camoCanvas) return null;
  return ctx.createPattern(camoCanvas, "repeat");
}

function paintFaces(ctx, faces) {
  faces.sort((a, b) => a.depth - b.depth);
  const pattern = camoPattern(ctx);
  for (const face of faces) {
    ctx.beginPath();
    ctx.moveTo(face.pts[0][0], face.pts[0][1]);
    for (let i = 1; i < face.pts.length; i++) ctx.lineTo(face.pts[i][0], face.pts[i][1]);
    ctx.closePath();
    ctx.fillStyle = face.color;
    ctx.fill();
    if (pattern && face.depth > 0) {
      ctx.save();
      ctx.clip();
      ctx.globalAlpha = 0.28;
      ctx.fillStyle = pattern;
      let minX = Infinity;
      let minY = Infinity;
      let maxX = -Infinity;
      let maxY = -Infinity;
      for (const p of face.pts) {
        minX = Math.min(minX, p[0]);
        minY = Math.min(minY, p[1]);
        maxX = Math.max(maxX, p[0]);
        maxY = Math.max(maxY, p[1]);
      }
      ctx.fillRect(minX, minY, maxX - minX, maxY - minY);
      ctx.restore();
    }
  }
}

function addWedge(faces, cx, cy, cz, sx, sy, sz, spin, yaw, color) {
  const hx = sx / 2;
  const hy = sy / 2;
  const hz = sz / 2;
  const raw = [
    [-hx, -hy, -hz],
    [hx, -hy, -hz * 0.15],
    [hx, hy, -hz * 0.15],
    [-hx, hy, -hz],
    [-hx, -hy, hz],
    [hx, -hy, hz * 0.02],
    [hx, hy, hz * 0.02],
    [-hx, hy, hz],
  ];
  const c = Math.cos(spin);
  const s = Math.sin(spin);
  const corners = raw.map(([x, y, z]) => [cx + x * c - y * s, cy + x * s + y * c, cz + z]);
  const quads = [
    { n: [0, 0, 1], idx: [4, 5, 6, 7], bias: 0.34 },
    { n: [0, 0, -1], idx: [0, 3, 2, 1], bias: -0.5 },
    { n: [1, 0, 0], idx: [1, 2, 6, 5], bias: -0.16 },
    { n: [-1, 0, 0], idx: [0, 4, 7, 3], bias: -0.26 },
    { n: [0, 1, 0], idx: [2, 3, 7, 6], bias: -0.32 },
    { n: [0, -1, 0], idx: [0, 1, 5, 4], bias: -0.1 },
  ];
  for (const quad of quads) {
    const sun = lit(quad.n[0], quad.n[1], quad.n[2], spin, yaw);
    let depth = 0;
    const pts = quad.idx.map((i) => {
      depth += depthOf(corners[i][0], corners[i][1], corners[i][2], yaw);
      return project(corners[i][0], corners[i][1], corners[i][2], yaw);
    });
    if (screenArea(pts) <= 0) continue;
    faces.push({
      pts,
      depth,
      color: shade(color, Math.max(-0.58, Math.min(0.46, quad.bias * 0.8 + sun.light * 0.4))),
    });
  }
}

function rotor(ctx, x, y, z, span, yaw, phase, blur) {
  const p0 = project(x, y, z, yaw);
  if (blur) {
    ctx.fillStyle = "rgba(214, 216, 208, 0.3)";
    ctx.beginPath();
    ctx.ellipse(p0[0], p0[1], span * 0.92, span * 0.34, -0.4, 0, Math.PI * 2);
    ctx.fill();
  }
  ctx.strokeStyle = "rgba(236,236,228,0.82)";
  ctx.lineWidth = blur ? 1.15 : 1.5;
  ctx.beginPath();
  const blades = blur ? 3 : 4;
  for (let i = 0; i < blades; i++) {
    const a = phase + (i * Math.PI * 2) / blades;
    const p1 = project(x + Math.cos(a) * span, y + Math.sin(a) * span * 0.4, z, yaw);
    ctx.moveTo(p0[0], p0[1]);
    ctx.lineTo(p1[0], p1[1]);
  }
  ctx.stroke();
}

export function drawIso(ctx, spec) {
  const faces = [];
  const u = spec.radius;
  const yaw = spec.angle || 0;
  const grow = spec.grow == null ? 1 : spec.grow;
  const lift = (spec.lift || 0) + 0;
  const paint = spec.paint || "#6e7a48";
  const dark = spec.dark || "#24281c";
  const cloth = spec.cloth || "#d7d2c4";
  const detail = spec.detail !== false;
  const phase = spec.phase || 0;
  const aim = (spec.aim ?? yaw) - yaw;
  const box = (cx, cy, cz, sx, sy, sz, spin, color) => {
    addBox(
      faces,
      cx * u,
      cy * u,
      cz * u * grow + lift,
      sx * u,
      sy * u,
      Math.max(0.35, sz * u * grow),
      spin || 0,
      yaw,
      color,
    );
  };
  const zLift = lift / Math.max(1, u);
  const decals = [];
  const wheel = (cx, cy, cz, r, spin) => {
    decals.push({ x: cx * u, y: cy * u, z: cz * u * grow + lift, r: r * u, phase: spin || 0 });
  };
  const wedge = (cx, cy, cz, sx, sy, sz, spin, color) => {
    addWedge(
      faces,
      cx * u,
      cy * u,
      cz * u * grow + lift,
      sx * u,
      sy * u,
      Math.max(0.35, sz * u * grow),
      spin || 0,
      yaw,
      color,
    );
  };

  if (spec.form === "tank") tank({ box, wheel, wedge }, spec, paint, dark, cloth, detail, phase, aim);
  else if (spec.form === "sph") sph(box, paint, dark, detail, aim);
  else if (spec.form === "mlrs") mlrs(box, paint, dark, cloth, detail, phase);
  else if (spec.form === "ifv") ifv(box, paint, dark, cloth, detail, aim);
  else if (spec.form === "atgm") atgm(box, paint, dark, cloth, aim);
  else if (spec.form === "truck") truck(box, paint, dark, cloth, detail, spec.carry);
  else if (spec.form === "infantry") infantry(box, paint, dark, cloth, phase);
  else if (spec.form === "heli") heli(box, paint, dark, cloth, detail);
  else if (spec.form === "jet") jet(box, paint, dark, cloth, spec.key, spec.bank || 0);
  else if (spec.form === "wing") wing(box, paint, dark, spec.key);
  else if (spec.form === "drone") drone(box, paint, dark);
  else if (spec.form === "quad") quad(box, dark, cloth, phase);
  else if (spec.form === "ship") ship(box, paint, dark, cloth, spec.name || "");
  else if (spec.form === "node") node(box, spec.key, paint, dark);
  else if (spec.form === "tree") tree(box, dark, paint);
  else if (spec.form === "peak") peak(box, dark, cloth);
  else building(box, spec, paint, dark, cloth, detail, aim);

  paintFaces(ctx, faces);
  paintWheels(ctx, decals, yaw);
  if (spec.form === "heli") rotor(ctx, 0, 0, (zLift + 0.95) * u * grow, u * 1.7, yaw, phase, true);
  if (spec.form === "quad") rotor(ctx, 0, 0, (zLift + 0.55) * u, u * 0.85, yaw, phase * 2, true);
  if (spec.form === "jet" && (spec.drive || 0) > 0.25) {
    const flame = project(-1.05 * u, 0, (zLift + 0.2) * u, yaw);
    ctx.fillStyle = "rgba(255, 170, 70, 0.75)";
    ctx.beginPath();
    ctx.ellipse(flame[0], flame[1], u * 0.16, u * 0.07, 0, 0, Math.PI * 2);
    ctx.fill();
  }
}

function paintWheels(ctx, decals, yaw) {
  for (const d of decals) {
    const p = project(d.x, d.y, d.z, yaw);
    ctx.fillStyle = "#121412";
    ctx.beginPath();
    ctx.ellipse(p[0], p[1], d.r, d.r * 0.7, 0, 0, Math.PI * 2);
    ctx.fill();
    const spin = d.phase || 0;
    ctx.strokeStyle = "#6d6456";
    ctx.lineWidth = Math.max(0.7, d.r * 0.2);
    ctx.beginPath();
    const dx = Math.cos(spin) * d.r * 0.72;
    const dy = Math.sin(spin) * d.r * 0.42;
    ctx.moveTo(p[0] - dx, p[1] - dy);
    ctx.lineTo(p[0] + dx, p[1] + dy);
    ctx.stroke();
    ctx.fillStyle = "#8a7d62";
    ctx.beginPath();
    ctx.ellipse(p[0], p[1], d.r * 0.28, d.r * 0.18, 0, 0, Math.PI * 2);
    ctx.fill();
  }
}

function tank(tools, spec, paint, dark, cloth, detail, phase, aim) {
  const { box, wheel, wedge } = tools;
  const p = TANKS[spec.key] || TANKS.altay;
  const recoil = (spec.kick || 0) * 0.26;
  box(0, 0, p.H * 0.42, p.L, p.W * 0.78, p.H * 0.62, 0, shade(paint, -0.08));
  wedge(p.L * 0.38, 0, p.H * 0.5, p.L * 0.36, p.W * 0.66, p.H * 0.4, 0, shade(paint, 0.08));
  box(0, -p.W * 0.48, p.H * 0.28, p.L * 0.92, p.W * 0.18, p.H * 0.5, 0, "#161814");
  box(0, p.W * 0.48, p.H * 0.28, p.L * 0.92, p.W * 0.18, p.H * 0.5, 0, "#161814");
  const span = p.L * 0.74;
  const step = span / Math.max(1, p.wheels - 1);
  for (let i = 0; i < p.wheels; i++) {
    const x = -span / 2 + i * step;
    const rad = i === 0 || i === p.wheels - 1 ? 0.18 : 0.15;
    wheel(x, -p.W * 0.52, 0.14, rad, phase + i * 0.7);
    wheel(x, p.W * 0.52, 0.14, rad, phase + i * 0.7);
  }
  if (detail) {
    for (let i = 0; i < 8; i++) {
      box(-p.L * 0.38 + i * (p.L * 0.1), -p.W * 0.56, p.H * 0.22, 0.06, 0.04, 0.08, 0, "#2a2c28");
      box(-p.L * 0.38 + i * (p.L * 0.1), p.W * 0.56, p.H * 0.22, 0.06, 0.04, 0.08, 0, "#2a2c28");
    }
  }
  if (p.wedge) box(p.L * 0.28, 0, p.H * 0.78, p.L * 0.28, p.W * 0.5, 0.12, 0, shade(paint, 0.08));
  if (p.era) {
    box(p.L * 0.22, -p.W * 0.28, p.H * 0.72, 0.28, 0.12, 0.12, 0, shade(cloth, -0.2));
    box(p.L * 0.22, p.W * 0.28, p.H * 0.72, 0.28, 0.12, 0.12, 0, shade(cloth, -0.2));
  }
  box(p.tX, 0, p.H * 0.95, p.tL, p.tW, p.tH, aim, shade(paint, 0.1));
  box(p.tX - p.tL * 0.28, 0, p.H * 0.9, p.tL * 0.32, p.tW * 0.9, p.tH * 0.7, aim, shade(paint, -0.02));
  box(p.tX + p.tL * 0.42 - recoil, 0, p.H + 0.1, p.gun, 0.16, 0.14, aim, "#1a1c18");
  box(p.tX + p.tL * 0.42 + p.gun * 0.46 - recoil, 0, p.H + 0.1, 0.22, 0.22, 0.18, aim, "#101210");
  box(p.tX - p.tL * 0.05, 0, p.H + p.tH * 0.85, 0.16, 0.16, 0.14, aim, dark);
  if (detail) {
    box(p.tX - p.tL * 0.2, -p.tW * 0.28, p.H + p.tH * 0.55, 0.05, 0.05, 0.34, aim, "#2a2c28");
    box(-p.L * 0.22, 0, p.H * 0.78, 0.34, 0.22, 0.12, 0, shade(cloth, -0.25));
    box(p.L * 0.05, p.W * 0.22, p.H * 0.78, 0.22, 0.16, 0.1, 0, shade(dark, 0.1));
    box(p.tX + 0.08, p.tW * 0.22, p.H + p.tH * 0.72, 0.08, 0.08, 0.1, aim, "#2c2e28");
    box(p.tX + 0.08, -p.tW * 0.22, p.H + p.tH * 0.72, 0.08, 0.08, 0.1, aim, "#2c2e28");
  }
}

function sph(box, paint, dark, detail, aim) {
  box(0, 0, 0.28, 2.3, 0.85, 0.42, 0, shade(paint, -0.08));
  box(0, -0.55, 0.22, 2.1, 0.2, 0.4, 0, dark);
  box(0, 0.55, 0.22, 2.1, 0.2, 0.4, 0, dark);
  box(-0.15, 0, 0.7, 1.15, 0.9, 0.42, aim, shade(paint, 0.06));
  box(0.85, 0, 0.78, 1.9, 0.08, 0.08, aim, "#2c2e28");
  if (detail) box(-0.9, 0, 0.55, 0.4, 0.5, 0.22, 0, shade(paint, 0.15));
}

function mlrs(box, paint, dark, cloth, detail, phase) {
  box(-0.85, 0, 0.38, 0.7, 0.7, 0.48, 0, shade(paint, 0.05));
  box(0.15, 0, 0.28, 1.7, 0.72, 0.28, 0, shade(paint, -0.12));
  box(0.35, -0.42, 0.16, 0.28, 0.16, 0.28, 0, dark);
  box(0.35, 0.42, 0.16, 0.28, 0.16, 0.28, 0, dark);
  box(-0.55, -0.42, 0.16, 0.28, 0.16, 0.28, 0, dark);
  box(-0.55, 0.42, 0.16, 0.28, 0.16, 0.28, 0, dark);
  const tilt = -0.35;
  box(0.45, 0, 0.72, 1.15, 0.55, 0.32, tilt, shade(cloth, -0.25));
  if (detail) {
    for (let i = 0; i < 3; i++) box(0.2 + i * 0.28, 0, 0.95, 0.5, 0.08, 0.08, tilt, "#3a342c");
  }
}

function ifv(box, paint, dark, cloth, detail, aim) {
  box(0, 0, 0.32, 2.15, 0.95, 0.4, 0, shade(paint, -0.04));
  box(0.15, 0, 0.62, 1.2, 0.85, 0.28, 0, shade(paint, 0.08));
  box(0.35, 0, 0.88, 0.4, 0.4, 0.22, aim, shade(cloth, -0.1));
  box(0.7, 0, 0.9, 0.7, 0.06, 0.06, aim, dark);
  if (detail) {
    for (let i = 0; i < 4; i++) {
      box(-0.7 + i * 0.45, -0.52, 0.12, 0.22, 0.12, 0.22, 0, "#1c1e1a");
      box(-0.7 + i * 0.45, 0.52, 0.12, 0.22, 0.12, 0.22, 0, "#1c1e1a");
    }
  }
}

function atgm(box, paint, dark, cloth, aim) {
  box(0, 0, 0.28, 1.55, 0.7, 0.32, 0, shade(paint, 0.02));
  box(-0.35, 0, 0.55, 0.45, 0.55, 0.28, 0, shade(cloth, -0.05));
  box(0.35, 0, 0.62, 0.7, 0.12, 0.1, aim, dark);
  box(0.15, -0.18, 0.62, 0.55, 0.08, 0.08, aim, "#4a4034");
  box(0.15, 0.18, 0.62, 0.55, 0.08, 0.08, aim, "#4a4034");
}

function truck(box, paint, dark, cloth, detail, carry) {
  box(-0.7, 0, 0.42, 0.7, 0.72, 0.5, 0, shade(paint, 0.08));
  box(0.35, 0, 0.4, 1.35, 0.78, 0.48, 0, shade(cloth, -0.18));
  box(0.55, -0.42, 0.16, 0.28, 0.16, 0.3, 0, dark);
  box(0.55, 0.42, 0.16, 0.28, 0.16, 0.3, 0, dark);
  box(-0.55, -0.42, 0.16, 0.26, 0.16, 0.3, 0, dark);
  box(-0.55, 0.42, 0.16, 0.26, 0.16, 0.3, 0, dark);
  if (detail) box(-0.72, 0, 0.62, 0.28, 0.4, 0.16, 0, "#9bb8c4");
  if (carry) box(0.35, 0, 0.78, 0.4, 0.4, 0.22, 0, "#c6a15a");
}

function infantry(box, paint, dark, cloth, phase) {
  const spots = [
    [-0.28, -0.22],
    [0.22, -0.16],
    [-0.16, 0.24],
    [0.3, 0.2],
  ];
  spots.forEach(([x, y], i) => {
    const step = Math.sin(phase * 6 + i) * 0.08;
    box(x, y, 0.28, 0.16, 0.18, 0.28, 0, shade(paint, -0.1));
    box(x, y - 0.08, 0.1, 0.08, 0.08, 0.16 + step, 0, dark);
    box(x, y + 0.08, 0.1, 0.08, 0.08, 0.16 - step, 0, dark);
    box(x + 0.02, y, 0.5, 0.12, 0.12, 0.12, 0, shade(cloth, -0.05));
    box(x + 0.12, y, 0.32, 0.22, 0.04, 0.04, 0, "#2a2c28");
  });
}

function heli(box, paint, dark, cloth, detail) {
  box(0.1, 0, 0.35, 1.35, 0.38, 0.32, 0, shade(paint, -0.02));
  box(0.55, 0, 0.42, 0.4, 0.28, 0.22, 0, "#8fb4c4");
  box(-0.85, 0, 0.4, 0.9, 0.1, 0.1, 0, shade(paint, -0.15));
  box(-1.25, 0, 0.55, 0.08, 0.28, 0.22, 0, dark);
  box(0.15, -0.45, 0.28, 0.55, 0.2, 0.08, 0, shade(paint, 0.05));
  box(0.15, 0.45, 0.28, 0.55, 0.2, 0.08, 0, shade(paint, 0.05));
  if (detail) {
    box(0.2, -0.55, 0.36, 0.35, 0.06, 0.06, 0, dark);
    box(0.2, 0.55, 0.36, 0.35, 0.06, 0.06, 0, dark);
  }
  box(0, 0, 0.62, 0.12, 0.12, 0.28, 0, cloth);
}

function jet(box, paint, dark, cloth, key, bank) {
  const stealth = key === "f35";
  box(0, bank * 0.2, 0.22, stealth ? 2.15 : 2.05, 0.28, 0.22, bank * 0.4, shade(paint, -0.08));
  box(0.15, bank, 0.28, 0.7, stealth ? 1.55 : 1.7, 0.06, bank * 0.35, shade(paint, 0.02));
  box(-0.85, bank * 0.3, 0.4, 0.28, 0.55, 0.28, bank * 0.3, shade(paint, -0.02));
  if (!stealth) box(0.85, bank * 0.15, 0.3, 0.35, 0.45, 0.05, bank * 0.2, shade(paint, 0.1));
  box(0.35, 0, 0.4, 0.35, 0.2, 0.12, 0, "#9ec0cc");
  box(-0.95, 0, 0.22, 0.2, 0.2, 0.16, 0, dark);
}

function wing(box, paint, dark, key) {
  const broad = key === "gj11" || key === "shahed";
  box(0, 0, 0.16, broad ? 1.4 : 1.7, broad ? 2.1 : 1.35, 0.1, 0, shade(paint, -0.05));
  if (!broad) {
    box(-0.55, -0.35, 0.28, 0.25, 0.08, 0.22, 0.4, shade(paint, 0.05));
    box(-0.55, 0.35, 0.28, 0.25, 0.08, 0.22, -0.4, shade(paint, 0.05));
  }
  box(0.15, 0, 0.28, 0.4, 0.22, 0.1, 0, shade(dark, 0.2));
  if (key === "shahed") box(-0.7, 0, 0.2, 0.18, 0.18, 0.18, 0, "#3a3834");
}

function drone(box, paint, dark) {
  box(0.1, 0, 0.2, 0.9, 0.22, 0.16, 0, shade(paint, -0.05));
  box(-0.15, 0, 0.28, 0.35, 0.7, 0.05, 0, shade(paint, 0.08));
  box(0.2, 0, 0.28, 0.3, 0.55, 0.04, 0.6, shade(dark, 0.15));
}

function quad(box, dark, cloth, phase) {
  const bob = Math.sin(phase * 10) * 0.04;
  box(0, 0, 0.22 + bob, 0.28, 0.28, 0.12, 0, cloth);
  box(0, 0, 0.3 + bob, 0.7, 0.08, 0.05, 0, dark);
  box(0, 0, 0.3 + bob, 0.08, 0.7, 0.05, 0, dark);
}

function ship(box, paint, dark, cloth, name) {
  const long = /Burke|055|Gorshkov|Type 45/i.test(name);
  const L = long ? 3.1 : 2.45;
  box(0, 0, 0.22, L, 0.62, 0.28, 0, shade(paint, -0.18));
  box(0.1, 0, 0.42, L * 0.72, 0.4, 0.16, 0, shade(paint, 0.05));
  box(-0.15, 0, 0.7, 0.55, 0.32, 0.4, 0, shade(cloth, -0.2));
  box(0.55, 0, 0.62, 0.7, 0.06, 0.06, 0, dark);
  box(-0.45, 0, 1.05, 0.08, 0.08, 0.4, 0, "#c6a15a");
}

function node(box, key, paint, dark) {
  if (key === "petrol") {
    box(0, 0, 0.2, 0.7, 0.7, 0.25, 0, shade(dark, 0.15));
    box(-0.1, 0, 0.7, 0.9, 0.08, 0.08, 0.5, paint);
    box(0.35, 0, 0.35, 0.12, 0.12, 0.5, 0, dark);
  } else if (key === "gaz") {
    box(0, 0, 0.35, 0.9, 0.45, 0.45, 0, "#d5ddd8");
    box(0.55, 0, 0.2, 0.2, 0.2, 0.35, 0, paint);
  } else if (key === "komur") {
    box(0, 0, 0.22, 0.8, 0.6, 0.35, 0, "#1a1a1a");
    box(0.1, 0.1, 0.4, 0.4, 0.35, 0.2, 0, "#2a2a2a");
  } else if (key === "tahil") {
    box(0, 0, 0.25, 0.7, 0.5, 0.35, 0, "#d2b85a");
    box(0, 0, 0.5, 0.4, 0.3, 0.2, 0, "#e6d48a");
  } else {
    box(0, 0, 0.2, 0.55, 0.4, 0.3, 0, "#8a7048");
    box(0.15, 0.1, 0.4, 0.35, 0.28, 0.22, 0, "#6a5438");
  }
}

function tree(box, dark, paint) {
  box(0, 0, 0.45, 0.1, 0.1, 0.9, 0, "#5a4632");
  box(0, 0, 1.05, 0.62, 0.62, 0.55, 0, shade(paint, -0.25));
  box(-0.08, 0.06, 1.4, 0.38, 0.38, 0.36, 0, shade(paint, -0.08));
}

function peak(box, dark, cloth) {
  box(-0.15, 0.05, 0.28, 0.85, 0.7, 0.45, 0.2, "#6d6256");
  box(0.28, -0.12, 0.42, 0.55, 0.48, 0.55, -0.3, "#7c7264");
  box(0.02, 0.02, 0.85, 0.38, 0.32, 0.48, 0.1, shade(cloth, -0.25));
  box(0.05, 0, 1.2, 0.16, 0.14, 0.22, 0, shade(cloth, 0.2));
}

function building(box, spec, paint, dark, cloth, detail, aim) {
  const form = spec.form;
  const grow = spec.grow == null ? 1 : spec.grow;
  if (grow < 0.98) {
    box(-0.7, -0.45, 0.7, 0.05, 0.05, 1.35, 0, "#8d8474");
    box(0.7, -0.45, 0.7, 0.05, 0.05, 1.35, 0, "#8d8474");
    box(-0.7, 0.45, 0.7, 0.05, 0.05, 1.35, 0, "#8d8474");
    box(0.7, 0.45, 0.7, 0.05, 0.05, 1.35, 0, "#8d8474");
    box(0, -0.45, 1.35, 1.4, 0.04, 0.04, 0, "#a39886");
    box(0, 0.45, 1.35, 1.4, 0.04, 0.04, 0, "#a39886");
  }
  const pads = {
    hq: [2.15, 1.6],
    barracks: [2.15, 1.2],
    shop: [2.05, 1.5],
    strip: [3.05, 1.45],
    tower: [1.2, 1.05],
    sam: [1.2, 1.05],
    bunker: [1.35, 1.15],
    yard: [2.15, 1.15],
    battery: [1.25, 1.2],
  };
  const pad = pads[form] || [1.7, 1.25];
  box(0, 0, 0.02, pad[0] * 0.92, pad[1] * 0.92, 0.03, 0, "#3e3a34");
  if (form === "hq") {
    box(0, 0, 0.32, 1.7, 1.25, 0.48, 0, shade(paint, -0.2));
    box(0.1, -0.08, 0.68, 0.7, 0.55, 0.32, 0, shade(paint, 0.02));
    box(-0.55, 0.15, 0.58, 0.55, 0.7, 0.36, 0, shade(paint, -0.08));
    box(0.55, 0.2, 0.5, 0.4, 0.45, 0.28, 0, shade(dark, 0.15));
    for (let i = 0; i < 3; i++) {
      box(-0.35 + i * 0.28, -0.58, 0.42, 0.16, 0.04, 0.14, 0, "#1c2428");
      box(0.05 + i * 0.18, 0.58, 0.4, 0.1, 0.04, 0.12, 0, "#1c2428");
    }
    box(-0.15, 0.15, 0.95, 0.22, 0.16, 0.16, 0, "#8fb0bc");
    box(0.2, -0.28, 0.7, 0.16, 0.08, 0.22, 0, shade(cloth, -0.15));
    box(-0.55, 0.4, 0.22, 0.55, 0.4, 0.16, 0, shade(dark, 0.2));
    box(0.15, -0.05, 1.02, 0.06, 0.06, 0.36, 0, cloth);
    box(0.62, -0.35, 0.78, 0.18, 0.18, 0.22, 0, shade(dark, 0.05));
    if (detail) box(0.42, 0.12, 0.92, 0.26, 0.26, 0.05, 0.5, "#c5d0d2");
  } else if (form === "barracks") {
    box(0, 0, 0.32, 1.8, 0.85, 0.48, 0, shade(paint, -0.02));
    box(0, 0.55, 0.12, 1.5, 0.25, 0.12, 0, shade(dark, 0.3));
    if (detail) box(-0.2, -0.43, 0.4, 0.2, 0.06, 0.28, 0, shade(cloth, -0.1));
  } else if (form === "shop") {
    box(0, 0, 0.4, 1.7, 1.15, 0.55, 0, shade(paint, -0.06));
    box(-0.4, -0.58, 0.28, 0.4, 0.08, 0.4, 0, shade(dark, 0.2));
    box(0.15, -0.58, 0.28, 0.4, 0.08, 0.4, 0, shade(dark, 0.2));
    box(0.7, -0.58, 0.28, 0.4, 0.08, 0.4, 0, shade(dark, 0.2));
  } else if (form === "strip") {
    box(0.35, 0, 0.04, 2.8, 1.15, 0.06, 0, "#5a584e");
    box(0.45, 0, 0.08, 2.2, 0.42, 0.05, 0, shade(dark, 0.4));
    box(-0.95, 0.55, 0.32, 0.85, 0.7, 0.48, 0, shade(paint, -0.04));
    box(-0.95, 0.55, 0.62, 0.7, 0.62, 0.12, 0, shade(paint, 0.08));
    if (detail && grow > 0.9) {
      box(0.35, 0.02, 0.2, 0.85, 0.16, 0.1, 0, shade(paint, -0.12));
      box(0.4, 0.02, 0.24, 0.22, 0.7, 0.04, 0, shade(paint, 0.02));
      box(0.15, 0.02, 0.3, 0.16, 0.28, 0.1, 0, shade(paint, -0.02));
    }
    if (detail) box(0.55, 0, 0.12, 1.3, 0.04, 0.02, 0, cloth);
  } else if (form === "sam" || form === "tower") {
    box(0, 0, 0.15, 0.9, 0.7, 0.2, 0, shade(dark, 0.28));
    for (let i = 0; i < 4; i++) {
      box(-0.15 + (i % 2) * 0.3, -0.12 + Math.floor(i / 2) * 0.24, 0.55, 0.1, 0.1, 0.55, aim, shade(paint, 0.1));
    }
    box(0.35, 0, 0.7, 0.08, 0.28, 0.35, aim, cloth);
  } else if (form === "bunker") {
    box(0, 0, 0.28, 1.05, 0.85, 0.4, 0, shade(dark, 0.22));
    box(0.35, 0, 0.45, 0.45, 0.12, 0.12, aim, paint);
  } else if (form === "yard") {
    box(0.2, 0, 0.12, 1.8, 0.7, 0.16, 0, shade(dark, 0.3));
    box(-0.55, 0, 0.4, 0.45, 0.55, 0.45, 0, shade(paint, -0.05));
    box(0.4, -0.45, 0.7, 0.08, 0.08, 0.9, 0, cloth);
    box(0.7, -0.15, 1.05, 0.6, 0.08, 0.08, 0, dark);
  } else if (form === "battery") {
    box(0, 0, 0.18, 0.9, 0.9, 0.22, 0, shade(dark, 0.25));
    box(0, 0, 0.45, 0.4, 0.4, 0.28, aim, shade(paint, 0.02));
    box(0.45, 0, 0.5, 0.8, 0.1, 0.1, aim, "#2c2e28");
  } else {
    box(0, 0, 0.4, 1.4, 0.95, 0.55, 0, shade(paint, -0.1));
    box(0, 0, 0.75, 1.45, 0.2, 0.12, 0, shade(dark, 0.15));
  }
}
