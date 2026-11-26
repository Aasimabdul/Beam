package com.beam.app

import android.app.Activity
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.Gravity
import android.view.KeyEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsets
import android.view.WindowInsetsController
import android.view.WindowManager
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.SeekBar
import android.widget.TextView
import kotlin.math.max
import kotlin.math.min
import kotlin.math.roundToInt

/** Full-screen player: video, picture or audio info, with small controls that appear on tap or remote key. */
class PlayerActivity : Activity(), Playback.Ui, SurfaceHolder.Callback {
    private lateinit var root: FrameLayout
    private lateinit var surface: SurfaceView
    private lateinit var picture: ImageView
    private lateinit var center: TextView
    private lateinit var titleView: TextView
    private lateinit var controls: LinearLayout
    private lateinit var playBtn: TextView
    private lateinit var timeView: TextView
    private lateinit var modeBtn: TextView
    private var mode = 0
    private lateinit var seek: SeekBar
    private val h = Handler(Looper.getMainLooper())
    private var seeking = false

    private val tick = object : Runnable {
        override fun run() {
            refreshTime()
            h.postDelayed(this, 500)
        }
    }
    private val hideControls = Runnable { controls.visibility = View.GONE; titleView.visibility = View.GONE }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (!Playback.uiWanted) {
            finish()
            return
        }
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        setShowWhenLocked(true)

        val match = ViewGroup.LayoutParams.MATCH_PARENT
        val wrap = ViewGroup.LayoutParams.WRAP_CONTENT
        root = FrameLayout(this).apply { setBackgroundColor(Color.BLACK) }

        surface = SurfaceView(this)
        surface.holder.addCallback(this)
        root.addView(surface, FrameLayout.LayoutParams(match, match))

        picture = ImageView(this).apply { scaleType = ImageView.ScaleType.FIT_CENTER; visibility = View.GONE }
        root.addView(picture, FrameLayout.LayoutParams(match, match))

        center = tv("", 18f, FG).apply {
            gravity = Gravity.CENTER
            setPadding(dp(24), 0, dp(24), 0)
        }
        root.addView(center, FrameLayout.LayoutParams(match, match))

        titleView = tv("", 16f, FG, true).apply {
            setBackgroundColor(0x99000000.toInt())
            setPadding(dp(16), dp(12), dp(16), dp(12))
            maxLines = 2
            visibility = View.GONE
        }
        root.addView(titleView, FrameLayout.LayoutParams(match, wrap, Gravity.TOP))

        playBtn = pill("Pause") { Playback.userToggle(); refreshTime(); showControls() }
        val stopBtn = pill("Stop") { Playback.userStop() }
        mode = Prefs.mode(this)
        modeBtn = pill(Aspect.labels[mode]) { cycleMode() }
        timeView = tv("0:00 / 0:00", 13f, FG).apply { setPadding(dp(10), 0, dp(10), 0) }
        seek = SeekBar(this).apply {
            max = 1000
            setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                override fun onProgressChanged(s: SeekBar, p: Int, fromUser: Boolean) {
                    if (fromUser) timeView.text = fmt(Playback.durationMs() * p / 1000) + " / " + fmt(Playback.durationMs())
                }
                override fun onStartTrackingTouch(s: SeekBar) { seeking = true; h.removeCallbacks(hideControls) }
                override fun onStopTrackingTouch(s: SeekBar) {
                    seeking = false
                    Playback.userSeekTo(Playback.durationMs() * s.progress / 1000)
                    showControls()
                }
            })
        }
        controls = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setBackgroundColor(0x99000000.toInt())
            setPadding(dp(12), dp(8), dp(12), dp(8))
            visibility = View.GONE
            addView(playBtn, LinearLayout.LayoutParams(dp(78), wrap))
            addView(timeView, LinearLayout.LayoutParams(wrap, wrap))
            addView(seek, LinearLayout.LayoutParams(0, wrap, 1f))
            addView(modeBtn, LinearLayout.LayoutParams(dp(64), wrap).apply { marginStart = dp(10) })
            addView(stopBtn, LinearLayout.LayoutParams(dp(70), wrap).apply { marginStart = dp(10) })
        }
        root.addView(controls, FrameLayout.LayoutParams(match, wrap, Gravity.BOTTOM))

        root.setOnClickListener { toggleControls() }
        root.addOnLayoutChangeListener { _, _, _, _, _, _, _, _, _ -> fitVideo() }
        setContentView(root)
        hideSystemBars()
        Playback.ui = this
        onMedia()
        h.post(tick)
    }

    override fun onResume() {
        super.onResume()
        if (!Playback.uiWanted) finish()
    }

    override fun onDestroy() {
        h.removeCallbacksAndMessages(null)
        if (Playback.ui === this) Playback.ui = null
        super.onDestroy()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemBars() {
        if (Build.VERSION.SDK_INT >= 30) {
            window.insetsController?.let {
                it.hide(WindowInsets.Type.systemBars())
                it.systemBarsBehavior = WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            }
        } else {
            window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
                View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY or View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
                View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
        }
    }

    // ---- Playback.Ui

    override fun onMedia() {
        val kind = Playback.kind
        surface.visibility = if (kind == "video") View.VISIBLE else View.GONE
        picture.setImageBitmap(Playback.image)
        picture.visibility = if (kind == "image" && Playback.image != null) View.VISIBLE else View.GONE
        val err = Playback.error
        center.text = when {
            err != null -> err
            kind == "video" && Playback.videoW == 0 -> "Loading…"
            kind == "image" && Playback.image == null -> "Loading…"
            kind == "audio" -> Playback.title
            else -> ""
        }
        titleView.text = Playback.title
        fitVideo()
        showControls()
    }

    override fun onClose() {
        finish()
    }

    override fun onVideoSize(w: Int, h: Int) {
        center.text = ""
        fitVideo()
    }

    // ---- SurfaceHolder.Callback

    override fun surfaceCreated(holder: SurfaceHolder) = Playback.setSurface(holder)
    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) { }
    override fun surfaceDestroyed(holder: SurfaceHolder) = Playback.setSurface(null)

    // ---- layout and controls

    private fun cycleMode() {
        mode = (mode + 1) % Aspect.labels.size
        Prefs.setMode(this, mode)
        modeBtn.text = Aspect.labels[mode]
        fitVideo()
        android.widget.Toast.makeText(this, Aspect.labels[mode] + ": " + Aspect.hints[mode], android.widget.Toast.LENGTH_SHORT).show()
        showControls()
    }

    /** Sizes the video surface and the picture for the chosen mode: Fit, Fill, 16:9 or Crop. */
    private fun fitVideo() {
        val rw = root.width
        val rh = root.height
        if (rw <= 0 || rh <= 0) return
        // the biggest 16:9 box that fits the screen
        var bw = rw
        var bh = (rw * 9f / 16f).roundToInt()
        if (bh > rh) {
            bh = rh
            bw = (rh * 16f / 9f).roundToInt()
        }
        // picture
        val plp = picture.layoutParams as FrameLayout.LayoutParams
        val pw = if (mode == 2) bw else ViewGroup.LayoutParams.MATCH_PARENT
        val ph = if (mode == 2) bh else ViewGroup.LayoutParams.MATCH_PARENT
        if (plp.width != pw || plp.height != ph || plp.gravity != Gravity.CENTER) {
            plp.width = pw
            plp.height = ph
            plp.gravity = Gravity.CENTER
            picture.layoutParams = plp
        }
        picture.scaleType = when (mode) {
            0 -> ImageView.ScaleType.FIT_CENTER
            3 -> ImageView.ScaleType.CENTER_CROP
            else -> ImageView.ScaleType.FIT_XY
        }
        // video
        val vw = Playback.videoW
        val vh = Playback.videoH
        if (vw <= 0 || vh <= 0) return
        val fit = min(rw / vw.toFloat(), rh / vh.toFloat())
        val fill = max(rw / vw.toFloat(), rh / vh.toFloat())
        val w: Int
        val hgt: Int
        when (mode) {
            1 -> { w = rw; hgt = rh }
            2 -> { w = bw; hgt = bh }
            3 -> { w = max(1, (vw * fill).roundToInt()); hgt = max(1, (vh * fill).roundToInt()) }
            else -> { w = max(1, (vw * fit).roundToInt()); hgt = max(1, (vh * fit).roundToInt()) }
        }
        val lp = surface.layoutParams as FrameLayout.LayoutParams
        if (lp.width != w || lp.height != hgt || lp.gravity != Gravity.CENTER) {
            lp.width = w
            lp.height = hgt
            lp.gravity = Gravity.CENTER
            surface.layoutParams = lp
        }
    }

    private fun fmt(ms: Long): String {
        val s = max(0L, ms) / 1000
        return if (s >= 3600) String.format(java.util.Locale.US, "%d:%02d:%02d", s / 3600, (s / 60) % 60, s % 60)
        else String.format(java.util.Locale.US, "%d:%02d", s / 60, s % 60)
    }

    private fun refreshTime() {
        val d = Playback.durationMs()
        val p = Playback.positionMs()
        playBtn.text = if (Playback.isPlaying()) "Pause" else "Play"
        playBtn.visibility = if (Playback.kind == "image") View.GONE else View.VISIBLE
        seek.visibility = if (d > 0) View.VISIBLE else View.INVISIBLE
        timeView.visibility = if (Playback.kind == "image") View.GONE else View.VISIBLE
        if (!seeking) {
            timeView.text = fmt(p) + " / " + fmt(d)
            if (d > 0) seek.progress = (p * 1000 / d).toInt().coerceIn(0, 1000)
        }
    }

    private fun showControls() {
        refreshTime()
        controls.visibility = View.VISIBLE
        titleView.visibility = View.VISIBLE
        h.removeCallbacks(hideControls)
        h.postDelayed(hideControls, 4000)
    }

    private fun toggleControls() {
        if (controls.visibility == View.VISIBLE) {
            h.removeCallbacks(hideControls)
            hideControls.run()
        } else {
            showControls()
        }
    }

    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        when (keyCode) {
            KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_MEDIA_PLAY_PAUSE,
            KeyEvent.KEYCODE_MEDIA_PLAY, KeyEvent.KEYCODE_MEDIA_PAUSE -> {
                Playback.userToggle()
                showControls()
                return true
            }
            KeyEvent.KEYCODE_DPAD_RIGHT, KeyEvent.KEYCODE_MEDIA_FAST_FORWARD -> {
                Playback.userSeekTo(Playback.positionMs() + 10_000)
                showControls()
                return true
            }
            KeyEvent.KEYCODE_DPAD_LEFT, KeyEvent.KEYCODE_MEDIA_REWIND -> {
                Playback.userSeekTo(max(0L, Playback.positionMs() - 10_000))
                showControls()
                return true
            }
            KeyEvent.KEYCODE_DPAD_UP, KeyEvent.KEYCODE_V, KeyEvent.KEYCODE_MENU -> {
                cycleMode()
                return true
            }
            KeyEvent.KEYCODE_MEDIA_STOP, KeyEvent.KEYCODE_BACK -> {
                Playback.userStop()
                return true
            }
        }
        return super.onKeyDown(keyCode, event)
    }

    @Suppress("OVERRIDE_DEPRECATION", "DEPRECATION")
    override fun onBackPressed() {
        Playback.userStop()
    }
}
