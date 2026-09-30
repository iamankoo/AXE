package com.axe.admin.auth

import com.axe.admin.model.AuthState
import com.axe.admin.model.Session
import com.axe.admin.model.SignOutReason
import com.axe.admin.network.AuthApi
import com.axe.admin.network.RefreshRejectedException
import com.axe.admin.network.TokenResponse
import com.axe.admin.network.UnauthorizedException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

fun interface Clock {
    fun nowMs(): Long

    companion object {
        val System = Clock { java.lang.System.currentTimeMillis() }
    }
}

/** The session is over (refresh rejected, or the server revoked admin access). The state is already SignedOut. */
class SessionExpiredException : Exception("Session expired")

/**
 * Owns the current admin session: restore on start, refresh, sign-out, and the [AuthState] the UI observes.
 *
 * Refreshes are serialized by a mutex because Supabase rotates refresh tokens: two concurrent refreshes
 * with the same token would invalidate the session.
 */
class SessionManager(
    private val store: SessionStore,
    private val authApi: AuthApi,
    private val clock: Clock = Clock.System,
) {
    private val mutex = Mutex()
    private var current: Session? = null

    private val _state = MutableStateFlow<AuthState>(AuthState.Restoring)
    val state: StateFlow<AuthState> = _state.asStateFlow()

    /** Loads the persisted session (if any). Network is not touched; validity is checked lazily. */
    fun restore() {
        val stored = store.load()
        current = stored
        _state.value = if (stored != null) AuthState.SignedIn(stored.email) else AuthState.SignedOut()
    }

    /** Persists a freshly authenticated session and signs in. */
    suspend fun establish(session: Session) = mutex.withLock {
        store.save(session)
        current = session
        _state.value = AuthState.SignedIn(session.email)
    }

    /** Ends the session locally and returns the access token so the caller can revoke it remotely. */
    suspend fun clearForSignOut(): String? = mutex.withLock {
        val token = current?.accessToken
        endLocked(null)
        token
    }

    /**
     * Returns a usable access token, refreshing when it is (about to be) expired or when [staleToken]
     * was just rejected by the server. A concurrent caller that already refreshed wins: its token is reused.
     *
     * @throws SessionExpiredException no session, or the refresh token was rejected
     * @throws com.axe.admin.network.ApiException transient failures (offline, server error); the session is kept
     */
    suspend fun accessToken(staleToken: String? = null): String = mutex.withLock {
        val session = current ?: throw SessionExpiredException()
        val stillValid = session.expiresAtMs - clock.nowMs() > REFRESH_MARGIN_MS
        if (stillValid && session.accessToken != staleToken) return session.accessToken
        try {
            val refreshed = authApi.refresh(session.refreshToken).toSession(session)
            store.save(refreshed)
            current = refreshed
            refreshed.accessToken
        } catch (_: RefreshRejectedException) {
            endLocked(SignOutReason.SessionExpired)
            throw SessionExpiredException()
        }
    }

    /**
     * Runs [block] with a valid access token. On HTTP 401 the token is refreshed once and the call is
     * retried; a second 401 means the server no longer accepts this account as an admin.
     */
    suspend fun <T> authorized(block: suspend (accessToken: String) -> T): T {
        val token = accessToken()
        try {
            return block(token)
        } catch (_: UnauthorizedException) {
            val fresh = accessToken(staleToken = token)
            try {
                return block(fresh)
            } catch (_: UnauthorizedException) {
                mutex.withLock { endLocked(SignOutReason.AccessRevoked) }
                throw SessionExpiredException()
            }
        }
    }

    private fun endLocked(reason: SignOutReason?) {
        store.clear()
        current = null
        _state.value = AuthState.SignedOut(reason)
    }

    private fun TokenResponse.toSession(previous: Session) = Session(
        accessToken = accessToken,
        refreshToken = refreshToken,
        expiresAtMs = clock.nowMs() + expiresIn * 1000,
        userId = user?.id ?: previous.userId,
        email = user?.email ?: previous.email,
    )

    companion object {
        /** Refresh this long before the access token actually expires. */
        const val REFRESH_MARGIN_MS = 60_000L

        fun sessionFrom(tokens: TokenResponse, fallbackEmail: String, clock: Clock): Session? {
            val user = tokens.user ?: return null
            return Session(
                accessToken = tokens.accessToken,
                refreshToken = tokens.refreshToken,
                expiresAtMs = clock.nowMs() + tokens.expiresIn * 1000,
                userId = user.id,
                email = user.email ?: fallbackEmail,
            )
        }
    }
}
