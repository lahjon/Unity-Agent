package com.spritely.remote.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.spritely.remote.data.model.TaskDto
import com.spritely.remote.ui.theme.*
import kotlinx.coroutines.delay
import java.time.Duration
import java.time.Instant
import java.time.format.DateTimeFormatter

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskDetailScreen(
    task: TaskDto,
    onBack: () -> Unit,
    onCancel: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onRefresh: (String) -> Unit = {}
) {
    // Auto-refresh for active tasks every 2 seconds
    LaunchedEffect(task.id, task.status) {
        if (task.isRunning) {
            while (true) {
                delay(2000)
                onRefresh(task.id)
            }
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Task #${task.taskNumber}", fontSize = 16.sp) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.Default.ArrowBack, "Back")
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(containerColor = SpriteSurface)
            )
        },
        containerColor = SpriteBg
    ) { padding ->
        val outputLines = remember(task.output) {
            task.output?.lines() ?: emptyList()
        }
        val listState = rememberLazyListState()

        // Auto-scroll to bottom when output changes
        LaunchedEffect(outputLines.size) {
            if (outputLines.isNotEmpty()) {
                // +2 accounts for header items before output lines
                listState.animateScrollToItem(outputLines.size + 2)
            }
        }

        LazyColumn(
            state = listState,
            modifier = Modifier
                .padding(padding)
                .fillMaxSize(),
            contentPadding = PaddingValues(16.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            // Status header with chip
            item {
                StatusHeader(task)
            }

            // Action buttons
            if (task.isRunning || task.isPaused || task.isQueued) {
                item {
                    ActionButtons(
                        task = task,
                        onCancel = onCancel,
                        onPause = onPause,
                        onResume = onResume
                    )
                }
            }

            // Description
            item {
                SectionHeader("Description")
                Card(
                    colors = CardDefaults.cardColors(containerColor = SpriteBgElevated),
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        task.description,
                        fontSize = 13.sp,
                        color = SpriteTextPrimary,
                        modifier = Modifier.padding(16.dp),
                        lineHeight = 20.sp
                    )
                }
            }

            // Changed files
            if (task.changedFiles.isNotEmpty()) {
                item {
                    SectionHeader("Changed Files (${task.changedFiles.size})")
                    Card(
                        colors = CardDefaults.cardColors(containerColor = SpriteBgElevated),
                        shape = RoundedCornerShape(12.dp),
                        modifier = Modifier.fillMaxWidth()
                    ) {
                        Column(modifier = Modifier.padding(12.dp)) {
                            task.changedFiles.forEach { file ->
                                Text(
                                    file,
                                    fontSize = 11.sp,
                                    color = SpriteTextMuted,
                                    fontFamily = FontFamily.Monospace,
                                    modifier = Modifier.padding(vertical = 2.dp)
                                )
                            }
                        }
                    }
                }
            }

            // Output section header
            if (outputLines.isNotEmpty()) {
                item {
                    SectionHeader("Output")
                }

                items(outputLines) { line ->
                    Text(
                        line,
                        fontSize = 11.sp,
                        color = SpriteTextMuted,
                        fontFamily = FontFamily.Monospace,
                        lineHeight = 16.sp,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(horizontal = 4.dp)
                    )
                }
            }

            // Bottom spacer
            item {
                Spacer(Modifier.height(80.dp))
            }
        }
    }
}

@Composable
private fun StatusHeader(task: TaskDto) {
    Card(
        colors = CardDefaults.cardColors(containerColor = SpriteBgElevated),
        shape = RoundedCornerShape(12.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        Column(modifier = Modifier.padding(16.dp)) {
            // Status chip row
            Row(
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                modifier = Modifier.fillMaxWidth()
            ) {
                StatusChip(task.status)

                if (task.isRunning) {
                    val elapsed = remember(task.startTime) { formatElapsed(task.startTime) }
                    // Re-compute elapsed every second for running tasks
                    var elapsedText by remember { mutableStateOf(elapsed) }
                    LaunchedEffect(task.startTime) {
                        while (true) {
                            elapsedText = formatElapsed(task.startTime)
                            delay(1000)
                        }
                    }
                    Text(
                        elapsedText,
                        fontSize = 12.sp,
                        color = SpriteTextMuted
                    )
                }
            }

            Spacer(Modifier.height(12.dp))

            // Info rows
            if (task.maxIterations > 0) {
                InfoRow("Iteration", "${task.currentIteration} / ${task.maxIterations}")
            }
            InfoRow("Model", task.model)
            InfoRow("Project", task.projectName)
            if (task.isFeatureMode) InfoRow("Mode", "Feature Mode")
            if (task.statusText.isNotBlank() && task.statusText != task.status) {
                InfoRow("Status", task.statusText)
            }
        }
    }
}

@Composable
private fun StatusChip(status: String) {
    val (color, label) = when (status) {
        "Running", "Planning", "Verifying", "Committing" -> SpriteSuccess to status
        "Completed" -> Color(0xFF60A5FA) to "Completed"
        "Failed" -> SpriteDanger to "Failed"
        "Paused" -> SpriteWarning to "Paused"
        "Queued", "InitQueued" -> SpriteTextMuted to "Queued"
        "Cancelled" -> SpriteTextDisabled to "Cancelled"
        else -> SpriteTextMuted to status
    }

    Surface(
        color = color.copy(alpha = 0.15f),
        shape = RoundedCornerShape(16.dp)
    ) {
        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 6.dp)
        ) {
            Box(
                modifier = Modifier
                    .size(8.dp)
                    .clip(CircleShape)
                    .background(color)
            )
            Spacer(Modifier.width(6.dp))
            Text(
                label,
                fontSize = 12.sp,
                fontWeight = FontWeight.SemiBold,
                color = color
            )
        }
    }
}

@Composable
private fun ActionButtons(
    task: TaskDto,
    onCancel: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit
) {
    Row(
        horizontalArrangement = Arrangement.spacedBy(8.dp),
        modifier = Modifier.fillMaxWidth()
    ) {
        if (task.isRunning) {
            Button(
                onClick = onPause,
                colors = ButtonDefaults.buttonColors(containerColor = SpriteWarning),
                modifier = Modifier.weight(1f),
                shape = RoundedCornerShape(10.dp)
            ) {
                Text("Pause", fontWeight = FontWeight.Bold)
            }
        }
        if (task.isPaused) {
            Button(
                onClick = onResume,
                colors = ButtonDefaults.buttonColors(containerColor = SpriteSuccess),
                modifier = Modifier.weight(1f),
                shape = RoundedCornerShape(10.dp)
            ) {
                Text("Resume", fontWeight = FontWeight.Bold)
            }
        }
        Button(
            onClick = onCancel,
            colors = ButtonDefaults.buttonColors(containerColor = SpriteDanger),
            modifier = Modifier.weight(1f),
            shape = RoundedCornerShape(10.dp)
        ) {
            Text("Cancel", fontWeight = FontWeight.Bold)
        }
    }
}

private fun formatElapsed(startTimeStr: String): String {
    return try {
        val start = Instant.parse(startTimeStr)
        val elapsed = Duration.between(start, Instant.now())
        val hours = elapsed.toHours()
        val minutes = elapsed.toMinutesPart()
        val seconds = elapsed.toSecondsPart()
        if (hours > 0) "${hours}h ${minutes}m ${seconds}s"
        else if (minutes > 0) "${minutes}m ${seconds}s"
        else "${seconds}s"
    } catch (_: Exception) {
        ""
    }
}

@Composable
private fun SectionHeader(title: String) {
    Text(
        title,
        fontSize = 11.sp,
        color = SpriteAccent,
        fontWeight = FontWeight.SemiBold,
        modifier = Modifier.padding(bottom = 8.dp)
    )
}

@Composable
private fun InfoRow(label: String, value: String) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 3.dp)
    ) {
        Text(label, fontSize = 12.sp, color = SpriteTextMuted, modifier = Modifier.width(90.dp))
        Text(value, fontSize = 12.sp, color = SpriteTextPrimary)
    }
}
