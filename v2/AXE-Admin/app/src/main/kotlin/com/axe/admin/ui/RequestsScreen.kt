package com.axe.admin.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.axe.admin.model.AccessRequest
import com.axe.admin.model.RequestKind
import com.axe.admin.ui.theme.AxeColors
import com.axe.admin.viewmodel.ListState

/** "AXE" wordmark with the letter-spaced ADMIN caption, as in the reference design. */
@Composable
fun AxeWordmark() {
    Column(horizontalAlignment = Alignment.CenterHorizontally) {
        Text(
            "AXE",
            color = AxeColors.Logo,
            fontSize = 38.sp,
            fontWeight = FontWeight.Black,
            fontStyle = FontStyle.Italic,
            letterSpacing = (-1).sp,
        )
        Text("A D M I N", color = MaterialTheme.colorScheme.onSurface, fontSize = 12.sp, fontWeight = FontWeight.Medium)
    }
}

/** Header with the AXE Admin wordmark and the authenticated admin chip (with sign out). */
@Composable
fun AdminHeader(email: String, onLogout: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 14.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        AxeWordmark()
        Spacer(Modifier.width(12.dp))
        Row(
            Modifier.weight(1f)
                .clip(RoundedCornerShape(16.dp))
                .background(MaterialTheme.colorScheme.surface)
                .border(BorderStroke(1.dp, AxeColors.CardBorder), RoundedCornerShape(16.dp))
                .padding(start = 12.dp, top = 10.dp, bottom = 10.dp, end = 4.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(Modifier.size(40.dp).background(MaterialTheme.colorScheme.surfaceVariant, CircleShape), contentAlignment = Alignment.Center) {
                PersonGlyph(MaterialTheme.colorScheme.onSurface, 22.dp)
            }
            Spacer(Modifier.width(10.dp))
            Column(Modifier.weight(1f)) {
                Text(email, style = MaterialTheme.typography.bodyMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text("Administrator", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            TextButton(onClick = onLogout, modifier = Modifier.semantics { contentDescription = "Sign out" }) {
                LogoutGlyph(MaterialTheme.colorScheme.onSurface)
            }
        }
    }
}

/** The pending-request list: loading, empty, error, and the cards. Data comes only from the backend. */
@Composable
fun RequestsScreen(
    state: ListState,
    onRefresh: () -> Unit,
    onOpen: (String) -> Unit,
    modifier: Modifier = Modifier,
) {
    Column(modifier.fillMaxSize()) {
        Row(
            Modifier.fillMaxWidth().padding(start = 20.dp, end = 20.dp, top = 8.dp, bottom = 14.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Column(Modifier.weight(1f)) {
                Text("Requests", fontSize = 30.sp, fontWeight = FontWeight.Bold)
                Text(
                    "Pending payment and invitation requests",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Surface(
                onClick = onRefresh,
                enabled = !state.refreshing,
                shape = RoundedCornerShape(14.dp),
                color = MaterialTheme.colorScheme.primaryContainer,
                border = BorderStroke(1.dp, AxeColors.CardBorder),
                modifier = Modifier.size(52.dp).semantics { contentDescription = "Refresh" },
            ) {
                Box(contentAlignment = Alignment.Center) {
                    if (state.refreshing) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp)
                    else RefreshGlyph(MaterialTheme.colorScheme.primary)
                }
            }
        }

        Box(Modifier.weight(1f).fillMaxWidth()) {
            when {
                !state.loaded && state.refreshing -> CenteredMessage { CircularProgressIndicator() }
                !state.loaded && state.error != null -> CenteredMessage {
                    Text(failureMessage(state.error), color = MaterialTheme.colorScheme.error)
                    TextButton(onClick = onRefresh) { Text("Try again") }
                }
                !state.loaded -> Unit // first load is about to start
                state.requests.isEmpty() -> CenteredMessage {
                    Text("No pending requests", style = MaterialTheme.typography.titleMedium)
                    Text(
                        "New payment and invitation requests will appear here.",
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                else -> LazyColumn(
                    Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(horizontal = 16.dp, vertical = 4.dp),
                    verticalArrangement = Arrangement.spacedBy(12.dp),
                ) {
                    if (state.error != null) {
                        item {
                            Text(
                                failureMessage(state.error) + " Showing the last loaded list.",
                                color = MaterialTheme.colorScheme.error,
                                style = MaterialTheme.typography.bodySmall,
                            )
                        }
                    }
                    items(state.requests, key = { it.id }) { RequestCard(it, onClick = { onOpen(it.id) }) }
                }
            }
        }

        Button(
            onClick = onRefresh,
            enabled = !state.refreshing,
            shape = RoundedCornerShape(16.dp),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp).height(56.dp),
        ) {
            RefreshGlyph(MaterialTheme.colorScheme.onPrimary, 20.dp)
            Spacer(Modifier.width(10.dp))
            Text("Refresh Requests", fontSize = 17.sp, fontWeight = FontWeight.Medium)
        }
    }
}

@Composable
private fun CenteredMessage(content: @Composable () -> Unit) {
    Box(Modifier.fillMaxSize().padding(24.dp), contentAlignment = Alignment.Center) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp)) {
            content()
        }
    }
}

@Composable
private fun RequestCard(request: AccessRequest, onClick: () -> Unit) {
    val accent = kindColor(request.kind)
    Surface(
        onClick = onClick,
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, AxeColors.CardBorder),
        modifier = Modifier.fillMaxWidth(),
    ) {
        Row(Modifier.padding(horizontal = 16.dp, vertical = 18.dp), verticalAlignment = Alignment.CenterVertically) {
            KindIcon(request.kind)
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(3.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        kindHeading(request.kind).uppercase(),
                        color = accent,
                        fontSize = 13.sp,
                        fontWeight = FontWeight.Medium,
                        modifier = Modifier.weight(1f),
                        maxLines = 1,
                    )
                    PendingPill()
                }
                Text(
                    request.name ?: "Unnamed requester",
                    fontSize = 19.sp,
                    fontWeight = FontWeight.SemiBold,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    if (request.kind == RequestKind.Payment) "${request.planLabel} Plan  •  ${formatRupees(request.expectedAmount)}"
                    else "${request.planLabel} Plan",
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                    fontSize = 16.sp,
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    ClockGlyph(MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.width(8.dp))
                    Text(formatAge(request.createdAt), color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 14.sp)
                }
                if (request.kind == RequestKind.Payment && request.amountMismatch) Warning("Amount differs from the plan price")
                if (request.duplicateUtr) Warning("Reference number already used")
            }
            Spacer(Modifier.width(6.dp))
            ChevronGlyph(MaterialTheme.colorScheme.onSurface)
        }
    }
}

/**
 * Every request the backend lists is pending (the endpoint returns nothing else), so the pill is a statement of
 * that endpoint's meaning, not a per-request field.
 */
@Composable
fun PendingPill() {
    Text(
        "Pending",
        color = AxeColors.PendingText,
        fontSize = 14.sp,
        modifier = Modifier
            .clip(RoundedCornerShape(50))
            .background(AxeColors.PendingFill)
            .border(BorderStroke(1.dp, AxeColors.PendingBorder), RoundedCornerShape(50))
            .padding(horizontal = 14.dp, vertical = 6.dp),
    )
}

@Composable
fun kindColor(kind: RequestKind) = when (kind) {
    RequestKind.Payment -> AxeColors.Payment
    RequestKind.Invite -> AxeColors.Invitation
    RequestKind.Unknown -> MaterialTheme.colorScheme.onSurfaceVariant
}

@Composable
fun Warning(text: String) {
    Text(text, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.error)
}
