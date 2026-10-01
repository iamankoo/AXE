package com.axe.admin

import com.axe.admin.network.BackendConfig
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class BackendConfigTest {
    @Test fun `derives auth and admin urls`() {
        val c = BackendConfig("https://abc.supabase.co/", "anon")
        assertEquals("https://abc.supabase.co/auth/v1", c.authUrl)
        assertEquals("https://abc.supabase.co/functions/v1/admin", c.adminUrl)
    }

    @Test fun `https is valid`() = assertTrue(BackendConfig("https://abc.supabase.co", "anon").isValid)

    @Test fun `loopback http is valid only when the build allows it (debug builds)`() {
        assertTrue(BackendConfig("http://127.0.0.1:54321", "anon", allowLoopback = true).isValid)
        assertTrue(BackendConfig("http://localhost:54321", "anon", allowLoopback = true).isValid)
        assertTrue(BackendConfig("http://10.0.2.2:54321", "anon", allowLoopback = true).isValid)
    }

    @Test fun `a release build never accepts a loopback or local server, even over https`() {
        for (url in listOf("http://127.0.0.1:54321", "http://localhost:54321", "https://localhost", "https://127.0.0.1", "http://10.0.2.2:54321")) {
            assertFalse(url, BackendConfig(url, "anon").isValid)
            assertFalse(url, BackendConfig(url, "anon", allowLoopback = false).isValid)
        }
    }

    @Test fun `remote cleartext http is not valid`() = assertFalse(BackendConfig("http://abc.supabase.co", "anon").isValid)

    @Test fun `missing values are not valid`() {
        assertFalse(BackendConfig("", "anon").isValid)
        assertFalse(BackendConfig("https://abc.supabase.co", "").isValid)
        assertFalse(BackendConfig("not a url", "anon").isValid)
    }
}
