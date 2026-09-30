package com.axe.admin.network

import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.IOException

/** Base type for failures of a backend call. Messages never contain tokens or credentials. */
sealed class ApiException(message: String, cause: Throwable? = null) : Exception(message, cause)

/** No usable connection: offline, DNS, timeout, TLS failure. */
class NetworkException(cause: Throwable) : ApiException("Network error", cause)

/** The server rejected the bearer token (HTTP 401): expired, invalid, or not an admin. */
class UnauthorizedException : ApiException("Unauthorized")

/** Any other unexpected HTTP status. */
class HttpStatusException(val status: Int) : ApiException("HTTP $status")

/** The response body was not what the contract promises. */
class InvalidResponseException(cause: Throwable? = null) : ApiException("Invalid response", cause)

/** HTTP 404 from the admin API, e.g. the request is no longer pending. */
class NotFoundException : ApiException("Not found")

/** HTTP 409 from the admin API, e.g. the request was already decided or has expired. */
class ConflictException : ApiException("Conflict")

/** Supabase Auth rejected the email/password. */
class InvalidCredentialsException : ApiException("Invalid credentials")

/** Supabase Auth is rate limiting this client. */
class RateLimitedException : ApiException("Rate limited")

/** The refresh token was rejected: the session is over. */
class RefreshRejectedException : ApiException("Refresh token rejected")

class RawResponse(val code: Int, val body: String)

/** A binary body (payment screenshot) held in memory only. */
class BinaryResponse(val code: Int, val bytes: ByteArray, val contentType: String?)

/** The only place that talks HTTP. It never logs requests or responses. */
class HttpTransport(
    private val client: OkHttpClient,
    private val io: CoroutineDispatcher = Dispatchers.IO,
) {
    suspend fun execute(request: Request): RawResponse = withContext(io) {
        try {
            client.newCall(request).execute().use { RawResponse(it.code, it.body?.string().orEmpty()) }
        } catch (e: IOException) {
            throw NetworkException(e)
        }
    }

    /** Like [execute] but for binary bodies. Refuses bodies larger than [maxBytes] (backend caps screenshots at 5 MB). */
    suspend fun executeBytes(request: Request, maxBytes: Long): BinaryResponse = withContext(io) {
        try {
            client.newCall(request).execute().use { response ->
                val body = response.body
                if (response.code == 200 && body != null && body.contentLength() > maxBytes) throw InvalidResponseException()
                val bytes = if (response.code == 200 && body != null) body.bytes() else ByteArray(0)
                if (bytes.size > maxBytes) throw InvalidResponseException()
                BinaryResponse(response.code, bytes, body?.contentType()?.toString())
            }
        } catch (e: IOException) {
            throw NetworkException(e)
        }
    }
}
