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

// ---- Client-safe Firebase configuration (push notifications) -----------------------------------
// These four values identify the Firebase app to the FCM client SDK. They are PUBLIC client identifiers (they ship in
// every Firebase Android app), NOT credentials: the FCM server credential (a service-account key) lives only in the
// backend secret store and is never read here. Source, in order:
//   1. AXE-Admin/local.properties  (axe.firebase.projectId / appId / apiKey / senderId)  - git-ignored
//   2. AXE-Admin/app/google-services.json (as downloaded from the Firebase console)       - git-ignored
// If neither exists the app builds and works normally; push notifications are simply off.
var fbProjectId = localProps.getProperty("axe.firebase.projectId", "").trim()
var fbAppId = localProps.getProperty("axe.firebase.appId", "").trim()
var fbApiKey = localProps.getProperty("axe.firebase.apiKey", "").trim()
var fbSenderId = localProps.getProperty("axe.firebase.senderId", "").trim()
if (fbProjectId.isEmpty() || fbAppId.isEmpty() || fbApiKey.isEmpty() || fbSenderId.isEmpty()) {
    val services = project.file("google-services.json")
    if (services.exists()) {
        @Suppress("UNCHECKED_CAST")
        val json = JsonSlurper().parse(services) as Map<String, Any?>
        @Suppress("UNCHECKED_CAST")
        val info = json["project_info"] as? Map<String, Any?> ?: emptyMap()
        @Suppress("UNCHECKED_CAST")
        val clients = json["client"] as? List<Map<String, Any?>> ?: emptyList()
        val mine = clients.firstOrNull { c ->
            @Suppress("UNCHECKED_CAST")
            val ci = c["client_info"] as? Map<String, Any?> ?: emptyMap()
            @Suppress("UNCHECKED_CAST")
            val a = ci["android_client_info"] as? Map<String, Any?> ?: emptyMap()
            a["package_name"] == "com.axe.admin"
        }
        if (mine != null) {
            @Suppress("UNCHECKED_CAST")
            val ci = mine["client_info"] as Map<String, Any?>
            @Suppress("UNCHECKED_CAST")
            val keys = mine["api_key"] as? List<Map<String, Any?>> ?: emptyList()
            if (fbProjectId.isEmpty()) fbProjectId = (info["project_id"] as? String).orEmpty()
            if (fbSenderId.isEmpty()) fbSenderId = (info["project_number"] as? String).orEmpty()
            if (fbAppId.isEmpty()) fbAppId = (ci["mobilesdk_app_id"] as? String).orEmpty()
            if (fbApiKey.isEmpty()) fbApiKey = (keys.firstOrNull()?.get("current_key") as? String).orEmpty()
        }
    }
}
// A RELEASE must never be built with development configuration: it fails closed instead of shipping a local server or
// placeholder Firebase identifiers. (Debug builds are unaffected.)
gradle.taskGraph.whenReady {
    val releaseBuild = allTasks.any { t ->
        t.path.startsWith(":app:") && (t.name.startsWith("assembleRelease") || t.name.startsWith("bundleRelease") || t.name.startsWith("packageRelease"))
    }
    if (releaseBuild) {
        val problems = buildList {
            if (!supabaseUrl.startsWith("https://")) add("the Supabase URL is not https (axe.supabaseUrl / ../config/server.json)")
            if (Regex("(?i)localhost|127\\.0\\.0\\.1|10\\.0\\.2\\.2|host\\.docker\\.internal").containsMatchIn(supabaseUrl)) add("the Supabase URL points at this machine")
            if (anonKey.isBlank()) add("no Supabase anon key is configured")
            if (fbProjectId.contains("fake", ignoreCase = true) || fbApiKey.contains("FAKE", ignoreCase = true)) add("the Firebase identifiers are development placeholders")
        }
        if (problems.isNotEmpty()) {
            throw GradleException("Refusing to build a RELEASE with development configuration: " + problems.joinToString("; ") + ". See v2/PRODUCTION.md.")
        }
        if (keystoreProps.getProperty("storeFile") == null) {
            logger.warn("WARNING: no keystore.properties: the release APK will be UNSIGNED and cannot be installed. See v2/PRODUCTION.md.")
        }
    }
}

fun String.asBuildConfigString() = "\"" + replace("\\", "\\\\").replace("\"", "\\\"") + "\""

// ---- Release signing (optional, secret, never committed) ---------------------------------------
// keystore.properties (git-ignored) holds: storeFile, storePassword, keyAlias, keyPassword. When it exists the release
// build is signed with it; when it does not, release builds are UNSIGNED (they cannot be installed) and say so.
val keystoreProps = Properties().apply {
    rootProject.file("keystore.properties").takeIf { it.exists() }?.inputStream()?.use { load(it) }
}

android {
    namespace = "com.axe.admin"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.axe.admin"
        minSdk = 26
        targetSdk = 36
        versionCode = 2
        versionName = "2.0.0"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
        buildConfigField("String", "SUPABASE_URL", supabaseUrl.asBuildConfigString())
        buildConfigField("String", "SUPABASE_ANON_KEY", anonKey.asBuildConfigString())
        buildConfigField("String", "FIREBASE_PROJECT_ID", fbProjectId.asBuildConfigString())
        buildConfigField("String", "FIREBASE_APP_ID", fbAppId.asBuildConfigString())
        buildConfigField("String", "FIREBASE_API_KEY", fbApiKey.asBuildConfigString())
        buildConfigField("String", "FIREBASE_SENDER_ID", fbSenderId.asBuildConfigString())
    }

    signingConfigs {
        if (keystoreProps.getProperty("storeFile") != null) {
            create("release") {
                storeFile = rootProject.file(keystoreProps.getProperty("storeFile"))
                storePassword = keystoreProps.getProperty("storePassword")
                keyAlias = keystoreProps.getProperty("keyAlias")
                keyPassword = keystoreProps.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            signingConfigs.findByName("release")?.let { signingConfig = it }
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
    sourceSets["androidTest"].java.srcDir("src/androidTest/kotlin")
    packaging { resources.excludes += "/META-INF/{AL2.0,LGPL2.1}" }
    lint { abortOnError = true }
}

dependencies {
    implementation(libs.androidx.core.ktx)
    implementation(libs.androidx.activity.compose)
    implementation(libs.androidx.lifecycle.viewmodel.compose)
    implementation(libs.androidx.lifecycle.runtime.compose)
    implementation(libs.androidx.lifecycle.process)
    implementation(platform(libs.firebase.bom))
    implementation(libs.firebase.messaging)
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

    androidTestImplementation(libs.androidx.test.runner)
    androidTestImplementation(libs.androidx.test.ext.junit)
}
