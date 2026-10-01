package com.axe.admin.network

import okhttp3.HttpUrl.Companion.toHttpUrlOrNull

/**
 * Client-safe backend configuration: the Supabase project URL and its PUBLIC anon key.
 * Nothing secret is ever part of this class.
 */
data class BackendConfig(
    val supabaseUrl: String,
    val anonKey: String,
    /** Loopback HTTP (a local Supabase) is a DEBUG-build convenience only; release builds require HTTPS. */
    val allowLoopback: Boolean = false,
) {
    private val base = supabaseUrl.trim().trimEnd('/')

    val authUrl: String get() = "$base/auth/v1"
    val adminUrl: String get() = "$base/functions/v1/admin"

    /** HTTPS is required. Loopback hosts are accepted only when [allowLoopback] (debug builds). Fails closed otherwise. */
    val isValid: Boolean
        get() {
            if (anonKey.isBlank()) return false
            val url = base.toHttpUrlOrNull() ?: return false
            val loopback = url.host in LOOPBACK_HOSTS
            if (loopback) return allowLoopback
            return url.isHttps
        }

    private companion object {
        val LOOPBACK_HOSTS = setOf("127.0.0.1", "localhost", "10.0.2.2")
    }
}
