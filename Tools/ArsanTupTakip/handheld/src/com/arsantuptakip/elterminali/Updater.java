package com.arsantuptakip.elterminali;

import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInfo;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Environment;

import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.net.HttpURLConnection;

// The APK is downloaded by the app itself: the device browser cannot open the HTTPS site on Android 4.4.
final class Updater {
    private static final String INFO = "/api/guncelleme/el-terminali";

    private Updater() {
    }

    static int ownBuild(Context context) {
        PackageInfo info = info(context);
        return info == null ? 0 : info.versionCode;
    }

    static String ownVersion(Context context) {
        PackageInfo info = info(context);
        return info == null ? "?" : info.versionName + " (build " + info.versionCode + ")";
    }

    private static PackageInfo info(Context context) {
        try {
            return context.getPackageManager().getPackageInfo(context.getPackageName(), 0);
        } catch (PackageManager.NameNotFoundException e) {
            return null;
        }
    }

    static void check(final BaseActivity activity, final boolean manual) {
        activity.call(new BaseActivity.Job<JSONObject>() {
            @Override
            public JSONObject run() throws Exception {
                try {
                    return Api.publicObject(INFO);
                } catch (Api.ApiException e) {
                    // The automatic check at start stays silent; the "Güncelleme" button reports the problem.
                    if (manual) {
                        throw e;
                    }

                    return null;
                }
            }
        }, new BaseActivity.Done<JSONObject>() {
            @Override
            public void ok(JSONObject info) {
                if (info == null) {
                    return;
                }

                int build = info.optInt("publishedMobileBuild");
                final String path = Api.str(info, "downloadUrl");
                if (!info.optBoolean("apkReady") || build <= ownBuild(activity) || path == null) {
                    if (manual) {
                        activity.alert("Güncelleme", "Uygulama güncel: " + ownVersion(activity));
                    }

                    return;
                }

                activity.confirm("Güncelleme",
                    "Yeni sürüm " + Api.str(info, "publishedMobileVersion") + " (build " + build + ") hazır. İndirilip kurulsun mu?",
                    "Güncelle", new Runnable() {
                        @Override
                        public void run() {
                            download(activity, path);
                        }
                    });
            }
        });
    }

    private static void download(final BaseActivity activity, final String path) {
        activity.call(new BaseActivity.Job<File>() {
            @Override
            public File run() throws Exception {
                File folder = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS);
                if (!folder.exists() && !folder.mkdirs()) {
                    throw new Exception("İndirme klasörü açılamadı: " + folder);
                }

                File file = new File(folder, "ArsanTupTakip-ElTerminali.apk");
                HttpURLConnection connection = Api.open(path);
                try {
                    if (connection.getResponseCode() != 200) {
                        throw new Exception("APK indirilemedi (" + connection.getResponseCode() + ").");
                    }

                    InputStream in = connection.getInputStream();
                    FileOutputStream out = new FileOutputStream(file);
                    byte[] buffer = new byte[16384];
                    int count;
                    long total = 0;
                    while ((count = in.read(buffer)) > 0) {
                        out.write(buffer, 0, count);
                        total += count;
                    }

                    out.close();
                    in.close();
                    int expected = connection.getContentLength();
                    if (expected > 0 && total != expected) {
                        throw new Exception("İndirme yarım kaldı; tekrar deneyin.");
                    }
                } finally {
                    connection.disconnect();
                }

                return file;
            }
        }, new BaseActivity.Done<File>() {
            @Override
            public void ok(File file) {
                Intent install = new Intent(Intent.ACTION_VIEW);
                install.setDataAndType(Uri.fromFile(file), "application/vnd.android.package-archive");
                install.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                activity.startActivity(install);
            }
        });
    }
}
