plugins { id("com.android.application") }
val baseVersion = rootProject.file("../../../client/VERSION").readText().trim()
val versionParts = baseVersion.split('.').map(String::toInt)
android {
    namespace = "dev.personalvpn.p2plab"
    compileSdk = 35
    buildToolsVersion = "35.0.0"
    defaultConfig {
        applicationId = "dev.personalvpn.p2plab"
        minSdk = 34; targetSdk = 35
        versionCode = versionParts[0] * 1000000 + versionParts[1] * 10000 + versionParts[2] * 100 + 1
        versionName = "$baseVersion-lab.1"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }
    signingConfigs {
        create("lab") {
            storeFile = file(System.getenv("P2P_LAB_KEYSTORE") ?: "missing-lab.p12")
            storeType = "PKCS12"; storePassword = "p2plab-test-only"
            keyAlias = "p2plab"; keyPassword = "p2plab-test-only"
        }
    }
    buildTypes { release { signingConfig = signingConfigs.getByName("lab"); isMinifyEnabled = false; isDebuggable = false } }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
}
dependencies {
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
}
