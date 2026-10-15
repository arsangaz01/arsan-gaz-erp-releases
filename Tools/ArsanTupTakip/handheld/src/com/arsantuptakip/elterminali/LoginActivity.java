package com.arsantuptakip.elterminali;

import android.content.Intent;
import android.os.Bundle;
import android.text.InputType;
import android.view.View;
import android.widget.EditText;

public final class LoginActivity extends BaseActivity {
    private EditText user;
    private EditText password;
    private EditText server;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        setTitle("ArsanTüpTakip · Giriş");
        if (Api.hasSession() && getIntent().getStringExtra("message") == null) {
            openHome();
            return;
        }

        user = edit("Kullanıcı adı");
        password = edit("Şifre");
        password.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_PASSWORD);
        server = edit("Sunucu adresi");
        server.setText(Api.server());
        server.setTextSize(14);

        page.addView(heading("ArsanTüpTakip El Terminali"));
        page.addView(note("Android 4.4 el terminalleri için şoför ekranları: sefer, tüp bilgisi, müşteri stoku."));
        String message = getIntent().getStringExtra("message");
        if (message != null) {
            android.widget.TextView warning = text(message, 15, true);
            warning.setTextColor(RED);
            page.addView(warning);
        }

        page.addView(user);
        page.addView(password);
        page.addView(button("Giriş yap", new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                login();
            }
        }));
        page.addView(note("Sunucu adresi (değiştirmeyin):"));
        page.addView(server);
        page.addView(note("Sürüm " + Updater.ownVersion(this)));
    }

    private void login() {
        final String name = user.getText().toString().trim();
        final String secret = password.getText().toString();
        if (name.length() == 0 || secret.length() == 0) {
            alert("Giriş", "Kullanıcı adı ve şifreyi yazın.");
            return;
        }

        Api.setServer(server.getText().toString());
        call(new Job<Object>() {
            @Override
            public Object run() throws Exception {
                Api.login(name, secret);
                return null;
            }
        }, new Done<Object>() {
            @Override
            public void ok(Object value) {
                openHome();
            }
        });
    }

    private void openHome() {
        startActivity(new Intent(this, HomeActivity.class));
        finish();
    }
}
