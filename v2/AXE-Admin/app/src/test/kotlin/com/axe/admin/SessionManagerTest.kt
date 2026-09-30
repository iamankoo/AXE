package com.axe.admin

import com.axe.admin.auth.SessionExpiredException
import com.axe.admin.model.AuthState
import com.axe.admin.model.SignOutReason
import com.axe.admin.network.NetworkException
import com.axe.admin.repository.BackendStatus
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Before
import org.junit.Test

class SessionManagerTest {
    private lateinit var h: Harness

    @Before fun setUp() { h = Harness() }
    @After fun tearDown() = h.shutdown()

    private fun startSignedIn(validForMs: Long = 3_600_000) {
        h.store.session = sessionOf(h.clock, validForMs = validForMs)
        h.sessions.restore()
    }

    @Test fun `a valid token is used as is`() = runTest {
        startSignedIn()
        assertEquals("access-1", h.sessions.accessToken())
        assertEquals(0, h.server.requestCount)
    }

    @Test fun `an expired token is refreshed and the new session is persisted`() = runTest {
        startSignedIn(validForMs = 30_000) // inside the 60 s refresh margin
        h.server.enqueue(json(200, tokenJson("access-2", "refresh-2")))

        assertEquals("access-2", h.sessions.accessToken())

        assertEquals("access-2", h.store.session?.accessToken)
        assertEquals("refresh-2", h.store.session?.refreshToken)
        val refresh = h.server.takeRequest()
        assertEquals("/auth/v1/token?grant_type=refresh_token", refresh.path)
        assertEquals(true, refresh.body.readUtf8().contains("refresh-1"))
    }

    @Test fun `a rejected refresh token ends the session as expired`() = runTest {
        startSignedIn(validForMs = -1)
        h.server.enqueue(json(400, """{"error":"invalid_grant"}"""))

        assertThrows(SessionExpiredException::class.java) { kotlinx.coroutines.runBlocking { h.sessions.accessToken() } }

        assertNull(h.store.session)
        assertEquals(AuthState.SignedOut(SignOutReason.SessionExpired), h.sessions.state.value)
    }

    @Test fun `a network failure during refresh keeps the session`() = runTest {
        startSignedIn(validForMs = -1)
        h.server.shutdown()

        assertThrows(NetworkException::class.java) { kotlinx.coroutines.runBlocking { h.sessions.accessToken() } }

        assertNotNull(h.store.session)
        assertEquals(AuthState.SignedIn("admin@axe.local"), h.sessions.state.value)
    }

    @Test fun `a 401 triggers one refresh and the call is retried with the new token`() = runTest {
        startSignedIn()
        h.server.enqueue(json(401, """{"error":"Sign in to AXE Admin."}"""))
        h.server.enqueue(json(200, tokenJson("access-2", "refresh-2")))
        h.server.enqueue(json(200, """{"ok":true}"""))

        assertEquals(BackendStatus.Connected, h.admin.checkBackend())

        assertEquals("Bearer access-1", h.server.takeRequest().getHeader("Authorization"))
        assertEquals("/auth/v1/token?grant_type=refresh_token", h.server.takeRequest().path)
        assertEquals("Bearer access-2", h.server.takeRequest().getHeader("Authorization"))
        assertEquals(AuthState.SignedIn("admin@axe.local"), h.sessions.state.value)
    }

    @Test fun `a second 401 after refreshing means admin access was revoked`() = runTest {
        startSignedIn()
        h.server.enqueue(json(401, "{}"))
        h.server.enqueue(json(200, tokenJson("access-2", "refresh-2")))
        h.server.enqueue(json(401, "{}"))

        assertEquals(BackendStatus.SignedOut, h.admin.checkBackend())

        assertNull(h.store.session)
        assertEquals(AuthState.SignedOut(SignOutReason.AccessRevoked), h.sessions.state.value)
    }

    @Test fun `expired session detected while checking the backend signs the admin out`() = runTest {
        startSignedIn()
        h.server.enqueue(json(401, "{}"))
        h.server.enqueue(json(400, """{"error":"invalid_grant"}"""))

        assertEquals(BackendStatus.SignedOut, h.admin.checkBackend())
        assertEquals(AuthState.SignedOut(SignOutReason.SessionExpired), h.sessions.state.value)
    }

    @Test fun `offline is reported without signing out`() = runTest {
        startSignedIn()
        h.server.shutdown()

        assertEquals(BackendStatus.Offline, h.admin.checkBackend())

        assertEquals(AuthState.SignedIn("admin@axe.local"), h.sessions.state.value)
        assertNotNull(h.store.session)
    }

    @Test fun `backend errors are reported without signing out`() = runTest {
        startSignedIn()
        h.server.enqueue(json(500, "{}"))

        assertEquals(BackendStatus.Error, h.admin.checkBackend())
        assertEquals(AuthState.SignedIn("admin@axe.local"), h.sessions.state.value)
    }

    @Test fun `no session means expired`() = runTest {
        h.sessions.restore()
        assertThrows(SessionExpiredException::class.java) { kotlinx.coroutines.runBlocking { h.sessions.accessToken() } }
    }

    @Test fun `Session toString never contains tokens`() {
        val text = sessionOf(h.clock, access = "SECRET-ACCESS", refresh = "SECRET-REFRESH").toString()
        assertEquals(false, text.contains("SECRET"))
    }
}
