"use strict";

const $ = (id) => document.getElementById(id);
let options = { statuses: [], gasTypes: [], plates: [], longDays: 60 };
let current = null;
let me = null;

// "a|b" = any of them.
const can = (permission) => !!me && permission.split("|").some((p) => me.permissions.includes(p));

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
  if (tab === "seferler") loadTrips();
  if (tab === "araclar") loadVehicles();
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

start().then(() => { if ($("app").classList.contains("hidden")) showLogin(); });
