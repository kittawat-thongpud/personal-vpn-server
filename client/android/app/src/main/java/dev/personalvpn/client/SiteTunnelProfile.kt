package dev.personalvpn.client

import org.json.JSONArray
import org.json.JSONObject
import org.json.JSONTokener
import java.io.InputStream
import java.nio.ByteBuffer

internal object SiteTunnelProfile {
    const val FILE_NAME = "personal-vpn-site-tunnel.json"
    private const val FORMAT = "personal-vpn.site-tunnel"
    private const val MAX_BYTES = 512 * 1024

    fun export(policy: Policy): ByteArray {
        SiteTargets.validate(policy)
        val clean = policy.copy(rules = policy.rules.map { rule ->
            rule.copy(target = SiteTargets.clean(rule.kind, rule.target))
        })
        return clean.json().put("format", FORMAT).put("version", 1).toString(2).toByteArray(Charsets.UTF_8).also {
            require(it.size <= MAX_BYTES) { "Site Tunnel file exceeds 512 KiB." }
        }
    }

    fun read(stream: InputStream): Policy {
        val bytes = ByteArray(MAX_BYTES + 1)
        var size = 0
        while (size < bytes.size) {
            val read = stream.read(bytes, size, bytes.size - size)
            if (read < 0) break
            if (read > 0) size += read else {
                val next = stream.read()
                if (next < 0) break
                bytes[size++] = next.toByte()
            }
        }
        require(size <= MAX_BYTES) { "Site Tunnel file exceeds 512 KiB." }
        val text = Charsets.UTF_8.newDecoder().decode(ByteBuffer.wrap(bytes, 0, size)).toString().trimStart('\uFEFF')
        val reader = JSONTokener(text)
        val root = reader.nextValue() as? JSONObject ?: throw IllegalArgumentException("Choose a Personal VPN Site Tunnel JSON file.")
        require(reader.nextClean() == '\u0000') { "Unexpected data after the Site Tunnel profile." }
        requireFields(root, "format", "version", "mode", "rules")
        require(root.opt("format") == FORMAT && root.opt("version") == 1) { "Choose a supported Personal VPN Site Tunnel JSON file." }
        val mode = readAction(root, "mode")
        val entries = root.opt("rules") as? JSONArray ?: throw IllegalArgumentException("The file must contain a list of site groups.")
        require(entries.length() <= SiteTargets.LIMIT) { "Use at most 500 site groups." }
        val rules = (0 until entries.length()).map { index ->
            val rule = entries.opt(index) as? JSONObject ?: throw IllegalArgumentException("Invalid site group.")
            requireFields(rule, "name", "kind", "target", "action")
            Rule(readString(rule, "target"), readAction(rule, "action"), readString(rule, "kind"), readString(rule, "name"))
        }
        val policy = Policy(mode, rules)
        SiteTargets.validate(policy)
        return policy.copy(rules = rules.map { it.copy(target = SiteTargets.clean(it.kind, it.target)) })
    }

    private fun requireFields(obj: JSONObject, vararg expected: String) {
        require(obj.keys().asSequence().toSet() == expected.toSet()) { "The file does not match the Personal VPN Site Tunnel format." }
    }

    private fun readString(obj: JSONObject, key: String) =
        obj.opt(key) as? String ?: throw IllegalArgumentException("Invalid Site Tunnel field: $key.")

    private fun readAction(obj: JSONObject, key: String): Action {
        val value = readString(obj, key)
        return Action.entries.find { it.name == value } ?: throw IllegalArgumentException("Invalid Site Tunnel action.")
    }
}
