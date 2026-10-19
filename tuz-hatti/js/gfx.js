/** Görüntü basamakları. Simülasyonu değiştirmez; yalnız çizim yükünü keser. */

export const GFX_PRESETS = {
  low: {
    id: "low",
    label: "Düşük",
    hint: "Eski ve zayıf telefon. Az piksel, akıcı kare.",
    dpr: 1.25,
    terrainScale: 1,
    shadows: true,
    decor: false,
    smoothing: true,
    fogSmooth: false,
    fxCap: 48,
    viewWidth: 760,
    nodeScale: 0.8,
  },
  medium: {
    id: "medium",
    label: "Orta",
    hint: "Çoğu telefon. Keskin arazi ve akıcı hareket.",
    dpr: 2,
    terrainScale: 1.35,
    shadows: true,
    decor: true,
    smoothing: true,
    fogSmooth: true,
    fxCap: 96,
    viewWidth: 600,
    nodeScale: 1,
  },
  high: {
    id: "high",
    label: "Yüksek",
    hint: "Güçlü telefon. En keskin arazi ve birlik.",
    dpr: 2.75,
    terrainScale: 1.75,
    shadows: true,
    decor: true,
    smoothing: true,
    fogSmooth: true,
    fxCap: 140,
    viewWidth: 500,
    nodeScale: 1.1,
  },
};

const RANK = { low: 0, medium: 1, high: 2 };

export function suggestTier(info) {
  const ram = Number(info?.ramMb) || 0;
  const cores = Number(info?.cores) || 4;
  const shortSide = Math.min(Number(info?.width) || 800, Number(info?.height) || 600);
  const density = Number(info?.density) || 1;
  if (ram > 0) {
    if (ram < 2400 || (ram < 3200 && cores <= 4)) return "low";
    if (ram >= 6000 && cores >= 6 && (shortSide >= 900 || density >= 2)) return "high";
    if (ram >= 4500 && cores >= 6) return "high";
    return "medium";
  }
  if (cores <= 4 && shortSide < 800) return "low";
  if (cores >= 6 && (shortSide >= 1000 || density >= 2)) return "high";
  return "medium";
}

export function tierAbove(chosen, suggested) {
  return (RANK[chosen] || 0) > (RANK[suggested] || 0);
}
