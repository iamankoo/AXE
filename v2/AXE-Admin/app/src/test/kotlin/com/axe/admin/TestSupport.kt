package com.axe.admin

import com.axe.admin.auth.Clock
import com.axe.admin.auth.SessionManager
import com.axe.admin.auth.SessionStore
import com.axe.admin.model.Session
import com.axe.admin.network.AdminApi
import com.axe.admin.network.AuthApi
import com.axe.admin.network.BackendConfig
import com.axe.admin.network.HttpTransport
import com.axe.admin.repository.AdminRepository
import com.axe.admin.repository.AuthRepository
import com.axe.admin.repository.BackendRequestRepository
import okhttp3.OkHttpClient
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import java.util.concurrent.TimeUnit

class InMemorySessionStore(var session: Session? = null) : SessionStore {
    override fun load() = session
    override fun save(session: Session) { this.session = session }
    override fun clear() { session = null }
}

class TestClock(var now: Long = 1_000_000L) : Clock {
    override fun nowMs() = now
}

fun tokenJson(access: String, refresh: String, expiresIn: Long = 3600, email: String = "admin@axe.local") =
    """{"access_token":"$access","refresh_token":"$refresh","expires_in":$expiresIn,"token_type":"bearer",""" +
        """"user":{"id":"user-1","email":"$email"}}"""

fun json(code: Int, body: String) = MockResponse().setResponseCode(code)
    .setHeader("Content-Type", "application/json").setBody(body)

fun sessionOf(clock: TestClock, access: String = "access-1", refresh: String = "refresh-1", validForMs: Long = 3_600_000) =
    Session(access, refresh, clock.nowMs() + validForMs, "user-1", "admin@axe.local")

/** Everything wired against a [MockWebServer], the way AppContainer wires the real app. */
class Harness(
    val server: MockWebServer = MockWebServer(),
    val store: InMemorySessionStore = InMemorySessionStore(),
    val clock: TestClock = TestClock(),
    beforeSignOut: suspend () -> Unit = {},
) {
    init { server.start() }

    val config = BackendConfig(server.url("/").toString().trimEnd('/'), "anon-key")
    private val client = OkHttpClient.Builder().callTimeout(20, TimeUnit.SECONDS).build()
    private val transport = HttpTransport(client)
    val authApi = AuthApi(config, transport)
    val adminApi = AdminApi(config, transport)
    val sessions = SessionManager(store, authApi, clock)
    val auth = AuthRepository(sessions, authApi, adminApi, clock, beforeSignOut)
    val admin = AdminRepository(sessions, adminApi)
    val requests = BackendRequestRepository(sessions, adminApi)
    val devices = com.axe.admin.push.BackendDeviceRepository(sessions, adminApi)

    /** Starts signed in with a valid stored session (no network involved). */
    fun signIn() { store.session = sessionOf(clock); sessions.restore() }

    fun shutdown() = server.shutdown()
}

/** Realistic payloads: the exact shape of `present()` in functions/admin/index.ts (Postgres timestamps use +00:00). */
object Fixtures {
    const val PAYMENT_ID = "0b6f2a54-5a43-4f43-9a52-7c1d7c6f9a01"
    const val INVITE_ID = "9d3c1e2f-88a7-4c60-b1f4-3a2d5e6f7b02"

    val payment = """{"id":"$PAYMENT_ID","kind":"payment","name":"Test Payer","plan":"5h","planLabel":"5 Hours",""" +
        """"expectedAmount":199,"amountPaid":199,"amountMismatch":false,"utr":"UTR123456","inviteCode":null,""" +
        """"duplicateUtr":false,"createdAt":"2026-09-30T16:20:11.123456+00:00","expiresAt":"2026-10-01T16:20:11.123+00:00"}"""

    val invite = """{"id":"$INVITE_ID","kind":"invite","name":"Test Invitee","plan":"1h","planLabel":"1 Hour",""" +
        """"expectedAmount":149,"amountPaid":null,"amountMismatch":false,"utr":null,"inviteCode":"TESTCODE01",""" +
        """"duplicateUtr":false,"createdAt":"2026-09-30T16:25:00+00:00","expiresAt":"2026-10-01T16:25:00+00:00"}"""

    fun list(vararg items: String) = """{"requests":[${items.joinToString(",")}]}"""
    fun detail(item: String, hasScreenshot: Boolean) = """{"request":${item.dropLast(1)},"hasScreenshot":$hasScreenshot}}"""

    /** A real 1x1 PNG. */
    val png: ByteArray = java.util.Base64.getDecoder().decode(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
    )
}
