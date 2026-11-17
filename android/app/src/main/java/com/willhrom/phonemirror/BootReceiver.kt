package com.willhrom.phonemirror

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Starts waiting for the laptop when the phone turns on (or the app is updated). */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val action = intent.action ?: return
        if (action != Intent.ACTION_BOOT_COMPLETED && action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (!Prefs.background(context) || !Prefs.startAtBoot(context)) return
        try { MirrorService.startIdle(context) } catch (_: Exception) { }
    }
}
