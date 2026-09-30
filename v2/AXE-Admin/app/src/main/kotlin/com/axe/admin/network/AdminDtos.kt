package com.axe.admin.network

import com.axe.admin.model.AccessRequest
import com.axe.admin.model.RequestKind
import com.axe.admin.model.parseInstant
import kotlinx.serialization.Serializable

/** Wire format of `present()` in `functions/admin/index.ts`. Unknown extra fields are ignored. */
@Serializable
data class AccessRequestDto(
    val id: String,
    val kind: String,
    val name: String? = null,
    val plan: String = "",
    val planLabel: String? = null,
    val expectedAmount: Double = 0.0,
    val amountPaid: Double? = null,
    val amountMismatch: Boolean = false,
    val utr: String? = null,
    val inviteCode: String? = null,
    val duplicateUtr: Boolean = false,
    val createdAt: String? = null,
    val expiresAt: String? = null,
    val hasScreenshot: Boolean? = null,
) {
    fun toDomain() = AccessRequest(
        id = id,
        kind = when (kind) {
            "payment" -> RequestKind.Payment
            "invite" -> RequestKind.Invite
            else -> RequestKind.Unknown
        },
        name = name?.takeIf { it.isNotBlank() },
        plan = plan,
        planLabel = planLabel?.takeIf { it.isNotBlank() } ?: plan,
        expectedAmount = expectedAmount,
        amountPaid = amountPaid,
        amountMismatch = amountMismatch,
        utr = utr?.takeIf { it.isNotBlank() },
        inviteCode = inviteCode?.takeIf { it.isNotBlank() },
        duplicateUtr = duplicateUtr,
        createdAt = parseInstant(createdAt),
        expiresAt = parseInstant(expiresAt),
        hasScreenshot = hasScreenshot,
    )
}

@Serializable
data class RequestListResponse(val requests: List<AccessRequestDto>)

@Serializable
data class RequestDetailResponse(val request: AccessRequestDto)

/**
 * `POST /admin/requests/:id/decision` answers `{ok, decision, expiresAt}`. `expiresAt` is the grant's expiry
 * and is deliberately NOT modeled: Android never reads, shows, or computes authorization expiry.
 */
@Serializable
data class DecisionResponse(val ok: Boolean = false, val decision: String? = null)
