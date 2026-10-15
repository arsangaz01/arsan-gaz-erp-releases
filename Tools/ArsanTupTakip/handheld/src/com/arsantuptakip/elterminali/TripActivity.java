package com.arsantuptakip.elterminali;

import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.drawable.Drawable;
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

// Same trip flow as the phone app's "Sefer" page, as numbered steps: load the vehicle, deliver to one customer after
// another (optionally taking their empties), unload the vehicle at the depot (remaining fulls and collected empties),
// then close the trip with reasons for anything unscanned.
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

    private final List<Button> modeButtons = new ArrayList<Button>();
    private final List<Drawable> modeBackgrounds = new ArrayList<Drawable>();
    private final List<ColorStateList> modeTextColors = new ArrayList<ColorStateList>();
    private int current = CHECK;
    private boolean firstTrip = true;
    private Spinner tripSpinner;
    private Spinner vehicleSpinner;
    private TextView stepHint;
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
    private Button startBig;
    private TextView tripLine;
    private LinearLayout pickers;
    private LinearLayout work;
    private LinearLayout tripTiles;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle("Sefer");
        routes = Api.can("sefer-rota");
        addMode("Araca yükle", LOAD, "sefer-yukle");
        addMode("Müşteriye teslim", DELIVER, "sefer-teslim");
        addMode("Müşteriden boş al", PICKUP, "sefer-bos-alim");
        addMode("Aracı boşalt (depo)", RETURN, "sefer-donus");
        if (!modeKinds.isEmpty()) {
            current = modeKinds.get(0);
        }

        // Simple view for drivers: trip line, step buttons, scan and save; everything else sits under "Ayrıntılar".
        startBig = big(button("🚚 SEFER BAŞLAT", v -> startTrip()));
        startBig.setVisibility(View.GONE);
        page.addView(startBig);
        tripLine = text("Seferler yükleniyor...", 20, true);
        page.addView(tripLine);
        tripTiles = new LinearLayout(this);
        page.addView(tripTiles);
        tripSpinner = new Spinner(this);
        vehicleSpinner = new Spinner(this);
        pickers = vertical();
        pickers.addView(tripSpinner);
        pickers.addView(vehicleSpinner);
        pickers.setVisibility(View.GONE);
        page.addView(pickers);

        work = vertical();
        work.setVisibility(View.GONE);
        page.addView(work);
        for (int i = 0; i < modeKinds.size(); i++) {
            final int kind = modeKinds.get(i);
            Button step = big(button(icon(kind) + " " + (i + 1) + ". " + modeLabels.get(i), v -> selectMode(kind)));
            modeButtons.add(step);
            modeBackgrounds.add(step.getBackground());
            modeTextColors.add(step.getTextColors());
            work.addView(step);
        }

        stepHint = text("", 17, true);
        stepHint.setTextColor(ACCENT);
        work.addView(stepHint);

        customerBox = vertical();
        customerBox.addView(heading("HANGİ MÜŞTERİ?"));
        customerSearch = searchField("Müşteri adı, kodu veya şehri", this::findCustomer);
        customerText = text("Müşteri seçilmedi.", 16, true);
        customerBox.addView(customerText);
        customerBox.addView(row(customerSearch, button("Müşteri ara", v -> findCustomer())));
        work.addView(customerBox);

        code = scanField("Barkodu okutun", this::addCode);
        work.addView(code);
        scannedText = text("", 16, false);
        work.addView(scannedText);
        work.addView(big(button("✅ KAYDET", v -> save(mode()))));
        result = text("", 16, false);
        work.addView(result);

        if (Api.can("sefer-donus") || Api.can("sefer-kapat")) {
            work.addView(heading("SEFERİ BİTİR"));
            work.addView(button("Okutulmayan tüplere gerekçe yaz", v -> explainMissing(false, null)));
            if (Api.can("sefer-kapat")) {
                work.addView(button("Seferi kapat", v -> closeTrip(true)));
            }
        }

        final LinearLayout details = vertical();
        details.setVisibility(View.GONE);
        final Button toggle = button("Ayrıntılar (rota, yük listesi, diğer)", null);
        toggle.setOnClickListener(v -> {
            boolean show = details.getVisibility() != View.VISIBLE;
            details.setVisibility(show ? View.VISIBLE : View.GONE);
            toggle.setText(show ? "Ayrıntıları gizle" : "Ayrıntılar (rota, yük listesi, diğer)");
        });
        page.addView(toggle);
        page.addView(details);

        details.addView(heading("SEFER AYRINTISI"));
        overview = note("");
        details.addView(overview);
        Button addVehicle = button("Sefere araç ekle", v -> addVehicle());
        details.addView(Api.can("sefer-baslat") && Api.fixedPlate() == null
            ? row(button("Yenile", v -> refreshTrips(null)), addVehicle)
            : button("Yenile", v -> refreshTrips(null)));
        if (Api.can("sefer-baslat")) {
            details.addView(button("Yeni sefer başlat", v -> startTrip()));
        }

        details.addView(heading("OKUTMA SEÇENEKLERİ"));
        details.addView(row(button("Kodu ekle", v -> submit(code, this::addCode)), button("Okutulanları temizle", v -> {
            scanned.clear();
            showScanned();
        })));

        if (routes) {
            details.addView(heading("ROTA (MÜŞTERİ SIRASI)"));
            routeText = note("");
            routeBox = vertical();
            details.addView(routeText);
            details.addView(routeBox);
            details.addView(button("Rotaya müşteri ekle", v -> addStop()));
        }

        details.addView(heading("YÜK LİSTESİ"));
        loadText = note("");
        loadBox = vertical();
        details.addView(loadText);
        details.addView(loadBox);
        details.addView(row(button("Yük listesini yenile", v -> refreshLoad()), button("Kontrol", v -> save(CHECK))));

        tripSpinner.setOnItemSelectedListener(new Selected(this::tripChanged));
        vehicleSpinner.setOnItemSelectedListener(new Selected(() -> {
            updateSimple();
            refreshRoute();
            refreshLoad();
        }));
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

    private Button big(Button button) {
        button.setTextSize(android.util.TypedValue.COMPLEX_UNIT_SP, 20);
        button.setMinHeight(dp(64));
        return button;
    }

    private static String icon(int kind) {
        switch (kind) {
            case LOAD:
                return "📦";
            case DELIVER:
                return "🏠";
            case PICKUP:
                return "🔁";
            case RETURN:
                return "🏭";
            default:
                return "🔍";
        }
    }

    private void updateSimple() {
        JSONObject t = trip();
        tripTiles.removeAllViews();
        startBig.setVisibility(t == null && Api.can("sefer-baslat") ? View.VISIBLE : View.GONE);
        work.setVisibility(t == null ? View.GONE : View.VISIBLE);
        pickers.setVisibility(trips.size() > 1 || plates.size() > 1 ? View.VISIBLE : View.GONE);
        if (t == null) {
            tripLine.setText(Api.can("sefer-baslat")
                ? "Aktif sefer yok. Başlamak için \"SEFER BAŞLAT\"a basın."
                : "Aktif sefer yok. Sefer depoda başlatılınca burada görünür.");
            return;
        }

        String p = plate();
        int loaded = 0;
        JSONArray vehicles = t.optJSONArray("vehicles");
        for (int i = 0; p != null && vehicles != null && i < vehicles.length(); i++) {
            JSONObject v = vehicles.optJSONObject(i);
            if (p.equalsIgnoreCase(Api.str(v, "plate"))) {
                loaded = v.optInt("onVehicle", v.optInt("loadedCount"));
            }
        }

        tripLine.setText("🚚 " + (p == null ? "Araç seçin" : p));
        tripTiles.addView(tiles(
            tile("📦", String.valueOf(loaded), "Araçta tüp", ACCENT),
            tile("🏠", String.valueOf(t.optInt("deliveredTotal")), "Teslim edilen", GOOD)), new LinearLayout.LayoutParams(
            android.view.ViewGroup.LayoutParams.MATCH_PARENT, android.view.ViewGroup.LayoutParams.WRAP_CONTENT));
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
        return current;
    }

    private void selectMode(int kind) {
        if (modeKinds.contains(kind)) {
            current = kind;
            modeChanged();
        }
    }

    private String step(int kind) {
        int index = modeKinds.indexOf(kind);
        return index < 0 ? "" : "\"" + (index + 1) + ". " + modeLabels.get(index) + "\"";
    }

    private String hint(int kind) {
        switch (kind) {
            case LOAD:
                return "Araca koyduğunuz tüpleri okutun, sonra KAYDET'e basın.";
            case DELIVER:
                return "Müşteriyi bulun, verdiğiniz tüpleri okutun, KAYDET'e basın.";
            case PICKUP:
                return "Müşteriyi bulun, aldığınız boş tüpleri okutun, KAYDET'e basın.";
            case RETURN:
                return "Araçtan inen bütün tüpleri (dolu ve boş) okutun, KAYDET'e basın.";
            default:
                return "Sefer işlemi için yetkiniz yok.";
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
        boolean needsCustomer = mode() == DELIVER || mode() == PICKUP;
        customerBox.setVisibility(needsCustomer ? View.VISIBLE : View.GONE);
        for (int i = 0; i < modeButtons.size(); i++) {
            Button b = modeButtons.get(i);
            if (modeKinds.get(i) == current) {
                b.setBackgroundColor(ACCENT);
                b.setTextColor(Color.WHITE);
            } else {
                b.setBackground(modeBackgrounds.get(i));
                b.setTextColor(modeTextColors.get(i));
            }
        }

        stepHint.setText(hint(current));
        if (needsCustomer && customer == null) {
            customerSearch.requestFocus();
        } else {
            code.requestFocus();
        }
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
                banner(result, "⚠", value + " zaten listede.", WARN);
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
            overview.setText("Aktif sefer yok." + (Api.can("sefer-baslat") ? " \"Yeni sefer başlat\" ile başlatın." : ""));
            vehicleSpinner.setAdapter(adapter(plates));
            updateSimple();
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
                lines.add("Araç " + p + " · " + v.optInt("loadedCount") + " yüklendi · araçta " + v.optInt("onVehicle"));
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

        if (firstTrip) {
            firstTrip = false;
            String p = plate();
            for (int i = 0; p != null && vehicles != null && i < vehicles.length(); i++) {
                JSONObject v = vehicles.optJSONObject(i);
                if (p.equalsIgnoreCase(Api.str(v, "plate")) && v.optBoolean("onTrip") && v.optInt("onVehicle", v.optInt("loadedCount")) > 0) {
                    selectMode(modeKinds.contains(RETURN) ? RETURN : DELIVER);
                }
            }
        }

        updateSimple();
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

    private void save(final int kind) {
        final JSONObject t = trip();
        if (t == null) {
            alert("Sefer", "Aktif sefer seçin.");
            return;
        }

        final int tripId = t.optInt("id");
        final String p = plate();
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
            boolean failed = shown[0] != null && (shown[0].startsWith("0/") || shown[0].startsWith("0 tüp"));
            if (failed) {
                banner(result, "❌", shown[0], RED);
            } else if (shown[1] != null) {
                banner(result, "⚠", shown[0], WARN);
            } else {
                banner(result, "✅", shown[0], GOOD);
            }

            buzz();
            scanned.clear();
            showScanned();
            refreshTrips(tripId);
            next(kind, shown);
        });
    }

    // Leads the driver to the next step: after loading to delivery, after a delivery to the empties of the same
    // customer or the next customer, after unloading the vehicle to closing the trip.
    private void next(int kind, String[] shown) {
        boolean failed = shown[0] != null && (shown[0].startsWith("0/") || shown[0].startsWith("0 tüp"));
        if (failed || shown[1] != null && kind != RETURN) {
            alert(failed ? "Kaydedilemedi" : "Sefer uyarısı", shown[0]);
            return;
        }

        switch (kind) {
            case LOAD:
                if (modeKinds.contains(DELIVER)) {
                    selectMode(DELIVER);
                    alert("Yükleme kaydedildi", shown[0] + "\n\nŞimdi " + step(DELIVER) + ": müşteriyi seçip teslim ettiğiniz tüpleri okutun.");
                }
                break;
            case DELIVER:
                if (modeKinds.contains(PICKUP)) {
                    new AlertDialog.Builder(this).setTitle("Teslim kaydedildi").setMessage(shown[0] + "\n\nBu müşteriden boş tüp aldınız mı?")
                        .setPositiveButton("Evet, boşları okut", (dialog, which) -> selectMode(PICKUP))
                        .setNegativeButton("Hayır, sonraki müşteri", (dialog, which) -> nextCustomer())
                        .setCancelable(false)
                        .show();
                } else {
                    alert("Teslim kaydedildi", shown[0]);
                    nextCustomer();
                }
                break;
            case PICKUP:
                alert("Boş alım kaydedildi", shown[0]);
                nextCustomer();
                break;
            case RETURN:
                if (Api.can("sefer-kapat")) {
                    new AlertDialog.Builder(this).setTitle(shown[1] != null ? "Araç boşaltıldı — uyarı" : "Araç boşaltıldı")
                        .setMessage(shown[0] + "\n\nSefer şimdi kapatılsın mı?")
                        .setPositiveButton("Seferi kapat", (dialog, which) -> closeTrip(false))
                        .setNegativeButton("Daha sonra", null)
                        .show();
                } else if (shown[1] != null) {
                    alert("Sefer uyarısı", shown[0]);
                }
                break;
            default:
                break;
        }
    }

    private void nextCustomer() {
        setCustomer(null);
        customerSearch.setText("");
        selectMode(DELIVER);
        stepHint.setText("Sıradaki müşteriyi seçin ve teslim ettiğiniz tüpleri okutun. Teslimatlar bitince "
            + (modeKinds.contains(RETURN) ? "depoda " + step(RETURN) + "." : "depoya dönün; aracı depoda operatör boşaltıp seferi kapatır."));
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
        banner(result, "👤", Api.str(s, "customerName") + " seçildi. Tüpleri okutup KAYDET'e basın.", ACCENT);
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

    // ask: confirm first, and point to unloading when a vehicle of the trip has not been unloaded yet.
    private void closeTrip(boolean ask) {
        final JSONObject t = trip();
        if (t == null) {
            alert("Sefer", "Aktif sefer seçin.");
            return;
        }

        final Runnable go = () -> {
            Runnable close = () -> call(() -> Api.post("/api/sefer/kapat", body("tripId", t.optInt("id"))), closed -> {
                JSONObject r = closed.optJSONObject("reconcile");
                boolean alerts = r != null && r.optBoolean("hasAlerts");
                banner(result, alerts ? "⚠" : "🏁", Api.str(closed, "message"), alerts ? WARN : GOOD);
                alert(alerts ? "Sefer kapatıldı — uyarı" : "Sefer kapatıldı", Api.str(closed, "message"));
                setCustomer(null);
                if (!modeKinds.isEmpty()) {
                    selectMode(modeKinds.get(0));
                }

                refreshTrips(null);
            });
            if (t.optBoolean("requireReasons")) {
                explainMissing(true, close);
            } else {
                close.run();
            }
        };
        if (!ask) {
            go.run();
            return;
        }

        List<String> loaded = new ArrayList<String>();
        JSONArray vehicles = t.optJSONArray("vehicles");
        for (int i = 0; vehicles != null && i < vehicles.length(); i++) {
            JSONObject v = vehicles.optJSONObject(i);
            if (v.optBoolean("onTrip")) {
                loaded.add(Api.str(v, "plate"));
            }
        }

        if (!loaded.isEmpty() && modeKinds.contains(RETURN)) {
            new AlertDialog.Builder(this).setTitle("Araç boşaltılmadı")
                .setMessage(TextUtils.join(", ", loaded) + " henüz boşaltılmadı. Önce depoda " + step(RETURN)
                    + " ile indirdiğiniz tüpleri (kalan dolular ve boşlar) okutun, sonra seferi kapatın.")
                .setPositiveButton("Aracı boşalt", (dialog, which) -> {
                    int index = plates.indexOf(loaded.get(0));
                    if (index >= 0) {
                        vehicleSpinner.setSelection(index);
                    }

                    selectMode(RETURN);
                })
                .setNeutralButton("Yine de kapat", (dialog, which) -> go.run())
                .setNegativeButton("Vazgeç", null)
                .show();
            return;
        }

        confirm("Seferi kapat", Api.str(t, "title") + " kapatılsın mı? Okutulmayan tüpler gerekçeleriyle yöneticiye bildirilir.", "Kapat", go);
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
                banner(result, "📝", message, GOOD);
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
                banner(result, "📝", message, GOOD);
                next.run();
                    });
                });
            });
        });
    }
}
