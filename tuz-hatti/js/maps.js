/** Sekiz özgün cephe haritası. Kaynak miktarı her haritada aynıdır.
 *  Fark, geçit ve açık alanın savunmayı veya saldırıyı kolaylaştırmasındadır.
 */

export const MAP_W = 2100;
export const MAP_H = 1300;

const SLOTS = [
  { x: 360, y: 280, team: 0 },
  { x: 300, y: 650, team: 0 },
  { x: 360, y: 1040, team: 0 },
  { x: 820, y: 240, team: 0 },
  { x: 1740, y: 280, team: 1 },
  { x: 1800, y: 650, team: 1 },
  { x: 1740, y: 1040, team: 1 },
  { x: 1280, y: 1080, team: 1 },
];

export const MATCH_SIZES = [2, 3, 4, 6, 8];

const PICKS = {
  2: [1, 5],
  3: [1, 4, 6],
  4: [0, 2, 4, 6],
  6: [0, 1, 2, 4, 5, 6],
  8: [0, 1, 2, 3, 4, 5, 6, 7],
};

const LOCAL_DIST = [150, 240];
const LOCAL_SIDE = [82, -82];
const NODE_AMOUNT = 1600;

function ridge(x0, y0, x1, y1, r, step, kind) {
  const out = [];
  const len = Math.hypot(x1 - x0, y1 - y0);
  const n = Math.max(1, Math.round(len / step));
  for (let i = 0; i <= n; i++) {
    const t = i / n;
    out.push({ x: x0 + (x1 - x0) * t, y: y0 + (y1 - y0) * t, r, kind });
  }
  return out;
}

function mapDef(partial) {
  return { w: MAP_W, h: MAP_H, slots: SLOTS, ...partial };
}

export const MAPS = {
  anadolu: mapDef({
    id: "anadolu",
    name: "Anadolu",
    country: "Türkiye",
    resource: "maden",
    resourceName: "Maden",
    posture: "İki dağ geçidi. Hattı tutan taraf saldırıyı daraltır.",
    blurb: "Toros sırtı ortayı keser. Kuzeyde boğaz suyu kıyıyı kapatır.",
    terrain: "map-tur",
    naval: true,
    obstacles: [
      ...ridge(1050, 280, 1050, 370, 46, 70, "mountain"),
      ...ridge(1050, 930, 1050, 1040, 46, 70, "mountain"),
      { x: 1050, y: 650, r: 48, kind: "mountain" },
      ...ridge(220, 80, 1880, 80, 28, 140, "water"),
    ],
  }),
  ovalar: mapDef({
    id: "ovalar",
    name: "Büyük Ovalar",
    country: "Amerika",
    resource: "petrol",
    resourceName: "Petrol",
    posture: "Geniş ırmak, üç sığ geçit. Kanatlar açık, saldırı rahat.",
    blurb: "Düz otlak. Irmak yolu böler ama kıyısından dolaşılır.",
    terrain: "map-usa",
    naval: true,
    obstacles: [
      ...ridge(1050, 150, 1050, 340, 40, 90, "water"),
      ...ridge(1050, 580, 1050, 740, 40, 90, "water"),
      ...ridge(1050, 980, 1050, 1160, 40, 90, "water"),
    ],
  }),
  havza: mapDef({
    id: "havza",
    name: "Nehir Havzası",
    country: "Çin",
    resource: "komur",
    resourceName: "Kömür",
    posture: "Enine nehir, iki köprü. Karşıya geçen daralır.",
    blurb: "Yeşil havza ortadan bir ırmakla ayrılır.",
    terrain: "map-cin",
    naval: true,
    obstacles: [
      ...ridge(640, 620, 900, 620, 36, 80, "water"),
      ...ridge(1200, 620, 1480, 620, 36, 80, "water"),
    ],
  }),
  tayga: mapDef({
    id: "tayga",
    name: "Tayga",
    country: "Rusya",
    resource: "gaz",
    resourceName: "Doğalgaz",
    posture: "Açık koridorlar. Mesafe uzun, saldırı kolu toplanır.",
    blurb: "Seyrek orman yolu kapatmaz. Cephe geniş kalır.",
    terrain: "map-rus",
    naval: false,
    obstacles: [
      { x: 760, y: 420, r: 34, kind: "forest" },
      { x: 1380, y: 860, r: 34, kind: "forest" },
      { x: 680, y: 900, r: 28, kind: "forest" },
      { x: 1460, y: 400, r: 28, kind: "forest" },
    ],
  }),
  vadi: mapDef({
    id: "vadi",
    name: "Nehir Vadisi",
    country: "Almanya",
    resource: "komur",
    resourceName: "Kömür",
    posture: "Tek temiz geçit. Savunma kısa, saldırı sıkışır.",
    blurb: "Sık orman ve dar su. Karşıya bir yerden geçilir.",
    terrain: "map-alm",
    naval: true,
    obstacles: [
      ...ridge(1050, 160, 1050, 390, 44, 80, "water"),
      ...ridge(1050, 910, 1050, 1160, 44, 80, "water"),
      { x: 780, y: 500, r: 30, kind: "forest" },
      { x: 1360, y: 760, r: 30, kind: "forest" },
    ],
  }),
  ada: mapDef({
    id: "ada",
    name: "Ada",
    country: "Birleşik Krallık",
    resource: "petrol",
    resourceName: "Petrol",
    posture: "Ortada kanal, iki kumsal. Çıkarma daralır.",
    blurb: "Kıyı suyla çevrili. Karşı cepheye iki sahilden girilir.",
    terrain: "map-ing",
    naval: true,
    obstacles: [
      ...ridge(1050, 150, 1050, 380, 42, 80, "water"),
      ...ridge(1050, 920, 1050, 1160, 42, 80, "water"),
      ...ridge(180, 150, 640, 150, 26, 150, "water"),
      ...ridge(1460, 1150, 1940, 1150, 26, 150, "water"),
    ],
  }),
  bozkir: mapDef({
    id: "bozkir",
    name: "Bozkır",
    country: "Ukrayna",
    resource: "tahil",
    resourceName: "Tahıl",
    posture: "İnce ırmak, çok geçit. Açık sahada saldırı hızlıdır.",
    blurb: "Düz tarla. Su yolu orduyu durdurmaz.",
    terrain: "map-ukr",
    naval: false,
    obstacles: [
      { x: 1050, y: 280, r: 28, kind: "water" },
      { x: 1050, y: 980, r: 28, kind: "water" },
    ],
  }),
  plato: mapDef({
    id: "plato",
    name: "Plato",
    country: "İran",
    resource: "petrol",
    resourceName: "Petrol",
    posture: "Çapraz sırt, iki geçit. Geçidi tutan savunur.",
    blurb: "Kurak plato. Dağ sırası orduları geçide sokar.",
    terrain: "map-irn",
    naval: false,
    obstacles: [
      { x: 700, y: 520, r: 46, kind: "mountain" },
      { x: 1420, y: 760, r: 46, kind: "mountain" },
      { x: 860, y: 860, r: 36, kind: "mountain" },
      { x: 1260, y: 420, r: 36, kind: "mountain" },
    ],
  }),
};

export const MAP_IDS = Object.keys(MAPS);

export function mapById(id) {
  return MAPS[id] || MAPS.anadolu;
}

export function battlePlan(mapId, count) {
  const map = mapById(mapId);
  const safeCount = PICKS[count] ? count : 2;
  const indices = PICKS[safeCount];
  const slots = indices.map((slotIndex, order) => {
    const slot = map.slots[slotIndex];
    const team = safeCount === 3 ? order : slot.team;
    return { ...slot, slotIndex, order, team };
  });
  const nodes = [];
  for (const slot of slots) {
    const ang = Math.atan2(map.h / 2 - slot.y, map.w / 2 - slot.x);
    const px = Math.cos(ang + Math.PI / 2);
    const py = Math.sin(ang + Math.PI / 2);
    LOCAL_DIST.forEach((dist, index) => {
      const side = LOCAL_SIDE[index];
      nodes.push({
        x: clamp(slot.x + Math.cos(ang) * dist + px * side, 90, map.w - 90),
        y: clamp(slot.y + Math.sin(ang) * dist + py * side, 90, map.h - 90),
        amount: NODE_AMOUNT,
        kind: map.resource,
        slotIndex: slot.slotIndex,
      });
    });
  }
  nodes.push({ x: map.w / 2, y: map.h / 2 - 180, amount: NODE_AMOUNT, kind: map.resource, contested: true });
  nodes.push({ x: map.w / 2, y: map.h / 2 + 180, amount: NODE_AMOUNT, kind: map.resource, contested: true });
  return { map, count: safeCount, slots, nodes, battle: safeCount === 3 ? "ffa" : "teams" };
}

export function resourceTotal(plan) {
  return plan.nodes.reduce((sum, node) => sum + node.amount, 0);
}

function clamp(v, a, b) {
  return Math.max(a, Math.min(b, v));
}
