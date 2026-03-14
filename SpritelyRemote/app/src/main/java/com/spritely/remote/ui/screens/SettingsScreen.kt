package com.spritely.remote.ui.screens

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.spritely.remote.data.model.AppSettings
import com.spritely.remote.ui.theme.*

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    appSettings: AppSettings?,
    autoQueueEnabled: Boolean,
    onAutoQueueChange: (Boolean) -> Unit,
    onRefresh: () -> Unit,
    onBack: () -> Unit
) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Text(
                        "Settings",
                        fontSize = 18.sp,
                        fontWeight = FontWeight.Bold,
                        color = SpriteTextPrimary
                    )
                },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Default.ArrowBack, "Back", tint = SpriteTextMuted)
                    }
                },
                actions = {
                    IconButton(onClick = onRefresh) {
                        Icon(Icons.Default.Refresh, "Refresh", tint = SpriteTextMuted)
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = SpriteSurface)
            )
        },
        containerColor = SpriteBg
    ) { padding ->
        Column(
            modifier = Modifier
                .padding(padding)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            // Remote App Settings
            SectionHeader("REMOTE APP")

            SettingsCard {
                SettingsToggle(
                    label = "Auto-Queue",
                    description = "New tasks depend on the previous task, forming a linear queue",
                    checked = autoQueueEnabled,
                    onCheckedChange = onAutoQueueChange
                )
            }

            // Desktop Settings (read-only, synced from server)
            SectionHeader("DESKTOP SETTINGS (SYNCED)")

            if (appSettings != null) {
                SettingsCard {
                    SettingsInfoRow("Auto-Commit", if (appSettings.autoCommit) "On" else "Off")
                    HorizontalDivider(color = SpriteBg, thickness = 1.dp)
                    SettingsInfoRow("Auto-Queue (Desktop)", if (appSettings.autoQueue) "On" else "Off")
                    HorizontalDivider(color = SpriteBg, thickness = 1.dp)
                    SettingsInfoRow("Auto-Verify", if (appSettings.autoVerify) "On" else "Off")
                    HorizontalDivider(color = SpriteBg, thickness = 1.dp)
                    SettingsInfoRow("Max Concurrent Tasks", appSettings.maxConcurrentTasks.toString())
                    HorizontalDivider(color = SpriteBg, thickness = 1.dp)
                    SettingsInfoRow("Task Timeout", "${appSettings.taskTimeoutMinutes} min")
                    HorizontalDivider(color = SpriteBg, thickness = 1.dp)
                    SettingsInfoRow("Opus Effort", appSettings.opusEffortLevel.replaceFirstChar { it.uppercase() })
                }
            } else {
                Card(
                    colors = CardDefaults.cardColors(containerColor = SpriteBgElevated),
                    shape = RoundedCornerShape(12.dp)
                ) {
                    Text(
                        "Unable to load desktop settings",
                        color = SpriteTextMuted,
                        fontSize = 13.sp,
                        modifier = Modifier.padding(16.dp)
                    )
                }
            }
        }
    }
}

@Composable
private fun SectionHeader(text: String) {
    Text(
        text = text,
        fontSize = 11.sp,
        color = SpriteAccent,
        fontWeight = FontWeight.SemiBold,
        modifier = Modifier.padding(start = 4.dp)
    )
}

@Composable
private fun SettingsCard(content: @Composable ColumnScope.() -> Unit) {
    Card(
        colors = CardDefaults.cardColors(containerColor = SpriteBgElevated),
        shape = RoundedCornerShape(12.dp)
    ) {
        Column(
            modifier = Modifier.padding(4.dp),
            content = content
        )
    }
}

@Composable
private fun SettingsToggle(
    label: String,
    description: String,
    checked: Boolean,
    onCheckedChange: (Boolean) -> Unit
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(label, fontSize = 14.sp, color = SpriteTextPrimary, fontWeight = FontWeight.Medium)
            Text(description, fontSize = 11.sp, color = SpriteTextMuted, lineHeight = 14.sp)
        }
        Spacer(Modifier.width(12.dp))
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = SpriteAccentBright,
                checkedTrackColor = SpriteAccent.copy(alpha = 0.4f),
                uncheckedThumbColor = SpriteTextMuted,
                uncheckedTrackColor = SpriteBg
            )
        )
    }
}

@Composable
private fun SettingsInfoRow(label: String, value: String) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 12.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically
    ) {
        Text(label, fontSize = 13.sp, color = SpriteTextPrimary)
        Text(value, fontSize = 13.sp, color = SpriteTextMuted)
    }
}
