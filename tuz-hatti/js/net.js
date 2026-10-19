/** Ev sahibi simülasyonu yürütür. Konuklar emir yollar, görüntü ev sahibinden gelir. */

import { recomputeFog } from "./sim.js";

const PEER_SRC = "https://unpkg.com/peerjs@1.5.4/dist/peerjs.min.js";
const ALPHABET = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

export function makeCode() {
  let code = "";
  for (let i = 0; i < 4; i++) code += ALPHABET[Math.floor(Math.random() * ALPHABET.length)];
  return code;
}

export function roomId(code) {
  return "cephe-" + String(code || "").trim().toUpperCase();
}

export function packState(state) {
  return {
    time: state.time,
    winner: state.winner,
    nextId: state.nextId,
    players: Object.fromEntries(
      Object.entries(state.players).map(([id, player]) => [
        id,
        { ash: player.ash, gathered: player.gathered, kills: player.kills },
      ]),
    ),
    nodes: state.nodes.map((node) => ({ id: node.id, amount: node.amount })),
    entities: state.entities.map((entity) => ({ ...entity })),
  };
}

export function applyPack(state, pack) {
  if (!state || !pack) return;
  state.time = pack.time;
  state.winner = pack.winner;
  state.nextId = pack.nextId;
  for (const [id, player] of Object.entries(pack.players || {})) {
    if (!state.players[id]) continue;
    state.players[id].ash = player.ash;
    state.players[id].gathered = player.gathered;
    state.players[id].kills = player.kills;
  }
  const amounts = new Map((pack.nodes || []).map((node) => [node.id, node.amount]));
  for (const node of state.nodes) {
    if (amounts.has(node.id)) node.amount = amounts.get(node.id);
  }
  if (Array.isArray(pack.entities)) state.entities = pack.entities;
  recomputeFog(state);
}

function loadPeer() {
  if (typeof window === "undefined") return Promise.reject(new Error("peer"));
  if (window.Peer) return Promise.resolve(window.Peer);
  return new Promise((resolve, reject) => {
    const script = document.createElement("script");
    script.src = PEER_SRC;
    script.async = true;
    script.onload = () => (window.Peer ? resolve(window.Peer) : reject(new Error("peer")));
    script.onerror = () => reject(new Error("peer"));
    document.head.appendChild(script);
  });
}

export function hostRoom(code, hooks) {
  return loadPeer().then(
    (Peer) =>
      new Promise((resolve, reject) => {
        const peer = new Peer(roomId(code));
        let opened = false;
        const timer = setTimeout(() => {
          if (opened) return;
          peer.destroy();
          reject(new Error("timeout"));
        }, 9000);
        peer.on("open", () => {
          opened = true;
          clearTimeout(timer);
          const links = new Set();
          peer.on("connection", (conn) => {
            conn.on("open", () => {
              const slot = hooks.onGuest(conn);
              if (slot == null) return;
              links.add(conn);
              conn.on("data", (msg) => {
                if (msg && msg.t === "cmd" && msg.cmd) hooks.onCommand(slot, msg.cmd);
              });
              conn.on("close", () => {
                links.delete(conn);
                hooks.onLeave?.(slot);
              });
            });
          });
          resolve({
            broadcast(pack, extra) {
              const msg = { t: "snap", pack, paused: !!extra?.paused };
              for (const conn of links) {
                if (conn.open) conn.send(msg);
              }
            },
            close() {
              peer.destroy();
            },
          });
        });
        peer.on("error", (err) => {
          if (!opened) {
            clearTimeout(timer);
            reject(err);
          }
        });
      }),
  );
}

export function joinRoom(code, hooks) {
  return loadPeer().then(
    (Peer) =>
      new Promise((resolve, reject) => {
        const peer = new Peer();
        let opened = false;
        const timer = setTimeout(() => {
          if (opened) return;
          peer.destroy();
          reject(new Error("timeout"));
        }, 9000);
        peer.on("open", () => {
          const conn = peer.connect(roomId(code), { reliable: true });
          conn.on("data", (msg) => {
            if (!msg) return;
            if (msg.t === "start") hooks.onStart(msg);
            if (msg.t === "snap") hooks.onSnap(msg);
            if (msg.t === "full") hooks.onFull?.();
            if (msg.t === "seat") hooks.onSeat?.(msg);
          });
          conn.on("open", () => {
            opened = true;
            clearTimeout(timer);
            resolve({
              send(cmd) {
                if (conn.open) conn.send({ t: "cmd", cmd });
              },
              close() {
                peer.destroy();
              },
            });
          });
          conn.on("error", () => hooks.onStatus?.("Bağlantı koptu"));
        });
        peer.on("error", (err) => {
          if (!opened) {
            clearTimeout(timer);
            reject(err);
          }
        });
      }),
  );
}
