/** Kısa sentez sesler. Dosya yok, ilk dokunuşta açılır. */

let ctx = null;
let lastShot = 0;

export function unlockAudio() {
  const AC = globalThis.AudioContext || globalThis.webkitAudioContext;
  if (!AC) return;
  if (!ctx) ctx = new AC();
  if (ctx.state === "suspended") ctx.resume();
}

function tone(freq, dur, type, gain, delay = 0) {
  if (!ctx) return;
  const t = ctx.currentTime + delay;
  const osc = ctx.createOscillator();
  const amp = ctx.createGain();
  osc.type = type;
  osc.frequency.setValueAtTime(freq, t);
  amp.gain.setValueAtTime(gain, t);
  amp.gain.exponentialRampToValueAtTime(0.001, t + dur);
  osc.connect(amp);
  amp.connect(ctx.destination);
  osc.start(t);
  osc.stop(t + dur + 0.02);
}

export function playEvent(event, nowMs) {
  if (!ctx) return;
  if (event.type === "shot") {
    if (nowMs - lastShot < 70) return;
    lastShot = nowMs;
    tone(180 + (event.heavy ? 0 : 90), 0.07, "square", 0.03);
    return;
  }
  if (event.type === "bank") {
    tone(520, 0.06, "sine", 0.04);
    tone(760, 0.08, "sine", 0.03, 0.05);
    return;
  }
  if (event.type === "built") {
    tone(240, 0.12, "triangle", 0.05);
    tone(360, 0.14, "triangle", 0.04, 0.08);
    return;
  }
  if (event.type === "death") {
    tone(event.building ? 90 : 140, 0.18, "sawtooth", 0.04);
    return;
  }
  if (event.type === "place") {
    tone(300, 0.08, "triangle", 0.04);
  }
}
