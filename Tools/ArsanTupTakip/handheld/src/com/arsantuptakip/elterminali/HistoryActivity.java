package com.arsantuptakip.elterminali;

import android.os.Bundle;
import android.view.View;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

// Home → Tamamlanmış seferler: same /api/sefer/gecmis list as the phone app (depot sees own closes).
public final class HistoryActivity extends BaseActivity {
    private int tripId;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        tripId = getIntent().getIntExtra("tripId", 0);
        if (tripId > 0) {
            setTitle("Sefer #" + tripId);
            loadDetail();
        } else {
            setTitle("Tamamlanmış seferler");
            loadList();
        }
    }

    private void loadList() {
        call(new Job<JSONArray>() {
            @Override
            public JSONArray run() throws Exception {
                return Api.getArray("/api/sefer/gecmis?adet=50");
            }
        }, new Done<JSONArray>() {
            @Override
            public void ok(JSONArray trips) {
                showList(trips);
            }
        });
    }

    private void showList(JSONArray trips) {
        page.removeAllViews();
        page.addView(button("Yenile", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                loadList();
            }
        }));
        page.addView(note("Sefer numarasına göre yeniden eskiye. Dokunun: detay."));
        if (trips == null || trips.length() == 0) {
            page.addView(note("Henüz tamamlanmış sefer yok."));
            return;
        }

        for (int i = 0; i < trips.length(); i++) {
            final JSONObject trip = trips.optJSONObject(i);
            if (trip == null) {
                continue;
            }

            String plates = joinPlates(trip.optJSONArray("plates"));
            TextView head = text(Api.str(trip, "number") + " · " + Api.str(trip, "title"), 18, true);
            page.addView(head);
            page.addView(note("🚚 " + plates
                + "\nBaşlangıç " + Api.str(trip, "started")
                + (Api.str(trip, "closed") == null ? "" : " · Bitiş " + Api.str(trip, "closed"))
                + "\n📦 " + trip.optInt("loaded") + " yük · 🤝 " + trip.optInt("delivered")
                + " teslim · ↩️ " + trip.optInt("pickedUp") + " alım · 🏭 " + trip.optInt("returned") + " dönüş"));
            page.addView(button("Detay", new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    startActivity(new android.content.Intent(HistoryActivity.this, HistoryActivity.class)
                        .putExtra("tripId", trip.optInt("id")));
                }
            }));
        }
    }

    private void loadDetail() {
        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.getObject("/api/sefer/" + tripId + "/detay");
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject detail) {
                showDetail(detail);
            }
        });
    }

    private void showDetail(JSONObject detail) {
        page.removeAllViews();
        page.addView(button("Yenile", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                loadDetail();
            }
        }));
        JSONObject trip = detail.optJSONObject("trip");
        if (trip == null) {
            page.addView(note("Sefer bulunamadı."));
            return;
        }

        setTitle(Api.str(trip, "number"));
        page.addView(heading(Api.str(trip, "number") + " · " + Api.str(trip, "title")));
        String plates = joinPlates(trip.optJSONArray("plates"));
        page.addView(note("🚚 " + plates
            + "\nBaşlangıç " + Api.str(trip, "started")
            + (Api.str(trip, "closed") == null ? "" : "\nBitiş " + Api.str(trip, "closed"))
            + (Api.str(trip, "note") == null ? "" : "\nNot: " + Api.str(trip, "note"))));
        page.addView(text("📦 " + trip.optInt("loaded") + "  🤝 " + trip.optInt("delivered")
            + "  ↩️ " + trip.optInt("pickedUp") + "  🏭 " + trip.optInt("returned"), 17, true));

        page.addView(heading("Müşteriler"));
        JSONArray customers = detail.optJSONArray("customers");
        if (customers == null || customers.length() == 0) {
            page.addView(note("Bu seferde müşteri kaydı yok."));
        } else {
            for (int i = 0; i < customers.length(); i++) {
                JSONObject c = customers.optJSONObject(i);
                page.addView(note(Api.str(c, "text")));
            }
        }

        page.addView(heading("Tüpler"));
        JSONArray cylinders = detail.optJSONArray("cylinders");
        if (cylinders == null || cylinders.length() == 0) {
            page.addView(note("Bu seferde tüp yok."));
            return;
        }

        for (int i = 0; i < cylinders.length(); i++) {
            JSONObject c = cylinders.optJSONObject(i);
            String state = Api.str(c, "delivered") != null ? "teslim " + Api.str(c, "delivered")
                : Api.str(c, "returned") != null ? "dönüş " + Api.str(c, "returned")
                : c.optBoolean("missing") ? "⚠ eksik" : "—";
            if (c.optBoolean("pickup")) {
                state = "alım · " + state;
            }

            TextView line = note(Api.str(c, "serialNumber") + " · " + Api.str(c, "gasType") + " · " + Api.str(c, "plate")
                + "\n" + state
                + (Api.str(c, "reasonLabel") == null ? "" : " · " + Api.str(c, "reasonLabel")));
            if (c.optBoolean("missing")) {
                line.setTextColor(RED);
            }

            page.addView(line);
        }
    }

    private static String joinPlates(JSONArray plates) {
        if (plates == null || plates.length() == 0) {
            return "-";
        }

        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < plates.length(); i++) {
            if (i > 0) {
                sb.append(", ");
            }

            sb.append(plates.optString(i));
        }

        return sb.toString();
    }
}
