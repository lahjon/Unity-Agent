package com.spritely.remote.ui.screens

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.spritely.remote.data.model.CreateTaskRequest
import com.spritely.remote.data.model.ProjectDto
import com.spritely.remote.ui.theme.*

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun CreateTaskDialog(
    projects: List<ProjectDto>,
    onDismiss: () -> Unit,
    onCreate: (CreateTaskRequest) -> Unit
) {
    var description by remember { mutableStateOf("") }
    var selectedProject by remember { mutableStateOf(projects.firstOrNull()) }
    var selectedModel by remember { mutableStateOf("ClaudeCode") }
    var selectedPriority by remember { mutableStateOf("Normal") }
    var isFeatureMode by remember { mutableStateOf(false) }
    var useMcp by remember { mutableStateOf(false) }
    var extendedPlanning by remember { mutableStateOf(false) }
    var projectMenuExpanded by remember { mutableStateOf(false) }
    var modelMenuExpanded by remember { mutableStateOf(false) }
    var priorityMenuExpanded by remember { mutableStateOf(false) }

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = SpriteBgElevated,
        title = {
            Text("Create Task", fontWeight = FontWeight.Bold, color = SpriteTextPrimary)
        },
        text = {
            Column(
                modifier = Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                // Description
                OutlinedTextField(
                    value = description,
                    onValueChange = { description = it },
                    label = { Text("Task Description") },
                    minLines = 3,
                    maxLines = 6,
                    modifier = Modifier.fillMaxWidth(),
                    colors = OutlinedTextFieldDefaults.colors(
                        focusedBorderColor = SpriteAccent,
                        unfocusedBorderColor = SpriteTextDisabled,
                        cursorColor = SpriteAccent
                    )
                )

                // Project selector
                ExposedDropdownMenuBox(
                    expanded = projectMenuExpanded,
                    onExpandedChange = { projectMenuExpanded = it }
                ) {
                    OutlinedTextField(
                        value = selectedProject?.name ?: "Select project",
                        onValueChange = {},
                        readOnly = true,
                        label = { Text("Project") },
                        trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = projectMenuExpanded) },
                        modifier = Modifier
                            .menuAnchor()
                            .fillMaxWidth(),
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = SpriteAccent,
                            unfocusedBorderColor = SpriteTextDisabled
                        )
                    )
                    ExposedDropdownMenu(
                        expanded = projectMenuExpanded,
                        onDismissRequest = { projectMenuExpanded = false }
                    ) {
                        projects.forEach { project ->
                            DropdownMenuItem(
                                text = {
                                    Column {
                                        Text(project.name, fontSize = 13.sp)
                                        if (project.shortDescription.isNotBlank()) {
                                            Text(
                                                project.shortDescription,
                                                fontSize = 10.sp,
                                                color = SpriteTextMuted,
                                                maxLines = 1
                                            )
                                        }
                                    }
                                },
                                onClick = {
                                    selectedProject = project
                                    projectMenuExpanded = false
                                }
                            )
                        }
                    }
                }

                // Model selector
                ExposedDropdownMenuBox(
                    expanded = modelMenuExpanded,
                    onExpandedChange = { modelMenuExpanded = it }
                ) {
                    OutlinedTextField(
                        value = selectedModel,
                        onValueChange = {},
                        readOnly = true,
                        label = { Text("Model") },
                        trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = modelMenuExpanded) },
                        modifier = Modifier
                            .menuAnchor()
                            .fillMaxWidth(),
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = SpriteAccent,
                            unfocusedBorderColor = SpriteTextDisabled
                        )
                    )
                    ExposedDropdownMenu(
                        expanded = modelMenuExpanded,
                        onDismissRequest = { modelMenuExpanded = false }
                    ) {
                        listOf("ClaudeCode", "Gemini").forEach { model ->
                            DropdownMenuItem(
                                text = { Text(model) },
                                onClick = {
                                    selectedModel = model
                                    modelMenuExpanded = false
                                }
                            )
                        }
                    }
                }

                // Priority selector
                ExposedDropdownMenuBox(
                    expanded = priorityMenuExpanded,
                    onExpandedChange = { priorityMenuExpanded = it }
                ) {
                    OutlinedTextField(
                        value = selectedPriority,
                        onValueChange = {},
                        readOnly = true,
                        label = { Text("Priority") },
                        trailingIcon = { ExposedDropdownMenuDefaults.TrailingIcon(expanded = priorityMenuExpanded) },
                        modifier = Modifier
                            .menuAnchor()
                            .fillMaxWidth(),
                        colors = OutlinedTextFieldDefaults.colors(
                            focusedBorderColor = SpriteAccent,
                            unfocusedBorderColor = SpriteTextDisabled
                        )
                    )
                    ExposedDropdownMenu(
                        expanded = priorityMenuExpanded,
                        onDismissRequest = { priorityMenuExpanded = false }
                    ) {
                        listOf("Low", "Normal", "High", "Critical").forEach { priority ->
                            DropdownMenuItem(
                                text = { Text(priority) },
                                onClick = {
                                    selectedPriority = priority
                                    priorityMenuExpanded = false
                                }
                            )
                        }
                    }
                }

                // Toggle options
                ToggleRow("Feature Mode", isFeatureMode) { isFeatureMode = it }
                ToggleRow("Use MCP", useMcp) { useMcp = it }
                ToggleRow("Extended Planning", extendedPlanning) { extendedPlanning = it }
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    if (description.isNotBlank()) {
                        onCreate(
                            CreateTaskRequest(
                                description = description,
                                projectPath = selectedProject?.path ?: "",
                                model = selectedModel,
                                priority = selectedPriority,
                                isFeatureMode = isFeatureMode,
                                useMcp = useMcp,
                                extendedPlanning = extendedPlanning
                            )
                        )
                    }
                },
                enabled = description.isNotBlank(),
                colors = ButtonDefaults.buttonColors(containerColor = SpriteAccent)
            ) {
                Text("Create")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Cancel", color = SpriteTextMuted)
            }
        }
    )
}

@Composable
private fun ToggleRow(label: String, checked: Boolean, onCheckedChange: (Boolean) -> Unit) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(label, fontSize = 13.sp, color = SpriteTextPrimary)
        Switch(
            checked = checked,
            onCheckedChange = onCheckedChange,
            colors = SwitchDefaults.colors(
                checkedThumbColor = SpriteAccent,
                checkedTrackColor = SpriteAccent.copy(alpha = 0.3f)
            )
        )
    }
}
