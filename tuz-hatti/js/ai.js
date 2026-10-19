/** Rakip hat. Yakıtı kurar, ikmal tıkanırsa ambar açar, ordu eşiğinde saldırır. */

import { buildingDef, unitDef } from "./balance.js";
import { canPlace, command, hostile, supplyInfo } from "./sim.js";

export function createAiMemory() {
  return { acc: 0, attackLock: 0 };
}

export function aiAct(state, owner, dt, mem) {
  if (state.winner !== null) return;
  mem.acc += dt;
  mem.attackLock = Math.max(0, mem.attackLock - dt);
  if (mem.acc < 0.55) return;
  mem.acc = 0;

  const factionId = state.players[owner].faction;
  const mine = state.entities.filter((e) => e.owner === owner && e.hp > 0);
  const workers = mine.filter((e) => !e.isBuilding && unitDef(factionId, e.kind)?.worker);
  const fighters = mine.filter((e) => !e.isBuilding && (unitDef(factionId, e.kind)?.damage || 0) > 0);
  const buildings = mine.filter((e) => e.isBuilding);

  for (const w of workers) {
    if (!w.plan || w.plan.kind === "guard") command(state, { by: owner, type: "harvest", ids: [w.id] });
  }
  resumeConstruction(state, owner, workers, buildings);

  const underAttack = threatPoint(state, owner, buildings);
  if (underAttack && fighters.length) {
    const ready = fighters.filter((f) => f.plan?.kind !== "attack");
    if (ready.length) {
      command(state, { by: owner, type: "attackMove", ids: ready.map((f) => f.id), x: underAttack.x, y: underAttack.y });
    }
  }

  const ash = state.players[owner].ash;
  const supply = supplyInfo(state, owner);
  const queuedWorkers = buildings.reduce(
    (n, b) => n + (b.queue?.filter((job) => job.kind === "tasiyici").length || 0),
    0,
  );
  const workerCount = workers.length + queuedWorkers;
  const hq = buildings.find((b) => b.kind === "ocak" && b.constructed);

  if (hq && workerCount < 3 && ash >= costOfUnit(factionId, "tasiyici") && supply.used + 1 <= supply.cap) {
    if ((hq.queue?.length || 0) < 2) {
      command(state, { by: owner, type: "train", buildingId: hq.id, unitKind: "tasiyici" });
    }
  }

  ensureBuilding(state, owner, factionId, buildings, workers, "barinak");

  const barracks = buildings.find((b) => b.kind === "barinak" && b.constructed);
  const infKind = barracks ? buildingDef(factionId, "barinak").trains[0] : null;
  const infCount = infKind ? mine.filter((e) => !e.isBuilding && e.kind === infKind).length : 0;
  if (barracks && infCount < 3) tryTrain(state, owner, barracks, infKind);

  if (barracks) ensureBuilding(state, owner, factionId, buildings, workers, "atolye");

  if (supply.cap > 0 && supply.used + 2 > supply.cap && countKind(buildings, "depo") < 3) {
    ensureBuilding(state, owner, factionId, buildings, workers, "depo");
  }

  const shop = buildings.find((b) => b.kind === "atolye" && b.constructed);
  if (shop) {
    const options = buildingDef(factionId, "atolye").trains
      .map((kind) => ({
        kind,
        n: mine.filter((e) => e.kind === kind && !e.isBuilding).length,
        cost: unitDef(factionId, kind).cost,
        siege: (unitDef(factionId, kind).bonus?.building || 1) > 1.7,
      }))
      .sort((a, b) => Number(b.siege) - Number(a.siege) || a.n - b.n || a.cost - b.cost);
    for (const opt of options) {
      if (opt.n >= (opt.siege ? 1 : 2)) continue;
      if (tryTrain(state, owner, shop, opt.kind)) break;
    }
  }

  const vehicles = mine.filter((e) => !e.isBuilding && (unitDef(factionId, e.kind)?.supply || 1) > 1).length;
  if (barracks && vehicles >= 1 && infCount < 7) tryTrain(state, owner, barracks, infKind);

  if (shop && vehicles >= 1) ensureBuilding(state, owner, factionId, buildings, workers, "pist");
  const strip = buildings.find((b) => b.kind === "pist" && b.constructed);
  if (strip) {
    const airKind = buildingDef(factionId, "pist").trains[0];
    const airDef = unitDef(factionId, airKind);
    const cap = airDef.cost < 90 ? 3 : 1;
    const flying = mine.filter((e) => !e.isBuilding && e.kind === airKind).length;
    if (flying < cap) tryTrain(state, owner, strip, airKind);
  }

  const barracksDone = buildings.some((b) => b.kind === "barinak" && b.constructed);
  if (barracksDone && (state.time > 70 || underAttack)) {
    ensureBuilding(state, owner, factionId, buildings, workers, "kule");
  }

  if (!underAttack && state.time > 62 && mem.attackLock <= 0 && fighters.length >= 4) {
    const enemyHq = nearestHostileHq(state, owner, hq);
    if (enemyHq) {
      const idle = fighters.filter((f) => !f.plan || f.plan.kind === "guard" || f.plan.kind === "move");
      const pack = idle.length >= 4 ? idle : fighters;
      const jx = (state.rng() - 0.5) * 90;
      const jy = (state.rng() - 0.5) * 90;
      command(state, {
        by: owner,
        type: "attackMove",
        ids: pack.map((f) => f.id),
        x: enemyHq.x + jx,
        y: enemyHq.y + jy,
      });
      mem.attackLock = 20;
    }
  }
}

function costOfUnit(factionId, kind) {
  return unitDef(factionId, kind).cost;
}

function freeWorker(workers) {
  return workers.find((w) => w.plan?.kind !== "construct") || null;
}

function resumeConstruction(state, owner, workers, buildings) {
  const pending = buildings
    .filter((b) => !b.constructed)
    .sort((a, b) => b.progress - a.progress);
  for (const building of pending) {
    const busy = workers.some((w) => w.plan?.kind === "construct" && w.plan.buildingId === building.id);
    if (busy) continue;
    const worker = freeWorker(workers);
    if (!worker) return;
    command(state, { by: owner, type: "construct", buildingId: building.id, workerIds: [worker.id] });
  }
}

function countKind(buildings, kind) {
  return buildings.filter((b) => b.kind === kind).length;
}

function ensureBuilding(state, owner, factionId, buildings, workers, kind) {
  if (countKind(buildings, kind) > 0) return false;
  const def = buildingDef(factionId, kind);
  if (state.players[owner].ash < def.cost) return false;
  const worker = freeWorker(workers);
  if (!worker) return false;
  const anchor = buildings.find((b) => b.kind === "ocak") || buildings[0];
  if (!anchor) return false;
  const spot = findSpot(state, owner, kind, anchor);
  if (!spot) return false;
  return command(state, { by: owner, type: "place", workerId: worker.id, kind, x: spot.x, y: spot.y }).ok;
}

function nearestHostileHq(state, owner, hq) {
  let best = null;
  let bestD = Infinity;
  for (const entity of state.entities) {
    if (!hostile(state, owner, entity.owner) || entity.kind !== "ocak" || entity.hp <= 0) continue;
    const d = hq ? Math.hypot(entity.x - hq.x, entity.y - hq.y) : 0;
    if (d < bestD) {
      bestD = d;
      best = entity;
    }
  }
  return best;
}

function findSpot(state, owner, kind, anchor) {
  const enemy = nearestHostileHq(state, owner, anchor);
  const bias = enemy ? Math.atan2(enemy.y - anchor.y, enemy.x - anchor.x) : 0;
  for (const dist of [130, 170, 210, 250, 300]) {
    for (let i = 0; i < 18; i++) {
      const ang = bias + (i / 18) * Math.PI * 2;
      const x = anchor.x + Math.cos(ang) * dist;
      const y = anchor.y + Math.sin(ang) * dist * 0.86;
      if (commandWouldFit(state, owner, kind, x, y)) return { x, y };
    }
  }
  return null;
}

function commandWouldFit(state, owner, kind, x, y) {
  return canPlace(state, owner, kind, x, y);
}

function tryTrain(state, owner, building, unitKind) {
  const result = command(state, { by: owner, type: "train", buildingId: building.id, unitKind });
  return result.ok;
}

function threatPoint(state, owner, buildings) {
  if (!buildings.length) return null;
  let sx = 0;
  let sy = 0;
  let n = 0;
  for (const foe of state.entities) {
    if (!hostile(state, owner, foe.owner) || foe.hp <= 0 || foe.isBuilding) continue;
    for (const b of buildings) {
      if (Math.hypot(foe.x - b.x, foe.y - b.y) < 460) {
        sx += foe.x;
        sy += foe.y;
        n += 1;
        break;
      }
    }
  }
  if (!n) return null;
  return { x: sx / n, y: sy / n };
}
