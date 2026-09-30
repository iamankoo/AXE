package com.axe.admin.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Modifier
import com.axe.admin.viewmodel.ShellViewModel

/** The authenticated Admin shell. Later phases add navigation and content around [RequestsScreen]. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun AdminShell(email: String, viewModel: ShellViewModel, onLogout: () -> Unit) {
    val status by viewModel.status.collectAsState()
    LaunchedEffect(Unit) { viewModel.recheck() }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("AXE Admin")
                        Text("Signed in as $email", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                },
                actions = { TextButton(onClick = onLogout) { Text("Sign out") } },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = MaterialTheme.colorScheme.background),
            )
        },
        containerColor = MaterialTheme.colorScheme.background,
    ) { padding ->
        RequestsScreen(status, onRetry = viewModel::recheck, modifier = Modifier.padding(padding))
    }
}
