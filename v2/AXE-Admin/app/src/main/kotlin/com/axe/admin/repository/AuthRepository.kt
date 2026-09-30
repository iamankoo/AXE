package com.axe.admin.repository

import com.axe.admin.auth.Clock
import com.axe.admin.auth.SessionManager
import com.axe.admin.model.AuthState
import com.axe.admin.network.AdminApi
import com.axe.admin.network.ApiException
import com.axe.admin.network.AuthApi
import com.axe.admin.network.HttpStatusException
import com.axe.admin.network.InvalidCredentialsException
import com.axe.admin.network.InvalidResponseException
import com.axe.admin.network.NetworkException
import com.axe.admin.network.RateLimitedException
import com.axe.admin.network.UnauthorizedException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.withTimeoutOrNull

enum class LoginFailure { InvalidCredentials, NotAnAdmin, RateLimited, Network, Server }

sealed interface LoginResult {
    data object Success : LoginResult
    data class Failure(val reason: LoginFailure) : LoginResult
}

/** Login / logout / session state. The UI never talks to the network directly. */
class AuthRepository(
    private val sessions: SessionManager,
    private val authApi: AuthApi,
    private val adminApi: AdminApi,
    private val clock: Clock = Clock.System,
    /** Runs before the session is cleared, while the server can still authenticate it (e.g. to remove the push token). */
    private val beforeSignOut: suspend () -> Unit = {},
) {
    val state: StateFlow<AuthState> get() = sessions.state

    fun restore() = sessions.restore()

    /**
     * Signs in with Supabase Auth, then asks the backend whether the account is an AXE admin. The session
     * is persisted only when both succeed, so a valid non-admin account never stays signed in.
     */
    suspend fun login(email: String, password: String): LoginResult {
        val cleanEmail = email.trim()
        val tokens = try {
            authApi.passwordLogin(cleanEmail, password)
        } catch (e: ApiException) {
            return LoginResult.Failure(e.toLoginFailure())
        }
        val session = SessionManager.sessionFrom(tokens, cleanEmail, clock)
            ?: return LoginResult.Failure(LoginFailure.Server)

        try {
            adminApi.verifyAccess(session.accessToken)
        } catch (_: UnauthorizedException) {
            runCatching { authApi.logout(session.accessToken) } // do not leave a valid non-admin session behind
            return LoginResult.Failure(LoginFailure.NotAnAdmin)
        } catch (e: ApiException) {
            return LoginResult.Failure(e.toLoginFailure())
        }
        sessions.establish(session)
        return LoginResult.Success
    }

    /** Ends the local session immediately, then revokes it on the server as a best effort. */
    suspend fun logout() {
        // Best effort and bounded: signing out must never hang on the network or fail because of it.
        try {
            withTimeoutOrNull(SIGN_OUT_HOOK_TIMEOUT_MS) { beforeSignOut() }
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            // push clean-up problems never block signing out
        }
        val token = sessions.clearForSignOut() ?: return
        runCatching { authApi.logout(token) }
    }

    private companion object {
        const val SIGN_OUT_HOOK_TIMEOUT_MS = 6_000L
    }

    private fun ApiException.toLoginFailure(): LoginFailure = when (this) {
        is InvalidCredentialsException -> LoginFailure.InvalidCredentials
        is RateLimitedException -> LoginFailure.RateLimited
        is NetworkException -> LoginFailure.Network
        is UnauthorizedException -> LoginFailure.NotAnAdmin
        is HttpStatusException, is InvalidResponseException -> LoginFailure.Server
        else -> LoginFailure.Server
    }
}
