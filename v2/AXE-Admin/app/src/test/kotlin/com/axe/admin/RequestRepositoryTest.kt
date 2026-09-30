package com.axe.admin

import com.axe.admin.model.AuthState
import com.axe.admin.model.Decision
import com.axe.admin.model.RequestKind
import com.axe.admin.model.SignOutReason
import com.axe.admin.repository.Outcome
import com.axe.admin.repository.RequestFailure
import kotlinx.coroutines.test.runTest
import okio.Buffer
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.time.Instant

class RequestRepositoryTest {
    private lateinit var h: Harness

    @Before fun setUp() { h = Harness().also { it.signIn() } }
    @After fun tearDown() = h.shutdown()

    @Suppress("UNCHECKED_CAST")
    private fun <T> Outcome<T>.value(): T = (this as Outcome.Success<T>).value
    private fun Outcome<*>.failure(): RequestFailure = (this as Outcome.Failure).failure

    // ---- list ----------------------------------------------------------------------------------------

    @Test fun `list maps payment and invitation requests`() = runTest {
        h.server.enqueue(json(200, Fixtures.list(Fixtures.payment, Fixtures.invite)))

        val requests = h.requests.pending().value()

        assertEquals(2, requests.size)
        val pay = requests[0]
        assertEquals(RequestKind.Payment, pay.kind)
        assertEquals("Test Payer", pay.name)
        assertEquals("5 Hours", pay.planLabel)
        assertEquals(199.0, pay.expectedAmount, 0.0)
        assertEquals(199.0, pay.amountPaid!!, 0.0)
        assertEquals("UTR123456", pay.utr)
        assertNull(pay.inviteCode)
        assertEquals(Instant.parse("2026-09-30T16:20:11.123456Z"), pay.createdAt)
        assertNull("the list does not say whether there is a screenshot", pay.hasScreenshot)

        val invite = requests[1]
        assertEquals(RequestKind.Invite, invite.kind)
        assertEquals("PAPAJI500", invite.inviteCode)
        assertNull(invite.amountPaid)
        assertNull(invite.utr)

        val sent = h.server.takeRequest()
        assertEquals("GET", sent.method)
        assertEquals("/functions/v1/admin/requests", sent.path)
        assertEquals("Bearer access-1", sent.getHeader("Authorization"))
    }

    @Test fun `empty list`() = runTest {
        h.server.enqueue(json(200, """{"requests":[]}"""))
        assertEquals(emptyList<Any>(), h.requests.pending().value())
    }

    @Test fun `missing and null optional fields are handled, unknown kinds and extra fields tolerated`() = runTest {
        h.server.enqueue(json(200, """{"requests":[{"id":"x1","kind":"gift","surprise":{"a":1},"name":"  ","createdAt":"not a date"}]}"""))

        val r = h.requests.pending().value().single()

        assertEquals(RequestKind.Unknown, r.kind)
        assertNull("blank names are treated as missing", r.name)
        assertNull("an unparseable timestamp becomes null instead of failing the list", r.createdAt)
        assertNull(r.expiresAt)
        assertEquals("", r.plan)
        assertFalse(r.duplicateUtr)
    }

    @Test fun `network failure`() = runTest {
        h.server.shutdown()
        assertEquals(RequestFailure.Network, h.requests.pending().failure())
    }

    @Test fun `server error`() = runTest {
        h.server.enqueue(json(500, """{"error":"The AXE server hit a problem. Please try again."}"""))
        assertEquals(RequestFailure.Server, h.requests.pending().failure())
    }

    @Test fun `unauthorized after a refresh ends the session and reports SessionEnded`() = runTest {
        h.server.enqueue(json(401, """{"error":"Sign in to AXE Admin."}"""))
        h.server.enqueue(json(200, tokenJson("access-2", "refresh-2")))
        h.server.enqueue(json(401, """{"error":"Sign in to AXE Admin."}"""))

        assertEquals(RequestFailure.SessionEnded, h.requests.pending().failure())
        assertEquals(AuthState.SignedOut(SignOutReason.AccessRevoked), h.sessions.state.value)
    }

    @Test fun `an expired token is refreshed transparently and the list is returned`() = runTest {
        h.server.enqueue(json(401, "{}"))
        h.server.enqueue(json(200, tokenJson("access-2", "refresh-2")))
        h.server.enqueue(json(200, Fixtures.list(Fixtures.invite)))

        assertEquals(1, h.requests.pending().value().size)
        h.server.takeRequest(); h.server.takeRequest()
        assertEquals("Bearer access-2", h.server.takeRequest().getHeader("Authorization"))
    }

    @Test fun `malformed list responses are reported as invalid`() = runTest {
        for (body in listOf("""{"requests":"nope"}""", "<html>gateway</html>", """{"requests":[{"kind":"payment"}]}""", "")) {
            h.server.enqueue(json(200, body))
            assertEquals("body: $body", RequestFailure.InvalidResponse, h.requests.pending().failure())
        }
        assertEquals("a bad response never signs the admin out", AuthState.SignedIn("admin@axe.local"), h.sessions.state.value)
    }

    // ---- detail ----------------------------------------------------------------------------------------

    @Test fun `detail of a payment request carries the screenshot flag`() = runTest {
        h.server.enqueue(json(200, Fixtures.detail(Fixtures.payment, hasScreenshot = true)))

        val r = h.requests.detail(Fixtures.PAYMENT_ID).value()

        assertEquals(RequestKind.Payment, r.kind)
        assertEquals(true, r.hasScreenshot)
        assertEquals("/functions/v1/admin/requests/${Fixtures.PAYMENT_ID}", h.server.takeRequest().path)
    }

    @Test fun `detail of an invitation request without a screenshot`() = runTest {
        h.server.enqueue(json(200, Fixtures.detail(Fixtures.invite, hasScreenshot = false)))

        val r = h.requests.detail(Fixtures.INVITE_ID).value()

        assertEquals(RequestKind.Invite, r.kind)
        assertEquals("PAPAJI500", r.inviteCode)
        assertEquals(false, r.hasScreenshot)
    }

    @Test fun `detail that is no longer pending is NotFound`() = runTest {
        h.server.enqueue(json(404, """{"error":"This request was already handled or has expired."}"""))
        assertEquals(RequestFailure.NotFound, h.requests.detail(Fixtures.PAYMENT_ID).failure())
    }

    @Test fun `an id can not escape its path segment`() = runTest {
        h.server.enqueue(json(404, "{}"))
        h.requests.detail("../devices?x=1")
        val path = h.server.takeRequest().path!!
        assertTrue(path, path.startsWith("/functions/v1/admin/requests/"))
        assertFalse(path, path.contains("/devices"))
        assertFalse(path.contains("?x=1"))
    }

    // ---- screenshot ------------------------------------------------------------------------------------

    @Test fun `screenshot bytes are returned with the authenticated request`() = runTest {
        h.server.enqueue(MockResponseBytes.png(Fixtures.png))

        val shot = h.requests.screenshot(Fixtures.PAYMENT_ID).value()

        assertArrayEquals(Fixtures.png, shot.bytes)
        assertEquals("image/png", shot.contentType)
        val sent = h.server.takeRequest()
        assertEquals("/functions/v1/admin/requests/${Fixtures.PAYMENT_ID}/screenshot", sent.path)
        assertEquals("Bearer access-1", sent.getHeader("Authorization"))
    }

    @Test fun `missing screenshot is NotFound`() = runTest {
        h.server.enqueue(json(404, """{"error":"No screenshot."}"""))
        assertEquals(RequestFailure.NotFound, h.requests.screenshot(Fixtures.PAYMENT_ID).failure())
    }

    @Test fun `empty screenshot body is invalid`() = runTest {
        h.server.enqueue(MockResponseBytes.png(ByteArray(0)))
        assertEquals(RequestFailure.InvalidResponse, h.requests.screenshot(Fixtures.PAYMENT_ID).failure())
    }

    @Test fun `oversized screenshot is refused`() = runTest {
        h.server.enqueue(MockResponseBytes.png(ByteArray(9 * 1024 * 1024)))
        assertEquals(RequestFailure.InvalidResponse, h.requests.screenshot(Fixtures.PAYMENT_ID).failure())
    }

    @Test fun `screenshot network failure`() = runTest {
        h.server.shutdown()
        assertEquals(RequestFailure.Network, h.requests.screenshot(Fixtures.PAYMENT_ID).failure())
    }

    // ---- decisions ---------------------------------------------------------------------------------------

    @Test fun `approval sends only the decision and ignores the grant expiry in the response`() = runTest {
        h.server.enqueue(json(200, """{"ok":true,"decision":"approve","expiresAt":1790000000000}"""))

        assertEquals(Unit, h.requests.decide(Fixtures.PAYMENT_ID, Decision.Approve).value())

        val sent = h.server.takeRequest()
        assertEquals("POST", sent.method)
        assertEquals("/functions/v1/admin/requests/${Fixtures.PAYMENT_ID}/decision", sent.path)
        assertEquals("""{"decision":"approve"}""", sent.body.readUtf8())
        assertEquals("Bearer access-1", sent.getHeader("Authorization"))
    }

    @Test fun `rejection sends reject`() = runTest {
        h.server.enqueue(json(200, """{"ok":true,"decision":"reject","expiresAt":null}"""))
        assertEquals(Unit, h.requests.decide(Fixtures.INVITE_ID, Decision.Reject).value())
        assertEquals("""{"decision":"reject"}""", h.server.takeRequest().body.readUtf8())
    }

    @Test fun `already handled is a Conflict`() = runTest {
        h.server.enqueue(json(409, """{"error":"This request was already handled."}"""))
        assertEquals(RequestFailure.Conflict, h.requests.decide(Fixtures.PAYMENT_ID, Decision.Approve).failure())
    }

    @Test fun `bad request is a server failure`() = runTest {
        h.server.enqueue(json(400, """{"error":"Decision must be approve or reject."}"""))
        assertEquals(RequestFailure.Server, h.requests.decide(Fixtures.PAYMENT_ID, Decision.Approve).failure())
    }

    @Test fun `unauthorized decision ends the session without deciding`() = runTest {
        h.server.enqueue(json(401, "{}"))
        h.server.enqueue(json(200, tokenJson("access-2", "refresh-2")))
        h.server.enqueue(json(401, "{}"))

        assertEquals(RequestFailure.SessionEnded, h.requests.decide(Fixtures.PAYMENT_ID, Decision.Approve).failure())
        assertEquals(AuthState.SignedOut(SignOutReason.AccessRevoked), h.sessions.state.value)
    }

    @Test fun `a decision response that does not confirm the decision is invalid`() = runTest {
        h.server.enqueue(json(200, """{"ok":true,"decision":"reject"}"""))
        assertEquals(RequestFailure.InvalidResponse, h.requests.decide(Fixtures.PAYMENT_ID, Decision.Approve).failure())
        h.server.enqueue(json(200, """{"ok":false}"""))
        assertEquals(RequestFailure.InvalidResponse, h.requests.decide(Fixtures.PAYMENT_ID, Decision.Approve).failure())
    }

    @Test fun `decision network failure`() = runTest {
        h.server.shutdown()
        assertEquals(RequestFailure.Network, h.requests.decide(Fixtures.PAYMENT_ID, Decision.Reject).failure())
        assertNotNull(h.store.session)
    }
}

private object MockResponseBytes {
    fun png(bytes: ByteArray) = okhttp3.mockwebserver.MockResponse().setResponseCode(200)
        .setHeader("Content-Type", "image/png").setHeader("Cache-Control", "no-store")
        .setBody(Buffer().write(bytes))
}
