package com.willhrom.phonemirror

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.media.projection.MediaProjectionManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.text.TextUtils
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.Switch
import android.widget.TextView

class MainActivity : Activity() {

    private val bg = Color.rgb(11, 13, 18)
    private val card = Color.rgb(22, 26, 35)
    private val line = Color.rgb(35, 40, 55)
    private val fg = Color.rgb(233, 236, 243)
    private val muted = Color.rgb(138, 147, 168)
    private val accent = Color.rgb(79, 140, 255)
    private val accent2 = Color.rgb(124, 92, 255)
    private val good = Color.rgb(52, 211, 153)
    private val warn = Color.rgb(251, 191, 36)

    private lateinit var dot: View
    private lateinit var titleView: TextView
    private lateinit var subtitle: TextView
    private lateinit var mainButton: Button
    private lateinit var codeText: TextView
    private lateinit var controlState: TextView
    private lateinit var controlButton: Button
    private lateinit var restrictedHint: TextView
    private lateinit var bgSwitch: Switch
    private lateinit var bootSwitch: Switch
    private lateinit var batteryButton: Button

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.statusBarColor = bg
        window.navigationBarColor = bg
        buildUi()
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 2)
        }
        handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    private fun handleIntent(i: Intent?) {
        if (i?.action == MirrorService.ACTION_SHARE) requestCapture()
    }

    override fun onResume() {
        super.onResume()
        if (Prefs.background(this) && !MirrorService.running) {
            try { MirrorService.startIdle(this) } catch (_: Exception) { }
        }
        MirrorService.listener = { runOnUiThread { refresh() } }
        MirrorService.onShareRequest = { runOnUiThread { requestCapture() } }
        refresh()
    }

    override fun onPause() {
        super.onPause()
        MirrorService.listener = null
        MirrorService.onShareRequest = null
    }

    // ---------------- actions ----------------

    private var askedAudio = false

    private fun requestCapture() {
        if (MirrorService.capturing) return
        // Phone audio on the laptop needs this once (Android 10+). Mirroring works either way.
        if (Build.VERSION.SDK_INT >= 29 && !askedAudio &&
            checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            askedAudio = true
            requestPermissions(arrayOf(Manifest.permission.RECORD_AUDIO), 3)
            return
        }
        val mpm = getSystemService(MediaProjectionManager::class.java)
        try { startActivityForResult(mpm.createScreenCaptureIntent(), 1) } catch (_: Exception) { }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode == 3) requestCapture()
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode == 1 && resultCode == RESULT_OK && data != null) {
            val i = Intent(this, MirrorService::class.java)
                .setAction(MirrorService.ACTION_CAPTURE)
                .putExtra("code", resultCode)
                .putExtra("data", data)
            startForegroundService(i)
        }
    }

    private fun stopSharing() {
        startService(Intent(this, MirrorService::class.java).setAction(MirrorService.ACTION_STOP_SHARING))
    }

    // ---------------- state ----------------

    private fun refresh() {
        val running = MirrorService.running
        val capturing = MirrorService.capturing
        val client = MirrorService.clientName
        val waiting = MirrorService.waitingFor
        when {
            client != null && capturing -> state(good, "Mirroring", "Showing on $client")
            waiting != null -> state(warn, "$waiting wants to mirror", "Tap Share screen, then Start")
            capturing -> state(good, "Sharing is on", "Open Phone Mirror on your laptop and click Mirror")
            running -> state(accent, "Ready for your laptop", "Click Mirror on your laptop. You'll tap once here to allow it.")
            else -> state(muted, "Off", "Turn on \"Stay ready\" below, or tap Share screen")
        }
        mainButton.text = if (capturing) "Stop sharing" else "Share screen"
        mainButton.background = if (capturing) rounded(Color.rgb(58, 28, 32), 16, Color.rgb(90, 40, 45))
        else gradient()
        codeText.text = Prefs.code(this).toCharArray().joinToString("  ")

        val ctl = ControlService.instance != null
        controlState.text = if (ctl) "On: your laptop's mouse and keyboard work on this phone"
        else "Off: you can watch, but not tap or type from the laptop"
        controlState.setTextColor(if (ctl) good else muted)
        controlButton.visibility = if (ctl) View.GONE else View.VISIBLE
        restrictedHint.visibility = if (ctl) View.GONE else View.VISIBLE

        bgSwitch.isChecked = Prefs.background(this)
        bootSwitch.isChecked = Prefs.startAtBoot(this)
        bootSwitch.isEnabled = Prefs.background(this)
        val pm = getSystemService(PowerManager::class.java)
        batteryButton.visibility = if (pm.isIgnoringBatteryOptimizations(packageName)) View.GONE else View.VISIBLE
    }

    private fun state(color: Int, t: String, s: String) {
        (dot.background as GradientDrawable).setColor(color)
        titleView.text = t
        subtitle.text = s
    }

    // ---------------- UI ----------------

    private fun rounded(color: Int, radiusDp: Int, stroke: Int? = null) = GradientDrawable().apply {
        setColor(color)
        cornerRadius = dp(radiusDp).toFloat()
        if (stroke != null) setStroke(dp(1), stroke)
    }

    private fun gradient() = GradientDrawable(GradientDrawable.Orientation.TL_BR, intArrayOf(accent, accent2)).apply {
        cornerRadius = dp(16).toFloat()
    }

    private fun label(s: String, size: Float, color: Int, bold: Boolean = false) = TextView(this).apply {
        text = s
        textSize = size
        setTextColor(color)
        if (bold) typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
    }

    private fun cardView(): LinearLayout = LinearLayout(this).apply {
        orientation = LinearLayout.VERTICAL
        background = rounded(card, 18, line)
        setPadding(dp(18), dp(16), dp(18), dp(16))
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            .apply { topMargin = dp(14) }
    }

    private fun smallButton(s: String, onClick: () -> Unit) = Button(this).apply {
        text = s
        isAllCaps = false
        setTextColor(fg)
        textSize = 14f
        background = rounded(Color.rgb(32, 37, 50), 12)
        setPadding(dp(16), 0, dp(16), 0)
        minHeight = dp(42)
        layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(42)).apply { topMargin = dp(12) }
        setOnClickListener { onClick() }
    }

    private fun switchRow(parent: LinearLayout, t: String, d: String, onChange: (Boolean) -> Unit): Switch {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, dp(8), 0, dp(8))
        }
        val texts = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            layoutParams = LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f)
        }
        texts.addView(label(t, 15f, fg))
        texts.addView(label(d, 12.5f, muted))
        val sw = Switch(this)
        sw.setOnClickListener { onChange(sw.isChecked); refresh() }
        row.addView(texts)
        row.addView(sw)
        parent.addView(row)
        return sw
    }

    private fun buildUi() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(28), dp(20), dp(32))
        }

        // header
        val header = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        header.addView(ImageView(this).apply {
            setImageResource(R.mipmap.ic_launcher)
            layoutParams = LinearLayout.LayoutParams(dp(40), dp(40)).apply { rightMargin = dp(12) }
        })
        header.addView(label("Phone Mirror", 22f, fg, bold = true))
        root.addView(header)

        // status
        val status = cardView()
        val statusRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL; gravity = Gravity.CENTER_VERTICAL }
        dot = View(this).apply {
            background = GradientDrawable().apply { shape = GradientDrawable.OVAL; setColor(accent) }
            layoutParams = LinearLayout.LayoutParams(dp(10), dp(10)).apply { rightMargin = dp(10) }
        }
        titleView = label("", 20f, fg, bold = true).apply { ellipsize = TextUtils.TruncateAt.END; maxLines = 1 }
        statusRow.addView(dot)
        statusRow.addView(titleView)
        status.addView(statusRow)
        subtitle = label("", 14f, muted).apply { setPadding(0, dp(6), 0, 0) }
        status.addView(subtitle)
        mainButton = Button(this).apply {
            isAllCaps = false
            textSize = 17f
            setTextColor(Color.WHITE)
            typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL)
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(56)).apply { topMargin = dp(16) }
            setOnClickListener { if (MirrorService.capturing) stopSharing() else requestCapture() }
        }
        status.addView(mainButton)
        root.addView(status)

        // pairing code
        val code = cardView()
        code.addView(label("PAIRING CODE", 12f, muted, bold = true))
        codeText = label("", 34f, fg, bold = true).apply { setPadding(0, dp(6), 0, 0); letterSpacing = 0.08f }
        code.addView(codeText)
        code.addView(label("Type this on your laptop the first time it connects.", 13f, muted))
        code.addView(smallButton("New code") { Prefs.newCode(this); refresh() })
        root.addView(code)

        // control
        val control = cardView()
        control.addView(label("CONTROL FROM YOUR LAPTOP", 12f, muted, bold = true))
        controlState = label("", 14.5f, muted).apply { setPadding(0, dp(6), 0, 0) }
        control.addView(controlState)
        controlButton = smallButton("Turn on in Accessibility") {
            startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
        }
        control.addView(controlButton)
        restrictedHint = label("Find \"Phone Mirror control\" under Installed apps and switch it on. " +
            "If Android says it's a restricted setting, open App info › ⋮ › Allow restricted settings, then try again.",
            12.5f, muted).apply { setPadding(0, dp(10), 0, 0) }
        control.addView(restrictedHint)
        control.addView(smallButton("Open App info") {
            startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:$packageName")))
        })
        root.addView(control)

        // background
        val bgCard = cardView()
        bgCard.addView(label("IN THE BACKGROUND", 12f, muted, bold = true))
        bgSwitch = switchRow(bgCard, "Stay ready for your laptop", "Uses almost no battery until you mirror") { on ->
            Prefs.setBackground(this, on)
            if (on) MirrorService.startIdle(this)
            else if (!MirrorService.capturing) startService(Intent(this, MirrorService::class.java).setAction(MirrorService.ACTION_QUIT))
        }
        bootSwitch = switchRow(bgCard, "Start when the phone turns on", "So it's always ready") { on ->
            Prefs.setStartAtBoot(this, on)
        }
        batteryButton = smallButton("Don't let Android pause the app") {
            try {
                startActivity(Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:$packageName")))
            } catch (_: Exception) {
                startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
            }
        }
        bgCard.addView(batteryButton)
        root.addView(bgCard)

        root.addView(label("Your phone and laptop need to be on the same Wi-Fi. On the laptop, open Phone Mirror. " +
            "This phone shows up under My phones.", 13f, muted).apply { setPadding(dp(4), dp(18), dp(4), 0) })

        setContentView(ScrollView(this).apply {
            setBackgroundColor(bg)
            addView(root)
        })
    }
}
