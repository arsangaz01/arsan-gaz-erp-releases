"use strict";

const $ = (id) => document.getElementById(id);
const BASE = "";
let options = { statuses: [], gasTypes: [], plates: [], longDays: 60 };
let current = null;
let me = null;

// "a|b" = any of them.
// "a|b" = any; "a&b" = all. Nested: "disa-aktar&musteri-stok-toplam".
const can = (permission) => !!me && permission.split("|").some((group) => group.split("&").every((p) => me.permissions.includes(p)));

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") node.className = value;
    else if (key === "onclick" || key === "onchange" || key === "oninput" || key === "onkeydown") {
      node.addEventListener(key.slice(2), value);
    } else if (key === "checked" || key === "selected" || key === "disabled") {
      node[key] = !!value;
    } else if (value !== undefined && value !== null) node.setAttribute(key, value);
  }
  for (const child of children) {
    if (child === null || child === undefined) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

function show(box, text, kind = "err") {
  box.textContent = text;
  box.className = "msg " + kind;
}

function hide(box) { box.className = "msg hidden"; }

// Lists refresh shortly after typing stops (Enter still works); filter selects and checkboxes refresh at once.
// delayMs kısa: ilk harften sonuç gelsin; her sekmede aynı davranış.
function liveFilter(inputIds, changeIds, load, delayMs = 180) {
  let timer = 0;
  const run = () => { clearTimeout(timer); timer = setTimeout(() => { void Promise.resolve(load()).catch(() => {}); }, delayMs); };
  for (const id of inputIds) {
    const node = $(id);
    if (!node) continue;
    node.addEventListener("input", run);
  }
  for (const id of changeIds) {
    const node = $(id);
    if (!node) continue;
    node.addEventListener("change", () => { clearTimeout(timer); void Promise.resolve(load()).catch(() => {}); });
  }
}

// Only the newest request of a list may draw it, so a slow earlier answer cannot overwrite a newer one.
const listRequests = {};
const newRequest = (name) => (listRequests[name] = (listRequests[name] || 0) + 1);
const isStale = (name, ticket) => listRequests[name] !== ticket;

async function api(path, options = {}) {
  const init = { method: options.method || "GET", headers: {}, credentials: "same-origin" };
  if (options.body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }
  if (init.method !== "GET") init.headers["X-Panel-Istek"] = "1";
  const response = await fetch(path, init);
  let data = {};
  try { data = await response.json(); } catch { data = {}; }
  if (response.status === 401 && path.startsWith("/panel/api/")) {
    showLogin("Oturum süresi doldu; yeniden giriş yapın.");
    throw new Error("Oturum kapalı.");
  }
  if (response.status === 403 && data.mustChangePassword) {
    forcePasswordChange();
  }
  if (!response.ok) {
    const error = new Error(data.error || `Sunucu hatası (${response.status}).`);
    error.status = response.status;
    error.data = data;
    throw error;
  }
  return data;
}

// Hides what the signed-in role may not do; the server enforces the same rules.
function applyPermissions() {
  for (const node of document.querySelectorAll("[data-perm]")) node.classList.toggle("hidden", !can(node.dataset.perm));
}

function roleBadge(role) { return el("span", { class: "role role-" + role }, role); }

function forcePasswordChange() {
  if ($("password-dialog").open && $("password-dialog").dataset.forced === "1") return;
  $("password-dialog").dataset.forced = "1";
  $("password-forced").classList.remove("hidden");
  $("password-close").classList.add("hidden");
  $("password-form").reset();
  $("password-user").value = me ? me.user : "";
  hide($("password-msg"));
  if (!$("password-dialog").open) $("password-dialog").showModal();
  $("p-current").focus();
}

$("password-dialog").addEventListener("cancel", (event) => {
  if ($("password-dialog").dataset.forced === "1") event.preventDefault();
});

function badge(status, name) { return el("span", { class: "badge b" + status }, name); }

function showLogin(message) {
  $("app").classList.add("hidden");
  $("login").classList.remove("hidden");
  if (message) show($("login-msg"), message); else hide($("login-msg"));
  $("login-user").focus();
}

async function start() {
  try {
    me = await api("/panel/api/ben");
    $("who").replaceChildren(me.displayName && me.displayName !== me.user ? `${me.displayName} (${me.user})` : me.user, roleBadge(me.role));
    applyPermissions();
    $("login").classList.add("hidden");
    $("app").classList.remove("hidden");
    if (me.mustChangePassword) {
      forcePasswordChange();
      return;
    }
    await loadOptions();
    const first = [...document.querySelectorAll("nav button")].find((b) => !b.classList.contains("hidden"));
    if (first) openTab(first.dataset.tab);
    else show($("global-msg"), "Hesabınıza henüz hiçbir ekran yetkisi verilmemiş. Yöneticinizden yetki isteyin.", "warn");
    if (can("musteri-goruntule")) for (const picker of [cylinderCustomer, movementCustomer, newCustomer]) picker.search();
  } catch (error) {
    if (!$("app").classList.contains("hidden")) show($("global-msg"), error.message);
  }
}

$("login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = event.submitter;
  if (button) button.disabled = true;
  try {
    await api("/panel/giris", { method: "POST", body: { kullanici: $("login-user").value, sifre: $("login-pass").value } });
    $("login-pass").value = "";
    await start();
  } catch (error) {
    show($("login-msg"), error.message);
  } finally {
    if (button) button.disabled = false;
  }
});

$("logout").addEventListener("click", async () => {
  try { await api("/panel/cikis", { method: "POST" }); } catch { /* already logged out */ }
  me = null;
  for (const d of document.querySelectorAll("dialog")) if (d.open) d.close();
  showLogin();
});

$("password-open").addEventListener("click", () => {
  $("password-form").reset();
  $("password-user").value = me ? me.user : "";
  delete $("password-dialog").dataset.forced;
  $("password-forced").classList.add("hidden");
  $("password-close").classList.remove("hidden");
  hide($("password-msg"));
  $("password-dialog").showModal();
  $("p-current").focus();
});
$("password-close").addEventListener("click", () => $("password-dialog").close());

$("password-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if ($("p-new").value !== $("p-repeat").value) { show($("password-msg"), "Yeni şifre ile tekrarı aynı değil."); return; }
  const button = event.submitter;
  if (button) button.disabled = true;
  try {
    const result = await api("/panel/api/sifre", { method: "POST", body: { mevcut: $("p-current").value, yeni: $("p-new").value, yeniTekrar: $("p-repeat").value } });
    $("password-form").reset();
    if ($("password-dialog").dataset.forced === "1") {
      delete $("password-dialog").dataset.forced;
      $("password-dialog").close();
      await start();
      show($("global-msg"), "Şifreniz değiştirildi. Hoş geldiniz.", "ok");
      return;
    }
    show($("password-msg"), result.message, "ok");
  } catch (error) {
    show($("password-msg"), error.message);
  } finally {
    if (button) button.disabled = false;
  }
});

for (const button of document.querySelectorAll("nav button")) {
  button.addEventListener("click", () => openTab(button.dataset.tab));
}

function openTab(tab) {
  for (const b of document.querySelectorAll("nav button")) b.classList.toggle("active", b.dataset.tab === tab);
  for (const s of document.querySelectorAll("section[data-page]")) s.classList.toggle("hidden", s.dataset.page !== tab);
  hide($("global-msg"));
  if (tab === "ozet") loadSummary();
  if (tab === "tupler") loadCylinders();
  if (tab === "hareketler") loadMovements();
  if (tab === "dolum") loadFills();
  if (tab === "seferler") loadTrips();
  if (tab === "araclar") loadVehicles();
  if (tab === "musteri-stok") loadStock();
  if (tab === "yalcin") loadYalcin();
  if (tab === "musteriler") loadCustomers();
  if (tab === "talepler") loadRequests();
  if (tab === "sefer-plani") loadPlans();
  if (tab === "sayimlar") loadCounts();
  if (tab === "barkod") loadLabelTemplates();
  if (tab === "yonetim") loadAdmin();
}

// Filling: codes become full in the depot with a lot number; the list shows past fills.
const fillCodes = () => [...new Set($("dl-codes").value.split(/[\s,;]+/).map((c) => c.trim()).filter(Boolean))];
$("dl-codes").addEventListener("input", () => { const n = fillCodes().length; $("dl-count").textContent = n ? `${n} tüp` : ""; });
$("dl-save").addEventListener("click", async () => {
  const codes = fillCodes();
  if (!codes.length) { show($("dl-msg"), "En az bir barkod veya seri no yazın."); return; }
  $("dl-save").disabled = true;
  try {
    const result = await api("/panel/api/dolum", { method: "POST", body: { codes, lot: $("dl-lot").value, note: $("dl-note").value, allowOverdueTest: $("dl-overdue").checked } });
    const failed = result.lines.filter((l) => !l.ok);
    const lines = [result.message, ...failed.map((l) => l.message)];
    if (result.needsTestConfirmation.length) lines.push("Testi geçmiş tüpleri yine de doldurmak için onay kutusunu işaretleyip yeniden kaydedin.");
    show($("dl-msg"), lines.join("\n"), failed.length ? "warn" : "ok");
    $("dl-codes").value = failed.map((l) => l.code).join("\n");
    $("dl-count").textContent = failed.length ? `${failed.length} tüp` : "";
    $("dl-overdue").checked = false;
    loadFills();
  } catch (error) {
    show($("dl-msg"), error.message);
  } finally {
    $("dl-save").disabled = false;
  }
});
async function loadFills() {
  try {
    const rows = await api(`/panel/api/dolumlar?gun=${$("dl-days").value}&q=${encodeURIComponent($("dl-q").value.trim())}`);
    $("dl-list-count").textContent = `${rows.length} dolum kaydı` + (rows.length >= 500 ? " (ilk 500; aramayı daraltın)" : "");
    const head = el("tr", {}, ...["Tarih", "Tüp", "Parti / lot", "Dolduran", "Not", "Test"].map((h) => el("th", {}, h)));
    const body = rows.map((f) => el("tr", { class: "click", onclick: () => openDetail(f.cylinderId) },
      el("td", {}, f.filledAt), el("td", {}, el("b", {}, f.serialNumber), el("div", { class: "muted" }, f.gasType)),
      el("td", {}, f.lotNumber || "-"), el("td", {}, f.filledBy), el("td", { class: "muted" }, f.note || ""),
      el("td", { class: f.testOverdue ? "warn-text" : "muted" }, f.testOverdue ? "⚠ testi geçmişti, onayla" : "uygun")));
    $("dl-rows").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Bu dönemde dolum kaydı yok."))])));
  } catch (error) {
    show($("global-msg"), error.message);
  }
}
$("dl-list").addEventListener("click", loadFills);
$("dl-days").addEventListener("change", loadFills);

async function loadOptions() {
  options = await api("/panel/api/secenekler");
  for (const id of ["f-durum", "h-durum"]) {
    const select = $(id);
    select.length = 1;
    for (const s of options.statuses) select.append(el("option", { value: s.id }, s.name));
  }
  const status = $("m-status");
  status.replaceChildren(el("option", { value: "" }, "Seçin"), ...options.statuses.map((s) => el("option", { value: s.id }, s.name)));
  $("f-plaka").length = 1;
  for (const p of options.plates) $("f-plaka").append(el("option", { value: p }, p));
  $("gas-list").replaceChildren(...options.gasTypes.map((g) => el("option", { value: g })));
  $("plate-list").replaceChildren(...options.plates.map((p) => el("option", { value: p })));
}

function customerLabel(c) {
  return `${c.name}${c.city ? " · " + c.city : ""}${c.cylinders ? ` (${c.cylinders} tüp)` : ""}${c.fromCrm ? " · CRM" : ""}`;
}

// Search box + select pair; the list comes from ERP customers and CRM companies.
function customerPicker(inputId, selectId, emptyLabel) {
  let timer = 0;
  let items = [];
  let active = -1;
  const select = $(selectId);
  const input = $(inputId);
  if (!input || !select) {
    return { search: async () => {}, reset() {}, query: () => "", choose() {} };
  }

  // Profesyonel açılır liste: input altında sonuçlar, tıklayınca seçilir.
  select.classList.add("ac-chosen");
  let wrap = input.closest(".ac-wrap");
  if (!wrap) {
    wrap = el("div", { class: "ac-wrap" });
    input.parentNode.insertBefore(wrap, input);
    wrap.append(input);
  }
  let list = wrap.querySelector(".ac-list");
  if (!list) {
    list = el("ul", { class: "ac-list hidden", role: "listbox" });
    wrap.append(list);
  }

  function hideList() { list.classList.add("hidden"); active = -1; }
  function showList() { list.classList.remove("hidden"); }

  function paintList() {
    if (!items.length) {
      list.replaceChildren(el("li", { class: "ac-empty" }, input.value.trim() ? "Eşleşen müşteri yok." : "Yazmaya başlayın…"));
      showList();
      return;
    }
    list.replaceChildren(...items.map((c, i) => el("li", {
      class: i === active ? "active" : "",
      role: "option",
      onclick: (e) => { e.preventDefault(); pick(c); }
    },
      el("div", { class: "ac-name" }, c.name),
      el("div", { class: "ac-meta" }, [c.code, c.city, c.cylinders ? c.cylinders + " tüp" : "", c.fromCrm ? "CRM" : ""].filter(Boolean).join(" · "))
    )));
    showList();
  }

  function syncSelect(c) {
    const keepEmpty = emptyLabel ? [el("option", { value: "" }, emptyLabel)] : [];
    select.replaceChildren(...keepEmpty, el("option", { value: c.id }, customerLabel(c)));
    select.value = String(c.id);
  }

  function pick(c) {
    input.value = c.name;
    syncSelect(c);
    hideList();
  }

  async function search() {
    const q = input.value.trim();
    try {
      const rows = await api("/panel/api/musteriler?hepsi=1&take=40&q=" + encodeURIComponent(q));
      items = rows;
      active = rows.length ? 0 : -1;
      paintList();
      if (rows.length === 1 && q.length >= 2) pick(rows[0]);
    } catch (error) {
      show($("global-msg"), error.message);
    }
  }

  input.addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(search, 120); });
  input.addEventListener("focus", () => { void search(); });
  input.addEventListener("keydown", (e) => {
    if (list.classList.contains("hidden") && (e.key === "ArrowDown" || e.key === "Enter")) { void search(); return; }
    if (e.key === "ArrowDown") { e.preventDefault(); active = Math.min(active + 1, items.length - 1); paintList(); }
    else if (e.key === "ArrowUp") { e.preventDefault(); active = Math.max(active - 1, 0); paintList(); }
    else if (e.key === "Enter" && active >= 0 && items[active]) { e.preventDefault(); pick(items[active]); }
    else if (e.key === "Escape") hideList();
  });
  document.addEventListener("click", (e) => { if (!wrap.contains(e.target)) hideList(); });

  return {
    search,
    reset() {
      input.value = "";
      select.replaceChildren(...(emptyLabel ? [el("option", { value: "" }, emptyLabel)] : []));
      items = [];
      hideList();
    },
    query() { return input.value.trim(); },
    choose(c) { pick(c); }
  };
}

const cylinderCustomer = customerPicker("f-musteri-q", "f-musteri", "Tümü");
const movementCustomer = customerPicker("h-musteri-q", "h-musteri", "Tümü");
const newCustomer = customerPicker("n-musteri-q", "n-musteri", "Depoda (müşteri yok)");
const moveCustomer = customerPicker("m-customer-q", "m-customer", null);

async function loadSummary() {
  try {
    const data = await api("/panel/api/ozet");
    const o = data.overview;
    const total = o.statuses.reduce((sum, s) => sum + s.total, 0);
    const stats = [
      stat(total, "Toplam aktif tüp", `Bugün ${data.todayMoves}, son 7 günde ${data.weekMoves} hareket`, () => openFiltered({})),
    ];
    if (o.longAtCustomer > 0) {
      stats.push(stat(o.longAtCustomer, `${o.longAtCustomerDays}+ gündür müşteride`, "Dönmeyen tüpler", () => openFiltered({ uzun: true }), true));
    }
    for (const s of o.statuses) {
      const split = s.full + s.empty > 0 ? `dolu ${s.full} · boş ${s.empty}` : "dolu/boş bilgisi yok";
      stats.push(stat(s.total, s.name, split, () => openFiltered({ durum: s.status })));
    }
    if (data.testSoon > 0) stats.push(stat(data.testSoon, "Test tarihi 30 gün içinde", "veya geçmiş", () => openFiltered({}), true));
    if (data.inactive > 0) stats.push(stat(data.inactive, "Pasif tüp", "Kullanım dışı kayıtlar", () => openFiltered({ pasif: true })));
    // Manager notice: unscanned trip cylinders whose reasons nobody has checked yet.
    if (can("sefer-kapat") && can("sefer-goruntule")) {
      try {
        const missing = await api("/panel/api/sefer-eksikler?gun=60");
        if (missing.length) {
          const unexplained = missing.filter((m) => !m.reason).length;
          stats.unshift(stat(missing.length, "Sefer eksiği kontrol bekliyor", `${missing.length - unexplained} gerekçeli · ${unexplained} gerekçesiz (Seferler > Kayıp kontrolü)`, () => openTab("seferler"), true));
        }
      } catch { /* the summary still shows without the trip notice */ }
    }
    $("stats").replaceChildren(...stats);

    const own = data.ownership || {};
    const ownership = [];
    ownership.push(stat(own.companyOwned || 0, "Firma malı", "Sahip müşteri atanmamış", () => openFiltered({ sahip: "firma" })));
    ownership.push(stat(own.customerOwned || 0, "Müşteri malı", `${own.ownerCustomerCount || 0} farklı sahip`, () => openFiltered({ sahip: "musteri" })));
    for (const row of own.topOwners || []) {
      ownership.push(stat(row.count, "🏷️ " + row.name, "Müşteri malı tüpler", () => openFiltered({ sahipId: row.id, sahipLabel: row.name })));
    }
    if (own.otherOwners > 0) {
      ownership.push(stat(own.otherOwners, "Diğer sahipler", "Listedeki ilk 12 dışı", () => openFiltered({ sahip: "musteri" })));
    }
    $("ownership").replaceChildren(...(ownership.length ? ownership : [el("div", { class: "muted" }, "Sahiplik özeti yok.")]));

    $("vehicles").replaceChildren(...(o.vehicles.length === 0
      ? [el("div", { class: "muted" }, "Araçta tüp yok.")]
      : o.vehicles.map((v) => stat(v.count, "🚚 " + v.plate, "Araçtaki tüpler", () => openFiltered({ durum: 2, plaka: v.plate })))));
    renderMovements($("recent"), data.recent, "Henüz hareket yok.");
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

function stat(count, label, sub, onclick, warn = false) {
  return el("div", { class: "stat" + (warn ? " warn" : ""), onclick }, el("div", { class: "n" }, count), el("div", { class: "l" }, label), el("div", { class: "s" }, sub));
}

function openFiltered(filter) {
  $("f-q").value = "";
  $("f-durum").value = filter.durum ?? "";
  $("f-plaka").value = filter.plaka ?? "";
  cylinderCustomer.reset();
  if (filter.customer) cylinderCustomer.choose(filter.customer);
  $("f-doluluk").value = "";
  $("f-uzun").checked = !!filter.uzun;
  $("f-pasif").checked = !!filter.pasif;
  setOwnerFilter(filter.sahip || "", filter.sahipId || "", filter.sahipLabel || "");
  openTab("tupler");
}

function setOwnerFilter(sahip, sahipId, label) {
  const select = $("f-sahip");
  // Keep static options; inject a one-off owner option when coming from the homepage tile.
  [...select.querySelectorAll("option[data-owner]")].forEach((o) => o.remove());
  if (sahipId) {
    const opt = el("option", { value: "id:" + sahipId, "data-owner": "1" }, label ? ("Sahip: " + label) : ("Sahip #" + sahipId));
    select.appendChild(opt);
    select.value = "id:" + sahipId;
  } else {
    select.value = sahip || "";
  }
}

$("f-ara").addEventListener("click", loadCylinders);
$("f-q").addEventListener("keydown", (e) => { if (e.key === "Enter") loadCylinders(); });
liveFilter(["f-q", "f-musteri-q"], ["f-durum", "f-plaka", "f-musteri", "f-sahip", "f-doluluk", "f-uzun", "f-pasif"], loadCylinders);

async function loadCylinders() {
  const query = new URLSearchParams();
  if ($("f-q").value.trim()) query.set("q", $("f-q").value.trim());
  if ($("f-durum").value) query.set("durum", $("f-durum").value);
  if ($("f-plaka").value) query.set("plaka", $("f-plaka").value);
  if ($("f-musteri").value) query.set("musteri", $("f-musteri").value);
  if ($("f-doluluk").value) query.set("doluluk", $("f-doluluk").value);
  if ($("f-uzun").checked) query.set("uzun", "true");
  if ($("f-pasif").checked) query.set("pasif", "true");
  const sahip = $("f-sahip").value;
  if (sahip.startsWith("id:")) query.set("sahipId", sahip.slice(3));
  else if (sahip) query.set("sahip", sahip);
  try {
    const rows = await api("/panel/api/tupler?" + query);
    $("cyl-count").textContent = `${rows.length} tüp`;
    const head = el("tr", {}, ...["Seri no", "Gaz", "Kapasite", "Durum", "Dolu/Boş", "Müşteri / Plaka", "Son hareket", "Test"].map((h) => el("th", {}, h)));
    const body = rows.map((r) => el("tr", { class: "click", onclick: () => openDetail(r.id) },
      el("td", {}, el("b", {}, r.serialNumber), r.barcode !== r.serialNumber ? el("div", { class: "muted" }, r.barcode) : null),
      el("td", {}, r.gasType),
      el("td", {}, r.capacity),
      el("td", {}, badge(r.status, r.statusName)),
      el("td", {}, r.fullness),
      el("td", {}, r.customer || r.plate || "-",
        r.owner ? el("div", { class: "muted" }, "Sahip: " + r.owner) : null),
      el("td", {}, r.lastMovement || "-", r.daysThere !== null && r.status === 3
        ? el("div", { class: r.daysThere >= options.longDays ? "warn-text" : "muted" }, `${r.daysThere} gündür müşteride`) : null),
      el("td", { class: r.testOverdue ? "bad-text" : "" }, r.testDue || "-")));
    $("cylinders").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 8, class: "muted" }, "Bu filtreye uyan tüp yok."))])));
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

function movementFilter() {
  const query = new URLSearchParams();
  for (const [key, id] of [["bas", "h-bas"], ["bit", "h-bit"], ["durum", "h-durum"], ["musteri", "h-musteri"], ["q", "h-q"]]) {
    const value = $(id).value.trim();
    if (value) query.set(key, value);
  }
  return query;
}

$("h-ara").addEventListener("click", loadMovements);
$("h-q").addEventListener("keydown", (e) => { if (e.key === "Enter") loadMovements(); });
liveFilter(["h-q", "h-musteri-q"], ["h-musteri", "h-durum", "h-bas", "h-bit"], loadMovements);
$("h-csv").addEventListener("click", (e) => {
  e.preventDefault();
  window.location.href = "/panel/api/hareketler.csv?" + movementFilter();
});

async function loadMovements() {
  if (!$("h-bas").value) {
    const from = new Date(Date.now() - 30 * 86400000);
    $("h-bas").value = from.toISOString().slice(0, 10);
    $("h-bit").value = new Date().toISOString().slice(0, 10);
  }
  try {
    const data = await api("/panel/api/hareketler?" + movementFilter());
    $("mov-count").textContent = data.truncated
      ? `İlk ${data.limit} hareket gösteriliyor; tamamı için tarih aralığını daraltın veya Excel'e aktarın.`
      : `${data.rows.length} hareket`;
    renderMovements($("movements"), data.rows, "Bu aralıkta hareket yok.");
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

function renderMovements(table, rows, empty) {
  const head = el("tr", {}, ...["Tarih", "Tüp", "Hareket", "Durum", "Müşteri / Plaka", "Dolu/Boş", "Not"].map((h) => el("th", {}, h)));
  const body = rows.map((m) => el("tr", { class: "click", onclick: () => openDetail(m.cylinderId) },
    el("td", {}, m.date),
    el("td", {}, el("b", {}, m.serial), el("div", { class: "muted" }, m.gasType)),
    el("td", {}, m.movementType),
    el("td", {}, `${m.from} → ${m.to}`),
    el("td", {}, m.customer || m.plate || "-"),
    el("td", {}, m.fullness),
    el("td", { class: m.warning ? "warn-text" : "muted" }, m.warning ? "⚠ " + (m.note || "") : (m.note || ""))));
  table.replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 7, class: "muted" }, empty))])));
}

async function openDetail(id) {
  hide($("d-msg"));
  try {
    current = await api("/panel/api/tup/" + id);
  } catch (error) {
    show($("global-msg"), error.message);
    return;
  }
  const c = current;
  $("d-title").textContent = `${c.serialNumber} · ${c.gasType}`;
  const info = [
    ["Seri no", c.serialNumber], ["Barkod", c.barcode], ["Gaz", c.gasType], ["Kapasite", `${c.capacity} ${c.capacityUnit}`],
    ["Durum", c.statusName], ["Dolu/Boş", c.fullness], ["Müşteri", c.customer || "-"], ["Plaka", c.plate || "-"],
    ["Son hareket", c.lastMovement ? `${c.lastMovement}${c.daysThere !== null ? ` (${c.daysThere} gün önce)` : ""}` : "-"],
    ["Test tarihi", c.testDue ? c.testDue.split("-").reverse().join(".") : "-"], ["Kayıt", c.isActive ? `Aktif · ${c.created}` : "Pasif"]
  ];
  if (c.owner) info.push(["Tüpün sahibi", "Müşteri malı · " + c.owner]);
  $("d-info").replaceChildren(...info.map(([k, v]) => el("div", {}, el("span", {}, k), v)));
  $("e-gas").value = c.gasType;
  $("e-cap").value = String(c.capacity).replace(".", ",");
  $("e-unit").value = c.capacityUnit;
  $("e-test").value = c.testDue || "";
  $("e-active").value = String(c.isActive);
  $("m-status").value = "";
  $("m-note").value = "";
  $("m-full").value = "";
  $("m-plate").value = "";
  moveCustomer.reset();
  updateMoveFields();
  const head = el("tr", {}, ...["Tarih", "Hareket", "Durum", "Müşteri / Plaka", "Dolu/Boş", "Not"].map((h) => el("th", {}, h)));
  const body = c.movements.map((m) => el("tr", {},
    el("td", {}, m.date), el("td", {}, m.movementType), el("td", {}, `${m.from} → ${m.to}`),
    el("td", {}, m.customer || m.plate || "-"), el("td", {}, m.fullness), el("td", { class: "muted" }, m.note || "")));
  $("d-history").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Hareket yok."))])));
  if (!$("detail").open) $("detail").showModal();
}

$("d-close").addEventListener("click", () => $("detail").close());
$("m-status").addEventListener("change", updateMoveFields);

function updateMoveFields() {
  const status = Number($("m-status").value);
  $("m-customer-wrap").classList.toggle("hidden", status !== 3);
  if (status === 3 && !$("m-customer").options.length) moveCustomer.search();
  $("m-plate-wrap").classList.toggle("hidden", status !== 2);
}

$("m-save").addEventListener("click", () => saveMove(false));

async function saveMove(confirm) {
  const status = Number($("m-status").value);
  if (!status) { show($("d-msg"), "Yeni durumu seçin."); return; }
  const customerId = status === 3 ? Number($("m-customer").value) || null : null;
  if (status === 3 && !customerId) { show($("d-msg"), "Müşteriye teslim için müşteri arayıp seçin."); return; }
  const full = $("m-full").value;
  const body = {
    status, customerId, confirm,
    plate: status === 2 ? $("m-plate").value.trim() || null : null,
    isFull: full === "" ? null : full === "true",
    note: $("m-note").value.trim() || null
  };
  $("m-save").disabled = true;
  try {
    const result = await api(`/panel/api/tup/${current.id}/hareket`, { method: "POST", body });
    await openDetail(current.id);
    show($("d-msg"), result.message, result.message.includes("⚠") ? "warn" : "ok");
  } catch (error) {
    if (error.status === 409 && error.data && error.data.stale) {
      await openDetail(current.id);
      show($("d-msg"), error.message, "warn");
    } else if (error.status === 409 && !confirm) {
      if (window.confirm(error.message + "\n\nYine de kaydedilsin mi?")) await saveMove(true);
      else show($("d-msg"), "Hareket kaydedilmedi.", "warn");
    } else {
      show($("d-msg"), error.message);
    }
  } finally {
    $("m-save").disabled = false;
  }
}

$("e-save").addEventListener("click", async () => {
  const capacity = Number($("e-cap").value.replace(",", "."));
  const active = $("e-active").value === "true";
  if (!active && current.isActive && !window.confirm("Tüp pasife alınsın mı? Listelerde ve sayımlarda görünmez (silinmez, geri alınabilir).")) return;
  $("e-save").disabled = true;
  try {
    const result = await api(`/panel/api/tup/${current.id}/duzenle`, {
      method: "POST",
      body: { gasType: $("e-gas").value.trim(), capacity, capacityUnit: $("e-unit").value, testDueDate: $("e-test").value || null, isActive: active, surum: current.version || null }
    });
    await openDetail(current.id);
    show($("d-msg"), result.message, "ok");
  } catch (error) {
    if (error.status === 409 && error.data && error.data.stale) {
      await openDetail(current.id);
      show($("d-msg"), error.message + " Güncel bilgiler yüklendi.", "warn");
    } else {
      show($("d-msg"), error.message);
    }
  } finally {
    $("e-save").disabled = false;
  }
});

$("new-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const capacity = Number($("n-cap").value.replace(",", "."));
  const full = $("n-full").value;
  try {
    const result = await api("/panel/api/tup", {
      method: "POST",
      body: {
        serialNumber: $("n-serial").value.trim(), barcode: $("n-barcode").value.trim() || null, gasType: $("n-gas").value.trim(),
        capacity, capacityUnit: $("n-unit").value, isFull: full === "" ? null : full === "true", testDueDate: $("n-test").value || null,
        customerId: Number($("n-musteri").value) || null
      }
    });
    show($("new-msg"), result.message, "ok");
    $("new-form").reset();
    newCustomer.reset();
    newCustomer.search();
    await loadOptions();
  } catch (error) {
    show($("new-msg"), error.message);
  }
});

// ---- New customer dialog -------------------------------------------------------------------------

let customerDone = null;
let customerDuplicate = null;
const customerFields = { name: "cu-name", contactName: "cu-contact", phone: "cu-phone", email: "cu-email", city: "cu-city", district: "cu-district", address: "cu-address", taxOffice: "cu-taxoffice", taxNumber: "cu-taxno" };

// onDone(customer, message) runs after a customer is created or an existing duplicate is chosen.
function openCustomerDialog(prefill, onDone) {
  customerDone = onDone;
  customerDuplicate = null;
  $("customer-form").reset();
  $("cu-name").value = prefill || "";
  hide($("cu-msg"));
  $("cu-dup").classList.add("hidden");
  $("customer-dialog").showModal();
  $("cu-name").focus();
}

async function saveCustomer(allowDuplicate) {
  const body = { allowDuplicate };
  for (const [key, id] of Object.entries(customerFields)) body[key] = $(id).value.trim() || null;
  if (!body.name) { show($("cu-msg"), "Müşteri adı zorunludur."); return; }
  $("cu-save").disabled = true;
  try {
    const result = await api("/panel/api/musteri", { method: "POST", body });
    $("customer-dialog").close();
    if (customerDone) customerDone(result.customer, result.message);
  } catch (error) {
    if (error.status === 409 && error.data && error.data.duplicate) {
      customerDuplicate = error.data.existing;
      show($("cu-msg"), error.message, "warn");
      $("cu-dup").classList.remove("hidden");
    } else {
      show($("cu-msg"), error.message);
    }
  } finally {
    $("cu-save").disabled = false;
  }
}

$("customer-form").addEventListener("submit", (event) => { event.preventDefault(); saveCustomer(false); });
$("cu-force").addEventListener("click", () => saveCustomer(true));
$("cu-close").addEventListener("click", () => $("customer-dialog").close());
$("cu-use").addEventListener("click", () => {
  const existing = customerDuplicate;
  $("customer-dialog").close();
  if (existing && customerDone) customerDone(existing, `Mevcut kayıt seçildi: ${existing.name}.`);
});

$("n-musteri-yeni").addEventListener("click", () => openCustomerDialog(newCustomer.query(), (c, message) => {
  newCustomer.choose(c);
  show($("new-msg"), message + " Tüp kaydında bu müşteri seçili.", "ok");
}));

$("m-customer-yeni").addEventListener("click", () => openCustomerDialog(moveCustomer.query(), (c, message) => {
  moveCustomer.choose(c);
  show($("d-msg"), message + " Hareket için seçildi; kaydetmek için \"Hareketi kaydet\"e basın.", "ok");
}));

$("c-yeni").addEventListener("click", () => openCustomerDialog($("c-q").value.trim(), async (c, message) => {
  $("c-q").value = c.name;
  await loadCustomers();
  show($("global-msg"), message, "ok");
}));

// ---- Trips tab -----------------------------------------------------------------------------------

function gasText(list) { return list.map((g) => `${g.gasType} ×${g.count}`).join(", "); }

function tripStatus(t) {
  if (t.missing > 0) return el("span", { class: "badge trip-alarm" }, `${t.missing} eksik`);
  return t.active ? el("span", { class: "badge trip-active" }, "Seferde") : el("span", { class: "badge trip-closed" }, "Kapandı");
}

$("s-yenile").addEventListener("click", loadTrips);
$("s-durum").addEventListener("change", loadTrips);

async function loadTrips() {
  try {
    const [active, rows] = await Promise.all([
      api("/panel/api/seferler?durum=aktif"),
      api("/panel/api/seferler" + ($("s-durum").value ? "?durum=" + $("s-durum").value : ""))
    ]);
    const cards = [];
    for (const t of active) {
      for (const v of t.vehicles) {
        const sub = v.onTrip
          ? `${t.title} · araçta ${v.onVehicle}, teslim ${v.delivered}, dönen ${v.returnedCount}`
          : `Döndü ${v.returned || ""} · teslim ${v.delivered}, dönen ${v.returnedCount}` + (v.missing ? ` · ${v.missing} eksik` : "");
        cards.push(stat(v.loaded, (v.onTrip ? "🚚 " : "🏁 ") + v.plate, sub, () => openTrip(t.id), false));
        if (v.missing) cards[cards.length - 1].classList.add("alarm-stat");
      }
    }
    $("s-active").replaceChildren(...(cards.length ? cards : [el("div", { class: "muted" }, "Şu an aktif sefer yok.")]));

    const alarms = active.filter((t) => t.missing > 0).map((t) => `${t.title}: ${t.missing} tüp okutulmadı (${gasText(t.missingByGas)})`
      + (t.missingUnexplained ? ` · ${t.missingUnexplained} gerekçesiz` : ""));
    if (alarms.length) { $("s-alarm").textContent = "⚠ Dönen araçlarda eksik okutma var:\n" + alarms.join("\n"); $("s-alarm").classList.remove("hidden"); }
    else $("s-alarm").classList.add("hidden");

    $("s-count").textContent = `${rows.length} sefer` + (rows.length >= 100 ? " (son 100)" : "");
    const head = el("tr", {}, ...["Sefer", "Başlangıç / bitiş", "Araçlar", "Dolu yüklenen", "Teslim", "Depoya dönen", "Araçta", "Durum", "Eksik (gaz türü)"].map((h) => el("th", {}, h)));
    const body = rows.map((t) => el("tr", { class: "click" + (t.missing ? " alarm-row" : ""), onclick: () => openTrip(t.id) },
      el("td", {}, el("b", {}, t.title), t.note ? el("div", { class: "muted" }, t.note) : null),
      el("td", {}, t.started, el("div", { class: "muted" }, t.closed ? "Kapandı " + t.closed : "Açık")),
      el("td", {}, el("div", { class: "trip-vehicles" }, ...t.vehicles.map((v) => el("span", { class: "badge" + (v.missing ? " trip-alarm" : v.onTrip ? " trip-active" : "") }, v.plate)))),
      el("td", {}, String(t.loaded), t.pickedUp ? el("div", { class: "muted" }, `+${t.pickedUp} boş alındı`) : null),
      el("td", {}, String(t.delivered)),
      el("td", {}, String(t.returned)),
      el("td", {}, String(t.onVehicle)),
      el("td", {}, tripStatus(t)),
      el("td", { class: t.missing ? "alarm" : "muted" }, t.missing ? gasText(t.missingByGas) : "-",
        t.missing ? el("div", { class: t.missingUnexplained ? "alarm" : "muted" },
          t.missingUnexplained ? `${t.missingUnexplained} gerekçesiz` : t.missingUnchecked ? `${t.missingUnchecked} kontrol bekliyor` : "Gerekçeli, kontrol edildi") : null)));
    $("trips").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 9, class: "muted" }, "Kayıtlı sefer yok."))])));
  } catch (error) {
    show($("global-msg"), error.message);
  }
  loadMissingReport();
}

// ---- Loss control: unscanned cylinders, their reasons and the manager check ------------------------

let missingReasons = null;
// Serial numbers the server refused to close the trip for; shown in the reason card even while the vehicle is on the road.
let closeBlocked = new Set();

async function ensureReasons() {
  if (!missingReasons) missingReasons = await api("/panel/api/sefer-gerekceler");
  if (!$("t-reason").options.length) {
    $("t-reason").replaceChildren(el("option", { value: "" }, "Nerede / neden okutulmadı?"),
      ...missingReasons.map((r) => el("option", { value: r.key }, r.label + (r.asksCustomer ? " (müşteri adı gerekli)" : r.noteRequired ? " (açıklama gerekli)" : ""))));
  }
}

function pickupText(c) {
  return c.isPickup ? "Müşteriden boş alındı" + (c.pickupCustomer ? ` (${c.pickupCustomer})` : "") : "Depodan dolu yüklendi";
}

$("t-reason").addEventListener("change", () => {
  const reason = (missingReasons || []).find((r) => r.key === $("t-reason").value);
  $("t-reason-note").placeholder = reason && reason.asksCustomer ? "Müşteri adı (ve varsa ayrıntı)" : "Araç, yer veya ayrıntı";
});

function reasonCell(c) {
  if (!c.reason) return el("td", { class: "alarm" }, "Gerekçe yok");
  return el("td", {}, el("b", {}, c.reasonLabel), c.note ? el("div", {}, c.note) : null,
    el("div", { class: "muted" }, [c.reasonBy, c.reasonAt].filter(Boolean).join(" · ")));
}

function checkCell(c) {
  if (c.checkedAt) return el("td", {}, "✔ " + c.checkedAt, el("div", { class: "muted" }, c.checkedBy || ""));
  return el("td", { class: c.reason ? "" : "muted" }, c.reason ? "Kontrol bekliyor" : "-");
}

$("sm-yenile").addEventListener("click", loadMissingReport);
$("sm-gun").addEventListener("change", loadMissingReport);
$("sm-tumu").addEventListener("change", loadMissingReport);

async function loadMissingReport() {
  try {
    const rows = await api(`/panel/api/sefer-eksikler?gun=${$("sm-gun").value}` + ($("sm-tumu").checked ? "&tumu=true" : ""));
    const unexplained = rows.filter((r) => !r.reason).length;
    const waiting = rows.filter((r) => r.reason && !r.checkedAt).length;
    $("sm-count").textContent = rows.length
      ? `${rows.length} tüp · ${unexplained} gerekçesiz · ${waiting} kontrol bekliyor`
      : "Bu aralıkta açıklama veya kontrol bekleyen okutulmamış tüp yok.";
    const head = el("tr", {}, ...["Seri no", "Gaz", "Sefer / plaka", "Gerekçe", "Kontrol", "Şu an"].map((h) => el("th", {}, h)));
    const body = rows.map((r) => el("tr", { class: "click" + (r.reason ? "" : " alarm-row"), onclick: () => openTrip(r.tripId) },
      el("td", {}, el("b", {}, r.serialNumber)),
      el("td", {}, r.gasType),
      el("td", {}, r.tripTitle, el("div", { class: "muted" }, `${r.plate} · ${r.tripActive ? "açık" : "kapandı"} · ${r.isPickup ? "müşteriden boş alındı" : "dolu yüklendi"}`)),
      reasonCell(r),
      checkCell(r),
      el("td", {}, r.currentStatus + (r.currentCustomer ? " · " + r.currentCustomer : r.currentPlate ? " · " + r.currentPlate : ""),
        r.seenAfterTrip ? el("div", { class: "muted" }, "Sonradan okutuldu " + (r.lastMovement || "")) : null)));
    $("sm-table").replaceChildren(el("thead", {}, head), el("tbody", {}, ...body));
  } catch (error) {
    $("sm-count").textContent = "Kayıp kontrol listesi alınamadı: " + error.message;
  }
}

function renderTripMissing(cylinders) {
  const rows = cylinders.filter((c) => !c.delivered && !c.returned && (c.missing || c.reason || closeBlocked.has(c.serialNumber)));
  $("t-missing-card").classList.toggle("hidden", rows.length === 0);
  if (!rows.length) return;
  ensureReasons().catch((error) => show($("t-msg"), error.message));
  const all = el("input", { type: "checkbox", title: "Tümünü seç" });
  all.addEventListener("change", () => { for (const box of $("t-missing").querySelectorAll("tbody input")) box.checked = all.checked; });
  const head = el("tr", {}, el("th", {}, all), ...["Seri no", "Gaz", "Plaka", "Nereden", "Gerekçe", "Kontrol"].map((h) => el("th", {}, h)));
  const body = rows.map((c) => el("tr", { class: c.reason ? "" : "alarm-row" },
    el("td", {}, el("input", { type: "checkbox", "data-id": c.cylinderId })),
    el("td", {}, el("b", {}, c.serialNumber)), el("td", {}, c.gasType), el("td", {}, c.plate), el("td", {}, pickupText(c)),
    reasonCell(c), checkCell(c)));
  $("t-missing").replaceChildren(el("thead", {}, head), el("tbody", {}, ...body));
}

function selectedMissing() {
  return [...$("t-missing").querySelectorAll("tbody input:checked")].map((box) => Number(box.dataset.id));
}

async function submitMissing(path, body, button) {
  const cylinderIds = selectedMissing();
  if (!cylinderIds.length) { show($("t-msg"), "Önce listeden tüp seçin."); return false; }
  button.disabled = true;
  try {
    const result = await api(`/panel/api/sefer/${currentTrip.id}/${path}`, { method: "POST", body: { cylinderIds, ...body } });
    await openTrip(currentTrip.id, true);
    show($("t-msg"), result.message, result.messages && result.messages.length ? "warn" : "ok");
    loadTrips();
    return true;
  } catch (error) {
    show($("t-msg"), error.message);
    return false;
  } finally {
    button.disabled = false;
  }
}

$("t-reason-save").addEventListener("click", () => {
  const reason = $("t-reason").value;
  if (!reason) { show($("t-msg"), "Bir gerekçe seçin."); return; }
  submitMissing("gerekce", { reason, note: $("t-reason-note").value.trim() || null }, $("t-reason-save"))
    .then((ok) => { if (ok) $("t-reason-note").value = ""; });
});
$("t-reason-check").addEventListener("click", () => submitMissing("gerekce-kontrol", { undo: false }, $("t-reason-check")));
$("t-reason-uncheck").addEventListener("click", () => submitMissing("gerekce-kontrol", { undo: true }, $("t-reason-uncheck")));

let currentTrip = null;

async function openTrip(id, keepMessage = false) {
  let data;
  try {
    data = await api("/panel/api/sefer/" + id);
  } catch (error) {
    show($("global-msg"), error.message);
    return;
  }
  const t = data.trip;
  const sameTrip = currentTrip && currentTrip.id === t.id;
  if (!sameTrip) closeBlocked = new Set();
  currentTrip = t;
  if (!keepMessage) hide($("t-msg"));
  $("t-load-card").classList.toggle("hidden", !(t.active && can("sefer-yukle|sefer-kapat")));
  const plates = t.vehicles.map((v) => v.plate);
  const loadPlate = sameTrip ? $("t-load-plate").value : "";
  $("t-load-plate").replaceChildren(...t.vehicles.filter((v) => v.onTrip).map((v) => el("option", { value: v.plate }, v.plate)));
  if (loadPlate && plates.includes(loadPlate)) $("t-load-plate").value = loadPlate;
  if (!sameTrip) $("t-load-codes").value = "";
  updateLoadCount();
  const groupPlate = sameTrip ? $("t-group-plate").value : "";
  $("t-group-plate").replaceChildren(el("option", { value: "" }, "Tüm araçlar"), ...plates.map((p) => el("option", { value: p }, p)));
  $("t-group-plate").value = plates.includes(groupPlate) ? groupPlate : "";
  loadTripGroups();
  $("t-title").textContent = `${t.title} · #${t.id}`;
  const alarms = [];
  if (t.missing) alarms.push(`⚠ ${t.missing} tüp okutulmadı (müşteriye teslim veya depoya dönüş yok): ${gasText(t.missingByGas)}`);
  if (t.missingUnexplained) alarms.push(`⚠ ${t.missingUnexplained} okutulmayan tüpün gerekçesi seçilmemiş` + (t.active && t.requireReasons ? "; gerekçeler seçilmeden sefer kapatılamaz." : "."));
  if (t.missingUnchecked) alarms.push(`${t.missingUnchecked} gerekçe yönetici kontrolü bekliyor.`);
  if (data.unexpected.length) alarms.push(`⚠ ${data.unexpected.length} tüp bu sefere yüklenmediği hâlde dönüşte okutuldu.`);
  $("t-alarm").textContent = alarms.join("\n");
  $("t-alarm").classList.toggle("hidden", alarms.length === 0);
  const info = [
    ["Durum", t.active ? "Seferde (açık)" : "Kapandı"], ["Başlangıç", t.started], ["Bitiş", t.closed || "-"],
    ["Açan", t.createdBy || "-"], ["Not", t.note || "-"],
    ["Dolu yüklenen", t.loaded], ["Müşteriden boş alınan", t.pickedUp || 0], ["Müşteriye teslim", t.delivered], ["Depoya dönen", t.returned], ["Araçta (yolda)", t.onVehicle], ["Eksik", t.missing]
  ];
  $("t-info").replaceChildren(...info.map(([k, v]) => el("div", {}, el("span", {}, k), String(v))));

  const vHead = el("tr", {}, ...["Plaka", "Durum", "Yüklenen", "Teslim", "Depoya dönen", "Araçta", "Eksik"].map((h) => el("th", {}, h)));
  const vBody = t.vehicles.map((v) => el("tr", { class: v.missing ? "alarm-row" : "" },
    el("td", {}, el("b", {}, v.plate)),
    el("td", {}, v.onTrip ? "Seferde" : "Döndü " + (v.returned || "")),
    el("td", {}, String(v.loaded)), el("td", {}, String(v.delivered)), el("td", {}, String(v.returnedCount)),
    el("td", {}, String(v.onVehicle)), el("td", { class: v.missing ? "alarm" : "" }, String(v.missing))));
  $("t-vehicles").replaceChildren(el("thead", {}, vHead), el("tbody", {}, ...(vBody.length ? vBody : [el("tr", {}, el("td", { colspan: 7, class: "muted" }, "Araç atanmamış."))])));

  const cHead = el("tr", {}, ...["Seri no", "Gaz", "Plaka", "Yükleme", "Durum"].map((h) => el("th", {}, h)));
  const cBody = data.cylinders.map((c) => el("tr", { class: "click" + (c.missing ? " alarm-row" : ""), onclick: () => openDetail(c.cylinderId) },
    el("td", {}, el("b", {}, c.serialNumber)), el("td", {}, c.gasType), el("td", {}, c.plate),
    el("td", {}, c.loaded, el("div", { class: "muted" }, pickupText(c))),
    el("td", { class: c.missing ? "alarm" : "" },
      c.delivered ? "Müşteriye teslim " + c.delivered
        : c.returned ? `Depoya ${c.isPickup ? "boş" : "dolu"} döndü ` + c.returned
        : c.missing ? "⚠ Eksik: okutulmadı" : "Araçta",
      !c.delivered && !c.returned && c.reasonLabel ? el("div", { class: "muted" }, "Gerekçe: " + c.reasonLabel + (c.checkedAt ? " ✔" : "")) : null)));
  $("t-cylinders").replaceChildren(el("thead", {}, cHead), el("tbody", {}, ...(cBody.length ? cBody : [el("tr", {}, el("td", { colspan: 5, class: "muted" }, "Bu sefere tüp yüklenmemiş."))])));
  renderTripMissing(data.cylinders);

  $("t-unexpected-card").classList.toggle("hidden", data.unexpected.length === 0);
  const uHead = el("tr", {}, ...["Seri no", "Gaz", "Plaka", "Okutma"].map((h) => el("th", {}, h)));
  const uBody = data.unexpected.map((u) => el("tr", { class: "click alarm-row", onclick: () => openDetail(u.cylinderId) },
    el("td", { class: "alarm" }, u.serialNumber), el("td", {}, u.gasType), el("td", {}, u.plate || "-"), el("td", {}, u.at)));
  $("t-unexpected").replaceChildren(el("thead", {}, uHead), el("tbody", {}, ...uBody));
  if (!$("trip-dialog").open) $("trip-dialog").showModal();
}

$("t-close").addEventListener("click", () => $("trip-dialog").close());

// Gas type groups: a clickable header per type with its count; the body lists the barcodes and details.
function renderGroups(container, groups, empty) {
  if (!groups.length) {
    container.replaceChildren(el("div", { class: "muted" }, empty));
    return;
  }
  container.replaceChildren(...groups.map((g) => {
    const body = el("div", { class: "gas-body hidden" });
    const head = el("div", { class: "gas-head", role: "button", tabindex: "0" },
      el("span", { class: "gas-count" }, String(g.count)),
      el("span", { class: "gas-name" }, g.gasType),
      el("span", { class: g.alarm ? "alarm" : "muted" }, g.summary),
      el("span", { class: "muted" }, "▾"));
    const toggle = () => {
      if (!body.childElementCount) body.append(groupTable(g));
      body.classList.toggle("hidden");
    };
    head.addEventListener("click", toggle);
    head.addEventListener("keydown", (e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); toggle(); } });
    return el("div", { class: "gas-group" + (g.alarm ? " alarm-group" : "") }, head, body);
  }));
}

function groupTable(g) {
  const head = el("tr", {}, ...["Barkod / seri no", "Kapasite", "Dolu/Boş", "Durum", "Müşteri / plaka", "Yükleme", "Son hareket", "Test"].map((h) => el("th", {}, h)));
  const rows = g.cylinders.map((c) => el("tr", { class: "click" + (c.alarm ? " alarm-row" : ""), onclick: () => openDetail(c.cylinderId) },
    el("td", {}, el("b", {}, c.barcode), c.barcode !== c.serialNumber ? el("div", { class: "muted" }, c.serialNumber) : null),
    el("td", {}, c.capacity),
    el("td", {}, c.fullness),
    el("td", { class: c.alarm ? "alarm" : "" }, c.state, el("div", { class: "muted" }, c.statusName)),
    el("td", {}, c.customer || c.plate || "-"),
    el("td", {}, c.loaded || "-"),
    el("td", {}, c.lastMovement || "-"),
    el("td", { class: c.testOverdue ? "bad-text" : "" }, c.testDue || "-")));
  return el("div", { class: "table-wrap" }, el("table", {}, el("thead", {}, head), el("tbody", {}, ...rows)));
}

async function loadTripGroups() {
  if (!currentTrip) return;
  const plate = $("t-group-plate").value;
  try {
    const load = await api(`/panel/api/sefer/${currentTrip.id}/yuk` + (plate ? "?plaka=" + encodeURIComponent(plate) : ""));
    renderGroups($("t-groups"), load.groups, plate ? `${plate} aracına bu seferde tüp yüklenmemiş.` : "Bu sefere henüz tüp yüklenmemiş.");
  } catch (error) {
    show($("t-msg"), error.message);
  }
}

$("t-group-plate").addEventListener("change", loadTripGroups);

function scannedCodes() {
  return [...new Set($("t-load-codes").value.split(/[\r\n,;\t]+/).map((c) => c.trim()).filter(Boolean))];
}

function updateLoadCount() {
  const n = scannedCodes().length;
  $("t-load-count").textContent = n ? `${n} kod okutuldu` : "";
}

$("t-load-codes").addEventListener("input", updateLoadCount);

$("t-load-save").addEventListener("click", async () => {
  const plate = $("t-load-plate").value;
  const codes = scannedCodes();
  if (!plate) { show($("t-msg"), "Yükleme için seferde olan bir araç seçin."); return; }
  if (!codes.length) { show($("t-msg"), "En az bir barkod okutun veya yazın."); return; }
  $("t-load-save").disabled = true;
  try {
    const result = await api(`/panel/api/sefer/${currentTrip.id}/yukle`, { method: "POST", body: { plate, codes } });
    $("t-load-codes").value = "";
    show($("t-msg"), result.message, result.skipped ? "warn" : "ok");
    $("t-group-plate").value = plate;
    await openTrip(currentTrip.id, true);
    loadTrips();
  } catch (error) {
    show($("t-msg"), error.message);
  } finally {
    $("t-load-save").disabled = false;
  }
});

$("t-close-trip").addEventListener("click", async () => {
  if (!currentTrip || !window.confirm(`${currentTrip.title} kapatılsın mı? Teslim veya dönüş okutulmamış tüpler eksik olarak raporlanır.`)) return;
  try {
    const result = await api(`/panel/api/sefer/${currentTrip.id}/kapat`, { method: "POST" });
    closeBlocked = new Set();
    await openTrip(currentTrip.id, true);
    show($("t-msg"), result.message, result.hasAlerts ? "warn" : "ok");
    loadTrips();
  } catch (error) {
    if (error.data && error.data.reasonsRequired) {
      // Show every unexplained cylinder in the reason card (also those whose vehicle is still marked on the road).
      closeBlocked = new Set(error.data.missing || []);
      await openTrip(currentTrip.id, true);
      $("t-missing-card").scrollIntoView({ behavior: "smooth", block: "start" });
    }
    show($("t-msg"), error.message);
  }
});

// ---- Trip start ------------------------------------------------------------------------------------

$("s-baslat").addEventListener("click", async () => {
  hide($("ts-msg"));
  $("ts-note").value = "";
  $("ts-vehicles").replaceChildren(el("div", { class: "muted" }, "Araçlar yükleniyor…"));
  $("trip-start-dialog").showModal();
  try {
    const vehicles = await api("/panel/api/araclar");
    if (!vehicles.length) {
      $("ts-vehicles").replaceChildren(el("div", { class: "muted" }, "Kayıtlı aktif araç yok. Önce \"Araç İşlemleri\" sekmesinden araç ekleyin."));
      return;
    }
    $("ts-vehicles").replaceChildren(...vehicles.map((v) => {
      const box = el("input", { type: "checkbox", value: v.plate });
      if (v.activeTripId) box.disabled = true;
      return el("label", { class: v.activeTripId ? "busy" : "" }, box,
        el("span", {}, el("b", {}, v.plate), " ", vehicleCaption(v),
          v.activeTripId ? el("div", { class: "muted" }, `Şu an seferde: ${v.activeTripTitle}`) : v.cylindersOnBoard ? el("div", { class: "muted" }, `Üzerinde ${v.cylindersOnBoard} tüp`) : null));
    }));
  } catch (error) {
    show($("ts-msg"), error.message);
  }
});

$("ts-close").addEventListener("click", () => $("trip-start-dialog").close());

$("ts-save").addEventListener("click", async () => {
  const plates = [...$("ts-vehicles").querySelectorAll("input:checked")].map((b) => b.value);
  if (!plates.length) { show($("ts-msg"), "En az bir araç seçin."); return; }
  $("ts-save").disabled = true;
  try {
    const result = await api("/panel/api/sefer/baslat", { method: "POST", body: { plates, note: $("ts-note").value.trim() || null } });
    $("trip-start-dialog").close();
    await loadTrips();
    await openTrip(result.id);
    show($("t-msg"), result.message + " Şimdi tüpleri okutup \"Araca yükle\"ye basın.", "ok");
    $("t-load-codes").focus();
  } catch (error) {
    show($("ts-msg"), error.message);
  } finally {
    $("ts-save").disabled = false;
  }
});

// ---- Vehicles tab ------------------------------------------------------------------------------------

let currentVehicle = null;
let editingVehicle = null;

function vehicleCaption(v) {
  return [v.name, v.vehicleType, v.driverName ? "şoför " + v.driverName : null].filter(Boolean).join(" · ");
}

$("v-yenile").addEventListener("click", loadVehicles);
$("v-pasif").addEventListener("change", loadVehicles);

async function loadVehicles() {
  try {
    const rows = await api("/panel/api/araclar" + ($("v-pasif").checked ? "?pasif=true" : ""));
    $("v-count").textContent = `${rows.length} araç`;
    const head = el("tr", {}, ...["Plaka", "Ad / tür", "Şoför", "Kapasite", "Araçtaki tüp", "Sefer", "Durum"].map((h) => el("th", {}, h)));
    const body = rows.map((v) => el("tr", { class: "click" + (v.isActive ? "" : " off"), onclick: () => openVehicle(v.id) },
      el("td", {}, el("b", {}, v.plate)),
      el("td", {}, [v.name, v.vehicleType].filter(Boolean).join(" · ") || "-"),
      el("td", {}, v.driverName || "-"),
      el("td", {}, v.cylinderCapacity ? `${v.cylinderCapacity} tüp` : "-"),
      el("td", { class: v.cylinderCapacity && v.cylindersOnBoard > v.cylinderCapacity ? "alarm" : "" }, String(v.cylindersOnBoard)),
      el("td", {}, v.activeTripId ? el("span", { class: "badge trip-active" }, v.activeTripTitle) : "-"),
      el("td", {}, el("span", { class: "dot" + (v.isActive ? " on" : "") }), v.isActive ? "Aktif" : "Pasif")));
    $("vehicle-table").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 7, class: "muted" }, "Kayıtlı araç yok. \"+ Araç ekle\" ile ekleyin."))])));
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

async function openVehicle(id, message) {
  let data;
  try {
    data = await api("/panel/api/arac/" + id);
  } catch (error) {
    show($("global-msg"), error.message);
    return;
  }
  const v = data.vehicle;
  currentVehicle = v;
  if (message) show($("vd-msg"), message, "ok"); else hide($("vd-msg"));
  $("vd-title").textContent = `🚚 ${v.plate}` + (v.name ? ` · ${v.name}` : "");
  $("vd-alarm").textContent = data.capacityWarning || "";
  $("vd-alarm").classList.toggle("hidden", !data.capacityWarning);
  const info = [
    ["Plaka", v.plate], ["Araç adı", v.name || "-"], ["Tür", v.vehicleType || "-"], ["Şoför", v.driverName || "-"],
    ["Kapasite", v.cylinderCapacity ? `${v.cylinderCapacity} tüp` : "-"], ["Araçtaki tüp", v.cylindersOnBoard],
    ["Aktif sefer", v.activeTripTitle || "Yok"], ["Durum", v.isActive ? "Aktif" : "Pasif"], ["Kayıt", v.created], ["Not", v.note || "-"]
  ];
  $("vd-info").replaceChildren(...info.map(([k, value]) => el("div", {}, el("span", {}, k), String(value))));
  $("vd-toggle").textContent = v.isActive ? "Pasife al" : "Aktif et";
  $("vd-toggle").className = v.isActive ? "danger" : "secondary small";
  $("vd-toggle").classList.toggle("hidden", !can("arac-yonetimi"));
  renderGroups($("vd-groups"), data.onBoard, "Bu araçta şu an tüp yok.");
  const head = el("tr", {}, ...["Sefer", "Başlangıç", "Araç dönüşü", "Yüklenen", "Teslim", "Depoya dönen", "Eksik"].map((h) => el("th", {}, h)));
  const body = data.trips.map((t) => el("tr", { class: "click" + (t.missing ? " alarm-row" : ""), onclick: () => openTrip(t.tripId) },
    el("td", {}, el("b", {}, t.title), " ", t.active ? el("span", { class: "badge trip-active" }, "Açık") : el("span", { class: "badge trip-closed" }, "Kapandı")),
    el("td", {}, t.started),
    el("td", {}, t.vehicleReturned || (t.active ? "Seferde" : t.closed || "-")),
    el("td", {}, String(t.loaded)), el("td", {}, String(t.delivered)), el("td", {}, String(t.returned)),
    el("td", { class: t.missing ? "alarm" : "" }, String(t.missing))));
  $("vd-trips").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 7, class: "muted" }, "Bu araç henüz sefere çıkmamış."))])));
  if (!$("vehicle-dialog").open) $("vehicle-dialog").showModal();
}

$("vd-close").addEventListener("click", () => $("vehicle-dialog").close());
$("vd-edit").addEventListener("click", () => openVehicleForm(currentVehicle));
$("v-yeni").addEventListener("click", () => openVehicleForm(null));
$("vf-close").addEventListener("click", () => $("vehicle-form-dialog").close());

function vehicleBody(active) {
  const capacity = $("vf-capacity").value.trim();
  return {
    plate: $("vf-plate").value.trim(), name: $("vf-name").value.trim() || null, vehicleType: $("vf-type").value.trim() || null,
    driverName: $("vf-driver").value.trim() || null, cylinderCapacity: capacity ? Number(capacity) : null,
    note: $("vf-note").value.trim() || null, isActive: active
  };
}

$("vd-toggle").addEventListener("click", async () => {
  const v = currentVehicle;
  if (!v) return;
  if (v.isActive && !window.confirm(`${v.plate} pasife alınsın mı? Yeni seferlerde seçilemez; geçmişi silinmez, tekrar aktif edilebilir.`)) return;
  try {
    const result = await api("/panel/api/arac/" + v.id, {
      method: "POST",
      body: { plate: v.plate, name: v.name, vehicleType: v.vehicleType, driverName: v.driverName, cylinderCapacity: v.cylinderCapacity, note: v.note, isActive: !v.isActive }
    });
    await openVehicle(v.id, result.message);
    loadVehicles();
  } catch (error) {
    show($("vd-msg"), error.message);
  }
});

function openVehicleForm(v) {
  editingVehicle = v;
  $("vehicle-form").reset();
  hide($("vf-msg"));
  $("vf-title").textContent = v ? `Aracı düzenle: ${v.plate}` : "Yeni araç";
  $("vf-plate").value = v ? v.plate : "";
  $("vf-plate").disabled = !!v;
  $("vf-name").value = v?.name || "";
  $("vf-type").value = v?.vehicleType || "";
  $("vf-driver").value = v?.driverName || "";
  $("vf-capacity").value = v?.cylinderCapacity || "";
  $("vf-note").value = v?.note || "";
  $("vf-active").value = String(v ? v.isActive : true);
  $("vf-active-wrap").classList.toggle("hidden", !v);
  $("vehicle-form-dialog").showModal();
  (v ? $("vf-name") : $("vf-plate")).focus();
}

$("vehicle-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const capacity = $("vf-capacity").value.trim();
  if (capacity && !/^\d+$/.test(capacity)) { show($("vf-msg"), "Kapasite tüp adedi olarak tam sayı olmalıdır."); return; }
  const active = editingVehicle ? $("vf-active").value === "true" : true;
  if (editingVehicle && editingVehicle.isActive && !active && !window.confirm(`${editingVehicle.plate} pasife alınsın mı?`)) return;
  $("vf-save").disabled = true;
  try {
    const result = await api("/panel/api/arac" + (editingVehicle ? "/" + editingVehicle.id : ""), { method: "POST", body: vehicleBody(active) });
    $("vehicle-form-dialog").close();
    await loadVehicles();
    if (editingVehicle) await openVehicle(editingVehicle.id, result.message);
    else show($("v-msg"), result.message, "ok");
    await loadOptions();
  } catch (error) {
    show($("vf-msg"), error.message);
  } finally {
    $("vf-save").disabled = false;
  }
});

// ---- Customers tab -------------------------------------------------------------------------------

$("c-ara").addEventListener("click", loadCustomers);
$("c-q").addEventListener("keydown", (e) => { if (e.key === "Enter") loadCustomers(); });
liveFilter(["c-q"], [], loadCustomers);

async function loadCustomers() {
  try {
    const rows = await api("/panel/api/musteriler?take=200&q=" + encodeURIComponent($("c-q").value.trim()));
    $("c-count").textContent = rows.length >= 200 ? "İlk 200 kayıt gösteriliyor; aramayı daraltın." : `${rows.length} kayıt`;
    const head = el("tr", {}, ...["Müşteri", "Kod", "Şehir", "Telefon", "Müşterideki tüp", "Kaynak"].map((h) => el("th", {}, h)));
    const body = rows.map((c) => el("tr", { class: "click", onclick: () => openCustomerCylinders(c) },
      el("td", {}, el("b", {}, c.name)),
      el("td", { class: "muted" }, c.code),
      el("td", {}, c.city || "-"),
      el("td", {}, c.phone || "-"),
      el("td", {}, String(c.cylinders)),
      el("td", {}, c.fromCrm ? el("span", { class: "badge crm" }, "CRM") : el("span", { class: "badge" }, "ERP"))));
    $("customers").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Eşleşen müşteri yok. Yeni kayıt açmak için \"+ Yeni müşteri\"ye basın."))])));
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

function openCustomerCylinders(c) {
  if (can("tup-goruntule")) openStockDialog(c.id);
  else openFiltered({ customer: c });
}

// ---- Customer stock --------------------------------------------------------------------------------

let stockCustomer = null;
let stockPick = null;
const daysText = (days) => (days === null || days === undefined ? "-" : `${days} gün`);
const fullEmpty = (full, empty) => `dolu ${full} · boş ${empty}`;

function stockFilter() {
  const query = new URLSearchParams();
  if ($("ms-q").value.trim()) query.set("q", $("ms-q").value.trim());
  if ($("ms-gaz").value.trim()) query.set("gaz", $("ms-gaz").value.trim());
  if ($("ms-uzun").checked) query.set("uzun", "true");
  return query;
}

$("ms-ara").addEventListener("click", loadStock);
for (const id of ["ms-q", "ms-gaz"]) $(id).addEventListener("keydown", (e) => { if (e.key === "Enter") loadStock(); });
liveFilter(["ms-q", "ms-gaz"], ["ms-uzun"], loadStock);
$("ms-csv").addEventListener("click", (e) => {
  e.preventDefault();
  const query = new URLSearchParams();
  if ($("ms-gaz").value.trim()) query.set("gaz", $("ms-gaz").value.trim());
  window.location.href = BASE + "/panel/api/musteri-stok.csv?" + query;
});

function stockGroupKey(gasType, capacity) { return `${(gasType || "").trim().toLocaleUpperCase("tr-TR")}|${capacity}`; }

function stockGroupTable(table, groups, longDays, onPick, picked) {
  const head = el("tr", {}, ...["Gaz türü", "Boyut", "Adet", "Dolu / boş", "En kısa", "Ortalama", "En uzun", `${longDays}+ gün`].map((h) => el("th", {}, h)));
  const body = groups.map((g) => el("tr", onPick
      ? { class: "click" + (picked === stockGroupKey(g.gasType, g.capacity) ? " selected" : ""), onclick: () => onPick(g) }
      : {},
    el("td", {}, el("b", {}, g.gasType)),
    el("td", {}, g.capacity),
    el("td", {}, el("b", {}, String(g.count))),
    el("td", {}, fullEmpty(g.full, g.empty)),
    el("td", {}, daysText(g.minDays)),
    el("td", {}, daysText(g.avgDays)),
    el("td", { class: g.maxDays >= longDays ? "warn-text" : "" }, daysText(g.maxDays)),
    el("td", { class: g.long ? "warn-text" : "muted" }, String(g.long))));
  table.replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 8, class: "muted" }, "Tüp yok."))])));
}

async function loadStock() {
  try {
    const data = await api("/panel/api/musteri-stok?" + stockFilter());
    if (data.totals) {
      $("ms-stats").replaceChildren(
        stat(data.customers, "Tüpü olan müşteri", "Filtreye uyan", () => {}),
        stat(data.cylinders, "Müşterideki tüp", fullEmpty(data.full, data.empty), () => openFiltered({ durum: 3 })),
        stat(data.longCount, `${data.longDays}+ gündür bekleyen`, "Dönmeyen tüpler", () => openFiltered({ uzun: true }), true));
      stockGroupTable($("ms-gas"), data.gasTotals, data.longDays, openGroupDialog);
    } else {
      $("ms-stats").replaceChildren();
      $("ms-gas").replaceChildren();
    }
    $("ms-count").textContent = data.totals ? `${data.customers} müşteri · ${data.cylinders} tüp` : `${data.customers} müşteri`;
    const head = el("tr", {}, ...["Müşteri", "Şehir", "Tüp", "Dolu / boş", "Gaz türleri", "Son teslim", "En uzun", `${data.longDays}+ gün`].map((h) => el("th", {}, h)));
    const body = data.rows.map((c) => el("tr", { class: "click", onclick: () => openStockDialog(c.id) },
      el("td", {}, el("b", {}, c.name), el("div", { class: "muted" }, c.code + (c.customerOwned ? ` · ${c.customerOwned} müşteri malı` : ""))),
      el("td", {}, [c.city, c.district].filter(Boolean).join(" / ") || "-"),
      el("td", {}, el("b", {}, String(c.count))),
      el("td", {}, fullEmpty(c.full, c.empty)),
      el("td", { class: "muted" }, c.gases),
      el("td", {}, c.lastDelivery || "-"),
      el("td", { class: c.maxDays >= data.longDays ? "warn-text" : "" }, daysText(c.maxDays)),
      el("td", { class: c.long ? "warn-text" : "muted" }, String(c.long))));
    $("ms-table").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 8, class: "muted" }, "Bu filtreye uyan müşteri stoku yok."))])));
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

async function openStockDialog(id, pick = null) {
  hide($("sd-msg"));
  try {
    stockCustomer = await api(`/panel/api/musteri/${id}/stok`);
  } catch (error) {
    show($("global-msg"), error.message);
    return;
  }
  const s = stockCustomer;
  const t = s.totals;
  $("sd-title").textContent = s.name;
  const info = [
    ["Müşteri kodu", s.code], ["Şehir / ilçe", [s.city, s.district].filter(Boolean).join(" / ") || "-"],
    ["Toplam tüp", String(t.count)], ["Dolu / boş", fullEmpty(t.full, t.empty) + (t.unknown ? ` · bilinmiyor ${t.unknown}` : "")],
    ["En uzun bekleyen", daysText(t.maxDays)], ["Ortalama bekleme", daysText(t.avgDays)],
    [`${s.longDays}+ gündür bekleyen`, String(t.long)], ["Müşteri malı tüp", String(t.customerOwned)]
  ];
  if (s.phone) info.splice(2, 0, ["Telefon", s.phone]);
  if (!s.isActive) info.push(["Kayıt", "Pasif müşteri"]);
  $("sd-info").replaceChildren(...info.map(([k, v]) => el("div", {}, el("span", {}, k), v)));
  stockPick = pick;
  renderStockGroups();
  $("sd-q").value = "";
  renderStockCylinders();
  if (!$("stock-dialog").open) $("stock-dialog").showModal();
}

function renderStockGroups() {
  const s = stockCustomer;
  stockGroupTable($("sd-groups"), s.groups, s.longDays, (g) => {
    const key = stockGroupKey(g.gasType, g.capacity);
    stockPick = stockPick === key ? null : key;
    renderStockGroups();
    renderStockCylinders();
  }, stockPick);
}

function renderStockCylinders() {
  const s = stockCustomer;
  if (!s) return;
  const term = $("sd-q").value.trim().toLocaleLowerCase("tr-TR");
  const rows = s.cylinders
    .filter((c) => !stockPick || stockGroupKey(c.gasType, c.capacity) === stockPick)
    .filter((c) => !term || [c.serialNumber, c.barcode, c.manufacturerSerial, c.gasType].some((v) => v && v.toLocaleLowerCase("tr-TR").includes(term)))
    .sort((a, b) => (b.days ?? -1) - (a.days ?? -1));
  const picked = s.groups.find((g) => stockGroupKey(g.gasType, g.capacity) === stockPick);
  $("sd-cyl-title").textContent = picked
    ? `${picked.gasType} ${picked.capacity} · ${picked.count} adet (tümünü görmek için gruba tekrar tıklayın)`
    : "Tüpler (gaz türü ve boyuta göre, en uzun bekleyen önce)";
  const order = new Map(s.groups.map((g, i) => [stockGroupKey(g.gasType, g.capacity), i]));
  const sections = new Map();
  for (const c of rows) {
    const key = stockGroupKey(c.gasType, c.capacity);
    if (!sections.has(key)) sections.set(key, []);
    sections.get(key).push(c);
  }
  const sorted = [...sections.entries()].sort((a, b) => (order.get(a[0]) ?? 1e9) - (order.get(b[0]) ?? 1e9));
  const head = el("tr", {}, ...["Seri no / barkod", "Gaz", "Boyut", "Dolu/Boş", "Teslim", "Gün", "Test"].map((h) => el("th", {}, h)));
  const body = sorted.flatMap(([, list]) => [el("tr", { class: "group-head" },
    el("td", { colspan: 7 }, `${list[0].gasType.trim()} ${list[0].capacity} · ${list.length} adet · ${fullEmpty(list.filter((c) => c.fullness === "Dolu").length, list.filter((c) => c.fullness === "Boş").length)}`)),
    ...list.map((c) => stockCylinderRow(c, s.longDays))]);
  $("sd-cylinders").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 7, class: "muted" }, s.cylinders.length ? "Aramaya uyan tüp yok." : "Bu müşteride tüp yok."))])));
}

function stockCylinderRow(c, longDays) {
  return el("tr", { class: "click", onclick: () => openDetail(c.id) },
    el("td", {}, el("b", {}, (c.needsReview ? "⚠ " : "") + c.serialNumber),
      c.barcode !== c.serialNumber ? el("div", { class: "muted" }, c.barcode) : null,
      c.manufacturerSerial ? el("div", { class: "muted" }, "Üretici seri: " + c.manufacturerSerial) : null,
      c.customerOwned ? el("div", { class: "muted" }, "Müşteri malı") : null),
    el("td", {}, c.gasType),
    el("td", {}, c.capacity),
    el("td", {}, c.fullness),
    el("td", {}, c.since || "-"),
    el("td", { class: c.days >= longDays ? "warn-text" : "" }, daysText(c.days)),
    el("td", { class: c.testOverdue ? "bad-text" : "" }, c.testDue || "-"));
}

let stockGroup = null;

async function openGroupDialog(g) {
  const query = stockFilter();
  query.set("grupGaz", g.gasType);
  query.set("boyut", g.capacity);
  try {
    stockGroup = await api("/panel/api/musteri-stok/grup?" + query);
  } catch (error) {
    show($("global-msg"), error.message);
    return;
  }
  const s = stockGroup;
  const t = s.totals;
  $("gd-title").textContent = `${s.gasType} ${s.capacity} · ${t.count} adet`;
  $("gd-info").replaceChildren(...[
    ["Toplam tüp", String(t.count)], ["Dolu / boş", fullEmpty(t.full, t.empty) + (t.unknown ? ` · bilinmiyor ${t.unknown}` : "")],
    ["Müşteri sayısı", String(s.customers.length)], ["En uzun bekleyen", daysText(t.maxDays)],
    ["Ortalama bekleme", daysText(t.avgDays)], [`${s.longDays}+ gündür bekleyen`, String(t.long)], ["Müşteri malı tüp", String(t.customerOwned)]
  ].map(([k, v]) => el("div", {}, el("span", {}, k), v)));
  const pick = stockGroupKey(s.gasType, s.capacity);
  const head = el("tr", {}, ...["Müşteri", "Şehir", "Adet", "Dolu / boş", "En uzun", `${s.longDays}+ gün`].map((h) => el("th", {}, h)));
  const body = s.customers.map((c) => el("tr", { class: "click", onclick: () => openStockDialog(c.id, pick) },
    el("td", {}, el("b", {}, c.name), el("div", { class: "muted" }, c.code)),
    el("td", {}, [c.city, c.district].filter(Boolean).join(" / ") || "-"),
    el("td", {}, el("b", {}, String(c.count))),
    el("td", {}, fullEmpty(c.full, c.empty)),
    el("td", { class: c.maxDays >= s.longDays ? "warn-text" : "" }, daysText(c.maxDays)),
    el("td", { class: c.long ? "warn-text" : "muted" }, String(c.long))));
  $("gd-customers").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Tüp yok."))])));
  $("gd-q").value = "";
  renderGroupCylinders();
  if (!$("group-dialog").open) $("group-dialog").showModal();
}

function renderGroupCylinders() {
  const s = stockGroup;
  if (!s) return;
  const term = $("gd-q").value.trim().toLocaleLowerCase("tr-TR");
  const rows = s.cylinders.filter((x) => !term || [x.customer, x.row.serialNumber, x.row.barcode, x.row.manufacturerSerial]
    .some((v) => v && v.toLocaleLowerCase("tr-TR").includes(term)));
  $("gd-cyl-title").textContent = term ? `'${$("gd-q").value.trim()}' için ${rows.length} tüp` : `Tüpler (${rows.length}) · en uzun bekleyen önce`;
  const head = el("tr", {}, ...["Seri no / barkod", "Müşteri", "Dolu/Boş", "Teslim", "Gün", "Test"].map((h) => el("th", {}, h)));
  const body = rows.map(({ row: c, customer, customerId }) => el("tr", { class: "click", onclick: () => openDetail(c.id) },
    el("td", {}, el("b", {}, (c.needsReview ? "⚠ " : "") + c.serialNumber),
      c.barcode !== c.serialNumber ? el("div", { class: "muted" }, c.barcode) : null,
      c.manufacturerSerial ? el("div", { class: "muted" }, "Üretici seri: " + c.manufacturerSerial) : null,
      c.customerOwned ? el("div", { class: "muted" }, "Müşteri malı") : null),
    el("td", {}, el("a", { href: "#", onclick: (e) => { e.preventDefault(); e.stopPropagation(); openStockDialog(customerId, stockGroupKey(s.gasType, s.capacity)); } }, customer)),
    el("td", {}, c.fullness),
    el("td", {}, c.since || "-"),
    el("td", { class: c.days >= s.longDays ? "warn-text" : "" }, daysText(c.days)),
    el("td", { class: c.testOverdue ? "bad-text" : "" }, c.testDue || "-")));
  $("gd-cylinders").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Aramaya uyan tüp yok."))])));
}

$("gd-q").addEventListener("input", renderGroupCylinders);
$("gd-close").addEventListener("click", () => $("group-dialog").close());
$("sd-q").addEventListener("input", renderStockCylinders);
$("sd-close").addEventListener("click", () => $("stock-dialog").close());
$("sd-list").addEventListener("click", () => {
  if (!stockCustomer) return;
  $("stock-dialog").close();
  openFiltered({ customer: { id: stockCustomer.id, name: stockCustomer.name, city: stockCustomer.city, cylinders: stockCustomer.totals.count } });
});
$("sd-csv").addEventListener("click", (e) => {
  e.preventDefault();
  if (stockCustomer) window.location.href = BASE + "/panel/api/musteri-stok.csv?musteri=" + stockCustomer.id;
});

// ---- Administration ------------------------------------------------------------------------------

let users = [];
let editingUser = null;
let resetUser = null;

let catalog = null;

async function loadAdmin() {
  hide($("admin-msg"));
  $("roles").replaceChildren(...me.roles.map((r) => el("div", {}, roleBadge(r.name), el("div", {}, r.description))));
  try {
    if (can("kullanici-yonetimi")) {
      [users, catalog] = await Promise.all([api("/panel/api/yonetim/kullanicilar"), api("/panel/api/yonetim/yetkiler")]);
      renderUsers();
    }
    if (can("cihaz-yonetimi")) await loadDevices();
    if (can("denetim-kaydi")) await loadAudit();
  } catch (error) {
    show($("admin-msg"), error.message);
  }
}

function permissionSummary(u) {
  if (u.role === "Yönetici") return el("div", { class: "muted" }, "Tüm yetkiler");
  const parts = [];
  if (u.extra.length) parts.push(`+${u.extra.length} ek`);
  if (u.removed.length) parts.push(`−${u.removed.length} çıkarıldı`);
  return el("div", { class: parts.length ? "perm-custom" : "muted" }, parts.length ? "Özel: " + parts.join(", ") : `${u.permissions.length} yetki (rol varsayılanı)`);
}

function renderUsers() {
  const head = el("tr", {}, ...["Kullanıcı", "Rol / yetkiler", "Durum", "Son giriş", "Açık oturum", "Oluşturan", ""].map((h) => el("th", {}, h)));
  const body = users.map((u) => el("tr", { class: u.isActive ? "" : "off" },
    el("td", {}, el("b", {}, u.displayName), u.isSelf ? el("span", { class: "muted" }, " (siz)") : null, el("div", { class: "muted" }, u.userName)),
    el("td", {}, roleBadge(u.role), permissionSummary(u)),
    el("td", {}, el("span", { class: "dot" + (u.isActive ? " on" : "") }), u.isActive ? "Aktif" : "Pasif",
      u.mustChangePassword ? el("div", { class: "warn-text muted" }, "Şifre değiştirmesi bekleniyor") : null),
    el("td", {}, u.lastLogin || "Hiç giriş yapmadı", u.lastLoginIp ? el("div", { class: "muted" }, u.lastLoginIp) : null),
    el("td", {}, `${u.sessions} tarayıcı`, el("div", { class: "muted" }, `${u.phones} telefon`)),
    el("td", {}, u.createdBy || "-", el("div", { class: "muted" }, u.created)),
    el("td", {},
      el("button", { class: "link", type: "button", onclick: () => openPermDialog(u) }, "Yetkiler"),
      el("button", { class: "link", type: "button", onclick: () => openUserDialog(u) }, "Düzenle"),
      el("button", { class: "link", type: "button", onclick: () => openResetDialog(u) }, "Şifre sıfırla"),
      u.isSelf ? null : el("button", { class: u.isActive ? "danger" : "link", type: "button", onclick: () => toggleUser(u) }, u.isActive ? "Pasife al" : "Aktif et"),
      u.isSelf ? null : el("button", { class: "link delete-link", type: "button", onclick: () => openDeleteDialog(u) }, "Sil"))));
  $("users").replaceChildren(el("thead", {}, head), el("tbody", {}, ...body));
}

async function loadAudit() {
  const rows = await api("/panel/api/yonetim/denetim");
  const head = el("tr", {}, ...["Zaman", "Kullanıcı", "İşlem", "Hedef", "Ayrıntı", "IP"].map((h) => el("th", {}, h)));
  const body = rows.map((a) => el("tr", {},
    el("td", {}, a.at), el("td", {}, a.actor),
    el("td", { class: a.action.includes("Hatalı") || a.action.includes("Pasif") || a.action.includes("silindi") ? "warn-text" : "" }, a.action),
    el("td", {}, a.target || "-"), el("td", { class: "muted" }, a.detail || ""), el("td", { class: "muted" }, a.ip || "")));
  $("audit").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Kayıt yok."))])));
}

$("audit-refresh").addEventListener("click", async () => {
  try { await loadAudit(); } catch (error) { show($("admin-msg"), error.message); }
});

// ---- Phones signed in with a panel user ----------------------------------------------------------

async function loadDevices() {
  const rows = await api("/panel/api/yonetim/cihazlar");
  const head = el("tr", {}, ...["Kullanıcı", "Telefon", "Giriş", "Son görülme", "Oturum bitişi", "Durum", ""].map((h) => el("th", {}, h)));
  const body = rows.map((d) => el("tr", { class: d.active ? "" : "off" },
    el("td", {}, d.user ? el("b", {}, d.displayName || d.user) : el("span", { class: "muted" }, "—"), d.user ? el("div", { class: "muted" }, d.user) : null),
    el("td", {}, d.name, d.ip ? el("div", { class: "muted" }, d.ip) : null),
    el("td", {}, d.created),
    el("td", {}, d.lastSeen || "-"),
    el("td", {}, d.expires || "-"),
    el("td", { class: d.active ? "" : "muted" }, el("span", { class: "dot" + (d.active ? " on" : "") }), d.state),
    el("td", {}, el("button", { class: d.active ? "danger" : "link", type: "button", onclick: () => closeDevice(d) }, d.active ? "Oturumu kapat" : "Kaydı sil"))));
  $("devices").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 7, class: "muted" }, "Giriş yapmış telefon yok."))])));
}

async function closeDevice(d) {
  if (d.active && !window.confirm(`${d.name} (${d.user}) telefonundaki oturum kapatılsın mı? Uygulama yeniden giriş isteyecek.`)) return;
  try {
    const result = await api(`/panel/api/yonetim/cihaz/${encodeURIComponent(d.id)}/kapat`, { method: "POST" });
    await loadDevices();
    show($("admin-msg"), result.message, "ok");
  } catch (error) {
    show($("admin-msg"), error.message);
  }
}

$("devices-refresh").addEventListener("click", async () => {
  try { await loadDevices(); } catch (error) { show($("admin-msg"), error.message); }
});

// ---- Per-user permissions ------------------------------------------------------------------------

let permUser = null;

function openPermDialog(u) {
  permUser = u;
  hide($("pm-msg"));
  $("pm-title").textContent = `Yetkiler: ${u.displayName} (${u.userName})`;
  const admin = u.role === "Yönetici";
  const role = catalog.roles.find((r) => r.name === u.role);
  const defaults = new Set(role ? role.defaults : []);
  $("pm-role").replaceChildren(roleBadge(u.role), " ", role ? role.description : "");
  $("pm-admin").classList.toggle("hidden", !admin);
  $("pm-save").disabled = admin;
  $("pm-reset").disabled = admin;
  const granted = new Set(u.permissions);
  $("pm-groups").replaceChildren(...catalog.groups.map((g) => el("fieldset", { class: "perm-group" },
    el("legend", {}, g.name),
    ...g.items.map((p) => {
      const box = el("input", { type: "checkbox", value: p.key });
      box.checked = granted.has(p.key);
      box.disabled = admin || p.adminOnly;
      const note = el("span", { class: "perm-note" });
      const mark = () => {
        const differs = !admin && box.checked !== defaults.has(p.key);
        note.textContent = p.adminOnly ? "yalnızca Yönetici" : differs ? (box.checked ? "rol dışı ek" : "rolden çıkarıldı") : "";
        label.classList.toggle("perm-diff", differs);
      };
      const label = el("label", { class: "perm-item" }, box, el("span", {}, p.label), note);
      box.addEventListener("change", mark);
      mark();
      return label;
    }))));
  $("perm-dialog").showModal();
}

async function savePermissions(reset) {
  const wanted = [...$("pm-groups").querySelectorAll("input[type=checkbox]")].filter((b) => b.checked && !b.disabled).map((b) => b.value);
  if (reset && !window.confirm(`${permUser.userName} kullanıcısının yetkileri "${permUser.role}" rolünün varsayılanına dönsün mü?`)) return;
  $("pm-save").disabled = $("pm-reset").disabled = true;
  try {
    const result = await api(`/panel/api/yonetim/kullanici/${permUser.id}/yetkiler`, { method: "POST", body: { izinler: wanted, sifirla: reset } });
    $("perm-dialog").close();
    await loadAdmin();
    show($("admin-msg"), `${permUser.userName}: ${result.message}`, "ok");
  } catch (error) {
    show($("pm-msg"), error.message);
  } finally {
    $("pm-save").disabled = $("pm-reset").disabled = false;
  }
}

$("pm-save").addEventListener("click", () => savePermissions(false));
$("pm-reset").addEventListener("click", () => savePermissions(true));
$("pm-close").addEventListener("click", () => $("perm-dialog").close());

function generatePassword() {
  const letters = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ";
  const digits = "23456789";
  const all = letters + digits;
  const random = new Uint32Array(12);
  crypto.getRandomValues(random);
  const chars = Array.from(random, (n) => all[n % all.length]);
  chars[random[0] % 6] = letters[random[1] % letters.length];
  chars[6 + (random[2] % 6)] = digits[random[3] % digits.length];
  return chars.join("");
}

function fillRoles(selected) {
  $("u-role").replaceChildren(...me.roles.map((r) => el("option", { value: r.name }, r.name)));
  $("u-role").value = selected;
  showRoleDescription();
}

function showRoleDescription() {
  const role = me.roles.find((r) => r.name === $("u-role").value);
  $("u-role-desc").textContent = role ? role.description : "";
}

$("u-role").addEventListener("change", showRoleDescription);
$("u-gen").addEventListener("click", () => { $("u-pass").value = generatePassword(); });
$("r-gen").addEventListener("click", () => { $("r-pass").value = generatePassword(); });
$("u-close").addEventListener("click", () => $("user-dialog").close());
$("r-close").addEventListener("click", () => $("reset-dialog").close());
$("u-new").addEventListener("click", () => openUserDialog(null));

function openUserDialog(u) {
  editingUser = u;
  $("user-form").reset();
  hide($("u-msg"));
  $("u-title").textContent = u ? `Kullanıcıyı düzenle: ${u.userName}` : "Yeni kullanıcı";
  $("u-name").value = u ? u.userName : "";
  $("u-name").disabled = !!u;
  $("u-display").value = u ? u.displayName : "";
  fillRoles(u ? u.role : "Operatör");
  $("u-pass").required = !u;
  $("u-pass-wrap").classList.toggle("hidden", !!u);
  $("u-must-wrap").classList.toggle("hidden", !!u);
  $("u-active-wrap").classList.toggle("hidden", !u || u.isSelf);
  $("u-active").checked = u ? u.isActive : true;
  $("u-role").disabled = !!u && u.isSelf;
  $("user-dialog").showModal();
  (u ? $("u-display") : $("u-name")).focus();
}

$("user-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("u-save").disabled = true;
  try {
    let result;
    if (editingUser) {
      result = await api(`/panel/api/yonetim/kullanici/${editingUser.id}`, {
        method: "POST", body: { adSoyad: $("u-display").value.trim(), rol: $("u-role").value, aktif: editingUser.isSelf ? true : $("u-active").checked }
      });
    } else {
      result = await api("/panel/api/yonetim/kullanici", {
        method: "POST",
        body: { kullaniciAdi: $("u-name").value.trim(), adSoyad: $("u-display").value.trim(), rol: $("u-role").value, sifre: $("u-pass").value, sifreDegistirsin: $("u-must").checked }
      });
    }
    $("user-dialog").close();
    await loadAdmin();
    show($("admin-msg"), result.message + (editingUser ? "" : " Giriş bilgilerini kullanıcıya güvenli bir yoldan iletin."), "ok");
  } catch (error) {
    show($("u-msg"), error.message);
  } finally {
    $("u-save").disabled = false;
  }
});

function openResetDialog(u) {
  resetUser = u;
  $("reset-form").reset();
  hide($("r-msg"));
  $("r-title").textContent = `Şifre sıfırla: ${u.userName}`;
  $("r-must").checked = !u.isSelf;
  $("reset-dialog").showModal();
  $("r-pass").focus();
}

$("reset-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  try {
    const result = await api(`/panel/api/yonetim/kullanici/${resetUser.id}/sifre`, { method: "POST", body: { sifre: $("r-pass").value, sifreDegistirsin: $("r-must").checked } });
    $("reset-dialog").close();
    if (resetUser.isSelf) { showLogin("Şifreniz sıfırlandı; yeni şifreyle tekrar giriş yapın."); return; }
    await loadAdmin();
    show($("admin-msg"), result.message, "ok");
  } catch (error) {
    show($("r-msg"), error.message);
  }
});

async function toggleUser(u) {
  const activate = !u.isActive;
  if (!activate && !window.confirm(`${u.userName} pasife alınsın mı? Giriş yapamaz ve açık oturumları hemen kapanır (silinmez, tekrar aktif edilebilir).`)) return;
  try {
    const result = await api(`/panel/api/yonetim/kullanici/${u.id}`, { method: "POST", body: { adSoyad: u.displayName, rol: u.role, aktif: activate } });
    await loadAdmin();
    show($("admin-msg"), result.message, "ok");
  } catch (error) {
    show($("admin-msg"), error.message);
  }
}

let deleteUser = null;

function openDeleteDialog(u) {
  deleteUser = u;
  $("delete-form").reset();
  hide($("x-msg"));
  $("x-title").textContent = `Kullanıcıyı sil: ${u.userName}`;
  $("x-who").textContent = `${u.displayName} (${u.userName}) – ${u.role}`;
  $("x-name").placeholder = u.userName;
  $("x-save").disabled = true;
  $("delete-dialog").showModal();
  $("x-name").focus();
}

$("x-name").addEventListener("input", () => {
  $("x-save").disabled = !deleteUser || $("x-name").value.trim().toLowerCase() !== deleteUser.userName.toLowerCase();
});
$("x-close").addEventListener("click", () => $("delete-dialog").close());

$("delete-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("x-save").disabled = true;
  try {
    const result = await api(`/panel/api/yonetim/kullanici/${deleteUser.id}/sil`, { method: "POST", body: { onay: $("x-name").value.trim() } });
    $("delete-dialog").close();
    await loadAdmin();
    show($("admin-msg"), result.message, "ok");
  } catch (error) {
    show($("x-msg"), error.message);
    $("x-save").disabled = false;
  }
});

// Customer requests: open ones first; a delivery scanned on a trip closes them by itself.
async function loadRequests() {
  const ticket = newRequest("requests");
  try {
    const rows = await api(`/panel/api/musteri-talepleri?durum=${$("rq-status").value}`);
    if (isStale("requests", ticket)) return;
    const open = rows.filter((r) => r.status === "acik").length;
    $("rq-count").textContent = `${open} açık talep` + (rows.length > open ? ` · ${rows.length - open} kapanmış` : "");
    const head = el("tr", {}, ...["Tarih", "Müşteri", "Talep", "Not", "Durum", ""].map((h) => el("th", {}, h)));
    const body = rows.map((r) => el("tr", { class: r.status === "acik" ? "" : "off" },
      el("td", {}, r.created, el("div", { class: "muted" }, "#" + r.id)),
      el("td", {}, el("b", {}, r.customerName), r.city ? el("div", { class: "muted" }, [r.city, r.district].filter(Boolean).join(" / ")) : null),
      el("td", {}, r.kindLabel + (r.quantity ? ` (${r.quantity} tüp)` : "")),
      el("td", { class: "muted" }, r.note || ""),
      el("td", {}, r.statusLabel, r.handled ? el("div", { class: "muted" }, `${r.handled} · ${r.handledBy || ""}`) : null,
        r.handledNote ? el("div", { class: "muted" }, r.handledNote) : null),
      el("td", {},
        r.status === "acik" ? el("button", { class: "link", type: "button", onclick: () => closeRequest(r, "tamamlandi") }, "Tamamlandı") : null,
        r.status === "acik" ? el("button", { class: "link delete-link", type: "button", onclick: () => closeRequest(r, "iptal") }, "İptal") : null)));
    $("rq-rows").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Talep yok."))])));
  } catch (error) {
    show($("rq-msg"), error.message);
  }
}

async function closeRequest(r, status) {
  const verb = status === "iptal" ? "iptal edilsin" : "tamamlandı olarak işaretlensin";
  const note = window.prompt(`${r.customerName}: ${r.kindLabel} talebi ${verb} mi? İsterseniz kısa bir not yazın.`, "");
  if (note === null) return;
  try {
    const result = await api(`/panel/api/musteri-talebi/${r.id}`, { method: "POST", body: { durum: status, not: note } });
    show($("rq-msg"), result.message, "ok");
    loadRequests();
  } catch (error) {
    show($("rq-msg"), error.message);
  }
}

$("rq-list").addEventListener("click", () => { hide($("rq-msg")); loadRequests(); });
$("rq-status").addEventListener("change", loadRequests);

// Trip plans: the customers a vehicle visits on a day and how close the phone must come before it warns.
const PLAN_STATUS = { bekliyor: "Bekliyor", seferde: "Seferde", kapandi: "Sefer kapandı" };
const planCustomer = customerPicker("tp-musteri-q", "tp-musteri", null);
let planData = null;
let editingPlan = null;
let planStops = [];

function isoToday() {
  const d = new Date();
  return new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 10);
}

function metersLabel(m) { return m >= 1000 ? `${(m / 1000).toLocaleString("tr-TR")} km` : `${m} m`; }

async function loadPlans() {
  if (!$("tp-date").value) $("tp-date").value = isoToday();
  const ticket = newRequest("plans");
  try {
    const data = await api(`/panel/api/sefer-planlari?tarih=${$("tp-date").value}`);
    if (isStale("plans", ticket)) return;
    planData = data;
    $("tp-count").textContent = data.plans.length ? `${data.plans.length} plan` : "";
    const head = el("tr", {}, ...["Araç", "Şoför", "Müşteri", "Konumu kayıtlı", "Uyarı mesafesi", "Durum", "Son değişiklik"].map((h) => el("th", {}, h)));
    const body = data.plans.map((p) => el("tr", { class: "click" + (p.status === "kapandi" ? " off" : ""), onclick: () => openPlan(p.id) },
      el("td", {}, el("b", {}, p.plate), p.note ? el("div", { class: "muted" }, p.note) : null),
      el("td", {}, p.drivers.length ? p.drivers.join(", ") : el("span", { class: "muted" }, "sabit şoför yok")),
      el("td", {}, String(p.stopCount), p.done ? el("div", { class: "muted" }, `${p.done} uğrandı`) : null),
      el("td", { class: p.located < p.stopCount ? "warn-text" : "" }, `${p.located} / ${p.stopCount}`,
        p.unverified ? el("div", { class: "muted" }, `${p.unverified} doğrulanmamış`) : null),
      el("td", {}, metersLabel(p.alertMeters)),
      el("td", {}, PLAN_STATUS[p.status] || p.status, p.tripId ? el("div", { class: "muted" }, `Sefer #${p.tripId}`) : null),
      el("td", { class: "muted" }, `${p.updated} · ${p.updatedBy}`)));
    $("tp-rows").replaceChildren(el("thead", {}, head),
      el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 7, class: "muted" }, "Bu gün için plan yok. \"Yeni plan\" ile ekleyin."))])));
  } catch (error) {
    show($("tp-msg"), error.message);
  }
}

async function openPlan(id) {
  hide($("tp-dmsg"));
  try {
    if (!planData) await loadPlans();
    const plan = id ? await api(`/panel/api/sefer-plani/${id}`) : null;
    editingPlan = plan;
    planStops = plan
      ? plan.stops.map((s) => ({ customerId: s.customerId, name: s.customerName, place: [s.city, s.district].filter(Boolean).join(" / "), note: s.note || "", latitude: s.latitude, longitude: s.longitude, verified: s.locationVerified, done: s.done }))
      : [];
    const plates = planData.vehicles.map((v) => v.plate);
    if (plan && !plates.includes(plan.plate)) plates.unshift(plan.plate);
    $("tp-plate").replaceChildren(el("option", { value: "" }, "Araç seçin"), ...plates.map((p) => {
      const v = planData.vehicles.find((x) => x.plate === p);
      return el("option", { value: p }, p + (v && (v.name || v.driverName) ? ` · ${[v.name, v.driverName].filter(Boolean).join(" · ")}` : ""));
    }));
    const meters = plan ? plan.alertMeters : planData.defaultMeters;
    const choices = planData.distances.includes(meters) ? planData.distances : [...planData.distances, meters].sort((a, b) => a - b);
    $("tp-meters").replaceChildren(...choices.map((m) => el("option", { value: m }, metersLabel(m))));
    $("tp-meters").value = String(meters);
    $("tp-day").value = plan ? plan.date : ($("tp-date").value || isoToday());
    $("tp-day").min = isoToday();
    $("tp-plate").value = plan ? plan.plate : "";
    $("tp-note").value = plan?.note || "";
    const onTrip = !!plan?.tripId;
    const closed = plan?.status === "kapandi";
    $("tp-day").disabled = onTrip;
    $("tp-plate").disabled = onTrip;
    $("tp-save").disabled = closed;
    $("tp-add").disabled = closed;
    $("tp-delete").classList.toggle("hidden", !plan);
    $("tp-title").textContent = plan ? `Sefer planı · ${plan.plate}` : "Yeni sefer planı";
    $("tp-status").textContent = !plan ? "" : closed ? "Seferi kapandı; plan değiştirilemez."
      : onTrip ? `Sefer #${plan.tripId} rotasına eklendi; değişiklikler rotaya da işlenir.` : "Araç bu gün sefere çıkınca rotaya eklenecek.";
    planCustomer.reset();
    renderPlanStops();
    if (!$("tp-dialog").open) $("tp-dialog").showModal();
  } catch (error) {
    show($("tp-msg"), error.message);
  }
}

function renderPlanStops() {
  const closed = editingPlan?.status === "kapandi";
  const head = el("tr", {}, ...["#", "Müşteri", "Durak notu", "Konum", ""].map((h) => el("th", {}, h)));
  const body = planStops.map((s, i) => {
    const note = el("input", { value: s.note, maxlength: 200, placeholder: "ör. 5 dolu bırak", style: "width:100%" });
    note.disabled = closed;
    note.addEventListener("input", () => { s.note = note.value; });
    const located = s.latitude != null;
    const unverified = located && s.verified === false;
    return el("tr", { class: s.done ? "off" : "" },
      el("td", {}, String(i + 1)),
      el("td", {}, el("b", {}, s.name), s.place ? el("div", { class: "muted" }, s.place) : null, s.done ? el("div", { class: "muted" }, "✓ uğrandı") : null),
      el("td", {}, note),
      el("td", {}, el("button", { class: "link" + (located && !unverified ? "" : " delete-link"), type: "button", onclick: () => openLocation(s) },
        !located ? "📍 Konum yok" : unverified ? "📍 Doğrulanmamış" : "📍 Kayıtlı")),
      el("td", {}, closed ? null : el("span", {},
        el("button", { class: "link", type: "button", title: "Yukarı", onclick: () => movePlanStop(i, -1) }, "▲"),
        el("button", { class: "link", type: "button", title: "Aşağı", onclick: () => movePlanStop(i, 1) }, "▼"),
        el("button", { class: "link delete-link", type: "button", onclick: () => { planStops.splice(i, 1); renderPlanStops(); } }, "Çıkar"))));
  });
  $("tp-stops").replaceChildren(el("thead", {}, head),
    el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 5, class: "muted" }, "Henüz müşteri yok. Yukarıdan arayıp ekleyin."))])));
}

function movePlanStop(i, direction) {
  const j = i + direction;
  if (j < 0 || j >= planStops.length) return;
  [planStops[i], planStops[j]] = [planStops[j], planStops[i]];
  renderPlanStops();
}

$("tp-add").addEventListener("click", async () => {
  hide($("tp-dmsg"));
  const select = $("tp-musteri");
  if (!select.value && planCustomer.query()) await planCustomer.search();
  const id = Number(select.value);
  if (!id) { show($("tp-dmsg"), "Önce müşteri arayıp seçin."); return; }
  if (planStops.some((s) => s.customerId === id)) { show($("tp-dmsg"), "Bu müşteri zaten planda."); return; }
  const label = select.options[select.selectedIndex].textContent;
  let location = null;
  try { location = (await api(`/panel/api/musteri/${id}/konum`)).location; } catch { location = null; }
  planStops.push({ customerId: id, name: label, place: "", note: "", latitude: location?.latitude ?? null, longitude: location?.longitude ?? null, verified: location?.verified, done: false });
  planCustomer.reset();
  renderPlanStops();
});

$("tp-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  hide($("tp-dmsg"));
  if (!$("tp-plate").value) { show($("tp-dmsg"), "Araç seçin."); return; }
  $("tp-save").disabled = true;
  try {
    const result = await api("/panel/api/sefer-plani", {
      method: "POST",
      body: {
        id: editingPlan?.id ?? null,
        tarih: $("tp-day").value,
        plaka: $("tp-plate").value,
        mesafe: Number($("tp-meters").value),
        not: $("tp-note").value,
        duraklar: planStops.map((s) => ({ musteriId: s.customerId, not: s.note }))
      }
    });
    $("tp-dialog").close();
    $("tp-date").value = $("tp-day").value;
    show($("tp-msg"), result.message, "ok");
    loadPlans();
  } catch (error) {
    show($("tp-dmsg"), error.message);
  } finally {
    $("tp-save").disabled = editingPlan?.status === "kapandi";
  }
});

$("tp-delete").addEventListener("click", async () => {
  if (!editingPlan || !window.confirm(`${editingPlan.plate} için ${editingPlan.date.split("-").reverse().join(".")} planı silinsin mi?`)) return;
  try {
    const result = await api(`/panel/api/sefer-plani/${editingPlan.id}/sil`, { method: "POST", body: {} });
    $("tp-dialog").close();
    show($("tp-msg"), result.message, "ok");
    loadPlans();
  } catch (error) {
    show($("tp-dmsg"), error.message);
  }
});

$("tp-close").addEventListener("click", () => $("tp-dialog").close());
$("tp-new").addEventListener("click", () => { hide($("tp-msg")); openPlan(null); });
$("tp-list").addEventListener("click", () => { hide($("tp-msg")); loadPlans(); });
$("tp-date").addEventListener("change", loadPlans);

// Customer location: a pasted Google Maps link (short links too) or coordinates; empty clears it.
let locationStop = null;

const LOCATION_SOURCE = { telefon: "telefondan", google: "Google'dan", panel: "panelden" };

function showLocation(location) {
  const unverified = location && location.verified === false;
  $("loc-current").replaceChildren(location
    ? el("span", {}, unverified ? el("b", { class: "warn-text" }, "Doğrulanmamış konum. ") : null,
      `Kayıtlı konum: ${location.latitude.toFixed(5)}, ${location.longitude.toFixed(5)} (${LOCATION_SOURCE[location.source] || location.source}, ${location.updated}) `,
      el("a", { href: location.mapUrl, target: "_blank", rel: "noopener" }, "Haritada aç"),
      location.label ? el("div", { class: "muted" }, `Google: ${location.label}`) : null,
      unverified ? el("div", { class: "muted" }, "Haritada doğruysa onaylayın; yanlışsa doğru bağlantıyı veya koordinatı yapıştırın. Şoför de müşteriye vardığında telefonda teyit eder.") : null)
    : "Kayıtlı konum yok; bu müşteride yaklaşma uyarısı çıkmaz.");
  $("loc-verify").classList.toggle("hidden", !unverified);
}

function locationChanged(location) {
  locationStop.latitude = location?.latitude ?? null;
  locationStop.longitude = location?.longitude ?? null;
  locationStop.verified = location?.verified;
  showLocation(location);
  if (planStops.includes(locationStop)) renderPlanStops();
}

async function openLocation(stop) {
  locationStop = stop;
  hide($("loc-msg"));
  $("loc-title").textContent = `Konum · ${stop.name}`;
  $("loc-text").value = "";
  $("loc-current").textContent = "Yükleniyor…";
  $("loc-verify").classList.add("hidden");
  $("loc-dialog").showModal();
  try {
    showLocation((await api(`/panel/api/musteri/${stop.customerId}/konum`)).location);
  } catch (error) {
    show($("loc-msg"), error.message);
  }
}

async function saveLocation(text) {
  hide($("loc-msg"));
  $("loc-save").disabled = true;
  try {
    const result = await api(`/panel/api/musteri/${locationStop.customerId}/konum`, { method: "POST", body: { metin: text } });
    locationChanged(result.location);
    $("loc-text").value = "";
    show($("loc-msg"), result.message, "ok");
  } catch (error) {
    show($("loc-msg"), error.message);
  } finally {
    $("loc-save").disabled = false;
  }
}

$("loc-form").addEventListener("submit", (event) => {
  event.preventDefault();
  const text = $("loc-text").value.trim();
  if (!text) { show($("loc-msg"), "Bağlantıyı veya koordinatı yapıştırın."); return; }
  saveLocation(text);
});
$("loc-clear").addEventListener("click", () => {
  if (window.confirm(`${locationStop.name} için kayıtlı konum silinsin mi?`)) saveLocation("");
});
$("loc-close").addEventListener("click", () => $("loc-dialog").close());
$("loc-verify").addEventListener("click", async () => {
  hide($("loc-msg"));
  $("loc-verify").disabled = true;
  try {
    const result = await api(`/panel/api/musteri/${locationStop.customerId}/konum/onayla`, { method: "POST", body: {} });
    locationChanged(result.location);
    show($("loc-msg"), result.message, "ok");
  } catch (error) {
    show($("loc-msg"), error.message);
  } finally {
    $("loc-verify").disabled = false;
  }
});

// Customer locations card: open any customer's location.
const locationCustomer = customerPicker("cl-musteri-q", "cl-musteri", null);

$("cl-open").addEventListener("click", async () => {
  hide($("cl-msg"));
  const select = $("cl-musteri");
  if (!select.value && locationCustomer.query()) await locationCustomer.search();
  const id = Number(select.value);
  if (!id) { show($("cl-msg"), "Önce müşteri arayıp seçin."); return; }
  openLocation({ customerId: id, name: select.options[select.selectedIndex].textContent });
});


const requestCustomer = customerPicker("rq-musteri-q", "rq-musteri", null);
$("rq-add").addEventListener("click", async () => {
  hide($("rq-msg"));
  const select = $("rq-musteri");
  if (!select.value && requestCustomer.query()) await requestCustomer.search();
  const id = Number(select.value);
  if (!id) { show($("rq-msg"), "Önce müşteri arayıp seçin."); return; }
  $("rq-add").disabled = true;
  try {
    const result = await api("/panel/api/musteri-talebi", { method: "POST", body: { musteriId: id, tur: $("rq-tur").value, adet: $("rq-adet").value ? Number($("rq-adet").value) : null, not: $("rq-not").value } });
    $("rq-adet").value = "";
    $("rq-not").value = "";
    requestCustomer.reset();
    show($("rq-msg"), result.message, "ok");
    loadRequests();
  } catch (error) {
    show($("rq-msg"), error.message);
  } finally {
    $("rq-add").disabled = false;
  }
});

// ---- Count reports -------------------------------------------------------------------------------

let countReport = null;
let countPick = null;

async function loadCounts() {
  const ticket = newRequest("counts");
  try {
    const data = await api("/panel/api/sayimlar");
    if (isStale("counts", ticket)) return;
    const open = data.counts.find((c) => c.open);
    const last = data.counts.find((c) => !c.open);
    $("sc-stats").replaceChildren(
      stat(open ? open.counted : "-", open ? "Açık sayım: okutulan" : "Açık sayım yok", open ? `${open.started} · ${open.startedBy}` : "Mobilde Depo Sayımı ile başlatın", () => open && openCountDialog(open.id)),
      stat(last ? last.missing : "-", "Son sayımda eksik", last ? last.closed : "Bitmiş sayım yok", () => last && openCountDialog(last.id, "missing"), !!last && last.missing > 0),
      stat(last ? last.extra : "-", "Son sayımda fazla", last ? `${last.matched} eşleşen · ${last.unknown} tanınmayan` : "", () => last && openCountDialog(last.id, "extra"), !!last && last.extra > 0));
    const latest = last || open;
    for (const [id, kind] of [["sc-xlsx", "excel"], ["sc-pdf", "pdf"]]) {
      $(id).classList.toggle("hidden", !latest);
      if (latest) $(id).href = `${BASE}/panel/api/sayim/${latest.id}/${kind}`;
    }
    $("sc-export-note").textContent = latest ? `${latest === open ? "Açık sayım" : "Son biten sayım"} · ${latest.started}` : "";
    const head = el("tr", {}, ...["Başlangıç", "Dışa aktar", "Durum", "Yapan", "Kayıtta depoda", "Okutulan", "Eşleşen", "Eksik", "Fazla", "Tanınmayan", "Not"].map((h) => el("th", {}, h)));
    const exportLink = (id, kind, text) => el("a", { class: "secondary small", href: `${BASE}/panel/api/sayim/${id}/${kind}`, onclick: (e) => e.stopPropagation(), ...(kind === "pdf" ? { target: "_blank", rel: "noopener" } : {}) }, text);
    const body = data.counts.map((c) => el("tr", { class: "click", onclick: () => openCountDialog(c.id) },
      el("td", {}, el("b", {}, c.started), el("div", { class: "muted" }, c.location)),
      el("td", {}, exportLink(c.id, "excel", "Excel"), " ", exportLink(c.id, "pdf", "PDF")),
      el("td", {}, c.open ? el("span", { class: "badge b2" }, "Devam ediyor") : el("span", {}, "Bitti", el("div", { class: "muted" }, c.closed))),
      el("td", {}, c.startedBy, c.closedBy && c.closedBy !== c.startedBy ? el("div", { class: "muted" }, "Bitiren: " + c.closedBy) : null),
      el("td", {}, String(c.expected)),
      el("td", {}, el("b", {}, String(c.counted))),
      el("td", {}, String(c.matched)),
      el("td", { class: c.missing ? "bad-text" : "muted" }, String(c.missing)),
      el("td", { class: c.extra ? "warn-text" : "muted" }, String(c.extra)),
      el("td", { class: c.unknown ? "warn-text" : "muted" }, String(c.unknown)),
      el("td", { class: "muted" }, c.note || "")));
    $("sc-counts").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 11, class: "muted" }, "Henüz depo sayımı yapılmadı. Mobil uygulamada Depo Sayımı ekranından başlatın."))])));
    const aHead = el("tr", {}, ...["Tarih", "Müşteri", "Kayıtta", "Bulunan", "Eksik", "Fazla", "Tanınmayan", "Yapan"].map((h) => el("th", {}, h)));
    const list = (items) => items.length ? el("div", { class: "muted" }, items.join(", ")) : null;
    const aBody = data.audits.map((a) => el("tr", {},
      el("td", {}, a.at),
      el("td", {}, el("b", {}, a.customerName), a.note ? el("div", { class: "muted" }, a.note) : null),
      el("td", {}, String(a.expected)),
      el("td", {}, String(a.found)),
      el("td", { class: a.missing.length ? "bad-text" : "muted" }, String(a.missing.length), list(a.missing)),
      el("td", { class: a.unexpected.length ? "warn-text" : "muted" }, String(a.unexpected.length), list(a.unexpected)),
      el("td", { class: a.unknown.length ? "warn-text" : "muted" }, String(a.unknown.length), list(a.unknown)),
      el("td", { class: "muted" }, a.performedBy || "")));
    $("sc-audits").replaceChildren(el("thead", {}, aHead), el("tbody", {}, ...(aBody.length ? aBody : [el("tr", {}, el("td", { colspan: 8, class: "muted" }, "Müşteride sayım kaydı yok."))])));
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

async function openCountDialog(id, kind = null) {
  hide($("cd-msg"));
  try {
    countReport = await api(`/panel/api/sayim/${id}`);
  } catch (error) {
    show($("global-msg"), error.message);
    return;
  }
  if (kind) $("cd-kind").value = kind;
  else if (!$("count-dialog").open) $("cd-kind").value = countReport.missing ? "missing" : countReport.extra ? "extra" : "matched";
  if (!$("count-dialog").open) { countPick = null; $("cd-q").value = ""; }
  renderCount();
  if (!$("count-dialog").open) $("count-dialog").showModal();
}

function renderCount() {
  const r = countReport;
  if (!r) return;
  $("cd-title").textContent = `${r.location} sayımı · ${r.started}` + (r.open ? " (devam ediyor)" : "");
  $("cd-close-count").classList.toggle("hidden", !r.open || !can("depo-sayim"));
  $("cd-xlsx").href = `${BASE}/panel/api/sayim/${r.id}/excel`;
  $("cd-pdf").href = `${BASE}/panel/api/sayim/${r.id}/pdf`;
  const info = [
    ["Başlatan", `${r.startedBy} · ${r.started}`],
    ["Durum", r.open ? "Devam ediyor (kayıttaki depo listesi canlı)" : `Bitti · ${r.closed} · ${r.closedBy || ""}`],
    [r.open ? "Şu an kayıtta depoda" : "Kayıtta depoda (bitişte)", String(r.expected)],
    ["Okutulan tüp", String(r.counted)], ["Eşleşen", String(r.matched)], ["Eksik", String(r.missing)],
    ["Fazla / yer uyuşmazlığı", String(r.extra)], ["Tanınmayan barkod", String(r.unknown)]
  ];
  if (r.note) info.push(["Not", r.note]);
  $("cd-info").replaceChildren(...info.map(([k, v]) => el("div", {}, el("span", {}, k), v)));
  const head = el("tr", {}, ...["Gaz türü", "Boyut", "Kayıtta depoda", "Sayılan", "Eşleşen", "Eksik", "Fazla", "Dolu / boş (sayılan)", "Fark"].map((h) => el("th", {}, h)));
  const body = r.groups.map((g) => {
    const diff = g.counted - g.expected;
    return el("tr", { class: "click" + (countPick === g.title ? " selected" : ""), onclick: () => { countPick = countPick === g.title ? null : g.title; renderCount(); } },
      el("td", {}, el("b", {}, g.gasType)),
      el("td", {}, g.capacity || "-"),
      el("td", {}, String(g.expected)),
      el("td", {}, el("b", {}, String(g.counted))),
      el("td", {}, String(g.matched)),
      el("td", { class: g.missing ? "bad-text" : "muted" }, String(g.missing)),
      el("td", { class: g.extra ? "warn-text" : "muted" }, String(g.extra)),
      el("td", {}, fullEmpty(g.full, g.empty)),
      el("td", { class: diff < 0 ? "bad-text" : diff > 0 ? "warn-text" : "muted" }, diff > 0 ? `+${diff}` : String(diff)));
  });
  $("cd-groups").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 9, class: "muted" }, "Kayıtta depoda tüp yok ve hiç tüp okutulmadı."))])));
  renderCountList();
}

function renderCountList() {
  const r = countReport;
  if (!r) return;
  const kind = $("cd-kind").value;
  const term = $("cd-q").value.trim().toLocaleLowerCase("tr-TR");
  const label = $("cd-kind").selectedOptions[0].textContent;
  if (kind === "unknown") {
    const rows = r.unknownCodes.filter((u) => !term || u.code.toLocaleLowerCase("tr-TR").includes(term));
    $("cd-list-title").textContent = `${label} · ${rows.length}`;
    const head = el("tr", {}, ...["Okutulan kod", "Zaman", "Okutan"].map((h) => el("th", {}, h)));
    const body = rows.map((u) => el("tr", {}, el("td", {}, el("b", {}, u.code)), el("td", {}, u.at), el("td", { class: "muted" }, u.scannedBy)));
    $("cd-list").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 3, class: "muted" }, "Tanınmayan barkod yok."))])));
    return;
  }
  const source = { missing: r.missingList, extra: r.extraList, matched: r.matchedList }[kind] || [];
  const rows = source
    .filter((c) => !countPick || c.group === countPick)
    .filter((c) => !term || [c.serialNumber, c.barcode, c.gasType, c.place].some((v) => v && v.toLocaleLowerCase("tr-TR").includes(term)));
  $("cd-list-title").textContent = `${label}${countPick ? " · " + countPick : ""} · ${rows.length} tüp`;
  const head = el("tr", {}, ...["Seri no / barkod", "Gaz", "Boyut", "Dolu/Boş", kind === "extra" ? "Kayıtta nerede (okutulurken)" : "Yer", "Okutulma"].map((h) => el("th", {}, h)));
  const sections = new Map();
  for (const c of rows) {
    if (!sections.has(c.group)) sections.set(c.group, []);
    sections.get(c.group).push(c);
  }
  const body = [...sections.entries()].flatMap(([group, list]) => [
    el("tr", { class: "group-head" }, el("td", { colspan: 6 }, `${group} · ${list.length} adet`)),
    ...list.map((c) => el("tr", { class: "click", onclick: () => openDetail(c.id) },
      el("td", {}, el("b", {}, c.serialNumber), c.barcode !== c.serialNumber ? el("div", { class: "muted" }, c.barcode) : null),
      el("td", {}, c.gasType),
      el("td", {}, c.capacity),
      el("td", {}, c.fullness),
      el("td", { class: kind === "extra" ? "warn-text" : "" }, c.place),
      el("td", { class: "muted" }, c.at ? `${c.at} · ${c.scannedBy || ""}` : "-")))]);
  const empty = { missing: "Eksik tüp yok.", extra: "Fazla tüp yok.", matched: "Eşleşen tüp yok." }[kind];
  $("cd-list").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, term || countPick ? "Filtreye uyan tüp yok." : empty))])));
}

$("sc-refresh").addEventListener("click", loadCounts);
$("cd-close").addEventListener("click", () => $("count-dialog").close());
$("cd-refresh").addEventListener("click", () => countReport && openCountDialog(countReport.id));
$("cd-kind").addEventListener("change", renderCountList);
$("cd-q").addEventListener("input", renderCountList);
$("cd-close-count").addEventListener("click", async () => {
  const r = countReport;
  if (!r || !r.open) return;
  if (!confirm(`${r.location} sayımı bitirilsin mi?\n\n${r.counted} tüp okutuldu, şu an ${r.missing} eksik, ${r.extra} fazla görünüyor. Bitirince depo listesi dondurulur ve bu sayıma okutma yapılamaz.`)) return;
  const button = $("cd-close-count");
  button.disabled = true;
  try {
    const result = await api(`/panel/api/sayim/${r.id}/bitir`, { method: "POST" });
    if (result.count) {
      countReport = result.count;
      renderCount();
      show($("cd-msg"), result.message, "ok");
    } else {
      $("count-dialog").close();
      show($("global-msg"), result.message, "ok");
    }
    loadCounts();
  } catch (error) {
    show($("cd-msg"), error.message);
  } finally {
    button.disabled = false;
  }
});

// ---- Karekod Yazdır: field-style stickers for TSC TA310.
// Preview uses SVG in the browser; real print sends TSPL2 so the printer builds QRCODE itself (300 dpi).
const LABEL_DOT_MM = 25.4 / 300;
const LABEL_DPI = 300;
const LABEL_DOTS_PER_MM = LABEL_DPI / 25.4;
const LABEL_SETTINGS_KEY = "arsan-etiket-ayar";
const LABEL_PRINTER_KEY = "arsan-etiket-yazici";
const TSPL_AGENT = "http://127.0.0.1:39201";
let labelList = [];
let labelResults = [];
let labelCatalog = null; // full active cylinder snapshot for category chips
let labelActiveCat = "";
let labelAgentOk = false;

const xmlEsc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

function labelSettings() {
  const num = (id, min, max, fallback) => { const v = Math.round(Number($(id).value)); return Number.isFinite(v) && v >= min && v <= max ? v : fallback; };
  return {
    w: num("lb-w", 20, 104, 50), h: num("lb-h", 10, 200, 30), copies: num("lb-copies", 1, 20, 1),
    title: $("lb-title").value.trim(), gas: $("lb-f-gas").checked, serial: $("lb-f-serial").checked,
    test: $("lb-f-test").checked, text: $("lb-f-text").checked
  };
}

function saveLabelSettings() {
  try { localStorage.setItem(LABEL_SETTINGS_KEY, JSON.stringify(labelSettings())); } catch { /* private mode */ }
}

function loadLabelSettings() {
  let s = null;
  try { s = JSON.parse(localStorage.getItem(LABEL_SETTINGS_KEY) || "null"); } catch { s = null; }
  if (!s) return;
  $("lb-w").value = s.w; $("lb-h").value = s.h; $("lb-copies").value = s.copies; $("lb-title").value = s.title ?? "";
  $("lb-f-gas").checked = !!s.gas; $("lb-f-serial").checked = !!s.serial; $("lb-f-test").checked = !!s.test; $("lb-f-text").checked = !!s.text;
  try {
    const printer = localStorage.getItem(LABEL_PRINTER_KEY);
    if (printer && $("lb-printer")) $("lb-printer").value = printer;
  } catch { /* private mode */ }
}

function saveLabelPrinter() {
  try { localStorage.setItem(LABEL_PRINTER_KEY, ($("lb-printer").value || "TSC TA310").trim()); } catch { /* private mode */ }
}

function tsplEsc(s) {
  return String(s ?? "").replace(/"/g, '\\["]');
}

function tsplMm(mm) { return Math.max(0, Math.round(mm * LABEL_DOTS_PER_MM)); }
function tsplPtDots(pt) { return Math.max(1, Math.round(pt * LABEL_DPI / 72)); }
function tsplTextWidth(text, pt) { return Math.ceil(String(text).length * tsplPtDots(pt) * 0.55); }

function tsplEstimateModules(code) {
  // UTF-8 byte length → rough QR version (ECC M) → module count. Printer encodes the real matrix.
  const n = new TextEncoder().encode(String(code || "")).length;
  const version = n <= 14 ? 1 : n <= 26 ? 2 : n <= 42 ? 3 : n <= 62 ? 4 : n <= 84 ? 5 : n <= 106 ? 6
    : n <= 122 ? 7 : n <= 152 ? 8 : n <= 180 ? 9 : n <= 213 ? 10 : n <= 251 ? 11 : n <= 287 ? 12 : 16;
  return 21 + (version - 1) * 4;
}

function tsplFitPt(text, widthDots, prefer, min) {
  const t = String(text || "").trim();
  if (!t) return prefer;
  let pt = prefer;
  while (pt > min && tsplTextWidth(t, pt) > widthDots) pt--;
  return pt;
}

function tsplWrap(text, widthDots, pt, maxLines) {
  const t = String(text || "").trim();
  if (!t) return [];
  const chars = Math.max(4, Math.floor(widthDots / (tsplPtDots(pt) * 0.55)));
  if (t.length <= chars || maxLines <= 1) return [t.length <= chars + 2 ? t : t.slice(0, chars)];
  const parts = t.split(/(\s+|·|-)/).filter((p) => p.length);
  const lines = [];
  let cur = "";
  for (const p of parts) {
    if ((cur + p).length <= chars || !cur) cur += p;
    else { lines.push(cur.trim()); cur = p.trimStart(); if (lines.length >= maxLines - 1) break; }
  }
  if (cur && lines.length < maxLines) lines.push(cur.trim());
  return lines.flatMap((line) => {
    if (line.length <= chars + 2) return [line];
    const out = [];
    for (let i = 0; i < line.length && out.length < maxLines; i += chars) out.push(line.slice(i, i + chars));
    return out;
  }).slice(0, maxLines);
}

function tsplTextCmd(x, y, pt, text) {
  return `TEXT ${x},${y},"0",0,${pt},${pt},"${tsplEsc(text)}"\r\n`;
}

function tsplWriteBlock(lines, x, y, widthDots, prefer, text, maxLines) {
  const pt = tsplFitPt(text, widthDots, prefer, 5);
  for (const line of tsplWrap(text, widthDots, pt, maxLines)) {
    lines.push(tsplTextCmd(x, y, pt, line));
    y += tsplPtDots(pt) + 3;
  }
  return y + 2;
}

// TSPL2 job: SIZE/GAP once, then CLS + QRCODE + TEXT + PRINT per label. TA310 draws the QR itself.
function buildTsplDocument(items, s) {
  const gapMm = 2;
  const lines = [
    `SIZE ${s.w} mm,${s.h} mm\r\n`,
    `GAP ${gapMm} mm,0\r\n`,
    `SPEED 4\r\n`,
    `DENSITY 8\r\n`,
    `DIRECTION 0\r\n`,
    `CODEPAGE UTF-8\r\n`
  ];
  for (const item of items) {
    const code = String(item.code || "").trim();
    if (!code) continue;
    const w = tsplMm(s.w), h = tsplMm(s.h);
    const pad = tsplMm(1.5), gap = tsplMm(2);
    const codeBand = s.text ? Math.max(tsplMm(2.8), Math.min(tsplMm(5.5), tsplMm(s.h * 0.16))) : 0;
    const qrSide = Math.max(tsplMm(8), Math.min(h - 2 * pad - codeBand, Math.floor(w * 0.42)));
    const modules = tsplEstimateModules(code) + 4;
    const cell = Math.max(1, Math.min(10, Math.floor(qrSide / modules)));
    const qrDots = (tsplEstimateModules(code) + 4) * cell;
    const qrX = pad;
    const qrY = pad + Math.max(0, Math.floor((h - 2 * pad - codeBand - qrDots) / 2));
    lines.push("CLS\r\n");
    lines.push(`QRCODE ${qrX},${qrY},M,${cell},A,0,M2,S7,"${tsplEsc(code)}"\r\n`);
    if (s.text) {
      let codeY = qrY + qrDots + tsplMm(0.4);
      const pt = tsplFitPt(code, qrDots, 7, 5);
      for (const line of tsplWrap(code, qrDots, pt, 2)) {
        const tw = tsplTextWidth(line, pt);
        const tx = qrX + Math.max(0, Math.floor((qrDots - tw) / 2));
        lines.push(tsplTextCmd(tx, codeY, pt, line));
        codeY += tsplPtDots(pt) + 2;
      }
    }
    const infoX = pad + qrDots + gap;
    const infoW = Math.max(tsplMm(12), w - pad - infoX);
    let y = pad + tsplMm(0.5);
    // Customer-owned: brand = owner (GÜNEŞ GAZ / KOYUNCU); company stock keeps Üst yazı (ARSAN GAZ).
    const brand = String(item.owner || s.title || "").trim();
    if (brand) y = tsplWriteBlock(lines, infoX, y, infoW, 11, brand, 1);
    if (s.gas && item.gas) y = tsplWriteBlock(lines, infoX, y, infoW, 9, String(item.gas), 2);
    if (s.serial && item.serial && item.serial !== item.code) y = tsplWriteBlock(lines, infoX, y, infoW, 7, String(item.serial), 1);
    if (s.test && item.stt) y = tsplWriteBlock(lines, infoX, y, infoW, 7, "STT: " + item.stt, 1);
    if (s.test && (item.gtt || item.test)) y = tsplWriteBlock(lines, infoX, y, infoW, 7, "GTT: " + (item.gtt || item.test), 1);
    if (item.extra) y = tsplWriteBlock(lines, infoX, y, infoW, 7, String(item.extra), 1);
    const ownerLine = String(item.owner || "").trim();
    if (ownerLine && ownerLine !== brand) tsplWriteBlock(lines, infoX, y, infoW, 7, ownerLine, 2);
    lines.push(`PRINT 1,${s.copies}\r\n`);
  }
  return lines.join("");
}

function labelTsplPayload(s) {
  return {
    items: labelList.map((i) => ({
      code: i.code, serial: i.serial || "", gas: i.gas || "", stt: i.stt || "",
      gtt: i.gtt || i.test || "", owner: i.owner || "", extra: i.extra || ""
    })),
    widthMm: s.w, heightMm: s.h, copies: s.copies, title: s.title,
    showGas: s.gas, showSerial: s.serial, showTest: s.test, showCodeText: s.text, gapMm: 2
  };
}

function downloadTsplFile(tspl) {
  const blob = new Blob([tspl], { type: "application/octet-stream" });
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = `arsan-etiket-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}.tspl`;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 2000);
}

async function refreshLabelAgentStatus() {
  const el = $("lb-agent-status");
  if (!el) return;
  try {
    const ctrl = new AbortController();
    const timer = setTimeout(() => ctrl.abort(), 800);
    const res = await fetch(TSPL_AGENT + "/health", { signal: ctrl.signal });
    clearTimeout(timer);
    if (!res.ok) throw new Error("ajan yanıt vermedi");
    const data = await res.json();
    labelAgentOk = true;
    el.textContent = `Yerel ajan hazır · ${(data.printer || "TSC TA310")}`;
    el.style.color = "var(--ok, #0a7a3e)";
    if (Array.isArray(data.printers) && data.printers.length && $("lb-printer")) {
      const cur = ($("lb-printer").value || "").trim();
      if (!cur || !data.printers.includes(cur)) {
        const prefer = data.printers.find((p) => /TA310|TSC/i.test(p)) || data.printers[0];
        if (prefer) $("lb-printer").value = prefer;
      }
    }
  } catch {
    labelAgentOk = false;
    el.textContent = "Yerel ajan yok — TsplPrintAgent.ps1 çalıştırın veya TSPL indirin";
    el.style.color = "var(--muted)";
  }
}

async function printViaTsplAgent(tspl, printer) {
  const res = await fetch(TSPL_AGENT + "/print", {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ printer, tspl })
  });
  const data = await res.json().catch(() => ({}));
  if (!res.ok || !data.ok) throw new Error(data.error || "Yerel ajan yazdıramadı");
  return data;
}

// Builds a QR matrix for the cylinder code (UTF-8 byte mode). Error M keeps modules larger on small labels.
function labelQrMatrix(code) {
  const text = String(code || "").trim();
  if (!text) return { error: "Barkod boş." };
  if (typeof qrcode !== "function") return { error: "Karekod kütüphanesi yüklenemedi; sayfayı yenileyin." };
  try {
    if (qrcode.stringToBytesFuncs && qrcode.stringToBytesFuncs["UTF-8"]) {
      qrcode.stringToBytes = qrcode.stringToBytesFuncs["UTF-8"];
    }
    const qr = qrcode(0, "M");
    qr.addData(text, "Byte");
    qr.make();
    const n = qr.getModuleCount();
    const cells = [];
    for (let r = 0; r < n; r++) {
      for (let c = 0; c < n; c++) if (qr.isDark(r, c)) cells.push([c, r]);
    }
    return { n, cells };
  } catch (error) {
    return { error: "Karekod oluşturulamadı: " + (error.message || error) };
  }
}

function labelBarcodeSvg(code, sizeMm) {
  const matrix = labelQrMatrix(code);
  if (matrix.error) return { error: matrix.error };
  const quiet = 2;
  const modules = matrix.n + 2 * quiet;
  const dots = Math.floor(sizeMm / (modules * LABEL_DOT_MM));
  if (dots < 1) return { error: `Karekod bu etikete sığmıyor (en az ${Math.ceil(modules * LABEL_DOT_MM)} mm kare gerekir).` };
  const mm = modules * dots * LABEL_DOT_MM;
  const rects = matrix.cells.map(([x, y]) => `<rect x="${x + quiet}" y="${y + quiet}" width="1" height="1"/>`).join("");
  return {
    svg: `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 ${modules} ${modules}" shape-rendering="crispEdges" style="width:${mm.toFixed(3)}mm;height:${mm.toFixed(3)}mm">${rects}</svg>`,
    sizeMm: mm,
    narrow: dots < 2
  };
}

// Scale font so text fits widthMm (approx. Latin/digit width ≈ 0.55×fontSize).
function labelFitFs(text, widthMm, prefer, min) {
  const t = String(text || "");
  if (!t) return prefer;
  return Math.max(min, Math.min(prefer, widthMm / (t.length * 0.55)));
}

// Wrap at spaces / · / - when a single line would be too long for the column.
function labelWrapLines(text, widthMm, fs, maxLines) {
  const t = String(text || "").trim();
  if (!t) return [];
  const chars = Math.max(6, Math.floor(widthMm / (fs * 0.55)));
  if (t.length <= chars) return [t];
  const parts = t.split(/(\s+|·|-)/).filter((p) => p.length);
  const lines = [];
  let cur = "";
  for (const p of parts) {
    if ((cur + p).length <= chars || !cur) cur += p;
    else { lines.push(cur.trim()); cur = p.trimStart(); if (lines.length >= maxLines - 1) break; }
  }
  if (cur && lines.length < maxLines) lines.push(cur.trim());
  else if (cur && lines.length) lines[lines.length - 1] = (lines[lines.length - 1] + cur).trim();
  // Hard-split leftover if still too long (GUID-like).
  return lines.flatMap((line) => {
    if (line.length <= chars + 2) return [line];
    const out = [];
    for (let i = 0; i < line.length && out.length < maxLines; i += chars) out.push(line.slice(i, i + chars));
    return out;
  }).slice(0, maxLines);
}

function labelInfoBlock(text, widthMm, prefer, min, maxLines, cls) {
  const t = String(text || "").trim();
  if (!t) return "";
  let fs = labelFitFs(t, widthMm, prefer, min);
  let lines = labelWrapLines(t, widthMm, fs, maxLines);
  // If wrapping still overflows a line, shrink further to the longest line.
  const longest = Math.max(...lines.map((l) => l.length), 1);
  fs = Math.max(min, Math.min(fs, widthMm / (longest * 0.55)));
  return `<div class="${cls}" style="font-size:${fs.toFixed(2)}mm">${lines.map((l) => `<div>${xmlEsc(l)}</div>`).join("")}</div>`;
}

// Fit the full barcode under the QR: scale font to column width; wrap long codes (e.g. GUID) to 2 lines.
function labelCodeUnderQrHtml(code, widthMm) {
  const text = String(code || "").trim();
  if (!text) return "";
  const prefer = 2.3;
  const fs = labelFitFs(text.length > 22 ? text.slice(0, Math.ceil(text.length / 2)) : text, widthMm, prefer, 1.05);
  const lines = labelWrapLines(text, widthMm, fs, 2);
  const longest = Math.max(...lines.map((l) => l.length), 1);
  const finalFs = Math.max(1.05, Math.min(prefer, widthMm / (longest * 0.62)));
  return `<div class="qr-code" style="width:${widthMm.toFixed(2)}mm;font-size:${finalFs.toFixed(2)}mm;line-height:1.12">`
    + lines.map((l) => `<div>${xmlEsc(l)}</div>`).join("")
    + `</div>`;
}

// Field sticker: left = QR + full barcode under it; right = brand / gas / serial / STT / GTT / owner (auto-fit, no …).
function labelHtml(item, s) {
  const pad = 1.5;
  const gap = 2;
  const codeH = s.text ? Math.min(5.5, Math.max(2.8, s.h * 0.16)) : 0;
  const qrSide = Math.max(8, Math.min(s.h - 2 * pad - codeH, s.w * 0.42));
  const qr = labelBarcodeSvg(item.code, qrSide);
  const qrW = qr.sizeMm || qrSide;
  const infoW = Math.max(12, s.w - 2 * pad - qrW - gap);
  const showCode = !!s.text;
  const serialLine = s.serial && item.serial && item.serial !== item.code ? String(item.serial) : "";
  const stt = s.test && item.stt ? "STT: " + item.stt : "";
  const gtt = s.test && (item.gtt || item.test) ? "GTT: " + (item.gtt || item.test) : "";
  // Prefer gas without repeating litre; capacity as its own line so each can shrink.
  // Customer-owned cylinders: brand = owner (like field stickers); company stock uses Üst yazı.
  const gasLine = s.gas && item.gas ? String(item.gas) : "";
  const capLine = item.extra || "";
  const brand = String(item.owner || s.title || "").trim();
  const ownerLine = String(item.owner || "").trim();
  const showOwnerAgain = ownerLine && ownerLine !== brand;
  const brandFs = Math.max(2.2, Math.min(4.0, s.h / 8));
  const gasFs = Math.max(2.0, Math.min(3.4, s.h / 9));
  const metaFs = Math.max(1.7, Math.min(2.8, s.h / 11));
  return `<div class="label">`
    + `<div class="qr-col">`
    + `<div class="qr">${qr.svg || `<b style="font-size:2mm">${xmlEsc(qr.error)}</b>`}</div>`
    + (showCode ? labelCodeUnderQrHtml(item.code, qrW) : "")
    + `</div>`
    + `<div class="info" style="width:${infoW.toFixed(2)}mm">`
    + (brand ? labelInfoBlock(brand, infoW, brandFs, 1.6, 1, "brand") : "")
    + (gasLine ? labelInfoBlock(gasLine, infoW, gasFs, 1.5, 2, "gas") : "")
    + (serialLine ? labelInfoBlock(serialLine, infoW, metaFs, 1.4, 1, "meta") : "")
    + (stt ? labelInfoBlock(stt, infoW, metaFs, 1.4, 1, "meta") : "")
    + (gtt ? labelInfoBlock(gtt, infoW, metaFs, 1.4, 1, "meta") : "")
    + (capLine ? labelInfoBlock(capLine, infoW, metaFs, 1.4, 1, "meta") : "")
    + (showOwnerAgain ? labelInfoBlock(ownerLine, infoW, metaFs, 1.35, 2, "meta") : "")
    + `</div></div>`;
}

function labelDocument(items, s) {
  const pages = items.flatMap((item) => Array(s.copies).fill(labelHtml(item, s))).join("");
  return `<!doctype html><html lang="tr"><head><meta charset="utf-8"><title>Karekod etiketleri (${items.length * s.copies})</title><style>
@page { size: ${s.w}mm ${s.h}mm; margin: 0; }
html, body { margin: 0; padding: 0; background: #fff; }
.label { width: ${s.w}mm; height: ${s.h}mm; box-sizing: border-box; padding: 1.5mm; overflow: hidden; display: flex; flex-direction: row;
  gap: 2mm; align-items: stretch; font-family: Arial, Helvetica, sans-serif; color: #000; page-break-after: always; break-after: page; }
.label:last-child { page-break-after: auto; break-after: auto; }
.qr-col { flex: none; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 0.5mm; max-width: 46%; }
.qr { display: flex; align-items: center; justify-content: center; }
.qr svg { display: block; }
.qr-code { text-align: center; font-weight: 700; font-family: Arial, Helvetica, sans-serif; letter-spacing: 0; overflow: hidden; }
.qr-code div { white-space: nowrap; overflow: hidden; }
.info { flex: 1; min-width: 0; display: flex; flex-direction: column; justify-content: center; gap: 0.35mm; overflow: hidden; }
.brand { font-weight: 700; line-height: 1.12; overflow: hidden; }
.brand div, .gas div, .meta div { white-space: nowrap; overflow: hidden; }
.gas { line-height: 1.12; overflow: hidden; }
.meta { line-height: 1.15; overflow: hidden; }
@media screen { body { background: #ccc; padding: 10px; } .label { background: #fff; margin: 0 auto 8px; outline: 1px dashed #888; } }
</style></head><body>${pages}</body></html>`;
}

function setLabelMode(mode) {
  for (const btn of document.querySelectorAll("[data-lb-mode]")) btn.classList.toggle("active", btn.dataset.lbMode === mode);
  for (const id of ["search", "new", "product"]) $("lb-panel-" + id).classList.toggle("on", id === mode);
  $("lb-step-1").classList.add("on");
}

function syncLabelPresets() {
  const w = Number($("lb-w").value), h = Number($("lb-h").value);
  for (const btn of $("lb-presets").querySelectorAll("button")) {
    btn.classList.toggle("active", Number(btn.dataset.w) === w && Number(btn.dataset.h) === h);
  }
}

function updateLabelSteps() {
  $("lb-step-2").classList.toggle("on", labelList.length > 0);
  $("lb-step-3").classList.toggle("on", labelList.length > 0);
}

function refreshLabelPreviews() {
  const s = labelSettings();
  const pill = $("lb-count-text");
  if (labelList.length) {
    pill.textContent = `${labelList.length} barkod × ${s.copies} = ${labelList.length * s.copies} etiket`;
    pill.classList.remove("hidden");
  } else {
    pill.classList.add("hidden");
  }
  updateLabelSteps();
  syncLabelPresets();
  const previews = $("lb-previews");
  if (!labelList.length) {
    previews.replaceChildren(el("div", { class: "lb-empty" },
      el("div", { class: "big" }, "👁️"),
      el("div", {}, "Önizleme burada görünecek")));
    return;
  }
  const scale = Math.min(2.4, Math.max(1.15, 160 / Math.max(s.w, s.h)));
  previews.replaceChildren(...labelList.slice(0, 12).map((item) => {
    const check = labelBarcodeSvg(item.code, Math.max(8, Math.min(s.h - 3, s.w * 0.42)));
    const shell = el("div", { style: `width:${(s.w * scale).toFixed(2)}mm;height:${(s.h * scale).toFixed(2)}mm;position:relative;flex:none` });
    const card = el("div", { class: "lb-preview-card" + (check.error ? " bad" : check.narrow ? " warn" : ""),
      style: `width:${s.w}mm;height:${s.h}mm;transform:scale(${scale.toFixed(3)});transform-origin:top left;position:absolute;left:0;top:0` });
    card.innerHTML = labelHtml(item, s);
    const inner = card.firstElementChild;
    if (inner) {
      inner.style.width = s.w + "mm";
      inner.style.height = s.h + "mm";
      inner.style.pageBreakAfter = "auto";
      inner.style.margin = "0";
      inner.style.outline = "none";
    }
    shell.append(card);
    return shell;
  }));
  if (labelList.length > 12) {
    previews.append(el("div", { class: "hint" }, `+${labelList.length - 12} etiket daha (yazdırmada hepsi basılır)`));
  }
}

function renderLabelList() {
  const s = labelSettings();
  const list = $("lb-list");
  if (!labelList.length) {
    list.replaceChildren(el("div", { class: "lb-empty" },
      el("div", { class: "big" }, "🏷️"),
      el("div", {}, "Liste boş"),
      el("div", { class: "hint" }, "Yukarıdan kategori seçin, ara veya yeni numara oluşturun.")));
    refreshLabelPreviews();
    return;
  }
  list.replaceChildren(...labelList.map((item, index) => {
    const check = labelBarcodeSvg(item.code, Math.max(8, Math.min(s.h - 3, s.w * 0.42)));
    const field = (key, label, cls) => {
      const input = el("input", { value: item[key] || "", class: cls || "" });
      input.addEventListener("input", () => { item[key] = input.value; refreshLabelPreviews(); });
      input.addEventListener("change", () => { item[key] = input.value.trim(); refreshLabelPreviews(); });
      return el("label", {}, label, input);
    };
    const warn = check.error
      ? el("div", { class: "err" }, check.error)
      : check.narrow ? el("div", { class: "err" }, "Karekod noktaları ince — daha büyük etiket deneyin") : null;
    return el("div", { class: "lb-chip" },
      el("div", { class: "lb-chip-top" },
        el("div", { style: "flex:1;font-size:12px;color:var(--muted)" }, item.source || "Etiket"),
        el("button", { class: "secondary small", type: "button", title: "Ürün olarak kaydet",
          onclick: () => openTemplateDialog(null, { sample: item.code, gas: item.gas, test: item.test }) }, "Ürün"),
        el("button", { class: "secondary small", type: "button", onclick: () => { labelList.splice(index, 1); renderLabelList(); } }, "Çıkar")),
      el("div", { class: "fields" },
        field("code", "Barkod no", "code"),
        field("serial", "Seri no"),
        field("gas", "Kategori (gaz / litre)"),
        field("stt", "STT (son test)"),
        field("gtt", "GTT (gelecek test)"),
        field("owner", "Ürün sahibi"),
        field("extra", "Hacim / basınç (ör. 10.6M3 · 200BAR)")),
      warn);
  }));
  refreshLabelPreviews();
}

function printLabelDocument(html) {
  const frame = $("lb-print-frame");
  const doc = frame.contentDocument || frame.contentWindow.document;
  doc.open();
  doc.write(html);
  doc.close();
  const run = () => {
    try {
      frame.contentWindow.focus();
      frame.contentWindow.print();
    } catch (error) {
      show($("lb-msg"), "Yazdırma başlatılamadı: " + (error.message || error));
    }
  };
  // Wait for SVG layout in the iframe before opening the print dialog.
  setTimeout(run, 350);
}

function addLabels(items) {
  const known = new Set(labelList.map((i) => i.code.toUpperCase()));
  let added = 0;
  for (const item of items) {
    if (!item.code || known.has(item.code.toUpperCase())) continue;
    known.add(item.code.toUpperCase());
    labelList.push(item);
    added++;
  }
  renderLabelList();
  return added;
}

function labelCylinderKey(c) {
  return `${c.gasType || "?"} · ${c.capacity || "?"}`.trim();
}

function labelCylinderItem(c) {
  // STT/GTT naming matches the legacy print dialog and field cylinder stickers.
  // Keep gas type and capacity separate so each line can shrink/wrap without "…".
  const gtt = c.testDue || "";
  return {
    code: (c.barcode || c.serialNumber || "").trim(),
    gas: (c.gasType || "").trim(),
    serial: c.serialNumber || "",
    test: gtt,
    stt: c.lastTest || "",
    gtt,
    owner: c.owner || "",
    extra: c.capacity || "",
    source: "Kayıtlı tüp"
  };
}

function hideLabelResults() {
  labelResults = [];
  labelActiveCat = "";
  $("lb-results").replaceChildren();
  $("lb-results-box").classList.add("hidden");
  for (const btn of $("lb-cats").querySelectorAll(".lb-cat")) btn.classList.remove("active");
}

function showLabelResults(rows, title) {
  labelResults = rows;
  $("lb-results-title").textContent = title || `${rows.length} tüp`;
  const all = el("input", { type: "checkbox", title: "Tümünü seç" });
  all.addEventListener("change", () => { for (const c of $("lb-results").querySelectorAll("input[data-i]")) c.checked = all.checked; });
  const head = el("tr", {}, el("th", {}, all), ...["Barkod", "Seri no", "Gaz / litre", "Test", "Durum / yer"].map((h) => el("th", {}, h)));
  const body = rows.map((c, i) => el("tr", {},
    el("td", {}, el("input", { type: "checkbox", "data-i": i })),
    el("td", {}, c.barcode || c.serialNumber || ""),
    el("td", {}, c.serialNumber || ""),
    el("td", {}, labelCylinderKey(c)),
    el("td", {}, c.testDue || ""),
    el("td", {}, c.customer || c.plate || c.statusName || "")));
  $("lb-results").replaceChildren(head, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6 }, "Kayıt bulunamadı."))]));
  $("lb-results-box").classList.remove("hidden");
}

function renderLabelCategories() {
  const cats = $("lb-cats");
  if (!labelCatalog) {
    cats.replaceChildren(el("div", { class: "hint" }, "Kategoriler yükleniyor…"));
    return;
  }
  const groups = new Map();
  for (const c of labelCatalog) {
    const key = labelCylinderKey(c);
    if (!groups.has(key)) groups.set(key, []);
    groups.get(key).push(c);
  }
  const keys = [...groups.keys()].sort((a, b) => a.localeCompare(b, "tr"));
  if (!keys.length) {
    cats.replaceChildren(el("div", { class: "hint" }, "Aktif tüp yok."));
    return;
  }
  cats.replaceChildren(...keys.map((key) => {
    const n = groups.get(key).length;
    const btn = el("button", { type: "button", class: "lb-cat" + (labelActiveCat === key ? " active" : "") },
      el("b", {}, key), el("small", {}, `${n} tüp · dokunun`));
    btn.addEventListener("click", () => {
      labelActiveCat = key;
      $("lb-q").value = "";
      showLabelResults(groups.get(key), key + ` · ${n} tüp`);
      renderLabelCategories();
    });
    return btn;
  }));
}

async function ensureLabelCatalog() {
  if (labelCatalog) {
    renderLabelCategories();
    return;
  }
  try {
    labelCatalog = await api("/panel/api/tupler");
    renderLabelCategories();
  } catch (error) {
    $("lb-cats").replaceChildren(el("div", { class: "hint" }, "Kategoriler alınamadı: " + error.message));
  }
}

async function searchLabelCylinders() {
  const term = $("lb-q").value.trim();
  if (!term) {
    hideLabelResults();
    show($("lb-msg"), "Boş arama tüm tüpleri açmaz. Gaz/litre kategorisine dokunun veya seri-barkod yazın.", "warn");
    await ensureLabelCatalog();
    return;
  }
  const ticket = newRequest("labels");
  try {
    const rows = await api("/panel/api/tupler?q=" + encodeURIComponent(term));
    if (isStale("labels", ticket)) return;
    labelActiveCat = "";
    showLabelResults(rows, `"${term}" · ${rows.length} sonuç`);
    renderLabelCategories();
    hide($("lb-msg"));
  } catch (error) {
    show($("lb-msg"), error.message);
  }
}

function addSelectedLabelCylinders() {
  const picked = [...$("lb-results").querySelectorAll("input[data-i]:checked")]
    .map((c) => labelResults[Number(c.dataset.i)])
    .filter(Boolean);
  if (!picked.length) {
    show($("lb-msg"), "Önce listeden en az bir tüp işaretleyin.", "warn");
    return;
  }
  const items = picked.map(labelCylinderItem).filter((i) => i.code);
  if (!items.length) {
    show($("lb-msg"), "Seçilen tüplerde barkod/seri no yok.", "warn");
    return;
  }
  const added = addLabels(items);
  hideLabelResults();
  show($("lb-msg"),
    added
      ? `${added} tüp yazdırma listesine eklendi. Aşağıda bilgileri düzenleyebilirsiniz.`
        + (added < items.length ? ` (${items.length - added} zaten listedeydi.)` : "")
      : "Seçilenler zaten yazdırma listesinde.",
    added ? "ok" : "warn");
  $("lb-list").scrollIntoView({ behavior: "smooth", block: "nearest" });
}

let labelTemplates = [];
let editingTemplate = null;
const LABEL_TEMPLATE_KEY = "arsan-etiket-urun";

function selectedTemplate() {
  return labelTemplates.find((t) => String(t.id) === $("lt-select").value) || null;
}

function renderTemplateInfo() {
  const t = selectedTemplate();
  $("lt-edit").disabled = $("lt-delete").disabled = $("lt-next").disabled = $("lt-generate").disabled = !t;
  if (!t) {
    $("lt-info").textContent = labelTemplates.length ? "" : "Henüz kayıtlı ürün yok. \"Yeni ürün kaydet\" ile veya listedeki bir etiketten kaydedin.";
    return;
  }
  const format = `${t.prefix}${"0".repeat(Math.max(0, t.digits - 1))}1`;
  $("lt-info").textContent = `Barkod biçimi: ${t.prefix || "(öneksiz)"} + ${t.digits} basamak (ör. ${format})`
    + (t.gasText ? ` · Etikette: ${t.gasText}` : "") + (t.testText ? ` · Test: ${t.testText}` : "")
    + ` · Etiket ${t.widthMm}x${t.heightMm} mm, ${t.copies} kopya` + (t.sampleCode ? ` · Örnek: ${t.sampleCode}` : "")
    + ` · Son değişiklik: ${t.updated} ${t.updatedBy}`;
}

function renderTemplates(selectId) {
  const keep = selectId ?? $("lt-select").value ?? localStorage.getItem(LABEL_TEMPLATE_KEY);
  $("lt-select").replaceChildren(el("option", { value: "" }, labelTemplates.length ? "Ürün seçin…" : "Kayıtlı ürün yok"),
    ...labelTemplates.map((t) => el("option", { value: String(t.id) }, t.gasText && t.gasText !== t.name ? `${t.name} (${t.gasText})` : t.name)));
  if (keep && labelTemplates.some((t) => String(t.id) === String(keep))) $("lt-select").value = String(keep);
  renderTemplateInfo();
}

async function loadLabelTemplates(selectId) {
  try {
    labelTemplates = await api("/panel/api/etiket/urunler");
    renderTemplates(selectId ?? localStorage.getItem(LABEL_TEMPLATE_KEY));
  } catch (error) {
    show($("lb-msg"), error.message);
  }
}

function applyTemplateSettings(t) {
  $("lb-w").value = t.widthMm; $("lb-h").value = t.heightMm; $("lb-copies").value = t.copies; $("lb-title").value = t.title || "";
  $("lb-f-gas").checked = t.showGas; $("lb-f-serial").checked = t.showSerial; $("lb-f-test").checked = t.showTest; $("lb-f-text").checked = t.showText;
  saveLabelSettings();
}

function openTemplateDialog(t, preset = {}) {
  editingTemplate = t;
  hide($("ltd-msg"));
  $("ltd-title").textContent = t ? `Ürünü düzenle: ${t.name}` : "Ürün kaydet";
  $("ltd-name").value = t ? t.name : preset.gas || "";
  $("ltd-sample").value = t ? t.sampleCode || "" : preset.sample || "";
  $("ltd-prefix").value = t ? t.prefix : "";
  $("ltd-digits").value = t ? t.digits : 6;
  $("ltd-gas").value = t ? t.gasText : preset.gas || "";
  $("ltd-test").value = t ? t.testText || "" : preset.test || "";
  $("label-template-dialog").showModal();
  $("ltd-name").focus();
}

async function saveTemplate() {
  const s = labelSettings();
  try {
    const saved = await api("/panel/api/etiket/urunler", { method: "POST", body: {
      id: editingTemplate ? editingTemplate.id : null, name: $("ltd-name").value, sampleCode: $("ltd-sample").value,
      prefix: $("ltd-prefix").value, digits: Number($("ltd-digits").value), gasText: $("ltd-gas").value, testText: $("ltd-test").value,
      title: s.title, widthMm: s.w, heightMm: s.h, copies: s.copies, showGas: s.gas, showSerial: s.serial, showTest: s.test, showText: s.text
    } });
    $("label-template-dialog").close();
    try { localStorage.setItem(LABEL_TEMPLATE_KEY, String(saved.id)); } catch { /* private mode */ }
    await loadLabelTemplates(saved.id);
    show($("lb-msg"), `"${saved.name}" ürünü kaydedildi. Seri numarasını yazıp "Barkod oluştur" ile yeni etiket ekleyebilirsiniz.`, "ok");
  } catch (error) {
    show($("ltd-msg"), error.message);
  }
}

async function generateFromTemplate() {
  const t = selectedTemplate();
  if (!t) { show($("lb-msg"), "Önce bir ürün seçin.", "warn"); return; }
  if (!$("lt-serial").value.trim()) { show($("lb-msg"), "Seri numarasını yazın (ör. 124) veya \"Sıradaki boş no\"ya basın.", "warn"); $("lt-serial").focus(); return; }
  try {
    const result = await api(`/panel/api/etiket/urunler/${t.id}/olustur`, { method: "POST", body: { serial: $("lt-serial").value, count: Number($("lt-count").value) || 1 } });
    applyTemplateSettings(result.template);
    const free = result.codes.filter((c) => !c.usedBy);
    const used = result.codes.filter((c) => c.usedBy);
    const added = addLabels(free.map((c) => ({ code: c.code, gas: result.template.gasText, serial: "", test: result.template.testText || "", source: "Ürün: " + result.template.name })));
    const last = result.codes[result.codes.length - 1].code;
    const lastNumber = Number(last.slice(result.template.prefix.length));
    if (Number.isSafeInteger(lastNumber)) $("lt-serial").value = String(lastNumber + 1);
    const usedText = used.length ? ` Bir tüpte kayıtlı olduğu için eklenmeyen ${used.length} barkod: ${used.slice(0, 5).map((c) => `${c.code} (${c.usedBy})`).join(", ")}${used.length > 5 ? "…" : ""}` : "";
    show($("lb-msg"), `${added} barkod listeye eklendi (${result.template.name}). Seri no kutusuna sıradaki numara yazıldı.${usedText}`, used.length || !added ? "warn" : "ok");
  } catch (error) {
    show($("lb-msg"), error.message);
  }
}

function initLabelTemplates() {
  $("lt-select").addEventListener("change", () => {
    try { localStorage.setItem(LABEL_TEMPLATE_KEY, $("lt-select").value); } catch { /* private mode */ }
    const t = selectedTemplate();
    if (t) applyTemplateSettings(t);
    renderTemplateInfo();
    renderLabelList();
  });
  $("lt-new").addEventListener("click", () => openTemplateDialog(null));
  $("lt-edit").addEventListener("click", () => { const t = selectedTemplate(); if (t) openTemplateDialog(t); });
  $("lt-delete").addEventListener("click", async () => {
    const t = selectedTemplate();
    if (!t || !window.confirm(`"${t.name}" ürünü silinsin mi? Yalnızca kayıtlı ürün şablonu silinir; tüpler ve basılmış etiketler etkilenmez.`)) return;
    try {
      const result = await api(`/panel/api/etiket/urunler/${t.id}/sil`, { method: "POST" });
      await loadLabelTemplates("");
      show($("lb-msg"), result.message, "ok");
    } catch (error) { show($("lb-msg"), error.message); }
  });
  $("lt-next").addEventListener("click", async () => {
    const t = selectedTemplate();
    if (!t) return;
    try {
      const result = await api("/panel/api/etiket/sonraki?onek=" + encodeURIComponent(t.prefix));
      $("lt-serial").value = result.next;
      hide($("lb-msg"));
    } catch (error) { show($("lb-msg"), error.message); }
  });
  $("lt-generate").addEventListener("click", generateFromTemplate);
  $("lt-serial").addEventListener("keydown", (e) => { if (e.key === "Enter") generateFromTemplate(); });
  $("ltd-close").addEventListener("click", () => $("label-template-dialog").close());
  $("ltd-save").addEventListener("click", saveTemplate);
  $("ltd-sample").addEventListener("input", () => {
    const m = /^(.*?)(\d+)$/.exec($("ltd-sample").value.trim().toUpperCase());
    if (m) { $("ltd-prefix").value = m[1]; $("ltd-digits").value = m[2].length; }
  });
  renderTemplates();
}

function initLabels() {
  loadLabelSettings();
  initLabelTemplates();
  setLabelMode("search");
  for (const btn of document.querySelectorAll("[data-lb-mode]")) {
    btn.addEventListener("click", () => setLabelMode(btn.dataset.lbMode));
  }
  for (const btn of $("lb-presets").querySelectorAll("button")) {
    btn.addEventListener("click", () => {
      $("lb-w").value = btn.dataset.w;
      $("lb-h").value = btn.dataset.h;
      saveLabelSettings();
      renderLabelList();
    });
  }
  for (const id of ["lb-w", "lb-h", "lb-copies", "lb-title", "lb-f-gas", "lb-f-serial", "lb-f-test", "lb-f-text"]) {
    $(id).addEventListener("change", () => { saveLabelSettings(); refreshLabelPreviews(); });
    $(id).addEventListener("input", () => { if (id === "lb-w" || id === "lb-h" || id === "lb-title") refreshLabelPreviews(); });
  }
  $("lb-search").addEventListener("click", searchLabelCylinders);
  $("lb-q").addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); searchLabelCylinders(); } });
  $("lb-add-selected").addEventListener("click", addSelectedLabelCylinders);
  $("lb-results-close").addEventListener("click", () => { hideLabelResults(); hide($("lb-msg")); });
  // Load litre/gas categories when the search mode is shown (not a full dump into the table).
  const searchMode = document.querySelector('[data-lb-mode="search"]');
  if (searchMode) {
    searchMode.addEventListener("click", () => { void ensureLabelCatalog(); });
  }
  void ensureLabelCatalog();
  $("lb-next").addEventListener("click", async () => {
    try {
      const result = await api("/panel/api/etiket/sonraki?onek=" + encodeURIComponent($("lb-prefix").value.trim()));
      $("lb-start").value = result.next;
      hide($("lb-msg"));
    } catch (error) { show($("lb-msg"), error.message); }
  });
  $("lb-generate").addEventListener("click", async () => {
    try {
      const result = await api("/panel/api/etiket/olustur", { method: "POST", body: {
        prefix: $("lb-prefix").value.trim(), start: Number($("lb-start").value), count: Number($("lb-count").value), digits: Number($("lb-digits").value)
      } });
      const free = result.codes.filter((c) => !c.usedBy);
      const used = result.codes.filter((c) => c.usedBy);
      const added = addLabels(free.map((c) => ({ code: c.code, gas: "", serial: "", test: "", source: "Yeni numara" })));
      const usedText = used.length ? ` Kayıtlı olduğu için eklenmeyen ${used.length} numara: ${used.slice(0, 5).map((c) => `${c.code} (${c.usedBy})`).join(", ")}${used.length > 5 ? "…" : ""}` : "";
      show($("lb-msg"), `${added} yeni numara listeye eklendi.${usedText}`, used.length || !added ? "warn" : "ok");
    } catch (error) { show($("lb-msg"), error.message); }
  });
  $("lb-clear").addEventListener("click", () => { labelList = []; renderLabelList(); });
  if ($("lb-printer")) {
    $("lb-printer").addEventListener("change", saveLabelPrinter);
    $("lb-printer").addEventListener("blur", saveLabelPrinter);
  }
  const ensurePrintable = () => {
    const s = labelSettings();
    if (!labelList.length) { show($("lb-msg"), "Yazdırılacak etiket yok. Önce barkod ekleyin.", "warn"); return null; }
    const bad = labelList.filter((i) => labelBarcodeSvg(i.code, Math.max(8, Math.min(s.h - 3, s.w * 0.42))).error);
    if (bad.length) {
      show($("lb-msg"), `Basılamayan barkod var: ${bad.map((i) => i.code).join(", ")}. Listeden çıkarın veya etiket genişliğini artırın.`);
      return null;
    }
    return s;
  };
  $("lb-print-tspl").addEventListener("click", async () => {
    const s = ensurePrintable();
    if (!s) return;
    saveLabelSettings();
    saveLabelPrinter();
    const printer = ($("lb-printer").value || "TSC TA310").trim();
    const tspl = buildTsplDocument(labelList, s);
    try {
      await refreshLabelAgentStatus();
      if (labelAgentOk) {
        await printViaTsplAgent(tspl, printer);
        show($("lb-msg"), `${labelList.length * s.copies} etiket TSPL ile ${printer} yazıcısına gönderildi. Karekodu yazıcı kendi üretti.`, "ok");
      } else {
        // API may be on Windows (dev); try server RAW, else download.
        try {
          await api("/panel/api/etiket/tspl", { method: "POST", body: { ...labelTsplPayload(s), printer, send: true } });
          show($("lb-msg"), `${labelList.length * s.copies} etiket sunucu üzerinden ${printer} yazıcısına gönderildi (TSPL RAW).`, "ok");
        } catch {
          downloadTsplFile(tspl);
          show($("lb-msg"), `Yerel ajan yok — TSPL indirildi. TsplPrintAgent.ps1 çalıştırıp tekrar "TA310'a bas" deyin, veya .tspl dosyasını Diagnostic Tool / copy /b ile gönderin.`, "warn");
        }
      }
      try {
        await api("/panel/api/etiket/yazdirildi", { method: "POST", body: {
          labels: labelList.length * s.copies, codes: labelList.map((i) => i.code),
          size: `${s.w}x${s.h} mm`, mode: "TSPL"
        } });
      } catch { /* audit best effort */ }
    } catch (error) {
      downloadTsplFile(tspl);
      show($("lb-msg"), `TSPL gönderilemedi (${error.message || error}). Dosya indirildi — ajanı kontrol edin.`, "warn");
    }
  });
  $("lb-download-tspl").addEventListener("click", async () => {
    const s = ensurePrintable();
    if (!s) return;
    downloadTsplFile(buildTsplDocument(labelList, s));
    show($("lb-msg"), "TSPL dosyası indirildi. TA310 bu komutlarla karekodu kendi çizer.", "ok");
  });
  $("lb-print").addEventListener("click", async () => {
    const s = ensurePrintable();
    if (!s) return;
    printLabelDocument(labelDocument(labelList, s));
    show($("lb-msg"), `${labelList.length * s.copies} etiket tarayıcı yazdırmasına gitti (yedek yol — Windows resmi rasterler). Tercihen "TA310'a bas (TSPL)" kullanın.`, "warn");
    try {
      await api("/panel/api/etiket/yazdirildi", { method: "POST", body: {
        labels: labelList.length * s.copies, codes: labelList.map((i) => i.code),
        size: `${s.w}x${s.h} mm`, mode: "tarayıcı"
      } });
    } catch { /* the audit entry is best effort */ }
  });
  void refreshLabelAgentStatus();
  setInterval(() => { void refreshLabelAgentStatus(); }, 15000);
  renderLabelList();
}

initLabels();

// ---- Yalçın partner workspace -------------------------------------------------------------------
const ycSelected = new Set();
/** @type {Map<number, {id:number,name:string,code:string,city?:string,cylinders?:number}>} */
const ycAdaySelected = new Map();

function renderYalcinSelected() {
  const bar = $("yc-secilenler");
  if (!bar) return;
  if (ycAdaySelected.size === 0) {
    bar.replaceChildren(el("span", { class: "sel-label" }, "Seçilenler"), el("span", { class: "sel-empty" }, "Henüz yok — aşağıdaki listeden işaretleyin."));
    return;
  }
  const chips = [...ycAdaySelected.values()].map((c) => el("span", { class: "sel-chip" },
    el("span", {}, c.name),
    el("button", {
      type: "button",
      title: "Seçimden çıkar",
      onclick: (e) => { e.stopPropagation(); ycAdaySelected.delete(c.id); renderYalcinSelected(); loadYalcinAdaylar().catch(() => {}); }
    }, "×")
  ));
  bar.replaceChildren(el("span", { class: "sel-label" }, `Seçilenler (${ycAdaySelected.size})`), ...chips);
}

function ycToggleAday(c, on) {
  if (on) ycAdaySelected.set(c.id, { id: c.id, name: c.name, code: c.code, city: c.city, cylinders: c.cylinders });
  else ycAdaySelected.delete(c.id);
  renderYalcinSelected();
}

function ycScroll(id) {
  const node = $(id);
  if (node) node.scrollIntoView({ behavior: "smooth", block: "start" });
}

async function loadYalcin() {
  hide($("yc-msg"));
  try {
    const ozet = await api("/panel/api/yalcin/ozet");
    $("yc-stats").replaceChildren(
      el("div", {
        class: "stat", title: "Tüm Yalçın müşterilerine git",
        onclick: () => { ycScroll("yc-stok-card"); void loadYalcinStock(); }
      }, el("div", { class: "muted" }, "Müşteri"), el("div", { class: "n" }, String(ozet.customers)), el("div", { class: "s" }, "Stoğu olan + olmayan")),
      el("div", {
        class: "stat", title: "Stoğu olan müşteriler",
        onclick: () => { ycScroll("yc-stok-card"); void loadYalcinStock(); }
      }, el("div", { class: "muted" }, "Müşterideki tüp"), el("div", { class: "n" }, String(ozet.cylindersAtCustomer)), el("div", { class: "s" }, "Listeye kaydır")),
      el("div", {
        class: "stat", title: "Son hareketler",
        onclick: () => { ycScroll("yc-moves-card"); void loadYalcinMoves(); }
      }, el("div", { class: "muted" }, "Son 7 gün hareket"), el("div", { class: "n" }, String(ozet.movementsLast7Days)), el("div", { class: "s" }, "Hareketlere git"))
    );
    await Promise.all([loadYalcinStock(), loadYalcinMoves()]);
    if (can("yalcin-yonet")) await loadYalcinAdaylar();
  } catch (error) {
    show($("yc-msg"), error.message);
  }
}

async function loadYalcinStock() {
  const q = new URLSearchParams();
  if ($("yc-q").value.trim()) q.set("q", $("yc-q").value.trim());
  const data = await api("/panel/api/yalcin/musteri-stok?" + q);
  const withN = data.withStock ?? (data.rows || []).length;
  const withoutN = data.withoutStockCount ?? (data.withoutStock || []).length;
  $("yc-count").textContent = `Stoğu olan: ${withN} müşteri` + (data.cylinders != null ? ` · ${data.cylinders} tüp` : "");
  const head = el("tr", {}, ...["", "Müşteri", "Kod", "Şehir", "Tüp", "Dolu/Boş", "Max gün", "Gazlar"].map((h) => el("th", {}, h)));
  const body = (data.rows || []).map((r) => {
    const cb = can("yalcin-yonet")
      ? el("input", { type: "checkbox", checked: ycSelected.has(r.id), onchange: (e) => { if (e.target.checked) ycSelected.add(r.id); else ycSelected.delete(r.id); } })
      : el("span", {}, "");
    return el("tr", { class: "click", onclick: (ev) => { if (ev.target.tagName === "INPUT") return; void openStockDialog(r.id); } },
      el("td", {}, cb),
      el("td", {}, el("b", {}, r.name)),
      el("td", { class: "muted" }, r.code),
      el("td", {}, r.city || "-"),
      el("td", {}, String(r.count)),
      el("td", {}, `${r.full}/${r.empty}`),
      el("td", {}, r.maxDays == null ? "-" : String(r.maxDays)),
      el("td", { class: "muted" }, r.gases || "-"));
  });
  $("yc-stok").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 8, class: "muted" }, "Stoğu olan Yalçın müşterisi yok."))])));

  const empty = data.withoutStock || [];
  $("yc-empty-count").textContent = empty.length ? `${empty.length} müşteri (tüpsüz)` : "Tüpsüz atanan yok.";
  const eHead = el("tr", {}, ...["", "Müşteri", "Kod", "Şehir", "Telefon"].map((h) => el("th", {}, h)));
  const eBody = empty.map((r) => {
    const cb = can("yalcin-yonet")
      ? el("input", { type: "checkbox", checked: ycSelected.has(r.id), onchange: (e) => { if (e.target.checked) ycSelected.add(r.id); else ycSelected.delete(r.id); } })
      : el("span", {}, "");
    return el("tr", { class: "click", onclick: (ev) => { if (ev.target.tagName === "INPUT") return; void openStockDialog(r.id); } },
      el("td", {}, cb),
      el("td", {}, el("b", {}, r.name)),
      el("td", { class: "muted" }, r.code),
      el("td", {}, r.city || "-"),
      el("td", {}, r.phone || "-"));
  });
  $("yc-stok-empty").replaceChildren(el("thead", {}, eHead), el("tbody", {}, ...(eBody.length ? eBody : [el("tr", {}, el("td", { colspan: 5, class: "muted" }, "—"))])));
}

async function loadYalcinMoves() {
  const q = new URLSearchParams({ take: "80" });
  if ($("yc-hq").value.trim()) q.set("q", $("yc-hq").value.trim());
  const data = await api("/panel/api/yalcin/hareketler?" + q);
  const head = el("tr", {}, ...["Tarih", "Seri", "Müşteri", "Tür", "Plaka"].map((h) => el("th", {}, h)));
  const body = (data.rows || []).map((r) => el("tr", {},
    el("td", {}, r.date), el("td", {}, r.serial), el("td", {}, r.customer),
    el("td", {}, r.movementType || "-"), el("td", { class: "muted" }, r.vehiclePlate || "-")));
  $("yc-moves").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 5, class: "muted" }, "Hareket yok."))])));
}

async function loadYalcinAdaylar() {
  renderYalcinSelected();
  const term = ($("yc-aday-q")?.value || "").trim();
  if (term.length === 0) {
    $("yc-adaylar").replaceChildren(el("thead", {}, el("tr", {}, ...["", "Müşteri", "Kod", "Şehir", "Tüp"].map((h) => el("th", {}, h)))),
      el("tbody", {}, el("tr", {}, el("td", { colspan: 5, class: "muted" }, "Aktarmak için müşteri adı veya kod yazın — sonuçlar yazdıkça gelir. Seçilenler üstte kalır."))));
    return;
  }
  const q = new URLSearchParams({ take: "60", q: term });
  const rows = await api("/panel/api/yalcin/adaylar?" + q);
  const head = el("tr", {}, ...["", "Müşteri", "Kod", "Şehir", "Tüp"].map((h) => el("th", {}, h)));
  const body = rows.map((c) => {
    const cb = el("input", {
      type: "checkbox",
      checked: ycAdaySelected.has(c.id),
      onchange: (e) => { ycToggleAday(c, e.target.checked); }
    });
    return el("tr", {
      class: "click" + (ycAdaySelected.has(c.id) ? " selected" : ""),
      onclick: (ev) => {
        if (ev.target.tagName === "INPUT") return;
        const on = !ycAdaySelected.has(c.id);
        ycToggleAday(c, on);
        const box = ev.currentTarget.querySelector("input[type=checkbox]");
        if (box) box.checked = on;
        ev.currentTarget.classList.toggle("selected", on);
      }
    }, el("td", {}, cb), el("td", {}, el("b", {}, c.name)), el("td", { class: "muted" }, c.code),
      el("td", {}, c.city || "-"), el("td", {}, String(c.cylinders ?? 0)));
  });
  $("yc-adaylar").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 5, class: "muted" }, `"${term}" için müşteri bulunamadı.`))])));
}

$("yc-ara")?.addEventListener("click", () => loadYalcinStock().catch((e) => show($("yc-msg"), e.message)));
$("yc-hareket")?.addEventListener("click", () => loadYalcinMoves().catch((e) => show($("yc-msg"), e.message)));
$("yc-aday-ara")?.addEventListener("click", () => loadYalcinAdaylar().catch((e) => show($("yc-msg"), e.message)));
liveFilter(["yc-q"], [], () => loadYalcinStock());
liveFilter(["yc-hq"], [], () => loadYalcinMoves());
liveFilter(["yc-aday-q"], [], () => loadYalcinAdaylar());
$("yc-ata")?.addEventListener("click", async () => {
  const ids = [...ycAdaySelected.keys()];
  if (!ids.length) { show($("yc-msg"), "Önce listeden müşteri seçin — seçilenler arama kutusunun altında görünür."); return; }
  try {
    const r = await api("/panel/api/yalcin/ata", { method: "POST", body: { customerIds: ids, assign: true } });
    ycAdaySelected.clear();
    renderYalcinSelected();
    show($("yc-msg"), r.message, "ok");
    $("yc-aday-q").value = "";
    await loadYalcin();
  } catch (e) { show($("yc-msg"), e.message); }
});
$("yc-cikar")?.addEventListener("click", async () => {
  const ids = [...ycSelected];
  if (!ids.length) { show($("yc-msg"), "Çıkarılacak Yalçın müşterisini seçin."); return; }
  try {
    const r = await api("/panel/api/yalcin/ata", { method: "POST", body: { customerIds: ids, assign: false } });
    ycSelected.clear();
    show($("yc-msg"), r.message, "ok");
    await loadYalcin();
  } catch (e) { show($("yc-msg"), e.message); }
});

start().then(() => { if ($("app").classList.contains("hidden")) showLogin(); });
