package dev.personalvpn.client

import android.content.Context
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.util.LruCache
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.core.graphics.drawable.toBitmap
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

data class InstalledApp(val packageName: String, val name: String, val system: Boolean, val installed: Boolean = true)
data class AppList(val apps: List<InstalledApp> = emptyList(), val loaded: Boolean = false, val failed: Boolean = false)

// Package enumeration and labels are loaded once per process, outside the UI
// thread. Search/selection never query PackageManager. Only visible rows load
// icons; the bounded cache is reused when navigating back to this page.
object InstalledApps {
    private val lock = Mutex()
    @Volatile private var cached = AppList()
    private val icons = LruCache<String, ImageBitmap>(96)
    fun peek() = cached
    suspend fun load(context: Context, refresh: Boolean = false): AppList = lock.withLock {
        if (cached.loaded && !refresh) return@withLock cached
        withContext(Dispatchers.IO) {
            try {
                val manager = context.applicationContext.packageManager
                @Suppress("DEPRECATION")
                val launchable = manager.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), 0).map { it.activityInfo.packageName }.toSet()
                @Suppress("DEPRECATION")
                val apps = manager.getInstalledApplications(0)
                    .filter { it.packageName != EngineConfig.APP }
                    .map { InstalledApp(it.packageName, runCatching { it.loadLabel(manager).toString() }.getOrDefault(it.packageName), it.flags and ApplicationInfo.FLAG_SYSTEM != 0 && it.packageName !in launchable) }
                    .sortedWith(compareBy<InstalledApp> { it.name.lowercase() }.thenBy { it.packageName })
                if (refresh) icons.evictAll()
                cached = AppList(apps, loaded = true)
                cached
            } catch (_: Exception) { cached.copy(failed = true) }
        }
    }
    suspend fun icon(context: Context, packageName: String): ImageBitmap? = withContext(Dispatchers.IO) {
        icons.get(packageName) ?: runCatching {
            context.applicationContext.packageManager.getApplicationIcon(packageName)
                .toBitmap(96, 96).asImageBitmap().also { icons.put(packageName, it) }
        }.getOrNull()
    }
}
