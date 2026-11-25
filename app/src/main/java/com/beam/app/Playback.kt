package com.beam.app

import android.content.Context
import android.graphics.Bitmap
import android.graphics.ImageDecoder
import android.media.AudioAttributes
import android.media.MediaPlayer
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.view.SurfaceHolder
import java.io.ByteArrayOutputStream
import java.io.IOException
import java.net.HttpURLConnection
import java.net.URL
import java.nio.ByteBuffer
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * The playback side of the receiver: one Android MediaPlayer (video/audio) or one decoded picture at a time.
 * UPnP calls arrive on HTTP threads and are handed to the main thread; reads use cached values and never block.
 */
object Playback : UpnpDevice.Player {
    /** What the full-screen player screen listens to. All calls are on the main thread. */
    interface Ui {
        fun onMedia()
        fun onClose()
        fun onVideoSize(w: Int, h: Int)
    }

    private val main = Handler(Looper.getMainLooper())
    private lateinit var app: Context

    var device: UpnpDevice? = null
    var ui: Ui? = null
    /** Opens the player screen (set by the service). */
    var launcher: (() -> Unit)? = null
    /** One-line status for the notification. */
    var onInfo: ((String) -> Unit)? = null

    @Volatile var kind = "video"
        private set
    @Volatile var title = ""
        private set
    @Volatile var image: Bitmap? = null
        private set
    @Volatile var error: String? = null
        private set
    /** True while the player screen should be on screen (video or picture is loaded). */
    @Volatile var uiWanted = false
        private set
    @Volatile var videoW = 0
        private set
    @Volatile var videoH = 0
        private set

    @Volatile private var pos = 0L
    @Volatile private var dur = 0L
    @Volatile private var vol = 100
    @Volatile private var mute = false

    private var holder: SurfaceHolder? = null
    private var mp: MediaPlayer? = null
    private var prepared = false
    private var wantPlay = false
    private var pendingSeek = -1L
    private var uri: String? = null
    private var gen = 0

    private val ticker = object : Runnable {
        override fun run() {
            val m = mp
            if (m != null && prepared) {
                try {
                    pos = m.currentPosition.toLong()
                    if (m.isPlaying) main.postDelayed(this, 500)
                } catch (e: IllegalStateException) {
                    // player was released meanwhile
                }
            }
        }
    }

    fun init(ctx: Context) {
        app = ctx.applicationContext
    }

    fun isPlaying(): Boolean = try { mp?.isPlaying == true } catch (e: IllegalStateException) { false }

    // ------------------------------------------------------------------ UpnpDevice.Player (any thread)

    override fun load(uri: String, meta: String, kind: String, title: String) {
        main.post { doLoad(uri, kind, title) }
    }

    override fun play() {
        main.post {
            wantPlay = true
            if (kind == "image") return@post
            val m = mp
            if (m == null) uri?.let { prepare(it) }
            else if (prepared) startPlayback(m)
        }
    }

    override fun pause() {
        main.post { doPause() }
    }

    override fun stop() {
        main.post { doStop() }
    }

    override fun seek(ms: Long) {
        main.post { doSeek(ms) }
    }

    override fun positionMs(): Long = pos
    override fun durationMs(): Long = dur

    override fun setVolume(percent: Int) {
        vol = percent
        main.post { applyVolume() }
    }

    override fun volume(): Int = vol

    override fun setMute(mute: Boolean) {
        this.mute = mute
        main.post { applyVolume() }
    }

    override fun muted(): Boolean = mute

    // ------------------------------------------------------------------ from the player screen (main thread)

    fun userToggle() {
        if (kind == "image") return
        if (isPlaying()) {
            doPause()
            device?.onPlayerState("PAUSED_PLAYBACK")
        } else {
            wantPlay = true
            val m = mp
            if (m == null) uri?.let { prepare(it) } else if (prepared) startPlayback(m)
            device?.onPlayerState("PLAYING")
        }
    }

    fun userSeekTo(ms: Long) = doSeek(ms)

    fun userStop() {
        doStop()
        device?.onPlayerState("STOPPED")
    }

    fun setSurface(h: SurfaceHolder?) {
        holder = h
        try {
            mp?.setDisplay(h)
        } catch (e: IllegalStateException) {
            // released
        }
    }

    fun shutdown() {
        gen++
        release()
        image = null
        uri = null
        uiWanted = false
        device = null
        ui = null
        launcher = null
        onInfo = null
    }

    // ------------------------------------------------------------------ main-thread work

    private fun info(text: String) {
        onInfo?.invoke(text)
    }

    private fun doLoad(u: String, k: String, t: String) {
        gen++
        release()
        uri = u
        kind = k
        title = if (t.isNotEmpty()) t else Uri.parse(u).lastPathSegment ?: u
        error = null
        image = null
        pos = 0
        dur = 0
        videoW = 0
        videoH = 0
        wantPlay = false
        pendingSeek = -1
        info("Loading: $title")
        uiWanted = k != "audio"
        if (uiWanted) launcher?.invoke()
        if (k == "image") loadImage(u, gen) else prepare(u)
        ui?.onMedia()
    }

    private fun prepare(u: String) {
        val g = gen
        val m = MediaPlayer()
        try {
            m.setWakeMode(app, PowerManager.PARTIAL_WAKE_LOCK)
            m.setAudioAttributes(
                AudioAttributes.Builder()
                    .setContentType(AudioAttributes.CONTENT_TYPE_MOVIE)
                    .setUsage(AudioAttributes.USAGE_MEDIA)
                    .build()
            )
            m.setDataSource(app, Uri.parse(u))
            if (kind == "video") holder?.let { m.setDisplay(it) }
            m.setOnPreparedListener { p ->
                if (g != gen) return@setOnPreparedListener
                prepared = true
                dur = max(0, p.duration).toLong()
                if (kind == "video") {
                    videoW = p.videoWidth
                    videoH = p.videoHeight
                    if (videoW > 0) ui?.onVideoSize(videoW, videoH)
                }
                applyVolume()
                if (pendingSeek >= 0) {
                    p.seekTo(pendingSeek, MediaPlayer.SEEK_CLOSEST)
                    pos = pendingSeek
                    pendingSeek = -1
                }
                device?.onMediaReady()
                if (wantPlay) startPlayback(p)
            }
            m.setOnVideoSizeChangedListener { _, w, h ->
                if (g == gen && w > 0 && h > 0) {
                    videoW = w
                    videoH = h
                    ui?.onVideoSize(w, h)
                }
            }
            m.setOnCompletionListener {
                if (g != gen) return@setOnCompletionListener
                wantPlay = false
                pos = dur
                main.removeCallbacks(ticker)
                device?.onPlayerState("STOPPED")
                info("Ready")
                if (uiWanted) main.postDelayed({ if (g == gen) closeUi() }, 1500)
            }
            m.setOnErrorListener { _, what, _ ->
                if (g == gen) fail("Can't play this (error $what)")
                true
            }
            mp = m
            prepared = false
            m.prepareAsync()
        } catch (e: Exception) {
            try { m.release() } catch (x: Exception) { }
            mp = null
            fail("Can't play this: ${e.message ?: "unknown error"}")
        }
    }

    private fun startPlayback(m: MediaPlayer) {
        try {
            m.start()
        } catch (e: IllegalStateException) {
            fail("Can't start playback")
            return
        }
        main.removeCallbacks(ticker)
        main.post(ticker)
        info("Playing: $title")
    }

    private fun doPause() {
        wantPlay = false
        val m = mp
        if (m != null && prepared) {
            try { if (m.isPlaying) m.pause() } catch (e: IllegalStateException) { }
        }
        main.removeCallbacks(ticker)
        info("Paused: $title")
    }

    private fun doStop() {
        wantPlay = false
        release()
        pos = 0
        pendingSeek = -1
        info("Ready")
        closeUi()
    }

    private fun closeUi() {
        uiWanted = false
        ui?.onClose()
    }

    private fun doSeek(ms: Long) {
        val m = mp
        if (m != null && prepared) {
            try {
                m.seekTo(ms, MediaPlayer.SEEK_CLOSEST)
                pos = ms
            } catch (e: IllegalStateException) { }
        } else {
            pendingSeek = ms
            pos = ms
        }
    }

    private fun applyVolume() {
        val f = if (mute) 0f else vol / 100f
        try { mp?.setVolume(f, f) } catch (e: IllegalStateException) { }
    }

    private fun release() {
        main.removeCallbacks(ticker)
        val m = mp ?: return
        mp = null
        prepared = false
        try {
            m.setOnPreparedListener(null)
            m.setOnCompletionListener(null)
            m.setOnErrorListener(null)
            m.setOnVideoSizeChangedListener(null)
            m.release()
        } catch (e: Exception) { }
    }

    private fun fail(msg: String) {
        release()
        wantPlay = false
        error = msg
        info(msg)
        device?.onPlayerState("STOPPED")
        ui?.onMedia()
    }

    // ------------------------------------------------------------------ pictures

    private fun loadImage(u: String, g: Int) {
        Thread(null, {
            try {
                val bytes = download(u)
                val bmp = ImageDecoder.decodeBitmap(ImageDecoder.createSource(ByteBuffer.wrap(bytes))) { d, info, _ ->
                    val longSide = max(info.size.width, info.size.height)
                    if (longSide > 2400) {
                        val k = 2400.0 / longSide
                        d.setTargetSize(max(1, (info.size.width * k).roundToInt()), max(1, (info.size.height * k).roundToInt()))
                    }
                }
                main.post {
                    if (g == gen) {
                        image = bmp
                        info("Showing: $title")
                        ui?.onMedia()
                    }
                }
            } catch (e: Exception) {
                main.post { if (g == gen) fail("Can't open this picture") }
            } catch (e: OutOfMemoryError) {
                main.post { if (g == gen) fail("Picture is too big for this device") }
            }
        }, "beam-image", 256 * 1024).start()
    }

    private fun download(u: String): ByteArray {
        val c = URL(u).openConnection() as HttpURLConnection
        try {
            c.connectTimeout = 10_000
            c.readTimeout = 20_000
            if (c.responseCode !in 200..299) throw IOException("HTTP ${c.responseCode}")
            val limit = 25 * 1024 * 1024
            if (c.contentLength > limit) throw IOException("too big")
            val out = ByteArrayOutputStream(if (c.contentLength > 0) c.contentLength else 64 * 1024)
            val buf = ByteArray(32 * 1024)
            c.inputStream.use { i ->
                while (true) {
                    val n = i.read(buf)
                    if (n < 0) break
                    out.write(buf, 0, n)
                    if (out.size() > limit) throw IOException("too big")
                }
            }
            return out.toByteArray()
        } finally {
            c.disconnect()
        }
    }
}
