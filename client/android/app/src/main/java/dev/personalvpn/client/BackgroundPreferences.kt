package dev.personalvpn.client

import android.content.Context

// UI acknowledgements only. These never change VPN configuration or claim that
// an Android setting has been measured; detected restrictions cannot be hidden.
internal class BackgroundPreferences(context: Context) {
    private val preferences=context.getSharedPreferences("background-guidance",Context.MODE_PRIVATE)
    fun confirmed(): Set<String> = preferences.getStringSet("confirmed-manual",emptySet())?.toSet().orEmpty()
    fun save(confirmed: Set<String>): Boolean = preferences.edit().putStringSet("confirmed-manual",confirmed.toSet()).commit()
}
