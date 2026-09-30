package com.axe.admin

import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.CreationExtras
import com.axe.admin.data.AppContainer
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
        setContent {
            AxeAdminTheme {
                AppRoot(container.config.isValid, factory)
            }
        }
    }

    class Factory(private val container: AppContainer) : ViewModelProvider.Factory {
        @Suppress("UNCHECKED_CAST")
        override fun <T : ViewModel> create(modelClass: Class<T>, extras: CreationExtras): T = when (modelClass) {
            AuthViewModel::class.java -> AuthViewModel(container.authRepository) as T
            RequestsViewModel::class.java -> RequestsViewModel(
                container.requestRepository,
                container.authRepository.state.map { it is AuthState.SignedIn },
                container.imageValidator,
            ) as T
            else -> throw IllegalArgumentException("Unknown ViewModel ${modelClass.name}")
        }
    }
}
