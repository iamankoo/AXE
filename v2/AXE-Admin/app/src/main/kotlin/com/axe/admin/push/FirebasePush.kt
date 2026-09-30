package com.axe.admin.push

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.os.Build
import com.axe.admin.AxeAdminApp
import com.axe.admin.auth.SessionExpiredException
import com.axe.admin.auth.SessionManager
import com.axe.admin.network.AdminApi
import com.axe.admin.network.ApiException
import com.axe.admin.network.NetworkException
import com.google.android.gms.tasks.Task
import com.google.firebase.FirebaseApp
import com.google.firebase.FirebaseOptions
import com.google.firebase.messaging.FirebaseMessaging
import com.google.firebase.messaging.FirebaseMessagingService
import com.google.firebase.messaging.RemoteMessage
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlin.coroutines.resume
import kotlin.coroutines.resumeWithException

private suspend fun <T> Task<T>.await(): T = suspendCancellableCoroutine { continuation ->
    addOnSuccessListener { continuation.resume(it) }
    addOnFailureListener { continuation.resumeWithException(it) }
}

/**
 * Firebase is initialized from client-safe identifiers (see [PushConfig]) instead of the google-services plugin, so the
 * app builds and runs without a Firebase project. Returns false when push is not configured.
 */
object FirebaseSetup {
    fun initialize(context: Context, config: PushConfig): Boolean {
        if (!config.isConfigured) return false
        if (FirebaseApp.getApps(context).isEmpty()) {
            FirebaseApp.initializeApp(
                context,
                FirebaseOptions.Builder()
                    .setProjectId(config.projectId)
                    .setApplicationId(config.appId)
                    .setApiKey(config.apiKey)
                    .setGcmSenderId(config.senderId)
                    .build(),
            )
        }
        return true
    }
}

object NotificationChannels {
    /** Must match `channel_id` in the backend's FCM message and the manifest default. */
    const val REQUESTS = "axe_requests"

    fun create(context: Context) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(REQUESTS, "New requests", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Tells you when a new payment or invitation request needs review."
            },
        )
    }
}

class FirebaseTokenSource : PushTokenSource {
    override suspend fun currentToken(): String? =
        runCatching { FirebaseMessaging.getInstance().token.await() }.getOrNull()?.takeIf { it.isNotBlank() }

    override suspend fun deleteToken() {
        FirebaseMessaging.getInstance().deleteToken().await()
    }
}

/** Device tokens go to the authenticated admin API (owner = the signed-in admin, decided by the server). */
class BackendDeviceRepository(
    private val sessions: SessionManager,
    private val api: AdminApi,
) : DeviceBackend {
    override suspend fun register(token: String, previousToken: String?) =
        call { api.registerDevice(it, token, previousToken) }

    override suspend fun unregister(token: String) = call { api.unregisterDevice(it, token) }

    private suspend fun call(block: suspend (accessToken: String) -> Unit): DeviceResult = try {
        sessions.authorized(block)
        DeviceResult.Ok
    } catch (_: SessionExpiredException) {
        DeviceResult.NotSignedIn
    } catch (_: NetworkException) {
        DeviceResult.NetworkError
    } catch (_: ApiException) {
        DeviceResult.Error
    }
}

class SharedPreferencesPushStore(context: Context) : PushStore {
    private val prefs = context.applicationContext.getSharedPreferences("axe_admin_push", Context.MODE_PRIVATE)

    override var lastRegisteredToken: String?
        get() = prefs.getString("registered", null)
        set(value) { prefs.edit().putString("registered", value).apply() }

    override var lastRegisteredAtMs: Long
        get() = prefs.getLong("registeredAt", 0)
        set(value) { prefs.edit().putLong("registeredAt", value).apply() }

    override var pendingUnregister: String?
        get() = prefs.getString("pendingUnregister", null)
        set(value) { prefs.edit().putString("pendingUnregister", value).apply() }

    override var permissionAsked: Boolean
        get() = prefs.getBoolean("permissionAsked", false)
        set(value) { prefs.edit().putBoolean("permissionAsked", value).apply() }
}

/**
 * Receives token rotations (always) and messages (only while the app is in the foreground: in the background the system
 * shows the notification itself and the app is told nothing until it is tapped). It never trusts message content: it
 * accepts only a known type and a well-formed request id, and the UI then fetches the real request from the backend.
 */
class AxeMessagingService : FirebaseMessagingService() {
    override fun onNewToken(token: String) {
        (application as AxeAdminApp).container.onNewPushToken(token)
    }

    override fun onMessageReceived(message: RemoteMessage) {
        val target = PushPayload.parse(message.data[PushPayload.KEY_TYPE], message.data[PushPayload.KEY_REQUEST_ID]) ?: return
        (application as AxeAdminApp).container.pushEvents.newRequest(target.requestId)
    }
}
