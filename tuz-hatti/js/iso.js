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

function project(x, y, z, yaw) {
  const c = Math.cos(yaw);
  const s = Math.sin(yaw);
  const rx = x * c - y * s;
  const ry = x * s + y * c;
  return [ (rx - ry) * IX, (rx + ry) * IY - z * 1.7 ];
}

function depthOf(x, y, yaw) {
  const c = Math.cos(yaw);
  const s = Math.sin(yaw);
  return x * c - y * s + (x * s + y * c);
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
  const face = -(rx + ry) * 0.82 + nz * 0.7;
  const light = -rx * 0.35 - ry * 0.55 + nz * 0.85;
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
    if (sun.face <= 0.02) continue;
    let depth = 0;
    const pts = quad.idx.map((i) => {
      depth += depthOf(corners[i][0], corners[i][1], yaw);
      return project(corners[i][0], corners[i][1], corners[i][2], yaw);
    });
    faces.push({
      pts,
      depth,
      color: shade(color, Math.max(-0.62, Math.min(0.5, quad.bias + sun.light * 0.22))),
    });
  }
}

function paintFaces(ctx, faces) {
  faces.sort((a, b) => a.depth - b.depth);
  for (const face of faces) {
    ctx.beginPath();
    ctx.moveTo(face.pts[0][0], face.pts[0][1]);
    for (let i = 1; i < face.pts.length; i++) ctx.lineTo(face.pts[i][0], face.pts[i][1]);
    ctx.closePath();
    ctx.fillStyle = face.color;
    ctx.fill();
    ctx.strokeStyle = "rgba(12,10,8,0.35)";
    ctx.lineWidth = 0.7;
    ctx.stroke();
  }
}

function rotor(ctx, x, y, z, span, yaw, phase) {
  ctx.strokeStyle = "rgba(230,230,220,0.75)";
  ctx.lineWidth = 1.4;
  ctx.beginPath();
  for (let i = 0; i < 4; i++) {
    const a = phase + (i * Math.PI) / 2;
    const p0 = project(x, y, z, yaw);
    const p1 = project(x + Math.cos(a) * span, y + Math.sin(a) * span * 0.55, z, yaw);
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

  if (spec.form === "tank") tank(box, spec, paint, dark, cloth, detail, phase, aim);
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
  else building(box, spec.form, paint, dark, cloth, detail, aim);

  paintFaces(ctx, faces);
  if (spec.form === "heli") rotor(ctx, 0, 0, (zLift + 0.95) * u * grow, u * 1.7, yaw, phase);
  if (spec.form === "quad") rotor(ctx, 0, 0, (zLift + 0.55) * u, u * 0.85, yaw, phase * 2);
}

function tank(box, spec, paint, dark, cloth, detail, phase, aim) {
  const p = TANKS[spec.key] || TANKS.altay;
  box(0, 0, p.H * 0.45, p.L, p.W * 0.72, p.H * 0.7, 0, shade(paint, -0.05));
  box(0, -p.W * 0.46, p.H * 0.32, p.L * 0.92, p.W * 0.22, p.H * 0.55, 0, dark);
  box(0, p.W * 0.46, p.H * 0.32, p.L * 0.92, p.W * 0.22, p.H * 0.55, 0, dark);
  if (detail) {
    const step = p.L / (p.wheels + 1);
    for (let i = 0; i < p.wheels; i++) {
      const wobble = ((i + Math.floor(phase * 2)) % 2) * 0.02;
      const x = -p.L / 2 + step * (i + 1);
      box(x, -p.W * 0.5, 0.12 + wobble, 0.16, 0.1, 0.16, 0, "#1a1c18");
      box(x, p.W * 0.5, 0.12 + wobble, 0.16, 0.1, 0.16, 0, "#1a1c18");
    }
  }
  if (p.wedge) box(p.L * 0.28, 0, p.H * 0.78, p.L * 0.28, p.W * 0.5, 0.12, 0, shade(paint, 0.08));
  if (p.era) {
    box(p.L * 0.22, -p.W * 0.28, p.H * 0.72, 0.28, 0.12, 0.12, 0, shade(cloth, -0.2));
    box(p.L * 0.22, p.W * 0.28, p.H * 0.72, 0.28, 0.12, 0.12, 0, shade(cloth, -0.2));
  }
  box(p.tX, 0, p.H * 0.85, p.tL, p.tW, p.tH, aim, shade(paint, 0.12));
  box(p.tX + p.tL * 0.45, 0, p.H * 0.95, p.gun, 0.08, 0.08, aim, "#2a2c28");
  box(p.tX - p.tL * 0.2, 0, p.H + p.tH * 0.7, 0.16, 0.16, 0.18, aim, dark);
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
    for (let i = 0; i < 3; i++) box(0.2 + i * 0.28, 0, 0.95, 0.5, 0.08, 0.08, tilt + phase * 0.02, "#3a342c");
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

function building(box, form, paint, dark, cloth, detail, aim) {
  if (form === "hq") {
    box(0, 0, 0.55, 1.55, 1.15, 0.85, 0, shade(paint, -0.2));
    box(0.05, -0.05, 1.15, 0.72, 0.58, 0.42, 0, shade(paint, 0.02));
    box(-0.15, 0.15, 0.95, 0.22, 0.16, 0.16, 0, "#8fb0bc");
    box(0.2, -0.28, 0.7, 0.16, 0.08, 0.22, 0, shade(cloth, -0.15));
    box(-0.55, 0.4, 0.22, 0.55, 0.4, 0.16, 0, shade(dark, 0.2));
    box(0.15, -0.05, 1.5, 0.06, 0.06, 0.4, 0, cloth);
    if (detail) box(0.42, 0.12, 1.42, 0.26, 0.26, 0.05, 0.5, "#c5d0d2");
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
    box(0.2, 0, 0.06, 2.4, 0.55, 0.08, 0, shade(dark, 0.35));
    box(-0.85, 0.45, 0.28, 0.7, 0.55, 0.4, 0, shade(paint, -0.04));
    if (detail) box(0.3, 0, 0.12, 1.2, 0.06, 0.02, 0, cloth);
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
