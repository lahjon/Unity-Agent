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
    var projectMenuExpanded by remember { mutableStateOf(false) }
    val isValid = description.trim().length >= 10

    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = SpriteBgElevated,
        title = {
            Text("New Task", fontWeight = FontWeight.Bold, color = SpriteTextPrimary)
        },
        text = {
            Column(
                modifier = Modifier.verticalScroll(rememberScrollState()),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                OutlinedTextField(
                    value = description,
                    onValueChange = { description = it },
                    label = { Text("Prompt") },
                    placeholder = { Text("Describe what you want done...", color = SpriteTextDisabled) },
                    minLines = 4,
                    maxLines = 8,
                    modifier = Modifier.fillMaxWidth(),
                    isError = description.isNotEmpty() && !isValid,
                    supportingText = if (description.isNotEmpty() && !isValid) {
                        { Text("Minimum 10 characters", color = MaterialTheme.colorScheme.error, fontSize = 11.sp) }
                    } else null,
                    colors = OutlinedTextFieldDefaults.colors(
                        focusedBorderColor = SpriteAccent,
                        unfocusedBorderColor = SpriteTextDisabled,
                        cursorColor = SpriteAccent
                    )
                )

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
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    if (isValid) {
                        onCreate(
                            CreateTaskRequest(
                                description = description.trim(),
                                projectPath = selectedProject?.path ?: "",
                                model = null,
                                priority = "Normal",
                                isFeatureMode = true,
                                useMcp = false,
                                autoDecompose = false,
                                extendedPlanning = false
                            )
                        )
                    }
                },
                enabled = isValid,
                colors = ButtonDefaults.buttonColors(containerColor = SpriteAccent)
            ) {
                Text("Submit")
            }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) {
                Text("Cancel", color = SpriteTextMuted)
            }
        }
    )
}
