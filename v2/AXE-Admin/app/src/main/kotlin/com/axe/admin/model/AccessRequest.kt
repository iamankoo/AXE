package com.axe.admin.model

import java.time.Instant
import java.time.OffsetDateTime
import java.time.format.DateTimeParseException

/** `kind` as the backend defines it (`access_requests.kind`: 'payment' | 'invite'). */
enum class RequestKind {
    Payment,
    Invite,

    /** A value this app does not know; shown generically and still reviewable. */
    Unknown,
}

/** What the admin decides. The server turns an approval into a signed grant; Android never does. */
enum class Decision(val wire: String) {
    Approve("approve"),
    Reject("reject"),
}

/**
 * A pending access request, exactly the fields `GET /admin/requests[/:id]` returns. The backend only lists
 * requests that are still pending and unexpired, so there is no status field; "not pending" shows up as a
 * 404 on the detail endpoint or a 409 on a decision.
 */
data class AccessRequest(
    val id: String,
    val kind: RequestKind,
    val name: String?,
    val plan: String,
    val planLabel: String,
    /** Price the server expects for [plan] (rupees). */
    val expectedAmount: Double,
    /** What the requester says they paid; null for invitation requests. */
    val amountPaid: Double?,
    /** Computed by the server: a payment whose amount differs from [expectedAmount]. */
    val amountMismatch: Boolean,
    val utr: String?,
    val inviteCode: String?,
    /** Server flag: this UTR was already used or is pending on another request. */
    val duplicateUtr: Boolean,
    val createdAt: Instant?,
    /** Review deadline; the request disappears after this. */
    val expiresAt: Instant?,
    /** Only known from the detail endpoint; null in list results. */
    val hasScreenshot: Boolean?,
)

internal fun parseInstant(value: String?): Instant? =
    if (value == null) null else try {
        OffsetDateTime.parse(value).toInstant()
    } catch (_: DateTimeParseException) {
        null
    }
