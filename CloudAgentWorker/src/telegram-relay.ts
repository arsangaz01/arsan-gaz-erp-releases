export interface RelayEnv {
  AI: Ai;
  STATE?: KVNamespace;
  TELEGRAM_BOT_TOKEN?: string;
  TELEGRAM_CHAT_ID?: string;
  BRIEFING_CITY?: string;
  BRIEFING_HOUR?: string;
}

export interface DeferredCommand {
  updateId: number;
  chatId: number;
  text: string;
  receivedUtc: string;
}

interface TelegramMessage {
  message_id: number;
  date: number;
  chat?: { id?: number };
  text?: string;
}

interface TelegramUpdate {
  update_id: number;
  message?: TelegramMessage;
}

const RELAY_MODEL = "@cf/openai/gpt-oss-20b";
// Watcher heartbeats every 2 minutes; KV's free tier allows ~1000 writes per day.
export const WATCHER_OFFLINE_AFTER_MS = 5 * 60 * 1000;
const LAST_SEEN_KEY = "watcher:lastSeen";
const DEFERRED_KEY = "telegram:deferred";
const BRIEFING_KEY = "briefing:lastDate";
const MAX_DEFERRED = 50;
const ISTANBUL_OFFSET_MS = 3 * 60 * 60 * 1000;
const NEWS_FEED = "https://news.google.com/rss?hl=tr&gl=TR&ceid=TR:tr";

const PROVINCES = [
  "Adana", "Adıyaman", "Afyonkarahisar", "Ağrı", "Amasya", "Ankara", "Antalya", "Artvin", "Aydın", "Balıkesir",
  "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur", "Bursa", "Çanakkale", "Çankırı", "Çorum", "Denizli",
  "Diyarbakır", "Edirne", "Elazığ", "Erzincan", "Erzurum", "Eskişehir", "Gaziantep", "Giresun", "Gümüşhane", "Hakkari",
  "Hatay", "Isparta", "Mersin", "İstanbul", "İzmir", "Kars", "Kastamonu", "Kayseri", "Kırklareli", "Kırşehir",
  "Kocaeli", "Konya", "Kütahya", "Malatya", "Manisa", "Kahramanmaraş", "Mardin", "Muğla", "Muş", "Nevşehir",
  "Niğde", "Ordu", "Rize", "Sakarya", "Samsun", "Siirt", "Sinop", "Sivas", "Tekirdağ", "Tokat",
  "Trabzon", "Tunceli", "Şanlıurfa", "Uşak", "Van", "Yozgat", "Zonguldak", "Aksaray", "Bayburt", "Karaman",
  "Kırıkkale", "Batman", "Şırnak", "Bartın", "Ardahan", "Iğdır", "Yalova", "Karabük", "Kilis", "Osmaniye", "Düzce",
];

export function isRelayConfigured(env: RelayEnv): env is RelayEnv & {
  STATE: KVNamespace;
  TELEGRAM_BOT_TOKEN: string;
  TELEGRAM_CHAT_ID: string;
} {
  return Boolean(env.STATE && env.TELEGRAM_BOT_TOKEN && env.TELEGRAM_CHAT_ID);
}

export async function recordWatcherHeartbeat(env: RelayEnv, now = Date.now()): Promise<DeferredCommand[]> {
  if (!env.STATE) return [];
  await env.STATE.put(LAST_SEEN_KEY, String(now));
  return readDeferred(env.STATE);
}

export async function acknowledgeDeferred(env: RelayEnv, updateIds: number[]): Promise<number> {
  if (!env.STATE) return 0;
  const remaining = (await readDeferred(env.STATE)).filter((item) => !updateIds.includes(item.updateId));
  await env.STATE.put(DEFERRED_KEY, JSON.stringify(remaining));
  return remaining.length;
}

export async function runScheduledRelay(env: RelayEnv, now = Date.now()): Promise<void> {
  if (!isRelayConfigured(env)) return;
  await sendMorningBriefingIfDue(env, now);

  const lastSeen = Number((await env.STATE.get(LAST_SEEN_KEY)) ?? "0");
  if (now - lastSeen < WATCHER_OFFLINE_AFTER_MS) return;

  const updates = await telegram<TelegramUpdate[]>(env, "getUpdates", {
    timeout: 0,
    allowed_updates: ["message"],
  });
  if (updates.length === 0) return;

  const allowedChatId = Number(env.TELEGRAM_CHAT_ID);
  const deferred = await readDeferred(env.STATE);
  for (const update of updates) {
    const message = update.message;
    // Older messages were already seen by the Watcher before it went offline.
    if (!message?.text || message.chat?.id !== allowedChatId || message.date * 1000 < lastSeen) continue;
    try {
      const reply = await answerOfflineMessage(env, message.text.trim(), lastSeen, deferred, update.update_id);
      await sendTelegram(env, reply);
    } catch (error) {
      console.warn(`Offline Telegram reply failed (update ${update.update_id}): ${error instanceof Error ? error.name : "unknown"}`);
    }
  }

  try {
    await env.STATE.put(DEFERRED_KEY, JSON.stringify(deferred.slice(-MAX_DEFERRED)));
  } finally {
    // Confirm even after a failed reply so the same messages are not answered again next minute.
    const confirmOffset = updates[updates.length - 1].update_id + 1;
    await telegram(env, "getUpdates", { offset: confirmOffset, timeout: 0, limit: 1, allowed_updates: ["message"] });
  }
}

async function answerOfflineMessage(
  env: RelayEnv,
  text: string,
  lastSeen: number,
  deferred: DeferredCommand[],
  updateId: number,
): Promise<string> {
  const lower = text.toLocaleLowerCase("tr-TR");
  const command = lower.split(/\s+/, 1)[0].replace(/@.*$/, "");
  const offlineSince = lastSeen > 0 ? formatIstanbul(lastSeen) : "bilinmiyor";

  if (command === "/durum") {
    return `☁️ 7/24 asistan çalışıyor.\nBilgisayar/AgentWatcher çevrimdışı (son sinyal: ${offlineSince}).\n` +
      `Bilgisayar açılınca çalıştırılacak bekleyen emir: ${deferred.length}.`;
  }
  if (command === "/start" || command === "/yardim" || command === "/help") {
    return "☁️ Bilgisayar kapalıyken 7/24 bulut asistanı yanıtlıyor.\n" +
      "• Düz mesaj: sohbet, hava durumu, döviz kuru, gündem\n" +
      "• /emir, /gorev, /ekip ve diğer komutlar: kaydedilir, bilgisayar açılınca AgentWatcher çalıştırır\n" +
      "• /durum: bilgisayar durumu ve bekleyen emirler";
  }
  if (lower.startsWith("/")) {
    deferred.push({ updateId, chatId: Number(env.TELEGRAM_CHAT_ID), text, receivedUtc: new Date().toISOString() });
    return "☁️ Bilgisayar şu an kapalı. Emriniz kaydedildi; bilgisayar ve AgentWatcher açılınca otomatik çalıştırılıp " +
      `rapor buraya gönderilecek. (Bekleyen emir: ${deferred.length})`;
  }

  try {
    const live = await answerLiveQuestion(env, lower);
    if (live) return `☁️ ${live}`;
  } catch {
    return "☁️ Canlı veri şu an alınamadı; birkaç dakika sonra yeniden deneyin.";
  }

  const answer = await completeJarvis(env, text);
  return `☁️ ${answer}\n\n(Bilgisayar kapalı; 7/24 bulut asistanı yanıtladı. ERP veya kod işi için /emir yazın, bilgisayar açılınca yapılır.)`;
}

async function answerLiveQuestion(env: RelayEnv, lower: string): Promise<string | null> {
  if (/(^|[^a-zçğıöşü])hava(sı|lar)?([^a-zçğıöşü]|$)|sıcaklık|kaç derece|yağmur|yağış|rüzgar|rüzgâr/u.test(lower)) {
    return weatherSummary(findProvince(lower) ?? env.BRIEFING_CITY ?? "Adana");
  }
  if (/(dolar|euro|avro|sterlin|döviz)/.test(lower) && /(kur|kaç|ne kadar|fiyat|döviz)/.test(lower)) {
    return currencySummary();
  }
  if (/(gündem|haber|manşet)/.test(lower)) {
    return newsSummary();
  }
  return null;
}

export function findProvince(lowerText: string): string | null {
  const tokens = lowerText.split(/[^a-zçğıöşüâîû]+/u).filter(Boolean);
  for (const token of tokens) {
    for (const province of PROVINCES) {
      const name = province.toLocaleLowerCase("tr-TR");
      if (token === name || (name.length >= 5 && token.startsWith(name) && token.length - name.length <= 4)) {
        return province;
      }
    }
  }
  return null;
}

async function completeJarvis(env: RelayEnv, text: string): Promise<string> {
  const result = (await env.AI.run(RELAY_MODEL, {
    messages: [
      {
        role: "system",
        content:
          "Sen Arsan Gaz'ın Jarvis adlı 7/24 Telegram asistanısın. Kullanıcının bilgisayarı ve ERP şu an kapalı; " +
          "ERP verisine, dosyalara veya koda erişimin yok ve erişmiş gibi davranma. Kayıt, ödeme, müşteri iletişimi " +
          "veya başka bir işlem yapmış gibi gösterme. Türkçe, kısa ve doğrudan yanıt ver.",
      },
      { role: "user", content: text.slice(0, 4000) },
    ],
    max_tokens: 1024,
    temperature: 0.3,
  })) as { response?: string | null; choices?: Array<{ message?: { content?: string | null } }> } | null;
  const answer = (result?.response ?? result?.choices?.[0]?.message?.content ?? "").trim();
  return answer || "Şu an yanıt üretemedim; biraz sonra yeniden deneyin.";
}

async function sendMorningBriefingIfDue(
  env: RelayEnv & { STATE: KVNamespace; TELEGRAM_BOT_TOKEN: string; TELEGRAM_CHAT_ID: string },
  now: number,
): Promise<void> {
  const istanbul = new Date(now + ISTANBUL_OFFSET_MS);
  const hour = Number(env.BRIEFING_HOUR ?? "8");
  if (!Number.isInteger(hour) || hour < 0 || hour > 23 || istanbul.getUTCHours() !== hour) return;
  const today = istanbul.toISOString().slice(0, 10);
  if ((await env.STATE.get(BRIEFING_KEY)) === today) return;
  await env.STATE.put(BRIEFING_KEY, today);
  await sendTelegram(env, await buildBriefing(env, now));
}

export async function buildBriefing(env: RelayEnv, now = Date.now()): Promise<string> {
  const city = env.BRIEFING_CITY ?? "Adana";
  const sections = await Promise.all([
    weatherSummary(city).catch(() => `${city} hava durumu alınamadı.`),
    currencySummary().catch(() => "TCMB kurları alınamadı."),
    newsSummary().catch(() => "Gündem başlıkları alınamadı."),
  ]);
  const lastSeen = Number((await env.STATE?.get(LAST_SEEN_KEY)) ?? "0");
  const watcher = now - lastSeen < WATCHER_OFFLINE_AFTER_MS
    ? "Bilgisayar/AgentWatcher: çevrimiçi"
    : `Bilgisayar/AgentWatcher: çevrimdışı (son sinyal: ${lastSeen > 0 ? formatIstanbul(lastSeen) : "bilinmiyor"})`;
  const deferred = env.STATE ? (await readDeferred(env.STATE)).length : 0;
  return [
    `☀️ Günaydın! ${formatIstanbul(now, true)} sabah özeti`,
    ...sections,
    `${watcher}${deferred > 0 ? ` · bekleyen emir: ${deferred}` : ""}`,
  ].join("\n\n");
}

export async function weatherSummary(city: string): Promise<string> {
  const geocode = (await fetchJson(
    `https://geocoding-api.open-meteo.com/v1/search?count=1&language=tr&countryCode=TR&name=${encodeURIComponent(city)}`,
  )) as { results?: Array<{ latitude: number; longitude: number }> };
  const place = geocode.results?.[0];
  if (!place) return `"${city}" için konum bulunamadı.`;
  const forecast = (await fetchJson(
    `https://api.open-meteo.com/v1/forecast?latitude=${place.latitude}&longitude=${place.longitude}` +
      "&current=temperature_2m,apparent_temperature,weather_code,wind_speed_10m" +
      "&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,weather_code" +
      "&timezone=Europe%2FIstanbul&forecast_days=1",
  )) as {
    current: { temperature_2m: number; apparent_temperature: number; weather_code: number; wind_speed_10m: number };
    daily: {
      temperature_2m_max: number[];
      temperature_2m_min: number[];
      precipitation_probability_max: Array<number | null>;
      weather_code: number[];
    };
  };
  const current = forecast.current;
  const daily = forecast.daily;
  const rain = daily.precipitation_probability_max[0];
  return `🌤 ${city}: şu an ${round(current.temperature_2m)} °C (hissedilen ${round(current.apparent_temperature)} °C), ` +
    `${describeWeather(current.weather_code)}, rüzgâr ${round(current.wind_speed_10m)} km/s.\n` +
    `Bugün ${round(daily.temperature_2m_min[0])}–${round(daily.temperature_2m_max[0])} °C, ` +
    `${describeWeather(daily.weather_code[0])}, yağış olasılığı ${rain === null || rain === undefined ? "bilinmiyor" : `%${rain}`}. (Open-Meteo)`;
}

export async function currencySummary(): Promise<string> {
  const response = await fetch("https://www.tcmb.gov.tr/kurlar/today.xml");
  if (!response.ok) throw new Error(`TCMB HTTP ${response.status}`);
  const xml = await response.text();
  const date = /Tarih="([^"]+)"/.exec(xml)?.[1] ?? "?";
  const lines = [["USD", "Dolar"], ["EUR", "Euro"], ["GBP", "Sterlin"]].map(([code, name]) => {
    const block = new RegExp(`CurrencyCode="${code}"[\\s\\S]*?</Currency>`).exec(xml)?.[0] ?? "";
    const buying = /<ForexBuying>([^<]*)</.exec(block)?.[1];
    const selling = /<ForexSelling>([^<]*)</.exec(block)?.[1];
    return `${name}: alış ${formatRate(buying)} · satış ${formatRate(selling)} TL`;
  });
  return `💱 TCMB kurları (${date}):\n${lines.join("\n")}`;
}

export async function newsSummary(limit = 5): Promise<string> {
  const response = await fetch(NEWS_FEED, { headers: { "User-Agent": "ArsanGazERP-Briefing/1.0" } });
  if (!response.ok) throw new Error(`Haber akışı HTTP ${response.status}`);
  const xml = await response.text();
  const titles = [...xml.matchAll(/<item>[\s\S]*?<title>([\s\S]*?)<\/title>/g)]
    .map((match) => decodeXml(match[1]).trim())
    .filter(Boolean)
    .slice(0, limit);
  if (titles.length === 0) return "📰 Gündem başlığı bulunamadı.";
  return `📰 Gündem (Google Haberler):\n${titles.map((title) => `• ${title}`).join("\n")}`;
}

async function readDeferred(state: KVNamespace): Promise<DeferredCommand[]> {
  try {
    const value = JSON.parse((await state.get(DEFERRED_KEY)) ?? "[]") as unknown;
    return Array.isArray(value) ? (value as DeferredCommand[]) : [];
  } catch {
    return [];
  }
}

async function sendTelegram(env: RelayEnv & { TELEGRAM_CHAT_ID: string }, text: string): Promise<void> {
  for (let offset = 0; offset < text.length; offset += 3900) {
    await telegram(env, "sendMessage", {
      chat_id: Number(env.TELEGRAM_CHAT_ID),
      text: text.slice(offset, offset + 3900),
      disable_web_page_preview: true,
    });
  }
}

async function telegram<T = unknown>(env: RelayEnv, method: string, body: Record<string, unknown>): Promise<T> {
  const response = await fetch(`https://api.telegram.org/bot${env.TELEGRAM_BOT_TOKEN}/${method}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  const payload = (await response.json()) as { ok?: boolean; result?: T; description?: string };
  if (!payload.ok) throw new Error(`Telegram ${method} başarısız: ${payload.description ?? response.status}`);
  return payload.result as T;
}

async function fetchJson(url: string): Promise<unknown> {
  const response = await fetch(url, { headers: { "User-Agent": "ArsanGazERP-Briefing/1.0" } });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.json();
}

function formatIstanbul(epochMs: number, dateOnly = false): string {
  const local = new Date(epochMs + ISTANBUL_OFFSET_MS);
  const date = `${pad(local.getUTCDate())}.${pad(local.getUTCMonth() + 1)}.${local.getUTCFullYear()}`;
  return dateOnly ? date : `${date} ${pad(local.getUTCHours())}:${pad(local.getUTCMinutes())}`;
}

function pad(value: number): string {
  return String(value).padStart(2, "0");
}

function round(value: number): string {
  return (Math.round(value * 10) / 10).toLocaleString("tr-TR");
}

function formatRate(value: string | undefined): string {
  const number = Number(value);
  return Number.isFinite(number) && value ? number.toLocaleString("tr-TR", { minimumFractionDigits: 2, maximumFractionDigits: 4 }) : "-";
}

function decodeXml(value: string): string {
  return value
    .replace(/<!\[CDATA\[([\s\S]*?)\]\]>/g, "$1")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&quot;/g, '"')
    .replace(/&#39;|&apos;/g, "'");
}

function describeWeather(code: number): string {
  if (code === 0) return "açık";
  if (code <= 2) return "az bulutlu";
  if (code === 3) return "kapalı";
  if (code === 45 || code === 48) return "sisli";
  if (code >= 51 && code <= 57) return "çiseleme";
  if (code >= 61 && code <= 67) return "yağmurlu";
  if (code >= 71 && code <= 77) return "karlı";
  if (code >= 80 && code <= 82) return "sağanak yağışlı";
  if (code === 85 || code === 86) return "kar sağanaklı";
  if (code >= 95) return "gök gürültülü fırtına";
  return "değişken";
}
