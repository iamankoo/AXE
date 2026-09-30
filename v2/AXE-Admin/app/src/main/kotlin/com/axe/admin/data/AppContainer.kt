package com.axe.admin.data

import android.content.Context
import android.graphics.BitmapFactory
import android.os.Build
import com.axe.admin.BuildConfig
import com.axe.admin.auth.AesGcmCipher
import com.axe.admin.auth.EncryptedSessionStore
import com.axe.admin.auth.KeystoreKeyProvider
import com.axe.admin.auth.SessionManager
import com.axe.admin.auth.SharedPreferencesBlobStorage
import com.axe.admin.model.AuthState
import com.axe.admin.network.AdminApi
import com.axe.admin.network.AuthApi
import com.axe.admin.network.BackendConfig
import com.axe.admin.network.HttpTransport
import com.axe.admin.repository.AdminRepository
import com.axe.admin.repository.AuthRepository
import com.axe.admin.repository.BackendRequestRepository
import com.axe.admin.push.BackendDeviceRepository
import com.axe.admin.push.DeepLinks
import com.axe.admin.push.FirebaseSetup
import com.axe.admin.push.FirebaseTokenSource
import com.axe.admin.push.NotificationChannels
import com.axe.admin.push.NotificationPermissionPolicy
import com.axe.admin.push.PushConfig
import com.axe.admin.push.PushEvents
import com.axe.admin.push.PushRegistrar
import com.axe.admin.push.SharedPreferencesPushStore
import com.axe.admin.repository.RequestRepository
import com.axe.admin.viewmodel.ImageValidator
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.filterIsInstance
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import java.util.concurrent.TimeUnit

/** Manual dependency wiring. No logging interceptor is installed, by design: no traffic is ever logged. */
class AppContainer(context: Context) {
    val config = BackendConfig(BuildConfig.SUPABASE_URL, BuildConfig.SUPABASE_ANON_KEY)

    private val httpClient = OkHttpClient.Builder()
        .connectTimeout(10, TimeUnit.SECONDS)
        .readTimeout(20, TimeUnit.SECONDS)
        .writeTimeout(20, TimeUnit.SECONDS)
        .build() // default system trust store and hostname verification; nothing is relaxed

    private val transport = HttpTransport(httpClient)
    private val authApi = AuthApi(config, transport)
    private val adminApi = AdminApi(config, transport)

    private val sessionStore = EncryptedSessionStore(
        SharedPreferencesBlobStorage(context),
        AesGcmCipher(KeystoreKeyProvider::key),
    )
    private val sessions = SessionManager(sessionStore, authApi)

    // ---- push notifications (FCM). Only client-safe identifiers are configured here; the FCM server credential is backend-only.
    val pushConfig = PushConfig(BuildConfig.FIREBASE_PROJECT_ID, BuildConfig.FIREBASE_APP_ID, BuildConfig.FIREBASE_API_KEY, BuildConfig.FIREBASE_SENDER_ID)
    val pushEvents = PushEvents()
    val deepLinks = DeepLinks()
    private val pushStore = SharedPreferencesPushStore(context)
    val notificationPermission = NotificationPermissionPolicy(pushStore, Build.VERSION.SDK_INT)
    private val appScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val pushRegistrar = PushRegistrar(
        FirebaseTokenSource(), pushStore, BackendDeviceRepository(sessions, adminApi), pushConfig.isConfigured,
    )

    val authRepository = AuthRepository(sessions, authApi, adminApi, beforeSignOut = pushRegistrar::onSigningOut)
    val adminRepository = AdminRepository(sessions, adminApi)
    val requestRepository: RequestRepository = BackendRequestRepository(sessions, adminApi)
    val imageValidator = ImageValidator(::isDecodableImage)

    /** Creates the notification channel and, if Firebase is configured, registers the device while an admin is signed in. */
    fun startPush(context: Context) {
        NotificationChannels.create(context)
        if (!FirebaseSetup.initialize(context, pushConfig)) return
        appScope.launch {
            authRepository.state.filterIsInstance<AuthState.SignedIn>().collect { pushRegistrar.onSignedIn() }
        }
    }

    /** FCM rotated this install's token. Registered at once if an admin is signed in, otherwise at the next sign-in. */
    fun onNewPushToken(token: String) {
        appScope.launch { pushRegistrar.onNewToken(token, authRepository.state.value is AuthState.SignedIn) }
    }
}

/** Reads only the image header (no pixels are allocated), enough to reject garbage or truncated headers. */
private fun isDecodableImage(bytes: ByteArray): Boolean {
    val options = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)
    return options.outWidth > 0 && options.outHeight > 0
}
