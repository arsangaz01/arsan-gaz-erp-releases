"use strict";

const $ = (id) => document.getElementById(id);
let options = { statuses: [], gasTypes: [], plates: [], longDays: 60 };
let current = null;
let me = null;

const can = (permission) => !!me && me.permissions.includes(permission);

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
    openTab("ozet");
    for (const picker of [cylinderCustomer, movementCustomer, newCustomer]) picker.search();
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
  if (tab === "musteriler") loadCustomers();
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
    if (data.testSoon > 0) stats.push(stat(data.testSoon, "Test tarihi 30 gün içinde", "veya geçmiş", () => openFiltered({}), true));
    if (data.inactive > 0) stats.push(stat(data.inactive, "Pasif tüp", "Kullanım dışı kayıtlar", () => openFiltered({ pasif: true })));
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
  $("f-uzun").checked = !!filter.uzun;
  $("f-pasif").checked = !!filter.pasif;
  openTab("tupler");
}

$("f-ara").addEventListener("click", loadCylinders);
$("f-q").addEventListener("keydown", (e) => { if (e.key === "Enter") loadCylinders(); });

async function loadCylinders() {
  const query = new URLSearchParams();
  if ($("f-q").value.trim()) query.set("q", $("f-q").value.trim());
  if ($("f-durum").value) query.set("durum", $("f-durum").value);
  if ($("f-plaka").value) query.set("plaka", $("f-plaka").value);
  if ($("f-musteri").value) query.set("musteri", $("f-musteri").value);
  if ($("f-doluluk").value) query.set("doluluk", $("f-doluluk").value);
  if ($("f-uzun").checked) query.set("uzun", "true");
  if ($("f-pasif").checked) query.set("pasif", "true");
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
    if (error.status === 409 && !confirm) {
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
      body: { gasType: $("e-gas").value.trim(), capacity, capacityUnit: $("e-unit").value, testDueDate: $("e-test").value || null, isActive: active }
    });
    await openDetail(current.id);
    show($("d-msg"), result.message, "ok");
  } catch (error) {
    show($("d-msg"), error.message);
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

// ---- Customers tab -------------------------------------------------------------------------------

$("c-ara").addEventListener("click", loadCustomers);
$("c-q").addEventListener("keydown", (e) => { if (e.key === "Enter") loadCustomers(); });

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
  openFiltered({ customer: c });
}

// ---- Administration ------------------------------------------------------------------------------

let users = [];
let editingUser = null;
let resetUser = null;

async function loadAdmin() {
  hide($("admin-msg"));
  $("roles").replaceChildren(...me.roles.map((r) => el("div", {}, roleBadge(r.name), el("div", {}, r.description))));
  try {
    users = await api("/panel/api/yonetim/kullanicilar");
    renderUsers();
    await loadAudit();
  } catch (error) {
    show($("admin-msg"), error.message);
  }
}

function renderUsers() {
  const head = el("tr", {}, ...["Kullanıcı", "Yetki", "Durum", "Son giriş", "Açık oturum", "Oluşturan", ""].map((h) => el("th", {}, h)));
  const body = users.map((u) => el("tr", { class: u.isActive ? "" : "off" },
    el("td", {}, el("b", {}, u.displayName), u.isSelf ? el("span", { class: "muted" }, " (siz)") : null, el("div", { class: "muted" }, u.userName)),
    el("td", {}, roleBadge(u.role)),
    el("td", {}, el("span", { class: "dot" + (u.isActive ? " on" : "") }), u.isActive ? "Aktif" : "Pasif",
      u.mustChangePassword ? el("div", { class: "warn-text muted" }, "Şifre değiştirmesi bekleniyor") : null),
    el("td", {}, u.lastLogin || "Hiç giriş yapmadı", u.lastLoginIp ? el("div", { class: "muted" }, u.lastLoginIp) : null),
    el("td", {}, String(u.sessions)),
    el("td", {}, u.createdBy || "-", el("div", { class: "muted" }, u.created)),
    el("td", {},
      el("button", { class: "link", type: "button", onclick: () => openUserDialog(u) }, "Düzenle"),
      el("button", { class: "link", type: "button", onclick: () => openResetDialog(u) }, "Şifre sıfırla"),
      u.isSelf ? null : el("button", { class: u.isActive ? "danger" : "link", type: "button", onclick: () => toggleUser(u) }, u.isActive ? "Pasife al" : "Aktif et"))));
  $("users").replaceChildren(el("thead", {}, head), el("tbody", {}, ...body));
}

async function loadAudit() {
  const rows = await api("/panel/api/yonetim/denetim");
  const head = el("tr", {}, ...["Zaman", "Kullanıcı", "İşlem", "Hedef", "Ayrıntı", "IP"].map((h) => el("th", {}, h)));
  const body = rows.map((a) => el("tr", {},
    el("td", {}, a.at), el("td", {}, a.actor),
    el("td", { class: a.action.includes("Hatalı") || a.action.includes("Pasif") ? "warn-text" : "" }, a.action),
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

start().then(() => { if ($("app").classList.contains("hidden")) showLogin(); });
