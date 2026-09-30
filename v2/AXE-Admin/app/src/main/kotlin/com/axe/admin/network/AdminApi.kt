package com.axe.admin.network

import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

/** The AXE Admin Edge Function (`functions/v1/admin`). Every call needs an admin's bearer token. */
class AdminApi(
    private val config: BackendConfig,
    private val transport: HttpTransport,
) {
    /**
     * Confirms the bearer token belongs to a user listed in `public.admins` and that the backend is
     * reachable. The backend has no dedicated "who am I" endpoint yet, so this uses
     * `DELETE /admin/devices` with an empty body: the function authenticates first, then treats a
     * missing token as a no-op (`{"ok":true}`), so it has no side effects.
     *
     * @throws UnauthorizedException the token is invalid or the user is not an admin (HTTP 401)
     */
    suspend fun verifyAccess(accessToken: String) {
        val request = Request.Builder()
            .url("${config.adminUrl}/devices")
            .header("apikey", config.anonKey)
            .header("Authorization", "Bearer $accessToken")
            .delete("{}".toRequestBody("application/json".toMediaType()))
            .build()
        val response = transport.execute(request)
        when (response.code) {
            200 -> Unit
            401 -> throw UnauthorizedException()
            else -> throw HttpStatusException(response.code)
        }
    }
}
