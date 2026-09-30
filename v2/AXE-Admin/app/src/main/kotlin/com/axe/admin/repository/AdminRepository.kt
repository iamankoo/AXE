package com.axe.admin.repository

import com.axe.admin.auth.SessionExpiredException
import com.axe.admin.auth.SessionManager
import com.axe.admin.network.AdminApi
import com.axe.admin.network.ApiException
import com.axe.admin.network.NetworkException

enum class BackendStatus {
    /** Reachable, and the session is accepted as an admin session. */
    Connected,

    /** No connection to the backend; the stored session is kept. */
    Offline,

    /** The backend answered, but with an unexpected error. */
    Error,

    /** The session ended while checking (the auth state has already changed to SignedOut). */
    SignedOut,
}

/** Backend access for the signed-in admin. Phase 1 only checks connectivity and admin access. */
class AdminRepository(
    private val sessions: SessionManager,
    private val adminApi: AdminApi,
) {
    suspend fun checkBackend(): BackendStatus = try {
        sessions.authorized { adminApi.verifyAccess(it) }
        BackendStatus.Connected
    } catch (_: SessionExpiredException) {
        BackendStatus.SignedOut
    } catch (_: NetworkException) {
        BackendStatus.Offline
    } catch (_: ApiException) {
        BackendStatus.Error
    }
}
