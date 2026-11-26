package com.beam.app

import android.content.Context
import android.os.Build
import java.util.UUID

/** The few settings Beam keeps: a stable device id, the visible name and the last HTTP port. */
object Prefs {
    private fun p(c: Context) = c.getSharedPreferences("beam", Context.MODE_PRIVATE)

    fun uuid(c: Context): String {
        val s = p(c)
        var id = s.getString("uuid", null)
        if (id == null) {
            id = UUID.randomUUID().toString()
            s.edit().putString("uuid", id).apply()
        }
        return id
    }

    fun name(c: Context): String = p(c).getString("name", null)?.takeIf { it.isNotBlank() } ?: "Beam (${Build.MODEL})"

    fun setName(c: Context, n: String) {
        p(c).edit().putString("name", n.trim().take(40)).apply()
    }

    fun port(c: Context): Int = p(c).getInt("port", 0)

    fun setPort(c: Context, port: Int) {
        p(c).edit().putInt("port", port).apply()
    }

    /** Picture mode index into [Aspect.labels]: 0 Fit, 1 Fill, 2 16:9, 3 Crop. */
    fun mode(c: Context): Int = p(c).getInt("mode", 0).coerceIn(0, Aspect.labels.size - 1)

    fun setMode(c: Context, m: Int) {
        p(c).edit().putInt("mode", m).apply()
    }

    /** Screen orientation: 0 landscape (default), 1 portrait. */
    fun orientation(c: Context): Int = p(c).getInt("orientation", 0).coerceIn(0, 1)

    fun setOrientation(c: Context, mode: Int) {
        p(c).edit().putInt("orientation", mode.coerceIn(0, 1)).apply()
    }
}

/** How video and pictures fit the screen. */
object Aspect {
    val labels = listOf("Fit", "Fill", "16:9", "Crop")
    val hints = listOf("Whole picture, bars if needed", "Stretch to the whole screen", "Force a 16:9 picture", "Zoom to fill, trim the edges")
}
