package com.willhrom.phonemirror

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Intent
import android.graphics.Path
import android.graphics.PointF
import android.graphics.Rect
import android.view.inputmethod.EditorInfo
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.util.DisplayMetrics
import android.view.KeyEvent
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
                    // Newer laptops decide taps themselves: "tap" is a click, "down" with now=true is a real hold/drag.
                    "tap" -> { main.removeCallbacks(startHeld); downAt = null; tap(point(m)) }
                    "down" -> if (m.optBoolean("now")) { main.removeCallbacks(startHeld); downAt = null; beginStroke(point(m)) } else down(point(m))
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
    private var lastSent = 0L

    private var downAt: PointF? = null
    private val startHeld = Runnable { downAt?.let { downAt = null; beginStroke(it) } }

    /** A press: wait briefly to see if it's a quick tap (sent as one clean tap) or a hold/drag. */
    private fun down(p: PointF) {
        main.removeCallbacks(startHeld)
        downAt = p
        main.postDelayed(startHeld, 110)
    }

    private var pendingTap: PointF? = null

    private fun tap(p: PointF) {
        // A finger is still lifting from the last swipe: tap right after it, instead of cancelling it.
        if (busy) { pendingTap = p; return }
        val path = Path().apply { moveTo(p.x, p.y) }
        dispatchGesture(GestureDescription.Builder()
            .addStroke(GestureDescription.StrokeDescription(path, 0, 60)).build(), null, null)
    }

    private var gen = 0
    private var deferredStart: PointF? = null

    private fun beginStroke(p: PointF) {
        pending = null
        pendingUp = false
        if (busy && stroke == null) {
            // The last finger is still lifting: put this one down as soon as it's up (moves are kept meanwhile).
            deferredStart = p
            return
        }
        startStroke(p)
    }

    private fun startStroke(p: PointF) {
        gen++
        stroke = null
        busy = false
        val path = Path().apply { moveTo(p.x, p.y) }
        val s = GestureDescription.StrokeDescription(path, 0, 1, true)
        stroke = s
        last = p
        lastSent = android.os.SystemClock.uptimeMillis()
        dispatch(s)
    }

    /** Called whenever the phone finished playing a piece and no finger is down any more. */
    private fun afterLift() {
        deferredStart?.let { deferredStart = null; startStroke(it); return }
        pendingTap?.let { pendingTap = null; tap(it) }
    }

    private fun move(p: PointF) {
        val d = downAt
        if (d != null) {
            // Moved before the tap window ended: it's a drag/scroll, start the finger now.
            if (abs(p.x - d.x) + abs(p.y - d.y) < 12f) return
            main.removeCallbacks(startHeld)
            downAt = null
            beginStroke(d)
        }
        pending = p
        pump()
    }

    private fun up() {
        val d = downAt
        if (d != null) {
            main.removeCallbacks(startHeld)
            downAt = null
            tap(d)
            return
        }
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
        // Play each piece over the real time it took on the laptop, so flings keep their speed.
        val now = android.os.SystemClock.uptimeMillis()
        val duration = (now - lastSent).coerceIn(8L, 40L)
        lastSent = now
        val next = s.continueStroke(path, 0, duration, keepGoing)
        pending = null
        last = PointF(endX, to.y)
        if (keepGoing) stroke = next else { stroke = null; pendingUp = false }
        dispatch(next)
    }

    private fun dispatch(s: GestureDescription.StrokeDescription) {
        busy = true
        val g = gen
        val ok = dispatchGesture(GestureDescription.Builder().addStroke(s).build(), object : GestureResultCallback() {
            override fun onCompleted(gestureDescription: GestureDescription?) {
                if (g != gen) return          // an older finger: ignore, it must not touch the new one
                busy = false
                if (stroke == null) afterLift() else pump()
            }

            override fun onCancelled(gestureDescription: GestureDescription?) {
                if (g != gen) return
                busy = false
                stroke = null
                pending = null
                pendingUp = false
                afterLift()
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
            .addStroke(GestureDescription.StrokeDescription(path, 0, 140)).build(), null, null)
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
            "newline" -> newline()
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

    /** Where the cursor (or selection) is in the field; end of text if the app doesn't say. */
    private fun selection(n: AccessibilityNodeInfo, len: Int): Pair<Int, Int> {
        var a = n.textSelectionStart
        var b = n.textSelectionEnd
        if (a < 0 || b < 0 || a > len || b > len) { a = len; b = len }
        return Pair(minOf(a, b), maxOf(a, b))
    }

    private fun placeCursor(n: AccessibilityNodeInfo, pos: Int) {
        val b = Bundle()
        b.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_START_INT, pos)
        b.putInt(AccessibilityNodeInfo.ACTION_ARGUMENT_SELECTION_END_INT, pos)
        n.performAction(AccessibilityNodeInfo.ACTION_SET_SELECTION, b)
    }

    /** Android 13+: a real keyboard connection to the focused text field (proper Enter, Backspace, typing). */
    private fun imeConnection(): android.accessibilityservice.InputMethod.AccessibilityInputConnection? {
        if (Build.VERSION.SDK_INT < 33) return null
        return try { inputMethod?.currentInputConnection } catch (_: Exception) { null }
    }

    private fun sendKey(code: Int): Boolean {
        val ic = imeConnection() ?: return false
        ic.sendKeyEvent(KeyEvent(KeyEvent.ACTION_DOWN, code))
        ic.sendKeyEvent(KeyEvent(KeyEvent.ACTION_UP, code))
        return true
    }

    /** Types at the cursor, replacing any selected text. */
    private fun type(s: String) {
        if (s.isEmpty()) return
        if (Build.VERSION.SDK_INT >= 33) {
            val ic = imeConnection()
            if (ic != null) { ic.commitText(s, 1, null); return }
        }
        val n = focused() ?: return
        if (!n.isEditable) return
        val t = currentText(n)
        val (a, b) = selection(n, t.length)
        setText(n, t.substring(0, a) + s + t.substring(b))
        placeCursor(n, a + s.length)
    }

    /** Deletes the selection, or the character before the cursor. */
    private fun backspace() {
        if (sendKey(KeyEvent.KEYCODE_DEL)) return
        val n = focused() ?: return
        val t = currentText(n)
        val (a, b) = selection(n, t.length)
        if (a == b && a == 0) return
        val from = if (a == b) a - 1 else a
        setText(n, t.substring(0, from) + t.substring(b))
        placeCursor(n, from)
    }

    /**
     * Enter works like on a laptop chat app: it sends. Fields with a keyboard action (send, search, go)
     * get that action; chat boxes get their Send button pressed. Otherwise it's a normal Enter.
     */
    private fun enter() {
        if (Build.VERSION.SDK_INT >= 33) {
            val info = try { inputMethod?.currentInputEditorInfo } catch (_: Exception) { null }
            val ic = imeConnection()
            if (info != null && ic != null) {
                val action = info.imeOptions and EditorInfo.IME_MASK_ACTION
                val noAction = info.imeOptions and EditorInfo.IME_FLAG_NO_ENTER_ACTION != 0
                if (!noAction && action in listOf(EditorInfo.IME_ACTION_SEND, EditorInfo.IME_ACTION_GO,
                        EditorInfo.IME_ACTION_SEARCH, EditorInfo.IME_ACTION_DONE, EditorInfo.IME_ACTION_NEXT)) {
                    ic.performEditorAction(action); return
                }
            }
        }
        val n = focused()
        if (n != null && n.isMultiLine && pressSend(n)) return
        newline()
    }

    /** Finds the chat's Send button next to the text box and presses it. */
    private fun pressSend(field: AccessibilityNodeInfo): Boolean {
        val root = rootInActiveWindow ?: return false
        val fb = Rect().also { field.getBoundsInScreen(it) }
        var best: AccessibilityNodeInfo? = null
        var bestDist = Float.MAX_VALUE
        val todo = ArrayDeque<AccessibilityNodeInfo>().apply { add(root) }
        var seen = 0
        while (todo.isNotEmpty() && seen < 3000) {
            val n = todo.removeFirst(); seen++
            for (i in 0 until n.childCount) n.getChild(i)?.let { todo.add(it) }
            if (n.isEditable || !n.isVisibleToUser || !n.isEnabled) continue
            val label = listOfNotNull(n.contentDescription?.toString(), n.text?.toString(), n.viewIdResourceName?.substringAfterLast('/'))
                .joinToString(" ").lowercase()
            if (!Regex("(^|[^a-z])send([^a-z]|$)|send_?button|sendbutton|send_?message|send_?icon").containsMatchIn(label)) continue
            var c: AccessibilityNodeInfo? = n
            while (c != null && !c.isClickable) c = c.parent
            if (c == null) continue
            val b = Rect().also { c.getBoundsInScreen(it) }
            val d = abs(b.centerX() - fb.centerX()).toFloat() + abs(b.centerY() - fb.centerY()) * 2f
            if (d < bestDist) { bestDist = d; best = c }
        }
        return best?.performAction(AccessibilityNodeInfo.ACTION_CLICK) == true
    }

    /** Shift+Enter: a new line. */
    private fun newline() {
        if (sendKey(KeyEvent.KEYCODE_ENTER)) return
        val n = focused() ?: return
        val done = Build.VERSION.SDK_INT >= 30 &&
            n.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
        if (!done && n.isMultiLine) type("\n")
    }
}
