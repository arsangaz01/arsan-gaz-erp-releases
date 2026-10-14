"use strict";

const $ = (id) => document.getElementById(id);
const BASE = (document.querySelector('meta[name="base-path"]')?.content || "").replace(/\/+$/, "");
let options = { statuses: [], gasTypes: [], plates: [], longDays: 60 };
let current = null;
let me = null;

// "a|b" means any of the listed permissions.
const can = (permission) => !!me && permission.split("|").some((p) => me.permissions.includes(p));
// Target status → permission needed to record it (same table as Perm.MoveByStatus on the server).
const MOVE_PERMISSION = { 1: "hareket-iade", 2: "hareket-arac", 3: "hareket-teslim", 4: "hareket-dolum", 5: "hareket-servis", 6: "hareket-hurda" };
const STALE_TEXT = "Bu kayıt başka bir kullanıcı tarafından değiştirildi, yenileyip tekrar deneyin.";
const newKey = () => (crypto.randomUUID ? crypto.randomUUID() : Array.from(crypto.getRandomValues(new Uint32Array(4)), (n) => n.toString(16)).join("-"));

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "class") node.className = value;
    else if (key === "onclick") node.addEventListener("click", value);
    else if (value !== undefined && value !== null) node.setAttribute(key, value);
  }
  for (const child of children) {
    if (child === null || child === undefined) continue;
    node.append(child instanceof Node ? child : document.createTextNode(String(child)));
  }
  return node;
}

// Lists refresh shortly after typing stops (Enter still works); filter selects and checkboxes refresh at once.
function liveFilter(inputIds, changeIds, load) {
  let timer = 0;
  for (const id of inputIds) $(id).addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(load, 350); });
  for (const id of changeIds) $(id).addEventListener("change", () => { clearTimeout(timer); load(); });
}

// Only the newest request of a list may draw it, so a slow earlier answer cannot overwrite a newer one.
const listRequests = {};
const newRequest = (name) => (listRequests[name] = (listRequests[name] || 0) + 1);
const isStale = (name, ticket) => listRequests[name] !== ticket;

function show(box, text, kind = "err") {
  box.textContent = text;
  box.className = "msg " + kind;
}

function hide(box) { box.className = "msg hidden"; }

async function api(path, options = {}) {
  const init = { method: options.method || "GET", headers: {}, credentials: "same-origin" };
  if (options.body !== undefined) {
    init.headers["Content-Type"] = "application/json";
    init.body = JSON.stringify(options.body);
  }
  if (init.method !== "GET") init.headers["X-Panel-Istek"] = "1";
  // The same key on a repeated request makes the server return the first result instead of saving twice.
  if (options.key) init.headers["Idempotency-Key"] = options.key;
  const response = await fetch(BASE + path, init);
  let data = {};
  try { data = await response.json(); } catch { data = {}; }
  if (response.status === 401 && path.startsWith("/panel/api/")) {
    me = null;
    for (const d of document.querySelectorAll("dialog")) if (d.open) d.close();
    showLogin("Oturum süresi doldu (panel girişi 1 saat geçerlidir); yeniden giriş yapın.");
    throw new Error("Oturum kapalı.");
  }
  if (response.status === 409 && data.stale) data.error = data.error || STALE_TEXT;
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
    else show($("global-msg"), "Hesabınıza panelde görüntüleyebileceğiniz bir bölüm verilmemiş. Yöneticinizden yetki isteyin.", "warn");
    if (can("musteri-goruntule|musteri-ekle|tup-goruntule|tup-kayit|tup-sayim|hareket-gecmisi|hareket-teslim|sefer-teslim")) {
      for (const picker of [cylinderCustomer, movementCustomer, newCustomer]) picker.search();
    }
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
  if (tab === "seferler") loadTrips();
  if (tab === "araclar") loadVehicles();
  if (tab === "musteriler") loadCustomers();
  if (tab === "musteri-stok") loadStock();
  if (tab === "yonetim") loadAdmin();
}

async function loadOptions() {
  options = await api("/panel/api/secenekler");
  for (const id of ["f-durum", "h-durum"]) {
    const select = $(id);
    select.length = 1;
    for (const s of options.statuses) select.append(el("option", { value: s.id }, s.name));
  }
  const status = $("m-status");
  const allowed = options.statuses.filter((s) => can(MOVE_PERMISSION[s.id] || "-"));
  status.replaceChildren(el("option", { value: "" }, "Seçin"), ...allowed.map((s) => el("option", { value: s.id }, s.name)));
  $("f-plaka").length = 1;
  for (const p of options.plates) $("f-plaka").append(el("option", { value: p }, p));
  $("gas-list").replaceChildren(...options.gasTypes.map((g) => el("option", { value: g })));
  $("plate-list").replaceChildren(...options.plates.map((p) => el("option", { value: p })));
}

function customerLabel(c) {
  return `${c.name}${c.city ? " · " + c.city : ""}${c.cylinders ? ` (${c.cylinders} tüp)` : ""}`;
}

// Search box + select pair for picking a customer.
function customerPicker(inputId, selectId, emptyLabel) {
  let timer = 0;
  const select = $(selectId);
  async function search() {
    const keep = select.value;
    try {
      const rows = await api("/panel/api/musteriler?q=" + encodeURIComponent($(inputId).value.trim()));
      const items = rows.map((c) => el("option", { value: c.id }, customerLabel(c)));
      select.replaceChildren(...(emptyLabel ? [el("option", { value: "" }, emptyLabel)] : []), ...items);
      if (keep && rows.some((c) => String(c.id) === keep)) select.value = keep;
      else if (!emptyLabel && rows.length) select.value = String(rows[0].id);
    } catch (error) {
      show($("global-msg"), error.message);
    }
  }
  $(inputId).addEventListener("input", () => { clearTimeout(timer); timer = setTimeout(search, 300); });
  return {
    search,
    reset() { $(inputId).value = ""; select.replaceChildren(...(emptyLabel ? [el("option", { value: "" }, emptyLabel)] : [])); },
    query() { return $(inputId).value.trim(); },
    // Selects a customer that was just created or picked from a duplicate warning.
    choose(c) {
      $(inputId).value = c.name;
      select.replaceChildren(...(emptyLabel ? [el("option", { value: "" }, emptyLabel)] : []), el("option", { value: c.id }, customerLabel(c)));
      select.value = String(c.id);
    }
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
    if (data.testOverdue > 0) stats.push(stat(data.testOverdue, "Test tarihi geçmiş", "Teste gönderilmesi gereken", () => openFiltered({ test: "gecmis" }), true));
    if (data.testSoon > 0) stats.push(stat(data.testSoon, `Test tarihi ${data.testSoonDays} gün içinde`, "Yaklaşan testler", () => openFiltered({ test: "yakin" }), true));
    if (data.inactive > 0) stats.push(stat(data.inactive, "Pasif tüp", "Kullanım dışı kayıtlar", () => openFiltered({ pasif: true })));
    if (data.review > 0) stats.push(stat(data.review, "İnceleme bekleyen tüp", "Eski sistemden eksik bilgiyle aktarılan", () => openFiltered({ inceleme: true }), true));
    $("stats").replaceChildren(...stats);

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
  $("f-test").value = filter.test ?? "";
  $("f-uzun").checked = !!filter.uzun;
  $("f-pasif").checked = !!filter.pasif;
  $("f-inceleme").checked = !!filter.inceleme;
  openTab("tupler");
}

$("f-ara").addEventListener("click", loadCylinders);
$("f-q").addEventListener("keydown", (e) => { if (e.key === "Enter") loadCylinders(); });
liveFilter(["f-q"], ["f-durum", "f-doluluk", "f-test", "f-plaka", "f-musteri", "f-uzun", "f-pasif", "f-inceleme"], loadCylinders);

async function loadCylinders() {
  const query = new URLSearchParams();
  if ($("f-q").value.trim()) query.set("q", $("f-q").value.trim());
  if ($("f-durum").value) query.set("durum", $("f-durum").value);
  if ($("f-plaka").value) query.set("plaka", $("f-plaka").value);
  if ($("f-musteri").value) query.set("musteri", $("f-musteri").value);
  if ($("f-doluluk").value) query.set("doluluk", $("f-doluluk").value);
  if ($("f-test").value) query.set("test", $("f-test").value);
  if ($("f-uzun").checked) query.set("uzun", "true");
  if ($("f-pasif").checked) query.set("pasif", "true");
  if ($("f-inceleme").checked) query.set("inceleme", "true");
  const ticket = newRequest("cylinders");
  try {
    const rows = await api("/panel/api/tupler?" + query);
    if (isStale("cylinders", ticket)) return;
    $("cyl-count").textContent = `${rows.length} tüp`;
    const head = el("tr", {}, ...["Seri no", "Gaz", "Kapasite", "Durum", "Dolu/Boş", "Müşteri / Plaka", "Son hareket", "Test"].map((h) => el("th", {}, h)));
    const body = rows.map((r) => el("tr", { class: "click", onclick: () => openDetail(r.id) },
      el("td", {}, el("b", {}, (r.needsReview ? "⚠ " : "") + r.serialNumber),
        r.manufacturerSerial ? el("div", { class: "muted" }, "Üretici seri: " + r.manufacturerSerial)
          : r.barcode !== r.serialNumber ? el("div", { class: "muted" }, r.barcode) : null),
      el("td", {}, r.gasType),
      el("td", {}, r.capacity),
      el("td", {}, badge(r.status, r.statusName)),
      el("td", {}, r.fullness),
      el("td", {}, r.customer || r.plate || "-"),
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
liveFilter(["h-q"], ["h-bas", "h-bit", "h-durum", "h-musteri"], loadMovements);
$("h-csv").addEventListener("click", (e) => {
  e.preventDefault();
  window.location.href = BASE + "/panel/api/hareketler.csv?" + movementFilter();
});

async function loadMovements() {
  if (!$("h-bas").value) {
    const from = new Date(Date.now() - 30 * 86400000);
    $("h-bas").value = from.toISOString().slice(0, 10);
    $("h-bit").value = new Date().toISOString().slice(0, 10);
  }
  const ticket = newRequest("movements");
  try {
    const data = await api("/panel/api/hareketler?" + movementFilter());
    if (isStale("movements", ticket)) return;
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
  if (c.manufacturerSerial) info.splice(2, 0, ["Üretici seri no", c.manufacturerSerial]);
  if (c.owner) info.push(["Tüpün sahibi", "Müşteri malı · " + c.owner]);
  if (c.productName) info.push(["Ürün (eski sistem)", c.productName]);
  if (c.manufactureDate) info.push(["İmalat tarihi", c.manufactureDate]);
  if (c.lastTestDate) info.push(["Son test", c.lastTestDate]);
  if (c.legacy) info.push(["Eski sistem kaydı", c.legacy]);
  if (c.needsReview || c.reviewNote) info.push([c.needsReview ? "⚠ İnceleme" : "İnceleme notu", c.reviewNote || "-"]);
  if (c.movementCount > c.movements.length) info.push(["Hareket sayısı", `${c.movementCount} (son ${c.movements.length} tanesi aşağıda)`]);
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
  const full = can("hareket-dolu-bos") ? $("m-full").value : "";
  const body = {
    status, customerId, confirm,
    plate: status === 2 ? $("m-plate").value.trim() || null : null,
    isFull: full === "" ? null : full === "true",
    note: $("m-note").value.trim() || null,
    version: current.version
  };
  $("m-save").disabled = true;
  try {
    const result = await api(`/panel/api/tup/${current.id}/hareket`, { method: "POST", body, key: newKey() });
    await openDetail(current.id);
    show($("d-msg"), result.message, result.message.includes("⚠") ? "warn" : "ok");
  } catch (error) {
    if (error.status === 409 && error.data?.stale) {
      await openDetail(current.id);
      show($("d-msg"), error.message + " Güncel bilgiler yüklendi.", "warn");
    } else if (error.status === 409 && !confirm && can("hareket-cakisma")) {
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
      body: { gasType: $("e-gas").value.trim(), capacity, capacityUnit: $("e-unit").value, testDueDate: $("e-test").value || null, isActive: active, version: current.version }
    });
    await openDetail(current.id);
    show($("d-msg"), result.message, "ok");
  } catch (error) {
    if (error.status === 409 && error.data?.stale) {
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

    const alarms = active.filter((t) => t.missing > 0).map((t) => `${t.title}: ${t.missing} tüp okutulmadı (${gasText(t.missingByGas)})`);
    if (alarms.length) { $("s-alarm").textContent = "⚠ Dönen araçlarda eksik okutma var:\n" + alarms.join("\n"); $("s-alarm").classList.remove("hidden"); }
    else $("s-alarm").classList.add("hidden");

    $("s-count").textContent = `${rows.length} sefer` + (rows.length >= 100 ? " (son 100)" : "");
    const head = el("tr", {}, ...["Sefer", "Başlangıç / bitiş", "Araçlar", "Yüklenen", "Teslim", "Depoya dönen", "Araçta", "Durum", "Eksik (gaz türü)"].map((h) => el("th", {}, h)));
    const body = rows.map((t) => el("tr", { class: "click" + (t.missing ? " alarm-row" : ""), onclick: () => openTrip(t.id) },
      el("td", {}, el("b", {}, t.title), t.note ? el("div", { class: "muted" }, t.note) : null),
      el("td", {}, t.started, el("div", { class: "muted" }, t.closed ? "Kapandı " + t.closed : "Açık")),
      el("td", {}, el("div", { class: "trip-vehicles" }, ...t.vehicles.map((v) => el("span", { class: "badge" + (v.missing ? " trip-alarm" : v.onTrip ? " trip-active" : "") }, v.plate)))),
      el("td", {}, String(t.loaded)),
      el("td", {}, String(t.delivered)),
      el("td", {}, String(t.returned)),
      el("td", {}, String(t.onVehicle)),
      el("td", {}, tripStatus(t)),
      el("td", { class: t.missing ? "alarm" : "muted" }, t.missing ? gasText(t.missingByGas) : "-")));
    $("trips").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 9, class: "muted" }, "Kayıtlı sefer yok."))])));
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

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
  if (data.unexpected.length) alarms.push(`⚠ ${data.unexpected.length} tüp bu sefere yüklenmediği hâlde dönüşte okutuldu.`);
  $("t-alarm").textContent = alarms.join("\n");
  $("t-alarm").classList.toggle("hidden", alarms.length === 0);
  const info = [
    ["Durum", t.active ? "Seferde (açık)" : "Kapandı"], ["Başlangıç", t.started], ["Bitiş", t.closed || "-"],
    ["Açan", t.createdBy || "-"], ["Not", t.note || "-"],
    ["Yüklenen", t.loaded], ["Müşteriye teslim", t.delivered], ["Depoya dönen", t.returned], ["Araçta (yolda)", t.onVehicle], ["Eksik", t.missing]
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
    el("td", {}, el("b", {}, c.serialNumber)), el("td", {}, c.gasType), el("td", {}, c.plate), el("td", {}, c.loaded),
    el("td", { class: c.missing ? "alarm" : "" },
      c.delivered ? "Müşteriye teslim " + c.delivered : c.returned ? "Depoya döndü " + c.returned : c.missing ? "⚠ Eksik: okutulmadı" : "Araçta")));
  $("t-cylinders").replaceChildren(el("thead", {}, cHead), el("tbody", {}, ...(cBody.length ? cBody : [el("tr", {}, el("td", { colspan: 5, class: "muted" }, "Bu sefere tüp yüklenmemiş."))])));

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
  const head = el("tr", {}, ...["Seri no / barkod", "Kapasite", "Dolu/Boş", "Durum", "Müşteri / plaka", "Yükleme", "Son hareket", "Test"].map((h) => el("th", {}, h)));
  const rows = g.cylinders.map((c) => el("tr", { class: "click" + (c.alarm ? " alarm-row" : ""), onclick: () => openDetail(c.cylinderId) },
    el("td", {}, el("b", {}, c.serialNumber), c.barcode !== c.serialNumber ? el("div", { class: "muted" }, c.barcode) : null),
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
    const result = await api(`/panel/api/sefer/${currentTrip.id}/yukle`, { method: "POST", body: { plate, codes }, key: newKey() });
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
    await openTrip(currentTrip.id, true);
    show($("t-msg"), result.message, result.hasAlerts ? "warn" : "ok");
    loadTrips();
  } catch (error) {
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
  $("vd-toggle").classList.toggle("hidden", !can("arac-pasif"));
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
    note: $("vf-note").value.trim() || null, isActive: active, version: editingVehicle ? editingVehicle.version : null
  };
}

$("vd-toggle").addEventListener("click", async () => {
  const v = currentVehicle;
  if (!v) return;
  if (v.isActive && !window.confirm(`${v.plate} pasife alınsın mı? Yeni seferlerde seçilemez; geçmişi silinmez, tekrar aktif edilebilir.`)) return;
  try {
    const result = await api("/panel/api/arac/" + v.id, {
      method: "POST",
      body: { plate: v.plate, name: v.name, vehicleType: v.vehicleType, driverName: v.driverName, cylinderCapacity: v.cylinderCapacity, note: v.note, isActive: !v.isActive, version: v.version }
    });
    await openVehicle(v.id, result.message);
    loadVehicles();
  } catch (error) {
    if (error.status === 409 && error.data?.stale) {
      await openVehicle(v.id);
      show($("vd-msg"), error.message + " Güncel bilgiler yüklendi.", "warn");
    } else {
      show($("vd-msg"), error.message);
    }
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
  const ticket = newRequest("customers");
  try {
    const rows = await api("/panel/api/musteriler?take=200&q=" + encodeURIComponent($("c-q").value.trim()));
    if (isStale("customers", ticket)) return;
    $("c-count").textContent = rows.length >= 200 ? "İlk 200 kayıt gösteriliyor; aramayı daraltın." : `${rows.length} kayıt`;
    const head = el("tr", {}, ...["Müşteri", "Kod", "Şehir", "Telefon", "Müşterideki tüp"].map((h) => el("th", {}, h)));
    const body = rows.map((c) => el("tr", { class: "click", onclick: () => openCustomerCylinders(c) },
      el("td", {}, el("b", {}, c.name)),
      el("td", { class: "muted" }, c.code),
      el("td", {}, c.city || "-"),
      el("td", {}, c.phone || "-"),
      el("td", {}, String(c.cylinders))));
    $("customers").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 5, class: "muted" }, "Eşleşen müşteri yok. Yeni kayıt açmak için \"+ Yeni müşteri\"ye basın."))])));
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
  const ticket = newRequest("stock");
  try {
    const data = await api("/panel/api/musteri-stok?" + stockFilter());
    if (isStale("stock", ticket)) return;
    $("ms-stats").replaceChildren(
      stat(data.customers, "Tüpü olan müşteri", "Filtreye uyan", () => {}),
      stat(data.cylinders, "Müşterideki tüp", fullEmpty(data.full, data.empty), () => openFiltered({ durum: 3 })),
      stat(data.longCount, `${data.longDays}+ gündür bekleyen`, "Dönmeyen tüpler", () => openFiltered({ uzun: true }), true));
    stockGroupTable($("ms-gas"), data.gasTotals, data.longDays, openGroupDialog);
    $("ms-count").textContent = `${data.customers} müşteri · ${data.cylinders} tüp`;
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
  try {
    if (can("kullanici-yonetimi")) {
      catalog = catalog || await api("/panel/api/yonetim/yetkiler");
      $("roles").replaceChildren(...catalog.roles.map((r) => el("div", {}, roleBadge(r.name), el("div", {}, r.description),
        el("div", { class: "muted", style: "margin-top:4px" }, r.name === "Yönetici" ? "Tüm yetkiler" : `${r.defaults.length} yetki varsayılan`))));
      users = await api("/panel/api/yonetim/kullanicilar");
      renderUsers();
    }
    if (can("cihaz-yonetimi")) await loadDevices();
    if (can("denetim-kaydi")) await loadAudit();
  } catch (error) {
    show($("admin-msg"), error.message);
  }
}

function permissionLabel(key) {
  for (const g of catalog?.groups || []) for (const p of g.permissions) if (p.key === key) return p.label;
  return key;
}

function permissionDiffText(u) {
  if (u.role === "Yönetici") return el("div", { class: "muted" }, "Tüm yetkiler");
  if (!u.granted.length && !u.revoked.length) return el("div", { class: "muted" }, "Rol varsayılanı");
  return el("div", {},
    ...u.granted.map((k) => el("span", { class: "perm-tag on", title: "Rolde yok, ayrıca verildi" }, "+ " + permissionLabel(k))),
    ...u.revoked.map((k) => el("span", { class: "perm-tag off", title: "Rolde var, kaldırıldı" }, "− " + permissionLabel(k))));
}

function renderUsers() {
  const head = el("tr", {}, ...["Kullanıcı", "Rol ve yetkiler", "Durum", "Son giriş", "Açık oturum", "Oluşturan", ""].map((h) => el("th", {}, h)));
  const body = users.map((u) => el("tr", { class: u.isActive ? "" : "off" },
    el("td", {}, el("b", {}, u.displayName), u.isSelf ? el("span", { class: "muted" }, " (siz)") : null, el("div", { class: "muted" }, u.userName)),
    el("td", {}, roleBadge(u.role), permissionDiffText(u)),
    el("td", {}, el("span", { class: "dot" + (u.isActive ? " on" : "") }), u.isActive ? "Aktif" : "Pasif",
      u.mustChangePassword ? el("div", { class: "warn-text muted" }, "Şifre değiştirmesi bekleniyor") : null),
    el("td", {}, u.lastLogin || "Hiç giriş yapmadı", u.lastLoginIp ? el("div", { class: "muted" }, u.lastLoginIp) : null),
    el("td", {}, `Panel: ${u.sessions}`, el("div", { class: "muted" }, `Telefon: ${u.phones}`)),
    el("td", {}, u.createdBy || "-", el("div", { class: "muted" }, u.created)),
    el("td", {},
      el("button", { class: "link", type: "button", onclick: () => openUserDialog(u) }, "Düzenle / yetkiler"),
      el("button", { class: "link", type: "button", onclick: () => openResetDialog(u) }, "Şifre sıfırla"),
      u.isSelf ? null : el("button", { class: u.isActive ? "danger" : "link", type: "button", onclick: () => toggleUser(u) }, u.isActive ? "Pasife al" : "Aktif et"),
      u.isSelf || (u.role === "Yönetici" && me.role !== "Yönetici") ? null
        : el("button", { class: "link delete-link", type: "button", onclick: () => openDeleteDialog(u) }, "Sil"))));
  $("users").replaceChildren(el("thead", {}, head), el("tbody", {}, ...body));
}

async function loadDevices() {
  const data = await api("/panel/api/yonetim/cihazlar");
  const head = el("tr", {}, ...["Kullanıcı", "Telefon", "Giriş", "Geçerlilik sonu", "Son görülme", ""].map((h) => el("th", {}, h)));
  const body = data.devices.map((d) => el("tr", {},
    el("td", {}, el("b", {}, d.displayName), el("div", { class: "muted" }, d.userName)),
    el("td", {}, d.name, d.ip ? el("div", { class: "muted" }, d.ip) : null),
    el("td", {}, d.created),
    el("td", {}, d.expires),
    el("td", {}, d.lastSeen || "-"),
    el("td", {}, el("button", { class: "danger", type: "button", onclick: () => removeDevice(d) }, "Oturumu kapat"))));
  $("devices").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Giriş yapmış telefon yok."))])));
}

async function removeDevice(d) {
  if (!confirm(`${d.userName} kullanıcısının "${d.name}" telefonundaki oturum kapatılsın mı? Telefon yeniden kullanıcı adı ve şifreyle giriş yapmalı.`)) return;
  try {
    const result = await api(`/panel/api/yonetim/cihaz/${encodeURIComponent(d.id)}/sil`, { method: "POST" });
    show($("admin-msg"), result.message, "ok");
    await loadDevices();
  } catch (error) {
    show($("admin-msg"), error.message);
  }
}

$("dev-refresh").addEventListener("click", async () => {
  try { await loadDevices(); } catch (error) { show($("admin-msg"), error.message); }
});

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

// ---- Permission checklist -------------------------------------------------------------------------
// Ticking a permission also ticks what it requires; unticking removes what depends on it (the server applies
// the same rule). Differences from the role default are highlighted.

function roleDefaults(role) {
  const r = catalog.roles.find((x) => x.name === role);
  return new Set(r ? r.defaults : []);
}

function permissionBoxes() { return [...$("u-perms").querySelectorAll("input[data-key]")]; }

function requiresOf(key) {
  for (const g of catalog.groups) for (const p of g.permissions) if (p.key === key) return p.requires || [];
  return [];
}

function setPermissions(selected) {
  for (const box of permissionBoxes()) box.checked = selected.has(box.dataset.key);
  refreshPermissionMarks();
}

function refreshPermissionMarks() {
  const role = $("u-role").value;
  const admin = role === "Yönetici";
  const defaults = roleDefaults(role);
  let added = 0;
  let removed = 0;
  for (const box of permissionBoxes()) {
    const key = box.dataset.key;
    const row = box.closest(".perm-item");
    if (admin) box.checked = true;
    box.disabled = admin || $("u-role").disabled || (!can(key) && !box.checked && me.role !== "Yönetici");
    const on = box.checked && !defaults.has(key);
    const off = !box.checked && defaults.has(key);
    if (on) added++;
    if (off) removed++;
    row.classList.toggle("diff-on", !admin && on);
    row.classList.toggle("diff-off", !admin && off);
    row.classList.toggle("locked", box.disabled);
    row.querySelector(".perm-tag")?.remove();
    if (!admin && (on || off)) row.querySelector("span").append(el("span", { class: "perm-tag " + (on ? "on" : "off") }, on ? "rolde yok, eklendi" : "rolde var, kaldırıldı"));
  }
  const total = permissionBoxes().filter((b) => b.checked).length;
  $("u-perm-summary").textContent = admin
    ? "Yönetici her zaman tüm yetkilere sahiptir; tek tek değiştirilemez."
    : `${total} yetki seçili · rol varsayılanından farklar: ${added} eklendi, ${removed} kaldırıldı.`;
  $("u-perm-reset").disabled = admin || (added === 0 && removed === 0) || $("u-role").disabled;
}

function renderPermissionChecklist() {
  $("u-perms").replaceChildren(...catalog.groups.map((g) => {
    const items = g.permissions.map((p) => {
      const box = el("input", { type: "checkbox", "data-key": p.key });
      box.addEventListener("change", () => {
        if (box.checked) {
          for (const r of p.requires || []) { const dep = $("u-perms").querySelector(`input[data-key="${r}"]`); if (dep) dep.checked = true; }
        } else {
          for (const other of permissionBoxes()) if (requiresOf(other.dataset.key).includes(p.key)) other.checked = false;
        }
        refreshPermissionMarks();
      });
      return el("label", { class: "perm-item", title: p.description }, box, el("span", {}, p.label, el("small", {}, p.description)));
    });
    const all = el("button", { class: "link", type: "button" }, "Tümü");
    all.addEventListener("click", () => {
      const boxes = g.permissions.map((p) => $("u-perms").querySelector(`input[data-key="${p.key}"]`)).filter((b) => b && !b.disabled);
      const target = !boxes.every((b) => b.checked);
      for (const b of boxes) { b.checked = target; b.dispatchEvent(new Event("change")); }
    });
    return el("div", { class: "perm-group" }, el("h4", {}, g.name, all), ...items);
  }));
}

// A user manager who is not a Yönetici can only hand out permissions they have (or the user already has).
function grantableDefaults(role) {
  const keep = (key) => me.role === "Yönetici" || can(key) || (editingUser && (editingUser.permissions || []).includes(key));
  return new Set([...roleDefaults(role)].filter(keep));
}

$("u-role").addEventListener("change", () => {
  showRoleDescription();
  if (catalog) setPermissions(grantableDefaults($("u-role").value));
});
$("u-perm-reset").addEventListener("click", () => setPermissions(grantableDefaults($("u-role").value)));
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
  $("u-role").disabled = !!u && u.isSelf && me.role !== "Yönetici";
  renderPermissionChecklist();
  setPermissions(u ? new Set(u.permissions) : grantableDefaults($("u-role").value));
  $("user-dialog").showModal();
  (u ? $("u-display") : $("u-name")).focus();
}

function selectedPermissions() {
  return permissionBoxes().filter((b) => b.checked).map((b) => b.dataset.key);
}

$("user-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  $("u-save").disabled = true;
  try {
    let result;
    const yetkiler = $("u-role").value === "Yönetici" ? null : selectedPermissions();
    if (editingUser) {
      result = await api(`/panel/api/yonetim/kullanici/${editingUser.id}`, {
        method: "POST", body: { adSoyad: $("u-display").value.trim(), rol: $("u-role").value, aktif: editingUser.isSelf ? true : $("u-active").checked, yetkiler }
      });
    } else {
      result = await api("/panel/api/yonetim/kullanici", {
        method: "POST",
        body: { kullaniciAdi: $("u-name").value.trim(), adSoyad: $("u-display").value.trim(), rol: $("u-role").value, sifre: $("u-pass").value, sifreDegistirsin: $("u-must").checked, yetkiler }
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

start().then(() => { if ($("app").classList.contains("hidden")) showLogin(); });
