package com.willhrom.phonemirror

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.text.InputType
import android.widget.EditText
import android.widget.FrameLayout
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
    private lateinit var keepSwitch: Switch
    private lateinit var batteryButton: Button
    private lateinit var updateText: TextView
    private lateinit var updateButton: Button
    private lateinit var autoSwitch: Switch
    private lateinit var directCard: LinearLayout
    private lateinit var directText: TextView
    private lateinit var directButton: Button
    private lateinit var lockView: LinearLayout
    private lateinit var lockInput: EditText
    private lateinit var lockErr: TextView
    private lateinit var pinText: TextView
    private lateinit var pinButtons: LinearLayout

    // App PIN: the settings screen stays hidden until it's entered. Locks again when you leave the app.
    private var unlocked = false
    private var openingSystemScreen = false
    private var badTries = 0
    private var lockedUntil = 0L

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
        // Act on each request once: reopening from Recents replays the old one (that could loop installs).
        if (i == null || i.flags and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY != 0) return
        val action = i.action
        i.action = null
        if (action == MirrorService.ACTION_SHARE) requestCapture()
        if (action == AppUpdater.ACTION_UPDATE) startUpdate()
    }

    private fun startUpdate() {
        if (!AppUpdater.canInstall(this)) {
            AppUpdater.status = "Allow Phone Mirror to install updates, then come back"
            startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:$packageName")))
        } else if (AppUpdater.updateAvailable(this)) AppUpdater.install(this, userAsked = true)
        else AppUpdater.check(this, force = true, installIfAuto = false)
        refresh()
    }

    override fun onStop() {
        super.onStop()
        if (!openingSystemScreen) unlocked = false
    }

    override fun startActivity(intent: Intent?) {
        openingSystemScreen = true
        super.startActivity(intent)
    }

    override fun startActivityForResult(intent: Intent?, requestCode: Int) {
        openingSystemScreen = true
        super.startActivityForResult(intent, requestCode)
    }

    private fun showLockIfNeeded() {
        val locked = Prefs.hasPin(this) && !unlocked
        lockView.visibility = if (locked) View.VISIBLE else View.GONE
        if (locked) { lockInput.setText(""); lockInput.requestFocus() }
    }

    private fun tryUnlock() {
        val pin = lockInput.text.toString()
        if (pin.isEmpty()) return
        val now = System.currentTimeMillis()
        if (now < lockedUntil) { lockErr.text = "Too many tries. Wait ${(lockedUntil - now + 999) / 1000} s."; return }
        lockInput.isEnabled = false
        Thread {
            val ok = Prefs.pinMatches(this, pin)
            runOnUiThread {
                lockInput.isEnabled = true
                if (ok) {
                    unlocked = true; badTries = 0; lockErr.text = ""
                    (getSystemService(INPUT_METHOD_SERVICE) as android.view.inputmethod.InputMethodManager)
                        .hideSoftInputFromWindow(lockInput.windowToken, 0)
                    showLockIfNeeded(); refresh()
                } else {
                    lockInput.setText("")
                    if (++badTries >= 5) { badTries = 0; lockedUntil = System.currentTimeMillis() + 30_000; lockErr.text = "Too many tries. Wait 30 s." }
                    else lockErr.text = "Wrong PIN"
                }
            }
        }.start()
    }

    private fun pinField(hint: String) = EditText(this).apply {
        inputType = InputType.TYPE_CLASS_NUMBER or InputType.TYPE_NUMBER_VARIATION_PASSWORD
        this.hint = hint
        filters = arrayOf<android.text.InputFilter>(android.text.InputFilter.LengthFilter(8))
    }

    /** mode: "set", "change" or "remove" */
    private fun pinDialog(mode: String) {
        val box = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(22), dp(8), dp(22), 0) }
        val old = pinField("Current PIN"); val a = pinField("New PIN (4 to 8 digits)"); val b = pinField("New PIN again")
        if (mode != "set") box.addView(old)
        if (mode != "remove") { box.addView(a); box.addView(b) }
        val err = TextView(this).apply { setTextColor(Color.rgb(220, 80, 80)); textSize = 13f }
        box.addView(err)
        val dlg = AlertDialog.Builder(this)
            .setTitle(when (mode) { "set" -> "Set a PIN"; "change" -> "Change your PIN"; else -> "Remove your PIN" })
            .setView(box)
            .setPositiveButton(if (mode == "remove") "Remove" else "Save", null)
            .setNegativeButton("Cancel", null)
            .create()
        dlg.setOnShowListener {
            dlg.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener {
                val o = old.text.toString(); val n = a.text.toString(); val n2 = b.text.toString()
                if (mode != "remove" && !Regex("^\\d{4,8}$").matches(n)) { err.text = "Use 4 to 8 digits."; return@setOnClickListener }
                if (mode != "remove" && n != n2) { err.text = "The two PINs don't match."; return@setOnClickListener }
                Thread {
                    val ok = mode == "set" && !Prefs.hasPin(this) || Prefs.pinMatches(this, o)
                    if (ok) Prefs.setPin(this, if (mode == "remove") null else n)
                    runOnUiThread {
                        if (!ok) err.text = "Current PIN is wrong."
                        else { dlg.dismiss(); refresh() }
                    }
                }.start()
            }
        }
        dlg.show()
    }

    override fun onResume() {
        super.onResume()
        openingSystemScreen = false
        showLockIfNeeded()
        if (Prefs.background(this) && !MirrorService.running) {
            try { MirrorService.startIdle(this) } catch (_: Exception) { }
        }
        MirrorService.listener = { runOnUiThread { refresh() } }
        AppUpdater.listener = { runOnUiThread { refresh() } }
        // Only look: opening the app never starts an install (that caused a loop with Android's confirm screen).
        AppUpdater.check(this, installIfAuto = false)
        MirrorService.onShareRequest = { runOnUiThread { requestCapture() } }
        refresh()
    }

    override fun onPause() {
        super.onPause()
        MirrorService.listener = null
        MirrorService.onShareRequest = null
        AppUpdater.listener = null
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
        // Android 14+: ask for the whole screen directly, so there's no "what to share" choice. Just Start.
        val intent = if (Build.VERSION.SDK_INT >= 34)
            mpm.createScreenCaptureIntent(android.media.projection.MediaProjectionConfig.createConfigForDefaultDisplay())
        else mpm.createScreenCaptureIntent()
        try { startActivityForResult(intent, 1) } catch (_: Exception) { }
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

    private fun refreshPin() {
        val has = Prefs.hasPin(this)
        pinText.text = if (has) "On. Asks for your PIN each time you open the app" else "Off. Anyone holding your phone can change these settings"
        pinButtons.removeAllViews()
        if (has) {
            pinButtons.addView(smallButton("Change PIN") { pinDialog("change") })
            pinButtons.addView(smallButton("Remove PIN") { pinDialog("remove") })
        } else pinButtons.addView(smallButton("Set a PIN") { pinDialog("set") })
    }

    private fun refresh() {
        refreshPin()
        val running = MirrorService.running
        val capturing = MirrorService.capturing
        val client = MirrorService.clientName
        val waiting = MirrorService.waitingFor
        when {
            client != null && capturing -> state(good, "Mirroring", "Showing on $client")
            waiting != null -> state(warn, "$waiting wants to mirror", "Tap Share screen, then Start")
            capturing -> state(good, "Sharing is on", "Click Mirror on your laptop. It opens right away.")
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
        keepSwitch.isChecked = Prefs.keepSharing(this)
        bootSwitch.isEnabled = Prefs.background(this)
        // updates
        val avail = AppUpdater.updateAvailable(this)
        val canInstall = AppUpdater.canInstall(this)
        updateText.text = AppUpdater.status ?: when {
            avail -> "Version ${AppUpdater.latestName} is ready (you have ${AppUpdater.installedName(this)})"
            AppUpdater.latestVersion > 0 -> "Version ${AppUpdater.installedName(this)}: up to date"
            else -> "Version ${AppUpdater.installedName(this)}"
        }
        updateButton.isEnabled = !AppUpdater.busy
        updateButton.text = when {
            !canInstall -> "Allow automatic updates"
            avail -> "Update now"
            else -> "Check for updates"
        }
        autoSwitch.isChecked = AppUpdater.auto(this)

        val laptop = Prefs.lastLaptop(this)
        directCard.visibility = if (laptop == null) View.GONE else View.VISIBLE
        if (laptop != null) {
            if (!DirectLink.supported()) {
                directText.text = "Direct connections need Android 10 or newer. Use your phone's hotspot instead."
                directButton.visibility = View.GONE
            } else {
                directText.text = DirectLink.state
                    ?: "Link straight to ${laptop.name}, no Wi-Fi or hotspot needed. Phone Mirror must be open on the laptop."
                directText.setTextColor(if (DirectLink.connected) good else muted)
                directButton.visibility = View.VISIBLE
                directButton.text = if (DirectLink.active) "Disconnect" else "Connect directly to ${laptop.name}"
            }
        }

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

        // direct link (no Wi-Fi)
        directCard = cardView()
        directCard.addView(label("NO WI-FI?", 12f, muted, bold = true))
        directText = label("", 14f, muted).apply { setPadding(0, dp(6), 0, 0) }
        directCard.addView(directText)
        directButton = smallButton("Connect directly") {
            val l = Prefs.lastLaptop(this) ?: return@smallButton
            if (DirectLink.active) DirectLink.disconnect(this)
            else {
                if (!MirrorService.running) MirrorService.startIdle(this)
                DirectLink.connect(this, l) { runOnUiThread { refresh() } }
            }
            refresh()
        }
        directCard.addView(directButton)
        root.addView(directCard)

        // pairing code
        val code = cardView()
        code.addView(label("PAIRING CODE", 12f, muted, bold = true))
        codeText = label("", 34f, fg, bold = true).apply { setPadding(0, dp(6), 0, 0); letterSpacing = 0.08f }
        code.addView(codeText)
        code.addView(label("Type this on your laptop the first time it connects. After 5 wrong tries the phone stops accepting codes for a minute.", 13f, muted))
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
        keepSwitch = switchRow(bgCard, "Keep sharing on", "Tap Start once; mirroring then opens instantly until you stop it or restart the phone") { on ->
            Prefs.setKeepSharing(this, on)
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

        // security
        val secCard = cardView()
        secCard.addView(label("SECURITY", 12f, muted, bold = true))
        pinText = label("", 14f, muted).apply { setPadding(0, dp(6), 0, 0) }
        secCard.addView(pinText)
        pinButtons = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        secCard.addView(pinButtons)
        root.addView(secCard)

        // updates
        val upCard = cardView()
        upCard.addView(label("UPDATES", 12f, muted, bold = true))
        updateText = label("", 14f, muted).apply { setPadding(0, dp(6), 0, 0) }
        upCard.addView(updateText)
        autoSwitch = switchRow(upCard, "Update automatically", "Installs new versions when you're not mirroring") { on ->
            AppUpdater.setAuto(this, on)
        }
        updateButton = smallButton("Check for updates") { AppUpdater.status = null; startUpdate() }
        upCard.addView(updateButton)
        root.addView(upCard)

        root.addView(label("Your phone and laptop need to be on the same Wi-Fi. On the laptop, open Phone Mirror. " +
            "This phone shows up under My phones.", 13f, muted).apply { setPadding(dp(4), dp(18), dp(4), 0) })

        // lock screen
        lockView = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setBackgroundColor(bg)
            setPadding(dp(40), 0, dp(40), dp(80))
            isClickable = true
            visibility = View.GONE
        }
        lockView.addView(ImageView(this).apply {
            setImageResource(R.mipmap.ic_launcher)
            layoutParams = LinearLayout.LayoutParams(dp(64), dp(64)).apply { bottomMargin = dp(16) }
        })
        lockView.addView(label("Phone Mirror is locked", 20f, fg, bold = true).apply { gravity = Gravity.CENTER })
        lockView.addView(label("Enter your PIN", 14f, muted).apply { gravity = Gravity.CENTER; setPadding(0, dp(4), 0, dp(16)) })
        lockInput = pinField("").apply {
            gravity = Gravity.CENTER
            textSize = 26f
            letterSpacing = 0.3f
            setTextColor(fg)
            background = rounded(card, 14, line)
            setPadding(dp(12), dp(12), dp(12), dp(12))
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT)
            imeOptions = android.view.inputmethod.EditorInfo.IME_ACTION_DONE
            setOnEditorActionListener { _, _, _ -> tryUnlock(); true }
        }
        lockView.addView(lockInput)
        lockErr = label("", 13f, Color.rgb(252, 165, 165)).apply { gravity = Gravity.CENTER; setPadding(0, dp(10), 0, dp(6)) }
        lockView.addView(lockErr)
        lockView.addView(Button(this).apply {
            text = "Unlock"; isAllCaps = false; textSize = 16f; setTextColor(Color.WHITE)
            background = gradient()
            layoutParams = LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(52))
            setOnClickListener { tryUnlock() }
        })

        setContentView(FrameLayout(this).apply {
            setBackgroundColor(bg)
            addView(ScrollView(this@MainActivity).apply {
                setBackgroundColor(bg)
                addView(root)
            })
            addView(lockView, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
        })
    }
}
