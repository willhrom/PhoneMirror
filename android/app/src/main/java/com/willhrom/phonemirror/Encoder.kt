package com.willhrom.phonemirror

import android.media.MediaCodec
import android.media.MediaCodecInfo
import android.media.MediaFormat
import android.os.Build
import android.os.Bundle
import android.view.Surface

/** One encoded piece of video (or a text message), queued for the laptop. */
class Packet(val flags: Int, val pts: Long, val data: ByteArray?, val text: String? = null) {
    companion object {
        const val KEY = 1
        const val CONFIG = 2
        const val AUDIO = 4
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

    fun start(): Surface {
        val c = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_VIDEO_AVC)
        try {
            c.configure(format(tuned = true), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        } catch (e: Exception) {
            // Some encoders reject the extra tuning; fall back to the basics.
            c.reset()
            c.configure(format(tuned = false), null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
        }
        val s = c.createInputSurface()
        c.start()
        codec = c
        surface = s
        running = true
        Thread({ drain(c) }, "pm-encoder").start()
        return s
    }

    private fun format(tuned: Boolean): MediaFormat =
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
            // codec stopped underneath us: normal when stopping
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
