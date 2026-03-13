package com.spritely.remote.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.ExperimentalMaterialApi
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material.pullrefresh.PullRefreshIndicator
import androidx.compose.material.pullrefresh.pullRefresh
import androidx.compose.material.pullrefresh.rememberPullRefreshState
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
import kotlinx.coroutines.delay
import java.time.Duration
import java.time.Instant

@OptIn(ExperimentalMaterial3Api::class, ExperimentalMaterialApi::class)
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
    var isRefreshing by remember { mutableStateOf(false) }

    val pullRefreshState = rememberPullRefreshState(
        refreshing = isRefreshing,
        onRefresh = {
            isRefreshing = true
            onRefresh()
        }
    )

    // Auto-clear refreshing after data updates
    LaunchedEffect(state.activeTasks, state.historyTasks) {
        if (isRefreshing) {
            delay(300)
            isRefreshing = false
        }
    }

    // Filter tasks by selected project
    val selectedProject = state.selectedProject
    val filteredActive = if (selectedProject != null) {
        state.activeTasks.filter { it.projectPath == selectedProject.path }
    } else state.activeTasks
    val filteredHistory = if (selectedProject != null) {
        state.historyTasks.filter { it.projectPath == selectedProject.path }
    } else state.historyTasks

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text(
                            selectedProject?.name ?: "All Projects",
                            fontSize = 18.sp,
                            fontWeight = FontWeight.Bold,
                            color = SpriteTextPrimary
                        )
                        Text(
                            "${filteredActive.count { !it.isFinished }} active tasks",
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
            LargeFloatingActionButton(
                onClick = onCreateTask,
                containerColor = SpriteAccent,
                shape = CircleShape
            ) {
                Icon(
                    Icons.Default.Add,
                    contentDescription = "Create Task",
                    modifier = Modifier.size(32.dp)
                )
            }
        },
        containerColor = SpriteBg
    ) { padding ->
        Box(
            modifier = Modifier
                .padding(padding)
                .fillMaxSize()
                .pullRefresh(pullRefreshState)
        ) {
            Column(modifier = Modifier.fillMaxSize()) {
                // Tab row: Active / History
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 8.dp)
                ) {
                    FilterChip(
                        selected = !showHistory,
                        onClick = { showHistory = false },
                        label = { Text("Active (${filteredActive.size})") },
                        modifier = Modifier.padding(end = 8.dp),
                        colors = FilterChipDefaults.filterChipColors(
                            selectedContainerColor = SpriteAccent.copy(alpha = 0.2f),
                            selectedLabelColor = SpriteAccentBright
                        )
                    )
                    FilterChip(
                        selected = showHistory,
                        onClick = { showHistory = true },
                        label = { Text("History (${filteredHistory.size})") },
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
                val tasks = if (showHistory) filteredHistory else filteredActive

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
                                when {
                                    showHistory && selectedProject != null ->
                                        "No completed tasks for ${selectedProject.name}"
                                    showHistory -> "No completed tasks"
                                    selectedProject != null ->
                                        "No active tasks for ${selectedProject.name}"
                                    else -> "No active tasks"
                                },
                                color = SpriteTextDisabled
                            )
                            if (!showHistory) {
                                Spacer(Modifier.height(8.dp))
                                Text(
                                    "Tap + to create a new task",
                                    fontSize = 12.sp,
                                    color = SpriteTextDisabled
                                )
                            }
                        }
                    }
                } else {
                    LazyColumn(
                        modifier = Modifier.fillMaxSize(),
                        contentPadding = PaddingValues(
                            start = 16.dp, end = 16.dp,
                            top = 8.dp, bottom = 96.dp
                        ),
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

            PullRefreshIndicator(
                refreshing = isRefreshing,
                state = pullRefreshState,
                modifier = Modifier.align(Alignment.TopCenter),
                contentColor = SpriteAccentBright
            )
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
            // Header: status dot + task number + status label
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(10.dp)
                        .clip(CircleShape)
                        .background(statusColor(task.status))
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    "#${task.taskNumber}",
                    fontSize = 13.sp,
                    color = SpriteAccentBright,
                    fontWeight = FontWeight.Bold
                )
                Spacer(Modifier.width(8.dp))
                Text(
                    task.status,
                    fontSize = 11.sp,
                    color = statusColor(task.status),
                    fontWeight = FontWeight.Medium
                )
                Spacer(Modifier.weight(1f))
                if (task.isRunning || task.isPaused) {
                    val elapsed = formatElapsedTime(task.startTime)
                    if (elapsed.isNotEmpty()) {
                        Text(elapsed, fontSize = 10.sp, color = SpriteTextMuted)
                        Spacer(Modifier.width(6.dp))
                    }
                }
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

            // Status text (truncated to 2 lines)
            if (task.statusText.isNotBlank() && task.statusText != task.status) {
                Text(
                    task.statusText,
                    fontSize = 11.sp,
                    color = SpriteTextMuted,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.padding(top = 4.dp)
                )
            }

            // Iteration progress + project name
            val hasIteration = task.maxIterations > 0
            val hasProject = task.projectName.isNotBlank()
            if (hasIteration || hasProject) {
                Spacer(Modifier.height(4.dp))
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    if (hasIteration) {
                        Badge(
                            "Iter ${task.currentIteration}/${task.maxIterations}",
                            SpriteAccentBright
                        )
                    }
                    if (hasProject) {
                        Text(
                            task.projectName,
                            fontSize = 10.sp,
                            color = SpriteTextMuted
                        )
                    }
                }
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

private fun formatElapsedTime(startTime: String): String {
    if (startTime.isBlank()) return ""
    return try {
        val start = Instant.parse(startTime)
        val elapsed = Duration.between(start, Instant.now())
        val hours = elapsed.toHours()
        val minutes = elapsed.toMinutes() % 60
        when {
            hours > 0 -> "${hours}h ${minutes}m"
            minutes > 0 -> "${minutes}m"
            else -> "<1m"
        }
    } catch (_: Exception) {
        ""
    }
}
