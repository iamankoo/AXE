package com.axe.admin.ui

import android.Manifest
import android.content.Context
import android.content.Intent
import android.provider.Settings
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalContext
import androidx.core.app.NotificationManagerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import com.axe.admin.push.DeepLinks
import com.axe.admin.push.NotificationPermissionPolicy
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

/** What the shell needs for notifications: waiting taps, the permission policy, and whether push is configured at all. */
class NotificationUi(val deepLinks: DeepLinks, val permission: NotificationPermissionPolicy, val pushConfigured: Boolean)

private fun notificationsEnabled(context: Context) = NotificationManagerCompat.from(context).areNotificationsEnabled()

/** The authenticated Admin shell: header + Requests list, or the detail screen when a request is open. */
@Composable
fun AdminShell(email: String, viewModel: RequestsViewModel, notifications: NotificationUi, onLogout: () -> Unit) {
    val list by viewModel.list.collectAsState()
    val detail by viewModel.detail.collectAsState()
    val message by viewModel.message.collectAsState()
    val snackbar = remember { SnackbarHostState() }

    // One load on entry; the ViewModel ignores repeats while a load is in flight.
    LaunchedEffect(Unit) { viewModel.loadIfNeeded() }

    // A tapped notification waits here until an admin is signed in (this composable only exists then), then the REAL
    // request is fetched from the backend; nothing from the notification is shown.
    val linkWaiting by notifications.deepLinks.hasPending.collectAsState()
    LaunchedEffect(linkWaiting) {
        if (linkWaiting) notifications.deepLinks.take()?.let(viewModel::openFromNotification)
    }

    // Notification permission (Android 13+): asked once, explained first, and the app works fully without it.
    val context = LocalContext.current
    var enabled by remember { mutableStateOf(notificationsEnabled(context)) }
    var explain by remember { mutableStateOf(false) }
    val permissionLauncher = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) {
        enabled = notificationsEnabled(context)
    }
    LaunchedEffect(Unit) {
        if (notifications.pushConfigured && notifications.permission.shouldAskNow(enabled)) explain = true
    }
    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) { enabled = notificationsEnabled(context) }
    if (explain) {
        AlertDialog(
            onDismissRequest = { explain = false; notifications.permission.markAsked() },
            containerColor = MaterialTheme.colorScheme.surface,
            title = { Text("Get notified about new requests") },
            text = {
                Text(
                    "AXE Admin can tell you when a new payment or invitation request arrives. The notification only says " +
                        "that a request is waiting; you always review it inside the app.",
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    explain = false
                    notifications.permission.markAsked()
                    permissionLauncher.launch(Manifest.permission.POST_NOTIFICATIONS)
                }) { Text("Allow notifications") }
            },
            dismissButton = { TextButton(onClick = { explain = false; notifications.permission.markAsked() }) { Text("Not now") } },
        )
    }
    LaunchedEffect(message) {
        message?.let {
            snackbar.showSnackbar(
                when (it) {
                    UiMessage.Approved -> "Request approved."
                    UiMessage.Rejected -> "Request rejected."
                    UiMessage.AlreadyHandled -> "That request was already handled or has expired. The list was refreshed."
                    UiMessage.NewRequest -> "A new request arrived."
                    UiMessage.RequestUnavailable -> "That request is no longer available. The list was refreshed."
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
                RequestsScreen(
                    list,
                    onRefresh = viewModel::refresh,
                    onOpen = viewModel::open,
                    showNotificationNotice = notifications.pushConfigured && !enabled && !explain,
                    onOpenNotificationSettings = {
                        context.startActivity(
                            Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                                .putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
                                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
                        )
                    },
                )
            }
        }
    }
}
