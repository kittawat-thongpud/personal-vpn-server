package dev.personalvpn.client

import java.net.InetAddress
import java.net.URI
import java.util.Locale

internal object SiteTargets {
    const val LIMIT=500
    const val INPUT_LIMIT=131072
    private fun tokens(text: String): List<String> {
        require(text.length<=INPUT_LIMIT) { "Target list is too long." }
        return text.split(',', ';', '\r', '\n').map { it.trim() }.filter { it.isNotEmpty() }.also { require(it.isNotEmpty()) { "Enter at least one target." } }
    }
    fun clean(kind: String,text: String)=tokens(text).distinctBy { normalize(kind,it) }.joinToString(", ")
    fun parse(kind: String,text: String)=tokens(text).map { normalize(kind,it) }.distinct().also { require(it.size<=LIMIT) { "Use at most 500 targets across site groups." } }
    fun normalize(kind: String,input: String): String {
        if(kind=="Domain") {
            require(input.length<=253 && input.matches(Regex("[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])?")) &&
                input.split('.').all { it.length in 1..63 && !it.startsWith('-') && !it.endsWith('-') } && !input.matches(Regex("[0-9.]+"))) { "Enter a domain without scheme, path, IP or wildcard." }
            return input.lowercase(Locale.ROOT)
        }
        require(kind=="IP") { "Choose Domain or IP." }
        var value=input
        if('*' in value) {
            val octets=value.split('.');val first=octets.indexOf("*")
            require(octets.size==4 && first>=0 && octets.drop(first).all { it=="*" } && octets.take(first).all { it.matches(Regex("[0-9]{1,3}")) && it.toInt()<=255 }) { "IPv4 wildcards must be trailing octets, such as 2.2.2.* or 10.*.*.*." }
            value=(octets.take(first).map { it.toInt().toString() }+List(4-first){"0"}).joinToString(".")+"/${first*8}"
        }
        val parts=value.split('/')
        require(parts.size<=2) { "Invalid IP / CIDR." }
        val literal=parts[0]
        val bytes: ByteArray = if(':' in literal) {
            require(literal.matches(Regex("[0-9a-fA-F:.]+"))) { "Invalid IPv6 address." }
            try { URI("http://[$literal]/");val parsed=InetAddress.getByName(literal).address; if(parsed.size==4) ByteArray(10)+byteArrayOf(-1,-1)+parsed else parsed }
            catch(_: Exception) { throw IllegalArgumentException("Invalid IPv6 address.") }
        } else {
            val octets=literal.split('.')
            require(octets.size==4 && octets.all { it.matches(Regex("[0-9]{1,3}")) && it.toInt()<=255 }) { "Enter an IPv4/IPv6 address, CIDR or trailing IPv4 wildcard." }
            octets.map { it.toInt().toByte() }.toByteArray()
        }
        val bits=bytes.size*8
        val prefix=if(parts.size==1)bits else {
            require(parts[1].matches(Regex("[0-9]{1,3}"))) { "Invalid CIDR prefix." };parts[1].toInt()
        }
        require(prefix in 0..bits) { "Invalid CIDR prefix." }
        for(i in bytes.indices) { val keep=(prefix-i*8).coerceIn(0,8);bytes[i]=(bytes[i].toInt() and (0xff shl (8-keep))).toByte() }
        val host=if(bytes.size==4)bytes.joinToString("."){(it.toInt() and 255).toString()} else (0..7).joinToString(":"){(((bytes[2*it].toInt() and 255) shl 8) or (bytes[2*it+1].toInt() and 255)).toString(16)}
        return "$host/$prefix"
    }
    fun validate(policy: Policy) {
        require(policy.rules.size<=LIMIT) { "Use at most 500 site groups." }
        val unique=mutableSetOf<String>();var count=0
        policy.rules.forEach { rule ->
            require(rule.action!=Action.Disabled && rule.name.length<=80 && rule.name.none { it.isISOControl() }) { "Invalid action or site name (maximum 80 characters)." }
            parse(rule.kind,rule.target).forEach { target -> require(unique.add("${rule.kind}:$target")) { "A site target already exists in another group." };count++ }
        }
        require(count<=LIMIT) { "Use at most 500 targets across site groups." }
    }
}
