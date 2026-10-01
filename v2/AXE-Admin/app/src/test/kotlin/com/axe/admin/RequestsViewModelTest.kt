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
import com.axe.admin.viewmodel.ScreenshotProblem
import com.axe.admin.viewmodel.ScreenshotState
import com.axe.admin.viewmodel.UiMessage
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.StandardTestDispatcher
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.time.Instant

private fun request(id: String, kind: RequestKind = RequestKind.Payment, hasScreenshot: Boolean? = null) = AccessRequest(
    id = id, kind = kind, name = "Name $id", plan = "5h", planLabel = "5 Hours", expectedAmount = 199.0,
    amountPaid = if (kind == RequestKind.Payment) 199.0 else null, amountMismatch = false,
    utr = if (kind == RequestKind.Payment) "UTR123456" else null,
    inviteCode = if (kind == RequestKind.Invite) "TESTCODE01" else null,
    duplicateUtr = false, createdAt = Instant.parse("2026-09-30T16:20:11Z"), expiresAt = null, hasScreenshot = hasScreenshot,
)

/** A scriptable backend: every call is counted, answers can be swapped, and calls can be held open. */
private class FakeRepository : RequestRepository {
    var pendingResult: Outcome<List<AccessRequest>> = Outcome.Success(emptyList())
    var detailResult: (String) -> Outcome<AccessRequest> = { Outcome.Failure(RequestFailure.NotFound) }
    var screenshotResult: Outcome<ScreenshotBytes> = Outcome.Success(ScreenshotBytes(byteArrayOf(1, 2, 3), "image/png"))
    var decideResult: Outcome<Unit> = Outcome.Success(Unit)
    var pendingGate: CompletableDeferred<Unit>? = null
    /** When set, every pending() call gets its own gate (in call order) so tests can finish calls in any order. */
    var holdEachPending = false
    val heldPending = mutableListOf<CompletableDeferred<Unit>>()
    var decideGate: CompletableDeferred<Unit>? = null

    var pendingCalls = 0
    val decisions = mutableListOf<Pair<String, Decision>>()

    override suspend fun pending(): Outcome<List<AccessRequest>> {
        pendingCalls++
        val snapshot = pendingResult // the answer as of when the call was made (a stale call stays stale)
        if (holdEachPending) CompletableDeferred<Unit>().also { heldPending += it }.await()
        pendingGate?.await()
        return snapshot
    }

    override suspend fun detail(id: String) = detailResult(id)
    override suspend fun screenshot(id: String) = screenshotResult
    override suspend fun decide(id: String, decision: Decision): Outcome<Unit> {
        decisions += id to decision
        decideGate?.await()
        return decideResult
    }
}

@OptIn(ExperimentalCoroutinesApi::class)
class RequestsViewModelTest {
    private val dispatcher = StandardTestDispatcher()
    private val signedIn = MutableStateFlow(true)
    private lateinit var repo: FakeRepository
    private lateinit var vm: RequestsViewModel
    private var imageOk = true

    @Before fun setUp() {
        Dispatchers.setMain(dispatcher)
        repo = FakeRepository()
        vm = RequestsViewModel(repo, signedIn) { imageOk }
    }

    @After fun tearDown() = Dispatchers.resetMain()

    private fun idle() = dispatcher.scheduler.advanceUntilIdle()

    private fun withList(vararg items: AccessRequest) {
        repo.pendingResult = Outcome.Success(items.toList())
        vm.refresh(); idle()
    }

    private fun open(item: AccessRequest) {
        repo.detailResult = { Outcome.Success(item) }
        vm.open(item.id); idle()
    }

    // ---- list -----------------------------------------------------------------------------------------

    @Test fun `loading then success`() {
        repo.pendingResult = Outcome.Success(listOf(request("a"), request("b", RequestKind.Invite)))
        assertFalse(vm.list.value.loaded)

        vm.loadIfNeeded()
        assertTrue("loading state is visible before the call finishes", vm.list.value.refreshing)
        idle()

        val s = vm.list.value
        assertFalse(s.refreshing); assertTrue(s.loaded); assertNull(s.error)
        assertEquals(listOf("a", "b"), s.requests.map { it.id })
    }

    @Test fun `empty list is loaded, not loading`() {
        vm.loadIfNeeded(); idle()
        assertTrue(vm.list.value.loaded)
        assertTrue(vm.list.value.requests.isEmpty())
    }

    @Test fun `loading then error`() {
        repo.pendingResult = Outcome.Failure(RequestFailure.Network)
        vm.loadIfNeeded(); idle()

        val s = vm.list.value
        assertFalse(s.refreshing); assertFalse(s.loaded)
        assertEquals(RequestFailure.Network, s.error)
    }

    @Test fun `a failed refresh keeps the previous list`() {
        withList(request("a"))
        repo.pendingResult = Outcome.Failure(RequestFailure.Server)
        vm.refresh(); idle()

        assertEquals(listOf("a"), vm.list.value.requests.map { it.id })
        assertEquals(RequestFailure.Server, vm.list.value.error)
        assertTrue(vm.list.value.loaded)
    }

    @Test fun `overlapping refreshes and recomposition-driven loads make a single call`() {
        repo.pendingGate = CompletableDeferred()
        vm.loadIfNeeded(); vm.loadIfNeeded(); vm.refresh(); vm.refresh(); idle()
        assertEquals(1, repo.pendingCalls)

        repo.pendingGate!!.complete(Unit); idle()
        vm.loadIfNeeded()
        assertEquals("already loaded: no further call", 1, repo.pendingCalls)
    }

    // ---- selection / detail ---------------------------------------------------------------------------

    @Test fun `selecting a request shows it from the summary, then the detail with its screenshot`() {
        val item = request("a", hasScreenshot = true)
        withList(request("a"))
        repo.detailResult = { Outcome.Success(item) }

        vm.open("a")
        val loading = vm.detail.value as DetailState.Loading
        assertEquals("a", loading.id)
        assertEquals("the list row is shown while the detail loads", "a", loading.summary?.id)
        idle()

        val ready = vm.detail.value as DetailState.Ready
        assertEquals(item, ready.request)
        assertTrue(ready.screenshot is ScreenshotState.Loaded)
        assertEquals(DecisionState.Idle, ready.decision)
    }

    @Test fun `a request without a screenshot does not fetch one`() {
        open(request("a", RequestKind.Invite, hasScreenshot = false))
        assertEquals(ScreenshotState.None, (vm.detail.value as DetailState.Ready).screenshot)
    }

    @Test fun `an undecodable screenshot is reported, not rendered`() {
        imageOk = false
        open(request("a", hasScreenshot = true))
        assertEquals(ScreenshotState.Unavailable(ScreenshotProblem.Invalid), (vm.detail.value as DetailState.Ready).screenshot)
    }

    @Test fun `screenshot network failure can be retried`() {
        repo.screenshotResult = Outcome.Failure(RequestFailure.Network)
        open(request("a", hasScreenshot = true))
        assertEquals(ScreenshotState.Unavailable(ScreenshotProblem.Network), (vm.detail.value as DetailState.Ready).screenshot)

        repo.screenshotResult = Outcome.Success(ScreenshotBytes(byteArrayOf(9), "image/png"))
        vm.retryScreenshot(); idle()
        assertTrue((vm.detail.value as DetailState.Ready).screenshot is ScreenshotState.Loaded)
    }

    @Test fun `detail that is gone fails as NotFound and refreshes the list`() {
        withList(request("a"))
        val before = repo.pendingCalls
        repo.pendingResult = Outcome.Success(emptyList())

        vm.open("a"); idle()

        assertEquals(RequestFailure.NotFound, (vm.detail.value as DetailState.Failed).failure)
        assertEquals(before + 1, repo.pendingCalls)
        assertTrue(vm.list.value.requests.isEmpty())
    }

    @Test fun `detail failure can be retried`() {
        repo.detailResult = { Outcome.Failure(RequestFailure.Network) }
        vm.open("a"); idle()
        assertTrue(vm.detail.value is DetailState.Failed)

        repo.detailResult = { Outcome.Success(request("a")) }
        vm.retryDetail(); idle()
        assertTrue(vm.detail.value is DetailState.Ready)
    }

    @Test fun `closing drops the detail and its screenshot`() {
        open(request("a", hasScreenshot = true))
        vm.close()
        assertEquals(DetailState.Closed, vm.detail.value)
    }

    // ---- decisions ----------------------------------------------------------------------------------------

    @Test fun `successful approval removes the request, closes the detail and tells the admin`() {
        val a = request("a", hasScreenshot = false); val b = request("b")
        withList(a, b)
        open(a)
        repo.pendingResult = Outcome.Success(listOf(b)) // what the server reports after the decision

        vm.decide(Decision.Approve); idle()

        assertEquals(listOf("a" to Decision.Approve), repo.decisions)
        assertEquals(DetailState.Closed, vm.detail.value)
        assertEquals(listOf("b"), vm.list.value.requests.map { it.id })
        assertEquals(UiMessage.Approved, vm.message.value)
        vm.messageShown(); assertNull(vm.message.value)
    }

    @Test fun `successful rejection`() {
        val a = request("a", hasScreenshot = false)
        withList(a); open(a)
        repo.pendingResult = Outcome.Success(emptyList())

        vm.decide(Decision.Reject); idle()

        assertEquals(listOf("a" to Decision.Reject), repo.decisions)
        assertEquals(UiMessage.Rejected, vm.message.value)
        assertTrue(vm.list.value.requests.isEmpty())
    }

    @Test fun `a failed decision keeps the request open and restores the controls`() {
        val a = request("a", hasScreenshot = false)
        withList(a); open(a)
        repo.decideResult = Outcome.Failure(RequestFailure.Network)

        vm.decide(Decision.Approve); idle()

        val ready = vm.detail.value as DetailState.Ready
        assertEquals(a, ready.request)
        assertEquals(DecisionState.Failed(Decision.Approve, RequestFailure.Network), ready.decision)
        assertEquals("the request stays in the list", listOf("a"), vm.list.value.requests.map { it.id })
        assertNull(vm.message.value)

        vm.dismissDecisionError()
        assertEquals(DecisionState.Idle, (vm.detail.value as DetailState.Ready).decision)

        repo.decideResult = Outcome.Success(Unit)
        vm.decide(Decision.Approve); idle() // and it can be tried again
        assertEquals(DetailState.Closed, vm.detail.value)
    }

    @Test fun `double tapping sends one decision and ignores back and close while it is in flight`() {
        val a = request("a", hasScreenshot = false)
        withList(a); open(a)
        repo.decideGate = CompletableDeferred()

        vm.decide(Decision.Approve)
        vm.decide(Decision.Approve)
        vm.decide(Decision.Reject)
        idle()

        assertEquals("only one decision reaches the server", 1, repo.decisions.size)
        assertEquals(DecisionState.Submitting(Decision.Approve), (vm.detail.value as DetailState.Ready).decision)
        vm.close()
        assertTrue("can't leave mid-decision", vm.detail.value is DetailState.Ready)

        repo.decideGate!!.complete(Unit); idle()
        assertEquals(DetailState.Closed, vm.detail.value)
    }

    @Test fun `a request already handled elsewhere is not reported as a success and the list refreshes`() {
        val a = request("a", hasScreenshot = false)
        withList(a); open(a)
        val calls = repo.pendingCalls
        repo.decideResult = Outcome.Failure(RequestFailure.Conflict)
        repo.pendingResult = Outcome.Success(emptyList())

        vm.decide(Decision.Approve); idle()

        assertEquals(UiMessage.AlreadyHandled, vm.message.value)
        assertEquals(DetailState.Closed, vm.detail.value)
        assertEquals(calls + 1, repo.pendingCalls)
        assertTrue(vm.list.value.requests.isEmpty())
    }

    @Test fun `a request deleted while viewing behaves like already handled`() {
        val a = request("a", hasScreenshot = false)
        withList(a); open(a)
        repo.decideResult = Outcome.Failure(RequestFailure.NotFound)

        vm.decide(Decision.Reject); idle()

        assertEquals(UiMessage.AlreadyHandled, vm.message.value)
        assertEquals(DetailState.Closed, vm.detail.value)
    }

    @Test fun `a refresh that started before the decision cannot bring the decided request back`() {
        val a = request("a", hasScreenshot = false)
        withList(a); open(a)
        repo.holdEachPending = true
        vm.refresh(); idle()                                // stale refresh in flight: it saw [a]
        repo.pendingResult = Outcome.Success(emptyList())   // the server's truth after the decision

        vm.decide(Decision.Approve); idle()                 // starts the post-decision refresh
        val fresh = repo.heldPending.last()
        fresh.complete(Unit); idle()
        repo.heldPending.first().complete(Unit); idle()     // the stale call finishes LAST

        assertEquals(2, repo.heldPending.size)
        assertTrue("the decided request must stay gone", vm.list.value.requests.isEmpty())
    }

    @Test fun `after a decision the list reflects the server's answer, not just a local hide`() {
        val a = request("a", hasScreenshot = false); val b = request("b")
        withList(a, b); open(a)

        // The server still reports "a" (e.g. its deletion did not happen): the UI must not pretend otherwise.
        repo.pendingResult = Outcome.Success(listOf(a, b))
        vm.decide(Decision.Approve); idle()
        assertEquals(listOf("a", "b"), vm.list.value.requests.map { it.id })

        // And when the server has deleted it, the refreshed list drops it.
        repo.pendingResult = Outcome.Success(listOf(b))
        vm.refresh(); idle()
        assertEquals(listOf("b"), vm.list.value.requests.map { it.id })
    }

    // ---- sign-out ---------------------------------------------------------------------------------------------

    @Test fun `signing out wipes the list, the open request and its screenshot`() {
        withList(request("a", hasScreenshot = true))
        open(request("a", hasScreenshot = true))

        signedIn.value = false; idle()

        assertEquals(com.axe.admin.viewmodel.ListState(), vm.list.value)
        assertEquals(DetailState.Closed, vm.detail.value)
    }
}
