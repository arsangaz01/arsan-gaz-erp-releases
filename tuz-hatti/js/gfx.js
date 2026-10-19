/** Görüntü basamakları. Simülasyonu değiştirmez; yalnız çizim yükünü keser. */

export const GFX_PRESETS = {
  low: {
    id: "low",
    label: "Düşük",
    hint: "Eski ve zayıf telefon. Az piksel, akıcı kare.",
    dpr: 1,
    terrainScale: 0.28,
    shadows: false,
    decor: false,
    smoothing: false,
    fogSmooth: false,
    fxCap: 10,
    viewWidth: 820,
    nodeScale: 0.72,
  },
  medium: {
    id: "medium",
    label: "Orta",
    hint: "Çoğu telefon. Görüntü ve akıcılık dengede.",
    dpr: 1.35,
    terrainScale: 0.42,
    shadows: true,
    decor: true,
    smoothing: true,
    fogSmooth: true,
    fxCap: 28,
    viewWidth: 680,
    nodeScale: 0.9,
  },
  high: {
    id: "high",
    label: "Yüksek",
    hint: "Güçlü telefon. Daha keskin arazi ve araç.",
    dpr: 2,
    terrainScale: 0.55,
    shadows: true,
    decor: true,
    smoothing: true,
    fogSmooth: true,
    fxCap: 64,
    viewWidth: 540,
    nodeScale: 1,
  },
};

const RANK = { low: 0, medium: 1, high: 2 };

export function suggestTier(info) {
  const ram = Number(info?.ramMb) || 0;
  const cores = Number(info?.cores) || 4;
  const shortSide = Math.min(Number(info?.width) || 800, Number(info?.height) || 600);
  if (ram > 0) {
    if (ram < 2800 || (ram < 3600 && cores <= 4)) return "low";
    if (ram >= 7000 && cores >= 8) return "high";
    if (ram >= 5500 && cores >= 6 && shortSide >= 1080) return "high";
    return "medium";
  }
  if (cores <= 4 && shortSide < 900) return "low";
  if (cores >= 8 && shortSide >= 1080) return "high";
  return "medium";
}

export function tierAbove(chosen, suggested) {
  return (RANK[chosen] || 0) > (RANK[suggested] || 0);
}
