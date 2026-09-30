package com.axe.admin.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.viewmodel.compose.viewModel
import com.axe.admin.model.AuthState
import com.axe.admin.viewmodel.AuthViewModel
import com.axe.admin.viewmodel.RequestsViewModel

/** Chooses the screen from the auth state. The server stays authoritative; this only reflects it. */
@Composable
fun AppRoot(configValid: Boolean, factory: ViewModelProvider.Factory) {
    val background = Modifier.fillMaxSize()
    if (!configValid) {
        Box(background.padding(24.dp), contentAlignment = Alignment.Center) {
            Text(
                "AXE Admin is not configured. Set axe.supabaseUrl and axe.anonKey (see admin.config.example.properties) and rebuild.",
                color = MaterialTheme.colorScheme.error,
            )
        }
        return
    }

    val auth: AuthViewModel = viewModel(factory = factory)
    val state by auth.authState.collectAsState()
    when (val s = state) {
        AuthState.Restoring -> Box(background, contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        is AuthState.SignedOut -> LoginScreen(auth, s.reason)
        is AuthState.SignedIn -> {
            val requests: RequestsViewModel = viewModel(factory = factory)
            AdminShell(s.email, requests, onLogout = auth::logout)
        }
    }
}
