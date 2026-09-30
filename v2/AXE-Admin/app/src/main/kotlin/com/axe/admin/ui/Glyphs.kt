package com.axe.admin.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import com.axe.admin.model.RequestKind
import com.axe.admin.ui.theme.AxeColors

/** Small drawn glyphs (no icon-font dependency) used by the list and detail screens. */

@Composable
fun CardGlyph(color: Color, modifier: Modifier = Modifier) = Canvas(modifier.size(22.dp)) {
    val w = size.width; val h = size.height; val s = w * 0.09f
    drawRoundRect(color, Offset(0f, h * 0.15f), Size(w, h * 0.7f), CornerRadius(w * 0.14f), style = Stroke(s))
    drawRect(color, Offset(0f, h * 0.32f), Size(w, h * 0.16f))
    drawRoundRect(color, Offset(w * 0.12f, h * 0.62f), Size(w * 0.28f, h * 0.1f), CornerRadius(2f))
}

@Composable
fun GroupGlyph(color: Color, modifier: Modifier = Modifier) = Canvas(modifier.size(22.dp)) {
    val w = size.width; val h = size.height
    drawCircle(color, w * 0.14f, Offset(w * 0.5f, h * 0.3f))
    drawCircle(color, w * 0.11f, Offset(w * 0.17f, h * 0.38f))
    drawCircle(color, w * 0.11f, Offset(w * 0.83f, h * 0.38f))
    drawRoundRect(color, Offset(w * 0.25f, h * 0.52f), Size(w * 0.5f, h * 0.36f), CornerRadius(w * 0.2f))
    drawRoundRect(color, Offset(0f, h * 0.58f), Size(w * 0.28f, h * 0.26f), CornerRadius(w * 0.12f))
    drawRoundRect(color, Offset(w * 0.72f, h * 0.58f), Size(w * 0.28f, h * 0.26f), CornerRadius(w * 0.12f))
}

@Composable
fun ClockGlyph(color: Color, size: Dp = 16.dp, modifier: Modifier = Modifier) = Canvas(modifier.size(size)) {
    val w = this.size.width; val s = w * 0.11f
    drawCircle(color, w / 2 - s / 2, center, style = Stroke(s))
    drawLine(color, center, Offset(w / 2, w * 0.24f), s, StrokeCap.Round)
    drawLine(color, center, Offset(w * 0.72f, w * 0.58f), s, StrokeCap.Round)
}

@Composable
fun PersonGlyph(color: Color, size: Dp = 22.dp, modifier: Modifier = Modifier) = Canvas(modifier.size(size)) {
    val w = this.size.width; val s = w * 0.09f
    drawCircle(color, w * 0.22f, Offset(w / 2, w * 0.3f), style = Stroke(s))
    drawArc(color, 180f, 180f, false, Offset(w * 0.12f, w * 0.6f), Size(w * 0.76f, w * 0.56f), style = Stroke(s))
}

@Composable
fun ImageGlyph(color: Color, size: Dp = 22.dp, modifier: Modifier = Modifier) = Canvas(modifier.size(size)) {
    val w = this.size.width; val s = w * 0.08f
    drawRoundRect(color, Offset(s, s), Size(w - 2 * s, w - 2 * s), CornerRadius(w * 0.12f), style = Stroke(s))
    drawCircle(color, w * 0.08f, Offset(w * 0.34f, w * 0.34f))
    val hill = Path().apply {
        moveTo(w * 0.14f, w * 0.8f); lineTo(w * 0.42f, w * 0.52f); lineTo(w * 0.58f, w * 0.68f)
        lineTo(w * 0.72f, w * 0.52f); lineTo(w * 0.86f, w * 0.8f); close()
    }
    drawPath(hill, color)
}

@Composable
fun ChevronGlyph(color: Color, modifier: Modifier = Modifier) = Canvas(modifier.size(20.dp)) {
    val w = size.width; val s = w * 0.12f
    drawLine(color, Offset(w * 0.34f, w * 0.14f), Offset(w * 0.68f, w * 0.5f), s, StrokeCap.Round)
    drawLine(color, Offset(w * 0.68f, w * 0.5f), Offset(w * 0.34f, w * 0.86f), s, StrokeCap.Round)
}

@Composable
fun BackGlyph(color: Color, modifier: Modifier = Modifier) = Canvas(modifier.size(24.dp)) {
    val w = size.width; val s = w * 0.09f
    drawLine(color, Offset(w * 0.88f, w * 0.5f), Offset(w * 0.14f, w * 0.5f), s, StrokeCap.Round)
    drawLine(color, Offset(w * 0.14f, w * 0.5f), Offset(w * 0.46f, w * 0.18f), s, StrokeCap.Round)
    drawLine(color, Offset(w * 0.14f, w * 0.5f), Offset(w * 0.46f, w * 0.82f), s, StrokeCap.Round)
}

@Composable
fun RefreshGlyph(color: Color, size: Dp = 22.dp, modifier: Modifier = Modifier) = Canvas(modifier.size(size)) {
    val w = this.size.width; val s = w * 0.1f
    drawArc(color, -50f, 290f, false, Offset(w * 0.14f, w * 0.14f), Size(w * 0.72f, w * 0.72f), style = Stroke(s, cap = StrokeCap.Round))
    val head = Path().apply {
        moveTo(w * 0.62f, w * 0.06f); lineTo(w * 0.9f, w * 0.26f); lineTo(w * 0.56f, w * 0.34f); close()
    }
    drawPath(head, color)
}

@Composable
fun LogoutGlyph(color: Color, modifier: Modifier = Modifier) = Canvas(modifier.size(24.dp)) {
    val w = size.width; val s = w * 0.09f
    val door = Path().apply {
        moveTo(w * 0.46f, w * 0.14f); lineTo(w * 0.14f, w * 0.14f); lineTo(w * 0.14f, w * 0.86f); lineTo(w * 0.46f, w * 0.86f)
    }
    drawPath(door, color, style = Stroke(s, cap = StrokeCap.Round, join = androidx.compose.ui.graphics.StrokeJoin.Round))
    drawLine(color, Offset(w * 0.36f, w * 0.5f), Offset(w * 0.88f, w * 0.5f), s, StrokeCap.Round)
    drawLine(color, Offset(w * 0.88f, w * 0.5f), Offset(w * 0.68f, w * 0.3f), s, StrokeCap.Round)
    drawLine(color, Offset(w * 0.88f, w * 0.5f), Offset(w * 0.68f, w * 0.7f), s, StrokeCap.Round)
}

@Composable
fun CheckGlyph(color: Color, modifier: Modifier = Modifier) = Canvas(modifier.size(22.dp)) {
    val w = size.width; val s = w * 0.12f
    drawLine(color, Offset(w * 0.16f, w * 0.54f), Offset(w * 0.4f, w * 0.78f), s, StrokeCap.Round)
    drawLine(color, Offset(w * 0.4f, w * 0.78f), Offset(w * 0.86f, w * 0.26f), s, StrokeCap.Round)
}

@Composable
fun TrashGlyph(color: Color, modifier: Modifier = Modifier) = Canvas(modifier.size(22.dp)) {
    val w = size.width; val s = w * 0.09f
    drawLine(color, Offset(w * 0.14f, w * 0.26f), Offset(w * 0.86f, w * 0.26f), s, StrokeCap.Round)
    drawLine(color, Offset(w * 0.38f, w * 0.14f), Offset(w * 0.62f, w * 0.14f), s, StrokeCap.Round)
    val bin = Path().apply {
        moveTo(w * 0.22f, w * 0.34f); lineTo(w * 0.28f, w * 0.88f); lineTo(w * 0.72f, w * 0.88f); lineTo(w * 0.78f, w * 0.34f)
    }
    drawPath(bin, color, style = Stroke(s, cap = StrokeCap.Round, join = androidx.compose.ui.graphics.StrokeJoin.Round))
    drawLine(color, Offset(w * 0.42f, w * 0.46f), Offset(w * 0.42f, w * 0.76f), s * 0.8f, StrokeCap.Round)
    drawLine(color, Offset(w * 0.58f, w * 0.46f), Offset(w * 0.58f, w * 0.76f), s * 0.8f, StrokeCap.Round)
}

/** The round colored badge at the start of a request card: orange card for payments, purple group for invitations. */
@Composable
fun KindIcon(kind: RequestKind, size: Dp = 52.dp) {
    val (bg, fg) = when (kind) {
        RequestKind.Payment -> AxeColors.Payment to Color.White
        RequestKind.Invite -> AxeColors.Invitation to Color.White
        RequestKind.Unknown -> Color(0xFF3A4A5E) to Color.White
    }
    Box(Modifier.size(size).background(bg, CircleShape), contentAlignment = Alignment.Center) {
        if (kind == RequestKind.Invite) GroupGlyph(fg) else CardGlyph(fg)
    }
}
