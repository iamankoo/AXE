package com.axe.admin.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.axe.admin.repository.BackendStatus

/**
 * Phase 1 foundation only: it shows the backend connection state and states plainly that request
 * management is not implemented yet. No request data, real or fake, is displayed here.
 */
@Composable
fun RequestsScreen(status: BackendStatus?, onRetry: () -> Unit, modifier: Modifier = Modifier) {
    Column(modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Requests", style = MaterialTheme.typography.headlineSmall)
        Row(verticalAlignment = Alignment.CenterVertically) {
            when (status) {
                null -> {
                    CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.width(8.dp))
                    Text("Checking the AXE server...", color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                BackendStatus.Connected ->
                    Text("Connected to the AXE server.", color = MaterialTheme.colorScheme.primary)
                BackendStatus.Offline -> {
                    Text("Offline. Can't reach the AXE server.", color = MaterialTheme.colorScheme.error)
                    TextButton(onClick = onRetry) { Text("Retry") }
                }
                BackendStatus.Error -> {
                    Text("The AXE server returned an error.", color = MaterialTheme.colorScheme.error)
                    TextButton(onClick = onRetry) { Text("Retry") }
                }
                BackendStatus.SignedOut -> Unit
            }
        }
        Text(
            "Request management (pending payment and invitation requests) will be added in Phase 2.",
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.fillMaxWidth(),
        )
    }
}
