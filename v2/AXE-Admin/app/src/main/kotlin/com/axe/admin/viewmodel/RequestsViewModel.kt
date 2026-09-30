package com.axe.admin.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.axe.admin.model.AccessRequest
import com.axe.admin.model.Decision
import com.axe.admin.repository.Outcome
import com.axe.admin.repository.RequestFailure
import com.axe.admin.repository.RequestRepository
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.emptyFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch

/** Checks that downloaded bytes are a decodable image before the UI tries to render them. */
fun interface ImageValidator {
    fun isDecodable(bytes: ByteArray): Boolean
}

data class ListState(
    val requests: List<AccessRequest> = emptyList(),
    val refreshing: Boolean = false,
    /** True once a list has been received at least once (so an empty list can be told from "not loaded"). */
    val loaded: Boolean = false,
    val error: RequestFailure? = null,
)

enum class ScreenshotProblem { Network, Missing, Invalid, Server }

sealed interface ScreenshotState {
    data object None : ScreenshotState
    data object Loading : ScreenshotState

    /** Bytes are held in memory only and dropped when the detail screen closes. */
    class Loaded(val bytes: ByteArray) : ScreenshotState
    data class Unavailable(val problem: ScreenshotProblem) : ScreenshotState
}

sealed interface DecisionState {
    data object Idle : DecisionState
    data class Submitting(val decision: Decision) : DecisionState
    data class Failed(val decision: Decision, val failure: RequestFailure) : DecisionState
}

sealed interface DetailState {
    data object Closed : DetailState
    data class Loading(val id: String, val summary: AccessRequest?) : DetailState
    data class Failed(val id: String, val summary: AccessRequest?, val failure: RequestFailure) : DetailState

    /** The request is still pending as far as the server said when it was fetched; actions are offered. */
    data class Ready(
        val request: AccessRequest,
        val screenshot: ScreenshotState,
        val decision: DecisionState = DecisionState.Idle,
    ) : DetailState
}

/** One-shot notices for the UI (shown once, then acknowledged). */
enum class UiMessage { Approved, Rejected, AlreadyHandled, NewRequest, RequestUnavailable }

/**
 * Screen state and actions for Requests (list and detail). It holds no authorization logic: a decision is just
 * sent to the backend, and the list and detail only ever reflect what the backend returned.
 */
class RequestsViewModel(
    private val repository: RequestRepository,
    signedIn: Flow<Boolean>,
    /** Request ids of "new request" pushes that arrived while the app is in the foreground. */
    newRequests: Flow<String> = emptyFlow(),
    private val imageValidator: ImageValidator,
) : ViewModel() {

    private val _list = MutableStateFlow(ListState())
    val list: StateFlow<ListState> = _list.asStateFlow()

    private val _detail = MutableStateFlow<DetailState>(DetailState.Closed)
    val detail: StateFlow<DetailState> = _detail.asStateFlow()

    private val _message = MutableStateFlow<UiMessage?>(null)
    val message: StateFlow<UiMessage?> = _message.asStateFlow()

    private var refreshJob: Job? = null
    private var detailJob: Job? = null
    private var decisionJob: Job? = null

    private val seenNotifications = LinkedHashSet<String>()

    init {
        // This ViewModel outlives a sign-out (it is Activity-scoped): never keep one admin's data around.
        viewModelScope.launch { signedIn.collect { if (!it) reset() } }
        viewModelScope.launch { newRequests.collect(::onNewRequestSignal) }
    }

    /**
     * A "new request" push arrived while the app is open: tell the admin once and refresh the list from the server. The
     * id is only used to recognise repeats; nothing in the push is displayed.
     */
    private fun onNewRequestSignal(requestId: String) {
        if (!seenNotifications.add(requestId)) return // the same event twice: ignore
        if (seenNotifications.size > MAX_SEEN_NOTIFICATIONS) seenNotifications.remove(seenNotifications.first())
        _message.value = UiMessage.NewRequest
        refresh() // joins a refresh already in flight instead of starting another
    }

    // ---- list ------------------------------------------------------------------------------------

    /** Loads the list once; safe to call from a composition effect (no duplicate in-flight calls). */
    fun loadIfNeeded() {
        if (!_list.value.loaded && refreshJob?.isActive != true) refresh()
    }

    /** Refreshes the list. A refresh already in flight is reused instead of starting another. */
    fun refresh() {
        if (refreshJob?.isActive == true) return
        startRefresh()
    }

    private fun startRefresh() {
        _list.update { it.copy(refreshing = true, error = null) }
        refreshJob = viewModelScope.launch {
            when (val result = repository.pending()) {
                is Outcome.Success ->
                    _list.value = ListState(requests = result.value, refreshing = false, loaded = true, error = null)
                is Outcome.Failure ->
                    _list.update { it.copy(refreshing = false, error = result.failure) }
            }
        }
    }

    // ---- detail ----------------------------------------------------------------------------------

    /**
     * The admin tapped a notification. The id is untrusted: the authoritative request is fetched from the backend (this
     * requires the signed-in session that this ViewModel exists under). If it no longer exists (handled, expired, deleted)
     * the admin is told so and the list is refreshed; nothing is ever shown from the notification itself.
     */
    fun openFromNotification(id: String) {
        val current = _detail.value
        if ((current as? DetailState.Ready)?.decision is DecisionState.Submitting) return // never interrupt a decision
        if (current is DetailState.Ready && current.request.id == id) return
        detailJob?.cancel()
        _detail.value = DetailState.Loading(id, null)
        refresh()
        detailJob = viewModelScope.launch { loadDetail(id, null, fromNotification = true) }
    }

    fun open(id: String) {
        if (_detail.value !is DetailState.Closed) return
        val summary = _list.value.requests.firstOrNull { it.id == id }
        _detail.value = DetailState.Loading(id, summary)
        detailJob?.cancel()
        detailJob = viewModelScope.launch { loadDetail(id, summary) }
    }

    /** Retries after a failed detail fetch. */
    fun retryDetail() {
        val state = _detail.value as? DetailState.Failed ?: return
        _detail.value = DetailState.Loading(state.id, state.summary)
        detailJob?.cancel()
        detailJob = viewModelScope.launch { loadDetail(state.id, state.summary) }
    }

    private suspend fun loadDetail(id: String, summary: AccessRequest?, fromNotification: Boolean = false) {
        when (val result = repository.detail(id)) {
            is Outcome.Failure -> {
                if (result.failure == RequestFailure.NotFound && fromNotification) {
                    // A stale notification: say so and go back to the (refreshed) list instead of an error screen.
                    _detail.value = DetailState.Closed
                    _message.value = UiMessage.RequestUnavailable
                    refresh()
                    return
                }
                _detail.value = DetailState.Failed(id, summary, result.failure)
                if (result.failure == RequestFailure.NotFound) refresh() // it is gone: bring the list up to date
            }
            is Outcome.Success -> {
                val request = result.value
                val hasShot = request.hasScreenshot == true
                _detail.value = DetailState.Ready(request, if (hasShot) ScreenshotState.Loading else ScreenshotState.None)
                if (hasShot) loadScreenshot(id)
            }
        }
    }

    /** Retries after a failed screenshot fetch. */
    fun retryScreenshot() {
        val ready = _detail.value as? DetailState.Ready ?: return
        if (ready.screenshot !is ScreenshotState.Unavailable) return
        updateReady(ready.request.id) { it.copy(screenshot = ScreenshotState.Loading) }
        detailJob = viewModelScope.launch { loadScreenshot(ready.request.id) }
    }

    private suspend fun loadScreenshot(id: String) {
        val state = when (val result = repository.screenshot(id)) {
            is Outcome.Success ->
                if (imageValidator.isDecodable(result.value.bytes)) ScreenshotState.Loaded(result.value.bytes)
                else ScreenshotState.Unavailable(ScreenshotProblem.Invalid)
            is Outcome.Failure -> ScreenshotState.Unavailable(
                when (result.failure) {
                    RequestFailure.Network -> ScreenshotProblem.Network
                    RequestFailure.NotFound -> ScreenshotProblem.Missing
                    RequestFailure.InvalidResponse -> ScreenshotProblem.Invalid
                    else -> ScreenshotProblem.Server
                },
            )
        }
        updateReady(id) { it.copy(screenshot = state) }
    }

    /** Leaves the detail screen and drops its in-memory screenshot. Ignored while a decision is in flight. */
    fun close() {
        if ((_detail.value as? DetailState.Ready)?.decision is DecisionState.Submitting) return
        detailJob?.cancel()
        _detail.value = DetailState.Closed
    }

    // ---- decisions ---------------------------------------------------------------------------------

    /** Sends the decision to the backend. A second call while one is in flight is ignored. */
    fun decide(decision: Decision) {
        val ready = _detail.value as? DetailState.Ready ?: return
        if (ready.decision is DecisionState.Submitting) return
        val id = ready.request.id
        _detail.value = ready.copy(decision = DecisionState.Submitting(decision))
        decisionJob = viewModelScope.launch {
            when (val result = repository.decide(id, decision)) {
                is Outcome.Success -> {
                    finishDecided(id)
                    _message.value = if (decision == Decision.Approve) UiMessage.Approved else UiMessage.Rejected
                }
                is Outcome.Failure -> when (result.failure) {
                    // Someone else already decided it, or it expired: do not pretend this decision succeeded.
                    RequestFailure.Conflict, RequestFailure.NotFound -> {
                        finishDecided(id)
                        _message.value = UiMessage.AlreadyHandled
                    }
                    else -> updateReady(id) { it.copy(decision = DecisionState.Failed(decision, result.failure)) }
                }
            }
        }
    }

    fun dismissDecisionError() {
        val ready = _detail.value as? DetailState.Ready ?: return
        if (ready.decision is DecisionState.Failed) updateReady(ready.request.id) { it.copy(decision = DecisionState.Idle) }
    }

    fun messageShown() {
        _message.value = null
    }

    /** The request is decided or gone: drop it locally, leave the detail screen, and re-sync with the server. */
    private fun finishDecided(id: String) {
        detailJob?.cancel()
        _detail.value = DetailState.Closed
        _list.update { it.copy(requests = it.requests.filterNot { r -> r.id == id }) }
        refreshJob?.cancel() // a refresh already in flight may predate the decision
        startRefresh()
    }

    private fun updateReady(id: String, change: (DetailState.Ready) -> DetailState.Ready) {
        _detail.update { state -> if (state is DetailState.Ready && state.request.id == id) change(state) else state }
    }

    private fun reset() {
        refreshJob?.cancel()
        detailJob?.cancel()
        decisionJob?.cancel()
        _list.value = ListState()
        _detail.value = DetailState.Closed
        _message.value = null
        seenNotifications.clear()
    }

    private companion object {
        const val MAX_SEEN_NOTIFICATIONS = 50
    }
}
