package com.axe.admin.push

import com.axe.admin.auth.Clock
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * Client-safe Firebase identifiers (public app identifiers, not credentials). Empty when the build has no Firebase
 * config, in which case push notifications are off and everything else works as normal.
 */
data class PushConfig(val projectId: String, val appId: String, val apiKey: String, val senderId: String) {
    val isConfigured: Boolean
        get() = projectId.isNotBlank() && appId.isNotBlank() && apiKey.isNotBlank() && senderId.isNotBlank()
}

/** The only thing a notification is allowed to tell the app: a new request exists, and its id. */
data class PushTarget(val requestId: String)

/**
 * Parses the data of a push message or of the launch intent of a tapped notification. The payload is untrusted input
 * (any app can send an intent to an exported activity): only a known type and a well-formed UUID are accepted, and even
 * then the id is only ever used to ask the authenticated backend for the real request.
 */
object PushPayload {
    const val KEY_TYPE = "type"
    const val KEY_REQUEST_ID = "requestId"
    const val TYPE_NEW_REQUEST = "new_request"

    private val UUID = Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")

    fun parse(type: String?, requestId: String?): PushTarget? {
        if (type != TYPE_NEW_REQUEST) return null
        val id = requestId?.trim()?.lowercase() ?: return null
        return if (UUID.matches(id)) PushTarget(id) else null
    }
}

/** Live "a new request arrived" signals from FCM while the app is in the foreground (request ids only). */
class PushEvents {
    private val _newRequests = MutableSharedFlow<String>(extraBufferCapacity = 16)
    val newRequests: SharedFlow<String> = _newRequests.asSharedFlow()

    fun newRequest(requestId: String) {
        _newRequests.tryEmit(requestId)
    }
}

/**
 * A notification tap waiting to be shown. Held in memory only (never persisted): if the admin is signed out the link
 * waits for the next sign-in, and it expires after [MAX_AGE_MS] so a very old tap cannot surprise anyone later.
 */
class DeepLinks(private val clock: Clock = Clock.System) {
    private data class Pending(val requestId: String, val atMs: Long)

    private val _pending = MutableStateFlow<Pending?>(null)

    /** Emits whenever a link is waiting (the value is not the id: use [take]). */
    val hasPending: StateFlow<Boolean> get() = _hasPending
    private val _hasPending = MutableStateFlow(false)

    fun offer(requestId: String) {
        _pending.value = Pending(requestId, clock.nowMs())
        _hasPending.value = true
    }

    /** Returns the waiting request id (if still fresh) and clears it, so it is handled exactly once. */
    fun take(): String? {
        val p = _pending.value
        _pending.value = null
        _hasPending.value = false
        return p?.takeIf { clock.nowMs() - it.atMs <= MAX_AGE_MS }?.requestId
    }

    companion object {
        const val MAX_AGE_MS = 10 * 60_000L
    }
}

/** Decides when to ask for the Android 13+ notification permission: at most once, never repeatedly. */
class NotificationPermissionPolicy(private val store: PushStore, private val sdkInt: Int) {
    val requiresRuntimePermission: Boolean get() = sdkInt >= 33

    fun shouldAskNow(granted: Boolean): Boolean =
        requiresRuntimePermission && !granted && !store.permissionAsked

    fun markAsked() {
        store.permissionAsked = true
    }
}
