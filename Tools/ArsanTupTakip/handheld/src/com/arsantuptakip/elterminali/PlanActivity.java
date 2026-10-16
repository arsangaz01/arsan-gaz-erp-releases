package com.arsantuptakip.elterminali;

import android.content.Intent;
import android.net.Uri;
import android.os.Bundle;
import android.view.View;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.Locale;

// Today's customer order prepared in the panel; a fixed-plate driver only gets the plans of their own vehicle.
public final class PlanActivity extends BaseActivity {
    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle("Bugünkü sefer planım");
        load();
    }

    private void load() {
        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.getObject("/api/sefer-plani/bugun");
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject response) {
                show(response);
            }
        });
    }

    private void show(JSONObject response) {
        page.removeAllViews();
        page.addView(button("Yenile", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                load();
            }
        }));
        JSONArray plans = response.optJSONArray("plans");
        if (!response.optBoolean("on") || plans == null) {
            page.addView(note("Sefer planı özelliği kapalı."));
            return;
        }

        if (plans.length() == 0) {
            String plate = Api.fixedPlate();
            page.addView(note(plate == null ? "Bugün için plan yok." : "Bugün " + plate + " için plan yok."));
            return;
        }

        for (int p = 0; p < plans.length(); p++) {
            JSONObject plan = plans.optJSONObject(p);
            page.addView(heading("Araç " + Api.str(plan, "plate") + " · " + plan.optInt("done") + "/" + plan.optInt("stopCount") + " tamamlandı"));
            if (Api.str(plan, "note") != null) {
                page.addView(note("Not: " + Api.str(plan, "note")));
            }

            JSONArray stops = plan.optJSONArray("stops");
            for (int i = 0; stops != null && i < stops.length(); i++) {
                final JSONObject stop = stops.optJSONObject(i);
                boolean done = stop.optBoolean("done");
                TextView name = text((i + 1) + ". " + Api.str(stop, "customerName") + (done ? " (tamamlandı)" : ""), 17, true);
                if (done) {
                    name.setTextColor(ACCENT);
                }

                page.addView(name);
                String place = StockActivity.join(", ", Api.str(stop, "address"), Api.str(stop, "district"), Api.str(stop, "city"));
                page.addView(note((place.length() == 0 ? "Adres kayıtlı değil" : place)
                    + (Api.str(stop, "note") == null ? "" : "\nNot: " + Api.str(stop, "note"))));
                if (hasCoordinates(stop)) {
                    page.addView(button("Haritada aç", new View.OnClickListener() {
                        @Override
                        public void onClick(View v) {
                            openMap(stop);
                        }
                    }));
                }
            }
        }
    }

    private static boolean hasCoordinates(JSONObject stop) {
        return stop != null
            && stop.has("latitude") && !stop.isNull("latitude")
            && stop.has("longitude") && !stop.isNull("longitude");
    }

    private void openMap(JSONObject stop) {
        if (!hasCoordinates(stop)) {
            alert("Harita", "Bu durak için enlem/boylam eksik.");
            return;
        }
        String point = String.format(Locale.US, "%.6f,%.6f", stop.optDouble("latitude"), stop.optDouble("longitude"));
        try {
            startActivity(new Intent(Intent.ACTION_VIEW, Uri.parse("geo:" + point + "?q=" + point + "(" + Uri.encode(Api.str(stop, "customerName")) + ")")));
        } catch (Exception e) {
            alert("Harita", "Bu cihazda harita uygulaması yok. Konum: " + point);
        }
    }
}
