/** Dokunmatik arayüz. Sürükle kaydırır, dokunuş seçer veya emir verir. */

import { BUILD_ORDER, buildingDef, faction, unitDef } from "./balance.js";
import { playEvent, unlockAudio } from "./audio.js";
import { aiAct, createAiMemory } from "./ai.js";
import { drawMinimap, drawWorld } from "./render.js";
import {
  MAP_H,
  MAP_W,
  canPlace,
  canSeeEntity,
  command,
  createMatch,
  entityDef,
  getEntity,
  pickEntity,
  recomputeFog,
  step,
  supplyInfo,
} from "./sim.js";

const REASONS = {
  ash: "Kül yetmiyor",
  supply: "İkmal dolu",
  space: "Buraya kurulamaz",
  worker: "Uygun taşıyıcı yok",
  queue: "Üretim kuyruğu dolu",
  building: "Bu bina hazır değil",
  units: "Önce birlik seç",
  target: "Hedef yok",
};

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
let aiMem = null;
let playerFaction = "tuz";
let paused = false;
let tool = null;
let pointer = null;
let hover = null;
let hudSig = "";
let toastTimer = 0;
let last = 0;
let running = false;

document.querySelectorAll(".faction").forEach((btn) => {
  btn.addEventListener("click", () => {
    playerFaction = btn.dataset.faction;
    document.querySelectorAll(".faction").forEach((el) => el.classList.toggle("selected", el === btn));
    unlockAudio();
  });
});

document.querySelector("#start").addEventListener("click", () => {
  unlockAudio();
  startMatch(playerFaction);
});

document.querySelector("#again").addEventListener("click", () => {
  unlockAudio();
  startMatch(playerFaction);
});

document.querySelector("#back-menu").addEventListener("click", () => {
  running = false;
  match = null;
  resultEl.classList.add("hidden");
  stage.classList.add("hidden");
  menu.classList.remove("hidden");
});

pauseBtn.addEventListener("click", () => {
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

function startMatch(chosen) {
  const enemy = chosen === "tuz" ? "ruzgar" : "tuz";
  match = createMatch({
    seed: (Date.now() ^ (Math.random() * 0x7fffffff)) >>> 0,
    playerFaction: chosen,
    enemyFaction: enemy,
  });
  aiMem = createAiMemory();
  view.selected.clear();
  view.fx = [];
  view.terrain = null;
  tool = null;
  paused = false;
  pauseBtn.textContent = "Duraklat";
  hudSig = "";
  resultEl.classList.add("hidden");
  menu.classList.add("hidden");
  stage.classList.remove("hidden");
  running = true;
  requestAnimationFrame(() => {
    resize();
    focusHome();
  });
  last = performance.now();
  toast("Taşıyıcılar kül toplar. İşçi seç, bina kur, birliklerle rakip ocağa yürü.");
  if (!looping) {
    looping = true;
    requestAnimationFrame(frame);
  }
}

let looping = false;

function resize() {
  const rect = canvas.getBoundingClientRect();
  if (rect.width < 2 || rect.height < 2) return;
  const dpr = Math.min(window.devicePixelRatio || 1, 2);
  canvas.width = Math.max(1, Math.floor(rect.width * dpr));
  canvas.height = Math.max(1, Math.floor(rect.height * dpr));
  const mini = minimap.getBoundingClientRect();
  minimap.width = Math.max(1, Math.floor(mini.width * dpr));
  minimap.height = Math.max(1, Math.floor(mini.height * dpr));
  const desired = rect.width < 900 ? 980 : 1200;
  view.zoom = canvas.width / desired;
  view.canvasWidth = canvas.width;
  view.canvasHeight = canvas.height;
  clampCam();
}

function focusHome() {
  const hq = match.entities.find((e) => e.owner === 0 && e.kind === "ocak");
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
  view.camX = clampTo(view.camX, 0, Math.max(0, MAP_W - viewW));
  view.camY = clampTo(view.camY, 0, Math.max(0, MAP_H - viewH));
}

function frame(now) {
  if (!looping) return;
  const dt = Math.min(0.05, (now - last) / 1000);
  last = now;
  if (running && match && !paused && match.winner === null) {
    aiAct(match, 1, dt, aiMem);
    const events = step(match, dt);
    for (const ev of events) acceptEvent(ev, now);
    if (match.winner !== null) showResult();
  }
  tickFx(dt);
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
      life: 0.12,
      max: 0.12,
    });
    if (ev.targetOwner === 0 && ev.targetKind === "ocak") navigator.vibrate?.(18);
  } else if (ev.type === "death") {
    view.fx.push({
      type: "puff",
      x: ev.x,
      y: ev.y,
      color: ev.building ? "#c46a3a" : "#f0d2bc",
      life: 0.35,
      max: 0.35,
    });
  } else if (ev.type === "built" && ev.owner === 0) {
    toast(`${buildingDef(match.players[0].faction, ev.kind).name} hazır`);
  }
}

function tickFx(dt) {
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
  const x = ((ev.clientX - rect.left) / rect.width) * MAP_W;
  const y = ((ev.clientY - rect.top) / rect.height) * MAP_H;
  const viewW = view.canvasWidth / view.zoom;
  const viewH = view.canvasHeight / view.zoom;
  view.camX = x - viewW / 2;
  view.camY = y - viewH / 2;
  clampCam();
}

function handleTap(x, y) {
  if (tool?.type === "place") {
    const result = command(match, { by: 0, type: "place", workerId: tool.workerId, kind: tool.kind, x, y });
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
    if (building) command(match, { by: 0, type: "rally", buildingId: building.id, x, y });
    tool = null;
    recomputeFog(match);
    return;
  }
  const hit = pickEntity(match, x, y, 0);
  if (tool === "attack") {
    const ids = selectedUnits().map((e) => e.id);
    if (hit && hit.owner !== 0) command(match, { by: 0, type: "attack", ids, targetId: hit.id });
    else command(match, { by: 0, type: "attackMove", ids, x, y });
    tool = null;
    return;
  }
  if (hit && hit.owner === 0) {
    if (!hit.constructed && hit.isBuilding && selectedWorkers().length) {
      const result = command(match, {
        by: 0,
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
    if (hit && hit.owner !== 0) {
      view.selected.clear();
      view.selected.add(hit.id);
    } else view.selected.clear();
    return;
  }
  if (hit && hit.owner !== 0) command(match, { by: 0, type: "attack", ids: units.map((u) => u.id), targetId: hit.id });
  else command(match, { by: 0, type: "move", ids: units.map((u) => u.id), x, y });
  recomputeFog(match);
}

function selectBox(x1, y1, x2, y2) {
  const minX = Math.min(x1, x2);
  const maxX = Math.max(x1, x2);
  const minY = Math.min(y1, y2);
  const maxY = Math.max(y1, y2);
  view.selected.clear();
  for (const e of match.entities) {
    if (e.owner !== 0 || e.hp <= 0) continue;
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
    if (!e || e.hp <= 0 || (e.owner !== 0 && !canSeeEntity(match, 0, e))) view.selected.delete(id);
  }
}

function updateGhost() {
  view.ghost = null;
  view.rally = null;
  if (tool?.type === "place" && hover) {
    view.ghost = { kind: tool.kind, x: hover.x, y: hover.y, ok: canPlace(match, 0, tool.kind, hover.x, hover.y) };
  }
  const only = selectedEntities();
  if (only.length === 1 && only[0].isBuilding && only[0].constructed && entityDef(match, only[0]).trains) {
    view.rally = { x: only[0].rallyX, y: only[0].rallyY };
  }
}

function updateCounters() {
  const p = match.players[0];
  const supply = supplyInfo(match, 0);
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
    Math.floor(match.players[0].ash / 5),
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
  else if (ents.some((e) => !e.isBuilding)) hintEl.textContent = "Yere dokun: yürü · Düşmana dokun: saldır";
  else hintEl.textContent = "Üretmek için birliğe dokun";
}

function buttonModel(ents) {
  const own = ents.filter((e) => e.owner === 0);
  const fac = match.players[0].faction;
  if (tool) return [{ id: "cancel", label: "İptal" }];
  if (!own.length) {
    return [
      { id: "army", label: "Ordu" },
      { id: "workers", label: "İşçiler" },
      { id: "home", label: "Ocağım" },
      { id: "box", label: "Kutu seç" },
    ];
  }
  const buildings = own.filter((e) => e.isBuilding);
  const units = own.filter((e) => !e.isBuilding);
  if (buildings.length === 1 && units.length === 0) {
    const b = buildings[0];
    const def = entityDef(match, b);
    const items = [];
    if (!b.constructed) items.push({ id: "send-build", label: "İşçi gönder" });
    else {
      for (const kind of def.trains || []) {
        const udef = unitDef(fac, kind);
        items.push({
          id: "train",
          unit: kind,
          label: `${udef.name} · ${udef.cost}`,
          poor: match.players[0].ash < udef.cost,
        });
      }
      if (def.trains?.length) items.push({ id: "rally", label: "Toplanma" });
    }
    items.push({ id: "stop", label: b.queue?.length ? "Kuyruğu sil" : "Dur" });
    return items;
  }
  const items = [
    { id: "attack", label: "Saldır" },
    { id: "stop", label: "Dur" },
  ];
  const workers = units.filter((e) => entityDef(match, e).worker);
  if (workers.length) items.push({ id: "harvest", label: "Topla" });
  if (workers.length && workers.length === units.length) {
    for (const kind of BUILD_ORDER) {
      const bdef = buildingDef(fac, kind);
      items.push({
        id: "build",
        kind,
        label: `${bdef.name} · ${bdef.cost}`,
        poor: match.players[0].ash < bdef.cost,
      });
    }
  }
  items.push({ id: "box", label: "Kutu seç" });
  return items;
}

function makeButton(item) {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "cmd" + (item.poor ? " poor" : "");
  btn.textContent = item.label;
  btn.addEventListener("click", () => {
    unlockAudio();
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
      if (e.owner === 0 && e.hp > 0 && !e.isBuilding && (entityDef(match, e).damage || 0) > 0) view.selected.add(e.id);
    }
    if (!view.selected.size) toast("Henüz savaşan birliğin yok");
    return;
  }
  if (item.id === "workers") {
    view.selected.clear();
    for (const e of match.entities) {
      if (e.owner === 0 && e.hp > 0 && !e.isBuilding && entityDef(match, e).worker) view.selected.add(e.id);
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
    command(match, { by: 0, type: "harvest", ids: selectedWorkers().map((w) => w.id) });
    return;
  }
  if (item.id === "build") {
    const worker = selectedWorkers()[0];
    if (!worker) {
      toast(REASONS.worker);
      return;
    }
    tool = { type: "place", kind: item.kind, workerId: worker.id };
    return;
  }
  if (item.id === "train") {
    const building = selectedEntities().find((e) => e.isBuilding);
    const result = command(match, { by: 0, type: "train", buildingId: building.id, unitKind: item.unit });
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
      if (e.owner !== 0 || e.hp <= 0 || e.isBuilding || !entityDef(match, e).worker) continue;
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
    command(match, { by: 0, type: "construct", buildingId: building.id, workerIds: [worker.id] });
  }
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
  command(match, { by: 0, type: "stop", ids });
}

function showResult() {
  const you = match.winner === 0;
  const draw = match.winner === "draw";
  resultTitle.textContent = draw ? "İki ocak da yıkıldı" : you ? "Rakip ocak yıkıldı" : "Ocağın düştü";
  const secs = Math.floor(match.time);
  const mm = String(Math.floor(secs / 60)).padStart(2, "0");
  const ss = String(secs % 60).padStart(2, "0");
  resultBody.textContent = `${mm}:${ss} · toplanan kül ${Math.floor(match.players[0].gathered)} · yıkılan ${match.players[0].kills}`;
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

const facRoot = faction("tuz");
document.querySelector("#card-tuz p").textContent = facRoot.blurb;
document.querySelector("#card-ruzgar p").textContent = faction("ruzgar").blurb;

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
