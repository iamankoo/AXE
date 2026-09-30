package com.axe.admin.push

import com.axe.admin.auth.Clock

/** Outcome of telling the backend about this device. */
enum class DeviceResult { Ok, NetworkError, NotSignedIn, Error }

/** The authenticated backend calls for device tokens. The token's owner is decided by the server from the session. */
interface DeviceBackend {
    suspend fun register(token: String, previousToken: String?): DeviceResult
    suspend fun unregister(token: String): DeviceResult
}

/** Where FCM registration tokens come from (Firebase in the app, a fake in tests). */
interface PushTokenSource {
    suspend fun currentToken(): String?

    /** Invalidates this install's token at FCM, so nothing can be pushed to it any more. */
    suspend fun deleteToken()
}

/** Small local state about push registration (app-private). The FCM token is not a credential: it cannot send anything. */
interface PushStore {
    var lastRegisteredToken: String?
    var lastRegisteredAtMs: Long

    /** A token whose server registration could not be removed at sign-out; removed at the next sign-in. */
    var pendingUnregister: String?

    /** The notification permission has been requested once already. */
    var permissionAsked: Boolean
}

/**
 * The lifecycle of this device's push token, always tied to authentication:
 *  * a device is registered only while an admin is signed in (an unauthenticated install is never a push target);
 *  * a rotated token replaces the old one (the server deletes the predecessor);
 *  * sign-out removes the registration and invalidates the token, and a failed removal is retried later.
 *
 * Every call is best effort: a failure never throws, it just leaves the state to be healed at the next opportunity.
 */
class PushRegistrar(
    private val tokens: PushTokenSource,
    private val store: PushStore,
    private val backend: DeviceBackend,
    private val configured: Boolean,
    private val clock: Clock = Clock.System,
) {
    /** Call when an admin is signed in (sign-in, or app start with a restored session). */
    suspend fun onSignedIn() {
        if (!configured) return
        retryPendingUnregister()
        val token = tokens.currentToken() ?: return
        val last = store.lastRegisteredToken
        val fresh = last == token && clock.nowMs() - store.lastRegisteredAtMs < REFRESH_INTERVAL_MS
        if (fresh) return
        register(token, previous = last)
    }

    /** FCM issued a new token. Registered at once if signed in; otherwise it is fetched at the next sign-in. */
    suspend fun onNewToken(token: String, signedIn: Boolean) {
        if (!configured || !signedIn) return
        if (token == store.lastRegisteredToken) return
        register(token, previous = store.lastRegisteredToken)
    }

    /** Call BEFORE the session is cleared, while the server can still authenticate the removal. */
    suspend fun onSigningOut() {
        if (!configured) return
        val token = store.lastRegisteredToken
        if (token != null && backend.unregister(token) != DeviceResult.Ok) {
            store.pendingUnregister = token
        }
        runCatching { tokens.deleteToken() } // the old token can no longer receive anything, even if the server call failed
        store.lastRegisteredToken = null
        store.lastRegisteredAtMs = 0
    }

    private suspend fun register(token: String, previous: String?) {
        val result = backend.register(token, previous?.takeIf { it != token })
        if (result == DeviceResult.Ok) {
            store.lastRegisteredToken = token
            store.lastRegisteredAtMs = clock.nowMs()
        }
    }

    private suspend fun retryPendingUnregister() {
        val pending = store.pendingUnregister ?: return
        if (backend.unregister(pending) == DeviceResult.Ok) store.pendingUnregister = null
    }

    companion object {
        /** Re-register at least this often so a server-side clean-up (stale token, device cap) heals itself. */
        const val REFRESH_INTERVAL_MS = 24 * 60 * 60_000L
    }
}
