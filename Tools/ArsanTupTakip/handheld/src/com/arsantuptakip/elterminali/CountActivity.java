package com.arsantuptakip.elterminali;

import android.os.Bundle;
import android.view.View;
import android.widget.Button;
import android.widget.EditText;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

// Depot count for Operatör/Yönetici: full and empty counts are separate (same API as the phone).
public final class CountActivity extends BaseActivity {
    static final String LOC_FULL = "Ana depo · Dolu";
    static final String LOC_EMPTY = "Ana depo · Boş";

    private TextView openLine;
    private TextView status;
    private TextView totals;
    private TextView recent;
    private EditText noteEdit;
    private EditText code;
    private Button fullButton;
    private Button emptyButton;
    private Button undoButton;
    private Button closeButton;
    private View scanBlock;
    private JSONObject count;
    private String lastCode;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle("Depo sayımı");
        if (!Api.can("depo-sayim")) {
            page.addView(note("Bu kullanıcıda depo sayımı yetkisi yok (yalnızca Operatör ve Yönetici)."));
            return;
        }

        page.addView(note("Dolu ve boş tüpler ayrı sayılır. Barkodu okutun; bitince \"Sayımı bitir\"e basın."));
        noteEdit = edit("Not (isteğe bağlı)");
        page.addView(noteEdit);
        openLine = text("", 15, false);
        page.addView(openLine);
        fullButton = button("Dolu sayımı başlat", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                start(LOC_FULL);
            }
        });
        emptyButton = button("Boş sayımı başlat", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                start(LOC_EMPTY);
            }
        });
        page.addView(fullButton);
        page.addView(emptyButton);

        scanBlock = new android.widget.LinearLayout(this);
        ((android.widget.LinearLayout) scanBlock).setOrientation(android.widget.LinearLayout.VERTICAL);
        status = text("", 16, true);
        totals = text("", 15, false);
        recent = text("", 14, false);
        code = scanField("Barkod / seri no", new Text() {
            @Override
            public void on(String value) {
                scan(value);
            }
        });
        ((android.widget.LinearLayout) scanBlock).addView(status);
        ((android.widget.LinearLayout) scanBlock).addView(code);
        ((android.widget.LinearLayout) scanBlock).addView(button("Okut", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                submit(code, new Text() {
                    @Override
                    public void on(String value) {
                        scan(value);
                    }
                });
            }
        }));
        undoButton = button("Son okutmayı geri al", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                undo();
            }
        });
        closeButton = button("Sayımı bitir", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                close();
            }
        });
        ((android.widget.LinearLayout) scanBlock).addView(undoButton);
        ((android.widget.LinearLayout) scanBlock).addView(closeButton);
        ((android.widget.LinearLayout) scanBlock).addView(button("Diğer sayıma geç", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                show(null);
                load();
            }
        }));
        ((android.widget.LinearLayout) scanBlock).addView(totals);
        ((android.widget.LinearLayout) scanBlock).addView(recent);
        page.addView(scanBlock);
        show(null);
        load();
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (Api.can("depo-sayim") && count == null) {
            load();
        }
    }

    private void load() {
        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.getObject("/api/depo-sayim/aktif");
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject active) {
                JSONArray counts = active.optJSONArray("counts");
                JSONObject full = find(counts, LOC_FULL);
                JSONObject empty = find(counts, LOC_EMPTY);
                fullButton.setText(full == null ? "Dolu sayımı başlat" : "Dolu sayımına devam (#" + full.optInt("id") + ")");
                emptyButton.setText(empty == null ? "Boş sayımı başlat" : "Boş sayımına devam (#" + empty.optInt("id") + ")");
                StringBuilder open = new StringBuilder();
                if (full != null) {
                    open.append("Açık dolu #").append(full.optInt("id")).append(": ")
                        .append(full.optInt("counted")).append(" okutulan / kayıtta ").append(full.optInt("expected"));
                }
                if (empty != null) {
                    if (open.length() > 0) {
                        open.append('\n');
                    }
                    open.append("Açık boş #").append(empty.optInt("id")).append(": ")
                        .append(empty.optInt("counted")).append(" okutulan / kayıtta ").append(empty.optInt("expected"));
                }
                openLine.setText(open.length() == 0 ? "Açık sayım yok." : open.toString());
                if (count != null) {
                    JSONObject keep = findById(counts, count.optInt("id"));
                    if (keep != null) {
                        show(keep);
                    } else {
                        show(null);
                    }
                }
            }
        });
    }

    private void start(final String location) {
        final JSONObject body = new JSONObject();
        try {
            body.put("note", noteEdit.getText().toString());
            body.put("location", location);
        } catch (Exception ignored) {
        }

        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.post("/api/depo-sayim/baslat", body);
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject response) {
                noteEdit.setText("");
                show(response.optJSONObject("count"));
                status.setTextColor(GOOD);
                status.setText(Api.str(response, "message"));
                load();
                code.requestFocus();
            }
        });
    }

    private void scan(final String value) {
        if (count == null || value == null || value.trim().length() == 0) {
            return;
        }

        final int id = count.optInt("id");
        final JSONObject body = new JSONObject();
        try {
            body.put("code", value.trim());
        } catch (Exception ignored) {
        }

        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.post("/api/depo-sayim/" + id + "/okut", body);
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject response) {
                String outcome = Api.str(response, "outcome");
                if (!"duplicate".equals(outcome)) {
                    lastCode = value.trim();
                }

                show(response.optJSONObject("count"));
                String message = Api.str(response, "message");
                if ("matched".equals(outcome)) {
                    status.setTextColor(GOOD);
                    buzz();
                } else if ("duplicate".equals(outcome)) {
                    status.setTextColor(MUTED);
                } else {
                    status.setTextColor(RED);
                }

                status.setText(message == null ? "" : message);
                code.setText("");
                code.requestFocus();
            }
        });
    }

    private void undo() {
        if (count == null || lastCode == null) {
            alert("Geri al", "Geri alınacak son okutma yok.");
            return;
        }

        final int id = count.optInt("id");
        final String codeValue = lastCode;
        final JSONObject body = new JSONObject();
        try {
            body.put("code", codeValue);
        } catch (Exception ignored) {
        }

        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.post("/api/depo-sayim/" + id + "/geri-al", body);
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject response) {
                lastCode = null;
                show(response.optJSONObject("count"));
                status.setTextColor(WARN);
                status.setText(Api.str(response, "message"));
            }
        });
    }

    private void close() {
        if (count == null) {
            return;
        }

        final int id = count.optInt("id");
        confirm("Sayımı bitir", "Sayım #" + id + " kapatılsın mı? Rapor panelde görünür.", "Bitir", new Runnable() {
            @Override
            public void run() {
                call(new Job<JSONObject>() {
                    @Override
                    public JSONObject run() throws Exception {
                        return Api.post("/api/depo-sayim/" + id + "/bitir", new JSONObject());
                    }
                }, new Done<JSONObject>() {
                    @Override
                    public void ok(JSONObject response) {
                        lastCode = null;
                        show(null);
                        status.setTextColor(GOOD);
                        status.setText(Api.str(response, "message"));
                        load();
                    }
                });
            }
        });
    }

    private void show(JSONObject value) {
        count = value;
        boolean open = value != null;
        scanBlock.setVisibility(open ? View.VISIBLE : View.GONE);
        if (!open) {
            status.setText("");
            totals.setText("");
            recent.setText("");
            return;
        }

        status.setText("#" + value.optInt("id") + " · " + Api.str(value, "location")
            + " · " + value.optInt("counted") + " okutulan / kayıtta " + value.optInt("expected"));
        totals.setText("Eşleşen " + value.optInt("matched")
            + " · Eksik " + value.optInt("missing")
            + " · Fazla " + value.optInt("extra")
            + " · Tanınmayan " + value.optInt("unknown"));
        JSONArray recentArr = value.optJSONArray("recent");
        if (recentArr == null || recentArr.length() == 0) {
            recent.setText("Henüz okutma yok.");
            return;
        }

        StringBuilder lines = new StringBuilder("Son okutulanlar:\n");
        for (int i = 0; i < recentArr.length() && i < 8; i++) {
            JSONObject row = recentArr.optJSONObject(i);
            if (row == null) {
                continue;
            }

            lines.append("• ").append(Api.str(row, "serialNumber"));
            String group = Api.str(row, "group");
            if (group != null) {
                lines.append(" · ").append(group);
            }

            lines.append('\n');
        }

        recent.setText(lines.toString().trim());
    }

    private static JSONObject find(JSONArray counts, String location) {
        if (counts == null) {
            return null;
        }

        for (int i = 0; i < counts.length(); i++) {
            JSONObject row = counts.optJSONObject(i);
            if (row != null && location.equals(Api.str(row, "location"))) {
                return row;
            }
        }

        return null;
    }

    private static JSONObject findById(JSONArray counts, int id) {
        if (counts == null) {
            return null;
        }

        for (int i = 0; i < counts.length(); i++) {
            JSONObject row = counts.optJSONObject(i);
            if (row != null && row.optInt("id") == id) {
                return row;
            }
        }

        return null;
    }
}
