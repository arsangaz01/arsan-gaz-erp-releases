package com.arsantuptakip.elterminali;

import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.text.TextUtils;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.Spinner;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.Collection;
import java.util.List;

// Same trip flow as the phone app's "Sefer" page: load, deliver, pick up empties, return, check, reasons and close.
public final class TripActivity extends BaseActivity {
    private static final int LOAD = 0;
    private static final int DELIVER = 1;
    private static final int RETURN = 2;
    private static final int CHECK = 3;
    private static final int PICKUP = 4;

    private final List<String> modeLabels = new ArrayList<String>();
    private final List<Integer> modeKinds = new ArrayList<Integer>();
    private final List<JSONObject> trips = new ArrayList<JSONObject>();
    private final List<String> plates = new ArrayList<String>();
    private final List<String> scanned = new ArrayList<String>();
    private boolean routes;

    private Spinner tripSpinner;
    private Spinner vehicleSpinner;
    private Spinner modeSpinner;
    private TextView overview;
    private EditText customerSearch;
    private LinearLayout customerBox;
    private TextView customerText;
    private EditText code;
    private TextView scannedText;
    private TextView result;
    private TextView loadText;
    private LinearLayout loadBox;
    private TextView routeText;
    private LinearLayout routeBox;
    private JSONObject customer;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle("Sefer");
        routes = Api.can("sefer-rota");
        addMode("Yükleme", LOAD, "sefer-yukle");
        addMode(Api.can("sefer-bos-alim") ? "Müşteriye teslim (dolu)" : "Müşteriye teslim", DELIVER, "sefer-teslim");
        addMode("Müşteriden boş alım", PICKUP, "sefer-bos-alim");
        addMode("Depo dönüşü", RETURN, "sefer-donus");
        addMode("Kontrol", CHECK, "sefer-goruntule");

        if (Api.can("sefer-baslat")) {
            page.addView(button("Sefer başlat", v -> startTrip()));
        }

        page.addView(heading("Sefer ve araç"));
        tripSpinner = new Spinner(this);
        vehicleSpinner = new Spinner(this);
        overview = note("");
        page.addView(tripSpinner);
        page.addView(overview);
        page.addView(vehicleSpinner);
        Button addVehicle = button("Sefere araç ekle", v -> addVehicle());
        page.addView(Api.can("sefer-baslat") && Api.fixedPlate() == null
            ? row(button("Yenile", v -> refreshTrips(null)), addVehicle)
            : button("Yenile", v -> refreshTrips(null)));

        if (routes) {
            page.addView(heading("Rota (müşteri sırası)"));
            routeText = note("");
            routeBox = vertical();
            page.addView(routeText);
            page.addView(routeBox);
            page.addView(button("Rotaya müşteri ekle", v -> addStop()));
        }

        page.addView(heading("Okutma"));
        modeSpinner = new Spinner(this);
        modeSpinner.setAdapter(adapter(modeLabels));
        page.addView(modeSpinner);
        customerBox = vertical();
        customerSearch = searchField("Müşteri adı veya kodu", this::findCustomer);
        customerText = text("Müşteri seçilmedi.", 16, true);
        customerBox.addView(customerText);
        customerBox.addView(row(customerSearch, button("Müşteri ara", v -> findCustomer())));
        page.addView(customerBox);
        code = scanField("Barkodu okutun", this::addCode);
        page.addView(code);
        page.addView(row(button("Kodu ekle", v -> submit(code, this::addCode)), button("Listeyi temizle", v -> {
            scanned.clear();
            showScanned();
        })));
        scannedText = text("", 16, false);
        page.addView(scannedText);
        page.addView(button("Kaydet / kontrol et", v -> save()));
        result = text("", 16, false);
        page.addView(result);

        page.addView(heading("Yük listesi"));
        loadText = note("");
        loadBox = vertical();
        page.addView(loadText);
        page.addView(loadBox);
        page.addView(button("Yük listesini yenile", v -> refreshLoad()));
        if (Api.can("sefer-teslim") || Api.can("sefer-donus") || Api.can("sefer-kapat")) {
            page.addView(button("Okutulmayan tüplere gerekçe yaz", v -> explainMissing(false, null)));
        }

        if (Api.can("sefer-kapat")) {
            page.addView(button("Seferi kapat", v -> closeTrip()));
        }

        tripSpinner.setOnItemSelectedListener(new Selected(this::tripChanged));
        vehicleSpinner.setOnItemSelectedListener(new Selected(() -> {
            refreshRoute();
            refreshLoad();
        }));
        modeSpinner.setOnItemSelectedListener(new Selected(this::modeChanged));
        modeChanged();
        showScanned();
        refreshTrips(null);
        code.requestFocus();
    }

    private interface Change {
        void on();
    }

    private static final class Selected implements AdapterView.OnItemSelectedListener {
        private final Change change;

        Selected(Change change) {
            this.change = change;
        }

        @Override
        public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
            change.on();
        }

        @Override
        public void onNothingSelected(AdapterView<?> parent) {
            change.on();
        }
    }

    private void addMode(String label, int kind, String permission) {
        if (Api.can(permission)) {
            modeLabels.add(label);
            modeKinds.add(kind);
        }
    }

    private LinearLayout vertical() {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        return box;
    }

    private ArrayAdapter<String> adapter(List<String> items) {
        ArrayAdapter<String> adapter = new ArrayAdapter<String>(this, android.R.layout.simple_spinner_item, new ArrayList<String>(items));
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        return adapter;
    }

    private int mode() {
        int index = modeSpinner.getSelectedItemPosition();
        return index >= 0 && index < modeKinds.size() ? modeKinds.get(index) : CHECK;
    }

    private void selectMode(int kind) {
        int index = modeKinds.indexOf(kind);
        if (index >= 0) {
            modeSpinner.setSelection(index);
        }
    }

    private JSONObject trip() {
        int index = tripSpinner.getSelectedItemPosition();
        return index >= 0 && index < trips.size() ? trips.get(index) : null;
    }

    private String plate() {
        int index = vehicleSpinner.getSelectedItemPosition();
        return index >= 0 && index < plates.size() ? plates.get(index) : null;
    }

    private void modeChanged() {
        customerBox.setVisibility(mode() == DELIVER || mode() == PICKUP ? View.VISIBLE : View.GONE);
    }

    private void findCustomer() {
        pickCustomer(customerSearch.getText().toString(), c -> {
            if (c != null) {
                setCustomer(c);
                code.requestFocus();
            }
        });
    }

    private void setCustomer(JSONObject value) {
        customer = value;
        customerText.setText(value == null ? "Müşteri seçilmedi." : "Müşteri: " + Api.str(value, "name"));
    }

    private void addCode(String value) {
        for (String existing : scanned) {
            if (existing.equalsIgnoreCase(value)) {
                result.setText(value + " zaten listede.");
                result.setTextColor(MUTED);
                return;
            }
        }

        scanned.add(value);
        showScanned();
    }

    private void showScanned() {
        scannedText.setText(scanned.isEmpty() ? "Henüz tüp okutulmadı." : scanned.size() + " tüp:\n" + TextUtils.join(", ", scanned));
    }

    static JSONObject body(Object... pairs) {
        JSONObject json = new JSONObject();
        try {
            for (int i = 0; i < pairs.length; i += 2) {
                Object value = pairs[i + 1];
                json.put((String) pairs[i], value instanceof Collection ? new JSONArray((Collection<?>) value) : value == null ? JSONObject.NULL : value);
            }
        } catch (JSONException e) {
            throw new IllegalArgumentException(e);
        }

        return json;
    }

    private void refreshTrips(final Integer selectId) {
        call(() -> Api.getArray("/api/sefer/aktif"), array -> {
            JSONObject current = trip();
            int keepId = selectId != null ? selectId : current == null ? -1 : current.optInt("id");
            trips.clear();
            List<String> titles = new ArrayList<String>();
            int wanted = -1;
            String own = Api.fixedPlate();
            for (int i = 0; i < array.length(); i++) {
                JSONObject t = array.optJSONObject(i);
                trips.add(t);
                int onTrip = 0;
                JSONArray vehicles = t.optJSONArray("vehicles");
                for (int v = 0; vehicles != null && v < vehicles.length(); v++) {
                    JSONObject vehicle = vehicles.optJSONObject(v);
                    if (vehicle.optBoolean("onTrip")) {
                        onTrip++;
                    }

                    if (wanted < 0 && keepId < 0 && own != null && own.equalsIgnoreCase(Api.str(vehicle, "plate"))) {
                        wanted = i;
                    }
                }

                titles.add(Api.str(t, "title") + " · " + t.optInt("loadedTotal") + " yüklü · " + onTrip + " araç");
                if (t.optInt("id") == keepId) {
                    wanted = i;
                }
            }

            tripSpinner.setAdapter(adapter(titles));
            if (trips.isEmpty()) {
                tripChanged();
                return;
            }

            tripSpinner.setSelection(Math.max(0, wanted));
            tripChanged();
        });
    }

    private void tripChanged() {
        JSONObject t = trip();
        String previous = plate() != null ? plate() : Api.fixedPlate();
        plates.clear();
        if (t == null) {
            overview.setText("Aktif sefer yok." + (Api.can("sefer-baslat") ? " \"Sefer başlat\" ile başlatın." : ""));
            vehicleSpinner.setAdapter(adapter(plates));
            refreshRoute();
            refreshLoad();
            return;
        }

        List<String> lines = new ArrayList<String>();
        lines.add("Yüklü " + t.optInt("loadedTotal") + " · teslim " + t.optInt("deliveredTotal") + " · depo dönüş " + t.optInt("returnedTotal"));
        JSONArray vehicles = t.optJSONArray("vehicles");
        for (int i = 0; vehicles != null && i < vehicles.length(); i++) {
            JSONObject v = vehicles.optJSONObject(i);
            String p = Api.str(v, "plate");
            if (!plates.contains(p)) {
                plates.add(p);
            }

            if (v.optBoolean("onTrip")) {
                lines.add("Araç " + p + " · " + v.optInt("loadedCount") + " tüp (seferde)");
            }
        }

        overview.setText(TextUtils.join("\n", lines));
        vehicleSpinner.setAdapter(adapter(plates));
        int keep = -1;
        for (int i = 0; previous != null && i < plates.size(); i++) {
            if (plates.get(i).equalsIgnoreCase(previous)) {
                keep = i;
            }
        }

        if (keep >= 0) {
            vehicleSpinner.setSelection(keep);
        }

        refreshRoute();
        refreshLoad();
    }

    private void startTrip() {
        final String own = Api.fixedPlate();
        if (own != null) {
            confirm("Sefer başlat", own + " aracınızla yeni sefer başlatılsın mı?", "Başlat", () -> start(java.util.Collections.singletonList(own)));
            return;
        }

        call(() -> Api.getArray("/api/arac-kayit"), all -> {
            final List<String> free = new ArrayList<String>();
            for (int i = 0; i < all.length(); i++) {
                JSONObject v = all.optJSONObject(i);
                if (v.optBoolean("isActive", true) && (v.isNull("activeTripId") || !v.has("activeTripId"))) {
                    free.add(Api.str(v, "plate"));
                }
            }

            if (free.isEmpty()) {
                alert("Sefer", "Boşta araç yok; bütün araçlar başka bir seferde.");
                return;
            }

            final boolean[] picked = new boolean[free.size()];
            new AlertDialog.Builder(this).setTitle("Sefere çıkacak araçlar")
                .setMultiChoiceItems(free.toArray(new String[free.size()]), picked, (dialog, which, isChecked) -> picked[which] = isChecked)
                .setPositiveButton("Başlat", (dialog, which) -> {
                    List<String> chosen = new ArrayList<String>();
                    for (int i = 0; i < picked.length; i++) {
                        if (picked[i]) {
                            chosen.add(free.get(i));
                        }
                    }

                    if (chosen.isEmpty()) {
                        alert("Sefer", "En az bir araç seçin.");
                    } else {
                        start(chosen);
                    }
                })
                .setNegativeButton("Vazgeç", null)
                .show();
        });
    }

    private void start(final List<String> chosen) {
        call(() -> Api.post("/api/sefer/baslat", body("note", null, "plates", chosen)), started -> {
            scanned.clear();
            showScanned();
            selectMode(LOAD);
            refreshTrips(started.optInt("id"));
            alert("Sefer", Api.str(started, "message") + "\n\nŞimdi araca yüklenen tüpleri okutup \"Kaydet\"e basın.");
        });
    }

    private void addVehicle() {
        final JSONObject t = trip();
        if (t == null) {
            alert("Sefer", "Önce aktif bir sefer seçin.");
            return;
        }

        call(() -> Api.getArray("/api/arac-kayit"), all -> {
            final List<String> free = new ArrayList<String>();
            for (int i = 0; i < all.length(); i++) {
                JSONObject v = all.optJSONObject(i);
                String p = Api.str(v, "plate");
                if (v.optBoolean("isActive", true) && (v.isNull("activeTripId") || !v.has("activeTripId")) && !plates.contains(p)) {
                    free.add(p);
                }
            }

            if (free.isEmpty()) {
                alert("Sefer", "Eklenebilecek boşta araç yok.");
                return;
            }

            choose("Sefere eklenecek araç", free, "Vazgeç", index -> {
                if (index >= 0) {
                    call(() -> Api.postMessage("/api/sefer/arac", body("tripId", t.optInt("id"), "plate", free.get(index))), message -> {
                        alert("Sefer", message);
                        refreshTrips(t.optInt("id"));
                    });
                }
            });
        });
    }

    private void save() {
        final JSONObject t = trip();
        if (t == null) {
            alert("Sefer", "Aktif sefer seçin.");
            return;
        }

        final int tripId = t.optInt("id");
        final String p = plate();
        final int kind = mode();
        if ((kind == LOAD || kind == RETURN || kind == PICKUP) && p == null) {
            alert("Sefer", "Yükleme, boş alım ve dönüş için araç seçin.");
            return;
        }

        if ((kind == DELIVER || kind == PICKUP) && customer == null) {
            alert("Sefer", "Önce müşteriyi seçin.");
            return;
        }

        if (kind != CHECK && kind != RETURN && scanned.isEmpty()) {
            alert("Sefer", "En az bir tüp okutun.");
            return;
        }

        final List<String> codes = new ArrayList<String>(scanned);
        final int customerId = customer == null ? 0 : customer.optInt("id");
        call(() -> {
            switch (kind) {
                case LOAD:
                    JSONObject load = Api.post("/api/sefer/yukle", body("tripId", tripId, "plate", p, "codes", codes));
                    List<String> lines = new ArrayList<String>();
                    lines.add(Api.str(load, "message"));
                    JSONArray messages = load.optJSONArray("messages");
                    for (int i = 0; messages != null && i < messages.length(); i++) {
                        lines.add(messages.optString(i));
                    }

                    return new String[] { TextUtils.join("\n", lines), null };
                case DELIVER:
                    return new String[] { Api.postMessage("/api/sefer/teslim", body("tripId", tripId, "customerId", customerId, "codes", codes, "note", null)), null };
                case PICKUP:
                    return new String[] { Api.postMessage("/api/sefer/bos-alim", body("tripId", tripId, "plate", p, "customerId", customerId, "codes", codes, "note", null)), null };
                case RETURN:
                    return reconcile(Api.post("/api/sefer/donus", body("tripId", tripId, "plate", p, "codes", codes, "note", null)));
                default:
                    return reconcile(Api.getObject("/api/sefer/" + tripId + "/kontrol" + (p == null ? "" : "?plaka=" + Api.q(p))));
            }
        }, shown -> {
            result.setText(shown[0]);
            result.setTextColor(shown[1] != null ? RED : MUTED);
            if (shown[1] != null) {
                alert("Sefer uyarısı", shown[0]);
            }

            scanned.clear();
            showScanned();
            refreshTrips(tripId);
        });
    }

    // [text, non-null when the server raised alerts]
    private static String[] reconcile(JSONObject r) {
        StringBuilder text = new StringBuilder(Api.str(r, "message") == null ? "" : Api.str(r, "message"));
        JSONArray unexpected = r.optJSONArray("unexpectedReturnCodes");
        if (unexpected != null && unexpected.length() > 0) {
            text.append("\nEşleşmeyen: ").append(join(unexpected));
        }

        JSONArray shortages = r.optJSONArray("shortagesByGas");
        if (shortages != null && shortages.length() > 0) {
            List<String> parts = new ArrayList<String>();
            for (int i = 0; i < shortages.length(); i++) {
                JSONObject s = shortages.optJSONObject(i);
                parts.add(Api.str(s, "gasType") + " ×" + s.optInt("count"));
            }

            text.append("\nOkutulmayan: ").append(TextUtils.join(", ", parts));
        }

        return new String[] { text.toString(), r.optBoolean("hasAlerts") ? "alert" : null };
    }

    private static String join(JSONArray array) {
        List<String> items = new ArrayList<String>();
        for (int i = 0; i < array.length(); i++) {
            items.add(array.optString(i));
        }

        return TextUtils.join(", ", items);
    }

    private void refreshLoad() {
        final JSONObject t = trip();
        loadBox.removeAllViews();
        if (t == null) {
            loadText.setText("Aktif sefer yok.");
            return;
        }

        final String p = plate();
        call(() -> Api.getObject("/api/sefer/" + t.optInt("id") + "/yuk" + (p == null ? "" : "?plaka=" + Api.q(p))), load -> {
            loadBox.removeAllViews();
            int total = load.optInt("total");
            loadText.setText(total == 0
                ? (p == null ? "Bu sefere henüz tüp yüklenmedi." : p + " aracına henüz tüp yüklenmedi.")
                : (p == null ? "Tüm araçlar" : p) + ": " + total + " tüp yüklendi · araçta " + load.optInt("onVehicle")
                    + (load.optInt("missing") > 0 ? " · (!) " + load.optInt("missing") + " eksik" : ""));
            loadText.setTextColor(load.optInt("missing") > 0 ? RED : MUTED);
            JSONArray groups = load.optJSONArray("groups");
            for (int i = 0; groups != null && i < groups.length(); i++) {
                final JSONObject g = groups.optJSONObject(i);
                loadBox.addView(button(Api.str(g, "gasType") + ": " + Api.str(g, "summary"), v -> showGroup(g)));
            }
        });
    }

    private void showGroup(JSONObject g) {
        List<String> lines = new ArrayList<String>();
        JSONArray cylinders = g.optJSONArray("cylinders");
        for (int i = 0; cylinders != null && i < cylinders.length(); i++) {
            JSONObject c = cylinders.optJSONObject(i);
            lines.add((c.optBoolean("alarm") ? "(!) " : "") + Api.str(c, "serialNumber") + " · " + Api.str(c, "capacity") + " · " + Api.str(c, "fullness")
                + " · " + Api.str(c, "state") + (Api.str(c, "customer") == null ? "" : " · " + Api.str(c, "customer"))
                + (c.optBoolean("testOverdue") ? " · TEST GEÇMİŞ" : ""));
        }

        alert(Api.str(g, "gasType") + " (" + g.optInt("count") + ")", TextUtils.join("\n\n", lines));
    }

    private void refreshRoute() {
        if (!routes) {
            return;
        }

        routeBox.removeAllViews();
        final JSONObject t = trip();
        final String p = plate();
        if (t == null || p == null) {
            routeText.setText("Rota için sefer ve araç seçin.");
            return;
        }

        call(() -> Api.getArray("/api/sefer/" + t.optInt("id") + "/rota?plaka=" + Api.q(p)), stops -> {
            routeBox.removeAllViews();
            int done = 0;
            String next = null;
            for (int i = 0; i < stops.length(); i++) {
                JSONObject s = stops.optJSONObject(i);
                if (s.optBoolean("done")) {
                    done++;
                } else if (next == null) {
                    next = Api.str(s, "customerName");
                }

                routeBox.addView(stopView(t.optInt("id"), s, i + 1));
            }

            routeText.setText(stops.length() == 0
                ? p + " için durak yok. \"Rotaya müşteri ekle\" ile gideceğiniz müşterileri ekleyin."
                : p + ": " + stops.length() + " durak · " + done + " tamamlandı · sıradaki: " + (next == null ? "yok" : next));
        });
    }

    private View stopView(final int tripId, final JSONObject s, int index) {
        LinearLayout box = vertical();
        boolean done = s.optBoolean("done");
        TextView name = text(index + ". " + Api.str(s, "customerName") + (done ? " (tamamlandı)" : ""), 17, true);
        if (done) {
            name.setTextColor(ACCENT);
        }

        box.addView(name);
        String place = StockActivity.join(", ", Api.str(s, "address"), Api.str(s, "district"), Api.str(s, "city"));
        int given = s.optInt("delivered");
        int taken = s.optInt("collected");
        box.addView(note((place.length() == 0 ? "Adres kayıtlı değil" : place)
            + (given + taken > 0 ? "\nBu seferde verilen " + given + ", alınan " + taken : "")
            + (Api.str(s, "note") == null ? "" : "\nNot: " + Api.str(s, "note"))));
        List<View> actions = new ArrayList<View>();
        actions.add(button("Burada", v -> useStop(s)));
        actions.add(button(done ? "Yeniden aç" : "Tamamlandı", v -> call(
            () -> Api.postMessage("/api/sefer/rota/tamam", body("tripId", tripId, "stopId", s.optInt("id"), "done", !s.optBoolean("done"))),
            message -> refreshRoute())));
        final String map = Api.str(s, "mapUrl");
        if (map != null) {
            actions.add(button("Harita", v -> {
                try {
                    startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse(map)));
                } catch (Exception e) {
                    alert("Harita", "Harita açılamadı: " + e.getMessage());
                }
            }));
        }

        box.addView(row(actions.toArray(new View[actions.size()])));
        box.setPadding(0, 0, 0, dp(8));
        return box;
    }

    // The scan section switches to delivery for this customer; "Müşteriden boş alım" keeps the same customer.
    private void useStop(JSONObject s) {
        setCustomer(body("id", s.optInt("customerId"), "name", Api.str(s, "customerName")));
        if (mode() != DELIVER && mode() != PICKUP) {
            selectMode(DELIVER);
        }

        scanned.clear();
        showScanned();
        result.setText(Api.str(s, "customerName") + " seçildi. Tüpleri okutup \"Kaydet\"e basın.");
        result.setTextColor(MUTED);
        code.requestFocus();
    }

    private void addStop() {
        final JSONObject t = trip();
        final String p = plate();
        if (t == null || p == null) {
            alert("Rota", "Önce sefer ve araç seçin.");
            return;
        }

        prompt("Rotaya müşteri ekle", "Müşteri adı veya kodu (en az 2 harf)", "Ara", "Vazgeç", query -> {
            if (query != null) {
                pickCustomer(query, c -> {
                    if (c != null) {
                        call(() -> Api.postMessage("/api/sefer/rota/ekle", body("tripId", t.optInt("id"), "plate", p, "customerId", c.optInt("id"))), message -> {
                            routeText.setText(message);
                            refreshRoute();
                        });
                    }
                });
            }
        });
    }

    private void closeTrip() {
        final JSONObject t = trip();
        if (t == null) {
            alert("Sefer", "Aktif sefer seçin.");
            return;
        }

        confirm("Seferi kapat", Api.str(t, "title") + " kapatılsın mı? Okutulmayan tüpler gerekçeleriyle yöneticiye bildirilir.", "Kapat", () -> {
            Runnable close = () -> call(() -> Api.post("/api/sefer/kapat", body("tripId", t.optInt("id"))), closed -> {
                JSONObject r = closed.optJSONObject("reconcile");
                boolean alerts = r != null && r.optBoolean("hasAlerts");
                result.setText(Api.str(closed, "message"));
                result.setTextColor(alerts ? RED : MUTED);
                alert(alerts ? "Sefer kapatıldı — uyarı" : "Sefer kapatıldı", Api.str(closed, "message"));
                refreshTrips(null);
            });
            if (t.optBoolean("requireReasons")) {
                explainMissing(true, close);
            } else {
                close.run();
            }
        });
    }

    // Asks a reason for every cylinder loaded but neither delivered nor scanned back. With forClose it repeats until
    // nothing is left unexplained and then runs onDone (the server refuses the close otherwise).
    private void explainMissing(final boolean forClose, final Runnable onDone) {
        final JSONObject t = trip();
        if (t == null) {
            alert("Sefer", "Aktif sefer seçin.");
            return;
        }

        final int tripId = t.optInt("id");
        call(() -> new JSONArray[] { Api.getArray("/api/sefer/gerekceler"), Api.getArray("/api/sefer/" + tripId + "/eksikler") }, data -> {
            final JSONArray reasons = data[0];
            JSONArray open = data[1];
            final List<JSONObject> unexplained = new ArrayList<JSONObject>();
            final List<List<JSONObject>> choices = new ArrayList<List<JSONObject>>();
            List<String> labels = new ArrayList<String>();
            for (int i = 0; i < open.length(); i++) {
                JSONObject c = open.optJSONObject(i);
                if (Api.str(c, "reason") == null) {
                    unexplained.add(c);
                }
            }

            if (forClose && unexplained.isEmpty()) {
                onDone.run();
                return;
            }

            if (open.length() == 0) {
                alert("Gerekçe", "Bu seferde teslim veya dönüş okutması eksik tüp yok.");
                return;
            }

            if (unexplained.size() > 1) {
                choices.add(unexplained);
                labels.add("Gerekçesiz " + unexplained.size() + " tüpün tümü");
            }

            for (int i = 0; i < open.length(); i++) {
                JSONObject c = open.optJSONObject(i);
                List<JSONObject> one = new ArrayList<JSONObject>();
                one.add(c);
                choices.add(one);
                String label = Api.str(c, "reasonLabel");
                labels.add(Api.str(c, "serialNumber") + " · " + Api.str(c, "gasType") + " · " + Api.str(c, "plate") + " · " + (label == null ? "gerekçe yok" : label));
            }

            String title = forClose ? "Kapatmadan önce " + unexplained.size() + " tüp için gerekçe seçin" : "Okutulmayan tüpler (" + unexplained.size() + " gerekçesiz)";
            choose(title, labels, forClose ? "Vazgeç (sefer kapanmaz)" : "Bitti", index -> {
                if (index >= 0) {
                    explain(tripId, choices.get(index), reasons, () -> explainMissing(forClose, onDone));
                }
            });
        });
    }

    private void explain(final int tripId, final List<JSONObject> cylinders, final JSONArray reasons, final Runnable next) {
        List<String> labels = new ArrayList<String>();
        for (int i = 0; i < reasons.length(); i++) {
            labels.add(Api.str(reasons.optJSONObject(i), "label"));
        }

        String title = cylinders.size() == 1 ? Api.str(cylinders.get(0), "serialNumber") + " neden okutulmadı?" : cylinders.size() + " tüp neden okutulmadı?";
        choose(title, labels, "Vazgeç", index -> {
            if (index < 0) {
                next.run();
                return;
            }

            final JSONObject reason = reasons.optJSONObject(index);
            final List<Integer> ids = new ArrayList<Integer>();
            for (JSONObject c : cylinders) {
                ids.add(c.optInt("cylinderId"));
            }

            final Text send = note -> {
                if (note == null) {
                    next.run();
                    return;
                }

                call(() -> Api.postMessage("/api/sefer/gerekce",
                    body("tripId", tripId, "cylinderIds", ids, "reason", Api.str(reason, "key"), "note", note.length() == 0 ? null : note, "customerId", null)), message -> {
                    result.setText(message);
                    result.setTextColor(MUTED);
                    next.run();
                });
            };
            final Text askNote = intro -> prompt(Api.str(reason, "label"), intro, "Kaydet", "Vazgeç", note -> {
                if (note != null && note.length() == 0 && reason.optBoolean("noteRequired")) {
                    alert("Gerekçe", "Bu gerekçe için açıklama zorunlu.", next);
                    return;
                }

                send.on(note);
            });
            if (!reason.optBoolean("asksCustomer")) {
                askNote.on(reason.optBoolean("noteRequired") ? "Açıklama yazın (araç, yer veya ayrıntı)." : "İsterseniz açıklama yazın (boş bırakılabilir).");
                return;
            }

            prompt("Müşteri", "Tüpün kaldığı müşteriyi arayın (en az 2 harf). Boş bırakırsanız adı elle yazarsınız.", "Ara", "Elle yaz", query -> {
                if (query == null || query.length() < 2) {
                    askNote.on("Müşteri seçilmedi. Müşteri adını veya ayrıntıyı yazın.");
                    return;
                }

                pickCustomer(query, c -> {
                    if (c == null) {
                        askNote.on("Müşteri seçilmedi. Müşteri adını veya ayrıntıyı yazın.");
                        return;
                    }

                    call(() -> Api.postMessage("/api/sefer/gerekce",
                        body("tripId", tripId, "cylinderIds", ids, "reason", Api.str(reason, "key"), "note", null, "customerId", c.optInt("id"))), message -> {
                        result.setText(message);
                        result.setTextColor(MUTED);
                        next.run();
                    });
                });
            });
        });
    }
}
