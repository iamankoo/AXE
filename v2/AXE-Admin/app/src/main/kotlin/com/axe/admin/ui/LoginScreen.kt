package com.axe.admin.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusDirection
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import com.axe.admin.model.SignOutReason
import com.axe.admin.repository.LoginFailure
import com.axe.admin.viewmodel.AuthViewModel
import com.axe.admin.viewmodel.LoginError

@Composable
fun LoginScreen(viewModel: AuthViewModel, signOutReason: SignOutReason?) {
    val ui by viewModel.login.collectAsState()
    var email by rememberSaveable { mutableStateOf("") }
    // Deliberately not rememberSaveable: the password must never be written to saved instance state.
    var password by remember { mutableStateOf("") }
    var showPassword by remember { mutableStateOf(false) }
    val focus = LocalFocusManager.current
    val submit = { viewModel.login(email, password) }

    Column(
        Modifier.fillMaxSize().safeDrawingPadding().imePadding().verticalScroll(rememberScrollState()).padding(24.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Text("AXE Admin", style = MaterialTheme.typography.headlineLarge, color = MaterialTheme.colorScheme.primary)
        Text(
            "Sign in to review AXE access requests.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(top = 4.dp, bottom = 24.dp),
        )

        val notice = signOutReason?.let {
            when (it) {
                SignOutReason.SessionExpired -> "Your session expired. Please sign in again."
                SignOutReason.AccessRevoked -> "This account is no longer allowed to use AXE Admin."
            }
        }
        if (notice != null && ui.failure == null && ui.inputError == null) {
            Text(notice, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(bottom = 12.dp))
        }

        OutlinedTextField(
            value = email,
            onValueChange = { email = it; viewModel.dismissError() },
            label = { Text("Email") },
            singleLine = true,
            enabled = !ui.inProgress,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Email, imeAction = ImeAction.Next),
            keyboardActions = KeyboardActions(onNext = { focus.moveFocus(FocusDirection.Down) }),
            modifier = Modifier.fillMaxWidth(),
        )
        Spacer(Modifier.height(12.dp))
        OutlinedTextField(
            value = password,
            onValueChange = { password = it; viewModel.dismissError() },
            label = { Text("Password") },
            singleLine = true,
            enabled = !ui.inProgress,
            visualTransformation = if (showPassword) VisualTransformation.None else PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password, imeAction = ImeAction.Done),
            keyboardActions = KeyboardActions(onDone = { focus.clearFocus(); submit() }),
            trailingIcon = {
                TextButton(onClick = { showPassword = !showPassword }) { Text(if (showPassword) "Hide" else "Show") }
            },
            modifier = Modifier.fillMaxWidth(),
        )

        val error = when {
            ui.inputError == LoginError.MissingFields -> "Enter your email and password."
            else -> ui.failure?.let(::loginFailureMessage)
        }
        if (error != null) {
            Text(error, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(top = 12.dp))
        }

        Spacer(Modifier.height(20.dp))
        Button(onClick = { focus.clearFocus(); submit() }, enabled = !ui.inProgress, modifier = Modifier.fillMaxWidth().height(48.dp)) {
            if (ui.inProgress) {
                CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = MaterialTheme.colorScheme.onPrimary)
                Text("  Signing in...", Modifier.align(Alignment.CenterVertically))
            } else {
                Text("Sign in")
            }
        }
    }
}

private fun loginFailureMessage(failure: LoginFailure): String = when (failure) {
    LoginFailure.InvalidCredentials -> "Incorrect email or password."
    LoginFailure.NotAnAdmin -> "This account is not an AXE admin."
    LoginFailure.RateLimited -> "Too many attempts. Wait a moment and try again."
    LoginFailure.Network -> "Can't reach the AXE server. Check your connection and try again."
    LoginFailure.Server -> "The AXE server had a problem. Please try again shortly."
}
