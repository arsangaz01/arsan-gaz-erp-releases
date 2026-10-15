package com.arsantuptakip.elterminali;

import android.os.Bundle;
import android.view.View;
import android.widget.EditText;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

// Cylinders at one customer: totals, per gas/size group and every cylinder with its days there.
public final class StockActivity extends BaseActivity {
    private EditText search;
    private TextView title;
    private TextView summary;
    private TextView groups;
    private TextView cylinders;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle("Müşteri stok bilgisi");
        search = searchField("Müşteri adı veya kodu", new Runnable() {
            @Override
            public void run() {
                find();
            }
        });
        page.addView(search);
        page.addView(button("Müşteri ara", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                find();
            }
        }));
        title = heading("");
        summary = text("", 16, false);
        groups = text("", 16, false);
        cylinders = text("", 15, false);
        page.addView(title);
        page.addView(summary);
        page.addView(groups);
        page.addView(cylinders);
    }

    private void find() {
        pickCustomer(search.getText().toString(), new Customer() {
            @Override
            public void on(JSONObject customer) {
                if (customer != null) {
                    load(customer.optInt("id"));
                }
            }
        });
    }

    private void load(final int id) {
        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.getObject("/api/musteri/" + id + "/stok");
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject stock) {
                show(stock);
            }
        });
    }

    private void show(JSONObject s) {
        title.setText(Api.str(s, "name") + " (" + Api.str(s, "code") + ")");
        List<String> head = new ArrayList<String>();
        String place = join(", ", Api.str(s, "district"), Api.str(s, "city"));
        if (place.length() > 0) {
            head.add(place);
        }

        if (Api.str(s, "phone") != null) {
            head.add("Tel: " + Api.str(s, "phone"));
        }

        JSONObject t = s.optJSONObject("totals");
        if (t != null) {
            head.add("Müşteride " + t.optInt("count") + " tüp · dolu " + t.optInt("full") + " · boş " + t.optInt("empty")
                + (t.optInt("unknown") > 0 ? " · belirsiz " + t.optInt("unknown") : ""));
            if (t.optInt("customerOwned") > 0) {
                head.add("Müşteri malı: " + t.optInt("customerOwned"));
            }

            if (!t.isNull("maxDays") && t.has("maxDays")) {
                head.add("En uzun " + t.optInt("maxDays") + " gün · ortalama " + t.optInt("avgDays") + " gün");
            }

            if (t.optInt("long") > 0) {
                head.add("(!) " + t.optInt("long") + " tüp " + s.optInt("longDays") + " günden uzun süredir müşteride");
            }

            if (Api.str(t, "lastDelivery") != null) {
                head.add("Son teslim: " + Api.str(t, "lastDelivery").replace('T', ' '));
            }
        }

        summary.setText(android.text.TextUtils.join("\n", head));

        List<String> g = new ArrayList<String>();
        JSONArray groupList = s.optJSONArray("groups");
        for (int i = 0; groupList != null && i < groupList.length(); i++) {
            JSONObject x = groupList.optJSONObject(i);
            g.add("• " + Api.str(x, "gasType") + " " + Api.str(x, "capacity") + ": " + x.optInt("count") + " tüp (dolu " + x.optInt("full")
                + ", boş " + x.optInt("empty") + ")" + (x.optInt("long") > 0 ? " (!) " + x.optInt("long") + " uzun süredir" : ""));
        }

        groups.setText(g.isEmpty() ? "Bu müşteride tüp yok." : "Gruplar:\n" + android.text.TextUtils.join("\n", g));

        List<String> c = new ArrayList<String>();
        JSONArray list = s.optJSONArray("cylinders");
        for (int i = 0; list != null && i < list.length(); i++) {
            JSONObject x = list.optJSONObject(i);
            String fullness = Api.str(x, "fullness");
            c.add((x.optBoolean("needsReview") ? "(!) " : "") + Api.str(x, "serialNumber") + " · " + Api.str(x, "gasType") + " " + Api.str(x, "capacity")
                + ("-".equals(fullness) || fullness == null ? "" : " · " + fullness)
                + (x.isNull("days") || !x.has("days") ? "" : " · " + x.optInt("days") + " gün")
                + (x.optBoolean("testOverdue") ? " · TEST GEÇMİŞ" : "")
                + (x.optBoolean("customerOwned") ? " · müşteri malı" : ""));
        }

        cylinders.setText(c.isEmpty() ? "" : "Tüpler:\n" + android.text.TextUtils.join("\n", c));
    }

    static String join(String separator, String... parts) {
        List<String> kept = new ArrayList<String>();
        for (String part : parts) {
            if (part != null && part.trim().length() > 0) {
                kept.add(part.trim());
            }
        }

        return android.text.TextUtils.join(separator, kept);
    }
}
