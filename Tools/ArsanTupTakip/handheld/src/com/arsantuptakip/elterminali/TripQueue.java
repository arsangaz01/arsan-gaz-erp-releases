package com.arsantuptakip.elterminali;

import java.text.SimpleDateFormat;
import java.util.ArrayList;
import java.util.Date;
import java.util.List;
import java.util.Locale;
import java.util.TimeZone;
import java.util.concurrent.atomic.AtomicBoolean;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

// Trip loads, deliveries and empty pickups saved on the handheld while the server was unreachable. They are sent in
// the order they were made with the key of the first attempt, so the server never records one twice. Same rules as
// the phone app's OfflineTripOps.
final class TripQueue {
    static final String LOAD = "/api/sefer/yukle";
    static final String DELIVER = "/api/sefer/teslim";
    static final String PICKUP = "/api/sefer/bos-alim";

    private static final String KEY = "trip_ops";
    private static final AtomicBoolean FLUSHING = new AtomicBoolean(false);

    private TripQueue() {
    }

    static final class Result {
        int sent;
        final List<String> done = new ArrayList<String>();
        final List<String> rejected = new ArrayList<String>();
    }

    static synchronized int count() {
        return read().length();
    }

    static synchronized int countFor(int tripId) {
        JSONArray ops = read();
        int count = 0;
        for (int i = 0; i < ops.length(); i++) {
            JSONObject row = ops.optJSONObject(i);
            if (row != null && row.optInt("tripId") == tripId) {
                count++;
            }
        }

        return count;
    }

    // body: the request as it would have been sent; label: what the driver sees in the list.
    static synchronized void add(String path, JSONObject body, String key, String label) throws JSONException {
        SimpleDateFormat utc = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss'Z'", Locale.US);
        utc.setTimeZone(TimeZone.getTimeZone("UTC"));
        body.put("offlineAt", utc.format(new Date()));
        JSONObject op = new JSONObject();
        op.put("path", path);
        op.put("tripId", body.optInt("tripId"));
        op.put("body", body);
        op.put("key", key);
        op.put("label", label);
        op.put("savedAt", new SimpleDateFormat("dd.MM HH:mm", Locale.US).format(new Date()));
        JSONArray ops = read();
        ops.put(op);
        write(ops);
    }

    // Runs on a background thread, one flush at a time; storage stays readable meanwhile. Stops at the first
    // operation that cannot be sent yet so the order is kept.
    static Result flush() {
        Result result = new Result();
        if (!FLUSHING.compareAndSet(false, true)) {
            return result;
        }

        try {
            JSONObject op;
            while ((op = first()) != null) {
                if (!send(op, result)) {
                    return result;
                }

                removeKey(op.optString("key"));
                result.sent++;
            }

            return result;
        } finally {
            FLUSHING.set(false);
        }
    }

    // False when the operation must stay in the queue (no connection or a temporary refusal).
    private static boolean send(JSONObject op, Result result) {
        String label = op.optString("label");
        try {
            JSONObject answer = Api.post(op.optString("path"), op.optJSONObject("body"), op.optString("key"));
            String message = Api.str(answer, "message");
            result.done.add(label + ": " + (message == null ? "kaydedildi" : message.split("\n")[0]));
            return true;
        } catch (Api.ApiException e) {
            if (e.unreachable || !permanent(e)) {
                return false;
            }

            result.rejected.add(label + " (" + op.optString("savedAt") + "): " + e.getMessage());
            return true;
        }
    }

    private static synchronized JSONObject first() {
        JSONArray ops = read();
        return ops.length() == 0 ? null : ops.optJSONObject(0);
    }

    private static synchronized void removeKey(String key) {
        JSONArray ops = read();
        JSONArray left = new JSONArray();
        for (int i = 0; i < ops.length(); i++) {
            JSONObject op = ops.optJSONObject(i);
            if (!key.equals(op.optString("key"))) {
                left.put(op);
            }
        }

        write(left);
    }

    // An expired login, forced password change, 403 (yetki/oturum), parallel change (409) and rate limits are
    // retried later; other 4xx answers mean the server will never accept this operation.
    private static boolean permanent(Api.ApiException e) {
        return e.status >= 400 && e.status < 500
            && e.status != 401 && e.status != 403 && e.status != 408 && e.status != 409 && e.status != 429
            && !e.mustChangePassword && !e.sessionEnded;
    }

    private static JSONArray read() {
        try {
            return new JSONArray(Api.prefs().getString(KEY, "[]"));
        } catch (JSONException e) {
            return new JSONArray();
        }
    }

    private static void write(JSONArray ops) {
        Api.prefs().edit().putString(KEY, ops.toString()).apply();
    }
}
