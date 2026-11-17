package com.willhrom.phonemirror

import android.annotation.SuppressLint
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.projection.MediaProjection
import android.os.Build
import android.annotation.TargetApi

/**
 * Captures what the phone is playing (Android 10+) as 48 kHz 16-bit stereo PCM in 20 ms chunks.
 * Apps that opt out of capture (some banking/DRM apps) simply stay silent.
 */
@TargetApi(29)
class AudioCapture(private val projection: MediaProjection, private val sink: (Packet) -> Unit) {
    private var record: AudioRecord? = null
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
            r.startRecording()
            record = r
            running = true
            Thread({ loop(r) }, "pm-audio").start()
            true
        } catch (_: Exception) {
            false
        }
    }

    private fun loop(r: AudioRecord) {
        val start = System.nanoTime()
        try {
            while (running) {
                val buf = ByteArray(CHUNK)
                var off = 0
                while (off < CHUNK && running) {
                    val n = r.read(buf, off, CHUNK - off)
                    if (n <= 0) break
                    off += n
                }
                if (off > 0 && running) {
                    val pts = (System.nanoTime() - start) / 1000
                    sink(Packet(Packet.AUDIO, pts, if (off == CHUNK) buf else buf.copyOf(off)))
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
    }

    companion object {
        const val RATE = 48000
        const val CHUNK = RATE / 50 * 2 * 2   // 20 ms of stereo 16-bit
        fun supported() = Build.VERSION.SDK_INT >= 29
    }
}
