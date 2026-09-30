package com.axe.admin.model

import kotlinx.serialization.Serializable

/**
 * A Supabase Auth session for an AXE admin. Persisted only through
 * [com.axe.admin.auth.EncryptedSessionStore]. [toString] is redacted so a stray log line can never
 * leak the tokens.
 */
@Serializable
data class Session(
    val accessToken: String,
    val refreshToken: String,
    /** Epoch millis at which [accessToken] expires (device clock; the server is authoritative). */
    val expiresAtMs: Long,
    val userId: String,
    val email: String,
) {
    override fun toString(): String = "Session(userId=$userId, tokens=<redacted>)"
}
