package com.axe.admin.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import com.axe.admin.viewmodel.DecisionState
import com.axe.admin.viewmodel.DetailState
import com.axe.admin.viewmodel.RequestsViewModel
import com.axe.admin.viewmodel.UiMessage

/** The authenticated Admin shell: header + Requests list, or the detail screen when a request is open. */
@Composable
fun AdminShell(email: String, viewModel: RequestsViewModel, onLogout: () -> Unit) {
    val list by viewModel.list.collectAsState()
    val detail by viewModel.detail.collectAsState()
    val message by viewModel.message.collectAsState()
    val snackbar = remember { SnackbarHostState() }

    // One load on entry; the ViewModel ignores repeats while a load is in flight.
    LaunchedEffect(Unit) { viewModel.loadIfNeeded() }
    LaunchedEffect(message) {
        message?.let {
            snackbar.showSnackbar(
                when (it) {
                    UiMessage.Approved -> "Request approved."
                    UiMessage.Rejected -> "Request rejected."
                    UiMessage.AlreadyHandled -> "That request was already handled or has expired. The list was refreshed."
                },
            )
            viewModel.messageShown()
        }
    }

    val inDetail = detail !is DetailState.Closed
    val deciding = (detail as? DetailState.Ready)?.decision is DecisionState.Submitting
    BackHandler(enabled = inDetail) { if (!deciding) viewModel.close() }

    Scaffold(
        snackbarHost = { SnackbarHost(snackbar) },
        containerColor = MaterialTheme.colorScheme.background,
        modifier = Modifier.fillMaxSize(),
    ) { padding ->
        Column(Modifier.fillMaxSize().padding(padding)) {
            if (inDetail) {
                DetailTopBar(onBack = viewModel::close, backEnabled = !deciding)
                RequestDetailScreen(
                    state = detail,
                    onRetry = viewModel::retryDetail,
                    onBack = viewModel::close,
                    onDecide = viewModel::decide,
                    onDismissDecisionError = viewModel::dismissDecisionError,
                    onRetryScreenshot = viewModel::retryScreenshot,
                )
            } else {
                AdminHeader(email, onLogout)
                RequestsScreen(list, onRefresh = viewModel::refresh, onOpen = viewModel::open)
            }
        }
    }
}
