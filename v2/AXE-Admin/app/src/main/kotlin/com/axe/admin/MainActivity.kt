package com.axe.admin

import android.content.Intent
import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.CreationExtras
import com.axe.admin.data.AppContainer
import com.axe.admin.push.PushPayload
import com.axe.admin.ui.NotificationUi
import com.axe.admin.ui.AppRoot
import com.axe.admin.ui.theme.AxeAdminTheme
import com.axe.admin.viewmodel.AuthViewModel
import com.axe.admin.model.AuthState
import com.axe.admin.viewmodel.RequestsViewModel
import kotlinx.coroutines.flow.map

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Credentials and admin data must not appear in screenshots or the recents thumbnail. Applied to release
        // builds only, so debug builds can be inspected with adb/emulator screenshots during development.
        if (!BuildConfig.DEBUG) {
            window.setFlags(WindowManager.LayoutParams.FLAG_SECURE, WindowManager.LayoutParams.FLAG_SECURE)
        }
        enableEdgeToEdge()
        val container = (application as AxeAdminApp).container
        val factory = Factory(container)
        // A notification tap launches the app with the push data as intent extras. Only on a fresh start: a recreated
        // activity (rotation) must not replay an old tap.
        if (savedInstanceState == null) handleNotificationIntent(intent, container)
        val notifications = NotificationUi(container.deepLinks, container.notificationPermission, container.pushConfig.isConfigured)
        setContent {
            AxeAdminTheme {
                AppRoot(container.config.isValid, factory, notifications)
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handleNotificationIntent(intent, (application as AxeAdminApp).container)
    }

    /**
     * Extras are untrusted (any app can send this activity an intent). Only a known type and a well-formed id are
     * accepted, and the id merely queues "open this request once signed in": the request itself is always fetched from
     * the backend with the admin's session.
     */
    private fun handleNotificationIntent(intent: Intent?, container: AppContainer) {
        val target = PushPayload.parse(
            intent?.getStringExtra(PushPayload.KEY_TYPE),
            intent?.getStringExtra(PushPayload.KEY_REQUEST_ID),
        ) ?: return
        container.deepLinks.offer(target.requestId)
        intent?.removeExtra(PushPayload.KEY_TYPE)
        intent?.removeExtra(PushPayload.KEY_REQUEST_ID)
    }

    class Factory(private val container: AppContainer) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T = when (modelClass) {
            AuthViewModel::class.java -> AuthViewModel(container.authRepository) as T
            RequestsViewModel::class.java -> RequestsViewModel(
                container.requestRepository,
                container.authRepository.state.map { it is AuthState.SignedIn },
                container.pushEvents.newRequests,
                container.imageValidator,
            ) as T
            else -> throw IllegalArgumentException("Unknown ViewModel ${modelClass.name}")
        }
    }
}
