package com.tuzhatti.game;

import android.app.Activity;
import android.app.ActivityManager;
import android.content.Context;
import android.util.DisplayMetrics;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.net.Uri;
import android.os.Build;
import android.provider.Settings;
import android.webkit.JavascriptInterface;
import android.webkit.WebView;

import androidx.core.content.FileProvider;

import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;
import java.net.URL;
import java.nio.charset.StandardCharsets;

public class UpdateBridge {
    private static final String VERSION_URL =
            "https://raw.githubusercontent.com/arsangaz01/arsan-gaz-erp-releases/cursor/tuz-hatti-rts-f116/tuz-hatti/version.json";

    private final Activity activity;
    private final WebView webView;

    public UpdateBridge(Activity activity, WebView webView) {
        this.activity = activity;
        this.webView = webView;
    }

    @JavascriptInterface
    public String deviceInfo() {
        try {
            ActivityManager manager = (ActivityManager) activity.getSystemService(Context.ACTIVITY_SERVICE);
            ActivityManager.MemoryInfo mem = new ActivityManager.MemoryInfo();
            manager.getMemoryInfo(mem);
            DisplayMetrics metrics = activity.getResources().getDisplayMetrics();
            int wide = Math.max(metrics.widthPixels, metrics.heightPixels);
            int tall = Math.min(metrics.widthPixels, metrics.heightPixels);
            JSONObject info = new JSONObject();
            info.put("ramMb", mem.totalMem / (1024L * 1024L));
            info.put("cores", Runtime.getRuntime().availableProcessors());
            info.put("width", wide);
            info.put("height", tall);
            info.put("density", (double) metrics.density);
            return info.toString();
        } catch (Exception ex) {
            return "{}";
        }
    }

    @JavascriptInterface
    public void checkAndUpdate() {
        new Thread(() -> {
            try {
                String body = httpGet(VERSION_URL + "?t=" + System.currentTimeMillis());
                JSONObject json = new JSONObject(body);
                long remote = json.optLong("versionCode", 0);
                String name = json.optString("versionName", "");
                String apk = json.optString("apk", "");
                long local = localVersion();
                if (remote <= local) {
                    post("güncel");
                    return;
                }
                if (!apk.startsWith("https://")) {
                    post("Yeni sürüm " + name + " bulundu ama kurulum adresi yok.");
                    return;
                }
                if (Build.VERSION.SDK_INT >= 26 && !activity.getPackageManager().canRequestPackageInstalls()) {
                    post("Kurulum izni gerekli. İzin verip Güncelle'ye tekrar bas.");
                    activity.runOnUiThread(() -> {
                        Intent intent = new Intent(
                                Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                                Uri.parse("package:" + activity.getPackageName()));
                        activity.startActivity(intent);
                    });
                    return;
                }
                post("Sürüm " + name + " indiriliyor");
                File file = download(apk);
                if (file.length() < 200000) {
                    post("İndirilen dosya kurulum paketi değil.");
                    return;
                }
                post("Kurulum ekranı açıldı");
                activity.runOnUiThread(() -> install(file));
            } catch (Exception ex) {
                String detail = ex.getMessage() == null ? "bağlantı" : ex.getMessage();
                post("Güncelleme olmadı: " + detail);
            }
        }).start();
    }

    private long localVersion() throws Exception {
        if (Build.VERSION.SDK_INT >= 28) {
            return activity.getPackageManager().getPackageInfo(activity.getPackageName(), 0).getLongVersionCode();
        }
        return legacyVersionCode();
    }

    @SuppressWarnings("deprecation")
    private long legacyVersionCode() throws Exception {
        return activity.getPackageManager().getPackageInfo(activity.getPackageName(), 0).versionCode;
    }

    private String localName() {
        try {
            return activity.getPackageManager().getPackageInfo(activity.getPackageName(), 0).versionName;
        } catch (Exception ex) {
            return "2.0";
        }
    }

    private void post(String message) {
        String quoted = JSONObject.quote(message);
        activity.runOnUiThread(() ->
                webView.evaluateJavascript("window.__cepheUpdate(" + quoted + ")", null));
    }

    private String httpGet(String address) throws Exception {
        HttpURLConnection conn = open(address);
        try (InputStream in = conn.getInputStream()) {
            return readText(in);
        } finally {
            conn.disconnect();
        }
    }

    private File download(String address) throws Exception {
        File dir = new File(activity.getCacheDir(), "updates");
        if (!dir.exists() && !dir.mkdirs()) throw new IllegalStateException("önbellek");
        File out = new File(dir, "CepheHatti.apk");
        HttpURLConnection conn = open(address);
        try (InputStream in = conn.getInputStream(); FileOutputStream fos = new FileOutputStream(out)) {
            byte[] buf = new byte[8192];
            int n;
            while ((n = in.read(buf)) >= 0) fos.write(buf, 0, n);
        } finally {
            conn.disconnect();
        }
        return out;
    }

    private HttpURLConnection open(String address) throws Exception {
        String current = address;
        for (int hop = 0; hop < 5; hop++) {
            HttpURLConnection conn = (HttpURLConnection) new URL(current).openConnection();
            conn.setInstanceFollowRedirects(false);
            conn.setUseCaches(false);
            conn.setConnectTimeout(20000);
            conn.setReadTimeout(120000);
            conn.setRequestProperty("User-Agent", "Mozilla/5.0");
            conn.setRequestProperty("Cache-Control", "no-cache");
            int code = conn.getResponseCode();
            if (code >= 300 && code < 400) {
                String next = conn.getHeaderField("Location");
                conn.disconnect();
                if (next == null || next.isEmpty()) throw new IllegalStateException("yönlendirme yok");
                current = next.startsWith("http") ? next : new URL(new URL(current), next).toString();
                continue;
            }
            if (code != 200) {
                conn.disconnect();
                throw new IllegalStateException("sunucu " + code);
            }
            return conn;
        }
        throw new IllegalStateException("adres yönlendirmesi");
    }

    private static String readText(InputStream in) throws Exception {
        ByteArrayOutputStream bos = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = in.read(buf)) >= 0) bos.write(buf, 0, n);
        return bos.toString(StandardCharsets.UTF_8.name());
    }

    private void install(File file) {
        Uri uri = FileProvider.getUriForFile(activity, activity.getPackageName() + ".fileprovider", file);
        Intent intent = new Intent(Intent.ACTION_VIEW);
        intent.setDataAndType(uri, "application/vnd.android.package-archive");
        intent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_ACTIVITY_NEW_TASK);
        for (ResolveInfo info : activity.getPackageManager().queryIntentActivities(intent, PackageManager.MATCH_DEFAULT_ONLY)) {
            activity.grantUriPermission(info.activityInfo.packageName, uri, Intent.FLAG_GRANT_READ_URI_PERMISSION);
        }
        activity.startActivity(intent);
    }
}
