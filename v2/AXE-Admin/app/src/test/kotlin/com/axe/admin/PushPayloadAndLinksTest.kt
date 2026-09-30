package com.axe.admin

import com.axe.admin.push.DeepLinks
import com.axe.admin.push.NotificationPermissionPolicy
import com.axe.admin.push.PushConfig
import com.axe.admin.push.PushPayload
import com.axe.admin.push.PushStore
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PushPayloadTest {
    private val id = "0b6f2a54-5a43-4f43-9a52-7c1d7c6f9a01"

    @Test fun `a new-request payload with a valid id is accepted`() {
        assertEquals(id, PushPayload.parse("new_request", id)?.requestId)
    }

    @Test fun `the id is normalised`() {
        assertEquals(id, PushPayload.parse("new_request", "  ${id.uppercase()} ")?.requestId)
    }

    @Test fun `unknown or missing types are ignored`() {
        assertNull(PushPayload.parse(null, id))
        assertNull(PushPayload.parse("", id))
        assertNull(PushPayload.parse("payment", id))
        assertNull(PushPayload.parse("NEW_REQUEST", id))
    }

    @Test fun `missing or malformed request ids are ignored`() {
        val bad = listOf(
            null, "", "   ", "abc", id.dropLast(1), id + "0", "../devices", "$id/../x", "$id?x=1", "'; drop table access_requests;--",
            "0b6f2a54-5a43-4f43-9a52-7c1d7c6f9a0z", "<script>", id.replace("-", ""),
        )
        for (value in bad) assertNull("\"$value\" must be rejected", PushPayload.parse("new_request", value))
    }
}

class DeepLinksTest {
    private val clock = TestClock(1_000_000)
    private val links = DeepLinks(clock)

    @Test fun `a tap is handled exactly once`() {
        assertFalse(links.hasPending.value)
        links.offer("a")
        assertTrue(links.hasPending.value)

        assertEquals("a", links.take())
        assertNull("the second take finds nothing", links.take())
        assertFalse(links.hasPending.value)
    }

    @Test fun `a tap made while signed out waits for the sign-in`() {
        links.offer("a")
        clock.now += 60_000 // the admin takes a minute to sign in
        assertEquals("a", links.take())
    }

    @Test fun `a tap that waited too long is dropped instead of surprising the admin later`() {
        links.offer("a")
        clock.now += DeepLinks.MAX_AGE_MS + 1
        assertNull(links.take())
        assertFalse(links.hasPending.value)
    }

    @Test fun `a newer tap replaces an older one`() {
        links.offer("old")
        links.offer("new")
        assertEquals("new", links.take())
        assertNull(links.take())
    }
}

private class MemoryStore : PushStore {
    override var lastRegisteredToken: String? = null
    override var lastRegisteredAtMs: Long = 0
    override var pendingUnregister: String? = null
    override var permissionAsked: Boolean = false
}

class NotificationPermissionPolicyTest {
    @Test fun `older Android versions never need the runtime permission`() {
        val policy = NotificationPermissionPolicy(MemoryStore(), sdkInt = 32)
        assertFalse(policy.requiresRuntimePermission)
        assertFalse(policy.shouldAskNow(granted = false))
    }

    @Test fun `Android 13 asks once when the permission is missing`() {
        val store = MemoryStore()
        val policy = NotificationPermissionPolicy(store, sdkInt = 33)
        assertTrue(policy.requiresRuntimePermission)
        assertTrue(policy.shouldAskNow(granted = false))

        policy.markAsked()

        assertFalse("never prompts again after it was asked", policy.shouldAskNow(granted = false))
        assertTrue(store.permissionAsked)
    }

    @Test fun `no prompt when notifications are already allowed`() {
        assertFalse(NotificationPermissionPolicy(MemoryStore(), sdkInt = 36).shouldAskNow(granted = true))
    }
}

class PushConfigTest {
    @Test fun `push is configured only when all four client identifiers are present`() {
        assertTrue(PushConfig("p", "1:2:android:3", "AIza", "123").isConfigured)
        assertFalse(PushConfig("", "1:2:android:3", "AIza", "123").isConfigured)
        assertFalse(PushConfig("p", "", "AIza", "123").isConfigured)
        assertFalse(PushConfig("p", "1:2:android:3", "", "123").isConfigured)
        assertFalse(PushConfig("p", "1:2:android:3", "AIza", " ").isConfigured)
        assertFalse(PushConfig("", "", "", "").isConfigured)
    }
}
