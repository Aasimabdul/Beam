package com.beam.app

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ActivityInfo
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.InputType
import android.view.View
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast

/**
 * Two small pages in one screen. Home: "Ready to cast" with the steps. Settings: name, picture mode, auto-open.
 * Everything else happens in the notification and the player.
 */
class MainActivity : Activity() {
    private lateinit var home: ScrollView
    private lateinit var settings: ScrollView
    private lateinit var tabHome: TextView
    private lateinit var tabSettings: TextView
    private lateinit var headline: TextView
    private lateinit var detail: TextView
    private lateinit var overlay: TextView
    private lateinit var nameField: EditText
    private lateinit var modeButtons: List<TextView>
    private lateinit var modeHint: TextView
    private lateinit var orientationButtons: List<TextView>
    private val h = Handler(Looper.getMainLooper())
    private val poll = object : Runnable {
        override fun run() {
            refresh()
            h.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        applyOrientation()
        home = ScrollView(this).apply {
            setBackgroundColor(BG)
            isFillViewport = true
            addView(buildHome())
        }
        settings = ScrollView(this).apply {
            setBackgroundColor(BG)
            isFillViewport = true
            visibility = View.GONE
            addView(buildSettings())
        }
        val body = FrameLayout(this).apply {
            addView(home, ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            addView(settings, ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
        }
        tabHome = tab("Home") { showSettings(false) }
        tabSettings = tab("Settings") { showSettings(true) }
        val tabs = LinearLayout(this).apply {
            setBackgroundColor(CARD)
            addView(tabHome, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
            addView(tabSettings, LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f))
        }
        setContentView(LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(BG)
            addView(body, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f))
            addView(tabs, LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT))
            applyInsets(this)
        })
        showSettings(false)
        startReceiverIfNeeded()
    }

    private fun tab(label: String, onClick: () -> Unit): TextView = tv(label, 15f, MUTED, true).apply {
        gravity = android.view.Gravity.CENTER
        setPadding(0, dp(15), 0, dp(15))
        isClickable = true
        setOnClickListener { onClick() }
    }

    private fun buildHome(): View {
        val match = ViewGroup.LayoutParams.MATCH_PARENT
        val wrap = ViewGroup.LayoutParams.WRAP_CONTENT
        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(20), dp(20), dp(28))
        }
        col.addView(tv("Beam", 26f, ACCENT, true))

        headline = tv("Beam is ready", 30f, NEON, true).apply { setPadding(0, dp(36), 0, 0) }
        col.addView(headline)
        detail = tv("Starting receiver…", 14f, MUTED).apply { setPadding(0, dp(6), 0, dp(18)) }
        col.addView(detail)

        col.addView(label("How to cast"))
        col.addView(tv(
            "1.  Put this phone and the sending device on the same Wi-Fi.\n\n" +
                "2.  On the sender open a DLNA app: VLC (renderer button), BubbleUPnP, LocalCast, Web Video Caster, " +
                "Kodi, or a gallery / music app with a cast button.\n\n" +
                "3.  Pick the name shown above in the device list, then choose a video, song or photo. It opens here.",
            14f, FG
        ).apply { setLineSpacing(0f, 1.1f) })

        col.addView(label("While it plays"))
        col.addView(tv("Tap the screen for controls. Picture button (or Up key on a remote): Fit, Fill, 16:9, Crop. Back stops.", 13f, MUTED))

        col.addView(label("Good to know"))
        col.addView(tv("Fully local: no cloud, no account. Chrome's own Cast button is not supported (it needs a Google licence); use a DLNA app.", 13f, MUTED))
        return col
    }

    private fun buildSettings(): View {
        val match = ViewGroup.LayoutParams.MATCH_PARENT
        val wrap = ViewGroup.LayoutParams.WRAP_CONTENT
        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(20), dp(20), dp(28))
        }
        col.addView(tv("Settings", 26f, ACCENT, true))

        col.addView(label("Name on the network"))
        nameField = input("Name", Prefs.name(this), InputType.TYPE_CLASS_TEXT).apply {
            imeOptions = EditorInfo.IME_ACTION_DONE
            setOnEditorActionListener { _, _, _ -> saveName(); true }
        }
        col.addView(nameField, LinearLayout.LayoutParams(match, wrap))
        col.addView(pill("Save name") { saveName() }, LinearLayout.LayoutParams(match, wrap).apply { topMargin = dp(8) })

        col.addView(label("Screen orientation"))
        orientationButtons = listOf("Landscape", "Portrait").mapIndexed { i, name ->
            pill(name) { setOrientationMode(i) }.also {
                // Added to the row below after both choices are created.
            }
        }
        val orientationRow = LinearLayout(this)
        orientationButtons.forEachIndexed { i, button ->
            orientationRow.addView(button, LinearLayout.LayoutParams(0, wrap, 1f).apply { if (i > 0) marginStart = dp(8) })
        }
        col.addView(orientationRow, LinearLayout.LayoutParams(match, wrap))

        col.addView(label("Picture"))
        val row = LinearLayout(this)
        modeButtons = Aspect.labels.mapIndexed { i, name ->
            pill(name) { setMode(i) }.also {
                row.addView(it, LinearLayout.LayoutParams(0, wrap, 1f).apply { if (i > 0) marginStart = dp(8) })
            }
        }
        col.addView(row, LinearLayout.LayoutParams(match, wrap))
        modeHint = tv("", 13f, MUTED).apply { setPadding(0, dp(8), 0, 0) }
        col.addView(modeHint)

        col.addView(label("Auto-open player"))
        overlay = tv("", 13f, MUTED)
        col.addView(overlay)
        col.addView(pill("Allow auto-open") {
            startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
        }, LinearLayout.LayoutParams(match, wrap).apply { topMargin = dp(8) })

        col.addView(label("About"))
        col.addView(tv("Beam 0.1.0. A local DLNA receiver: no cloud, no account, no ads.", 13f, MUTED))
        return col
    }

    private fun showSettings(on: Boolean) {
        settings.visibility = if (on) View.VISIBLE else View.GONE
        home.visibility = if (on) View.GONE else View.VISIBLE
        tabHome.setTextColor(if (on) MUTED else ACCENT)
        tabSettings.setTextColor(if (on) ACCENT else MUTED)
        if (on) nameField.setText(Prefs.name(this))
        refresh()
    }

    @Suppress("OVERRIDE_DEPRECATION", "DEPRECATION")
    override fun onBackPressed() {
        if (settings.visibility == View.VISIBLE) showSettings(false) else super.onBackPressed()
    }

    override fun onResume() {
        super.onResume()
        h.post(poll)
    }

    override fun onPause() {
        h.removeCallbacks(poll)
        super.onPause()
    }

    private fun startReceiverIfNeeded() {
        if (ReceiverService.running) return
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        ReceiverService.start(this)
        h.postDelayed({ refresh() }, 400)
    }

    private fun applyOrientation() {
        requestedOrientation = if (Prefs.orientation(this) == 0) ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
        else ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
    }

    private fun setOrientationMode(mode: Int) {
        Prefs.setOrientation(this, mode)
        applyOrientation()
        refresh()
    }

    private fun saveName() {
        val n = nameField.text.toString().trim()
        if (n.isEmpty()) {
            Toast.makeText(this, "Type a name first", Toast.LENGTH_SHORT).show()
            return
        }
        if (n != Prefs.name(this)) {
            Prefs.setName(this, n)
            if (ReceiverService.running) {
                ReceiverService.stop(this)
                h.postDelayed({ ReceiverService.start(this) }, 700)
            }
        }
        Toast.makeText(this, "Saved: $n", Toast.LENGTH_SHORT).show()
        refresh()
    }

    private fun setMode(i: Int) {
        Prefs.setMode(this, i)
        refresh()
    }

    private fun refresh() {
        val on = ReceiverService.running
        headline.text = if (on) "Ready to cast" else "Starting receiver…"
        headline.setTextColor(NEON)
        detail.text = if (on) "Visible as \"${Prefs.name(this)}\"\n${ReceiverService.address ?: ""}" else "Beam starts automatically when you open the app."
        overlay.text = if (Settings.canDrawOverlays(this)) "Allowed: the player opens by itself when a cast starts."
        else "Not allowed: you will get a notification to tap when a cast starts."
        val m = Prefs.mode(this)
        modeButtons.forEachIndexed { i, b ->
            b.setTextColor(if (i == m) BG else ACCENT)
            b.background = android.graphics.drawable.GradientDrawable().apply {
                cornerRadius = dp(10).toFloat()
                setColor(if (i == m) ACCENT else CARD)
                if (i != m) setStroke(dp(1), LINE)
            }
        }
        modeHint.text = Aspect.hints[m]
        val orientation = Prefs.orientation(this)
        orientationButtons.forEachIndexed { i, b ->
            b.setTextColor(if (i == orientation) BG else ACCENT)
            b.background = android.graphics.drawable.GradientDrawable().apply {
                cornerRadius = dp(10).toFloat()
                setColor(if (i == orientation) ACCENT else CARD)
                if (i != orientation) setStroke(dp(1), LINE)
            }
        }
    }
}