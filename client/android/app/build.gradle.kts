plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.android")
    id("org.jetbrains.kotlin.plugin.compose")
}
val clientVersion = rootProject.file("../VERSION").readText().trim()
val versionParts = clientVersion.split('.').map(String::toInt)
require(versionParts.size == 3 && versionParts.all { it in 0..99 })
android {
    namespace = "dev.personalvpn.client"
    compileSdk = 35
    buildToolsVersion = "35.0.0"
    defaultConfig {
        applicationId = "dev.personalvpn.client"
        minSdk = 29
        targetSdk = 35
        versionCode = versionParts[0] * 1000000 + versionParts[1] * 10000 + versionParts[2] * 100
        versionName = clientVersion
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        ndk { abiFilters += listOf("arm64-v8a", "x86_64") }
    }
    signingConfigs {
        create("release") {
            storeFile = file(System.getenv("ANDROID_KEYSTORE") ?: "missing-signing.p12")
            storeType = "PKCS12"
            storePassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
            keyAlias = "personal-vpn"
            keyPassword = System.getenv("ANDROID_KEYSTORE_PASSWORD")
        }
    }
    buildTypes {
        release { signingConfig = signingConfigs.getByName("release"); isMinifyEnabled = false; isDebuggable = false }
    }
    buildFeatures { compose = true; buildConfig = true }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
    packaging { jniLibs { useLegacyPackaging = true } }
    testOptions { unitTests.isReturnDefaultValues = true }
}
dependencies {
    implementation(files("libs/personalvpn.aar"))
    implementation(platform("androidx.compose:compose-bom:2025.04.01"))
    implementation("androidx.activity:activity-compose:1.10.1")
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.lifecycle:lifecycle-runtime-compose:2.9.0")
    implementation("androidx.core:core-ktx:1.16.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    testImplementation("junit:junit:4.13.2")
    testImplementation("org.json:json:20240303")
    androidTestImplementation("androidx.test:runner:1.6.2")
    androidTestImplementation("androidx.test.ext:junit:1.2.1")
    androidTestImplementation("androidx.test.uiautomator:uiautomator:2.3.0")
}
