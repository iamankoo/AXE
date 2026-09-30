package com.axe.admin.model

/** Why the admin was signed out without asking to be. */
enum class SignOutReason {
    /** The refresh token was rejected or is gone: the admin must sign in again. */
    SessionExpired,

    /** The server no longer treats this account as an AXE admin (401 with a fresh token). */
    AccessRevoked,
}

sealed interface AuthState {
    /** The stored session has not been read yet. */
    data object Restoring : AuthState

    data class SignedOut(val reason: SignOutReason? = null) : AuthState

    data class SignedIn(val email: String) : AuthState
}
