package com.willhrom.phonemirror

import android.content.Context
import android.os.Build
import android.provider.Settings
import org.json.JSONObject

/** Small settings store: pairing code, trusted laptops, background options. */
object Prefs {
    private fun sp(c: Context) = c.getSharedPreferences("phonemirror", Context.MODE_PRIVATE)

    fun code(c: Context): String {
        val s = sp(c)
        return s.getString("code", null) ?: newCode(c)
    }

    /** New pairing code; laptops have to enter the new code once. */
    fun newCode(c: Context): String {
        val v = (1000..9999).random().toString()
        sp(c).edit().putString("code", v).putStringSet("trusted", emptySet()).apply()
        return v
    }

    fun isTrusted(c: Context, pcId: String): Boolean =
        pcId.isNotEmpty() && (sp(c).getStringSet("trusted", emptySet()) ?: emptySet()).contains(pcId)

    fun trust(c: Context, pcId: String) {
        val set = HashSet(sp(c).getStringSet("trusted", emptySet()) ?: emptySet())
        set.add(pcId)
        sp(c).edit().putStringSet("trusted", set).apply()
    }

    /** Stay ready for the laptop in the background. */
    fun background(c: Context): Boolean = sp(c).getBoolean("background", true)
    fun setBackground(c: Context, v: Boolean) = sp(c).edit().putBoolean("background", v).apply()

    /** Start waiting for the laptop when the phone turns on. */
    fun startAtBoot(c: Context): Boolean = sp(c).getBoolean("boot", true)
    fun setStartAtBoot(c: Context, v: Boolean) = sp(c).edit().putBoolean("boot", v).apply()

    /** A laptop this phone has mirrored to, with its private direct-link (Wi-Fi Direct) details. */
    class Laptop(val id: String, val name: String, val ssid: String, val pass: String, val time: Long)

    fun saveLaptop(c: Context, id: String, name: String, ssid: String, pass: String) {
        if (id.isEmpty() || ssid.isEmpty() || pass.length < 8) return
        val all = try { JSONObject(sp(c).getString("laptops", "{}") ?: "{}") } catch (_: Exception) { JSONObject() }
        all.put(id, JSONObject().put("name", name).put("ssid", ssid).put("pass", pass).put("t", System.currentTimeMillis()))
        sp(c).edit().putString("laptops", all.toString()).apply()
    }

    /** The laptop used most recently (the one "Connect directly" links to). */
    fun lastLaptop(c: Context): Laptop? {
        val all = try { JSONObject(sp(c).getString("laptops", "{}") ?: "{}") } catch (_: Exception) { return null }
        var best: Laptop? = null
        for (k in all.keys()) {
            val o = all.optJSONObject(k) ?: continue
            val l = Laptop(k, o.optString("name", "your laptop"), o.optString("ssid"), o.optString("pass"), o.optLong("t"))
            if (l.ssid.isNotEmpty() && (best == null || l.time > best.time)) best = l
        }
        return best
    }

    fun deviceId(c: Context): String =
        Settings.Secure.getString(c.contentResolver, Settings.Secure.ANDROID_ID) ?: "unknown"

    fun deviceName(c: Context): String =
        Settings.Global.getString(c.contentResolver, "device_name")?.takeIf { it.isNotBlank() } ?: Build.MODEL
}
