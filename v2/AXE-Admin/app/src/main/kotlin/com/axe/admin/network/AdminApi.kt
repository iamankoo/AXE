package com.axe.admin.network

import com.axe.admin.model.Decision
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.HttpUrl
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

/** A screenshot as delivered by the admin API; held in memory only. */
class ScreenshotBytes(val bytes: ByteArray, val contentType: String?)

/**
 * The AXE Admin Edge Function (`functions/v1/admin`). Every call needs an admin's bearer token; 401 means the
 * token is invalid or the user is not an admin. Responses are never logged.
 */
class AdminApi(
    private val config: BackendConfig,
    private val transport: HttpTransport,
    private val json: Json = Json { ignoreUnknownKeys = true },
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
        val request = authed(accessToken, "${config.adminUrl}/devices".toHttpUrl())
            .delete("{}".toRequestBody(JSON_MEDIA))
            .build()
        val response = transport.execute(request)
        when (response.code) {
            200 -> Unit
            401 -> throw UnauthorizedException()
            else -> throw HttpStatusException(response.code)
        }
    }

    /** `GET /admin/requests`: pending, unexpired requests, oldest first (server caps at 100). */
    suspend fun listRequests(accessToken: String): List<AccessRequestDto> {
        val request = authed(accessToken, requestsUrl()).get().build()
        val response = transport.execute(request)
        requireOk(response.code)
        return decode(RequestListResponse.serializer(), response.body).requests
    }

    /** `GET /admin/requests/:id`. 404 when the request is no longer pending. */
    suspend fun getRequest(accessToken: String, id: String): AccessRequestDto {
        val request = authed(accessToken, requestsUrl(id)).get().build()
        val response = transport.execute(request)
        requireOk(response.code)
        return decode(RequestDetailResponse.serializer(), response.body).request
    }

    /** `GET /admin/requests/:id/screenshot`: raw image bytes. 404 when there is none or the request was decided. */
    suspend fun getScreenshot(accessToken: String, id: String): ScreenshotBytes {
        val request = authed(accessToken, requestsUrl(id, "screenshot")).get().build()
        val response = transport.executeBytes(request, MAX_SCREENSHOT_BYTES)
        requireOk(response.code)
        if (response.bytes.isEmpty()) throw InvalidResponseException()
        return ScreenshotBytes(response.bytes, response.contentType)
    }

    /**
     * `POST /admin/requests/:id/decision` with `{"decision":"approve"|"reject"}`. The server validates, signs any
     * grant, stores it for the Windows client and deletes the request data. 409 means already handled or expired.
     */
    suspend fun decide(accessToken: String, id: String, decision: Decision) {
        val body = buildJsonObject { put("decision", decision.wire) }.toString()
        val request = authed(accessToken, requestsUrl(id, "decision")).post(body.toRequestBody(JSON_MEDIA)).build()
        val response = transport.execute(request)
        requireOk(response.code)
        val parsed = decode(DecisionResponse.serializer(), response.body)
        if (!parsed.ok || parsed.decision != decision.wire) throw InvalidResponseException()
    }

    private fun requestsUrl(vararg segments: String): HttpUrl {
        val builder = "${config.adminUrl}/requests".toHttpUrl().newBuilder()
        segments.forEach { builder.addPathSegment(it) } // encodes each segment; an id can never escape its path slot
        return builder.build()
    }

    private fun authed(accessToken: String, url: HttpUrl) = Request.Builder().url(url)
        .header("apikey", config.anonKey)
        .header("Authorization", "Bearer $accessToken")

    private fun requireOk(code: Int) {
        when (code) {
            200 -> Unit
            401 -> throw UnauthorizedException()
            404 -> throw NotFoundException()
            409 -> throw ConflictException()
            else -> throw HttpStatusException(code)
        }
    }

    private fun <T> decode(serializer: kotlinx.serialization.KSerializer<T>, body: String): T = try {
        json.decodeFromString(serializer, body)
    } catch (e: SerializationException) {
        throw InvalidResponseException(e)
    } catch (e: IllegalArgumentException) {
        throw InvalidResponseException(e)
    }

    private companion object {
        val JSON_MEDIA = "application/json".toMediaType()
        const val MAX_SCREENSHOT_BYTES = 8L * 1024 * 1024 // backend accepts at most 5 MB
    }
}
