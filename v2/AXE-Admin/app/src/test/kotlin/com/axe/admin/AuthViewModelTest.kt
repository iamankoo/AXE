package com.axe.admin

import com.axe.admin.model.AuthState
import com.axe.admin.viewmodel.AuthViewModel
import com.axe.admin.viewmodel.LoginError
import com.axe.admin.viewmodel.LoginUiState
import com.axe.admin.repository.LoginFailure
import androidx.lifecycle.viewModelScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancelAndJoin
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test

@OptIn(ExperimentalCoroutinesApi::class)
class AuthViewModelTest {
    private lateinit var h: Harness
    private lateinit var vm: AuthViewModel

    @Before fun setUp() {
        Dispatchers.setMain(UnconfinedTestDispatcher())
        h = Harness().also { it.sessions.restore() }
        vm = AuthViewModel(h.auth)
    }

    @After fun tearDown() {
        // Login/logout run real network work on IO threads. Finish (cancel + join) everything the ViewModel started BEFORE
        // the test Main dispatcher is reset, otherwise a late coroutine resumes onto a missing Main dispatcher on a worker
        // thread and the uncaught exception is blamed on whichever test runs next (seen only under CPU load).
        kotlinx.coroutines.runBlocking { vm.viewModelScope.coroutineContext[kotlinx.coroutines.Job]?.cancelAndJoin() }
        h.shutdown()
        Dispatchers.resetMain()
    }

    /** Login runs on a real IO thread (OkHttp), so poll briefly for the outcome. */
    private fun awaitIdle() {
        val deadline = System.currentTimeMillis() + 5_000
        while (vm.login.value.inProgress && System.currentTimeMillis() < deadline) Thread.sleep(10)
        check(!vm.login.value.inProgress) { "login did not finish" }
    }

    @Test fun `blank fields are rejected without a network call`() {
        vm.login("", "pw")
        assertEquals(LoginUiState(inputError = LoginError.MissingFields), vm.login.value)
        vm.login("a@b.c", "")
        assertEquals(LoginUiState(inputError = LoginError.MissingFields), vm.login.value)
        assertEquals(0, h.server.requestCount)
    }

    @Test fun `successful login signs in and leaves no error`() {
        h.server.enqueue(json(200, tokenJson("acc", "ref")))
        h.server.enqueue(json(200, """{"ok":true}"""))

        vm.login("admin@axe.local", "pw")
        awaitIdle()

        assertEquals(AuthState.SignedIn("admin@axe.local"), vm.authState.value)
        assertEquals(LoginUiState(), vm.login.value)
    }

    @Test fun `failed login exposes the failure and stays signed out`() {
        h.server.enqueue(json(400, """{"error":"invalid_grant"}"""))

        vm.login("admin@axe.local", "wrong")
        awaitIdle()

        assertEquals(LoginFailure.InvalidCredentials, vm.login.value.failure)
        assertEquals(AuthState.SignedOut(null), vm.authState.value)
    }

    @Test fun `editing dismisses the error`() {
        vm.login("", "")
        vm.dismissError()
        assertNull(vm.login.value.inputError)
    }

    @Test fun `logout signs out`() {
        h.store.session = sessionOf(h.clock)
        h.sessions.restore()
        h.server.enqueue(json(204, ""))
        assertEquals(AuthState.SignedIn("admin@axe.local"), vm.authState.value)

        vm.logout()
        val deadline = System.currentTimeMillis() + 5_000
        while (h.store.session != null && System.currentTimeMillis() < deadline) Thread.sleep(10)

        assertEquals(AuthState.SignedOut(null), vm.authState.value)
        assertNull(h.store.session)
    }
}
