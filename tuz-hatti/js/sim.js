/** Tuz Hattı simülasyonu. DOM yok; tarayıcı ve node testleri aynı kodu çalıştırır. */

import {
  ARMOR_TAKEN,
  SUPPLY_CAP,
  buildingDef,
  defFor,
  structureLock,
  unitDef,
} from "./balance.js";
import { battlePlan } from "./maps.js";

export const CELL = 22;
export const BUILD_RADIUS = 290;

export function mulberry32(seed) {
  let a = seed >>> 0;
  return function rng() {
    a |= 0;
    a = (a + 0x6d2b79f5) | 0;
    let t = Math.imul(a ^ (a >>> 15), 1 | a);
    t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
    return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
  };
}

export function createMatch({
  seed = 1,
  playerFaction = "usa",
  enemyFaction = "rus",
  mapId = "anadolu",
  count = 2,
  factions = null,
  humans = null,
} = {}) {
  const rng = mulberry32(seed);
  const plan = battlePlan(mapId, count);
  const roster = factions && factions.length ? factions : [playerFaction, enemyFaction];
  const cols = Math.ceil(plan.map.w / CELL);
  const rows = Math.ceil(plan.map.h / CELL);
  const cells = cols * rows;
  const players = {};
  const visible = {};
  const explored = {};
  plan.slots.forEach((slot, index) => {
    players[index] = {
      faction: roster[index % roster.length],
      ash: 420,
      gathered: 0,
      kills: 0,
      team: slot.team,
      human: humans ? !!humans[index] : index === 0,
    };
    visible[index] = new Uint8Array(cells);
    explored[index] = new Uint8Array(cells);
  });
  const state = {
    seed,
    rng,
    time: 0,
    winner: null,
    nextId: 1,
    cols,
    rows,
    mapId: plan.map.id,
    mapW: plan.map.w,
    mapH: plan.map.h,
    terrainKey: plan.map.terrain,
    naval: !!plan.map.naval,
    battle: plan.battle,
    playerCount: plan.count,
    slots: plan.slots,
    entities: [],
    nodes: plan.nodes.map((node, index) => ({
      id: index + 1,
      x: node.x,
      y: node.y,
      amount: node.amount,
      max: node.amount,
      radius: 26,
      kind: node.kind,
    })),
    obstacles: plan.map.obstacles.map((obstacle) => ({ ...obstacle })),
    decor: [],
    players,
    visible,
    explored,
  };

  for (let i = 0; i < 18; i++) {
    state.decor.push({
      x: 40 + rng() * (state.mapW - 80),
      y: 40 + rng() * (state.mapH - 80),
      r: 10 + rng() * 18,
      kind: rng() > 0.55 ? "bush" : "crack",
    });
  }

  for (let index = 0; index < plan.count; index++) {
    spawnBase(state, index);
    const slot = plan.slots[index];
    revealDisk(state, index, slot.x, slot.y, 420);
  }
  recomputeFog(state);
  return state;
}

function blankPlayer(factionId) {
  return { faction: factionId, ash: 420, gathered: 0, kills: 0, team: 0, human: false };
}

function spawnBase(state, owner) {
  const base = state.slots[owner];
  const ang = Math.atan2(state.mapH / 2 - base.y, state.mapW / 2 - base.x);
  const hq = makeBuilding(state, owner, "ocak", base.x, base.y);
  hq.constructed = true;
  hq.progress = 1;
  hq.hp = hq.maxHp;
  hq.angle = ang;
  state.entities.push(hq);
  const side = ang + Math.PI / 2;
  const spots = [
    [base.x + Math.cos(ang) * 78 + Math.cos(side) * 36, base.y + Math.sin(ang) * 78 + Math.sin(side) * 36],
    [base.x + Math.cos(ang) * 78 - Math.cos(side) * 36, base.y + Math.sin(ang) * 78 - Math.sin(side) * 36],
  ];
  for (const [x, y] of spots) {
    const worker = makeUnit(state, owner, "tasiyici", x, y);
    worker.angle = ang;
    const node = nearestOre(state, x, y);
    if (node) worker.plan = { kind: "harvest", nodeId: node.id, phase: "toNode", gather: 0 };
    state.entities.push(worker);
  }
}

function makeBuilding(state, owner, kind, x, y) {
  const def = buildingDef(state.players[owner].faction, kind);
  const ang = Math.atan2(state.mapH / 2 - y, state.mapW / 2 - x);
  return {
    id: state.nextId++,
    owner,
    kind,
    isBuilding: true,
    x,
    y,
    hp: Math.round(def.hp * 0.2),
    maxHp: def.hp,
    radius: def.radius,
    angle: ang,
    constructed: false,
    progress: 0,
    cooldown: 0,
    queue: [],
    rallyX: x + Math.cos(ang) * (def.radius + 40),
    rallyY: y + Math.sin(ang) * (def.radius + 40),
    carry: 0,
    plan: null,
  };
}

function makeUnit(state, owner, kind, x, y) {
  const def = unitDef(state.players[owner].faction, kind);
  return {
    id: state.nextId++,
    owner,
    kind,
    isBuilding: false,
    x,
    y,
    hp: def.hp,
    maxHp: def.hp,
    radius: def.radius,
    angle: Math.atan2(state.mapH / 2 - y, state.mapW / 2 - x),
    constructed: true,
    progress: 1,
    cooldown: 0,
    queue: null,
    rallyX: x,
    rallyY: y,
    carry: 0,
    plan: null,
  };
}

export function factionOf(state, owner) {
  return state.players[owner].faction;
}

export function entityDef(state, entity) {
  return defFor(factionOf(state, entity.owner), entity);
}

export function getEntity(state, id) {
  return state.entities.find((e) => e.id === id) ?? null;
}

export function supplyInfo(state, owner) {
  let cap = 0;
  let used = 0;
  for (const e of state.entities) {
    if (e.owner !== owner || e.hp <= 0) continue;
    if (e.isBuilding) {
      const def = entityDef(state, e);
      if (e.constructed) cap += def.supply || 0;
      if (e.queue) {
        for (const job of e.queue) used += job.supply;
      }
    } else {
      used += entityDef(state, e).supply || 1;
    }
  }
  return { used, cap: Math.min(SUPPLY_CAP, cap) };
}

export function canPlace(state, owner, kind, x, y) {
  const def = buildingDef(state.players[owner].faction, kind);
  if (!def || def.hq) return false;
  const r = def.radius;
  if (x < r + 16 || y < r + 16 || x > state.mapW - r - 16 || y > state.mapH - r - 16) return false;

  let nearFriendly = false;
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    const dist = Math.hypot(e.x - x, e.y - y);
    if (e.isBuilding && e.owner === owner && e.constructed && dist <= BUILD_RADIUS + r) {
      nearFriendly = true;
    }
    if (dist < e.radius + r + 10) return false;
  }
  if (!nearFriendly) return false;

  for (const o of state.obstacles) {
    if (Math.hypot(o.x - x, o.y - y) < o.r + r + 8) return false;
  }
  for (const n of state.nodes) {
    if (n.amount > 0 && Math.hypot(n.x - x, n.y - y) < n.radius + r + 10) return false;
  }
  if ((def.navalYard || def.shore) && !nearWater(state, x, y, 72)) return false;
  if (!structureLock(state.players[owner].faction, kind, state.entities, owner, state.naval).ok) return false;
  return true;
}

export function command(state, cmd) {
  if (!cmd || state.winner !== null) return { ok: false, reason: "over" };
  const owner = cmd.by;
  const player = state.players[owner];
  if (!player) return { ok: false, reason: "owner" };

  switch (cmd.type) {
    case "move":
    case "attackMove": {
      const ids = livingOwnedUnits(state, owner, cmd.ids);
      if (!ids.length) return { ok: false, reason: "units" };
      const spots = spread(cmd.x, cmd.y, ids.length);
      ids.forEach((e, i) => {
        e.plan = { kind: cmd.type === "move" ? "move" : "attackMove", x: spots[i].x, y: spots[i].y };
      });
      return { ok: true };
    }
    case "attack": {
      const target = getEntity(state, cmd.targetId);
      if (!target || target.hp <= 0 || !hostile(state, owner, target.owner)) return { ok: false, reason: "target" };
      const ids = livingOwnedUnits(state, owner, cmd.ids);
      if (!ids.length) return { ok: false, reason: "units" };
      for (const e of ids) {
        if ((entityDef(state, e).damage || 0) <= 0) {
          e.plan = { kind: "move", x: target.x, y: target.y };
        } else {
          e.plan = { kind: "attack", targetId: target.id, leash: 720 };
        }
      }
      return { ok: true };
    }
    case "stop": {
      for (const id of cmd.ids || []) {
        const e = ownedAlive(state, owner, id);
        if (!e) continue;
        if (e.isBuilding) {
          refundQueue(state, e);
          e.queue = [];
        } else {
          e.plan = { kind: "guard", x: e.x, y: e.y };
        }
      }
      return { ok: true };
    }
    case "harvest": {
      let any = false;
      for (const id of cmd.ids || []) {
        const e = ownedAlive(state, owner, id);
        if (!e || e.isBuilding || !entityDef(state, e).worker) continue;
        if (e.plan?.kind === "construct") continue;
        const node = nearestOre(state, e.x, e.y);
        if (!node && e.carry <= 0) continue;
        e.plan = {
          kind: "harvest",
          nodeId: node ? node.id : e.plan?.nodeId ?? 0,
          phase: e.carry > 0 ? "return" : "toNode",
          gather: 0,
        };
        any = true;
      }
      return any ? { ok: true } : { ok: false, reason: "worker" };
    }
    case "train": {
      const b = ownedAlive(state, owner, cmd.buildingId);
      if (!b || !b.isBuilding || !b.constructed) return { ok: false, reason: "building" };
      const bdef = entityDef(state, b);
      if (!bdef.trains?.includes(cmd.unitKind)) return { ok: false, reason: "unit" };
      if ((b.queue?.length || 0) >= 5) return { ok: false, reason: "queue" };
      const udef = unitDef(player.faction, cmd.unitKind);
      if (player.ash < udef.cost) return { ok: false, reason: "ash" };
      const supply = supplyInfo(state, owner);
      if (supply.used + udef.supply > supply.cap) return { ok: false, reason: "supply" };
      player.ash -= udef.cost;
      b.queue.push({
        kind: cmd.unitKind,
        left: udef.time,
        total: udef.time,
        cost: udef.cost,
        supply: udef.supply,
      });
      return { ok: true };
    }
    case "place": {
      const worker = ownedAlive(state, owner, cmd.workerId);
      if (!worker || worker.isBuilding || !entityDef(state, worker).worker) {
        return { ok: false, reason: "worker" };
      }
      const def = buildingDef(player.faction, cmd.kind);
      if (!def || def.hq) return { ok: false, reason: "kind" };
      if (player.ash < def.cost) return { ok: false, reason: "ash" };
      if (!canPlace(state, owner, cmd.kind, cmd.x, cmd.y)) return { ok: false, reason: "space" };
      player.ash -= def.cost;
      const building = makeBuilding(state, owner, cmd.kind, cmd.x, cmd.y);
      state.entities.push(building);
      worker.plan = { kind: "construct", buildingId: building.id };
      return { ok: true, id: building.id };
    }
    case "construct": {
      const building = ownedAlive(state, owner, cmd.buildingId);
      if (!building || !building.isBuilding || building.constructed) return { ok: false, reason: "building" };
      let any = false;
      for (const id of cmd.workerIds || []) {
        const e = ownedAlive(state, owner, id);
        if (!e || e.isBuilding || !entityDef(state, e).worker) continue;
        e.plan = { kind: "construct", buildingId: building.id };
        any = true;
      }
      return any ? { ok: true } : { ok: false, reason: "worker" };
    }
    case "rally": {
      const b = ownedAlive(state, owner, cmd.buildingId);
      if (!b || !b.isBuilding) return { ok: false, reason: "building" };
      b.rallyX = clamp(cmd.x, 20, state.mapW - 20);
      b.rallyY = clamp(cmd.y, 20, state.mapH - 20);
      return { ok: true };
    }
    default:
      return { ok: false, reason: "type" };
  }
}

function refundQueue(state, building) {
  if (!building.queue?.length) return;
  const player = state.players[building.owner];
  for (const job of building.queue) player.ash += job.cost;
  building.queue = [];
}

function ownedAlive(state, owner, id) {
  const e = getEntity(state, id);
  if (!e || e.owner !== owner || e.hp <= 0) return null;
  return e;
}

function livingOwnedUnits(state, owner, ids) {
  const out = [];
  for (const id of ids || []) {
    const e = ownedAlive(state, owner, id);
    if (e && !e.isBuilding) out.push(e);
  }
  return out;
}

function spread(x, y, n) {
  if (n <= 1) return [{ x, y }];
  const rad = 16 + n * 3;
  const spots = [];
  for (let i = 0; i < n; i++) {
    const a = (i / n) * Math.PI * 2;
    spots.push({ x: x + Math.cos(a) * rad, y: y + Math.sin(a) * rad });
  }
  return spots;
}

export function step(state, dt) {
  const events = [];
  if (state.winner !== null) return events;
  dt = Math.max(0, Math.min(0.05, dt));
  state.time += dt;
  const spawned = [];

  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    if (e.cooldown > 0) e.cooldown -= dt;
    const def = entityDef(state, e);
    if (e.isBuilding) {
      if (e.constructed) updateTraining(state, e, dt, spawned, events);
      if (def.turret && e.constructed) fireAtNearest(state, e, def, events);
      continue;
    }
    updateUnit(state, e, def, dt, events);
  }

  for (const s of spawned) state.entities.push(s);
  separateUnits(state);

  const alive = [];
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    alive.push(e);
  }
  state.entities = alive;

  recomputeFog(state);
  checkWinner(state);
  return events;
}

function updateTraining(state, building, dt, spawned, events) {
  if (!building.queue.length) return;
  const job = building.queue[0];
  job.left -= dt;
  if (job.left > 0) return;
  building.queue.shift();
  const udef = unitDef(factionOf(state, building.owner), job.kind);
  const dx = building.rallyX - building.x;
  const dy = building.rallyY - building.y;
  const d = Math.hypot(dx, dy) || 1;
  const dist = building.radius + udef.radius + 8;
  const sx = building.x + (dx / d) * dist;
  const sy = building.y + (dy / d) * dist;
  const spot = udef.air ? { x: sx, y: sy } : freeSpot(state, sx, sy, udef.radius, building.id);
  const unit = makeUnit(state, building.owner, job.kind, spot.x, spot.y);
  if (udef.worker) {
    const node = nearestOre(state, unit.x, unit.y);
    if (node) unit.plan = { kind: "harvest", nodeId: node.id, phase: "toNode", gather: 0 };
  } else {
    unit.plan = { kind: "move", x: building.rallyX, y: building.rallyY };
  }
  spawned.push(unit);
  events.push({ type: "trained", owner: building.owner, kind: job.kind, x: unit.x, y: unit.y });
}

function updateUnit(state, e, def, dt, events) {
  const plan = e.plan;
  if (!plan || plan.kind === "guard") {
    const home = plan?.kind === "guard" ? plan : { x: e.x, y: e.y };
    if (!e.plan) e.plan = { kind: "guard", x: e.x, y: e.y };
    if ((def.damage || 0) > 0) {
      const foe = nearestEnemy(state, e, (def.range || 0) + 28);
      if (foe) e.plan = { kind: "attack", targetId: foe.id, leash: 200, homeX: home.x, homeY: home.y };
    }
    return;
  }

  if (plan.kind === "move") {
    const arrived = moveToward(state, e, plan.x, plan.y, def.speed * dt, 10, null);
    if (arrived) e.plan = { kind: "guard", x: e.x, y: e.y };
    return;
  }

  if (plan.kind === "attackMove") {
    const acquire = Math.max((def.sight || 160) * 0.85, (def.range || 40) + 90);
    const foe = nearestEnemy(state, e, acquire);
    if (foe && (def.damage || 0) > 0) {
      e.plan = { kind: "attack", targetId: foe.id, leash: 320, resume: { kind: "attackMove", x: plan.x, y: plan.y } };
      return;
    }
    const arrived = moveToward(state, e, plan.x, plan.y, def.speed * dt, 12, null);
    if (arrived) e.plan = { kind: "guard", x: e.x, y: e.y };
    return;
  }

  if (plan.kind === "attack") {
    const target = getEntity(state, plan.targetId);
    if (!target || target.hp <= 0) {
      e.plan = plan.resume || { kind: "guard", x: plan.homeX ?? e.x, y: plan.homeY ?? e.y };
      return;
    }
    if (plan.homeX !== undefined) {
      const away = Math.hypot(e.x - plan.homeX, e.y - plan.homeY);
      if (away > (plan.leash || 200)) {
        e.plan = { kind: "move", x: plan.homeX, y: plan.homeY };
        return;
      }
    }
    engage(state, e, def, target, dt, events);
    return;
  }

  if (plan.kind === "harvest") {
    updateHarvest(state, e, def, dt, events);
    return;
  }

  if (plan.kind === "construct") {
    updateConstruct(state, e, def, dt, events);
  }
}

function shotReach(def, target) {
  return (def.range || 0) + target.radius;
}

function engage(state, e, def, target, dt, events) {
  const dist = Math.hypot(target.x - e.x, target.y - e.y);
  const reach = shotReach(def, target);
  const minR = def.minRange || 0;
  if (minR && dist < minR) {
    const ang = Math.atan2(e.y - target.y, e.x - target.x);
    moveToward(state, e, e.x + Math.cos(ang) * 90, e.y + Math.sin(ang) * 90, def.speed * dt, 4, null);
    return;
  }
  if (dist > reach) {
    const stand = target.radius + e.radius + 1;
    moveToward(state, e, target.x, target.y, def.speed * dt, Math.min(stand, reach - 2), target.isBuilding ? target.id : null);
    return;
  }
  face(e, target.x, target.y, dt);
  if (e.cooldown <= 0 && dist >= minR * 0.85) {
    deal(state, e, def, target, events);
  }
}

function fireAtNearest(state, e, def, events) {
  if (e.cooldown > 0 || !(def.damage > 0)) return;
  const reach = shotReach(def, { radius: 0 });
  const prefer = (target, tdef) => {
    if (def.role === "air") return !!tdef.air;
    if (def.role === "ground") return !tdef.air && !tdef.naval && !target.isBuilding;
    if (def.role === "sea") return !!tdef.naval;
    return true;
  };
  const fallback = (target, tdef) => {
    if (def.role === "air") return !tdef.naval;
    if (def.role === "ground") return !tdef.air;
    if (def.role === "sea") return !tdef.air;
    return true;
  };
  const target = (def.role ? nearestEnemy(state, e, reach, prefer) : null) || nearestEnemy(state, e, reach, def.role ? fallback : null);
  if (!target) return;
  const dist = Math.hypot(target.x - e.x, target.y - e.y);
  if (dist > shotReach(def, target)) return;
  face(e, target.x, target.y, 1);
  deal(state, e, def, target, events);
}

function deal(state, attacker, def, target, events) {
  if (target.hp <= 0) return;
  const tdef = entityDef(state, target);
  const bonus = def.bonus?.[tdef.armor] ?? 1;
  const taken = ARMOR_TAKEN[tdef.armor] ?? 1;
  const amount = def.damage * bonus * taken;
  target.hp -= amount;
  attacker.cooldown = def.cooldown || 0.8;
  events.push({
    type: "shot",
    x1: attacker.x,
    y1: attacker.y,
    x2: target.x,
    y2: target.y,
    owner: attacker.owner,
    heavy: def.armor === "heavy" || !!def.turret,
    air: !!def.air,
    targetId: target.id,
    targetKind: target.kind,
    targetOwner: target.owner,
  });
  if (target.hp <= 0) kill(state, target, attacker, events);
}

function kill(state, victim, killer, events) {
  victim.hp = 0;
  if (victim.isBuilding) {
    const def = entityDef(state, victim);
    let refund = 0;
    if (!victim.constructed) refund += Math.round(def.cost * 0.5);
    if (victim.queue?.length) {
      for (const job of victim.queue) refund += job.cost;
      victim.queue = [];
    }
    if (refund > 0) state.players[victim.owner].ash += refund;
  }
  if (killer && killer.owner !== victim.owner) state.players[killer.owner].kills += 1;
  events.push({
    type: "death",
    x: victim.x,
    y: victim.y,
    owner: victim.owner,
    kind: victim.kind,
    building: victim.isBuilding,
    killer: killer ? killer.owner : null,
  });
}

function updateHarvest(state, e, def, dt, events) {
  const plan = e.plan;
  if (e.carry > 0 && plan.phase !== "return") plan.phase = "return";
  if (plan.phase === "return") {
    const drop = nearestDropoff(state, e.owner, e.x, e.y);
    if (!drop) {
      e.plan = { kind: "guard", x: e.x, y: e.y };
      return;
    }
    const arrived = moveToward(state, e, drop.x, drop.y, def.speed * dt, drop.radius + e.radius + 6, drop.id);
    if (arrived) {
      state.players[e.owner].ash += e.carry;
      state.players[e.owner].gathered += e.carry;
      events.push({ type: "bank", owner: e.owner, amount: e.carry, x: drop.x, y: drop.y });
      e.carry = 0;
      const next = nearestOre(state, e.x, e.y);
      if (!next) e.plan = { kind: "guard", x: e.x, y: e.y };
      else e.plan = { kind: "harvest", nodeId: next.id, phase: "toNode", gather: 0 };
    }
    return;
  }

  let node = state.nodes.find((n) => n.id === plan.nodeId && n.amount > 0);
  if (!node) node = nearestOre(state, e.x, e.y);
  if (!node) {
    e.plan = { kind: "guard", x: e.x, y: e.y };
    return;
  }
  plan.nodeId = node.id;
  if (plan.phase !== "gather") {
    const arrived = moveToward(state, e, node.x, node.y, def.speed * dt, 8, null);
    if (arrived) {
      plan.phase = "gather";
      plan.gather = 0;
    }
    return;
  }
  if (Math.hypot(node.x - e.x, node.y - e.y) > node.radius + e.radius) {
    plan.phase = "toNode";
    return;
  }
  face(e, node.x, node.y, dt);
  plan.gather += dt;
  if (plan.gather < (def.gather || 2)) return;
  const take = Math.min(def.carry || 40, node.amount);
  node.amount -= take;
  e.carry = take;
  plan.phase = "return";
  plan.gather = 0;
  if (take > 0) events.push({ type: "scoop", owner: e.owner, x: node.x, y: node.y, amount: take });
}

function updateConstruct(state, e, def, dt, events) {
  const b = getEntity(state, e.plan.buildingId);
  if (!b || b.hp <= 0 || !b.isBuilding) {
    const node = nearestOre(state, e.x, e.y);
    e.plan = node
      ? { kind: "harvest", nodeId: node.id, phase: e.carry > 0 ? "return" : "toNode", gather: 0 }
      : { kind: "guard", x: e.x, y: e.y };
    return;
  }
  if (b.constructed) {
    const node = nearestOre(state, e.x, e.y);
    e.plan = node
      ? { kind: "harvest", nodeId: node.id, phase: e.carry > 0 ? "return" : "toNode", gather: 0 }
      : { kind: "guard", x: e.x, y: e.y };
    return;
  }
  const bdef = entityDef(state, b);
  const arrived = moveToward(state, e, b.x, b.y, def.speed * dt, b.radius + e.radius + 4, b.id);
  if (!arrived) return;
  face(e, b.x, b.y, dt);
  b.progress += dt / bdef.time;
  b.hp = Math.min(b.maxHp, b.maxHp * (0.2 + 0.8 * b.progress));
  if (b.progress >= 1) {
    b.progress = 1;
    b.constructed = true;
    b.hp = b.maxHp;
    events.push({ type: "built", owner: b.owner, kind: b.kind, x: b.x, y: b.y });
  }
}

function nearestEnemy(state, e, range, accept) {
  let best = null;
  let bestD = Infinity;
  for (const o of state.entities) {
    if (!hostile(state, e.owner, o.owner) || o.hp <= 0) continue;
    if (o.isBuilding && !o.constructed && o.progress <= 0) continue;
    if (accept && !accept(o, entityDef(state, o))) continue;
    const d = Math.hypot(o.x - e.x, o.y - e.y);
    if (d <= range + o.radius && d < bestD) {
      if (!o.isBuilding && !cellVisible(state, e.owner, o.x, o.y) && d > (entityDef(state, e).sight || range)) {
        continue;
      }
      best = o;
      bestD = d;
    }
  }
  return best;
}

function nearestOre(state, x, y) {
  let best = null;
  let bd = Infinity;
  for (const n of state.nodes) {
    if (n.amount <= 0) continue;
    const d = Math.hypot(n.x - x, n.y - y);
    if (d < bd) {
      bd = d;
      best = n;
    }
  }
  return best;
}

function nearestDropoff(state, owner, x, y) {
  let best = null;
  let bd = Infinity;
  for (const e of state.entities) {
    if (e.owner !== owner || e.hp <= 0 || !e.isBuilding || !e.constructed) continue;
    if (!entityDef(state, e).dropoff) continue;
    const d = Math.hypot(e.x - x, e.y - y);
    if (d < bd) {
      bd = d;
      best = e;
    }
  }
  return best;
}

function moveToward(state, e, tx, ty, step, stopDist, ignoreId) {
  let dx = tx - e.x;
  let dy = ty - e.y;
  let dist = Math.hypot(dx, dy);
  if (dist <= stopDist + 1.25) {
    face(e, tx, ty, 0.05);
    return true;
  }
  let vx = dx / dist;
  let vy = dy / dist;
  const look = isAir(state, e) ? 18 : 32;
  const naval = isNaval(state, e);
  if (!isAir(state, e) && circleBlocked(state, e.x + vx * look, e.y + vy * look, e.radius * 0.75, ignoreId, naval)) {
    const base = Math.atan2(vy, vx);
    let steered = false;
    for (const off of [0.55, -0.55, 1.05, -1.05, 1.7, -1.7, 2.4, -2.4, 3.1]) {
      const ax = Math.cos(base + off);
      const ay = Math.sin(base + off);
      if (!circleBlocked(state, e.x + ax * look, e.y + ay * look, e.radius * 0.7, ignoreId, naval)) {
        vx = ax;
        vy = ay;
        steered = true;
        break;
      }
    }
    if (!steered) {
      vx *= 0.28;
      vy *= 0.28;
    }
  }
  const travel = Math.min(step, dist - stopDist);
  e.x += vx * travel;
  e.y += vy * travel;
  e.x = clamp(e.x, e.radius, state.mapW - e.radius);
  e.y = clamp(e.y, e.radius, state.mapH - e.radius);
  face(e, e.x + vx, e.y + vy, 0.16);
  return Math.hypot(tx - e.x, ty - e.y) <= stopDist + 1.25;
}

function face(e, x, y, dt) {
  const target = Math.atan2(y - e.y, x - e.x);
  let d = target - e.angle;
  while (d > Math.PI) d -= Math.PI * 2;
  while (d < -Math.PI) d += Math.PI * 2;
  const turn = e.isBuilding ? 6 : 11;
  const max = Math.max(0.05, dt) * turn;
  if (Math.abs(d) < max) e.angle = target;
  else e.angle += Math.sign(d) * max;
}

function nearWater(state, x, y, pad) {
  return state.obstacles.some((o) => o.kind === "water" && Math.hypot(o.x - x, o.y - y) <= o.r + pad);
}

function circleBlocked(state, x, y, r, ignoreId, naval) {
  if (x < r || y < r || x > state.mapW - r || y > state.mapH - r) return true;
  for (const o of state.obstacles) {
    if (naval && o.kind === "water") continue;
    if (Math.hypot(o.x - x, o.y - y) < o.r + r) return true;
  }
  for (const b of state.entities) {
    if (!b.isBuilding || b.hp <= 0 || b.id === ignoreId) continue;
    if (Math.hypot(b.x - x, b.y - y) < b.radius + r) return true;
  }
  return false;
}

function isAir(state, e) {
  return !e.isBuilding && !!entityDef(state, e).air;
}

function isNaval(state, e) {
  return !e.isBuilding && !!entityDef(state, e).naval;
}

function pullToWater(state, u) {
  if (nearWater(state, u.x, u.y, 46)) return;
  let best = null;
  let bestD = Infinity;
  for (const obstacle of state.obstacles) {
    if (obstacle.kind !== "water") continue;
    const d = Math.hypot(obstacle.x - u.x, obstacle.y - u.y);
    if (d < bestD) {
      bestD = d;
      best = obstacle;
    }
  }
  if (!best) return;
  const dx = best.x - u.x;
  const dy = best.y - u.y;
  const dist = Math.hypot(dx, dy) || 1;
  u.x += (dx / dist) * 8;
  u.y += (dy / dist) * 8;
}

function separateUnits(state) {
  const units = state.entities.filter((e) => !e.isBuilding && e.hp > 0);
  for (let i = 0; i < units.length; i++) {
    for (let j = i + 1; j < units.length; j++) {
      const a = units[i];
      const b = units[j];
      if (isAir(state, a) !== isAir(state, b)) continue;
      let dx = b.x - a.x;
      let dy = b.y - a.y;
      const min = a.radius + b.radius - 1;
      const d2 = dx * dx + dy * dy;
      if (d2 === 0) {
        a.x -= 0.6;
        b.x += 0.6;
        continue;
      }
      if (d2 >= min * min) continue;
      const d = Math.sqrt(d2);
      const push = (min - d) / 2;
      dx /= d;
      dy /= d;
      a.x -= dx * push;
      a.y -= dy * push;
      b.x += dx * push;
      b.y += dy * push;
    }
  }
  for (const u of units) {
    if (isAir(state, u)) {
      u.x = clamp(u.x, u.radius, state.mapW - u.radius);
      u.y = clamp(u.y, u.radius, state.mapH - u.radius);
      continue;
    }
    for (const o of state.obstacles) {
      if (isNaval(state, u) && o.kind === "water") continue;
      pushOut(u, o.x, o.y, o.r);
    }
    if (isNaval(state, u)) pullToWater(state, u);
    for (const b of state.entities) {
      if (!b.isBuilding || b.hp <= 0) continue;
      if (u.plan?.kind === "construct" && u.plan.buildingId === b.id) continue;
      if (u.plan?.kind === "harvest" && u.plan.phase === "return") {
        const drop = nearestDropoff(state, u.owner, u.x, u.y);
        if (drop && drop.id === b.id) continue;
      }
      pushOut(u, b.x, b.y, b.radius);
    }
    u.x = clamp(u.x, u.radius, state.mapW - u.radius);
    u.y = clamp(u.y, u.radius, state.mapH - u.radius);
  }
}

function pushOut(u, x, y, r) {
  let dx = u.x - x;
  let dy = u.y - y;
  const min = r + u.radius;
  const d2 = dx * dx + dy * dy;
  if (d2 >= min * min) return;
  if (d2 < 0.0001) {
    u.x += min;
    return;
  }
  const d = Math.sqrt(d2);
  dx /= d;
  dy /= d;
  u.x = x + dx * min;
  u.y = y + dy * min;
}

function freeSpot(state, x, y, radius, ignoreId) {
  if (!circleBlocked(state, x, y, radius, ignoreId)) return { x, y };
  for (let i = 0; i < 14; i++) {
    const a = (i / 14) * Math.PI * 2;
    const nx = x + Math.cos(a) * (radius + 18);
    const ny = y + Math.sin(a) * (radius + 18);
    if (!circleBlocked(state, nx, ny, radius, ignoreId)) return { x: nx, y: ny };
  }
  return { x, y };
}

function checkWinner(state) {
  const alive = new Set();
  for (const entity of state.entities) {
    if (entity.kind === "ocak" && entity.hp > 0) alive.add(state.players[entity.owner].team);
  }
  if (alive.size === 0) state.winner = "draw";
  else if (alive.size === 1) state.winner = [...alive][0];
}

export function hostile(state, owner, other) {
  if (owner === other) return false;
  const left = state.players[owner];
  const right = state.players[other];
  if (!left || !right) return true;
  return left.team !== right.team;
}

export function cellIndex(state, x, y) {
  const cx = clamp(Math.floor(x / CELL), 0, state.cols - 1);
  const cy = clamp(Math.floor(y / CELL), 0, state.rows - 1);
  return cy * state.cols + cx;
}

export function cellVisible(state, owner, x, y) {
  return state.visible[owner][cellIndex(state, x, y)] === 1;
}

export function cellExplored(state, owner, x, y) {
  return state.explored[owner][cellIndex(state, x, y)] === 1;
}

export function canSeeEntity(state, viewer, entity) {
  const viewerTeam = state.players[viewer]?.team;
  const ownerTeam = state.players[entity.owner]?.team;
  if (entity.owner === viewer || (viewerTeam !== undefined && viewerTeam === ownerTeam)) return true;
  if (entity.isBuilding && cellExplored(state, viewer, entity.x, entity.y)) return true;
  return cellVisible(state, viewer, entity.x, entity.y);
}

function revealDisk(state, owner, x, y, r) {
  const r2 = r * r;
  const x0 = clamp(Math.floor((x - r) / CELL), 0, state.cols - 1);
  const x1 = clamp(Math.floor((x + r) / CELL), 0, state.cols - 1);
  const y0 = clamp(Math.floor((y - r) / CELL), 0, state.rows - 1);
  const y1 = clamp(Math.floor((y + r) / CELL), 0, state.rows - 1);
  for (let cy = y0; cy <= y1; cy++) {
    for (let cx = x0; cx <= x1; cx++) {
      const px = cx * CELL + CELL / 2;
      const py = cy * CELL + CELL / 2;
      const dx = px - x;
      const dy = py - y;
      if (dx * dx + dy * dy <= r2) state.explored[owner][cy * state.cols + cx] = 1;
    }
  }
}

export function recomputeFog(state) {
  const ids = Object.keys(state.players).map(Number);
  for (const id of ids) state.visible[id].fill(0);
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    const def = entityDef(state, e);
    let r = def.sight || 0;
    if (e.isBuilding && !e.constructed) r = 100;
    if (r <= 0) continue;
    const r2 = r * r;
    const x0 = clamp(Math.floor((e.x - r) / CELL), 0, state.cols - 1);
    const x1 = clamp(Math.floor((e.x + r) / CELL), 0, state.cols - 1);
    const y0 = clamp(Math.floor((e.y - r) / CELL), 0, state.rows - 1);
    const y1 = clamp(Math.floor((e.y + r) / CELL), 0, state.rows - 1);
    const vis = state.visible[e.owner];
    const exp = state.explored[e.owner];
    for (let cy = y0; cy <= y1; cy++) {
      for (let cx = x0; cx <= x1; cx++) {
        const px = cx * CELL + CELL / 2;
        const py = cy * CELL + CELL / 2;
        const dx = px - e.x;
        const dy = py - e.y;
        if (dx * dx + dy * dy > r2) continue;
        const i = cy * state.cols + cx;
        vis[i] = 1;
        exp[i] = 1;
      }
    }
  }
  for (const id of ids) {
    for (const ally of ids) {
      if (ally === id || state.players[ally].team !== state.players[id].team) continue;
      const vis = state.visible[id];
      const exp = state.explored[id];
      const otherVis = state.visible[ally];
      const otherExp = state.explored[ally];
      for (let i = 0; i < vis.length; i++) {
        if (otherVis[i]) vis[i] = 1;
        if (otherExp[i]) exp[i] = 1;
      }
    }
  }
}

export function pickEntity(state, x, y, viewer) {
  let best = null;
  let score = Infinity;
  for (const e of state.entities) {
    if (e.hp <= 0) continue;
    if (viewer !== undefined && !canSeeEntity(state, viewer, e)) continue;
    const d = Math.hypot(e.x - x, e.y - y);
    if (d > e.radius + 12) continue;
    const s = d - (e.isBuilding ? 0 : 14);
    if (s < score) {
      score = s;
      best = e;
    }
  }
  return best;
}

function clamp(v, a, b) {
  return Math.max(a, Math.min(b, v));
}
