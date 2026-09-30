package com.axe.admin

import com.axe.admin.push.DeviceBackend
import com.axe.admin.push.DeviceResult
import com.axe.admin.push.PushRegistrar
import com.axe.admin.push.PushStore
import com.axe.admin.push.PushTokenSource
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

private class FakeTokens(var token: String? = "token-A") : PushTokenSource {
    val events = mutableListOf<String>()
    var deleteFails = false
    override suspend fun currentToken() = token
    override suspend fun deleteToken() {
        events += "delete-token"
        if (deleteFails) error("no network")
    }
}

private class FakeStore : PushStore {
    override var lastRegisteredToken: String? = null
    override var lastRegisteredAtMs: Long = 0
    override var pendingUnregister: String? = null
    override var permissionAsked: Boolean = false
}

private class FakeBackend(private val events: MutableList<String>) : DeviceBackend {
    val registers = mutableListOf<Pair<String, String?>>()
    val unregisters = mutableListOf<String>()
    var registerResult = DeviceResult.Ok
    var unregisterResult = DeviceResult.Ok

    override suspend fun register(token: String, previousToken: String?): DeviceResult {
        registers += token to previousToken
        return registerResult
    }

    override suspend fun unregister(token: String): DeviceResult {
        events += "unregister:$token"
        unregisters += token
        return unregisterResult
    }
}

class PushRegistrarTest {
    private val clock = TestClock(1_000_000)
    private val tokens = FakeTokens()
    private val store = FakeStore()
    private val backend = FakeBackend(tokens.events)

    private fun registrar(configured: Boolean = true) = PushRegistrar(tokens, store, backend, configured, clock)

    @Test fun `signing in registers the current token with no predecessor`() = runTest {
        registrar().onSignedIn()

        assertEquals(listOf("token-A" to null), backend.registers)
        assertEquals("token-A", store.lastRegisteredToken)
        assertEquals(1_000_000L, store.lastRegisteredAtMs)
    }

    @Test fun `an unchanged token is not re-sent on every launch, but is refreshed once a day`() = runTest {
        val r = registrar()
        r.onSignedIn()
        clock.now += 60 * 60_000L
        r.onSignedIn()
        assertEquals("within a day: no repeated registration", 1, backend.registers.size)

        clock.now += PushRegistrar.REFRESH_INTERVAL_MS
        r.onSignedIn()
        assertEquals("after a day it is re-registered so server-side cleanups heal", 2, backend.registers.size)
        assertEquals("token-A" to null, backend.registers[1])
    }

    @Test fun `a rotated token replaces the old one`() = runTest {
        val r = registrar()
        r.onSignedIn()

        r.onNewToken("token-B", signedIn = true)

        assertEquals("token-B" to "token-A", backend.registers.last())
        assertEquals("token-B", store.lastRegisteredToken)
    }

    @Test fun `a token that FCM reports again is not registered twice`() = runTest {
        val r = registrar()
        r.onSignedIn()
        r.onNewToken("token-A", signedIn = true)
        assertEquals(1, backend.registers.size)
    }

    @Test fun `a signed-out device is never registered, not even when its token rotates`() = runTest {
        val r = registrar()
        r.onNewToken("token-B", signedIn = false)

        assertTrue("nothing is sent for an unauthenticated device", backend.registers.isEmpty())
        assertNull(store.lastRegisteredToken)

        tokens.token = "token-B"
        r.onSignedIn() // the admin signs in: only now is the device registered, with the current token
        assertEquals(listOf("token-B" to null), backend.registers)
    }

    @Test fun `a failed registration is retried at the next opportunity`() = runTest {
        val r = registrar()
        backend.registerResult = DeviceResult.NetworkError
        r.onSignedIn()
        assertNull("not recorded as registered", store.lastRegisteredToken)

        backend.registerResult = DeviceResult.Ok
        r.onSignedIn()
        assertEquals(2, backend.registers.size)
        assertEquals("token-A", store.lastRegisteredToken)
    }

    @Test fun `a registration rejected because the session ended is not recorded`() = runTest {
        backend.registerResult = DeviceResult.NotSignedIn
        registrar().onSignedIn()
        assertNull(store.lastRegisteredToken)
    }

    @Test fun `no token yet means nothing is registered`() = runTest {
        tokens.token = null
        registrar().onSignedIn()
        assertTrue(backend.registers.isEmpty())
    }

    @Test fun `without a Firebase configuration nothing is registered or removed`() = runTest {
        val r = registrar(configured = false)
        r.onSignedIn()
        r.onNewToken("token-B", signedIn = true)
        store.lastRegisteredToken = "old"
        r.onSigningOut()

        assertTrue(backend.registers.isEmpty())
        assertTrue(backend.unregisters.isEmpty())
        assertTrue(tokens.events.isEmpty())
    }

    // ---- sign-out -------------------------------------------------------------------------------------------

    @Test fun `signing out removes the registration first, then invalidates the token`() = runTest {
        val r = registrar()
        r.onSignedIn()

        r.onSigningOut()

        assertEquals("server registration is removed before the token is deleted", listOf("unregister:token-A", "delete-token"), tokens.events)
        assertNull(store.lastRegisteredToken)
        assertEquals(0L, store.lastRegisteredAtMs)
        assertNull(store.pendingUnregister)
    }

    @Test fun `a failed removal is remembered and retried at the next sign-in, while the token is still invalidated`() = runTest {
        val r = registrar()
        r.onSignedIn()
        backend.unregisterResult = DeviceResult.NetworkError

        r.onSigningOut()

        assertEquals("token-A", store.pendingUnregister)
        assertTrue("the token is invalidated even though the server could not be told", tokens.events.contains("delete-token"))
        assertNull(store.lastRegisteredToken)

        backend.unregisterResult = DeviceResult.Ok
        tokens.token = "token-NEW"
        r.onSignedIn()

        assertNull("the leftover registration was removed", store.pendingUnregister)
        assertEquals(listOf("token-A", "token-A"), backend.unregisters)
        assertEquals("token-NEW" to null, backend.registers.last())
    }

    @Test fun `a token that cannot be deleted never blocks signing out`() = runTest {
        val r = registrar()
        r.onSignedIn()
        tokens.deleteFails = true

        r.onSigningOut() // must not throw

        assertNull(store.lastRegisteredToken)
    }

    @Test fun `signing out a device that never registered still invalidates its token`() = runTest {
        registrar().onSigningOut()
        assertTrue(backend.unregisters.isEmpty())
        assertEquals(listOf("delete-token"), tokens.events)
    }

    @Test fun `signing in again after signing out registers the fresh token with no predecessor`() = runTest {
        val r = registrar()
        r.onSignedIn()
        r.onSigningOut()
        tokens.token = "token-FRESH"

        r.onSignedIn()

        assertEquals("token-FRESH" to null, backend.registers.last())
        assertEquals("token-FRESH", store.lastRegisteredToken)
    }
}
