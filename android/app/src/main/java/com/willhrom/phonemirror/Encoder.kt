package com.willhrom.phonemirror

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.view.Surface

/** One encoded piece of video (or a text message), queued for the laptop. */
class Packet(val flags: Int, val pts: Long, val data: ByteArray?, val text: String? = null) {
    /** When it was queued, to tell how far behind the Wi-Fi is. */
    val queuedAt: Long = System.nanoTime()
    companion object {
        const val KEY = 1
        const val CONFIG = 2
        const val AUDIO = 4      // raw PCM
        const val OPUS = 8       // Opus-compressed audio
    }
}

/** Hardware H.264 encoder fed by a Surface; hands encoded frames to [sink]. */
class Encoder(
    private val width: Int,
    private val height: Int,
    private val bitrate: Int,
    private val fps: Int,
    private val sink: (Packet) -> Unit,
) {
    private var codec: MediaCodec? = null
    private var surface: Surface? = null
    @Volatile private var running = false

    /** When the encoder last produced anything (SystemClock.elapsedRealtime). */
    @Volatile var lastOutputAt = 0L
        private set
    /** The encoder failed on its own while it should have been running. */
    @Volatile var died = false
        private set

    fun start(): Surface {
        val c = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        // Best first; some encoders reject parts of the tuning, so step down until one is accepted.
        var ok = false
        // (Gradual refresh is off: on some phones it made the encoder overshoot its bit rate and lag.)
        for ((tuned, refresh) in listOf(true to false, false to false)) {
            try {
                c.configure(format(tuned, refresh), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
                ok = true; break
            } catch (_: Exception) { c.reset() }
        }
        if (!ok) c.configure(format(false, false), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        val s = c.createInputSurface()
        c.start()
        codec = c
        surface = s
        running = true
        lastOutputAt = android.os.SystemClock.elapsedRealtime()
        Thread({ drain(c) }, "pm-encoder").start()
        return s
    }

    private fun format(tuned: Boolean, refresh: Boolean): MediaFormat =
        MediaFormat.createVideoFormat(MediaFormat.MIMETYPE_VIDEO_AVC, width, height).apply {
            setInteger(MediaFormat.KEY_COLOR_FORMAT, MediaCodecInfo.CodecCapabilities.COLOR_FormatSurface)
            setInteger(MediaFormat.KEY_BIT_RATE, bitrate)
            setInteger(MediaFormat.KEY_FRAME_RATE, fps)
            setInteger(MediaFormat.KEY_I_FRAME_INTERVAL, 30)   // big full pictures cause hitches: rarely, or on request
            // Keep sending frames when the screen is still, so the picture never goes stale.
            setLong(MediaFormat.KEY_REPEAT_PREVIOUS_FRAME_AFTER, 100_000L)
            // No B-frames: the encoder must not hold frames back to reorder them (that adds lag).
            if (Build.VERSION.SDK_INT >= 29) setInteger(MediaFormat.KEY_MAX_B_FRAMES, 0)
            if (tuned) {
                setInteger(MediaFormat.KEY_PRIORITY, 0) // real-time
                // Baseline: frames never need reordering, so the laptop can show each one immediately.
                setInteger(MediaFormat.KEY_PROFILE, MediaCodecInfo.CodecProfileLevel.AVCProfileBaseline)
                setInteger(MediaFormat.KEY_LEVEL, MediaCodecInfo.CodecProfileLevel.AVCLevel42)
                if (Build.VERSION.SDK_INT >= 29) setFloat("max-fps-to-encoder", fps.toFloat())
                if (Build.VERSION.SDK_INT >= 30) setInteger(MediaFormat.KEY_LATENCY, 1)
                // Refresh the picture a slice at a time over about a second, instead of sending a whole
                // new picture at once (those big frames are a common cause of lag spikes).
                if (refresh) setInteger(MediaFormat.KEY_INTRA_REFRESH_PERIOD, fps)
            }
        }

    /** Changes the video bit rate on the fly (adaptive quality). */
    fun setBitrate(bps: Int) {
        try {
            codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_VIDEO_BITRATE, bps) })
        } catch (_: Exception) { }
    }

    fun requestKeyFrame() {
        try {
            codec?.setParameters(Bundle().apply { putInt(MediaCodec.PARAMETER_KEY_REQUEST_SYNC_FRAME, 0) })
        } catch (_: Exception) { }
    }

    private fun drain(c: MediaCodec) {
        val info = MediaCodec.BufferInfo()
        try {
            while (running) {
                val idx = c.dequeueOutputBuffer(info, 20_000)
                if (idx < 0) continue
                lastOutputAt = android.os.SystemClock.elapsedRealtime()
                val buf = c.getOutputBuffer(idx)
                if (buf != null && info.size > 0) {
                    val data = ByteArray(info.size)
                    buf.position(info.offset)
                    buf.limit(info.offset + info.size)
                    buf.get(data)
                    val flags = when {
                        info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG != 0 -> Packet.CONFIG
                        info.flags and MediaCodec.BUFFER_FLAG_KEY_FRAME != 0 -> Packet.KEY
                        else -> 0
                    }
                    sink(Packet(flags, info.presentationTimeUs, data))
                }
                c.releaseOutputBuffer(idx, false)
                if (info.flags and MediaCodec.BUFFER_FLAG_END_OF_STREAM != 0) break
            }
        } catch (_: Exception) {
            // Normal when stopping; otherwise the encoder broke and needs a restart.
            if (running) died = true
        }
    }

    fun stop() {
        running = false
        val c = codec
        codec = null
        try { c?.stop() } catch (_: Exception) { }
        try { c?.release() } catch (_: Exception) { }
        try { surface?.release() } catch (_: Exception) { }
        surface = null
    }
}
