/** Sentez ses. Kayıtlı ses dosyası yok; silah, inşaat ve arayüz ayrı katmanlarda. */

let audioCtx = null;
let lastShot = 0;
let noiseBuf = null;

export function unlockAudio() {
  const AC = globalThis.AudioContext || globalThis.webkitAudioContext;
  if (!AC) return;
  if (!audioCtx) audioCtx = new AC();
  if (audioCtx.state === "suspended") audioCtx.resume();
}

function noise() {
  if (noiseBuf || !audioCtx) return noiseBuf;
  const len = Math.floor(audioCtx.sampleRate * 0.4);
  noiseBuf = audioCtx.createBuffer(1, len, audioCtx.sampleRate);
  const data = noiseBuf.getChannelData(0);
  let last = 0;
  for (let i = 0; i < len; i++) {
    const white = Math.random() * 2 - 1;
    last = last * 0.82 + white * 0.18;
    data[i] = last;
  }
  return noiseBuf;
}

function burst(freq, dur, gain, delay = 0, type = "bandpass", q = 0.8) {
  if (!audioCtx) return;
  const t = audioCtx.currentTime + delay;
  const src = audioCtx.createBufferSource();
  src.buffer = noise();
  const filter = audioCtx.createBiquadFilter();
  filter.type = type;
  filter.frequency.setValueAtTime(freq, t);
  filter.Q.value = q;
  const amp = audioCtx.createGain();
  amp.gain.setValueAtTime(gain, t);
  amp.gain.exponentialRampToValueAtTime(0.001, t + dur);
  src.connect(filter);
  filter.connect(amp);
  amp.connect(audioCtx.destination);
  src.start(t);
  src.stop(t + dur + 0.02);
}

function tone(freq, dur, type, gain, delay = 0) {
  if (!audioCtx) return;
  const t = audioCtx.currentTime + delay;
  const osc = audioCtx.createOscillator();
  const amp = audioCtx.createGain();
  const filter = audioCtx.createBiquadFilter();
  filter.type = "lowpass";
  filter.frequency.value = 1800;
  osc.type = type;
  osc.frequency.setValueAtTime(freq, t);
  if (type !== "sine") osc.frequency.exponentialRampToValueAtTime(Math.max(40, freq * 0.6), t + dur);
  amp.gain.setValueAtTime(gain, t);
  amp.gain.exponentialRampToValueAtTime(0.001, t + dur);
  osc.connect(filter);
  filter.connect(amp);
  amp.connect(audioCtx.destination);
  osc.start(t);
  osc.stop(t + dur + 0.02);
}

export function playEvent(event, nowMs) {
  if (!audioCtx) return;
  if (event.type === "ui") {
    tone(620, 0.035, "sine", 0.03);
    burst(1400, 0.03, 0.015, 0, "highpass", 0.6);
    return;
  }
  if (event.type === "shot") {
    if (nowMs - lastShot < 55) return;
    lastShot = nowMs;
    if (event.air) {
      burst(900, 0.09, 0.04, 0, "bandpass", 1.4);
      tone(240, 0.08, "triangle", 0.02);
      return;
    }
    if (event.heavy) {
      burst(140, 0.16, 0.07, 0, "lowpass", 0.7);
      tone(70, 0.18, "sine", 0.05);
      return;
    }
    burst(420, 0.07, 0.045, 0, "bandpass", 1.1);
    tone(180, 0.05, "triangle", 0.02);
    return;
  }
  if (event.type === "bank") {
    tone(480, 0.05, "sine", 0.035);
    tone(720, 0.07, "sine", 0.025, 0.04);
    return;
  }
  if (event.type === "built") {
    burst(180, 0.12, 0.04);
    tone(196, 0.12, "triangle", 0.04, 0.02);
    tone(294, 0.16, "triangle", 0.035, 0.1);
    return;
  }
  if (event.type === "death") {
    burst(event.building ? 90 : 160, 0.22, 0.05, 0, "lowpass", 0.6);
    tone(event.building ? 60 : 110, 0.2, "sine", 0.03);
    return;
  }
  if (event.type === "place") {
    burst(240, 0.08, 0.035);
    tone(330, 0.07, "triangle", 0.03, 0.02);
  }
}
