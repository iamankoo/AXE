package com.axe.admin

import com.axe.admin.model.AccessRequest
import com.axe.admin.model.Decision
import com.axe.admin.model.RequestKind
import com.axe.admin.network.ScreenshotBytes
import com.axe.admin.repository.Outcome
import com.axe.admin.repository.RequestFailure
import com.axe.admin.repository.RequestRepository
import com.axe.admin.viewmodel.DecisionState
import com.axe.admin.viewmodel.DetailState
import com.axe.admin.viewmodel.RequestsViewModel
import com.axe.admin.viewmodel.UiMessage
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.time.Instant

private const val A = "0b6f2a54-5a43-4f43-9a52-7c1d7c6f9a01"
private const val B = "9d3c1e2f-88a7-4c60-b1f4-3a2d5e6f7b02"

private fun req(id: String, name: String = "Test $id") = AccessRequest(
    id = id, kind = RequestKind.Payment, name = name, plan = "5h", planLabel = "5 Hours", expectedAmount = 199.0,
    amountPaid = 199.0, amountMismatch = false, utr = "UTR123456", inviteCode = null, duplicateUtr = false,
    createdAt = Instant.parse("2026-09-30T16:20:11Z"), expiresAt = null, hasScreenshot = false,
)

/** A scriptable backend that records every call, so the tests can prove what the ViewModel asks the server. */
private class Backend : RequestRepository {
    var list: Outcome<List<AccessRequest>> = Outcome.Success(emptyList())
    var detailResult: (String) -> Outcome<AccessRequest> = { Outcome.Failure(RequestFailure.NotFound) }
    var decideGate: CompletableDeferred<Unit>? = null
    val listCalls = mutableListOf<Unit>()
    val detailCalls = mutableListOf<String>()

    override suspend fun pending(): Outcome<List<AccessRequest>> { listCalls += Unit; return list }
    override suspend fun detail(id: String): Outcome<AccessRequest> { detailCalls += id; return detailResult(id) }
    override suspend fun screenshot(id: String): Outcome<ScreenshotBytes> = Outcome.Failure(RequestFailure.NotFound)
    override suspend fun decide(id: String, decision: Decision): Outcome<Unit> { decideGate?.await(); return Outcome.Success(Unit) }
}

@OptIn(ExperimentalCoroutinesApi::class)
class RequestsViewModelPushTest {
    private val dispatcher = StandardTestDispatcher()
    private val signedIn = MutableStateFlow(true)
    private val pushes = MutableSharedFlow<String>(extraBufferCapacity = 16)
    private lateinit var backend: Backend
    private lateinit var vm: RequestsViewModel

    @Before fun setUp() {
        Dispatchers.setMain(dispatcher)
        backend = Backend()
        vm = RequestsViewModel(backend, signedIn, pushes) { true }
        idle() // let the collectors start
    }

    @After fun tearDown() = Dispatchers.resetMain()

    private fun idle() = dispatcher.scheduler.advanceUntilIdle()

    // ---- foreground pushes -----------------------------------------------------------------------------------

    @Test fun `a push while the app is open tells the admin once and refreshes the list from the server`() {
        backend.list = Outcome.Success(listOf(req(A)))

        pushes.tryEmit(A); idle()

        assertEquals(UiMessage.NewRequest, vm.message.value)
        assertEquals("the list is fetched from the backend", 1, backend.listCalls.size)
        assertEquals(listOf(A), vm.list.value.requests.map { it.id })
    }

    @Test fun `the same notification delivered repeatedly causes one message and one refresh, never a loop`() {
        pushes.tryEmit(A); idle()
        vm.messageShown()

        repeat(5) { pushes.tryEmit(A) }
        idle()

        assertNull("no second banner for the same event", vm.message.value)
        assertEquals("no further refreshes", 1, backend.listCalls.size)
    }

    @Test fun `different requests each get their own notice, and refreshes in flight are shared`() {
        val gate = CompletableDeferred<Unit>()
        val slow = object : RequestRepository by backend {
            override suspend fun pending(): Outcome<List<AccessRequest>> { backend.listCalls += Unit; gate.await(); return backend.list }
        }
        val isolatedPushes = MutableSharedFlow<String>(extraBufferCapacity = 16) // the setUp ViewModel must not also react
        val slowVm = RequestsViewModel(slow, signedIn, isolatedPushes) { true }
        idle()

        isolatedPushes.tryEmit(A); isolatedPushes.tryEmit(B); idle()
        assertEquals("both pushes share the refresh that is already running", 1, backend.listCalls.size)
        assertEquals(UiMessage.NewRequest, slowVm.message.value)

        gate.complete(Unit); idle()
        assertTrue(slowVm.list.value.loaded)
    }

    @Test fun `a push never puts anything from the notification on screen`() {
        // The ViewModel only receives an id; the list shows exactly what the backend returns.
        backend.list = Outcome.Success(emptyList())
        pushes.tryEmit(A); idle()
        assertTrue(vm.list.value.requests.isEmpty())
        assertEquals(DetailState.Closed, vm.detail.value)
    }

    @Test fun `signing out forgets seen notifications and wipes the state`() {
        pushes.tryEmit(A); idle()
        signedIn.value = false; idle()
        assertEquals(com.axe.admin.viewmodel.ListState(), vm.list.value)
        assertNull(vm.message.value)

        signedIn.value = true; idle()
        pushes.tryEmit(A); idle() // after a new sign-in the same id is news again
        assertEquals(UiMessage.NewRequest, vm.message.value)
    }

    // ---- notification taps ------------------------------------------------------------------------------------

    @Test fun `a tap opens the request that the backend returns, not anything from the notification`() {
        backend.detailResult = { Outcome.Success(req(it, name = "Name from the server")) }
        backend.list = Outcome.Success(listOf(req(A, "Name from the server")))

        vm.openFromNotification(A)
        assertTrue("shown as loading until the server answers", vm.detail.value is DetailState.Loading)
        idle()

        val ready = vm.detail.value as DetailState.Ready
        assertEquals("Name from the server", ready.request.name)
        assertEquals("the request was fetched by id from the backend", listOf(A), backend.detailCalls)
        assertEquals("and the list was refreshed too", 1, backend.listCalls.size)
    }

    @Test fun `a stale notification says the request is gone and returns to the refreshed list`() {
        backend.detailResult = { Outcome.Failure(RequestFailure.NotFound) } // approved, rejected, expired or deleted meanwhile
        backend.list = Outcome.Success(listOf(req(B)))

        vm.openFromNotification(A); idle()

        assertEquals("no error screen and no cached details", DetailState.Closed, vm.detail.value)
        assertEquals(UiMessage.RequestUnavailable, vm.message.value)
        assertEquals("the list reflects the server", listOf(B), vm.list.value.requests.map { it.id })
    }

    @Test fun `a tap when the server can't be reached offers a retry instead of inventing data`() {
        backend.detailResult = { Outcome.Failure(RequestFailure.Network) }

        vm.openFromNotification(A); idle()

        val failed = vm.detail.value as DetailState.Failed
        assertEquals(RequestFailure.Network, failed.failure)
        assertNull("nothing about the request is shown", failed.summary)

        backend.detailResult = { Outcome.Success(req(A)) }
        vm.retryDetail(); idle()
        assertTrue(vm.detail.value is DetailState.Ready)
    }

    @Test fun `a tap never interrupts a decision that is being sent`() {
        backend.detailResult = { Outcome.Success(req(it)) }
        backend.list = Outcome.Success(listOf(req(A)))
        vm.openFromNotification(A); idle()
        backend.decideGate = CompletableDeferred()
        vm.decide(Decision.Approve); idle()
        assertTrue((vm.detail.value as DetailState.Ready).decision is DecisionState.Submitting)

        vm.openFromNotification(B); idle()

        assertEquals("still the same request, still submitting", A, (vm.detail.value as DetailState.Ready).request.id)
        assertEquals("no fetch for the other id", listOf(A), backend.detailCalls)
    }

    @Test fun `tapping a notification for the request that is already open does nothing`() {
        backend.detailResult = { Outcome.Success(req(it)) }
        vm.openFromNotification(A); idle()
        val calls = backend.detailCalls.size

        vm.openFromNotification(A); idle()

        assertEquals(calls, backend.detailCalls.size)
    }

    @Test fun `a tap while another request is open switches to the tapped one`() {
        backend.detailResult = { Outcome.Success(req(it)) }
        vm.openFromNotification(A); idle()
        vm.openFromNotification(B); idle()
        assertEquals(B, (vm.detail.value as DetailState.Ready).request.id)
    }

    @Test fun `a request that was handled after a tap can no longer be opened`() {
        backend.detailResult = { Outcome.Success(req(it)) }
        vm.openFromNotification(A); idle()
        vm.close()
        backend.detailResult = { Outcome.Failure(RequestFailure.NotFound) }

        vm.openFromNotification(A); idle() // the same notification tapped again later

        assertEquals(DetailState.Closed, vm.detail.value)
        assertEquals(UiMessage.RequestUnavailable, vm.message.value)
    }
}
