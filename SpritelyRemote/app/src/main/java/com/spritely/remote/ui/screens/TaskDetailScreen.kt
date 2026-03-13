package com.spritely.remote.ui.screens

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.spritely.remote.data.model.TaskDto
import com.spritely.remote.ui.theme.*

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun TaskDetailScreen(
    task: TaskDto,
    onBack: () -> Unit,
    onCancel: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit
) {
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
        Column(
            modifier = Modifier
                .padding(padding)
                .verticalScroll(rememberScrollState())
                .padding(16.dp)
        ) {
            // Status card
            Card(
                colors = CardDefaults.cardColors(containerColor = SpriteBgElevated),
                shape = RoundedCornerShape(12.dp),
                modifier = Modifier.fillMaxWidth()
            ) {
                Column(modifier = Modifier.padding(16.dp)) {
                    InfoRow("Status", task.statusText.ifBlank { task.status })
                    InfoRow("Model", task.model)
                    InfoRow("Priority", task.priority)
                    InfoRow("Project", task.projectName)
                    if (task.isFeatureMode) InfoRow("Mode", "Feature Mode")
                    InfoRow("Iteration", "${task.currentIteration} / ${task.maxIterations}")
                }
            }

            Spacer(Modifier.height(12.dp))

            // Description
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

            // Actions
            if (task.isRunning || task.isPaused || task.isQueued) {
                Spacer(Modifier.height(16.dp))
                SectionHeader("Actions")
                Row(
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    if (task.isRunning) {
                        Button(
                            onClick = onPause,
                            colors = ButtonDefaults.buttonColors(containerColor = SpriteWarning),
                            modifier = Modifier.weight(1f)
                        ) { Text("Pause") }
                    }
                    if (task.isPaused) {
                        Button(
                            onClick = onResume,
                            colors = ButtonDefaults.buttonColors(containerColor = SpriteSuccess),
                            modifier = Modifier.weight(1f)
                        ) { Text("Resume") }
                    }
                    Button(
                        onClick = onCancel,
                        colors = ButtonDefaults.buttonColors(containerColor = SpriteDanger),
                        modifier = Modifier.weight(1f)
                    ) { Text("Cancel") }
                }
            }

            // Changed files
            if (task.changedFiles.isNotEmpty()) {
                Spacer(Modifier.height(16.dp))
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

            // Output (if available)
            if (!task.output.isNullOrBlank()) {
                Spacer(Modifier.height(16.dp))
                SectionHeader("Output")
                Card(
                    colors = CardDefaults.cardColors(containerColor = SpriteSurface),
                    shape = RoundedCornerShape(12.dp),
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Text(
                        task.output,
                        fontSize = 11.sp,
                        color = SpriteTextMuted,
                        fontFamily = FontFamily.Monospace,
                        modifier = Modifier.padding(12.dp),
                        lineHeight = 16.sp
                    )
                }
            }

            Spacer(Modifier.height(80.dp))
        }
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
