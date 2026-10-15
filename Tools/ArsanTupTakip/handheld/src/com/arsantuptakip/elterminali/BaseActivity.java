package com.arsantuptakip.elterminali;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.DialogInterface;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.Typeface;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.text.Editable;
import android.text.InputType;
import android.text.TextWatcher;
import android.util.TypedValue;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.inputmethod.EditorInfo;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;
import java.util.List;

// Code-built screens (no layouts) with one helper for background calls; every call shows the busy bar and reports
// errors in a dialog, an ended session returns to the login screen.
abstract class BaseActivity extends Activity {
    static final int RED = Color.rgb(185, 28, 28);
    static final int MUTED = Color.rgb(71, 85, 105);
    static final int ACCENT = Color.rgb(15, 118, 110);
    static final int GOOD = Color.rgb(22, 163, 74);
    static final int WARN = Color.rgb(217, 119, 6);

    interface Job<T> {
        T run() throws Exception;
    }

    interface Done<T> {
        void ok(T value);
    }

    interface Text {
        void on(String value);
    }

    interface Pick {
        void on(int index);
    }

    private final Handler main = new Handler(Looper.getMainLooper());
    private ProgressBar busy;
    private int running;
    protected LinearLayout page;
    private EditText scanEdit;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        Api.init(this);
        ScrollView scroll = new ScrollView(this);
        scroll.setFillViewport(true);
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        busy = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        busy.setIndeterminate(true);
        busy.setVisibility(View.INVISIBLE);
        root.addView(busy, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(6)));
        page = new LinearLayout(this);
        page.setOrientation(LinearLayout.VERTICAL);
        page.setPadding(dp(12), dp(4), dp(12), dp(24));
        root.addView(page);
        scroll.addView(root);
        setContentView(scroll);
    }

    // Scanner text typed while no field has focus (e.g. after tapping a button) goes to the scan field.
    @Override
    public boolean dispatchKeyEvent(KeyEvent event) {
        if (scanEdit != null && event.getAction() == KeyEvent.ACTION_DOWN && event.getUnicodeChar() != 0
            && !(getCurrentFocus() instanceof EditText)) {
            scanEdit.requestFocus();
        }

        return super.dispatchKeyEvent(event);
    }

    // Emoji only from Unicode 6.0: Android 4.4 shows those in colour, newer ones as empty boxes.
    android.graphics.drawable.GradientDrawable rounded(int color, boolean outline) {
        android.graphics.drawable.GradientDrawable shape = new android.graphics.drawable.GradientDrawable();
        shape.setCornerRadius(dp(12));
        shape.setColor(Color.argb(32, Color.red(color), Color.green(color), Color.blue(color)));
        if (outline) {
            shape.setStroke(dp(2), color);
        }

        return shape;
    }

    // A big number with an icon and caption, like the phone app's tiles.
    LinearLayout tile(String icon, String value, String caption, int color) {
        LinearLayout box = new LinearLayout(this);
        box.setOrientation(LinearLayout.VERTICAL);
        box.setPadding(dp(10), dp(8), dp(10), dp(8));
        box.setBackgroundDrawable(rounded(color, false));
        TextView number = text(icon + " " + value, 24, true);
        number.setTextColor(color);
        box.addView(number);
        box.addView(text(caption, 14, true));
        return box;
    }

    LinearLayout tiles(View... views) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        for (int i = 0; i < views.length; i++) {
            LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
            params.setMargins(i == 0 ? 0 : dp(4), dp(4), i == views.length - 1 ? 0 : dp(4), dp(4));
            row.addView(views[i], params);
        }

        return row;
    }

    // Coloured result box: success, warning or failure is clear before reading the text.
    void banner(TextView view, String icon, String message, int color) {
        view.setText(icon + "  " + message);
        view.setTextColor(color);
        view.setTypeface(Typeface.DEFAULT_BOLD);
        view.setPadding(dp(10), dp(10), dp(10), dp(10));
        view.setBackgroundDrawable(rounded(color, true));
    }

    void buzz() {
        try {
            android.os.Vibrator vibrator = (android.os.Vibrator) getSystemService(VIBRATOR_SERVICE);
            if (vibrator != null) {
                vibrator.vibrate(120);
            }
        } catch (RuntimeException ignored) {
            // No vibrator or no permission.
        }
    }

    int dp(int value) {
        return (int) TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value, getResources().getDisplayMetrics());
    }

    TextView text(String value, int sp, boolean bold) {
        TextView view = new TextView(this);
        view.setText(value);
        view.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        view.setTextColor(Color.BLACK);
        if (bold) {
            view.setTypeface(Typeface.DEFAULT_BOLD);
        }

        view.setPadding(0, dp(4), 0, dp(4));
        return view;
    }

    TextView note(String value) {
        TextView view = text(value, 14, false);
        view.setTextColor(MUTED);
        return view;
    }

    TextView heading(String value) {
        TextView view = text(value, 18, true);
        view.setTextColor(ACCENT);
        view.setPadding(0, dp(14), 0, dp(4));
        return view;
    }

    Button button(String label, View.OnClickListener click) {
        Button button = new Button(this);
        button.setText(label);
        button.setTextSize(TypedValue.COMPLEX_UNIT_SP, 17);
        button.setOnClickListener(click);
        button.setMinHeight(dp(52));
        return button;
    }

    LinearLayout row(View... views) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        for (View view : views) {
            row.addView(view, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        }

        return row;
    }

    EditText edit(String hint) {
        EditText edit = new EditText(this);
        edit.setHint(hint);
        edit.setSingleLine(true);
        edit.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18);
        return edit;
    }

    // The EDA50 scanner (keyboard wedge) types the code and presses Enter; some profiles type a newline instead, others
    // send no suffix at all. Text arriving faster than a person types (or in one piece) is therefore submitted after a
    // short pause too. On the EDA50 the app needs its own Internal Scanner profile with Wedge and the label symbologies
    // (QR Code) enabled.
    EditText scanField(String hint, final Text onCode) {
        final EditText edit = edit(hint);
        scanEdit = edit;
        final Runnable auto = new Runnable() {
            @Override
            public void run() {
                if (edit.getText().toString().trim().length() >= 3) {
                    submit(edit, onCode);
                }
            }
        };
        edit.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        edit.setImeOptions(EditorInfo.IME_ACTION_DONE);
        edit.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override
            public boolean onEditorAction(TextView v, int actionId, KeyEvent event) {
                if (event != null && event.getAction() != KeyEvent.ACTION_DOWN) {
                    return true;
                }

                submit(edit, onCode);
                return true;
            }
        });
        edit.addTextChangedListener(new TextWatcher() {
            private long last;
            private int fast;

            @Override
            public void beforeTextChanged(CharSequence s, int start, int count, int after) {
            }

            @Override
            public void onTextChanged(CharSequence s, int start, int before, int count) {
                int added = count - before;
                if (added <= 0) {
                    return;
                }

                long now = SystemClock.uptimeMillis();
                fast = added > 1 ? added : now - last < 80 ? fast + 1 : 1;
                last = now;
                if (fast >= 4) {
                    main.removeCallbacks(auto);
                    main.postDelayed(auto, 300);
                }
            }

            @Override
            public void afterTextChanged(Editable s) {
                String value = s.toString();
                if (value.indexOf('\n') >= 0 || value.indexOf('\r') >= 0) {
                    edit.setText(value.replace("\r", "").replace("\n", ""));
                    submit(edit, onCode);
                }
            }
        });
        return edit;
    }

    // Search fields: Enter (keyboard or scanner) runs the search and keeps the text.
    EditText searchField(String hint, final Runnable search) {
        EditText edit = edit(hint);
        edit.setImeOptions(EditorInfo.IME_ACTION_SEARCH);
        edit.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            @Override
            public boolean onEditorAction(TextView v, int actionId, KeyEvent event) {
                if (event == null || event.getAction() == KeyEvent.ACTION_DOWN) {
                    search.run();
                }

                return true;
            }
        });
        return edit;
    }

    void submit(EditText edit, Text onCode) {
        String code = edit.getText().toString().trim();
        edit.setText("");
        edit.requestFocus();
        if (code.length() > 0) {
            onCode.on(code);
        }
    }

    <T> void call(final Job<T> job, final Done<T> done) {
        running++;
        busy.setVisibility(View.VISIBLE);
        new Thread(new Runnable() {
            @Override
            public void run() {
                T value = null;
                Exception error = null;
                try {
                    value = job.run();
                } catch (Exception e) {
                    error = e;
                }

                final T result = value;
                final Exception failure = error;
                main.post(new Runnable() {
                    @Override
                    public void run() {
                        if (--running <= 0) {
                            running = 0;
                            busy.setVisibility(View.INVISIBLE);
                        }

                        if (isFinishing()) {
                            return;
                        }

                        if (failure != null) {
                            fail(failure);
                        } else if (done != null) {
                            done.ok(result);
                        }
                    }
                });
            }
        }).start();
    }

    void fail(Exception error) {
        if (error instanceof Api.ApiException && ((Api.ApiException) error).sessionEnded) {
            Intent intent = new Intent(this, LoginActivity.class);
            intent.putExtra("message", error.getMessage());
            intent.addFlags(Intent.FLAG_ACTIVITY_CLEAR_TOP | Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
            startActivity(intent);
            finish();
            return;
        }

        if (error instanceof Api.ApiException && ((Api.ApiException) error).mustChangePassword) {
            alert("Şifre", "Devam etmeden önce şifrenizi değiştirmeniz gerekiyor. Ana ekrandaki \"Şifre değiştir\" düğmesini kullanın.");
            return;
        }

        String message = error.getMessage();
        alert("Hata", message == null || message.length() == 0 ? error.toString() : message);
    }

    void alert(String title, String message) {
        alert(title, message, null);
    }

    void alert(String title, String message, final Runnable then) {
        new AlertDialog.Builder(this).setTitle(title).setMessage(message)
            .setPositiveButton("Tamam", new DialogInterface.OnClickListener() {
                @Override
                public void onClick(DialogInterface dialog, int which) {
                    if (then != null) {
                        then.run();
                    }
                }
            })
            .setCancelable(then == null)
            .show();
    }

    void confirm(String title, String message, String yes, final Runnable then) {
        new AlertDialog.Builder(this).setTitle(title).setMessage(message)
            .setPositiveButton(yes, new DialogInterface.OnClickListener() {
                @Override
                public void onClick(DialogInterface dialog, int which) {
                    then.run();
                }
            })
            .setNegativeButton("Vazgeç", null)
            .show();
    }

    void choose(String title, List<String> items, String cancel, final Pick then) {
        AlertDialog.Builder builder = new AlertDialog.Builder(this).setTitle(title)
            .setItems(items.toArray(new String[items.size()]), new DialogInterface.OnClickListener() {
                @Override
                public void onClick(DialogInterface dialog, int which) {
                    then.on(which);
                }
            });
        builder.setNegativeButton(cancel, new DialogInterface.OnClickListener() {
            @Override
            public void onClick(DialogInterface dialog, int which) {
                then.on(-1);
            }
        });
        builder.setOnCancelListener(new DialogInterface.OnCancelListener() {
            @Override
            public void onCancel(DialogInterface dialog) {
                then.on(-1);
            }
        });
        builder.show();
    }

    interface Customer {
        void on(JSONObject customer);
    }

    // Searches customers (at least 2 letters) and lets the user pick one; then gets null when nothing was picked.
    void pickCustomer(final String query, final Customer then) {
        if (query == null || query.trim().length() < 2) {
            alert("Müşteri", "En az 2 harf yazın.");
            return;
        }

        call(new Job<JSONArray>() {
            @Override
            public JSONArray run() throws Exception {
                return Api.getArray("/api/musteriler?q=" + Api.q(query.trim()));
            }
        }, new Done<JSONArray>() {
            @Override
            public void ok(final JSONArray found) {
                List<String> items = new ArrayList<String>();
                for (int i = 0; i < found.length(); i++) {
                    JSONObject c = found.optJSONObject(i);
                    String city = Api.str(c, "city");
                    items.add(Api.str(c, "name") + " (" + Api.str(c, "code") + ")" + (city == null ? "" : " · " + city));
                }

                if (items.isEmpty()) {
                    alert("Müşteri", "\"" + query.trim() + "\" ile eşleşen müşteri bulunamadı.");
                    then.on(null);
                    return;
                }

                choose("Müşteri seçin", items, "Vazgeç", new Pick() {
                    @Override
                    public void on(int index) {
                        then.on(index < 0 ? null : found.optJSONObject(index));
                    }
                });
            }
        });
    }

    // then receives null when the user cancels.
    void prompt(String title, String message, String ok, String cancel, final Text then) {
        final EditText input = edit("");
        input.setSingleLine(false);
        new AlertDialog.Builder(this).setTitle(title).setMessage(message).setView(input)
            .setPositiveButton(ok, new DialogInterface.OnClickListener() {
                @Override
                public void onClick(DialogInterface dialog, int which) {
                    then.on(input.getText().toString().trim());
                }
            })
            .setNegativeButton(cancel, new DialogInterface.OnClickListener() {
                @Override
                public void onClick(DialogInterface dialog, int which) {
                    then.on(null);
                }
            })
            .setCancelable(false)
            .show();
    }
}
