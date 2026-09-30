package com.axe.admin

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.axe.admin.auth.AesGcmCipher
import com.axe.admin.auth.EncryptedSessionStore
import com.axe.admin.auth.KeystoreKeyProvider
import com.axe.admin.auth.SharedPreferencesBlobStorage
import com.axe.admin.model.Session
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/**
 * Runs on a real device/emulator against the REAL Android Keystore. The JVM tests use a software key, which
 * accepts things a Keystore key rejects (e.g. a caller-provided IV), so this is the test that proves session
 * persistence actually works on Android.
 */
@RunWith(AndroidJUnit4::class)
class KeystoreSessionStoreTest {
    private val context: Context = ApplicationProvider.getApplicationContext()
    private val session = Session("SECRET-ACCESS-TOKEN", "SECRET-REFRESH-TOKEN", 1_900_000_000_000, "user-1", "admin@axe.local")

    private fun store() = EncryptedSessionStore(SharedPreferencesBlobStorage(context), AesGcmCipher(KeystoreKeyProvider::key))

    @Before @After fun wipe() = store().clear()

    @Test fun keystoreCipherRoundTrips() {
        val cipher = AesGcmCipher(KeystoreKeyProvider::key)
        val blob = cipher.encrypt("hello".encodeToByteArray())
        assertEquals("hello", cipher.decrypt(blob).decodeToString())
    }

    @Test fun sessionIsSavedAndLoadedThroughTheKeystore() {
        store().save(session)
        assertEquals(session, store().load()) // a second instance, as after an app restart
    }

    @Test fun storedValueIsCiphertextOnly() {
        store().save(session)
        val raw = context.getSharedPreferences("axe_admin_session", Context.MODE_PRIVATE).getString("blob", null)
        assertNotNull(raw)
        assertFalse(raw!!.contains("SECRET"))
        assertFalse(raw.contains("admin@axe.local"))
    }

    @Test fun clearRemovesTheSession() {
        store().save(session)
        store().clear()
        assertNull(store().load())
    }
}
