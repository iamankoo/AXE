package com.axe.admin.ui

import com.axe.admin.model.RequestKind
import com.axe.admin.repository.RequestFailure
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import java.util.Locale

// Formatters are built per call (cheap) so a change of time zone or language while the app is running is respected.
fun formatTime(instant: Instant?): String = instant?.let {
    DateTimeFormatter.ofLocalizedDateTime(FormatStyle.MEDIUM, FormatStyle.SHORT).withZone(ZoneId.systemDefault()).format(it)
} ?: "Unknown"

/** The Windows client prices plans in rupees (₹); amounts come from the server as numbers. */
fun formatRupees(amount: Double): String =
    if (amount % 1.0 == 0.0) "₹${amount.toLong()}" else "₹" + String.format(Locale.US, "%.2f", amount)

/** "2 minutes ago" style age for the list. Display only; uses the device clock. */
fun formatAge(instant: Instant?, now: Instant = Instant.now()): String {
    if (instant == null) return "Time unknown"
    val minutes = java.time.Duration.between(instant, now).toMinutes()
    return when {
        minutes < 1 -> "Just now"
        minutes < 60 -> "$minutes ${if (minutes == 1L) "minute" else "minutes"} ago"
        minutes < 24 * 60 -> (minutes / 60).let { "$it ${if (it == 1L) "hour" else "hours"} ago" }
        else -> (minutes / (24 * 60)).let { "$it ${if (it == 1L) "day" else "days"} ago" }
    }
}

/** e.g. "5 Feb 2025, 10:32 AM" */
fun formatDetailTime(instant: Instant?): String = instant?.let {
    DateTimeFormatter.ofPattern("d MMM yyyy, h:mm a", Locale.getDefault()).withZone(ZoneId.systemDefault()).format(it)
} ?: "Unknown"

fun kindLabel(kind: RequestKind): String = when (kind) {
    RequestKind.Payment -> "Payment"
    RequestKind.Invite -> "Invitation"
    RequestKind.Unknown -> "Request"
}

fun kindHeading(kind: RequestKind): String = when (kind) {
    RequestKind.Payment -> "Payment Request"
    RequestKind.Invite -> "Invitation Request"
    RequestKind.Unknown -> "Request"
}

/** The short form of the server's request id (first 8 characters), enough to tell requests apart. */
fun shortId(id: String): String = id.take(8).uppercase()

fun failureMessage(failure: RequestFailure): String = when (failure) {
    RequestFailure.Network -> "Can't reach the AXE server. Check your connection and try again."
    RequestFailure.SessionEnded -> "Your session ended. Please sign in again."
    RequestFailure.NotFound -> "This request is no longer pending."
    RequestFailure.Conflict -> "This request was already handled or has expired."
    RequestFailure.Server -> "The AXE server had a problem. Please try again shortly."
    RequestFailure.InvalidResponse -> "The AXE server sent an unexpected response."
}
