package com.arsantuptakip.elterminali;

import android.content.Context;
import android.content.SharedPreferences;
import android.os.Build;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.UnsupportedEncodingException;
import java.net.HttpURLConnection;
import java.net.InetAddress;
import java.net.Socket;
import java.net.URL;
import java.net.URLEncoder;
import java.security.KeyStore;
import java.security.cert.CertificateException;
import java.security.cert.CertificateFactory;
import java.security.cert.X509Certificate;
import java.util.ArrayList;
import java.util.List;
import java.util.UUID;

import javax.net.ssl.HttpsURLConnection;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.SSLSocketFactory;
import javax.net.ssl.TrustManager;
import javax.net.ssl.TrustManagerFactory;
import javax.net.ssl.X509TrustManager;

// Same REST API and session rules as the MAUI app (24-hour bearer token, Idempotency-Key on every POST).
final class Api {
    static final String DEFAULT_SERVER = "https://arsantuptakip.com/tuptakip";

    private static final String PREFS = "tuptakip";
    private static SharedPreferences prefs;
    private static SSLSocketFactory ssl;
    private static String sslError;

    private Api() {
    }

    static final class ApiException extends Exception {
        final int status;
        final boolean sessionEnded;
        final boolean mustChangePassword;

        ApiException(String message, int status, boolean sessionEnded, boolean mustChangePassword) {
            super(message);
            this.status = status;
            this.sessionEnded = sessionEnded;
            this.mustChangePassword = mustChangePassword;
        }

        ApiException(String message) {
            this(message, 0, false, false);
        }
    }

    static synchronized void init(Context context) {
        if (prefs != null) {
            return;
        }

        prefs = context.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
        try {
            ssl = buildSsl(context);
        } catch (Exception e) {
            sslError = e.toString();
        }
    }

    static String server() {
        String value = prefs.getString("server", "");
        return value.length() == 0 ? DEFAULT_SERVER : value;
    }

    static void setServer(String value) {
        String url = value == null ? "" : value.trim();
        while (url.endsWith("/")) {
            url = url.substring(0, url.length() - 1);
        }

        if (url.length() > 0 && !url.toLowerCase().startsWith("http")) {
            url = "https://" + url;
        }

        prefs.edit().putString("server", url).apply();
    }

    static String token() {
        return prefs.getString("token", null);
    }

    static JSONObject account() {
        String json = prefs.getString("account", null);
        if (json == null) {
            return null;
        }

        try {
            return new JSONObject(json);
        } catch (JSONException e) {
            return null;
        }
    }

    static boolean hasSession() {
        return token() != null && account() != null;
    }

    static boolean can(String permission) {
        JSONObject account = account();
        JSONArray permissions = account == null ? null : account.optJSONArray("permissions");
        if (permissions == null) {
            return false;
        }

        for (int i = 0; i < permissions.length(); i++) {
            if (permission.equals(permissions.optString(i))) {
                return true;
            }
        }

        return false;
    }

    static String fixedPlate() {
        JSONObject account = account();
        String plate = account == null ? null : str(account, "fixedPlate");
        return plate == null || plate.trim().length() == 0 ? null : plate;
    }

    static void login(String user, String password) throws ApiException {
        JSONObject body = new JSONObject();
        try {
            body.put("kullanici", user.trim());
            body.put("sifre", password);
            body.put("cihazAdi", (Build.MANUFACTURER + " " + Build.MODEL + " (el terminali)").trim());
        } catch (JSONException e) {
            throw new ApiException(e.getMessage());
        }

        JSONObject response = (JSONObject) send("POST", "/api/giris", body, false);
        String token = str(response, "token");
        JSONObject account = response.optJSONObject("account");
        if (token == null || account == null) {
            throw new ApiException("Sunucu oturum bilgisini döndürmedi.");
        }

        prefs.edit().putString("token", token).putString("account", account.toString()).apply();
    }

    static JSONObject refreshAccount() throws ApiException {
        JSONObject account = (JSONObject) send("GET", "/api/ben", null, true);
        prefs.edit().putString("account", account.toString()).apply();
        return account;
    }

    static void logout() {
        try {
            if (token() != null) {
                send("POST", "/api/cikis", new JSONObject(), true);
            }
        } catch (ApiException ignored) {
        }

        clearSession();
    }

    static void clearSession() {
        prefs.edit().remove("token").remove("account").apply();
    }

    static JSONObject getObject(String path) throws ApiException {
        return (JSONObject) send("GET", path, null, true);
    }

    static JSONArray getArray(String path) throws ApiException {
        return (JSONArray) send("GET", path, null, true);
    }

    static JSONObject publicObject(String path) throws ApiException {
        return (JSONObject) send("GET", path, null, false);
    }

    static JSONObject post(String path, JSONObject body) throws ApiException {
        return (JSONObject) send("POST", path, body, true);
    }

    static String postMessage(String path, JSONObject body) throws ApiException {
        String message = str(post(path, body), "message");
        return message == null ? "Kaydedildi." : message;
    }

    static String q(String value) {
        try {
            return URLEncoder.encode(value, "UTF-8");
        } catch (UnsupportedEncodingException e) {
            return value;
        }
    }

    // String value or null (org.json's optString turns JSON null into "null").
    static String str(JSONObject object, String key) {
        return object == null || !object.has(key) || object.isNull(key) ? null : object.optString(key);
    }

    static HttpURLConnection open(String path) throws IOException {
        HttpURLConnection connection = (HttpURLConnection) new URL(server() + path).openConnection();
        if (connection instanceof HttpsURLConnection) {
            if (ssl == null) {
                throw new IOException("Güvenli bağlantı hazırlanamadı: " + sslError);
            }

            ((HttpsURLConnection) connection).setSSLSocketFactory(ssl);
        }

        connection.setConnectTimeout(20000);
        connection.setReadTimeout(120000);
        return connection;
    }

    private static Object send(String method, String path, JSONObject body, boolean authorize) throws ApiException {
        String token = authorize ? token() : null;
        if (authorize && token == null) {
            throw new ApiException("Oturum açık değil. Kullanıcı adı ve şifrenizle giriş yapın.", 401, true, false);
        }

        HttpURLConnection connection = null;
        try {
            connection = open(path);
            connection.setRequestMethod(method);
            connection.setRequestProperty("Accept", "application/json");
            if (token != null) {
                connection.setRequestProperty("Authorization", "Bearer " + token);
            }

            if (body != null) {
                if (authorize) {
                    connection.setRequestProperty("Idempotency-Key", UUID.randomUUID().toString().replace("-", ""));
                }

                byte[] bytes = body.toString().getBytes("UTF-8");
                connection.setDoOutput(true);
                connection.setRequestProperty("Content-Type", "application/json; charset=utf-8");
                connection.setFixedLengthStreamingMode(bytes.length);
                OutputStream out = connection.getOutputStream();
                out.write(bytes);
                out.close();
            }

            int status;
            try {
                status = connection.getResponseCode();
            } catch (IOException e) {
                // Android 4.x throws on a 401 without a WWW-Authenticate header; the second call returns the code.
                status = connection.getResponseCode();
            }

            InputStream stream = status >= 400 ? connection.getErrorStream() : connection.getInputStream();
            String text = stream == null ? "" : read(stream).trim();
            if (status >= 400) {
                String error = errorText(text);
                if (status == 401 && authorize) {
                    clearSession();
                    throw new ApiException(error != null ? error : "Oturumunuz sona erdi. Kullanıcı adı ve şifrenizle yeniden giriş yapın.", 401, true, false);
                }

                if (error == null && (status == 404 || status == 502 || status == 503 || status == 504 || status == 530)) {
                    error = "ArsanTüpTakip sunucusuna ulaşılamıyor (" + status + ").";
                }

                if (error == null && status == 401) {
                    error = "Kullanıcı adı veya şifre hatalı.";
                }

                throw new ApiException(error != null ? error : "Sunucu hatası (" + status + ").", status, false,
                    status == 403 && text.contains("\"mustChangePassword\":true"));
            }

            if (text.startsWith("[")) {
                return new JSONArray(text);
            }

            return new JSONObject(text.length() == 0 ? "{}" : text);
        } catch (IOException e) {
            throw new ApiException("Sunucuya ulaşılamadı. İnternet bağlantısını kontrol edin. (" + e.getMessage() + ")");
        } catch (JSONException e) {
            throw new ApiException("Sunucu yanıtı anlaşılamadı.");
        } finally {
            if (connection != null) {
                connection.disconnect();
            }
        }
    }

    private static String errorText(String content) {
        try {
            return content.startsWith("{") ? str(new JSONObject(content), "error") : null;
        } catch (JSONException e) {
            return null;
        }
    }

    private static String read(InputStream stream) throws IOException {
        ByteArrayOutputStream buffer = new ByteArrayOutputStream();
        byte[] chunk = new byte[8192];
        int count;
        while ((count = stream.read(chunk)) > 0) {
            buffer.write(chunk, 0, count);
        }

        stream.close();
        return new String(buffer.toByteArray(), "UTF-8");
    }

    // Android 4.4 trusts neither ISRG nor GTS roots and leaves TLS 1.2 off: the roots (Let's Encrypt and Google
    // Trust Services, the CAs Cloudflare uses) ship in assets/trusted-roots.txt.
    private static SSLSocketFactory buildSsl(Context context) throws Exception {
        KeyStore store = KeyStore.getInstance(KeyStore.getDefaultType());
        store.load(null, null);
        CertificateFactory factory = CertificateFactory.getInstance("X.509");
        String pem = read(context.getAssets().open("trusted-roots.txt"));
        String begin = "-----BEGIN CERTIFICATE-----";
        String end = "-----END CERTIFICATE-----";
        int index = 0;
        int from;
        while ((from = pem.indexOf(begin)) >= 0) {
            int to = pem.indexOf(end, from);
            byte[] der = Base64.decode(pem.substring(from + begin.length(), to), Base64.DEFAULT);
            store.setCertificateEntry("root" + index++, factory.generateCertificate(new ByteArrayInputStream(der)));
            pem = pem.substring(to + end.length());
        }

        TrustManagerFactory own = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());
        own.init(store);
        TrustManagerFactory system = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm());
        system.init((KeyStore) null);
        SSLContext context2 = SSLContext.getInstance("TLS");
        context2.init(null, new TrustManager[] { new EitherTrust(first(own), first(system)) }, null);
        return new ModernTls(context2.getSocketFactory());
    }

    private static X509TrustManager first(TrustManagerFactory factory) {
        for (TrustManager manager : factory.getTrustManagers()) {
            if (manager instanceof X509TrustManager) {
                return (X509TrustManager) manager;
            }
        }

        return null;
    }

    private static final class EitherTrust implements X509TrustManager {
        private final X509TrustManager own;
        private final X509TrustManager system;

        EitherTrust(X509TrustManager own, X509TrustManager system) {
            this.own = own;
            this.system = system;
        }

        @Override
        public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            throw new CertificateException("İstemci sertifikası kullanılmıyor.");
        }

        @Override
        public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
            try {
                own.checkServerTrusted(chain, authType);
            } catch (CertificateException e) {
                if (system == null) {
                    throw e;
                }

                system.checkServerTrusted(chain, authType);
            }
        }

        @Override
        public X509Certificate[] getAcceptedIssuers() {
            return own.getAcceptedIssuers();
        }
    }

    private static final class ModernTls extends SSLSocketFactory {
        private final SSLSocketFactory inner;

        ModernTls(SSLSocketFactory inner) {
            this.inner = inner;
        }

        private Socket enable(Socket socket) {
            if (socket instanceof SSLSocket) {
                SSLSocket ssl = (SSLSocket) socket;
                List<String> wanted = new ArrayList<String>();
                for (String protocol : ssl.getSupportedProtocols()) {
                    if (protocol.equals("TLSv1.2") || protocol.equals("TLSv1.1") || protocol.equals("TLSv1")) {
                        wanted.add(protocol);
                    }
                }

                ssl.setEnabledProtocols(wanted.toArray(new String[wanted.size()]));
            }

            return socket;
        }

        @Override
        public String[] getDefaultCipherSuites() {
            return inner.getDefaultCipherSuites();
        }

        @Override
        public String[] getSupportedCipherSuites() {
            return inner.getSupportedCipherSuites();
        }

        @Override
        public Socket createSocket(Socket s, String host, int port, boolean autoClose) throws IOException {
            return enable(inner.createSocket(s, host, port, autoClose));
        }

        @Override
        public Socket createSocket(String host, int port) throws IOException {
            return enable(inner.createSocket(host, port));
        }

        @Override
        public Socket createSocket(String host, int port, InetAddress localHost, int localPort) throws IOException {
            return enable(inner.createSocket(host, port, localHost, localPort));
        }

        @Override
        public Socket createSocket(InetAddress host, int port) throws IOException {
            return enable(inner.createSocket(host, port));
        }

        @Override
        public Socket createSocket(InetAddress address, int port, InetAddress localAddress, int localPort) throws IOException {
            return enable(inner.createSocket(address, port, localAddress, localPort));
        }
    }
}
