package dev.personalvpn.client

import android.content.Context

// Remember display, not approval. This never changes VPN configuration or
// claims that a manual setting was measured; detected restrictions stay visible.
internal class BackgroundPreferences(context: Context) {
    private val preferences=context.getSharedPreferences("background-guidance",Context.MODE_PRIVATE)
    fun manualShown(): Boolean = preferences.getBoolean("manual-shown",false)
    fun saveManualShown(shown: Boolean): Boolean = preferences.edit().putBoolean("manual-shown",shown).remove("confirmed-manual").commit()
}
