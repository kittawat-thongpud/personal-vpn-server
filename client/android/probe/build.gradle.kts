plugins { id("com.android.application"); id("org.jetbrains.kotlin.android") }
android {
    namespace = "dev.personalvpn.probe"
    compileSdk = 35
    defaultConfig { applicationId = "dev.personalvpn.probe"; minSdk = 29; targetSdk = 35; versionCode = 1; versionName = "test-only" }
    compileOptions { sourceCompatibility = JavaVersion.VERSION_17; targetCompatibility = JavaVersion.VERSION_17 }
    kotlinOptions { jvmTarget = "17" }
}
