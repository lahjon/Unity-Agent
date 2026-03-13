package com.spritely.remote.ui.theme

import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

// Spritely dark theme colors matching the desktop app
val SpriteBg = Color(0xFF1A1A2E)
val SpriteBgElevated = Color(0xFF252542)
val SpriteSurface = Color(0xFF16213E)
val SpriteAccent = Color(0xFF7C6AEF)
val SpriteAccentBright = Color(0xFF9B8AFB)
val SpriteSuccess = Color(0xFF4ADE80)
val SpriteWarning = Color(0xFFFBBF24)
val SpriteDanger = Color(0xFFEF4444)
val SpriteTextPrimary = Color(0xFFE2E8F0)
val SpriteTextMuted = Color(0xFF94A3B8)
val SpriteTextDisabled = Color(0xFF475569)

private val DarkColorScheme = darkColorScheme(
    primary = SpriteAccent,
    onPrimary = Color.White,
    primaryContainer = SpriteAccent.copy(alpha = 0.2f),
    secondary = SpriteAccentBright,
    background = SpriteBg,
    surface = SpriteSurface,
    surfaceVariant = SpriteBgElevated,
    onBackground = SpriteTextPrimary,
    onSurface = SpriteTextPrimary,
    onSurfaceVariant = SpriteTextMuted,
    error = SpriteDanger,
    outline = SpriteTextDisabled
)

@Composable
fun SpritelyRemoteTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = DarkColorScheme,
        content = content
    )
}
