package dev.personalvpn.client

import android.app.*
import android.appwidget.AppWidgetManager
import android.appwidget.AppWidgetProvider
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.widget.RemoteViews
import androidx.core.app.NotificationManagerCompat
import java.util.Locale

fun bytes(value: Long): String = when { value < 1024 -> "$value B"; value < 1024 * 1024 -> String.format(Locale.US, "%.1f KiB", value / 1024.0); value < 1024L * 1024 * 1024 -> String.format(Locale.US, "%.1f MiB", value / (1024.0 * 1024)); else -> String.format(Locale.US, "%.2f GiB", value / (1024.0 * 1024 * 1024)) }
object Surfaces {
    private const val CHANNEL = "vpn-connection"
    fun channels(context: Context) { context.getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel(CHANNEL, "VPN connection", NotificationManager.IMPORTANCE_LOW).apply { description = "Connection status and current tunnel speed"; setSound(null, null); enableVibration(false); setShowBadge(false) }) }
    fun activity(context: Context, action: String = "", request: Int = 0): PendingIntent = PendingIntent.getActivity(context, request, Intent(context, MainActivity::class.java).setAction(action).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    private fun command(context: Context, action: String, request: Int) = PendingIntent.getService(context, request, Intent(context, TunnelService::class.java).setAction(action), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    fun notification(context: Context): Notification {
        val state = Repository.state.value
        val config = Repository.config.value
        val profile = state.profileName.takeIf { state.active && it.isNotEmpty() } ?: config.profiles.find { it.id == config.selected }?.name ?: "Personal VPN"
        val protocol = state.protocol.takeIf { state.active && it.isNotEmpty() } ?: config.protocol
        val rates = "↑ ${bytes(state.uploadRate)}/s    ↓ ${bytes(state.downloadRate)}/s"
        val detail = "$profile · $protocol\n$rates\nSent ${bytes(state.uploaded)} · Received ${bytes(state.downloaded)}"
        return Notification.Builder(context, CHANNEL).setSmallIcon(R.drawable.ic_notification).setColor(Color.rgb(20, 198, 162)).setContentTitle("Personal VPN · ${state.status}").setContentText(rates).setStyle(Notification.BigTextStyle().bigText(detail)).setContentIntent(activity(context)).setOnlyAlertOnce(true).setOngoing(true).setCategory(Notification.CATEGORY_SERVICE).setVisibility(Notification.VISIBILITY_PRIVATE).setShowWhen(false)
            .addAction(Notification.Action.Builder(null, "Reconnect", command(context, TunnelService.RECONNECT, 1)).build())
            .addAction(Notification.Action.Builder(null, "Disconnect", command(context, TunnelService.DISCONNECT, 2)).build()).build()
    }
    fun rate(context: Context) { try { context.getSystemService(NotificationManager::class.java).notify(1, notification(context)) } catch (_: SecurityException) { /* Permission guidance is in the app; no event spam. */ } }
    fun refresh(context: Context) {
        if (Repository.state.value.active) rate(context)
        val manager = AppWidgetManager.getInstance(context)
        VpnWidget.update(context, manager, manager.getAppWidgetIds(ComponentName(context, VpnWidget::class.java)))
        TileService.requestListeningState(context, ComponentName(context, VpnTile::class.java))
    }
}
class VpnWidget : AppWidgetProvider() {
    override fun onUpdate(context: Context, manager: AppWidgetManager, ids: IntArray) = update(context, manager, ids)
    companion object {
        fun update(context: Context, manager: AppWidgetManager, ids: IntArray) {
            val state = Repository.state.value
            val profile = Repository.config.value.profiles.find { it.id == Repository.config.value.selected }?.name.orEmpty()
            val views = RemoteViews(context.packageName, R.layout.vpn_widget)
            views.setTextViewText(R.id.widget_status, "${state.status} · $profile")
            views.setTextViewText(R.id.widget_connect, if (state.active) "Disconnect" else "Connect")
            // Activity PendingIntent preserves Android's VPN-consent flow and
            // the background activity rules on Android 14.
            views.setOnClickPendingIntent(R.id.widget_connect, Surfaces.activity(context, if (state.active) TunnelService.DISCONNECT else TunnelService.CONNECT, 3))
            views.setOnClickPendingIntent(R.id.widget_title, Surfaces.activity(context))
            manager.updateAppWidget(ids, views)
        }
    }
}
class VpnTile : TileService() {
    override fun onStartListening() {
        val state = Repository.state.value
        qsTile?.apply { this.state = if (state.active) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE; subtitle = state.status; label = "Personal VPN"; updateTile() }
    }
    override fun onClick() {
        super.onClick()
        val action = if (Repository.state.value.active) TunnelService.DISCONNECT else TunnelService.CONNECT
        if (VpnService.prepare(this) == null) { TunnelService.command(this, action); onStartListening() }
        else if (Build.VERSION.SDK_INT >= 34) startActivityAndCollapse(Surfaces.activity(this, action, 4))
        else launchBeforeAndroid14(action)
    }
    // PendingIntent overload was added in API 34. This compatibility path is
    // reachable only on API 29–33, where the Intent overload is still supported.
    @android.annotation.SuppressLint("StartActivityAndCollapseDeprecated")
    @Suppress("DEPRECATION")
    private fun launchBeforeAndroid14(action: String) { startActivityAndCollapse(Intent(this, MainActivity::class.java).setAction(action).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
}
