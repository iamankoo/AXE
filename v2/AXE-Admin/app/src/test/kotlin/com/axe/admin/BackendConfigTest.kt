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

    @Test fun `loopback http is valid for local development`() {
        assertTrue(BackendConfig("http://127.0.0.1:54321", "anon").isValid)
        assertTrue(BackendConfig("http://localhost:54321", "anon").isValid)
    }

    @Test fun `remote cleartext http is not valid`() = assertFalse(BackendConfig("http://abc.supabase.co", "anon").isValid)

    @Test fun `missing values are not valid`() {
        assertFalse(BackendConfig("", "anon").isValid)
        assertFalse(BackendConfig("https://abc.supabase.co", "").isValid)
        assertFalse(BackendConfig("not a url", "anon").isValid)
    }
}
