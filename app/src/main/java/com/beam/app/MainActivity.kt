package com.beam.app

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.text.InputType
import android.view.ViewGroup
import android.view.inputmethod.EditorInfo
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView

/** The only screen: switch the receiver on or off, rename it, allow auto-open. Everything else happens in the notification and the player. */
class MainActivity : Activity() {
    private lateinit var status: TextView
    private lateinit var toggle: TextView
    private lateinit var overlay: TextView
    private lateinit var nameField: EditText
    private val h = Handler(Looper.getMainLooper())
    private val poll = object : Runnable {
        override fun run() {
            refresh()
            h.postDelayed(this, 1000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val match = ViewGroup.LayoutParams.MATCH_PARENT
        val wrap = ViewGroup.LayoutParams.WRAP_CONTENT

        val col = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(20), dp(28), dp(20), dp(28))
        }
        col.addView(tv("Beam", 30f, ACCENT, true))
        col.addView(tv("Cast from your phone, PC or TV app to this screen. Fully local, no cloud.", 14f, MUTED).apply { setPadding(0, dp(8), 0, 0) })

        status = tv("", 15f, FG).apply { setPadding(0, dp(20), 0, dp(12)) }
        col.addView(status)

        toggle = pill("Turn on", filled = true) { onToggle() }
        col.addView(toggle, LinearLayout.LayoutParams(match, wrap))

        col.addView(label("Name on the network"))
        nameField = input("Name", Prefs.name(this), InputType.TYPE_CLASS_TEXT).apply {
            imeOptions = EditorInfo.IME_ACTION_DONE
            setOnEditorActionListener { _, _, _ -> saveName(); true }
        }
        col.addView(nameField, LinearLayout.LayoutParams(match, wrap))

        col.addView(label("Auto-open player"))
        overlay = tv("", 13f, MUTED)
        col.addView(overlay)
        col.addView(pill("Allow auto-open") {
            startActivity(Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:$packageName")))
        }, LinearLayout.LayoutParams(match, wrap).apply { topMargin = dp(8) })

        col.addView(label("Works with"))
        col.addView(tv("DLNA / UPnP: BubbleUPnP, VLC, Kodi, Windows \"Cast to device\", gallery and music apps with a cast button.\nChrome tab casting: not supported yet.", 13f, MUTED))

        setContentView(ScrollView(this).apply {
            setBackgroundColor(BG)
            clipToPadding = false
            isFillViewport = true
            addView(col)
            applyInsets(this)
        })
    }

    override fun onResume() {
        super.onResume()
        h.post(poll)
    }

    override fun onPause() {
        h.removeCallbacks(poll)
        super.onPause()
    }

    private fun onToggle() {
        if (ReceiverService.running) {
            ReceiverService.stop(this)
        } else {
            saveName()
            if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
            }
            ReceiverService.start(this)
        }
        h.postDelayed({ refresh() }, 400)
    }

    private fun saveName() {
        val n = nameField.text.toString().trim()
        if (n.isNotEmpty() && n != Prefs.name(this)) {
            Prefs.setName(this, n)
            if (ReceiverService.running) {
                ReceiverService.stop(this)
                h.postDelayed({ ReceiverService.start(this) }, 700)
            }
        }
    }

    private fun refresh() {
        val on = ReceiverService.running
        status.text = if (on) "Ready, visible as \"${Prefs.name(this)}\"\n${ReceiverService.address ?: ""}" else "Receiver is off"
        status.setTextColor(if (on) NEON else MUTED)
        toggle.text = if (on) "Turn off" else "Turn on"
        overlay.text = if (Settings.canDrawOverlays(this)) "Allowed: the player opens by itself when a cast starts."
        else "Not allowed: you will get a notification to tap when a cast starts."
    }
}
