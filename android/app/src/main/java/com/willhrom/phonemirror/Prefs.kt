package com.willhrom.phonemirror

import android.content.Context
import android.os.Build
import android.provider.Settings
import org.json.JSONObject
import java.security.MessageDigest
import java.security.SecureRandom
import javax.crypto.SecretKeyFactory
import javax.crypto.spec.PBEKeySpec

/** Small settings store: pairing code, trusted laptops, background options. */
object Prefs {
    private fun sp(c: Context) = c.getSharedPreferences("phonemirror", Context.MODE_PRIVATE)

    fun code(c: Context): String {
        val s = sp(c)
        val v = s.getString("code", null)
        if (v == null || v.length < 6) {
            // Older 4-digit codes are too easy to guess: switch to 6 digits, keeping laptops already paired.
            val n = random6()
            s.edit().putString("code", n).apply()
            return n
        }
        return v
    }

    private fun random6() = (100000 + SecureRandom().nextInt(900000)).toString()

    /** New pairing code; laptops have to enter the new code once. */
    fun newCode(c: Context): String {
        val v = random6()
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

    /** Keep screen sharing approved between mirroring sessions (tap Start once per phone restart). */
    fun keepSharing(c: Context): Boolean = sp(c).getBoolean("keepSharing", true)
    fun setKeepSharing(c: Context, v: Boolean) = sp(c).edit().putBoolean("keepSharing", v).apply()

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

    // ---- App PIN (only a salted, slow hash is kept; the PIN itself is never stored) ----

    fun hasPin(c: Context) = !sp(c).getString("pinHash", null).isNullOrEmpty()

    private fun hash(pin: String, salt: ByteArray): ByteArray =
        SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256")
            .generateSecret(PBEKeySpec(pin.toCharArray(), salt, 60_000, 256)).encoded

    fun pinMatches(c: Context, pin: String): Boolean {
        val s = sp(c)
        val h = s.getString("pinHash", null) ?: return true
        val salt = android.util.Base64.decode(s.getString("pinSalt", "") ?: "", android.util.Base64.NO_WRAP)
        return MessageDigest.isEqual(hash(pin, salt), android.util.Base64.decode(h, android.util.Base64.NO_WRAP))
    }

    fun setPin(c: Context, pin: String?) {
        if (pin.isNullOrEmpty()) { sp(c).edit().remove("pinHash").remove("pinSalt").apply(); return }
        val salt = ByteArray(16).also { SecureRandom().nextBytes(it) }
        sp(c).edit()
            .putString("pinSalt", android.util.Base64.encodeToString(salt, android.util.Base64.NO_WRAP))
            .putString("pinHash", android.util.Base64.encodeToString(hash(pin, salt), android.util.Base64.NO_WRAP))
            .apply()
    }

    fun deviceId(c: Context): String =
        Settings.Secure.getString(c.contentResolver, Settings.Secure.ANDROID_ID) ?: "unknown"

    fun deviceName(c: Context): String =
        Settings.Global.getString(c.contentResolver, "device_name")?.takeIf { it.isNotBlank() } ?: Build.MODEL
}
