package com.willhrom.phonemirror

import android.annotation.TargetApi
import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.net.wifi.WifiNetworkSpecifier
import android.os.Build

/**
 * Joins a laptop's private Wi-Fi Direct link (Android 10+), for places with no shared Wi-Fi.
 * Android asks the user to approve the first time; after that it connects without asking.
 */
object DirectLink {
    @Volatile var state: String? = null
        private set
    @Volatile var connected = false
        private set
    @Volatile var laptopName: String? = null
        private set
    private var callback: ConnectivityManager.NetworkCallback? = null

    fun supported() = Build.VERSION.SDK_INT >= 29
    val active get() = callback != null

    @TargetApi(29)
    fun connect(c: Context, laptop: Prefs.Laptop, onChange: () -> Unit) {
        disconnect(c)
        val cm = c.getSystemService(ConnectivityManager::class.java)
        val spec = WifiNetworkSpecifier.Builder()
            .setSsid(laptop.ssid)
            .setWpa2Passphrase(laptop.pass)
            .build()
        val request = NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .removeCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
            .setNetworkSpecifier(spec)
            .build()
        laptopName = laptop.name
        state = "Looking for ${laptop.name}…"
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                connected = true
                state = "Linked to ${laptop.name}. It will show up there in a few seconds."
                onChange()
            }

            override fun onUnavailable() {
                connected = false
                callback = null
                state = "Couldn't find ${laptop.name}. Make sure Phone Mirror is open on the laptop and try again."
                onChange()
            }

            override fun onLost(network: Network) {
                connected = false
                state = "The direct link to ${laptop.name} ended."
                onChange()
            }
        }
        callback = cb
        try {
            cm.requestNetwork(request, cb, 60_000)
        } catch (e: Exception) {
            callback = null
            state = "Couldn't start a direct link: ${e.message}"
        }
        onChange()
    }

    fun disconnect(c: Context) {
        val cb = callback ?: return
        callback = null
        connected = false
        state = null
        try { c.getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(cb) } catch (_: Exception) { }
    }
}
