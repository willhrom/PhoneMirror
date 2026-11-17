package com.willhrom.phonemirror

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Intent
import android.graphics.Path
import android.graphics.PointF
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.DisplayMetrics
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import org.json.JSONObject
import kotlin.math.abs

/**
 * Turns the laptop's mouse and keyboard into touches and typing on the phone.
 * Only active while the user has switched it on in Settings › Accessibility.
 */
class ControlService : AccessibilityService() {

    companion object {
        @Volatile var instance: ControlService? = null
    }

    private val main = Handler(Looper.getMainLooper())

    override fun onServiceConnected() {
        instance = this
        MirrorService.instance?.controlChanged()
        MirrorService.listener?.invoke()
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}

    override fun onUnbind(intent: Intent?): Boolean {
        instance = null
        MirrorService.instance?.controlChanged()
        MirrorService.listener?.invoke()
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        instance = null
        super.onDestroy()
    }

    fun handle(m: JSONObject) {
        main.post {
            try {
                when (m.optString("t")) {
                    "down" -> down(point(m))
                    "move" -> move(point(m))
                    "up" -> up()
                    "scroll" -> scroll(point(m), m.optDouble("dy", 0.0).toFloat())
                    "key" -> key(m.optString("k"))
                    "text" -> type(m.optString("s"))
                }
            } catch (_: Exception) { }
        }
    }

    // ---------------- geometry ----------------

    private fun screen(): Pair<Float, Float> {
        val dm = DisplayMetrics()
        @Suppress("DEPRECATION")
        (getSystemService(WINDOW_SERVICE) as WindowManager).defaultDisplay.getRealMetrics(dm)
        return Pair(dm.widthPixels.toFloat(), dm.heightPixels.toFloat())
    }

    private fun point(m: JSONObject): PointF {
        val (w, h) = screen()
        val x = (m.optDouble("x", 0.5).toFloat() * w).coerceIn(0f, w - 1f)
        val y = (m.optDouble("y", 0.5).toFloat() * h).coerceIn(0f, h - 1f)
        return PointF(x, y)
    }

    // ---------------- touches (streamed as one continuous finger) ----------------

    private var stroke: GestureDescription.StrokeDescription? = null
    private var last = PointF()
    private var busy = false
    private var pending: PointF? = null
    private var pendingUp = false

    private fun down(p: PointF) {
        stroke = null
        busy = false
        pending = null
        pendingUp = false
        val path = Path().apply { moveTo(p.x, p.y) }
        val s = GestureDescription.StrokeDescription(path, 0, 1, true)
        stroke = s
        last = p
        dispatch(s)
    }

    private fun move(p: PointF) {
        pending = p
        pump()
    }

    private fun up() {
        pendingUp = true
        pump()
    }

    /** Sends the next piece of the finger's path once the previous one has been played. */
    private fun pump() {
        val s = stroke ?: return
        if (busy) return
        val target = pending
        if (target == null && !pendingUp) return
        val to = target ?: last
        val endX = if (to.x == last.x && to.y == last.y) (to.x + 0.5f) else to.x
        val path = Path().apply {
            moveTo(last.x, last.y)
            lineTo(endX, to.y)
        }
        val keepGoing = !pendingUp
        val next = s.continueStroke(path, 0, 16, keepGoing)
        pending = null
        last = PointF(endX, to.y)
        if (keepGoing) stroke = next else { stroke = null; pendingUp = false }
        dispatch(next)
    }

    private fun dispatch(s: GestureDescription.StrokeDescription) {
        busy = true
        val ok = dispatchGesture(GestureDescription.Builder().addStroke(s).build(), object : GestureResultCallback() {
            override fun onCompleted(gestureDescription: GestureDescription?) {
                busy = false
                pump()
            }

            override fun onCancelled(gestureDescription: GestureDescription?) {
                busy = false
                stroke = null
                pending = null
                pendingUp = false
            }
        }, main)
        if (!ok) {
            busy = false
            stroke = null
        }
    }

    /** Mouse wheel: a short, gentle swipe. */
    private fun scroll(p: PointF, dy: Float) {
        if (stroke != null) return
        val (_, h) = screen()
        val distance = (-dy * h * 0.0009f).coerceIn(-h * 0.45f, h * 0.45f)
        val endY = (p.y + distance).coerceIn(1f, h - 2f)
        if (abs(endY - p.y) < 4f) return
        val path = Path().apply {
            moveTo(p.x, p.y)
            lineTo(p.x, endY)
        }
        dispatchGesture(GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, 220)).build(), null, null)
    }

    // ---------------- keys and typing ----------------

    private fun key(k: String) {
        when (k) {
            "back" -> performGlobalAction(GLOBAL_ACTION_BACK)
            "home" -> performGlobalAction(GLOBAL_ACTION_HOME)
            "recents" -> performGlobalAction(GLOBAL_ACTION_RECENTS)
            "notifications" -> performGlobalAction(GLOBAL_ACTION_NOTIFICATIONS)
            "power" -> performGlobalAction(if (Build.VERSION.SDK_INT >= 28) GLOBAL_ACTION_LOCK_SCREEN else GLOBAL_ACTION_POWER_DIALOG)
            "enter" -> enter()
            "del" -> backspace()
        }
    }

    private fun focused(): AccessibilityNodeInfo? =
        rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)

    private fun currentText(n: AccessibilityNodeInfo): String {
        if (Build.VERSION.SDK_INT >= 26 && n.isShowingHintText) return ""
        return n.text?.toString() ?: ""
    }

    private fun setText(n: AccessibilityNodeInfo, t: String) {
        val b = Bundle()
        b.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, t)
        n.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, b)
    }

    private fun type(s: String) {
        if (s.isEmpty()) return
        val n = focused() ?: return
        if (!n.isEditable) return
        setText(n, currentText(n) + s)
    }

    private fun backspace() {
        val n = focused() ?: return
        val t = currentText(n)
        if (t.isNotEmpty()) setText(n, t.dropLast(1))
    }

    private fun enter() {
        val n = focused() ?: return
        if (Build.VERSION.SDK_INT >= 30) {
            n.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
        } else {
            setText(n, currentText(n) + "\n")
        }
    }
}
