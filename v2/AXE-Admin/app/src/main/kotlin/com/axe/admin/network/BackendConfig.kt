package com.axe.admin.network

import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * Client-safe backend configuration: the Supabase project URL and its PUBLIC anon key.
 * Nothing secret is ever part of this class.
 */
data class BackendConfig(val supabaseUrl: String, val anonKey: String) {
    private val base = supabaseUrl.trim().trimEnd('/')

    val authUrl: String get() = "$base/auth/v1"
    val adminUrl: String get() = "$base/functions/v1/admin"

    /** HTTPS is required, except for loopback hosts used with a local Supabase in debug builds. */
    val isValid: Boolean
        get() {
            if (anonKey.isBlank()) return false
            val url = base.toHttpUrlOrNull() ?: return false
            return url.isHttps || url.host in LOOPBACK_HOSTS
        }

    private companion object {
        val LOOPBACK_HOSTS = setOf("127.0.0.1", "localhost", "10.0.2.2")
    }
}
