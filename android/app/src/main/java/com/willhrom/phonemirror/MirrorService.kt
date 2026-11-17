package com.willhrom.phonemirror

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.drawable.Icon
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.AudioManager
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.os.Build
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import android.util.DisplayMetrics
import android.view.Display
import android.view.WindowManager
import org.json.JSONObject
import java.io.IOException
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit

/**
 * Runs in the background. While idle it only listens on the Wi-Fi network (tiny battery cost).
 * When a trusted laptop connects it captures the screen, encodes H.264 and streams it,
 * and passes the laptop's taps and keys to [ControlService].
 */
class MirrorService : Service() {

    companion object {
        const val ACTION_IDLE = "com.willhrom.phonemirror.IDLE"
        const val ACTION_CAPTURE = "com.willhrom.phonemirror.CAPTURE"
        const val ACTION_STOP_SHARING = "com.willhrom.phonemirror.STOP_SHARING"
        const val ACTION_QUIT = "com.willhrom.phonemirror.QUIT"
        const val ACTION_SHARE = "com.willhrom.phonemirror.SHARE"

        private const val CH_STATUS = "status"
        private const val CH_REQUEST = "request"
        private const val ID_STATUS = 1
        private const val ID_REQUEST = 2
        private const val KEEP_SHARING_MS = 2 * 60 * 1000L

        @Volatile var running = false
        @Volatile var capturing = false
        @Volatile var clientName: String? = null
        @Volatile var waitingFor: String? = null
        @Volatile var listener: (() -> Unit)? = null
        /** Set by the visible activity: lets it start sharing right away when a laptop asks. */
        @Volatile var onShareRequest: ((String) -> Unit)? = null
        @Volatile var instance: MirrorService? = null

        fun startIdle(c: Context) {
            c.startForegroundService(Intent(c, MirrorService::class.java).setAction(ACTION_IDLE))
        }
    }

    private class Settings(val maxSize: Int = 1600, val bitrate: Int = 12_000_000, val fps: Int = 60)

    private val main = Handler(Looper.getMainLooper())
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var server: ServerSocket? = null
    private var port = 8765
    private var nsd: NsdManager? = null
    private var nsdListener: NsdManager.RegistrationListener? = null
    @Volatile private var client: WsConnection? = null
    @Volatile private var encoder: Encoder? = null
    @Volatile private var settings = Settings()
    @Volatile private var needKey = true
    private var size = Pair(0, 0)
    private val queue = LinkedBlockingQueue<Packet>(96)
    private var wakeLock: PowerManager.WakeLock? = null
    private val releaseLater = Runnable { stopSharing() }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action != ACTION_QUIT) {
            running = true
            instance = this
        }
        when (intent?.action) {
            ACTION_QUIT -> { quit(); return START_NOT_STICKY }
            ACTION_STOP_SHARING -> {
                if (Prefs.background(this)) stopSharing() else quit()
                return START_NOT_STICKY
            }
            ACTION_CAPTURE -> {
                goForeground(capture = true)
                beginCapture(intent)
            }
            else -> goForeground(capture = capturing)
        }
        startServer()
        registerNsd()
        notifyChanged()
        return START_STICKY
    }

    // ---------------- foreground + notifications ----------------

    private fun goForeground(capture: Boolean) {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CH_STATUS, "Ready for your laptop", NotificationManager.IMPORTANCE_MIN))
        nm.createNotificationChannel(NotificationChannel(CH_REQUEST, "Requests to share your screen", NotificationManager.IMPORTANCE_HIGH))
        val n = statusNotification()
        when {
            Build.VERSION.SDK_INT >= 34 -> startForeground(ID_STATUS, n,
                if (capture) ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION else ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
            Build.VERSION.SDK_INT >= 29 && capture -> startForeground(ID_STATUS, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
            else -> startForeground(ID_STATUS, n)
        }
    }

    private fun statusNotification(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val text = when {
            clientName != null -> "Mirroring to $clientName"
            capturing -> "Sharing is on. Waiting for your laptop"
            else -> "Ready for your laptop"
        }
        val b = Notification.Builder(this, CH_STATUS)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("Phone Mirror")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setShowWhen(false)
        if (capturing) {
            val stop = PendingIntent.getService(this, 1,
                Intent(this, MirrorService::class.java).setAction(ACTION_STOP_SHARING), PendingIntent.FLAG_IMMUTABLE)
            b.addAction(Notification.Action.Builder(null as Icon?, "Stop sharing", stop).build())
        }
        return b.build()
    }

    private fun updateStatus() {
        if (!running) return
        try { getSystemService(NotificationManager::class.java).notify(ID_STATUS, statusNotification()) } catch (_: Exception) { }
        notifyChanged()
    }

    private fun notifyChanged() {
        try { listener?.invoke() } catch (_: Exception) { }
    }

    /** A laptop wants to mirror but sharing is off: ask the user (Android requires a tap). */
    private fun askToShare(pcName: String) {
        waitingFor = pcName
        notifyChanged()
        val visible = onShareRequest
        if (visible != null) { main.post { visible(pcName) }; return }
        val intent = Intent(this, MainActivity::class.java).setAction(ACTION_SHARE).putExtra("pc", pcName)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
        val pi = PendingIntent.getActivity(this, 3, intent, PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val n = Notification.Builder(this, CH_REQUEST)
            .setSmallIcon(R.drawable.ic_stat)
            .setContentTitle("Share your screen with $pcName?")
            .setContentText("Tap to start mirroring")
            .setContentIntent(pi)
            .setAutoCancel(true)
            .setCategory(Notification.CATEGORY_CALL)
            .addAction(Notification.Action.Builder(null as Icon?, "Share screen", pi).build())
            .build()
        try { getSystemService(NotificationManager::class.java).notify(ID_REQUEST, n) } catch (_: Exception) { }
    }

    // ---------------- screen capture ----------------

    private fun beginCapture(intent: Intent) {
        val code = intent.getIntExtra("code", 0)
        val data: Intent? = if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra("data", Intent::class.java)
        else @Suppress("DEPRECATION") intent.getParcelableExtra("data")
        if (data == null) return
        releaseCapture()
        val mpm = getSystemService(MediaProjectionManager::class.java)
        val p = try { mpm.getMediaProjection(code, data) } catch (e: Exception) { null } ?: return
        p.registerCallback(object : MediaProjection.Callback() {
            override fun onStop() { main.post { if (projection === p) stopSharing() } }
        }, main)
        projection = p
        capturing = true
        waitingFor = null
        getSystemService(NotificationManager::class.java).cancel(ID_REQUEST)
        getSystemService(DisplayManager::class.java).registerDisplayListener(displayListener, main)
        if (client != null) startEncoding() else main.postDelayed(releaseLater, KEEP_SHARING_MS)
        updateStatus()
    }

    /** Turns screen sharing off but keeps waiting for the laptop (if background is on). */
    private fun stopSharing(keepService: Boolean = Prefs.background(this)) {
        releaseCapture()
        if (!keepService) { quit(); return }
        if (running) goForeground(capture = false)
        updateStatus()
    }

    private fun releaseCapture() {
        main.removeCallbacks(releaseLater)
        stopEncoding()
        try { display?.release() } catch (_: Exception) { }
        display = null
        val p = projection
        projection = null
        capturing = false
        try { p?.stop() } catch (_: Exception) { }
        try { getSystemService(DisplayManager::class.java).unregisterDisplayListener(displayListener) } catch (_: Exception) { }
    }

    private fun captureSize(maxSize: Int): Triple<Int, Int, Int> {
        val dm = DisplayMetrics()
        @Suppress("DEPRECATION")
        getSystemService(WindowManager::class.java).defaultDisplay.getRealMetrics(dm)
        var w = dm.widthPixels
        var h = dm.heightPixels
        val longSide = maxOf(w, h)
        if (maxSize in 1 until longSide) {
            val s = maxSize.toFloat() / longSide
            w = (w * s).toInt()
            h = (h * s).toInt()
        }
        return Triple(w and 7.inv(), h and 7.inv(), dm.densityDpi)
    }

    private fun startEncoding() {
        val proj = projection ?: return
        val c = client ?: return
        main.removeCallbacks(releaseLater)
        stopEncoding()
        val (w, h, dpi) = captureSize(settings.maxSize)
        queue.clear()
        needKey = true
        queue.offer(Packet(0, 0, null, JSONObject().put("t", "config").put("w", w).put("h", h).toString()))
        val enc = Encoder(w, h, settings.bitrate, settings.fps) { enqueue(it) }
        val surface = try { enc.start() } catch (e: Exception) {
            queue.offer(Packet(0, 0, null, JSONObject().put("t", "error").put("text", "Video encoder failed: ${e.message}").toString()))
            return
        }
        encoder = enc
        size = Pair(w, h)
        val vd = display
        if (vd == null) {
            display = proj.createVirtualDisplay("PhoneMirror", w, h, dpi,
                DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, surface, null, null)
        } else {
            vd.resize(w, h, dpi)
            vd.surface = surface
        }
        acquireWake()
        if (c !== client) stopEncoding()
    }

    private fun stopEncoding() {
        val e = encoder
        encoder = null
        try { display?.surface = null } catch (_: Exception) { }
        e?.stop()
        releaseWake()
    }

    private val displayListener = object : DisplayManager.DisplayListener {
        override fun onDisplayAdded(displayId: Int) {}
        override fun onDisplayRemoved(displayId: Int) {}
        override fun onDisplayChanged(displayId: Int) {
            if (displayId != Display.DEFAULT_DISPLAY || encoder == null) return
            val (w, h, _) = captureSize(settings.maxSize)
            if (Pair(w, h) != size) startEncoding()   // rotated
        }
    }

    // ---------------- sending ----------------

    /** Queues video for the laptop. If the network falls behind, old frames are dropped for low lag. */
    private fun enqueue(p: Packet) {
        if (p.flags == 0 && p.text == null && needKey) return
        if (p.flags == Packet.KEY) needKey = false
        if (!queue.offer(p) || queue.size > 10) {
            val keep = queue.filter { it.text != null || it.flags == Packet.CONFIG }
            queue.clear()
            queue.addAll(keep)
            needKey = true
            encoder?.requestKeyFrame()
        }
    }

    private fun sendText(json: JSONObject) {
        queue.offer(Packet(0, 0, null, json.toString()))
    }

    private fun writerLoop(c: WsConnection) {
        val header = ByteBuffer.allocate(9)
        while (client === c) {
            val p = try { queue.poll(500, TimeUnit.MILLISECONDS) } catch (_: InterruptedException) { break } ?: continue
            try {
                if (p.text != null) c.sendText(p.text)
                else if (p.data != null) {
                    header.clear()
                    header.put(p.flags.toByte())
                    header.putLong(p.pts)
                    c.send(2, p.data, header.array().copyOf())
                }
            } catch (_: Exception) {
                c.close()
                break
            }
        }
    }

    // ---------------- network ----------------

    private fun startServer() {
        if (server != null) return
        for (p in 8765..8775) {
            try { server = ServerSocket(p); port = p; break } catch (_: IOException) { }
        }
        val s = server ?: return
        Thread({
            while (!s.isClosed) {
                val sock = try { s.accept() } catch (_: IOException) { break }
                Thread({ handleSocket(sock) }, "pm-client").start()
            }
        }, "pm-accept").start()
    }

    private fun info(): String = JSONObject()
        .put("app", "phonemirror")
        .put("id", Prefs.deviceId(this))
        .put("model", Build.MODEL)
        .put("name", Prefs.deviceName(this))
        .put("port", port)
        .put("sharing", capturing)
        .toString()

    private fun handleSocket(sock: Socket) {
        sock.tcpNoDelay = true
        val c = WsConnection(sock)
        try {
            if (!c.handshake { info() }) { c.close(); return }
            var approved = false
            while (true) {
                val text = c.readText() ?: break
                val m = try { JSONObject(text) } catch (_: Exception) { continue }
                when (m.optString("t")) {
                    "hello" -> {
                        val pcId = m.optString("pcId")
                        val pcName = m.optString("pcName").ifBlank { "your laptop" }
                        val code = m.optString("code")
                        if (Prefs.isTrusted(this, pcId) || (code.isNotEmpty() && code == Prefs.code(this))) {
                            Prefs.trust(this, pcId)
                            approved = true
                            settings = Settings(
                                m.optInt("maxSize", 1600).coerceIn(480, 2560),
                                m.optInt("bitrate", 12_000_000).coerceIn(1_000_000, 60_000_000),
                                m.optInt("fps", 60).coerceIn(15, 120))
                            attach(c, pcName)
                        } else {
                            c.sendText(JSONObject().put("t", if (code.isEmpty()) "needCode" else "badCode").toString())
                        }
                    }
                    "sync" -> if (approved) { needKey = true; encoder?.requestKeyFrame() }
                    "key" -> if (approved) key(m.optString("k"))
                    else -> if (approved) {
                        val ctl = ControlService.instance
                        if (ctl != null) ctl.handle(m) else sendText(JSONObject().put("t", "control").put("enabled", false))
                    }
                }
            }
        } catch (_: Exception) {
        } finally {
            detach(c)
            c.close()
        }
    }

    private fun key(k: String) {
        when (k) {
            "volup", "voldown" -> {
                val am = getSystemService(AudioManager::class.java)
                am.adjustStreamVolume(AudioManager.STREAM_MUSIC,
                    if (k == "volup") AudioManager.ADJUST_RAISE else AudioManager.ADJUST_LOWER, AudioManager.FLAG_SHOW_UI)
            }
            else -> ControlService.instance?.handle(JSONObject().put("t", "key").put("k", k))
        }
    }

    private fun attach(c: WsConnection, pcName: String) {
        val old = client
        client = c
        clientName = pcName
        if (old != null && old !== c) old.close()
        queue.clear()
        Thread({ writerLoop(c) }, "pm-writer").start()
        sendText(JSONObject().put("t", "ok").put("control", ControlService.instance != null)
            .put("name", Prefs.deviceName(this)).put("sharing", capturing))
        main.post {
            if (projection != null) startEncoding() else askToShare(pcName)
            updateStatus()
        }
    }

    private fun detach(c: WsConnection) {
        if (client !== c) return
        client = null
        clientName = null
        main.post {
            if (client == null) {
                stopEncoding()
                waitingFor = null
                getSystemService(NotificationManager::class.java).cancel(ID_REQUEST)
                if (capturing) main.postDelayed(releaseLater, KEEP_SHARING_MS)
                updateStatus()
            }
        }
    }

    /** Lets the activity tell a waiting laptop what's happening. */
    fun controlChanged() {
        if (client != null) sendText(JSONObject().put("t", "control").put("enabled", ControlService.instance != null))
    }

    private fun registerNsd() {
        if (nsdListener != null) return
        val info = NsdServiceInfo().apply {
            serviceName = Prefs.deviceName(this@MirrorService)
            serviceType = "_phonemirror._tcp"
            setPort(this@MirrorService.port)
            setAttribute("id", Prefs.deviceId(this@MirrorService))
            setAttribute("model", Build.MODEL)
        }
        val l = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(s: NsdServiceInfo) {}
            override fun onRegistrationFailed(s: NsdServiceInfo, e: Int) {}
            override fun onServiceUnregistered(s: NsdServiceInfo) {}
            override fun onUnregistrationFailed(s: NsdServiceInfo, e: Int) {}
        }
        try {
            nsd = getSystemService(NsdManager::class.java)
            nsd?.registerService(info, NsdManager.PROTOCOL_DNS_SD, l)
            nsdListener = l
        } catch (_: Exception) { }
    }

    // ---------------- power ----------------

    private fun acquireWake() {
        if (wakeLock?.isHeld == true) return
        @Suppress("DEPRECATION")
        wakeLock = getSystemService(PowerManager::class.java)
            .newWakeLock(PowerManager.SCREEN_DIM_WAKE_LOCK or PowerManager.ACQUIRE_CAUSES_WAKEUP, "phonemirror:mirror")
            .apply { acquire(4 * 60 * 60 * 1000L) }
    }

    private fun releaseWake() {
        try { if (wakeLock?.isHeld == true) wakeLock?.release() } catch (_: Exception) { }
        wakeLock = null
    }

    // ---------------- shutdown ----------------

    private fun quit() {
        running = false
        val c = client
        client = null
        clientName = null
        c?.close()
        releaseCapture()
        try { server?.close() } catch (_: Exception) { }
        server = null
        try { nsdListener?.let { nsd?.unregisterService(it) } } catch (_: Exception) { }
        nsdListener = null
        getSystemService(NotificationManager::class.java).cancel(ID_REQUEST)
        instance = null
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
        notifyChanged()
    }

    override fun onDestroy() {
        if (running) quit()
        super.onDestroy()
    }
}
