package com.axe.admin.network

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.put
import okhttp3.HttpUrl.Companion.toHttpUrl
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody

@Serializable
data class TokenResponse(
    @SerialName("access_token") val accessToken: String,
    @SerialName("refresh_token") val refreshToken: String,
    @SerialName("expires_in") val expiresIn: Long = 3600,
    val user: TokenUser? = null,
)

@Serializable
data class TokenUser(val id: String, val email: String? = null)

/** Supabase Auth (GoTrue) endpoints. The admin account lives only in Supabase Auth. */
class AuthApi(
    private val config: BackendConfig,
    private val transport: HttpTransport,
    private val json: Json = Json { ignoreUnknownKeys = true },
) {
    /** POST /auth/v1/token?grant_type=password */
    suspend fun passwordLogin(email: String, password: String): TokenResponse {
        val body = buildJsonObject { put("email", email); put("password", password) }
        val response = transport.execute(tokenRequest("password", body.toString()))
        return when {
            response.code == 200 -> parse(response.body)
            response.code == 429 -> throw RateLimitedException()
            response.code in 400..422 -> throw InvalidCredentialsException()
            else -> throw HttpStatusException(response.code)
        }
    }

    /** POST /auth/v1/token?grant_type=refresh_token */
    suspend fun refresh(refreshToken: String): TokenResponse {
        val body = buildJsonObject { put("refresh_token", refreshToken) }
        val response = transport.execute(tokenRequest("refresh_token", body.toString()))
        return when (response.code) {
            200 -> parse(response.body)
            400, 401, 403, 422 -> throw RefreshRejectedException()
            429 -> throw RateLimitedException()
            else -> throw HttpStatusException(response.code)
        }
    }

    /** POST /auth/v1/logout?scope=local ends only this device's session. Best effort. */
    suspend fun logout(accessToken: String) {
        val url = "${config.authUrl}/logout".toHttpUrl().newBuilder().addQueryParameter("scope", "local").build()
        val request = Request.Builder().url(url)
            .header("apikey", config.anonKey)
            .header("Authorization", "Bearer $accessToken")
            .post(EMPTY_JSON)
            .build()
        transport.execute(request)
    }

    private fun tokenRequest(grantType: String, jsonBody: String): Request {
        val url = "${config.authUrl}/token".toHttpUrl().newBuilder().addQueryParameter("grant_type", grantType).build()
        return Request.Builder().url(url)
            .header("apikey", config.anonKey)
            .post(jsonBody.toRequestBody(JSON_MEDIA))
            .build()
    }

    private fun parse(body: String): TokenResponse = try {
        json.decodeFromString(TokenResponse.serializer(), body)
    } catch (e: SerializationException) {
        throw InvalidResponseException(e)
    }

    private companion object {
        val JSON_MEDIA = "application/json".toMediaType()
        val EMPTY_JSON = "{}".toRequestBody(JSON_MEDIA)
    }
}
