package dev.personalvpn.client

internal data class AppOwner(val uid: Int, val packages: List<String>, val failure: String = "") {
    val unresolved: Boolean get() = failure.isNotEmpty()
}

internal object AppOwnership {
    const val UNRESOLVED = "__unresolved__"

    // Android explicitly allows more than one package for a UID. A shared UID
    // is a known owner, not a failed lookup. Retain every package for routing.
    // https://developer.android.com/reference/android/content/pm/PackageManager#getPackagesForUid(int)
    fun resolve(uid: Int, packages: List<String>, failure: String = ""): AppOwner {
        val names = packages.filter { it.isNotBlank() }.distinct().sorted()
        val reason = failure.ifEmpty { if (uid < 0) "uid_unavailable" else if (names.isEmpty()) "packages_unavailable" else "" }
        return if (reason.isEmpty()) AppOwner(uid, names) else AppOwner(-1, listOf(UNRESOLVED), reason)
    }

    // A socket identifies a UID, not an individual package inside a shared UID.
    // Conflicting explicit exceptions must not depend on the list's edit order.
    fun orderedRules(rules: List<Rule>) = rules.sortedBy { when (it.action) {
        Action.Block -> 0; Action.Tunnel -> 1; Action.Bypass -> 2; Action.Disabled -> 3
    } }
}

// Backend diagnostics only: at most one notice of each kind per minute. Never
// log packet addresses, visited domains or every socket into the session ring.
internal class OwnerNotices {
    private val last = mutableMapOf<String, Long>()
    @Synchronized fun next(owner: AppOwner, now: Long): Pair<String, String>? {
        val code = when { owner.unresolved -> "policy.owner_unresolved"; owner.packages.size > 1 -> "policy.shared_uid"; else -> return null }
        if (last[code]?.let { now - it < 60_000 } == true) return null
        last[code] = now
        return code to if (owner.unresolved) "App owner unavailable (${owner.failure}); rejected while App rules are enabled. No Direct fallback."
        else "Shared UID has ${owner.packages.size} packages; App exceptions use Block > Tunnel > Bypass."
    }
}
