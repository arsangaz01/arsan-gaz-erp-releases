"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { doneCheck, formatDoneCheck, requestedComponents } = require("./done-check");

// Shape of 0311907 (Sefer): new DbSets + SQLite schema, mobile page, no PostgreSQL schema, no web panel.
const seferFiles = [
  "Data/ArsanGazDbContext.cs",
  "Services/CylinderSchemaService.cs",
  "Tools/MobileApi/CylinderTripEndpoints.cs",
  "Tools/MobileApp/Pages/CylinderTripPage.cs",
  "Tools/MobileApp/ArsanGazMobile.csproj",
];
const seferDiff = [
  "diff --git a/Data/ArsanGazDbContext.cs b/Data/ArsanGazDbContext.cs",
  "--- a/Data/ArsanGazDbContext.cs",
  "+++ b/Data/ArsanGazDbContext.cs",
  "@@ -11,0 +12,1 @@",
  "+ public DbSet<CylinderTrip> CylinderTrips => Set<CylinderTrip>();",
  "diff --git a/Services/CylinderSchemaService.cs b/Services/CylinderSchemaService.cs",
  "--- a/Services/CylinderSchemaService.cs",
  "+++ b/Services/CylinderSchemaService.cs",
  "@@ -27,0 +28,1 @@",
  "+CREATE TABLE IF NOT EXISTS CylinderTrips (",
  "diff --git a/Tools/MobileApp/ArsanGazMobile.csproj b/Tools/MobileApp/ArsanGazMobile.csproj",
  "--- a/Tools/MobileApp/ArsanGazMobile.csproj",
  "+++ b/Tools/MobileApp/ArsanGazMobile.csproj",
  "@@ -30,2 +30,2 @@",
  "-\t\t<ApplicationVersion>23</ApplicationVersion>",
  "+\t\t<ApplicationVersion>24</ApplicationVersion>",
].join("\n");
const request = "Sefer bazlı tüp takibi: mobilde araç yükleme okutması, eksikler web panelinde görünsün";

test("missing PostgreSQL schema for a new table is flagged", () => {
  const result = doneCheck({ requestText: request, files: seferFiles, diff: seferDiff });
  assert.ok(result.warnings.some((warning) => /Data\/PostgresSchema\.cs değişmemiş/.test(warning)));
  assert.ok(result.warnings.some((warning) => /ArsanGazDbContext\.cs/.test(warning)));
});

test("requested web panel that was not touched is flagged, done parts are listed", () => {
  const result = doneCheck({ requestText: request, files: seferFiles, diff: seferDiff });
  assert.ok(result.checklist.includes("✅ Mobil uygulama: dosyaları değişti"));
  assert.ok(result.checklist.includes("❌ Web paneli/sitesi: istekte geçiyor ama bu işte dosyası değişmedi"));
  assert.ok(result.warnings.some((warning) => /Web paneli\/sitesi/.test(warning)));
  assert.match(formatDoneCheck(result), /🔎 Otomatik kontrol/);
});

test("complete change (PostgreSQL + panel + version bump) has no warnings", () => {
  const files = [...seferFiles, "Data/PostgresSchema.cs", "Tools/MobileApi/WebPanel/panel.js"];
  const result = doneCheck({ requestText: request, files, diff: seferDiff });
  assert.deepEqual(result.warnings, []);
});

test("mobile change without an ApplicationVersion bump is flagged", () => {
  const result = doneCheck({
    requestText: "mobil ekranda buton rengi",
    files: ["Tools/MobileApp/Pages/HomePage.cs"],
    diff: "+++ b/Tools/MobileApp/Pages/HomePage.cs\n+ Color = Colors.Navy;",
  });
  assert.ok(result.warnings.some((warning) => /ApplicationVersion artırılmamış/.test(warning)));
});

test("display-only csproj edit does not count as a version bump", () => {
  const result = doneCheck({
    requestText: "mobil",
    files: ["Tools/MobileApp/ArsanGazMobile.csproj", "Tools/MobileApp/App.xaml.cs"],
    diff: "+++ b/Tools/MobileApp/ArsanGazMobile.csproj\n+<ApplicationDisplayVersion>1.7.7</ApplicationDisplayVersion>",
  });
  assert.ok(result.warnings.some((warning) => /ApplicationVersion artırılmamış/.test(warning)));
});

test("no commits means no automatic check text", () => {
  const result = doneCheck({ requestText: request, files: [], diff: "" });
  assert.equal(formatDoneCheck(result), "");
});

test("components are recognised in Turkish requests", () => {
  assert.deepEqual(requestedComponents("Masaüstü ERP'de ve Gözcü'de rapor"), ["masaustu", "watcher"]);
  assert.deepEqual(requestedComponents("telefondan sipariş, web sitesinde liste"), ["mobil", "web"]);
  assert.deepEqual(requestedComponents("kapı numarası"), []);
});
