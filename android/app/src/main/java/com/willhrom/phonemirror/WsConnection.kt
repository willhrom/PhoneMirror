package com.willhrom.phonemirror

import android.util.Base64
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.IOException
import java.net.Socket
import java.security.MessageDigest

/**
 * A minimal WebSocket server connection (RFC 6455), enough for one laptop:
 * text messages in, binary video and text out. Plain HTTP GET requests are
 * answered with a small JSON "who am I" so laptops can find the phone.
 */
class WsConnection(private val socket: Socket) {
    private val input = BufferedInputStream(socket.getInputStream(), 16 * 1024)
    private val output = BufferedOutputStream(socket.getOutputStream(), 64 * 1024)

    /** Returns true if this became a WebSocket; false if it was a plain info request (already answered). */
    fun handshake(info: () -> String): Boolean {
        readLine() ?: return false
        val headers = HashMap<String, String>()
        while (true) {
            val line = readLine() ?: return false
            if (line.isEmpty()) break
            val i = line.indexOf(':')
            if (i > 0) headers[line.substring(0, i).trim().lowercase()] = line.substring(i + 1).trim()
        }
        val key = headers["sec-websocket-key"]
        if (key == null) {
            val body = info().toByteArray(Charsets.UTF_8)
            val head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n" +
                "Access-Control-Allow-Origin: *\r\nContent-Length: ${body.size}\r\nConnection: close\r\n\r\n"
            output.write(head.toByteArray(Charsets.US_ASCII))
            output.write(body)
            output.flush()
            return false
        }
        val digest = MessageDigest.getInstance("SHA-1")
            .digest((key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11").toByteArray(Charsets.US_ASCII))
        val accept = Base64.encodeToString(digest, Base64.NO_WRAP)
        val response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
            "Sec-WebSocket-Accept: $accept\r\n\r\n"
        output.write(response.toByteArray(Charsets.US_ASCII))
        output.flush()
        return true
    }

    private fun readLine(): String? {
        val sb = StringBuilder()
        while (true) {
            val b = input.read()
            if (b < 0) return null
            if (b == '\n'.code) return sb.toString().trimEnd('\r')
            sb.append(b.toChar())
            if (sb.length > 8192) return null
        }
    }

    private fun readByte(): Int {
        val b = input.read()
        if (b < 0) throw IOException("closed")
        return b
    }

    private fun readFully(buf: ByteArray) {
        var off = 0
        while (off < buf.size) {
            val n = input.read(buf, off, buf.size - off)
            if (n < 0) throw IOException("closed")
            off += n
        }
    }

    /** Waits for the next text message. Returns null when the connection closes. */
    fun readText(): String? {
        while (true) {
            val b0 = input.read()
            if (b0 < 0) return null
            val b1 = readByte()
            val opcode = b0 and 0x0F
            val masked = (b1 and 0x80) != 0
            var len = (b1 and 0x7F).toLong()
            if (len == 126L) {
                len = ((readByte() shl 8) or readByte()).toLong()
            } else if (len == 127L) {
                len = 0
                repeat(8) { len = (len shl 8) or readByte().toLong() }
            }
            if (len > 4_000_000) return null
            val mask = if (masked) ByteArray(4) { readByte().toByte() } else null
            val data = ByteArray(len.toInt())
            readFully(data)
            if (mask != null) for (i in data.indices) data[i] = (data[i].toInt() xor mask[i % 4].toInt()).toByte()
            when (opcode) {
                1 -> return String(data, Charsets.UTF_8)
                8 -> return null
                9 -> send(0xA, data)
                else -> { /* binary / pong / continuation: not used */ }
            }
        }
    }

    @Synchronized
    fun send(opcode: Int, data: ByteArray, prefix: ByteArray? = null) {
        val total = data.size + (prefix?.size ?: 0)
        output.write(0x80 or opcode)
        when {
            total < 126 -> output.write(total)
            total < 65536 -> {
                output.write(126)
                output.write(total shr 8)
                output.write(total and 0xFF)
            }
            else -> {
                output.write(127)
                for (i in 7 downTo 0) output.write(((total.toLong() shr (8 * i)) and 0xFF).toInt())
            }
        }
        if (prefix != null) output.write(prefix)
        output.write(data)
        output.flush()
    }

    fun sendText(s: String) = send(1, s.toByteArray(Charsets.UTF_8))

    fun close() {
        try { socket.close() } catch (_: Exception) { }
    }
}
