/** Dokunmatik arayüz. Sürükle kaydırır, dokunuş seçer veya emir verir. */

import { GFX_PRESETS, suggestTier, tierAbove } from "./gfx.js";
import { BUILD_RULES, FACTION_IDS, buildingDef, faction, structureLock, unitDef } from "./balance.js";
import { MAP_IDS, MATCH_SIZES, battlePlan, mapById } from "./maps.js";
import { applyPack, hostRoom, joinRoom, makeCode, packState } from "./net.js";
import { playEvent, unlockAudio } from "./audio.js";
import { aiAct, createAiMemory } from "./ai.js";
import { drawMinimap, drawWorld } from "./render.js";
import {
  canPlace,
  canSeeEntity,
  command,
  createMatch,
  entityDef,
  getEntity,
  hostile,
  pickEntity,
  recomputeFog,
  step,
  supplyInfo,
} from "./sim.js";

const REASONS = {
  ash: "Yakıt yetmiyor",
  supply: "İkmal dolu",
  space: "Buraya kurulamaz",
  worker: "Uygun ikmal aracı yok",
  queue: "Üretim kuyruğu dolu",
  building: "Bu bina hazır değil",
  units: "Önce birlik seç",
  target: "Hedef yok",
};

const APP = { code: 6, name: "2.4" };
const SIZE_LABEL = { 2: "1'e 1", 3: "Herkes", 4: "2'ye 2", 6: "3'e 3", 8: "4'e 4" };
const VERSION_URL =
  "https://cdn.jsdelivr.net/gh/arsangaz01/arsan-gaz-erp-releases@cursor/tuz-hatti-rts-f116/tuz-hatti/version.json";

const menu = document.querySelector("#menu");
const stage = document.querySelector("#stage");
const canvas = document.querySelector("#view");
const minimap = document.querySelector("#minimap");
const bar = document.querySelector("#bar");
const ashEl = document.querySelector("#ash");
const supplyEl = document.querySelector("#supply");
const hintEl = document.querySelector("#hint");
const inspectEl = document.querySelector("#inspect");
const toastEl = document.querySelector("#toast");
const resultEl = document.querySelector("#result");
const resultTitle = document.querySelector("#result-title");
const resultBody = document.querySelector("#result-body");
const pauseBtn = document.querySelector("#pause");
const portraitBtn = document.querySelector("#play-portrait");

const ctx = canvas.getContext("2d");
const view = {
  camX: 0,
  camY: 0,
  zoom: 1,
  canvasWidth: 1,
  canvasHeight: 1,
  selected: new Set(),
  fx: [],
  ghost: null,
  box: null,
  rally: null,
  terrain: null,
  terrainSeed: null,
};

let match = null;
let aiMem = [];
let playerFaction = "tur";
let enemyFaction = "rus";
let selectedMap = "anadolu";
let selectedCount = 2;
let me = 0;
let role = "local";
let net = null;
let lobby = null;
let snapAcc = 0;
let paused = false;
let tool = null;
let pointer = null;
let hover = null;
let hudSig = "";
let toastTimer = 0;
let last = 0;
let running = false;

document.querySelector("#start").addEventListener("click", () => {
  unlockAudio();
  startMatch();
});

document.querySelector("#again").addEventListener("click", () => {
  unlockAudio();
  startMatch();
});

function leaveMatch() {
  running = false;
  match = null;
  if (net) {
    net.close();
    net = null;
  }
  role = "local";
  lobby = null;
  me = 0;
  resultEl.classList.add("hidden");
  stage.classList.add("hidden");
  menu.classList.remove("hidden");
  sayRoom("Yapay zekâ boş koltukları doldurur. Oda kodu ile karşılıklı da oynanır.");
}

document.querySelector("#back-menu").addEventListener("click", leaveMatch);
document.querySelector("#leave")?.addEventListener("click", () => {
  unlockAudio();
  leaveMatch();
});

pauseBtn.addEventListener("click", () => {
  if (role === "guest") {
    toast("Ev sahibi duraklatır");
    return;
  }
  paused = !paused;
  pauseBtn.textContent = paused ? "Sürdür" : "Duraklat";
  pauseBtn.setAttribute("aria-pressed", paused ? "true" : "false");
});

portraitBtn.addEventListener("click", () => {
  document.body.classList.add("allow-portrait");
});

window.addEventListener("resize", () => {
  if (match) resize();
});

window.addEventListener("keydown", (ev) => {
  if (!match) return;
  if (ev.key === "Escape") {
    tool = null;
    view.selected.clear();
    refreshHud(true);
  } else if (ev.key === " ") {
    ev.preventDefault();
    pauseBtn.click();
  } else if (ev.key === "h" || ev.key === "H") focusHome();
  else if (ev.key === "a" || ev.key === "A") armAttack();
  else if (ev.key === "s" || ev.key === "S") issueStop();
  else if (ev.key === "ArrowLeft") view.camX -= 40;
  else if (ev.key === "ArrowRight") view.camX += 40;
  else if (ev.key === "ArrowUp") view.camY -= 40;
  else if (ev.key === "ArrowDown") view.camY += 40;
  clampCam();
});

canvas.addEventListener("pointerdown", onPointerDown);
canvas.addEventListener("pointermove", onPointerMove);
canvas.addEventListener("pointerup", onPointerUp);
canvas.addEventListener("pointercancel", onPointerUp);
canvas.addEventListener("contextmenu", (ev) => ev.preventDefault());

minimap.addEventListener("pointerdown", onMinimap);
minimap.addEventListener("pointermove", (ev) => {
  if (ev.buttons) onMinimap(ev);
});

function factionsFor(count) {
  const plan = battlePlan(selectedMap, count);
  const pool = FACTION_IDS.filter((id) => id !== playerFaction);
  const foe = enemyFaction === playerFaction ? pool[0] : enemyFaction;
  const rest = pool.filter((id) => id !== foe);
  let ally = 0;
  let enemy = 0;
  return plan.slots.map((slot, index) => {
    if (index === 0) return playerFaction;
    if (plan.battle === "ffa" || slot.team !== plan.slots[0].team) {
      const id = enemy === 0 ? foe : rest[enemy % rest.length] || foe;
      enemy += 1;
      return id;
    }
    const id = rest[ally % rest.length] || playerFaction;
    ally += 1;
    return id;
  });
}

function humanFlags(count) {
  const flags = Array.from({ length: count }, () => false);
  flags[0] = true;
  if (role === "host" && lobby) {
    for (const guest of lobby.guests) flags[guest.slot] = true;
  }
  return flags;
}

function beginMatch(options) {
  const count = options.count || selectedCount;
  const mapId = options.mapId || selectedMap;
  me = options.slot ?? 0;
  match = createMatch({
    seed: options.seed,
    mapId,
    count,
    factions: options.factions,
    humans: options.humans,
  });
  aiMem = Array.from({ length: count }, () => createAiMemory());
  view.viewer = me;
  view.selected.clear();
  view.fx = [];
  view.terrain = null;
  view.fogCanvas = null;
  tool = null;
  paused = false;
  snapAcc = 0;
  pauseBtn.textContent = "Duraklat";
  hudSig = "";
  const goal = document.querySelector("#goal");
  if (goal) {
    goal.textContent = match.battle === "ffa" ? "Diğer karargâhları düşür" : "Rakip hattın karargâhını düşür";
  }
  resultEl.classList.add("hidden");
  menu.classList.add("hidden");
  stage.classList.remove("hidden");
  running = true;
  requestAnimationFrame(() => {
    resize();
    focusHome();
  });
  last = performance.now();
  const map = mapById(match.mapId);
  const hq = match.entities.find((e) => e.owner === me && e.kind === "ocak");
  if (hq) view.selected.add(hq.id);
  toast(
    `${map.name}. Karargâh seçili: alttan bina kur. Askere dokun, yere dokununca yürür, düşmana dokununca saldırır.` +
      (map.naval ? " Bu haritada tersane ve gemi açılır." : " Bu haritada gemi hattı yok."),
  );
  if (!looping) {
    looping = true;
    requestAnimationFrame(frame);
  }
  if (role === "host" && net && lobby) {
    const payload = {
      seed: options.seed,
      mapId,
      count,
      factions: options.factions,
      humans: options.humans,
    };
    for (const guest of lobby.guests) {
      if (guest.conn.open) guest.conn.send({ t: "start", ...payload, slot: guest.slot });
    }
    net.broadcast(packState(match), { paused });
  }
}

function startMatch() {
  if (role === "guest") {
    toast("Ev sahibi savaşı başlatınca harita açılır");
    return;
  }
  const seed = (Date.now() ^ (Math.random() * 0x7fffffff)) >>> 0;
  const factions = factionsFor(selectedCount);
  const humans = humanFlags(selectedCount);
  beginMatch({ seed, mapId: selectedMap, count: selectedCount, factions, humans, slot: 0 });
}

let looping = false;

function resize() {
  const rect = canvas.getBoundingClientRect();
  if (rect.width < 2 || rect.height < 2) return;
  const gfx = view.gfx || GFX_PRESETS.medium;
  const dpr = Math.min(window.devicePixelRatio || 1, gfx.dpr || 1);
  canvas.width = Math.max(1, Math.floor(rect.width * dpr));
  canvas.height = Math.max(1, Math.floor(rect.height * dpr));
  const mini = minimap.getBoundingClientRect();
  const miniDpr = Math.min(dpr, 1.5);
  minimap.width = Math.max(1, Math.floor(mini.width * miniDpr));
  minimap.height = Math.max(1, Math.floor(mini.height * miniDpr));
  const desired = rect.width < 900 ? gfx.viewWidth : Math.round(gfx.viewWidth * 1.12);
  view.zoom = canvas.width / desired;
  view.canvasWidth = canvas.width;
  view.canvasHeight = canvas.height;
  clampCam();
}

function focusHome() {
  const hq = match.entities.find((e) => e.owner === me && e.kind === "ocak");
  if (!hq) return;
  const viewW = view.canvasWidth / view.zoom;
  const viewH = view.canvasHeight / view.zoom;
  view.camX = hq.x - viewW * 0.38;
  view.camY = hq.y - viewH * 0.5;
  clampCam();
}

function clampCam() {
  const viewW = view.canvasWidth / view.zoom;
  const viewH = view.canvasHeight / view.zoom;
  view.camX = clampTo(view.camX, 0, Math.max(0, match.mapW - viewW));
  view.camY = clampTo(view.camY, 0, Math.max(0, match.mapH - viewH));
}

function frame(now) {
  if (!looping) return;
  const dt = Math.min(0.05, (now - last) / 1000);
  last = now;
  if (running && match && match.winner === null && role !== "guest") {
    if (!paused) {
      for (let owner = 0; owner < match.playerCount; owner++) {
        if (match.players[owner].human) continue;
        aiAct(match, owner, dt, aiMem[owner]);
      }
      const events = step(match, dt);
      for (const ev of events) acceptEvent(ev, now);
      if (match.winner !== null) showResult();
    }
    if (role === "host" && net) {
      snapAcc += dt;
      if (snapAcc >= 0.2) {
        snapAcc = 0;
        net.broadcast(packState(match), { paused });
      }
    }
  }
  if (match && view.gfx?.decor !== false) {
    view.dustAcc = (view.dustAcc || 0) + dt;
    if (view.dustAcc > 0.16) {
      view.dustAcc = 0;
      for (const e of match.entities) {
        if (e.owner !== me || e.hp <= 0 || e.isBuilding) continue;
        const moving = e.plan && (e.plan.kind === "move" || e.plan.kind === "attackMove" || e.plan.kind === "harvest");
        if (!moving) continue;
        view.fx.push({ type: "puff", x: e.x, y: e.y + 4, color: "rgba(120,100,70,0.35)", life: 0.28, max: 0.28 });
      }
      const cap = view.gfx?.fxCap || 28;
      while (view.fx.length > cap) view.fx.shift();
    }
  }
  if (match) {
    pruneSelection();
    updateGhost();
    drawWorld(ctx, match, view);
    drawMinimap(minimap, match, view);
    updateCounters();
    refreshHud(false);
    if (toastTimer > 0) {
      toastTimer -= dt;
      if (toastTimer <= 0) toastEl.classList.add("hidden");
    }
  }
  requestAnimationFrame(frame);
}

function acceptEvent(ev, now) {
  playEvent(ev, now);
  if (ev.type === "shot") {
    view.fx.push({
      type: "shot",
      x1: ev.x1,
      y1: ev.y1,
      x2: ev.x2,
      y2: ev.y2,
      heavy: ev.heavy,
      air: ev.air,
      life: 0.2,
      max: 0.2,
    });
    if (ev.targetOwner === me && ev.building) {
      const nowMs = performance.now();
      if (nowMs - (view.lastAlarm || 0) > 4000) {
        view.lastAlarm = nowMs;
        toast("Yapı ateş altında");
      }
    }
  } else if (ev.type === "death") {
    view.scorches = view.scorches || [];
    view.scorches.push({ x: ev.x, y: ev.y });
    if (view.scorches.length > 24) view.scorches.shift();
    view.fx.push({
      type: "puff",
      x: ev.x,
      y: ev.y,
      color: ev.building ? "#6a5340" : "#e7d2a8",
      life: 0.55,
      max: 0.55,
    });
  } else if (ev.type === "built" && ev.owner === me) {
    toast(`${buildingDef(match.players[me].faction, ev.kind).name} hazır`);
  }
  const cap = view.gfx?.fxCap || 28;
  while (view.fx.length > cap) view.fx.shift();
}

function tickFx(dt) {
  if (view.ping) {
    view.ping.life -= dt;
    if (view.ping.life <= 0) view.ping = null;
  }
  for (const fx of view.fx) fx.life -= dt;
  if (view.fx.length > 80) view.fx.splice(0, view.fx.length - 80);
  view.fx = view.fx.filter((fx) => fx.life > 0);
}

function onPointerDown(ev) {
  if (!match) return;
  unlockAudio();
  canvas.setPointerCapture(ev.pointerId);
  const world = worldFromEvent(ev);
  pointer = {
    id: ev.pointerId,
    x: ev.clientX,
    y: ev.clientY,
    ox: ev.clientX,
    oy: ev.clientY,
    wx: world.x,
    wy: world.y,
    moved: false,
  };
  hover = world;
}

function onPointerMove(ev) {
  if (!match || !pointer || ev.pointerId !== pointer.id) return;
  const world = worldFromEvent(ev);
  hover = world;
  const dx = ev.clientX - pointer.ox;
  const dy = ev.clientY - pointer.oy;
  if (!pointer.moved && Math.hypot(dx, dy) > 10) pointer.moved = true;
  if (tool === "box") {
    if (pointer.moved) view.box = { x1: pointer.wx, y1: pointer.wy, x2: world.x, y2: world.y };
    return;
  }
  if (pointer.moved) {
    const cssPerWorld = canvas.getBoundingClientRect().width / (view.canvasWidth / view.zoom);
    view.camX -= (ev.clientX - pointer.x) / cssPerWorld;
    view.camY -= (ev.clientY - pointer.y) / cssPerWorld;
    clampCam();
  }
  pointer.x = ev.clientX;
  pointer.y = ev.clientY;
}

function onPointerUp(ev) {
  if (!match || !pointer || ev.pointerId !== pointer.id) return;
  const world = worldFromEvent(ev);
  if (tool === "box" && pointer.moved) {
    selectBox(pointer.wx, pointer.wy, world.x, world.y);
    tool = null;
  } else if (!pointer.moved) {
    handleTap(world.x, world.y);
  }
  pointer = null;
  view.box = null;
  refreshHud(true);
}

function onMinimap(ev) {
  if (!match) return;
  const rect = minimap.getBoundingClientRect();
  const x = ((ev.clientX - rect.left) / rect.width) * match.mapW;
  const y = ((ev.clientY - rect.top) / rect.height) * match.mapH;
  const viewW = view.canvasWidth / view.zoom;
  const viewH = view.canvasHeight / view.zoom;
  view.camX = x - viewW / 2;
  view.camY = y - viewH / 2;
  clampCam();
}

function handleTap(x, y) {
  if (tool?.type === "place") {
    const result = issue({ type: "place", workerId: tool.workerId, kind: tool.kind, x, y });
    if (!result.ok) toast(REASONS[result.reason] || "Kurulamadı");
    else {
      tool = null;
      navigator.vibrate?.(12);
    }
    recomputeFog(match);
    return;
  }
  if (tool === "rally") {
    const building = [...view.selected]
      .map((id) => getEntity(match, id))
      .find((e) => e && e.isBuilding);
    if (building) issue({ type: "rally", buildingId: building.id, x, y });
    tool = null;
    recomputeFog(match);
    return;
  }
  const hit = pickEntity(match, x, y, me);
  if (tool === "attack") {
    const ids = selectedUnits().map((e) => e.id);
    if (hit && hostile(match, me, hit.owner)) issue({ type: "attack", ids, targetId: hit.id });
    else {
      issue({ type: "attackMove", ids, x, y });
      view.ping = { x, y, life: 0.7, max: 0.7 };
    }
    tool = null;
    return;
  }
  if (hit && hit.owner === me) {
    const now = performance.now();
    if (!hit.isBuilding && view.lastPick && view.lastPick.kind === hit.kind && now - view.lastPick.at < 450) {
      view.selected.clear();
      for (const e of match.entities) {
        if (e.owner === me && e.hp > 0 && e.kind === hit.kind && canSeeEntity(match, me, e)) view.selected.add(e.id);
      }
      view.lastPick = { kind: hit.kind, at: now };
      return;
    }
    view.lastPick = { kind: hit.kind, at: now };
    if (!hit.constructed && hit.isBuilding && selectedWorkers().length) {
      const result = issue({
        type: "construct",
        buildingId: hit.id,
        workerIds: selectedWorkers().map((w) => w.id),
      });
      if (!result.ok) toast(REASONS[result.reason] || "İnşa başlamadı");
      return;
    }
    view.selected.clear();
    view.selected.add(hit.id);
    return;
  }
  const units = selectedUnits();
  if (!units.length) {
    if (hit && hostile(match, me, hit.owner)) {
      view.selected.clear();
      view.selected.add(hit.id);
    } else view.selected.clear();
    return;
  }
  if (hit && hostile(match, me, hit.owner)) issue({ type: "attack", ids: units.map((u) => u.id), targetId: hit.id });
  else {
    issue({ type: "move", ids: units.map((u) => u.id), x, y });
    view.ping = { x, y, life: 0.7, max: 0.7 };
  }
  recomputeFog(match);
}

function selectBox(x1, y1, x2, y2) {
  const minX = Math.min(x1, x2);
  const maxX = Math.max(x1, x2);
  const minY = Math.min(y1, y2);
  const maxY = Math.max(y1, y2);
  view.selected.clear();
  for (const e of match.entities) {
    if (e.owner !== me || e.hp <= 0) continue;
    if (e.x < minX || e.x > maxX || e.y < minY || e.y > maxY) continue;
    view.selected.add(e.id);
  }
  if (!view.selected.size) toast("Bu alanda senin birliğin yok");
}

function selectedEntities() {
  return [...view.selected].map((id) => getEntity(match, id)).filter((e) => e && e.hp > 0);
}

function selectedUnits() {
  return selectedEntities().filter((e) => !e.isBuilding);
}

function selectedWorkers() {
  return selectedUnits().filter((e) => entityDef(match, e).worker);
}

function pruneSelection() {
  for (const id of [...view.selected]) {
    const e = getEntity(match, id);
    if (!e || e.hp <= 0 || (e.owner !== me && !canSeeEntity(match, me, e))) view.selected.delete(id);
  }
}

function updateGhost() {
  view.ghost = null;
  view.rally = null;
  if (tool?.type === "place" && hover) {
    view.ghost = {
      kind: tool.kind,
      x: hover.x,
      y: hover.y,
      ok: canPlace(match, me, tool.kind, hover.x, hover.y),
      workerId: tool.workerId,
    };
  }
  const only = selectedEntities();
  if (only.length === 1 && only[0].isBuilding && only[0].constructed && entityDef(match, only[0]).trains) {
    view.rally = { x: only[0].rallyX, y: only[0].rallyY, fromX: only[0].x, fromY: only[0].y };
  }
}

function updateCounters() {
  const p = match.players[me];
  const supply = supplyInfo(match, me);
  ashEl.textContent = String(Math.floor(p.ash));
  supplyEl.textContent = `${supply.used}/${supply.cap}`;
}

function refreshHud(force) {
  const ents = selectedEntities();
  const sig = [
    ents.map((e) => e.id).join(","),
    tool?.type || tool || "",
    tool?.kind || "",
    paused,
    Math.floor(match.players[me].ash / 5),
    ents
      .filter((e) => e.isBuilding)
      .map((e) => `${e.queue?.length || 0}:${e.constructed ? 1 : 0}`)
      .join(","),
  ].join("|");
  if (!force && sig === hudSig) {
    paintInspect(ents);
    return;
  }
  hudSig = sig;
  paintInspect(ents);
  bar.replaceChildren();
  for (const item of buttonModel(ents)) bar.append(makeButton(item));
}

function paintInspect(ents) {
  if (!ents.length) {
    inspectEl.textContent = "Kimse seçili değil";
    hintEl.textContent = "Sürükle: haritayı kaydır";
    return;
  }
  if (ents.length === 1) {
    const e = ents[0];
    const def = entityDef(match, e);
    const hp = Math.max(0, Math.ceil(e.hp));
    const extra = e.isBuilding && !e.constructed ? ` · inşa %${Math.floor(e.progress * 100)}` : "";
    const queue = e.queue?.length ? ` · sıra ${e.queue.length}` : "";
    inspectEl.textContent = `${def.name} · ${hp}/${e.maxHp}${extra}${queue}`;
  } else {
    inspectEl.textContent = `${ents.length} seçili`;
  }
  if (tool?.type === "place") hintEl.textContent = "Yeşil alana dokunarak kur. Sürükleyerek kaydır.";
  else if (tool === "attack") hintEl.textContent = "Hedefe veya yere dokun";
  else if (tool === "box") hintEl.textContent = "Sürükleyerek kutu seç";
  else if (tool === "rally") hintEl.textContent = "Toplanma noktasına dokun";
  else if (ents.length === 1 && ents[0].kind === "ocak") hintEl.textContent = "Alt şerit: önce kışla, sonra araç, hava ve varsa deniz.";
  else if (ents.some((e) => !e.isBuilding)) hintEl.textContent = "Yere dokun: yürü · Düşmana dokun: saldır · Çift dokunuş: aynı tip";
  else hintEl.textContent = "Bu binadan alttaki kartla üret";
}

function structureCards() {
  const fac = match.players[me].faction;
  return BUILD_RULES.map((rule) => {
    const def = buildingDef(fac, rule.kind);
    const lock = structureLock(fac, rule.kind, match.entities, me, match.naval);
    return {
      id: "build",
      kind: rule.kind,
      label: def.name,
      sub: lock.ok ? `${rule.band} · ${def.cost}` : lock.reason,
      poor: match.players[me].ash < def.cost,
      locked: !lock.ok,
    };
  });
}

function trainCards(building) {
  const fac = match.players[me].faction;
  const def = entityDef(match, building);
  const items = [];
  for (const kind of def.trains || []) {
    const udef = unitDef(fac, kind);
    const queued = building.queue?.filter((job) => job.kind === kind).length || 0;
    items.push({
      id: "train",
      unit: kind,
      label: udef.name,
      sub: `${udef.cost} yakıt${queued ? ` · sıra ${queued}` : ""}`,
      poor: match.players[me].ash < udef.cost,
    });
  }
  if (def.trains?.length) items.push({ id: "rally", label: "Toplanma", sub: "Çıkan birlik buraya gelir" });
  return items;
}

function buttonModel(ents) {
  const own = ents.filter((e) => e.owner === me);
  if (tool) return [{ id: "cancel", label: "İptal", sub: "Emri kapat" }];
  if (!own.length) {
    return [
      { id: "army", label: "Ordu", sub: "Savaşanları seç" },
      { id: "workers", label: "İkmal", sub: "Toplayıcıları seç" },
      { id: "home", label: "Karargâh", sub: "Üsse dön" },
      { id: "box", label: "Kutu", sub: "Sürükleyerek seç" },
    ];
  }
  const buildings = own.filter((e) => e.isBuilding);
  const units = own.filter((e) => !e.isBuilding);
  if (buildings.length === 1 && units.length === 0) {
    const b = buildings[0];
    if (!b.constructed) return [{ id: "send-build", label: "İkmal gönder", sub: "İnşayı bitir" }, { id: "stop", label: "Dur", sub: "Emri kes" }];
    const items = trainCards(b);
    if (b.kind === "ocak") items.push(...structureCards());
    items.push({ id: "stop", label: b.queue?.length ? "Kuyruğu sil" : "Dur", sub: "Üretimi kes" });
    return items;
  }
  const items = [
    { id: "attack", label: "Saldır", sub: "Hedefe yürü" },
    { id: "stop", label: "Dur", sub: "Olduğun yerde kal" },
    { id: "same", label: "Aynı tip", sub: "Bu cinsten hepsi" },
  ];
  const workers = units.filter((e) => entityDef(match, e).worker);
  if (workers.length) items.push({ id: "harvest", label: "Yakıt", sub: "Kaynağa dön" });
  if (workers.length && workers.length === units.length) items.push(...structureCards());
  items.push({ id: "box", label: "Kutu", sub: "Sürükleyerek seç" });
  return items;
}

function makeButton(item) {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "cmd" + (item.poor ? " poor" : "") + (item.locked ? " locked" : "");
  const title = document.createElement("b");
  title.textContent = item.label;
  btn.append(title);
  if (item.sub) {
    const sub = document.createElement("small");
    sub.textContent = item.sub;
    btn.append(sub);
  }
  btn.addEventListener("click", () => {
    unlockAudio();
    playEvent({ type: "ui" }, performance.now());
    runButton(item);
    refreshHud(true);
  });
  return btn;
}

function runButton(item) {
  if (item.id === "cancel") {
    tool = null;
    return;
  }
  if (item.id === "army") {
    view.selected.clear();
    for (const e of match.entities) {
      if (e.owner === me && e.hp > 0 && !e.isBuilding && (entityDef(match, e).damage || 0) > 0) view.selected.add(e.id);
    }
    if (!view.selected.size) toast("Henüz savaşan birliğin yok");
    return;
  }
  if (item.id === "workers") {
    view.selected.clear();
    for (const e of match.entities) {
      if (e.owner === me && e.hp > 0 && !e.isBuilding && entityDef(match, e).worker) view.selected.add(e.id);
    }
    return;
  }
  if (item.id === "home") {
    focusHome();
    return;
  }
  if (item.id === "box") {
    tool = "box";
    return;
  }
  if (item.id === "attack") {
    armAttack();
    return;
  }
  if (item.id === "stop") {
    issueStop();
    return;
  }
  if (item.id === "harvest") {
    issue({ type: "harvest", ids: selectedWorkers().map((w) => w.id) });
    return;
  }
  if (item.id === "same") {
    const kind = selectedUnits()[0]?.kind;
    if (!kind) return;
    view.selected.clear();
    for (const e of match.entities) {
      if (e.owner === me && e.hp > 0 && e.kind === kind) view.selected.add(e.id);
    }
    return;
  }
  if (item.id === "build") {
    if (item.locked) {
      toast(item.sub);
      return;
    }
    const worker = nearestFreeWorker();
    if (!worker) {
      toast("Önce karargâhtan ikmal aracı üret");
      return;
    }
    tool = { type: "place", kind: item.kind, workerId: worker.id };
    toast(`${item.label}: karargâhın yanındaki açık alana dokun`);
    return;
  }
  if (item.id === "train") {
    const building = selectedEntities().find((e) => e.isBuilding);
    const result = issue({ type: "train", buildingId: building.id, unitKind: item.unit });
    if (!result.ok) toast(REASONS[result.reason] || "Üretilemedi");
    return;
  }
  if (item.id === "rally") {
    tool = "rally";
    return;
  }
  if (item.id === "send-build") {
    const building = selectedEntities().find((e) => e.isBuilding);
    let worker = null;
    let best = Infinity;
    for (const e of match.entities) {
      if (e.owner !== me || e.hp <= 0 || e.isBuilding || !entityDef(match, e).worker) continue;
      if (e.plan?.kind === "construct") continue;
      const d = Math.hypot(e.x - building.x, e.y - building.y);
      if (d < best) {
        best = d;
        worker = e;
      }
    }
    if (!worker) {
      toast(REASONS.worker);
      return;
    }
    issue({ type: "construct", buildingId: building.id, workerIds: [worker.id] });
  }
}

function nearestFreeWorker() {
  const selected = selectedWorkers().find((w) => w.plan?.kind !== "construct");
  if (selected) return selected;
  let best = null;
  let bestD = Infinity;
  const hq = match.entities.find((e) => e.owner === me && e.kind === "ocak" && e.hp > 0);
  const ax = hq ? hq.x : 0;
  const ay = hq ? hq.y : 0;
  for (const e of match.entities) {
    if (e.owner !== me || e.hp <= 0 || e.isBuilding || !entityDef(match, e).worker) continue;
    if (e.plan?.kind === "construct") continue;
    const d = Math.hypot(e.x - ax, e.y - ay);
    if (d < bestD) {
      bestD = d;
      best = e;
    }
  }
  return best;
}

function issue(cmd) {
  cmd.by = me;
  if (role === "guest" && net) {
    net.send(cmd);
    return { ok: true };
  }
  return command(match, cmd);
}

function armAttack() {
  if (!selectedUnits().length) {
    toast(REASONS.units);
    return;
  }
  tool = "attack";
}

function issueStop() {
  const ids = selectedEntities().map((e) => e.id);
  if (!ids.length) return;
  issue({ type: "stop", ids });
}

function showResult() {
  if (!resultEl.classList.contains("hidden")) return;
  const myTeam = match.players[me].team;
  const you = match.winner === myTeam;
  const draw = match.winner === "draw";
  const ffa = match.battle === "ffa";
  resultTitle.textContent = draw ? "Karargâhlar düştü" : you ? (ffa ? "Son karargâh sende" : "Rakip hat düştü") : "Karargâhın düştü";
  const secs = Math.floor(match.time);
  const mm = String(Math.floor(secs / 60)).padStart(2, "0");
  const ss = String(secs % 60).padStart(2, "0");
  resultBody.textContent = `${mm}:${ss} · toplanan yakıt ${Math.floor(match.players[me].gathered)} · imha ${match.players[me].kills}`;
  resultEl.classList.remove("hidden");
  if (you) navigator.vibrate?.([20, 40, 20]);
}

function toast(text) {
  toastEl.textContent = text;
  toastEl.classList.remove("hidden");
  toastTimer = 3.2;
}

function worldFromEvent(ev) {
  const rect = canvas.getBoundingClientRect();
  const sx = ((ev.clientX - rect.left) / rect.width) * canvas.width;
  const sy = ((ev.clientY - rect.top) / rect.height) * canvas.height;
  return {
    x: view.camX + sx / view.zoom,
    y: view.camY + sy / view.zoom,
  };
}

function clampTo(v, a, b) {
  return Math.max(a, Math.min(b, v));
}

const GFX_KEY = "cephe-gfx";
let gfxChoice = { mode: "auto" };

function readDeviceInfo() {
  if (window.CepheUpdate && typeof window.CepheUpdate.deviceInfo === "function") {
    try {
      const parsed = JSON.parse(window.CepheUpdate.deviceInfo() || "{}");
      if (parsed && (parsed.ramMb || parsed.cores)) return parsed;
    } catch (err) {
      /* tarayıcı yolu */
    }
  }
  const ramGb = navigator.deviceMemory || 0;
  return {
    ramMb: ramGb ? ramGb * 1024 : 0,
    cores: navigator.hardwareConcurrency || 0,
    width: Math.max(screen.width || 0, screen.height || 0),
    height: Math.min(screen.width || 0, screen.height || 0),
    density: window.devicePixelRatio || 1,
  };
}

function loadGfxChoice() {
  try {
    const saved = JSON.parse(localStorage.getItem(GFX_KEY) || "null");
    if (saved && (saved.mode === "auto" || GFX_PRESETS[saved.mode])) return saved;
  } catch (err) {
    /* ilk kurulum */
  }
  return { mode: "auto" };
}

function deviceLine(info) {
  const bits = [];
  if (info.ramMb) bits.push(`${Math.max(1, Math.round(info.ramMb / 1024))} GB bellek`);
  if (info.cores) bits.push(`${info.cores} çekirdek`);
  return bits.join(" · ");
}

function applyGfx(choice) {
  const info = readDeviceInfo();
  const suggested = suggestTier(info);
  const mode = choice?.mode || "auto";
  const tier = mode === "auto" ? suggested : mode;
  gfxChoice = { mode };
  view.gfx = { ...GFX_PRESETS[tier], mode, suggested, info };
  view.terrain = null;
  try {
    localStorage.setItem(GFX_KEY, JSON.stringify(gfxChoice));
  } catch (err) {
    /* gizli sekme */
  }
  const device = document.querySelector("#gfx-device");
  if (device) {
    const hardware = deviceLine(info);
    const warn = mode !== "auto" && tierAbove(tier, suggested) ? " Bu basamak bu telefonda kasabilir." : "";
    device.textContent = `${hardware ? hardware + " · " : ""}Önerilen: ${GFX_PRESETS[suggested].label}.${warn}`;
  }
  document.querySelectorAll("[data-gfx]").forEach((btn) => {
    btn.classList.toggle("selected", btn.dataset.gfx === mode);
  });
  if (match) resize();
}

function openGfx() {
  applyGfx(gfxChoice);
  document.querySelector("#gfx")?.classList.remove("hidden");
}

function closeGfx() {
  document.querySelector("#gfx")?.classList.add("hidden");
}

applyGfx(loadGfxChoice());

function mountPickers() {
  const yours = document.querySelector("#yours");
  const enemy = document.querySelector("#enemy");
  yours.replaceChildren();
  enemy.replaceChildren();
  for (const id of FACTION_IDS) {
    yours.append(factionCard(id, "you"));
    enemy.append(factionCard(id, "foe"));
  }
}

function factionCard(id, role) {
  const data = faction(id);
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "faction";
  btn.dataset.faction = id;
  const selected = role === "you" ? id === playerFaction : id === enemyFaction;
  btn.classList.toggle("selected", selected);
  btn.innerHTML = `<span class="swatch" style="background:${data.colors.main}"></span><strong>${data.name}</strong><p></p><em></em>`;
  btn.querySelector("p").textContent = data.blurb;
  btn.querySelector("em").textContent = data.roster;
  btn.addEventListener("click", () => {
    unlockAudio();
    if (role === "you") playerFaction = id;
    else enemyFaction = id;
    mountPickers();
  });
  return btn;
}

const MAP_ART = {
  anadolu: "assets/map-tur.jpg",
  ovalar: "assets/map-usa.jpg",
  havza: "assets/map-cin.jpg",
  tayga: "assets/map-rus.jpg",
  vadi: "assets/map-alm.jpg",
  ada: "assets/map-ing.jpg",
  bozkir: "assets/map-ukr.jpg",
  plato: "assets/map-irn.jpg",
};

function mountMaps() {
  const root = document.querySelector("#maps");
  if (!root) return;
  root.replaceChildren();
  for (const id of MAP_IDS) {
    const map = mapById(id);
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "map-card" + (id === selectedMap ? " selected" : "");
    btn.innerHTML = `<img alt="" loading="lazy" /><div class="map-copy"><strong></strong><span></span></div>`;
    btn.querySelector("img").src = MAP_ART[id] || "assets/terrain.jpg";
    btn.querySelector("strong").textContent = map.name;
    btn.querySelector("span").textContent = `${map.country} · ${map.resourceName}`;
    btn.addEventListener("click", () => {
      unlockAudio();
      selectedMap = id;
      mountMaps();
      paintMapBlurb();
    });
    root.append(btn);
  }
  paintMapBlurb();
}

function paintMapBlurb() {
  const el = document.querySelector("#map-blurb");
  if (!el) return;
  const map = mapById(selectedMap);
  el.textContent = `${map.blurb} ${map.posture} Kaynak adı ${map.resourceName}; miktar aynı oyuncu sayısında diğer haritalarla eşittir.`;
}

function mountSizes() {
  const root = document.querySelector("#sizes");
  if (!root) return;
  root.replaceChildren();
  for (const count of MATCH_SIZES) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "size-btn" + (count === selectedCount ? " selected" : "");
    btn.innerHTML = `<strong>${count}</strong><span>${SIZE_LABEL[count]}</span>`;
    btn.addEventListener("click", () => {
      unlockAudio();
      selectedCount = count;
      mountSizes();
    });
    root.append(btn);
  }
}

function sayRoom(text) {
  const status = document.querySelector("#room-status");
  if (status) status.textContent = text;
}

function nextGuestSlot() {
  const plan = battlePlan(selectedMap, selectedCount);
  const taken = new Set([0, ...(lobby?.guests || []).map((guest) => guest.slot)]);
  const order = plan.slots
    .map((_, index) => index)
    .filter((index) => index > 0)
    .sort((a, b) => {
      const hostTeam = plan.slots[0].team;
      const ta = plan.slots[a].team === hostTeam ? 1 : 0;
      const tb = plan.slots[b].team === hostTeam ? 1 : 0;
      return ta - tb || a - b;
    });
  for (const index of order) {
    if (!taken.has(index)) return index;
  }
  return null;
}

async function openRoom() {
  const input = document.querySelector("#room-code");
  const code = ((input?.value || makeCode()) + "").trim().toUpperCase().slice(0, 4);
  if (input) input.value = code;
  sayRoom("Oda açılıyor…");
  try {
    if (net) {
      net.close();
      net = null;
    }
    role = "host";
    lobby = { code, guests: [] };
    net = await hostRoom(code, {
      onGuest(conn) {
        const slot = nextGuestSlot();
        if (slot == null) {
          if (conn.open) conn.send({ t: "full" });
          return null;
        }
        lobby.guests.push({ conn, slot });
        if (conn.open) conn.send({ t: "seat", slot, mapId: selectedMap, count: selectedCount });
        sayRoom(`Oda ${code} · ${lobby.guests.length} oyuncu bağlandı`);
        return slot;
      },
      onCommand(slot, cmd) {
        if (!match || match.winner !== null) return;
        cmd.by = slot;
        command(match, cmd);
      },
      onLeave(slot) {
        if (!lobby) return;
        lobby.guests = lobby.guests.filter((guest) => guest.slot !== slot);
        sayRoom(`Oda ${code} · koltuk ${slot + 1} boşaldı`);
      },
    });
    sayRoom(`Oda ${code} hazır. Kodu paylaş, sonra Savaşa gir.`);
  } catch (err) {
    role = "local";
    lobby = null;
    net = null;
    sayRoom("Oda açılamadı. Yerel skirmish ile devam edebilirsin.");
  }
}

async function enterRoom() {
  const input = document.querySelector("#room-code");
  const code = ((input?.value || "") + "").trim().toUpperCase();
  if (code.length < 4) {
    sayRoom("Dört harfli oda kodunu yaz");
    return;
  }
  sayRoom("Odaya bağlanılıyor…");
  try {
    if (net) {
      net.close();
      net = null;
    }
    role = "guest";
    me = 0;
    net = await joinRoom(code, {
      onStart(msg) {
        beginMatch({
          seed: msg.seed,
          mapId: msg.mapId,
          count: msg.count,
          factions: msg.factions,
          humans: msg.humans,
          slot: msg.slot,
        });
      },
      onSnap(msg) {
        if (!match) return;
        paused = !!msg.paused;
        pauseBtn.textContent = paused ? "Sürdür" : "Duraklat";
        applyPack(match, msg.pack);
        if (match.winner !== null) showResult();
      },
      onFull() {
        sayRoom("Oda dolu");
      },
      onSeat(msg) {
        me = msg.slot;
        if (msg.mapId) selectedMap = msg.mapId;
        if (msg.count) selectedCount = msg.count;
        mountMaps();
        mountSizes();
        sayRoom(`Koltuk ${msg.slot + 1}. Ev sahibi savaşı başlatınca gireceksin.`);
      },
      onStatus(text) {
        sayRoom(text);
      },
    });
  } catch (err) {
    role = "local";
    net = null;
    sayRoom("Odaya girilemedi. Kod veya bağlantıyı kontrol et.");
  }
}

mountMaps();
mountSizes();
mountPickers();

document.querySelector("#room-host")?.addEventListener("click", () => {
  unlockAudio();
  openRoom();
});
document.querySelector("#room-join")?.addEventListener("click", () => {
  unlockAudio();
  enterRoom();
});

function sayUpdate(text) {
  const status = document.querySelector("#update-status");
  if (status) status.textContent = text;
  if (match) toast(text);
}

window.__cepheUpdate = sayUpdate;

async function checkUpdate() {
  sayUpdate("Sürüm denetleniyor");
  if (window.CepheUpdate && typeof window.CepheUpdate.checkAndUpdate === "function") {
    window.CepheUpdate.checkAndUpdate();
    return;
  }
  try {
    const res = await fetch(VERSION_URL + "?t=" + Math.floor(Date.now() / 3600000), { cache: "no-store" });
    if (!res.ok) throw new Error("http");
    const data = await res.json();
    if ((data.versionCode | 0) > APP.code) {
      sayUpdate("Yeni sürüm " + (data.versionName || "") + " hazır. Kurulum dosyası açılıyor.");
      if (data.apk) window.open(data.apk, "_blank");
    } else {
      sayUpdate("Sürüm güncel · " + APP.name);
    }
  } catch (err) {
    sayUpdate("Sürüm denetlenemedi. Bağlantıyı kontrol et.");
  }
}

document.querySelector("#update")?.addEventListener("click", () => {
  unlockAudio();
  checkUpdate();
});
document.querySelector("#update-hud")?.addEventListener("click", () => {
  unlockAudio();
  checkUpdate();
});
document.querySelector("#gfx-open")?.addEventListener("click", () => {
  unlockAudio();
  openGfx();
});
document.querySelector("#gfx-hud")?.addEventListener("click", () => {
  unlockAudio();
  openGfx();
});
document.querySelector("#gfx-close")?.addEventListener("click", () => closeGfx());
document.querySelectorAll("[data-gfx]").forEach((btn) => {
  btn.addEventListener("click", () => {
    unlockAudio();
    applyGfx({ mode: btn.dataset.gfx });
  });
});

if (new URLSearchParams(location.search).has("debug")) {
  window.__tuz = {
    get match() {
      return match;
    },
    get view() {
      return view;
    },
    command,
  };
}
