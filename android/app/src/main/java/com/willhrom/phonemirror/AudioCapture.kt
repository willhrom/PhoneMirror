package com.willhrom.phonemirror

import android.annotation.SuppressLint
import android.annotation.TargetApi
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.MediaCodec
import android.media.MediaFormat
import android.media.projection.MediaProjection
import android.os.Build

/**
 * Captures what the phone is playing (Android 10+), 48 kHz stereo, and sends it as Opus
 * (about 128 kbps, the format video calls use). Falls back to raw PCM if the phone has no Opus encoder.
 * Apps that opt out of capture (some banking/DRM apps) simply stay silent.
 */
@TargetApi(29)
class AudioCapture(private val projection: MediaProjection, private val sink: (Packet) -> Unit) {
    private var record: AudioRecord? = null
    private var opus: MediaCodec? = null
    @Volatile private var running = false

    @SuppressLint("MissingPermission")
    fun start(): Boolean {
        return try {
            val config = AudioPlaybackCaptureConfiguration.Builder(projection)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .build()
            val format = AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(RATE)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                .build()
            val minBuf = AudioRecord.getMinBufferSize(RATE, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT)
            val r = AudioRecord.Builder()
                .setAudioFormat(format)
                .setBufferSizeInBytes(maxOf(minBuf, CHUNK * 4))
                .setAudioPlaybackCaptureConfig(config)
                .build()
            if (r.state != AudioRecord.STATE_INITIALIZED) { r.release(); return false }
            opus = createOpus()
            r.startRecording()
            record = r
            running = true
            Thread({ loop(r) }, "pm-audio").start()
            true
        } catch (_: Exception) {
            false
        }
    }

    private fun createOpus(): MediaCodec? = try {
        val f = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, RATE, 2).apply {
            setInteger(MediaFormat.KEY_BIT_RATE, 128_000)
        }
        MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS).apply {
            configure(f, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE)
            start()
        }
    } catch (_: Exception) { null }

    private fun loop(r: AudioRecord) {
        val start = System.nanoTime()
        val info = MediaCodec.BufferInfo()
        try {
            while (running) {
                val buf = ByteArray(CHUNK)
                var off = 0
                while (off < CHUNK && running) {
                    val n = r.read(buf, off, CHUNK - off)
                    if (n <= 0) break
                    off += n
                }
                if (off <= 0 || !running) continue
                val pts = (System.nanoTime() - start) / 1000
                val enc = opus
                if (enc == null) {
                    sink(Packet(Packet.AUDIO, pts, if (off == CHUNK) buf else buf.copyOf(off)))
                    continue
                }
                // Opus: feed 20 ms of PCM, collect whatever packets are ready.
                val inIdx = enc.dequeueInputBuffer(10_000)
                if (inIdx >= 0) {
                    val inBuf = enc.getInputBuffer(inIdx)!!
                    inBuf.clear()
                    val len = minOf(off, inBuf.remaining())
                    inBuf.put(buf, 0, len)
                    enc.queueInputBuffer(inIdx, 0, len, pts, 0)
                }
                while (true) {
                    val outIdx = enc.dequeueOutputBuffer(info, 0)
                    if (outIdx < 0) break
                    if (info.size > 0 && info.flags and MediaCodec.BUFFER_FLAG_CODEC_CONFIG == 0) {
                        val out = enc.getOutputBuffer(outIdx)!!
                        val data = ByteArray(info.size)
                        out.position(info.offset)
                        out.get(data)
                        sink(Packet(Packet.OPUS, info.presentationTimeUs, data))
                    }
                    enc.releaseOutputBuffer(outIdx, false)
                }
            }
        } catch (_: Exception) { }
    }

    fun stop() {
        running = false
        val r = record
        record = null
        try { r?.stop() } catch (_: Exception) { }
        try { r?.release() } catch (_: Exception) { }
        val o = opus
        opus = null
        try { o?.stop() } catch (_: Exception) { }
        try { o?.release() } catch (_: Exception) { }
    }

    companion object {
        const val RATE = 48000
        const val CHUNK = RATE / 50 * 2 * 2   // 20 ms of stereo 16-bit
        fun supported() = Build.VERSION.SDK_INT >= 29
    }
}
