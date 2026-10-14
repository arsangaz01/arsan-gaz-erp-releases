"use strict";

// Customer portal: one customer's cylinders and requests. The server only ever returns the signed-in customer's data.
const $ = (id) => document.getElementById(id);
const BASE = (document.querySelector('meta[name="base-path"]')?.content || "").replace(/\/+$/, "");
let me = null;

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
  const response = await fetch(BASE + path, init);
  let data = {};
  try { data = await response.json(); } catch { data = {}; }
  if (response.status === 401 && path.startsWith("/musteri/api/")) {
    const wasSignedIn = !!me;
    me = null;
    showLogin(wasSignedIn ? "Oturumunuz sona erdi; yeniden giriş yapın." : null);
    throw new Error("Oturum kapalı.");
  }
  if (response.status === 403 && data.mustChangePassword) openPassword(true);
  if (!response.ok) throw new Error(data.error || `Sunucu hatası (${response.status}).`);
  return data;
}

function showLogin(message) {
  $("app").classList.add("hidden");
  $("login").classList.remove("hidden");
  if (message) show($("login-msg"), message); else hide($("login-msg"));
  $("login-user").focus();
}

function openPassword(forced) {
  $("password-card").classList.remove("hidden");
  $("password-forced").classList.toggle("hidden", !forced);
  $("content").classList.toggle("hidden", forced);
  $("username-hidden").value = me ? me.user : "";
  hide($("password-msg"));
  $("p-current").focus();
}

async function start() {
  try {
    me = await api("/musteri/api/ben");
    $("who").textContent = me.customerName;
    $("login").classList.add("hidden");
    $("app").classList.remove("hidden");
    if (me.mustChangePassword) { openPassword(true); return; }
    $("password-card").classList.add("hidden");
    $("content").classList.remove("hidden");
    await Promise.all([loadCylinders(), loadRequests()]);
  } catch (error) {
    if (me) show($("global-msg"), error.message);
  }
}

function stat(n, label) { return el("div", { class: "stat" }, el("div", { class: "n" }, String(n)), el("div", { class: "l" }, label)); }

async function loadCylinders() {
  const data = await api("/musteri/api/tupler");
  const t = data.totals;
  $("stats").replaceChildren(stat(t.count, "Toplam tüp"), stat(t.full, "Dolu"), stat(t.empty, "Boş"));
  const gHead = el("tr", {}, ...["Gaz", "Boyut", "Adet", "Dolu", "Boş"].map((h) => el("th", {}, h)));
  const gBody = data.groups.map((g) => el("tr", {}, el("td", {}, el("b", {}, g.gasType)), el("td", {}, g.capacity), el("td", {}, String(g.count)), el("td", {}, String(g.full)), el("td", {}, String(g.empty))));
  $("groups").replaceChildren(el("thead", {}, gHead), el("tbody", {}, ...(gBody.length ? gBody : [el("tr", {}, el("td", { colspan: 5, class: "muted" }, "Kayıtlarımıza göre sizde tüp görünmüyor."))])));
  const cHead = el("tr", {}, ...["Seri no", "Gaz", "Boyut", "Dolu/Boş", "Teslim", "Test tarihi"].map((h) => el("th", {}, h)));
  const cBody = data.cylinders.map((c) => el("tr", {},
    el("td", {}, el("b", {}, c.serialNumber)), el("td", {}, c.gasType), el("td", {}, c.capacity), el("td", {}, c.fullness),
    el("td", {}, c.since || "-", c.days !== null && c.days !== undefined ? el("div", { class: "muted" }, `${c.days} gün`) : null),
    el("td", { class: c.testOverdue ? "warn" : "" }, c.testDue || "-", c.testOverdue ? el("div", { class: "muted" }, "test süresi geçmiş") : null)));
  $("cylinders").replaceChildren(el("thead", {}, cHead), el("tbody", {}, ...(cBody.length ? cBody : [el("tr", {}, el("td", { colspan: 6, class: "muted" }, "Tüp yok."))])));
}

async function loadRequests() {
  const rows = await api("/musteri/api/talepler");
  const head = el("tr", {}, ...["Tarih", "Talep", "Durum", ""].map((h) => el("th", {}, h)));
  const body = rows.map((r) => el("tr", {},
    el("td", {}, r.created),
    el("td", {}, el("b", {}, r.kindLabel), r.quantity ? ` · ${r.quantity} tüp` : "", r.note ? el("div", { class: "muted" }, r.note) : null),
    el("td", {}, el("span", { class: "badge" + (r.status === "acik" ? " open" : "") }, r.statusLabel), r.handled ? el("div", { class: "muted" }, r.handled) : null),
    el("td", {}, r.status === "acik" ? el("button", { class: "secondary small", type: "button", onclick: () => cancelRequest(r) }, "İptal") : null)));
  $("requests").replaceChildren(el("thead", {}, head), el("tbody", {}, ...(body.length ? body : [el("tr", {}, el("td", { colspan: 4, class: "muted" }, "Son 90 günde talebiniz yok."))])));
}

async function cancelRequest(r) {
  if (!confirm(`${r.kindLabel} talebiniz iptal edilsin mi?`)) return;
  try {
    const result = await api(`/musteri/api/talep/${r.id}/iptal`, { method: "POST" });
    show($("global-msg"), result.message, "ok");
    await loadRequests();
  } catch (error) {
    show($("global-msg"), error.message);
  }
}

$("login-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = event.submitter;
  if (button) button.disabled = true;
  try {
    await api("/musteri/giris", { method: "POST", body: { kullanici: $("login-user").value, sifre: $("login-pass").value } });
    $("login-pass").value = "";
    await start();
  } catch (error) {
    show($("login-msg"), error.message);
  } finally {
    if (button) button.disabled = false;
  }
});

$("logout").addEventListener("click", async () => {
  try { await api("/musteri/cikis", { method: "POST" }); } catch { /* already signed out */ }
  me = null;
  showLogin();
});

$("password-open").addEventListener("click", () => openPassword(false));

$("password-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  if ($("p-new").value !== $("p-repeat").value) { show($("password-msg"), "Yeni şifre ile tekrarı aynı değil."); return; }
  const button = event.submitter;
  if (button) button.disabled = true;
  try {
    const result = await api("/musteri/api/sifre", { method: "POST", body: { mevcut: $("p-current").value, yeni: $("p-new").value, yeniTekrar: $("p-repeat").value } });
    $("password-form").reset();
    await start();
    show($("global-msg"), result.message, "ok");
  } catch (error) {
    show($("password-msg"), error.message);
  } finally {
    if (button) button.disabled = false;
  }
});

$("request-form").addEventListener("submit", async (event) => {
  event.preventDefault();
  const button = event.submitter;
  if (button) button.disabled = true;
  const kind = document.querySelector('input[name="kind"]:checked')?.value;
  const count = $("r-count").value.trim();
  try {
    const result = await api("/musteri/api/talep", { method: "POST", body: { tur: kind, adet: count ? Number(count) : null, not: $("r-note").value.trim() || null } });
    $("r-count").value = "";
    $("r-note").value = "";
    show($("request-msg"), result.message, "ok");
    await loadRequests();
  } catch (error) {
    show($("request-msg"), error.message);
  } finally {
    if (button) button.disabled = false;
  }
});

start().catch(() => showLogin());
