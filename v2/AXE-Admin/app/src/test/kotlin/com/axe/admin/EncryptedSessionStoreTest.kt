package com.axe.admin

import com.axe.admin.auth.AesGcmCipher
import com.axe.admin.auth.BlobStorage
import com.axe.admin.auth.EncryptedSessionStore
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.util.Base64
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey

private class MemoryBlob : BlobStorage {
    var value: String? = null
    override fun read() = value
    override fun write(value: String) { this.value = value }
    override fun delete() { value = null }
}

/** Real AES-256-GCM with a software key; production swaps in the Android Keystore key. */
class EncryptedSessionStoreTest {
    private fun newKey(): SecretKey = KeyGenerator.getInstance("AES").apply { init(256) }.generateKey()
    private val session = sessionOf(TestClock(), access = "SECRET-ACCESS-TOKEN", refresh = "SECRET-REFRESH-TOKEN")

    @Test fun `session round-trips`() {
        val key = newKey()
        val store = EncryptedSessionStore(MemoryBlob(), AesGcmCipher { key })
        store.save(session)
        assertEquals(session, store.load())
    }

    @Test fun `nothing readable is stored`() {
        val key = newKey()
        val blob = MemoryBlob()
        EncryptedSessionStore(blob, AesGcmCipher { key }).save(session)

        val raw = String(Base64.getDecoder().decode(blob.value), Charsets.ISO_8859_1)
        assertFalse(raw.contains("SECRET-ACCESS-TOKEN"))
        assertFalse(raw.contains("SECRET-REFRESH-TOKEN"))
        assertFalse(raw.contains("admin@axe.local"))
        assertFalse(blob.value!!.contains("SECRET"))
    }

    @Test fun `every save uses a fresh IV`() {
        val key = newKey()
        val blob = MemoryBlob()
        val store = EncryptedSessionStore(blob, AesGcmCipher { key })
        store.save(session); val first = blob.value
        store.save(session)
        assertFalse(first == blob.value)
    }

    @Test fun `a tampered blob is discarded and treated as signed out`() {
        val key = newKey()
        val blob = MemoryBlob()
        val store = EncryptedSessionStore(blob, AesGcmCipher { key })
        store.save(session)
        val bytes = Base64.getDecoder().decode(blob.value)
        bytes[bytes.size - 1] = (bytes[bytes.size - 1].toInt() xor 1).toByte()
        blob.value = Base64.getEncoder().encodeToString(bytes)

        assertNull(store.load())
        assertNull("the corrupt blob is removed", blob.value)
    }

    @Test fun `a blob encrypted with a different key is discarded`() {
        val blob = MemoryBlob()
        val keyA = newKey()
        EncryptedSessionStore(blob, AesGcmCipher { keyA }).save(session)

        val keyB = newKey()
        assertNull(EncryptedSessionStore(blob, AesGcmCipher { keyB }).load())
        assertNull(blob.value)
    }

    @Test fun `garbage that is not Base64 is discarded`() {
        val key = newKey()
        val blob = MemoryBlob().also { it.value = "!!! not base64 !!!" }
        assertNull(EncryptedSessionStore(blob, AesGcmCipher { key }).load())
        assertNull(blob.value)
    }

    @Test fun `clear removes the session`() {
        val key = newKey()
        val store = EncryptedSessionStore(MemoryBlob(), AesGcmCipher { key })
        store.save(session)
        assertNotNull(store.load())
        store.clear()
        assertNull(store.load())
    }
}
