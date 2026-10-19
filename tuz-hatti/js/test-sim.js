import assert from "node:assert/strict";
import { aiAct, createAiMemory } from "./ai.js";
import { cellExplored, cellVisible, command, createMatch, getEntity, step, supplyInfo } from "./sim.js";

function simulate(state, seconds, owners = []) {
  const mems = owners.map(() => createAiMemory());
  const steps = Math.round(seconds / 0.05);
  for (let i = 0; i < steps; i++) {
    owners.forEach((owner, idx) => aiAct(state, owner, 0.05, mems[idx]));
    step(state, 0.05);
    for (const e of state.entities) {
      assert.equal(Number.isFinite(e.x), true, "x");
      assert.equal(Number.isFinite(e.y), true, "y");
      assert.ok(e.x > -2 && e.x < 2102, "bounds x");
      assert.ok(e.y > -2 && e.y < 1302, "bounds y");
    }
  }
}

const economy = createMatch({ seed: 1, playerFaction: "tuz", enemyFaction: "ruzgar" });
const startAsh = economy.players[0].ash;
const hq = economy.entities.find((e) => e.owner === 0 && e.kind === "ocak");
assert.equal(hq.hp, hq.maxHp);
assert.equal(cellExplored(economy, 0, 1800, 650), false);
assert.equal(cellVisible(economy, 0, 540, 500), true);
simulate(economy, 30);
assert.ok(economy.players[0].ash > startAsh + 40, `kül artmadı: ${economy.players[0].ash}`);
assert.ok(economy.players[1].gathered > 0, "rakip de kül topladı");

const built = createMatch({ seed: 2, playerFaction: "tuz", enemyFaction: "ruzgar" });
const worker = built.entities.find((e) => e.owner === 0 && e.kind === "tasiyici");
const placed = command(built, { by: 0, type: "place", workerId: worker.id, kind: "barinak", x: 450, y: 650 });
assert.equal(placed.ok, true, placed.reason);
simulate(built, 20);
const barracks = getEntity(built, placed.id);
assert.equal(barracks.constructed, true, "kışla bitmedi");
const trained = command(built, { by: 0, type: "train", buildingId: barracks.id, unitKind: "mizrak" });
assert.equal(trained.ok, true, trained.reason);
simulate(built, 8);
assert.ok(built.entities.some((e) => e.owner === 0 && e.kind === "mizrak" && e.hp > 0));

const crowded = createMatch({ seed: 3, playerFaction: "tuz", enemyFaction: "ruzgar" });
const hall = crowded.entities.find((e) => e.owner === 0 && e.kind === "ocak");
const supply = supplyInfo(crowded, 0);
const missing = supply.cap - supply.used;
for (let i = 0; i < missing + 2; i++) {
  command(crowded, { by: 0, type: "train", buildingId: hall.id, unitKind: "tasiyici" });
}
assert.ok(supplyInfo(crowded, 0).used <= supplyInfo(crowded, 0).cap);

const fight = createMatch({ seed: 4, playerFaction: "tuz", enemyFaction: "ruzgar" });
fight.entities.push(
  {
    id: 900,
    owner: 0,
    kind: "mizrak",
    isBuilding: false,
    x: 700,
    y: 400,
    hp: 92,
    maxHp: 92,
    radius: 12,
    angle: 0,
    constructed: true,
    progress: 1,
    cooldown: 0,
    queue: null,
    rallyX: 700,
    rallyY: 400,
    carry: 0,
    plan: { kind: "attack", targetId: 901, leash: 400 },
  },
  {
    id: 901,
    owner: 1,
    kind: "izci",
    isBuilding: false,
    x: 724,
    y: 400,
    hp: 58,
    maxHp: 58,
    radius: 11,
    angle: Math.PI,
    constructed: true,
    progress: 1,
    cooldown: 0,
    queue: null,
    rallyX: 724,
    rallyY: 400,
    carry: 0,
    plan: { kind: "attack", targetId: 900, leash: 400 },
  },
);
const before = fight.entities.find((e) => e.id === 901).hp;
simulate(fight, 8);
const foe = fight.entities.find((e) => e.id === 901);
assert.ok(!foe || foe.hp < before, "çatışma hasar vermedi");

const end = createMatch({ seed: 5, playerFaction: "ruzgar", enemyFaction: "tuz" });
end.entities.find((e) => e.owner === 1 && e.kind === "ocak").hp = 0;
step(end, 0.05);
assert.equal(end.winner, 0);

const skirmish = createMatch({ seed: 7, playerFaction: "tuz", enemyFaction: "ruzgar" });
const saw = { barracks: [false, false], fighter: [false, false] };
const mem = [createAiMemory(), createAiMemory()];
for (let t = 0; t < 160; t += 0.05) {
  aiAct(skirmish, 0, 0.05, mem[0]);
  aiAct(skirmish, 1, 0.05, mem[1]);
  step(skirmish, 0.05);
  for (const owner of [0, 1]) {
    if (skirmish.entities.some((e) => e.owner === owner && e.kind === "barinak" && e.constructed)) {
      saw.barracks[owner] = true;
    }
    if (skirmish.entities.some((e) => e.owner === owner && !e.isBuilding && e.kind !== "tasiyici" && e.hp > 0)) {
      saw.fighter[owner] = true;
    }
  }
}
assert.deepEqual(saw.barracks, [true, true], "kışlalar bitmedi");
assert.deepEqual(saw.fighter, [true, true], "iki taraf da birlik üretmedi");

const blocked = createMatch({ seed: 8, playerFaction: "tuz", enemyFaction: "ruzgar" });
const overlap = command(blocked, {
  by: 0,
  type: "place",
  workerId: blocked.entities.find((e) => e.owner === 0 && e.kind === "tasiyici").id,
  kind: "kule",
  x: 300,
  y: 650,
});
assert.equal(overlap.ok, false);

console.log("tuz-hatti sim tamam");
console.log(
  JSON.stringify({
    ash: economy.players[0].ash,
    gathered: economy.players[0].gathered,
    skirmishTime: Math.round(skirmish.time),
    buildings: skirmish.entities.filter((e) => e.isBuilding && e.hp > 0).map((e) => `${e.owner}:${e.kind}`),
    winner: skirmish.winner,
  }),
);
