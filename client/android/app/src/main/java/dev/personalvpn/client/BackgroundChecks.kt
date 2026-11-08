package dev.personalvpn.client

import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.net.ConnectivityManager
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.IntentCompat
import androidx.core.content.PackageManagerCompat
import androidx.core.content.UnusedAppRestrictionsConstants as Unused
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.util.concurrent.TimeUnit

internal enum class CheckState(val label: String) { Good("Configured"), Attention("Change recommended"), Manual("Check manually"), Unknown("Could not check") }
internal enum class SettingsTarget { BatteryOptimization, App, BatterySaver, BackgroundData, Notifications, NotificationChannel, UnusedApps, Developer, Battery, Device, Vpn }
internal data class BackgroundCheck(val id: String,val title: String,val state: CheckState,val steps: List<String>,val target: SettingsTarget)
internal data class PreviousExit(val time: Long,val reason: String,val unexpected: Boolean)
internal data class BackgroundReview(val checks: List<BackgroundCheck>,val unusedStatus: Int,val previousExit: PreviousExit?) {
    val detectedIssues get()=checks.filter { it.state==CheckState.Attention }
    val shouldPrompt get()=detectedIssues.isNotEmpty()
    val manualChecks get()=checks.filter { it.state==CheckState.Manual }
    fun showManualOnLaunch(alreadyShown: Boolean)=!alreadyShown && manualChecks.isNotEmpty()
}
internal data class BackgroundFacts(val batteryExempt: Boolean?=null,val restricted: Boolean?=null,val saver: Boolean?=null,val dataRestricted: Boolean?=null,val notifications: Boolean?=null,val channel: Boolean?=null,val unused: Int=Unused.ERROR,val dontKeepActivities: Boolean?=null,val developer: Boolean=false,val xiaomi: Boolean=false,val samsung: Boolean=false)

internal object BackgroundChecks {
    fun isLauncherOpen(action: String?,categories: Set<String>?,flags: Int) = action==Intent.ACTION_MAIN && categories?.contains(Intent.CATEGORY_LAUNCHER)==true && (flags and Intent.FLAG_ACTIVITY_LAUNCHED_FROM_HISTORY)==0
    fun checks(f: BackgroundFacts): List<BackgroundCheck> {
        fun status(risk: Boolean?)=when(risk){true->CheckState.Attention;false->CheckState.Good;null->CheckState.Unknown}
        val rows=mutableListOf(
            BackgroundCheck("doze","Battery optimization",status(f.batteryExempt?.not()),listOf("Open Battery optimization.","Choose All apps, then Personal VPN.","Select Don't optimize, then tap Done."),SettingsTarget.BatteryOptimization),
            BackgroundCheck("background","Background activity",status(f.restricted),listOf("Open Personal VPN's App settings.","Tap Battery or App battery usage.","Choose Unrestricted, or turn Allow background usage on."),SettingsTarget.App),
            BackgroundCheck("saver","Battery saver",status(f.saver),listOf("Open Battery saver.","Turn Battery saver off while using VPN."),SettingsTarget.BatterySaver),
            BackgroundCheck("data","Data Saver",status(f.dataRestricted),listOf("Open Unrestricted data usage.","Find Personal VPN and turn its switch on.","In App settings → Mobile data, also allow Background data if shown."),SettingsTarget.BackgroundData),
            BackgroundCheck("notifications","App notifications",status(f.notifications?.not()),listOf("Open Personal VPN notifications.","Turn Allow notifications on."),SettingsTarget.Notifications),
            BackgroundCheck("channel","VPN notification channel",status(f.channel?.not()),listOf("Open the VPN connection notification category.","Turn Allow notifications on."),SettingsTarget.NotificationChannel),
            BackgroundCheck("unused","Pause unused app activity",when(f.unused){Unused.DISABLED,Unused.FEATURE_NOT_AVAILABLE->CheckState.Good;Unused.API_30_BACKPORT,Unused.API_30,Unused.API_31->CheckState.Attention;else->CheckState.Unknown},listOf("Open Personal VPN's App settings.","Find Pause app activity if unused or Remove permissions if app isn't used.","Turn that switch off."),SettingsTarget.UnusedApps),
            BackgroundCheck("activities","Don't keep activities",status(f.dontKeepActivities),listOf("Open Developer options.","Find Don't keep activities.","Turn it off."),SettingsTarget.Developer)
        )
        if(f.developer)rows+=BackgroundCheck("process_limit","Background process limit",CheckState.Manual,listOf("Open Developer options.","Tap Background process limit.","Choose Standard limit."),SettingsTarget.Developer)
        if(f.xiaomi)rows+=listOf(
            BackgroundCheck("xiaomi_battery","Xiaomi battery policy",CheckState.Manual,listOf("Open Personal VPN's App settings.","Tap Battery or Power.","Choose No restrictions."),SettingsTarget.App),
            BackgroundCheck("xiaomi_autostart","Xiaomi Background autostart",CheckState.Manual,listOf("In Personal VPN's App settings, look for Background autostart.","If missing, open Settings → Apps → Permissions → Background autostart.","Turn Personal VPN on."),SettingsTarget.App),
            BackgroundCheck("xiaomi_locked","Xiaomi screen-off limits",CheckState.Manual,listOf("Open Battery → Additional features.","Set Turn off mobile data when device is locked to Never, if shown.","Set Clear cache when device is locked to Never, if shown."),SettingsTarget.Battery),
            BackgroundCheck("xiaomi_recent","Optional protection from Clear all",CheckState.Manual,listOf("Open Security → Settings → Boost speed → Lock apps.","Enable Personal VPN if you want Clear all to keep it running.","Unlock it before removing its Recent Apps task to stop VPN."),SettingsTarget.App)
        )
        if(f.samsung)rows+=listOf(
            BackgroundCheck("samsung_sleep","Samsung sleeping apps",CheckState.Manual,listOf("Open Settings → Battery → Background usage limits. Some phones show Device care → Battery first.","Remove Personal VPN from Sleeping apps and Deep sleeping apps.","Add it to Never auto sleeping apps, if available."),SettingsTarget.Battery),
            BackgroundCheck("samsung_battery","Samsung app battery policy",CheckState.Manual,listOf("Open Personal VPN's App settings.","Tap Battery.","Choose Unrestricted."),SettingsTarget.App),
            BackgroundCheck("samsung_restart","Samsung automatic restart",CheckState.Manual,listOf("Open Settings → Device care → Auto optimization.","Tap Auto restart.","If automatic phone restarts interrupt VPN, turn Restart when needed and Restart on schedule off."),SettingsTarget.Device),
            BackgroundCheck("samsung_memory","Samsung memory cleanup",CheckState.Manual,listOf("Open Settings → Device care → Memory.","Tap Excluded apps.","Add Personal VPN, if available."),SettingsTarget.Device)
        )
        return rows
    }
    suspend fun read(context: Context): BackgroundReview = withContext(Dispatchers.IO) {
        fun <T> value(read: ()->T): T?=runCatching(read).getOrNull()
        val power=context.getSystemService(PowerManager::class.java)
        val activity=context.getSystemService(ActivityManager::class.java)
        val manager=context.getSystemService(NotificationManager::class.java)
        val pending=runCatching { PackageManagerCompat.getUnusedAppRestrictionsStatus(context) }.getOrNull()
        val unused=try { pending?.get(2,TimeUnit.SECONDS)?:Unused.ERROR } catch(_: Exception) { Unused.ERROR } finally { if(pending!=null && !pending.isDone)pending.cancel(true) }
        val facts=BackgroundFacts(value { power.isIgnoringBatteryOptimizations(context.packageName) },value { activity.isBackgroundRestricted },value { power.isPowerSaveMode },
            value { context.getSystemService(ConnectivityManager::class.java).restrictBackgroundStatus==ConnectivityManager.RESTRICT_BACKGROUND_STATUS_ENABLED },
            value { NotificationManagerCompat.from(context).areNotificationsEnabled() },value { manager.getNotificationChannel("vpn-connection")?.importance!=NotificationManager.IMPORTANCE_NONE },unused,
            value { Settings.Global.getInt(context.contentResolver,Settings.Global.ALWAYS_FINISH_ACTIVITIES,0)!=0 },value { Settings.Global.getInt(context.contentResolver,Settings.Global.DEVELOPMENT_SETTINGS_ENABLED,0)!=0 }?:false,
            listOf(Build.MANUFACTURER,Build.BRAND).any { it.equals("Xiaomi",true)||it.equals("Redmi",true)||it.equals("Poco",true) },Build.MANUFACTURER.equals("Samsung",true)||Build.BRAND.equals("Samsung",true))
        val previous=if(Build.VERSION.SDK_INT>=30)value { activity.getHistoricalProcessExitReasons(context.packageName,0,4).firstOrNull { it.processName==context.packageName }?.let {
            // Android 17 documents this fixed marker for memory-limit exits.
            // Never display or retain the raw description (it may be sensitive).
            val memoryLimit=Build.VERSION.SDK_INT>=37 && it.reason==ApplicationExitInfo.REASON_OTHER && it.description?.contains("MemoryLimiter:AnonSwap")==true
            val reason=if(memoryLimit)"Android memory limit" else when(it.reason) { ApplicationExitInfo.REASON_CRASH->"App crash";ApplicationExitInfo.REASON_CRASH_NATIVE->"Native crash";ApplicationExitInfo.REASON_ANR->"App not responding";ApplicationExitInfo.REASON_LOW_MEMORY->"System low memory";ApplicationExitInfo.REASON_USER_REQUESTED->"User/task stop";ApplicationExitInfo.REASON_EXIT_SELF->"App exited";else->"Other Android exit (reason ${it.reason})" }
            PreviousExit(it.timestamp,reason,memoryLimit || it.reason in setOf(ApplicationExitInfo.REASON_CRASH,ApplicationExitInfo.REASON_CRASH_NATIVE,ApplicationExitInfo.REASON_ANR,ApplicationExitInfo.REASON_LOW_MEMORY))
        } } else null
        BackgroundReview(checks(facts),unused,previous)
    }
    fun intents(context: Context,target: SettingsTarget,unusedStatus: Int): List<Intent> {
        val uri=Uri.parse("package:${context.packageName}")
        val specific=when(target) {
            SettingsTarget.BatteryOptimization->Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
            SettingsTarget.App->Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,uri)
            SettingsTarget.BatterySaver->Intent(Settings.ACTION_BATTERY_SAVER_SETTINGS)
            SettingsTarget.BackgroundData->Intent(Settings.ACTION_IGNORE_BACKGROUND_DATA_RESTRICTIONS_SETTINGS,uri)
            SettingsTarget.Notifications->Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,context.packageName)
            SettingsTarget.NotificationChannel->Intent(Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE,context.packageName).putExtra(Settings.EXTRA_CHANNEL_ID,"vpn-connection")
            SettingsTarget.UnusedApps->if(unusedStatus!=Unused.ERROR&&unusedStatus!=Unused.FEATURE_NOT_AVAILABLE)runCatching { IntentCompat.createManageUnusedAppRestrictionsIntent(context,context.packageName) }.getOrNull() else null
            SettingsTarget.Developer->Intent(Settings.ACTION_APPLICATION_DEVELOPMENT_SETTINGS)
            SettingsTarget.Battery->Intent(Intent.ACTION_POWER_USAGE_SUMMARY)
            SettingsTarget.Device->Intent(Settings.ACTION_SETTINGS)
            SettingsTarget.Vpn->Intent(Settings.ACTION_VPN_SETTINGS)
        }
        return listOfNotNull(specific,Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,uri),Intent(Settings.ACTION_SETTINGS))
    }
}
