package com.willhrom.phonemirror

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.os.Build
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL

/**
 * Keeps the app up to date from the project's GitHub releases (the same place the laptop app comes from).
 * The first update needs the user's OK; after that Android 12+ can install updates quietly.
 */
object AppUpdater {
    private const val API = "https://api.github.com/repos/willhrom/PhoneMirror/releases/latest"
    private const val CH = "updates"
    private const val ID_NOTE = 7
    private const val CHECK_EVERY_MS = 12 * 60 * 60 * 1000L

    @Volatile var latestVersion = 0L
        private set
    @Volatile var latestName = ""
        private set
    @Volatile var status: String? = null
    @Volatile var busy = false
        private set
    @Volatile var listener: (() -> Unit)? = null
    private var apkUrl = ""

    fun installedVersion(c: Context): Long {
        val info = c.packageManager.getPackageInfo(c.packageName, 0)
        @Suppress("DEPRECATION")
        return if (Build.VERSION.SDK_INT >= 28) info.longVersionCode else info.versionCode.toLong()
    }

    fun installedName(c: Context): String =
        c.packageManager.getPackageInfo(c.packageName, 0).versionName ?: "?"

    fun updateAvailable(c: Context) = latestVersion > installedVersion(c)

    /** Allowed to install its own updates ("Install unknown apps" for Phone Mirror). */
    fun canInstall(c: Context) = c.packageManager.canRequestPackageInstalls()

    private fun prefs(c: Context) = c.getSharedPreferences("phonemirror", Context.MODE_PRIVATE)
    fun auto(c: Context) = prefs(c).getBoolean("autoUpdate", true)
    fun setAuto(c: Context, v: Boolean) = prefs(c).edit().putBoolean("autoUpdate", v).apply()

    private fun changed() { try { listener?.invoke() } catch (_: Exception) { } }

    /** Checks GitHub (at most every 12 h unless [force]) and, if allowed, installs a newer version. */
    fun check(c: Context, force: Boolean = false, installIfAuto: Boolean = true) {
        val app = c.applicationContext
        val last = prefs(app).getLong("lastCheck", 0)
        if (!force && System.currentTimeMillis() - last < CHECK_EVERY_MS && latestVersion != 0L) {
            if (installIfAuto) maybeAutoInstall(app)
            return
        }
        Thread({
            try {
                val conn = URL(API).openConnection() as HttpURLConnection
                conn.setRequestProperty("User-Agent", "PhoneMirror-Android")
                conn.connectTimeout = 10_000
                conn.readTimeout = 10_000
                val json = JSONObject(conn.inputStream.bufferedReader().readText())
                val tag = json.optString("tag_name").trimStart('v', 'V')
                val code = tag.substringAfterLast('.').toLongOrNull() ?: 0L
                val assets = json.optJSONArray("assets")
                var url = ""
                if (assets != null) for (i in 0 until assets.length()) {
                    val a = assets.getJSONObject(i)
                    if (a.optString("name") == "PhoneMirror.apk") url = a.optString("browser_download_url")
                }
                prefs(app).edit().putLong("lastCheck", System.currentTimeMillis()).apply()
                if (url.isNotEmpty()) {
                    latestVersion = code
                    latestName = tag
                    apkUrl = url
                }
                if (force && !updateAvailable(app)) status = "You have the latest version"
                changed()
                if (updateAvailable(app)) {
                    if (installIfAuto) maybeAutoInstall(app) else notifyAvailable(app)
                }
            } catch (_: Exception) {
                if (force) { status = "Couldn't check for updates. Are you online?"; changed() }
            }
        }, "pm-update-check").start()
    }

    private fun maybeAutoInstall(c: Context) {
        if (!updateAvailable(c)) return
        // Never interrupt a mirroring session; try again later.
        if (MirrorService.clientName != null) return
        if (auto(c) && canInstall(c)) install(c) else notifyAvailable(c)
    }

    private fun notifyAvailable(c: Context) {
        val nm = c.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CH, "App updates", NotificationManager.IMPORTANCE_DEFAULT))
        val open = PendingIntent.getActivity(c, 7, Intent(c, MainActivity::class.java).setAction(ACTION_UPDATE)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val n = Notification.Builder(c, CH)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("Phone Mirror update ready")
            .setContentText("Version $latestName. Tap to install.")
            .setContentIntent(open)
            .setAutoCancel(true)
            .build()
        try { nm.notify(ID_NOTE, n) } catch (_: Exception) { }
    }

    const val ACTION_UPDATE = "com.willhrom.phonemirror.UPDATE"

    /** Downloads the new version and hands it to Android's installer. */
    fun install(c: Context) {
        val app = c.applicationContext
        if (busy || apkUrl.isEmpty()) return
        busy = true
        status = "Downloading version $latestName…"
        changed()
        Thread({
            try {
                val file = File(app.cacheDir, "update.apk")
                val conn = URL(apkUrl).openConnection() as HttpURLConnection
                conn.setRequestProperty("User-Agent", "PhoneMirror-Android")
                conn.instanceFollowRedirects = true
                conn.connectTimeout = 15_000
                conn.readTimeout = 30_000
                conn.inputStream.use { input -> file.outputStream().use { input.copyTo(it) } }
                if (file.length() < 100_000) throw IllegalStateException("download looks incomplete")

                status = "Installing…"
                changed()
                val installer = app.packageManager.packageInstaller
                val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL)
                params.setAppPackageName(app.packageName)
                if (Build.VERSION.SDK_INT >= 31) params.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
                val sessionId = installer.createSession(params)
                installer.openSession(sessionId).use { session ->
                    session.openWrite("PhoneMirror.apk", 0, file.length()).use { out ->
                        file.inputStream().use { it.copyTo(out) }
                        session.fsync(out)
                    }
                    val flags = PendingIntent.FLAG_UPDATE_CURRENT or
                        (if (Build.VERSION.SDK_INT >= 31) PendingIntent.FLAG_MUTABLE else 0)
                    val result = PendingIntent.getBroadcast(app, sessionId, Intent(app, InstallReceiver::class.java), flags)
                    session.commit(result.intentSender)
                }
            } catch (e: Exception) {
                busy = false
                status = "Update failed: ${e.message}"
                changed()
            }
        }, "pm-update-install").start()
    }

    internal fun finished(message: String?) {
        busy = false
        status = message
        changed()
    }
}

/** Android's installer reports back here. */
class InstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                // Android wants a tap to confirm (first update, or some phone brands): show it.
                @Suppress("DEPRECATION")
                val confirm: Intent? = if (Build.VERSION.SDK_INT >= 33)
                    intent.getParcelableExtra(Intent.EXTRA_INTENT, Intent::class.java)
                else intent.getParcelableExtra(Intent.EXTRA_INTENT)
                if (confirm != null) {
                    confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    try { context.startActivity(confirm) } catch (_: Exception) {
                        val pi = PendingIntent.getActivity(context, 8, confirm, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
                        val nm = context.getSystemService(NotificationManager::class.java)
                        nm.createNotificationChannel(NotificationChannel("updates", "App updates", NotificationManager.IMPORTANCE_DEFAULT))
                        nm.notify(7, Notification.Builder(context, "updates").setSmallIcon(R.drawable.ic_stat)
                            .setContentTitle("Finish updating Phone Mirror").setContentText("Tap to install the update")
                            .setContentIntent(pi).setAutoCancel(true).build())
                    }
                }
                AppUpdater.finished("Tap Update to finish installing")
            }
            PackageInstaller.STATUS_SUCCESS -> AppUpdater.finished("Updated")
            else -> AppUpdater.finished("Update didn't install: " +
                (intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE) ?: "unknown error"))
        }
    }
}
