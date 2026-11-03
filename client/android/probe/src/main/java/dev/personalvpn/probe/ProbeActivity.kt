package dev.personalvpn.probe
import android.app.Activity
import android.os.Bundle
import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import javax.net.ssl.HttpsURLConnection

class ProbeActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val udp = intent.getBooleanExtra("udp", false)
        Thread {
            val result = try {
                if (udp) {
                    DatagramSocket().use { socket -> socket.soTimeout = 7000; val data = "personal-vpn-udp".toByteArray(); socket.send(DatagramPacket(data, data.size, InetAddress.getByName("198.18.0.1"), 19000)); val response = DatagramPacket(ByteArray(64), 64); socket.receive(response); check(String(response.data, 0, response.length) == String(data)) }
                    JSONObject().put("success", true).put("status", 200)
                } else {
                    val c = java.net.URL("https://www.google.com/generate_204").openConnection() as HttpsURLConnection
                    c.connectTimeout = 7000; c.readTimeout = 7000; c.useCaches = false
                    try { val status = c.responseCode; JSONObject().put("success", status == 204).put("status", status) } finally { c.disconnect() }
                }
            } catch (e: Exception) { JSONObject().put("success", false).put("error", e.javaClass.simpleName) }
            java.io.File(filesDir, "result.json").writeText(result.toString())
            runOnUiThread { finish() }
        }.start()
    }
}
