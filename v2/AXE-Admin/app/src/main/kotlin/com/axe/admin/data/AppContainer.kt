package com.axe.admin.data

import android.content.Context
import com.axe.admin.BuildConfig
import com.axe.admin.auth.AesGcmCipher
import com.axe.admin.auth.EncryptedSessionStore
import com.axe.admin.auth.KeystoreKeyProvider
import com.axe.admin.auth.SessionManager
import com.axe.admin.auth.SharedPreferencesBlobStorage
import com.axe.admin.network.AdminApi
import com.axe.admin.network.AuthApi
import com.axe.admin.network.BackendConfig
import com.axe.admin.network.HttpTransport
import com.axe.admin.repository.AdminRepository
import com.axe.admin.repository.AuthRepository
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

    val authRepository = AuthRepository(sessions, authApi, adminApi)
    val adminRepository = AdminRepository(sessions, adminApi)
}
