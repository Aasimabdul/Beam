package com.beam.app

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.drawable.Icon
import android.net.ConnectivityManager
import android.net.Network
import android.net.wifi.WifiManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import java.io.IOException

/** Keeps the DLNA receiver alive: HTTP server, SSDP announcements, multicast lock. Only runs while the user has it switched on. */
class ReceiverService : Service() {
    companion object {
        private const val CHANNEL = "beam"
        private const val NOTIF_ID = 1
        private const val ACTION_STOP = "com.beam.app.STOP"

        @Volatile var running = false
            private set
        @Volatile var address: String? = null
            private set

        fun start(c: Context) {
            c.startForegroundService(Intent(c, ReceiverService::class.java))
        }

        fun stop(c: Context) {
            c.stopService(Intent(c, ReceiverService::class.java))
        }
    }

    private val main = Handler(Looper.getMainLooper())
    private var http: MiniHttp? = null
    private var ssdp: SsdpServer? = null
    private var device: UpnpDevice? = null
    private var multicast: WifiManager.MulticastLock? = null
    private var netCallback: ConnectivityManager.NetworkCallback? = null
    private var lastRestart = 0L
    private var starting = false

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (!running && !starting) boot()
        return START_NOT_STICKY
    }

    private fun boot() {
        starting = true
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Receiver", NotificationManager.IMPORTANCE_LOW))
        startForeground(NOTIF_ID, notification("Starting…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PLAYBACK)

        Playback.init(this)
        Playback.launcher = {
            startActivity(
                Intent(this, PlayerActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_REORDER_TO_FRONT or Intent.FLAG_ACTIVITY_SINGLE_TOP)
            )
        }
        Playback.onInfo = { text -> postStatus(text) }

        // sockets must not be opened on the main thread
        Thread(null, {
            try {
                val uuid = Prefs.uuid(this)
                val name = Prefs.name(this)
                val dev = UpnpDevice(uuid, name, Playback)
                Playback.device = dev
                val server = MiniHttp(dev)
                val port = try {
                    server.start(Prefs.port(this))
                } catch (e: IOException) {
                    server.start(0)
                }
                Prefs.setPort(this, port)
                val wifi = applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
                multicast = wifi.createMulticastLock("beam").apply {
                    setReferenceCounted(false)
                    acquire()
                }
                val s = SsdpServer(uuid, port)
                s.start()
                device = dev
                http = server
                ssdp = s
                address = (Net.localIpFor(null) ?: "?") + ":" + port
                running = true
                watchNetwork()
                main.post { postStatus("Ready · $name") }
            } catch (e: Exception) {
                main.post {
                    postStatus("Couldn't start: ${e.message ?: "network error"}")
                    stopSelf()
                }
            } finally {
                starting = false
            }
        }, "beam-boot", 256 * 1024).start()
    }

    /** Wi-Fi changes (new network, hotspot on/off): announce again on the new interfaces. */
    private fun watchNetwork() {
        val cm = getSystemService(ConnectivityManager::class.java)
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) {
                val now = System.currentTimeMillis()
                if (now - lastRestart < 1500) return
                lastRestart = now
                try {
                    ssdp?.restart()
                    address = (Net.localIpFor(null) ?: "?") + ":" + Prefs.port(this@ReceiverService)
                } catch (e: IOException) {
                    // try again on the next change
                }
            }
        }
        lastRestart = System.currentTimeMillis()
        try {
            cm.registerDefaultNetworkCallback(cb)
            netCallback = cb
        } catch (e: RuntimeException) {
            // no network monitoring; the receiver still works on the current network
        }
    }

    private fun notification(text: String): Notification {
        val target = if (Playback.uiWanted) PlayerActivity::class.java else MainActivity::class.java
        val open = PendingIntent.getActivity(this, 0, Intent(this, target), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop = PendingIntent.getService(
            this, 1, Intent(this, ReceiverService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE
        )
        val icon = Icon.createWithResource(this, R.drawable.ic_stat)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(icon)
            .setContentTitle("Beam")
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .addAction(Notification.Action.Builder(icon, "Stop", stop).build())
            .build()
    }

    private fun postStatus(text: String) {
        getSystemService(NotificationManager::class.java).notify(NOTIF_ID, notification(text))
    }

    override fun onDestroy() {
        running = false
        address = null
        val cm = getSystemService(ConnectivityManager::class.java)
        netCallback?.let { try { cm.unregisterNetworkCallback(it) } catch (e: RuntimeException) { } }
        netCallback = null
        Playback.shutdown()
        val s = ssdp
        val h = http
        val d = device
        val m = multicast
        ssdp = null
        http = null
        device = null
        multicast = null
        // sending the "byebye" announcement is network I/O, so not on the main thread
        Thread(null, {
            try { s?.shutdown() } catch (e: Exception) { }
            h?.stop()
            d?.shutdown()
            try { if (m != null && m.isHeld) m.release() } catch (e: Exception) { }
        }, "beam-stop", 128 * 1024).start()
        super.onDestroy()
    }
}
