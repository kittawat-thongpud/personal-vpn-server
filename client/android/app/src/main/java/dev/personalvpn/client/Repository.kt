package dev.personalvpn.client

import android.app.Application
import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.AtomicFile
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

// An exact fixture trust anchor, used only by the instrumentation process.
internal fun fixtureTrust(pem: String): javax.net.ssl.SSLSocketFactory {
    val certificate = java.security.cert.CertificateFactory.getInstance("X.509").generateCertificate(pem.byteInputStream())
    val store = KeyStore.getInstance(KeyStore.getDefaultType()).apply { load(null); setCertificateEntry("fixture", certificate) }
    val managers = javax.net.ssl.TrustManagerFactory.getInstance(javax.net.ssl.TrustManagerFactory.getDefaultAlgorithm()).apply { init(store) }
    return javax.net.ssl.SSLContext.getInstance("TLS").apply { init(null, managers.trustManagers, null) }.socketFactory
}

class VpnApplication : Application() { override fun onCreate() { super.onCreate(); Repository.initialize(this) } }
class SecureStore(context: Context) {
    private val file = AtomicFile(File(context.noBackupFilesDir, "profiles.enc"))
    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        return (store.getKey("personal-vpn-profiles-v1", null) as? SecretKey) ?: KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore").apply { init(KeyGenParameterSpec.Builder("personal-vpn-profiles-v1", KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build()) }.generateKey()
    }
    @Synchronized fun load(): Config {
        if (!file.baseFile.exists()) return Config()
        val bytes = file.readFully(); require(bytes.size in 29..(2 * 1024 * 1024))
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, bytes.copyOfRange(0, 12))) }
        return Config.parse(String(cipher.doFinal(bytes.copyOfRange(12, bytes.size)), Charsets.UTF_8))
    }
    @Synchronized fun save(config: Config) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val plain = config.json().toString().toByteArray(Charsets.UTF_8)
        require(plain.size <= 2 * 1024 * 1024 - 28) { "Settings size limit reached." }
        val encrypted = cipher.iv + cipher.doFinal(plain)
        val output = file.startWrite()
        try { output.write(encrypted); file.finishWrite(output) } catch (e: Exception) { file.failWrite(output); throw e }
    }
}
data class BackendEvent(val time: Long, val level: String, val code: String, val detail: String)
class EventRing {
    private val values = ArrayDeque<BackendEvent>(); private var bytes = 0
    @Synchronized fun add(level: String, code: String, detail: String): List<BackendEvent> {
        val event = BackendEvent(System.currentTimeMillis(), level.take(8), code.take(80), detail.take(1024))
        values.addLast(event); bytes += size(event)
        while (values.size > 1000 || bytes > 1024 * 1024) bytes -= size(values.removeFirst())
        return values.toList()
    }
    private fun size(event: BackendEvent) = event.level.toByteArray().size + event.code.toByteArray().size + event.detail.toByteArray().size + 64
    @Synchronized fun clear() { values.clear(); bytes = 0 }
}
data class TunnelState(val status: String = "Disconnected", val active: Boolean = false, val busy: Boolean = false, val exitIp: String = "—", val detail: String = "", val appliedConfig: String = "", val profileName: String = "", val protocol: String = "", val uploadRate: Long = 0, val downloadRate: Long = 0, val uploaded: Long = 0, val downloaded: Long = 0, val history: List<Pair<Long, Long>> = emptyList())
object Repository {
    lateinit var controlToken: String; private set
    // Enabled only by the isolated instrumentation APK with generated fixtures.
    // There is no UI or external command that enables raw exception details.
    @Volatile internal var instrumentedDiagnostics = false
    @Volatile internal var instrumentedCertificate: String? = null
    lateinit var context: Context; private lateinit var store: SecureStore
    private val settings = MutableStateFlow(Config()); val config = settings.asStateFlow()
    val state = MutableStateFlow(TunnelState())
    private val events = MutableStateFlow<List<BackendEvent>>(emptyList()); val logs = events.asStateFlow()
    private val ring = EventRing()
    fun initialize(context: Context) { this.context = context.applicationContext; val control = this.context.getSharedPreferences("private-control", Context.MODE_PRIVATE); controlToken = control.getString("token", null) ?: java.util.UUID.randomUUID().toString().also { control.edit().putString("token", it).commit() }; store = SecureStore(this.context); try { settings.value = store.load() } catch (e: Exception) { log("ERROR", "settings.read_failed", e.javaClass.simpleName); state.value = TunnelState(detail = "Stored settings could not be decrypted. The original file was preserved.") } }
    @Synchronized fun save(config: Config) { store.save(config); settings.value = config }
    @Synchronized fun log(level: String, code: String, detail: String = "") { events.value = ring.add(level, code, detail) }
    @Synchronized fun clearLogs() { ring.clear(); events.value = emptyList() }
    fun cause(error: Throwable): String {
        if (instrumentedDiagnostics) return error.javaClass.simpleName + ": " + error.message.orEmpty().take(800)
        return BackendFailures.reason(error)
    }
}
