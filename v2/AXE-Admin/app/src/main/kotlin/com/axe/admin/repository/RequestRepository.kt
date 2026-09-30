package com.axe.admin.repository

import com.axe.admin.auth.SessionExpiredException
import com.axe.admin.auth.SessionManager
import com.axe.admin.model.AccessRequest
import com.axe.admin.model.Decision
import com.axe.admin.network.AdminApi
import com.axe.admin.network.ApiException
import com.axe.admin.network.ConflictException
import com.axe.admin.network.InvalidResponseException
import com.axe.admin.network.NetworkException
import com.axe.admin.network.NotFoundException
import com.axe.admin.network.ScreenshotBytes
import com.axe.admin.network.UnauthorizedException

enum class RequestFailure {
    /** No connection. */
    Network,

    /** The session ended (refresh rejected or admin access revoked). The auth state is already SignedOut. */
    SessionEnded,

    /** 404: no longer pending, or no screenshot. */
    NotFound,

    /** 409: already decided by someone else, or expired. */
    Conflict,

    /** Any other server error. */
    Server,

    /** The response did not match the backend contract. */
    InvalidResponse,
}

sealed interface Outcome<out T> {
    data class Success<T>(val value: T) : Outcome<T>
    data class Failure(val failure: RequestFailure) : Outcome<Nothing>
}

/** Request management for the signed-in admin. The UI talks to this, never to HTTP. */
interface RequestRepository {
    suspend fun pending(): Outcome<List<AccessRequest>>
    suspend fun detail(id: String): Outcome<AccessRequest>
    suspend fun screenshot(id: String): Outcome<ScreenshotBytes>
    suspend fun decide(id: String, decision: Decision): Outcome<Unit>
}

class BackendRequestRepository(
    private val sessions: SessionManager,
    private val api: AdminApi,
) : RequestRepository {

    override suspend fun pending() = call { token -> api.listRequests(token).map { it.toDomain() } }

    override suspend fun detail(id: String) = call { token -> api.getRequest(token, id).toDomain() }

    override suspend fun screenshot(id: String) = call { token -> api.getScreenshot(token, id) }

    override suspend fun decide(id: String, decision: Decision) = call { token -> api.decide(token, id, decision) }

    /**
     * Runs an authorized call and maps failures to [RequestFailure]. Only known API failures are caught, so
     * coroutine cancellation always propagates.
     */
    private suspend fun <T> call(block: suspend (accessToken: String) -> T): Outcome<T> = try {
        Outcome.Success(sessions.authorized(block))
    } catch (_: SessionExpiredException) {
        Outcome.Failure(RequestFailure.SessionEnded)
    } catch (e: ApiException) {
        Outcome.Failure(
            when (e) {
                is NetworkException -> RequestFailure.Network
                is NotFoundException -> RequestFailure.NotFound
                is ConflictException -> RequestFailure.Conflict
                is InvalidResponseException -> RequestFailure.InvalidResponse
                is UnauthorizedException -> RequestFailure.SessionEnded
                else -> RequestFailure.Server
            },
        )
    }
}
