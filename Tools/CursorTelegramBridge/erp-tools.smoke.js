"use strict";

// Calls every read-only ERP tool against the real database and checks that no unmasked phone or e-mail leaks.
// Usage: node erp-tools.smoke.js

const path = require("node:path");
const { loadErpTools } = require("./erp-tools");

const samples = {
  erp_ozet: {},
  musteri_ara: { arama: "a" },
  tup_ara: { kod: "YOK-123" },
  musterideki_tupler: {},
  test_tarihi_yaklasan_tupler: { gun: 30 },
  siparis_tahmini: {},
  son_hareketler: { gun: 7 },
  crm_ozet: {},
  crm_firma_ara: { arama: "gaz" },
  crm_takip_listesi: { gun: 7 },
};
const UNMASKED = /(?<![*\d])0?5\d{2}[\s-]?\d{3}[\s-]?\d{2}[\s-]?\d{2}(?!\d)|[A-Za-z0-9._%+-]{2,}@[A-Za-z0-9-]+\./;

(async () => {
  const { tools, client } = await loadErpTools(path.resolve(__dirname, "..", ".."));
  let failures = 0;
  for (const [name, args] of Object.entries(samples)) {
    if (!tools[name]) {
      console.log(`${name}: ARAÇ YOK`);
      failures++;
      continue;
    }
    const started = Date.now();
    const result = await tools[name].execute(args);
    const text = result.content.map((block) => block.text).join("\n");
    const leaked = UNMASKED.test(text);
    if (result.isError || leaked) failures++;
    console.log(`${name}: hata=${result.isError} süre=${Date.now() - started}ms satır=${text.split("\n").length} sızıntı=${leaked}`);
  }
  const empty = await tools.musteri_ara.execute({ arama: "" });
  console.log(`boş arama: ${empty.content[0].text}`);
  const wrongType = await tools.son_hareketler.execute({ gun: "abc" });
  console.log(`hatalı tip: hata=${wrongType.isError} ${wrongType.content[0].text.slice(0, 120)}`);
  client?.stop();
  console.log(failures === 0 ? "SONUÇ: tüm araçlar geçti" : `SONUÇ: ${failures} sorun`);
  process.exitCode = failures === 0 ? 0 : 1;
})().catch((error) => {
  console.error("HATA", error);
  process.exitCode = 1;
});
