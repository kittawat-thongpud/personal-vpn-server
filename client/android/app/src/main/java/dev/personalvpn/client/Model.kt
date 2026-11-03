package dev.personalvpn.client

import org.json.JSONArray
import org.json.JSONObject
import java.net.URI
import java.net.URLDecoder
import java.util.Base64
import java.util.UUID

enum class Action { Tunnel, Bypass, Block, Disabled }
data class Rule(val target: String, val action: Action, val kind: String = "Domain") {
    fun json() = JSONObject().put("target", target).put("action", action.name).put("kind", kind)
}
data class Policy(val mode: Action = Action.Disabled, val rules: List<Rule> = emptyList()) {
    fun json() = JSONObject().put("mode", mode.name).put("rules", JSONArray(rules.map { it.json() }))
    companion object {
        fun parse(o: JSONObject?) = if (o == null) Policy() else Policy(Action.valueOf(o.getString("mode")), o.optJSONArray("rules").objects().map { Rule(it.getString("target"), Action.valueOf(it.getString("action")), it.optString("kind", "Domain")) })
    }
}
data class Profile(val id: String = UUID.randomUUID().toString(), val name: String = "My server", val country: String = "JP", val reality: String? = null, val hysteria: String? = null) {
    fun json() = JSONObject().put("id", id).put("name", name).put("country", country).put("reality", reality).put("hysteria", hysteria)
    fun endpoint(protocol: String): String = if (protocol == "REALITY") JSONObject(reality!!).getString("server") else JSONObject(hysteria!!).getString("server")
    companion object { fun parse(o: JSONObject) = Profile(o.getString("id"), o.getString("name"), o.optString("country", "JP"), o.optString("reality").takeIf { it.isNotBlank() && it != "null" }, o.optString("hysteria").takeIf { it.isNotBlank() && it != "null" }) }
}
data class Config(val profiles: List<Profile> = emptyList(), val selected: String? = null, val protocol: String = "Hysteria 2", val apps: Policy = Policy(), val sites: Policy = Policy(Action.Tunnel), val ipv6Dns: Boolean = false) {
    fun fingerprint(): String = java.util.Base64.getEncoder().encodeToString(java.security.MessageDigest.getInstance("SHA-256").digest(json().toString().toByteArray(Charsets.UTF_8)))
    fun selectedProfile() = profiles.find { it.id == selected } ?: error("Import and select a server profile first.")
    fun json() = JSONObject().put("profiles", JSONArray(profiles.map { it.json() })).put("selected", selected).put("protocol", protocol).put("apps", apps.json()).put("sites", sites.json()).put("ipv6Dns", ipv6Dns)
    companion object { fun parse(text: String): Config { val o = JSONObject(text); return Config(o.optJSONArray("profiles").objects().map(Profile::parse), o.optString("selected").takeIf { it.isNotBlank() && it != "null" }, o.optString("protocol", "Hysteria 2"), Policy.parse(o.optJSONObject("apps")), Policy.parse(o.optJSONObject("sites")), o.optBoolean("ipv6Dns")) } }
}
fun JSONArray?.objects(): List<JSONObject> = if (this == null) emptyList() else (0 until length()).map { getJSONObject(it) }
fun JSONArray?.strings(): List<String> = if (this == null) emptyList() else (0 until length()).map { getString(it) }

object Imports {
    private fun query(uri: URI) = (uri.rawQuery ?: "").split('&').filter { it.contains('=') }.associate { val parts = it.split('=', limit = 2); URLDecoder.decode(parts[0], "UTF-8") to URLDecoder.decode(parts[1], "UTF-8") }
    fun parse(documents: List<String>): List<Profile> {
        require(documents.sumOf { it.length } <= 512 * 1024) { "Profile input is too large." }
        val profiles = linkedMapOf<String, Profile>()
        fun add(protocol: String, o: JSONObject) {
            validate(protocol, o)
            val server = o.getString("server")
            val key = server.lowercase()
            val previous = profiles[key] ?: Profile(name = server)
            profiles[key] = if (protocol == "REALITY") previous.copy(reality = o.toString()) else previous.copy(hysteria = o.toString())
        }
        for (document in documents) {
            val text = document.trim()
            if (text.startsWith("{")) {
                val o = JSONObject(text)
                when {
                    o.has("outbounds") -> {
                        val out = o.getJSONArray("outbounds").objects().first { it.optString("protocol") == "vless" }
                        val server = out.getJSONObject("settings").getJSONArray("vnext").getJSONObject(0)
                        val user = server.getJSONArray("users").getJSONObject(0)
                        val tls = out.getJSONObject("streamSettings").getJSONObject("realitySettings")
                        add("REALITY", JSONObject().put("server", server.getString("address")).put("port", server.getInt("port")).put("uuid", user.getString("id")).put("sni", tls.getString("serverName")).put("publicKey", tls.optString("publicKey").ifBlank { tls.getString("password") }).put("shortId", tls.getString("shortId")))
                    }
                    o.has("auth") && o.has("tls") -> {
                        val authority = URI("hysteria2://${o.getString("server")}")
                        val tls = o.getJSONObject("tls")
                        add("Hysteria 2", JSONObject().put("server", authority.host).put("port", if (authority.port > 0) authority.port else 443).put("auth", o.getString("auth")).put("sni", tls.optString("sni", authority.host)).put("pin", tls.optString("pinSHA256")).put("insecure", tls.optBoolean("insecure")).put("obfs", o.optJSONObject("obfs")?.optJSONObject("salamander")?.optString("password") ?: ""))
                    }
                    o.has("server") && o.has("uuid") -> add("REALITY", o)
                    o.has("server") && o.has("auth") -> add("Hysteria 2", o)
                    else -> error("Choose a vpnctl export JSON or VLESS/Hysteria share link.")
                }
            } else for (line in text.lineSequence().map { it.trim() }.filter { it.isNotEmpty() }) {
                val uri = URI(line); val q = query(uri); val host = uri.host ?: error("Server host is missing.")
                val o = JSONObject().put("server", host).put("port", if (uri.port > 0) uri.port else 443).put("sni", q["sni"] ?: host)
                when (uri.scheme) {
                    "vless" -> { require(q["security"] == "reality") { "Only VLESS + REALITY links are supported." }; add("REALITY", o.put("uuid", uri.userInfo).put("publicKey", q["pbk"]).put("shortId", q["sid"] ?: "")) }
                    "hysteria2", "hy2" -> add("Hysteria 2", o.put("auth", uri.userInfo).put("pin", q["pinSHA256"] ?: "").put("insecure", q["insecure"] == "1").put("obfs", q["obfs-password"] ?: ""))
                    else -> error("Unsupported share-link protocol.")
                }
            }
        }
        require(profiles.isNotEmpty()) { "No supported profile found." }
        return profiles.values.toList()
    }
    fun validate(protocol: String, o: JSONObject) {
        require(o.getString("server").isNotBlank() && o.getString("server").length <= 253) { "Enter a server hostname or IP." }
        require(o.getInt("port") in 1..65535) { "Invalid server port." }
        require(o.getString("sni").isNotBlank()) { "TLS server name is required." }
        if (protocol == "REALITY") {
            UUID.fromString(o.getString("uuid"))
            require(Base64.getUrlDecoder().decode(o.getString("publicKey")).size == 32) { "Invalid REALITY public key." }
            require(o.getString("shortId").matches(Regex("(?:[0-9a-fA-F]{2}){0,8}"))) { "Invalid REALITY short ID." }
        } else {
            require(o.getString("auth").isNotBlank()) { "Hysteria password is required." }
            val pin = o.optString("pin").replace(":", "")
            require(pin.isEmpty() || pin.matches(Regex("[0-9a-fA-F]{64}"))) { "Invalid certificate SHA-256 pin." }
            require(!o.optBoolean("insecure") || pin.isNotEmpty()) { "An insecure connection without a certificate pin is not allowed." }
        }
    }
}

object EngineConfig {
    const val APP = "dev.personalvpn.client"
    fun outbound(profile: Profile, protocol: String, resolvedServer: String): JSONObject {
        val o = JSONObject(if (protocol == "REALITY") profile.reality!! else profile.hysteria!!)
        Imports.validate(protocol, o)
        val tls = JSONObject().put("enabled", true).put("server_name", o.getString("sni"))
        val out = JSONObject().put("type", if (protocol == "REALITY") "vless" else "hysteria2").put("tag", "tunnel").put("server", resolvedServer).put("server_port", o.getInt("port")).put("tls", tls)
        if (protocol == "REALITY") {
            out.put("uuid", o.getString("uuid")).put("flow", "xtls-rprx-vision")
            tls.put("utls", JSONObject().put("enabled", true).put("fingerprint", "chrome")).put("reality", JSONObject().put("enabled", true).put("public_key", o.getString("publicKey")).put("short_id", o.getString("shortId")))
        } else {
            out.put("password", o.getString("auth"))
            val pin = o.optString("pin").replace(":", "")
            if (pin.isNotEmpty()) tls.put("certificate_sha256", JSONArray().put(Base64.getEncoder().encodeToString(pin.chunked(2).map { it.toInt(16).toByte() }.toByteArray())))
            if (o.optString("obfs").isNotEmpty()) out.put("obfs", JSONObject().put("type", "salamander").put("password", o.getString("obfs")))
        }
        return out
    }
    fun compile(config: Config, resolvedServer: String, fixtureDns: Boolean = false): String {
        validate(config)
        val rules = JSONArray().put(JSONObject().put("action", "sniff")).put(JSONObject().put("protocol", "dns").put("action", "hijack-dns"))
        // The app's health/control traffic cannot be overridden by user rules.
        rules.put(JSONObject().put("package_name", JSONArray().put(APP)).put("outbound", "tunnel"))
        if (config.apps.mode != Action.Disabled) {
            rules.put(JSONObject().put("package_name", JSONArray().put("__unresolved__")).put("action", "reject"))
            config.apps.rules.forEach { rules.put(rule(it, "package_name")) }
        }
        if (config.sites.mode != Action.Disabled) config.sites.rules.forEach { rules.put(rule(it, if (it.kind == "Domain") "domain_suffix" else "ip_cidr")) }
        val fallback = if (config.apps.mode != Action.Disabled) config.apps.mode else if (config.sites.mode != Action.Disabled) config.sites.mode else Action.Tunnel
        if (fallback == Action.Block) rules.put(JSONObject().put("action", "reject"))
        val dns = JSONObject().put("servers", JSONArray().put(JSONObject().put("type", "https").put("tag", "remote").put("server", "1.1.1.1").put("path", "/dns-query").put("tls", JSONObject().put("enabled", true).put("server_name", "cloudflare-dns.com")).put("detour", "tunnel"))).put("strategy", if (config.ipv6Dns) "prefer_ipv4" else "ipv4_only").put("reverse_mapping", true)
        if (fixtureDns) dns.put("servers", JSONArray().put(JSONObject().put("type", "udp").put("tag", "remote").put("server", "198.18.0.1").put("server_port", 15353).put("detour", "tunnel")))
        val tun = JSONObject().put("type", "tun").put("tag", "capture").put("address", JSONArray().put("172.29.255.1/30").put("fd66:706e:1::1/126")).put("mtu", 1280).put("auto_route", true).put("strict_route", true).put("stack", "gvisor")
        return JSONObject().put("log", JSONObject().put("disabled", !fixtureDns).put("level", "debug")).put("dns", dns).put("inbounds", JSONArray().put(tun)).put("outbounds", JSONArray().put(outbound(config.selectedProfile(), config.protocol, resolvedServer)).put(JSONObject().put("type", "direct").put("tag", "direct"))).put("route", JSONObject().put("auto_detect_interface", true).put("rules", rules).put("final", if (fallback == Action.Bypass) "direct" else "tunnel")).toString()
    }
    private fun rule(rule: Rule, field: String) = JSONObject().put(field, JSONArray().put(rule.target)).put("action", if (rule.action == Action.Block) "reject" else "route").also { if (rule.action != Action.Block) it.put("outbound", if (rule.action == Action.Bypass) "direct" else "tunnel") }
    fun validate(config: Config) {
        val profile = config.selectedProfile()
        require(config.protocol in listOf("REALITY", "Hysteria 2")) { "Select a supported VPN protocol." }
        require(if (config.protocol == "REALITY") profile.reality != null else profile.hysteria != null) { "This profile has no credentials for ${config.protocol}. Select its available protocol." }
        require(config.apps.rules.map { it.target }.distinct().size == config.apps.rules.size) { "Duplicate app rule." }
        for (rule in config.apps.rules + config.sites.rules) require(rule.action != Action.Disabled && rule.target.isNotBlank()) { "Invalid rule." }
        for (rule in config.apps.rules) require(rule.target.matches(Regex("[A-Za-z][A-Za-z0-9_]*(\\.[A-Za-z0-9_]+)+")) && rule.target != APP) { "Choose an installed app package." }
        for (rule in config.sites.rules) {
            if (rule.kind == "Domain") require(rule.target.matches(Regex("[A-Za-z0-9_*-]+(\\.[A-Za-z0-9_-]+)+")) && !rule.target.contains('*')) { "Enter a domain without scheme, path or wildcard." }
            else { val parts = rule.target.split('/'); require(parts.size <= 2 && parts[0].matches(Regex("[0-9a-fA-F:.]+"))); if (parts.size == 2) require(parts[1].toInt() in 0..if (parts[0].contains(':')) 128 else 32) }
        }
    }
}
