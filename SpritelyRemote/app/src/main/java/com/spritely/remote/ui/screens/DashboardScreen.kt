package com.spritely.remote.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.spritely.remote.data.model.TaskDto
import com.spritely.remote.ui.theme.*
import com.spritely.remote.viewmodel.TaskListState

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(
    state: TaskListState,
    onTaskClick: (TaskDto) -> Unit,
    onCreateTask: () -> Unit,
    onCancelTask: (String) -> Unit,
    onPauseTask: (String) -> Unit,
    onResumeTask: (String) -> Unit,
    onDisconnect: () -> Unit,
    onRefresh: () -> Unit
) {
    var showHistory by remember { mutableStateOf(false) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("SpritelyRemote", fontSize = 16.sp, fontWeight = FontWeight.Bold)
                        Text(
                            "${state.activeTasks.count { !it.isFinished }} active tasks",
                            fontSize = 11.sp,
                            color = SpriteTextMuted
                        )
                    }
                },
                actions = {
                    IconButton(onClick = onRefresh) {
                        Icon(Icons.Default.Refresh, "Refresh", tint = SpriteTextMuted)
                    }
                    IconButton(onClick = onDisconnect) {
                        Icon(Icons.Default.LinkOff, "Disconnect", tint = SpriteTextMuted)
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = SpriteSurface
                )
            )
        },
        floatingActionButton = {
            FloatingActionButton(
                onClick = onCreateTask,
                containerColor = SpriteAccent
            ) {
                Icon(Icons.Default.Add, "Create Task")
            }
        },
        containerColor = SpriteBg
    ) { padding ->
        Column(modifier = Modifier.padding(padding)) {
            // Tab row: Active / History
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 8.dp)
            ) {
                FilterChip(
                    selected = !showHistory,
                    onClick = { showHistory = false },
                    label = { Text("Active (${state.activeTasks.size})") },
                    modifier = Modifier.padding(end = 8.dp),
                    colors = FilterChipDefaults.filterChipColors(
                        selectedContainerColor = SpriteAccent.copy(alpha = 0.2f),
                        selectedLabelColor = SpriteAccentBright
                    )
                )
                FilterChip(
                    selected = showHistory,
                    onClick = { showHistory = true },
                    label = { Text("History (${state.historyTasks.size})") },
                    colors = FilterChipDefaults.filterChipColors(
                        selectedContainerColor = SpriteAccent.copy(alpha = 0.2f),
                        selectedLabelColor = SpriteAccentBright
                    )
                )
            }

            // Error banner
            if (state.error != null) {
                Card(
                    colors = CardDefaults.cardColors(containerColor = SpriteDanger.copy(alpha = 0.15f)),
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 4.dp)
                ) {
                    Text(
                        text = state.error,
                        color = SpriteDanger,
                        fontSize = 12.sp,
                        modifier = Modifier.padding(12.dp)
                    )
                }
            }

            // Task list
            val tasks = if (showHistory) state.historyTasks else state.activeTasks

            if (tasks.isEmpty()) {
                Box(
                    modifier = Modifier
                        .fillMaxSize()
                        .padding(32.dp),
                    contentAlignment = Alignment.Center
                ) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(
                            if (showHistory) Icons.Default.History else Icons.Default.PlayArrow,
                            contentDescription = null,
                            tint = SpriteTextDisabled,
                            modifier = Modifier.size(48.dp)
                        )
                        Spacer(Modifier.height(12.dp))
                        Text(
                            if (showHistory) "No completed tasks" else "No active tasks",
                            color = SpriteTextDisabled
                        )
                    }
                }
            } else {
                LazyColumn(
                    modifier = Modifier.fillMaxSize(),
                    contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
                    verticalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    items(tasks, key = { it.id }) { task ->
                        TaskCard(
                            task = task,
                            onClick = { onTaskClick(task) },
                            onCancel = { onCancelTask(task.id) },
                            onPause = { onPauseTask(task.id) },
                            onResume = { onResumeTask(task.id) }
                        )
                    }
                }
            }
        }
    }
}

@Composable
private fun TaskCard(
    task: TaskDto,
    onClick: () -> Unit,
    onCancel: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit
) {
    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
        colors = CardDefaults.cardColors(containerColor = SpriteBgElevated),
        shape = RoundedCornerShape(12.dp)
    ) {
        Column(modifier = Modifier.padding(14.dp)) {
            // Header: status dot + task number + model
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(8.dp)
                        .clip(CircleShape)
                        .background(statusColor(task.status))
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    "#${task.taskNumber}",
                    fontSize = 12.sp,
                    color = SpriteAccentBright,
                    fontWeight = FontWeight.Bold
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    task.statusText.ifBlank { task.status },
                    fontSize = 11.sp,
                    color = statusColor(task.status)
                )
                Spacer(Modifier.weight(1f))
                Text(
                    task.model,
                    fontSize = 10.sp,
                    color = SpriteTextDisabled
                )
            }

            Spacer(Modifier.height(6.dp))

            // Description
            Text(
                task.description,
                fontSize = 13.sp,
                color = SpriteTextPrimary,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis
            )

            // Project name
            if (task.projectName.isNotBlank()) {
                Text(
                    task.projectName,
                    fontSize = 10.sp,
                    color = SpriteTextMuted,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }

            // Action buttons for active tasks
            if (task.isRunning || task.isPaused || task.isQueued) {
                Spacer(Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (task.isRunning) {
                        SmallActionButton("Pause", SpriteWarning, onPause)
                    }
                    if (task.isPaused) {
                        SmallActionButton("Resume", SpriteSuccess, onResume)
                    }
                    SmallActionButton("Cancel", SpriteDanger, onCancel)
                }
            }

            // Verified / Committed badges for finished tasks
            if (task.isFinished) {
                Spacer(Modifier.height(6.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    if (task.isVerified) {
                        Badge("Verified", SpriteSuccess)
                    }
                    if (task.isCommitted) {
                        Badge("Committed", SpriteAccent)
                    }
                    if (task.changedFiles.isNotEmpty()) {
                        Badge("${task.changedFiles.size} files", SpriteTextMuted)
                    }
                }
            }
        }
    }
}

@Composable
private fun SmallActionButton(label: String, color: Color, onClick: () -> Unit) {
    OutlinedButton(
        onClick = onClick,
        contentPadding = PaddingValues(horizontal = 12.dp, vertical = 4.dp),
        colors = ButtonDefaults.outlinedButtonColors(contentColor = color),
        border = ButtonDefaults.outlinedButtonBorder.copy(
            brush = androidx.compose.ui.graphics.SolidColor(color.copy(alpha = 0.5f))
        )
    ) {
        Text(label, fontSize = 11.sp)
    }
}

@Composable
private fun Badge(label: String, color: Color) {
    Surface(
        shape = RoundedCornerShape(4.dp),
        color = color.copy(alpha = 0.15f)
    ) {
        Text(
            label,
            fontSize = 10.sp,
            color = color,
            modifier = Modifier.padding(horizontal = 6.dp, vertical = 2.dp)
        )
    }
}

private fun statusColor(status: String): Color = when (status) {
    "Running", "Planning", "Verifying", "Committing" -> SpriteAccentBright
    "Completed" -> SpriteSuccess
    "Failed", "Cancelled" -> SpriteDanger
    "Paused", "SoftStop" -> SpriteWarning
    "Queued", "InitQueued" -> SpriteTextMuted
    else -> SpriteTextDisabled
}
