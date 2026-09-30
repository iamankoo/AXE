package com.axe.admin

import com.axe.admin.model.AuthState
import com.axe.admin.model.SignOutReason
import com.axe.admin.push.DeviceResult
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/** The device-token calls against the real admin API contract (POST/DELETE /admin/devices). */
class DeviceRepositoryTest {
    private lateinit var h: Harness

    @Before fun setUp() { h = Harness().also { it.signIn() } }
    @After fun tearDown() = h.shutdown()

    @Test fun `registering sends the token with the admin session and no client-supplied owner`() = runTest {
        h.server.enqueue(json(200, """{"ok":true}"""))

        assertEquals(DeviceResult.Ok, h.devices.register("fcm-token-12345678901234", null))

        val sent = h.server.takeRequest()
        assertEquals("POST", sent.method)
        assertEquals("/functions/v1/admin/devices", sent.path)
        assertEquals("Bearer access-1", sent.getHeader("Authorization"))
        assertEquals("only the token: the server decides the owner", """{"token":"fcm-token-12345678901234"}""", sent.body.readUtf8())
    }

    @Test fun `a rotation names the token it replaces`() = runTest {
        h.server.enqueue(json(200, """{"ok":true}"""))

        assertEquals(DeviceResult.Ok, h.devices.register("new-token-1234567890123456", "old-token-1234567890123456"))

        assertEquals(
            """{"token":"new-token-1234567890123456","previousToken":"old-token-1234567890123456"}""",
            h.server.takeRequest().body.readUtf8(),
        )
    }

    @Test fun `unregistering deletes by token`() = runTest {
        h.server.enqueue(json(200, """{"ok":true}"""))

        assertEquals(DeviceResult.Ok, h.devices.unregister("fcm-token-12345678901234"))

        val sent = h.server.takeRequest()
        assertEquals("DELETE", sent.method)
        assertEquals("/functions/v1/admin/devices", sent.path)
        assertEquals("""{"token":"fcm-token-12345678901234"}""", sent.body.readUtf8())
    }

    @Test fun `an expired access token is refreshed and the call is retried`() = runTest {
        h.server.enqueue(json(401, """{"error":"Sign in to AXE Admin."}"""))
        h.server.enqueue(json(200, tokenJson("access-2", "refresh-2")))
        h.server.enqueue(json(200, """{"ok":true}"""))

        assertEquals(DeviceResult.Ok, h.devices.register("fcm-token-12345678901234", null))
        h.server.takeRequest(); h.server.takeRequest()
        assertEquals("Bearer access-2", h.server.takeRequest().getHeader("Authorization"))
    }

    @Test fun `a revoked admin session means not signed in, and nothing is stored`() = runTest {
        h.server.enqueue(json(401, "{}"))
        h.server.enqueue(json(200, tokenJson("access-2", "refresh-2")))
        h.server.enqueue(json(401, "{}"))

        assertEquals(DeviceResult.NotSignedIn, h.devices.register("fcm-token-12345678901234", null))
        assertEquals(AuthState.SignedOut(SignOutReason.AccessRevoked), h.sessions.state.value)
    }

    @Test fun `without a session nothing is sent at all`() = runTest {
        h.store.session = null
        h.sessions.restore()

        assertEquals(DeviceResult.NotSignedIn, h.devices.register("fcm-token-12345678901234", null))
        assertEquals(0, h.server.requestCount)
    }

    @Test fun `failures are reported, not thrown`() = runTest {
        h.server.enqueue(json(500, "{}"))
        assertEquals(DeviceResult.Error, h.devices.register("fcm-token-12345678901234", null))
        h.server.enqueue(json(400, """{"error":"Bad token."}"""))
        assertEquals(DeviceResult.Error, h.devices.unregister("x"))
        h.server.shutdown()
        assertEquals(DeviceResult.NetworkError, h.devices.register("fcm-token-12345678901234", null))
        assertNotNull(h.store.session)
    }
}

/** Sign-out runs the push clean-up while the session can still authenticate it. */
class LogoutPushHookTest {
    @Test fun `the hook runs before the session is cleared and can still use it`() = runTest {
        var signedInDuringHook: Boolean? = null
        lateinit var h: Harness
        h = Harness(beforeSignOut = {
            signedInDuringHook = h.store.session != null && h.auth.state.value is AuthState.SignedIn
            h.devices.unregister("fcm-token-12345678901234") // a real authenticated call, made before the session is cleared
        })
        try {
            h.signIn()
            h.server.enqueue(json(200, """{"ok":true}"""))   // the device removal
            h.server.enqueue(json(204, ""))                   // the session revocation

            h.auth.logout()

            assertEquals("the session was still signed in while the hook ran", true, signedInDuringHook)
            val removal = h.server.takeRequest()
            assertEquals("DELETE", removal.method)
            assertEquals("/functions/v1/admin/devices", removal.path)
            assertEquals("Bearer access-1", removal.getHeader("Authorization"))
            assertNull("afterwards the session is gone", h.store.session)
            assertEquals(AuthState.SignedOut(null), h.auth.state.value)
        } finally {
            h.shutdown()
        }
    }

    @Test fun `a failing hook never blocks signing out`() = runTest {
        val h = Harness(beforeSignOut = { error("push clean-up exploded") })
        try {
            h.signIn()
            h.server.enqueue(json(204, ""))

            h.auth.logout()

            assertNull(h.store.session)
            assertEquals(AuthState.SignedOut(null), h.auth.state.value)
        } finally {
            h.shutdown()
        }
    }

    @Test fun `a hook that hangs is cut off and signing out still completes`() = runTest {
        val h = Harness(beforeSignOut = { kotlinx.coroutines.delay(10 * 60_000) })
        try {
            h.signIn()
            h.server.enqueue(json(204, ""))

            h.auth.logout() // virtual time: the 6 s bound fires long before the hook's 10 minutes

            assertNull(h.store.session)
            assertFalse(h.auth.state.value is AuthState.SignedIn)
        } finally {
            h.shutdown()
        }
    }

    @Test fun `logout without a hook behaves as before`() = runTest {
        val h = Harness()
        try {
            h.signIn()
            h.server.enqueue(json(204, ""))
            h.auth.logout()
            assertEquals(AuthState.SignedOut(null), h.auth.state.value)
        } finally {
            h.shutdown()
        }
    }
}
