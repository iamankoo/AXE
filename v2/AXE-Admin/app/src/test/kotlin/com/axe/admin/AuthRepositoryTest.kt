package com.axe.admin

import com.axe.admin.model.AuthState
import com.axe.admin.repository.LoginFailure
import com.axe.admin.repository.LoginResult
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

class AuthRepositoryTest {
    private lateinit var h: Harness

    @Before fun setUp() { h = Harness().also { it.sessions.restore() } }
    @After fun tearDown() = h.shutdown()

    @Test fun `successful login verifies admin access, persists the session and signs in`() = runTest {
        h.server.enqueue(json(200, tokenJson("acc", "ref")))
        h.server.enqueue(json(200, """{"ok":true}"""))

        assertEquals(LoginResult.Success, h.auth.login("  admin@axe.local ", "pw"))

        assertEquals(AuthState.SignedIn("admin@axe.local"), h.auth.state.value)
        assertEquals("acc", h.store.session?.accessToken)
        assertEquals("ref", h.store.session?.refreshToken)

        val login = h.server.takeRequest()
        assertEquals("POST", login.method)
        assertEquals("/auth/v1/token?grant_type=password", login.path)
        assertEquals("anon-key", login.getHeader("apikey"))
        assertTrue(login.body.readUtf8().contains(""""email":"admin@axe.local""""))

        val verify = h.server.takeRequest()
        assertEquals("DELETE", verify.method)
        assertEquals("/functions/v1/admin/devices", verify.path)
        assertEquals("Bearer acc", verify.getHeader("Authorization"))
        assertFalse("the password must never reach the admin function", verify.body.readUtf8().contains("pw"))
    }

    @Test fun `wrong credentials fail and nothing is persisted`() = runTest {
        h.server.enqueue(json(400, """{"error":"invalid_grant","error_description":"Invalid login credentials"}"""))

        assertEquals(LoginResult.Failure(LoginFailure.InvalidCredentials), h.auth.login("a@b.c", "bad"))

        assertNull(h.store.session)
        assertEquals(AuthState.SignedOut(null), h.auth.state.value)
        assertEquals("only the token request is made", 1, h.server.requestCount)
    }

    @Test fun `a valid account that is not an admin is rejected, not persisted, and its session is revoked`() = runTest {
        h.server.enqueue(json(200, tokenJson("acc", "ref")))
        h.server.enqueue(json(401, """{"error":"Sign in to AXE Admin."}"""))
        h.server.enqueue(json(204, ""))

        assertEquals(LoginResult.Failure(LoginFailure.NotAnAdmin), h.auth.login("user@axe.local", "pw"))

        assertNull(h.store.session)
        assertEquals(AuthState.SignedOut(null), h.auth.state.value)
        h.server.takeRequest(); h.server.takeRequest()
        val revoke = h.server.takeRequest()
        assertEquals("/auth/v1/logout?scope=local", revoke.path)
        assertEquals("Bearer acc", revoke.getHeader("Authorization"))
    }

    @Test fun `rate limiting is reported`() = runTest {
        h.server.enqueue(json(429, """{"error":"over_request_rate_limit"}"""))
        assertEquals(LoginResult.Failure(LoginFailure.RateLimited), h.auth.login("a@b.c", "pw"))
    }

    @Test fun `server errors are reported`() = runTest {
        h.server.enqueue(json(500, "boom"))
        assertEquals(LoginResult.Failure(LoginFailure.Server), h.auth.login("a@b.c", "pw"))
    }

    @Test fun `admin function server error after sign-in is reported and not persisted`() = runTest {
        h.server.enqueue(json(200, tokenJson("acc", "ref")))
        h.server.enqueue(json(500, """{"error":"The AXE server hit a problem."}"""))
        assertEquals(LoginResult.Failure(LoginFailure.Server), h.auth.login("a@b.c", "pw"))
        assertNull(h.store.session)
    }

    @Test fun `a malformed token response is a server failure`() = runTest {
        h.server.enqueue(json(200, """{"unexpected":true}"""))
        assertEquals(LoginResult.Failure(LoginFailure.Server), h.auth.login("a@b.c", "pw"))
        assertNull(h.store.session)
    }

    @Test fun `network failure is reported and nothing is persisted`() = runTest {
        h.server.shutdown()
        assertEquals(LoginResult.Failure(LoginFailure.Network), h.auth.login("a@b.c", "pw"))
        assertNull(h.store.session)
    }

    @Test fun `logout clears the session immediately and revokes it remotely`() = runTest {
        h.store.session = sessionOf(h.clock)
        h.sessions.restore()
        h.server.enqueue(json(204, ""))

        h.auth.logout()

        assertNull(h.store.session)
        assertEquals(AuthState.SignedOut(null), h.auth.state.value)
        assertEquals("Bearer access-1", h.server.takeRequest().getHeader("Authorization"))
    }

    @Test fun `logout still signs out when the server cannot be reached`() = runTest {
        h.store.session = sessionOf(h.clock)
        h.sessions.restore()
        h.server.shutdown()

        h.auth.logout()

        assertNull(h.store.session)
        assertEquals(AuthState.SignedOut(null), h.auth.state.value)
    }

    @Test fun `restore signs in from a persisted session without touching the network`() {
        val fresh = Harness() // not restored yet
        try {
            fresh.store.session = sessionOf(fresh.clock)
            assertEquals(AuthState.Restoring, fresh.auth.state.value)

            fresh.auth.restore()

            assertEquals(AuthState.SignedIn("admin@axe.local"), fresh.auth.state.value)
            assertEquals(0, fresh.server.requestCount)
        } finally {
            fresh.shutdown()
        }
    }

    @Test fun `restore with no persisted session is signed out`() {
        h.auth.restore()
        assertEquals(AuthState.SignedOut(null), h.auth.state.value)
        assertNotNull(h.config)
    }
}
