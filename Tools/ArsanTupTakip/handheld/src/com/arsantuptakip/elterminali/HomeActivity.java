package com.arsantuptakip.elterminali;

import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.Intent;
import android.os.Bundle;
import android.text.InputType;
import android.view.View;
import android.widget.EditText;
import android.widget.LinearLayout;

import org.json.JSONObject;

// Driver menu; buttons follow the permissions set in the panel, like the driver tiles of the phone app.
public final class HomeActivity extends BaseActivity {
    private static boolean updateChecked;
    private String shownAccess;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle("ArsanTüpTakip");
        build();
    }

    @Override
    protected void onResume() {
        super.onResume();
        call(new Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                return Api.refreshAccount();
            }
        }, new Done<JSONObject>() {
            @Override
            public void ok(JSONObject account) {
                if (!account.toString().equals(shownAccess)) {
                    build();
                }

                if (account.optBoolean("mustChangePassword")) {
                    changePassword();
                } else if (!updateChecked) {
                    updateChecked = true;
                    Updater.check(HomeActivity.this, false);
                }
            }
        });
    }

    private void build() {
        page.removeAllViews();
        JSONObject account = Api.account();
        shownAccess = account == null ? null : account.toString();
        String name = account == null ? "-" : Api.str(account, "displayName");
        String role = account == null ? "" : Api.str(account, "role");
        boolean driver = "Şoför".equals(role);
        page.addView(heading(name == null ? "-" : name));
        String plate = Api.fixedPlate();
        if (plate != null) {
            page.addView(text("🚚 Araç: " + plate, 17, true));
        }

        if (Api.can("sefer-goruntule")) {
            add(plate == null ? "🚚 Sefer" : "🚚 Sevkiyat / sefer", TripActivity.class);
            add("✅ Tamamlanmış seferler", HistoryActivity.class);
            if (Api.can("sefer-rota") && Api.can("musteri-konum-kaydet")) {
                add("📋 Bugünkü sefer planım", PlanActivity.class);
            }
        }

        if (Api.can("tup-goruntule")) {
            add("🔍 Tüp bilgisi", InfoActivity.class);
            if (!driver) {
                add("👥 Müşteri stok bilgisi", StockActivity.class);
            }
        }

        if (Api.can("depo-sayim")) {
            add("📦 Depo sayımı", CountActivity.class);
        }

        if (!Api.can("sefer-goruntule") && !Api.can("tup-goruntule") && !Api.can("depo-sayim")) {
            page.addView(note("Bu kullanıcıya el terminalindeki ekranlar için yetki verilmemiş. Yöneticiden sefer, tüp veya depo sayımı yetkisi isteyin."));
        }

        page.addView(heading("Hesap"));
        page.addView(row(
            button("🔄 Güncelle", new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    Updater.check(HomeActivity.this, true);
                }
            }),
            button("🔑 Şifre", new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    changePassword();
                }
            }),
            button("🚪 Çıkış", new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    logout();
                }
            })));
        page.addView(note("Sürüm " + Updater.ownVersion(this) + " · " + Api.server()));
    }

    private void add(String label, final Class<?> screen) {
        android.widget.Button button = button(label, new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                startActivity(new Intent(HomeActivity.this, screen));
            }
        });
        button.setTextSize(android.util.TypedValue.COMPLEX_UNIT_SP, 20);
        button.setMinHeight(dp(64));
        page.addView(button);
    }

    private void logout() {
        confirm("Çıkış", "Bu cihazdaki oturum kapatılsın mı?", "Çıkış yap", new Runnable() {
            @Override
            public void run() {
                call(new Job<Object>() {
                    @Override
                    public Object run() {
                        Api.logout();
                        return null;
                    }
                }, new Done<Object>() {
                    @Override
                    public void ok(Object value) {
                        startActivity(new Intent(HomeActivity.this, LoginActivity.class).putExtra("message", "Çıkış yapıldı."));
                        finish();
                    }
                });
            }
        });
    }

    private void changePassword() {
        final EditText current = passwordField("Mevcut şifre");
        final EditText fresh = passwordField("Yeni şifre");
        final EditText repeat = passwordField("Yeni şifre (tekrar)");
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(12), 0, dp(12), 0);
        box.addView(current);
        box.addView(fresh);
        box.addView(repeat);
        new AlertDialog.Builder(this).setTitle("Şifre değiştir").setView(box)
            .setPositiveButton("Kaydet", new DialogInterface.OnClickListener() {
                @Override
                public void onClick(DialogInterface dialog, int which) {
                    final JSONObject body = new JSONObject();
                    try {
                        body.put("mevcut", current.getText().toString());
                        body.put("yeni", fresh.getText().toString());
                        body.put("tekrar", repeat.getText().toString());
                    } catch (Exception ignored) {
                    }

                    call(new Job<String>() {
                        @Override
                        public String run() throws Exception {
                            String message = Api.postMessage("/api/sifre", body);
                            Api.refreshAccount();
                            return message;
                        }
                    }, new Done<String>() {
                        @Override
                        public void ok(String message) {
                            alert("Şifre", message);
                            build();
                        }
                    });
                }
            })
            .setNegativeButton("Vazgeç", null)
            .show();
    }

    private EditText passwordField(String hint) {
        EditText edit = edit(hint);
        edit.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        return edit;
    }
}
