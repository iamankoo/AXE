import groovy.json.JsonSlurper
import java.util.Properties

plugins {
    alias(libs.plugins.android.application)
    alias(libs.plugins.kotlin.android)
    alias(libs.plugins.kotlin.compose)
    alias(libs.plugins.kotlin.serialization)
}

// ---- Client-safe backend configuration --------------------------------------------------------
// Only the Supabase project URL and the PUBLIC anon key are read here. Source, in order:
//   1. AXE-Admin/local.properties  (axe.supabaseUrl / axe.anonKey)         - git-ignored
//   2. v2/config/server.json       (derived from functionsUrl / anonKey)   - git-ignored
// Neither file is committed. Secrets (service-role key, FCM credentials, signing keys) are never
// read by this build and never belong in the app.
val localProps = Properties().apply {
    rootProject.file("local.properties").takeIf { it.exists() }?.inputStream()?.use { load(it) }
}
var supabaseUrl: String = localProps.getProperty("axe.supabaseUrl", "").trim()
var anonKey: String = localProps.getProperty("axe.anonKey", "").trim()
if (supabaseUrl.isEmpty() || anonKey.isEmpty()) {
    val serverJson = rootProject.file("../config/server.json")
    if (serverJson.exists()) {
        @Suppress("UNCHECKED_CAST")
        val parsed = JsonSlurper().parse(serverJson) as Map<String, Any?>
        if (supabaseUrl.isEmpty()) {
            supabaseUrl = (parsed["functionsUrl"] as? String).orEmpty().removeSuffix("/").removeSuffix("/functions/v1")
        }
        if (anonKey.isEmpty()) anonKey = (parsed["anonKey"] as? String).orEmpty()
    }
}
fun String.asBuildConfigString() = "\"" + replace("\\", "\\\\").replace("\"", "\\\"") + "\""

android {
    namespace = "com.axe.admin"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.axe.admin"
        minSdk = 26
        targetSdk = 36
        versionCode = 1
        versionName = "0.1.0-phase1"
        buildConfigField("String", "SUPABASE_URL", supabaseUrl.asBuildConfigString())
        buildConfigField("String", "SUPABASE_ANON_KEY", anonKey.asBuildConfigString())
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    kotlinOptions { jvmTarget = "17" }
    buildFeatures {
        compose = true
        buildConfig = true
    }
    sourceSets["main"].java.srcDir("src/main/kotlin")
    sourceSets["test"].java.srcDir("src/test/kotlin")
    packaging { resources.excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    lint { abortOnError = true }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(platform(libs.compose.bom))
    implementation(libs.compose.ui)
    implementation(libs.compose.ui.tooling.preview)
    implementation(libs.compose.material3)
    implementation(libs.okhttp)
    implementation(libs.kotlinx.serialization.json)
    implementation(libs.kotlinx.coroutines.android)
    debugImplementation(libs.compose.ui.tooling)

    testImplementation(libs.junit)
    testImplementation(libs.okhttp.mockwebserver)
    testImplementation(libs.kotlinx.coroutines.test)
}
