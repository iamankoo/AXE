package com.axe.admin.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val AxeDark = darkColorScheme(
    primary = Color(0xFF5CC8FF),
    onPrimary = Color(0xFF00202E),
    primaryContainer = Color(0xFF0B3A52),
    onPrimaryContainer = Color(0xFFC6EAFF),
    background = Color(0xFF0E1116),
    onBackground = Color(0xFFE6E8EB),
    surface = Color(0xFF151A21),
    onSurface = Color(0xFFE6E8EB),
    surfaceVariant = Color(0xFF1E252E),
    onSurfaceVariant = Color(0xFFA9B1BB),
    outline = Color(0xFF3A444F),
    error = Color(0xFFFF8A80),
    onError = Color(0xFF3B0A06),
    errorContainer = Color(0xFF4A1C18),
    onErrorContainer = Color(0xFFFFDAD6),
)

/** AXE Admin is dark-only, matching the AXE brand. */
@Composable
fun AxeAdminTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = AxeDark, content = content)
}
