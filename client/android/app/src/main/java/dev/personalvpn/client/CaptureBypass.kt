package dev.personalvpn.client

import android.content.pm.PackageManager
import android.os.Process

internal class CaptureBypassUnavailable(val code: String, detail: String) : IllegalArgumentException(detail)

internal object CaptureBypass {
    fun requested(policy: Policy): List<String> = if (policy.mode == Action.Disabled) emptyList()
        else policy.rules.filter { it.action == Action.Exclude }.map { it.target }.distinct().sorted()

    // Android VPN selection is UID-wide, even though the Builder takes package names.
    fun resolve(policy: Policy, locked: Boolean, owner: (String) -> AppOwner): List<String> {
        val packages = requested(policy)
        if (packages.isEmpty()) return packages
        if (locked) throw CaptureBypassUnavailable("lockdown", "Android blocks connections outside the VPN. Disable VPN lockdown or use Direct via TUN.")
        val selected = packages.toSet()
        for (name in packages) {
            val group = owner(name)
            if (group.unresolved || group.uid < Process.FIRST_APPLICATION_UID || name !in group.packages || EngineConfig.APP in group.packages)
                throw CaptureBypassUnavailable("package_unavailable", "A selected app cannot be excluded. Check the app is installed or use Direct via TUN.")
            if (!selected.containsAll(group.packages))
                throw CaptureBypassUnavailable("shared_uid", "Apps that share an Android UID must all use Bypass VPN together. Select all related apps or use Direct via TUN.")
        }
        return packages
    }

    fun resolve(policy: Policy, locked: Boolean, manager: PackageManager): List<String> = resolve(policy, locked) { name ->
        try {
            val uid = manager.getPackageUid(name, 0)
            AppOwnership.resolve(uid, manager.getPackagesForUid(uid)?.toList().orEmpty())
        } catch (_: PackageManager.NameNotFoundException) {
            AppOwnership.resolve(-1, emptyList())
        }
    }
}
