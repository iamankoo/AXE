package com.axe.admin.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

/** Extra brand colors from the approved AXE Admin reference design. */
object AxeColors {
    val Payment = Color(0xFFFF9F1C)
    val Invitation = Color(0xFF8B5CF6)
    val PendingText = Color(0xFFFFB627)
    val PendingFill = Color(0xFF30260F)
    val PendingBorder = Color(0xFF7A5A16)
    val Reject = Color(0xFFFF5F5F)
    val Logo = Color(0xFF1F6BFF)
    val CardBorder = Color(0xFF1B2A3B)
}

private val AxeDark = darkColorScheme(
    primary = Color(0xFF0B6BFF),
    onPrimary = Color.White,
    primaryContainer = Color(0xFF0E2747),
    onPrimaryContainer = Color(0xFFCFE0FF),
    background = Color(0xFF0A1118),
    onBackground = Color(0xFFF2F5F9),
    surface = Color(0xFF0F1823),
    onSurface = Color(0xFFF2F5F9),
    surfaceVariant = Color(0xFF14202E),
    onSurfaceVariant = Color(0xFF8FA3BC),
    outline = AxeColors.CardBorder,
    error = AxeColors.Reject,
    onError = Color(0xFF2B0606),
    errorContainer = Color(0xFF3B1414),
    onErrorContainer = Color(0xFFFFDAD6),
    tertiary = AxeColors.Invitation,
)

/** AXE Admin is dark-only, matching the AXE brand. */
@Composable
fun AxeAdminTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = AxeDark, content = content)
}
