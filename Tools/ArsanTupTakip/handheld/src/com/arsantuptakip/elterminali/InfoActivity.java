package com.arsantuptakip.elterminali;

import android.os.Bundle;
import android.view.View;
import android.widget.EditText;
import android.widget.TextView;

import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

// Read-only: scan a cylinder and see where it is, full or empty, and its test date.
public final class InfoActivity extends BaseActivity {
    private EditText code;
    private TextView title;
    private TextView result;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle("Tüp bilgisi");
        page.addView(note("Barkodu okutun (okuyucu Enter gönderir) veya seri numarasını yazıp \"Sorgula\"ya basın."));
        code = scanField("Barkod veya seri no", new Text() {
            @Override
            public void on(String value) {
                lookup(value);
            }
        });
        page.addView(code);
        page.addView(button("Sorgula", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                submit(code, new Text() {
                    @Override
                    public void on(String value) {
                        lookup(value);
                    }
                });
            }
        }));
        title = text("", 20, true);
        result = text("", 17, false);
        page.addView(title);
        page.addView(result);
        code.requestFocus();
    }

    private void lookup(final String value) {
        title.setText(value + " sorgulanıyor…");
        title.setTextColor(MUTED);
        result.setText("");
        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.getObject("/api/tup/bilgi?kod=" + Api.q(value));
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject c) {
                show(c);
            }
        });
    }

    @Override
    void fail(Exception error) {
        if (error instanceof Api.ApiException && ((Api.ApiException) error).status == 404) {
            title.setText(error.getMessage());
            title.setTextColor(RED);
            return;
        }

        title.setText("");
        super.fail(error);
    }

    private void show(JSONObject c) {
        boolean warn = c.optBoolean("testOverdue") || c.optBoolean("needsReview") || !c.optBoolean("isActive", true);
        title.setText(Api.str(c, "serialNumber") + " · " + Api.str(c, "gasType") + " " + Api.str(c, "capacity"));
        title.setTextColor(warn ? RED : ACCENT);
        List<String> lines = new ArrayList<String>();
        add(lines, "Durum", Api.str(c, "statusName"));
        String fullness = Api.str(c, "fullness");
        add(lines, "Dolu/boş", "-".equals(fullness) ? null : fullness);
        String location = Api.str(c, "location");
        String customer = Api.str(c, "customer");
        add(lines, "Yer", location);
        if (customer != null && (location == null || !location.contains(customer))) {
            add(lines, "Müşteri", customer);
        }

        add(lines, "Araç", Api.str(c, "plate"));
        add(lines, "Sefer", Api.str(c, "trip"));
        if (!c.isNull("daysThere") && c.has("daysThere")) {
            add(lines, "Orada", c.optInt("daysThere") + " gündür");
        }

        String last = Api.str(c, "lastMovementDate");
        if (last != null) {
            String type = Api.str(c, "lastMovementType");
            add(lines, "Son hareket", last + (type == null ? "" : " · " + type));
        }

        String test = Api.str(c, "testDue");
        if (test != null) {
            add(lines, "Test tarihi", test + (c.optBoolean("testOverdue") ? " (GEÇMİŞ!)" : ""));
        }

        add(lines, "Barkod", Api.str(c, "barcode"));
        add(lines, "Üretici seri", Api.str(c, "manufacturerSerial"));
        add(lines, "Ürün", Api.str(c, "productName"));
        if (!c.optBoolean("isActive", true)) {
            lines.add("(!) Tüp pasif (kullanımdan kaldırılmış).");
        }

        if (c.optBoolean("needsReview")) {
            lines.add("(!) Kayıt kontrol bekliyor.");
        }

        result.setText(android.text.TextUtils.join("\n", lines));
    }

    private static void add(List<String> lines, String label, String value) {
        if (value != null && value.trim().length() > 0) {
            lines.add(label + ": " + value);
        }
    }
}
