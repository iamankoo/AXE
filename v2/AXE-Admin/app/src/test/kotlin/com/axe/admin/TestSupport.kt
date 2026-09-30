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
class Harness(val server: MockWebServer = MockWebServer(), val store: InMemorySessionStore = InMemorySessionStore(), val clock: TestClock = TestClock()) {
    init { server.start() }

    val config = BackendConfig(server.url("/").toString().trimEnd('/'), "anon-key")
    private val client = OkHttpClient.Builder().callTimeout(5, TimeUnit.SECONDS).build()
    private val transport = HttpTransport(client)
    val authApi = AuthApi(config, transport)
    val adminApi = AdminApi(config, transport)
    val sessions = SessionManager(store, authApi, clock)
    val auth = AuthRepository(sessions, authApi, adminApi, clock)
    val admin = AdminRepository(sessions, adminApi)

    fun shutdown() = server.shutdown()
}
