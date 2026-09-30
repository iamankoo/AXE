package com.axe.admin.ui

import android.graphics.BitmapFactory
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.axe.admin.model.AccessRequest
import com.axe.admin.model.Decision
import com.axe.admin.model.RequestKind
import com.axe.admin.repository.RequestFailure
import com.axe.admin.ui.theme.AxeColors
import com.axe.admin.viewmodel.DecisionState
import com.axe.admin.viewmodel.DetailState
import com.axe.admin.viewmodel.ScreenshotProblem
import com.axe.admin.viewmodel.ScreenshotState
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** Top bar of the detail page: back arrow and "Request Details". */
@Composable
fun DetailTopBar(onBack: () -> Unit, backEnabled: Boolean) {
    Row(Modifier.fillMaxWidth().padding(start = 8.dp, end = 20.dp, top = 12.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        TextButton(onClick = onBack, enabled = backEnabled) {
            BackGlyph(if (backEnabled) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onSurfaceVariant)
        }
        Spacer(Modifier.width(4.dp))
        Text("Request Details", fontSize = 24.sp, fontWeight = FontWeight.SemiBold)
    }
}

/** Request detail: what the backend returned, the payment screenshot, and Approve / Reject. */
@Composable
fun RequestDetailScreen(
    state: DetailState,
    onRetry: () -> Unit,
    onBack: () -> Unit,
    onDecide: (Decision) -> Unit,
    onDismissDecisionError: () -> Unit,
    onRetryScreenshot: () -> Unit,
    modifier: Modifier = Modifier,
) {
    when (state) {
        DetailState.Closed -> Unit
        is DetailState.Loading -> Box(modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        is DetailState.Failed -> Column(
            modifier.fillMaxSize().padding(24.dp),
            verticalArrangement = Arrangement.Center,
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Text(failureMessage(state.failure), color = MaterialTheme.colorScheme.error)
            Row {
                if (state.failure != RequestFailure.NotFound) TextButton(onClick = onRetry) { Text("Try again") }
                TextButton(onClick = onBack) { Text("Back to requests") }
            }
        }
        is DetailState.Ready -> ReadyContent(state, onDecide, onDismissDecisionError, onRetryScreenshot, modifier)
    }
}

@Composable
private fun ReadyContent(
    state: DetailState.Ready,
    onDecide: (Decision) -> Unit,
    onDismissDecisionError: () -> Unit,
    onRetryScreenshot: () -> Unit,
    modifier: Modifier,
) {
    val request = state.request
    var confirming by remember { mutableStateOf<Decision?>(null) }
    val submitting = state.decision as? DecisionState.Submitting

    Column(modifier.fillMaxSize()) {
        Column(
            Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
        ) {
            HeaderCard(request)
            RequesterCard(request)
            if (request.kind == RequestKind.Payment) {
                DetailCard(title = "Payment Screenshot", icon = { ImageGlyph(MaterialTheme.colorScheme.onSurface) }) {
                    ScreenshotSection(state.screenshot, onRetryScreenshot)
                }
            }
        }

        Column(Modifier.padding(horizontal = 16.dp, vertical = 12.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            (state.decision as? DecisionState.Failed)?.let { failed ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "Couldn't ${if (failed.decision == Decision.Approve) "approve" else "reject"}: ${failureMessage(failed.failure)}",
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall,
                        modifier = Modifier.weight(1f),
                    )
                    TextButton(onClick = onDismissDecisionError) { Text("Dismiss") }
                }
            }
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Button(
                    onClick = { confirming = Decision.Reject },
                    enabled = submitting == null,
                    shape = RoundedCornerShape(16.dp),
                    colors = ButtonDefaults.buttonColors(containerColor = AxeColors.Reject, contentColor = Color(0xFF2B0606)),
                    modifier = Modifier.weight(1f).height(56.dp),
                ) { ActionLabel("Reject", submitting?.decision == Decision.Reject, Color(0xFF2B0606)) { TrashGlyph(it) } }
                Button(
                    onClick = { confirming = Decision.Approve },
                    enabled = submitting == null,
                    shape = RoundedCornerShape(16.dp),
                    modifier = Modifier.weight(1f).height(56.dp),
                ) { ActionLabel("Approve", submitting?.decision == Decision.Approve, Color.White) { CheckGlyph(it) } }
            }
        }
    }

    confirming?.let { decision ->
        ConfirmDialog(
            decision = decision,
            request = request,
            onConfirm = { confirming = null; onDecide(decision) },
            onCancel = { confirming = null },
        )
    }
}

@Composable
private fun HeaderCard(request: AccessRequest) {
    DetailSurface {
        Row(Modifier.padding(16.dp), verticalAlignment = Alignment.CenterVertically) {
            KindIcon(request.kind, 56.dp)
            Spacer(Modifier.width(16.dp))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(kindHeading(request.kind), fontSize = 19.sp, fontWeight = FontWeight.SemiBold)
                Text("Request ID: ${shortId(request.id)}", color = MaterialTheme.colorScheme.onSurfaceVariant, fontSize = 14.sp)
            }
            PendingPill()
        }
    }
}

@Composable
private fun RequesterCard(request: AccessRequest) {
    DetailCard(title = "Requester Information", icon = { PersonGlyph(MaterialTheme.colorScheme.onSurface) }) {
        Column(verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Field("Name", request.name ?: "Not provided")
            TwoColumns(
                { Field("Plan", "${request.planLabel} Plan") },
                { Field("Requested On", formatDetailTime(request.createdAt)) },
            )
            when (request.kind) {
                RequestKind.Payment -> {
                    TwoColumns(
                        { Field("Expected Amount", formatRupees(request.expectedAmount)) },
                        { Field("Amount Paid", request.amountPaid?.let(::formatRupees) ?: "Not provided") },
                    )
                    if (request.amountMismatch) Warning("The amount paid differs from the plan price.")
                    Field("UTR / Reference", request.utr ?: "Not provided")
                    if (request.duplicateUtr) Warning("This reference number was already used or is pending on another request.")
                }
                RequestKind.Invite -> {
                    Field("Expected Amount", formatRupees(request.expectedAmount))
                    Field("Invitation Code", request.inviteCode ?: "Not provided")
                }
                RequestKind.Unknown -> Field("Expected Amount", formatRupees(request.expectedAmount))
            }
            Field("Review By", formatDetailTime(request.expiresAt))
        }
    }
}

@Composable
private fun TwoColumns(left: @Composable () -> Unit, right: @Composable () -> Unit) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Box(Modifier.weight(1f)) { left() }
        Box(Modifier.weight(1f)) { right() }
    }
}

@Composable
private fun DetailSurface(content: @Composable () -> Unit) {
    Surface(
        shape = RoundedCornerShape(20.dp),
        color = MaterialTheme.colorScheme.surface,
        border = BorderStroke(1.dp, AxeColors.CardBorder),
        modifier = Modifier.fillMaxWidth(),
    ) { content() }
}

@Composable
private fun DetailCard(title: String, icon: @Composable () -> Unit, content: @Composable () -> Unit) {
    DetailSurface {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                icon()
                Spacer(Modifier.width(12.dp))
                Text(title, fontSize = 18.sp, fontWeight = FontWeight.SemiBold)
            }
            content()
        }
    }
}

@Composable
private fun ActionLabel(text: String, busy: Boolean, tint: Color, glyph: @Composable (Color) -> Unit) {
    if (busy) {
        CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = tint)
    } else {
        glyph(tint)
        Spacer(Modifier.width(10.dp))
        Text(text, fontSize = 18.sp, fontWeight = FontWeight.Medium)
    }
}

@Composable
private fun ConfirmDialog(decision: Decision, request: AccessRequest, onConfirm: () -> Unit, onCancel: () -> Unit) {
    val approve = decision == Decision.Approve
    AlertDialog(
        onDismissRequest = onCancel,
        containerColor = MaterialTheme.colorScheme.surface,
        title = { Text(if (approve) "Approve this request?" else "Reject this request?") },
        text = {
            Text(
                if (approve) "${request.name ?: "The requester"} will get ${request.planLabel} of AXE access. The server starts the timer at approval."
                else "${request.name ?: "The requester"} will be told the request was rejected. This can't be undone.",
            )
        },
        confirmButton = {
            TextButton(onClick = onConfirm) {
                Text(if (approve) "Approve" else "Reject", color = if (approve) MaterialTheme.colorScheme.primary else AxeColors.Reject)
            }
        },
        dismissButton = { TextButton(onClick = onCancel) { Text("Cancel") } },
    )
}

@Composable
private fun Field(label: String, value: String) {
    Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
        Text(label, fontSize = 15.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
        Text(value, fontSize = 18.sp)
    }
}

@Composable
private fun ScreenshotSection(state: ScreenshotState, onRetry: () -> Unit) {
    when (state) {
        ScreenshotState.None -> Text("No screenshot attached.", color = MaterialTheme.colorScheme.onSurfaceVariant)
        ScreenshotState.Loading -> Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        is ScreenshotState.Unavailable -> Column {
            Text(
                when (state.problem) {
                    ScreenshotProblem.Network -> "Couldn't load the screenshot. Check your connection."
                    ScreenshotProblem.Missing -> "The screenshot is no longer available."
                    ScreenshotProblem.Invalid -> "The screenshot couldn't be displayed (invalid image)."
                    ScreenshotProblem.Server -> "The server couldn't provide the screenshot."
                },
                color = MaterialTheme.colorScheme.error,
            )
            if (state.problem != ScreenshotProblem.Missing) TextButton(onClick = onRetry) { Text("Try again") }
        }
        is ScreenshotState.Loaded -> {
            // Decoded off the main thread, downsampled, kept in memory only. Nothing is written to disk.
            val bitmap by produceState<ImageBitmap?>(null, state.bytes) {
                value = withContext(Dispatchers.Default) { decodeScaled(state.bytes) }
            }
            val shown = bitmap
            if (shown == null) {
                Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            } else {
                Image(
                    bitmap = shown,
                    contentDescription = "Payment screenshot submitted by the requester",
                    contentScale = ContentScale.FillWidth,
                    modifier = Modifier.fillMaxWidth().clip(RoundedCornerShape(14.dp)),
                )
            }
        }
    }
}

private const val MAX_DECODED_WIDTH = 1600

private fun decodeScaled(bytes: ByteArray): ImageBitmap? {
    val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
    BitmapFactory.decodeByteArray(bytes, 0, bytes.size, bounds)
    if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null
    var sample = 1
    while (bounds.outWidth / (sample * 2) >= MAX_DECODED_WIDTH) sample *= 2
    val options = BitmapFactory.Options().apply { inSampleSize = sample }
    return BitmapFactory.decodeByteArray(bytes, 0, bytes.size, options)?.asImageBitmap()
}
