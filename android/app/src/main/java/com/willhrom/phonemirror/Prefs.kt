package com.willhrom.phonemirror

import android.content.Context
import android.os.Build
import android.provider.Settings

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

    fun deviceId(c: Context): String =
        Settings.Secure.getString(c.contentResolver, Settings.Secure.ANDROID_ID) ?: "unknown"

    fun deviceName(c: Context): String =
        Settings.Global.getString(c.contentResolver, "device_name")?.takeIf { it.isNotBlank() } ?: Build.MODEL
}
