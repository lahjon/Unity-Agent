package com.spritely.remote.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
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
import com.spritely.remote.data.model.ProjectDto
import com.spritely.remote.ui.theme.*

@Composable
fun ProjectSelectionScreen(
    projects: List<ProjectDto>,
    isLoading: Boolean,
    onSelectProject: (ProjectDto) -> Unit,
    onSkip: () -> Unit
) {
    Column(
        modifier = Modifier
            .fillMaxSize()
            .background(SpriteBg)
            .padding(top = 48.dp)
    ) {
        // Header
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 24.dp, vertical = 16.dp)
        ) {
            Text(
                text = "Select Project",
                fontSize = 28.sp,
                fontWeight = FontWeight.Bold,
                color = SpriteTextPrimary
            )
            Spacer(Modifier.height(4.dp))
            Text(
                text = "Choose a project to work with",
                fontSize = 14.sp,
                color = SpriteTextMuted
            )
        }

        if (isLoading) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                contentAlignment = Alignment.Center
            ) {
                CircularProgressIndicator(color = SpriteAccent)
            }
        } else if (projects.isEmpty()) {
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                contentAlignment = Alignment.Center
            ) {
                Column(horizontalAlignment = Alignment.CenterHorizontally) {
                    Text(
                        "No projects found",
                        fontSize = 16.sp,
                        color = SpriteTextDisabled
                    )
                    Spacer(Modifier.height(8.dp))
                    Text(
                        "Add projects in Spritely Desktop first",
                        fontSize = 13.sp,
                        color = SpriteTextDisabled
                    )
                }
            }
        } else {
            LazyColumn(
                modifier = Modifier
                    .fillMaxWidth()
                    .weight(1f),
                contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                items(projects, key = { it.path }) { project ->
                    ProjectCard(
                        project = project,
                        onClick = { onSelectProject(project) }
                    )
                }
            }
        }

        // Skip button
        TextButton(
            onClick = onSkip,
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 24.dp, vertical = 16.dp)
                .padding(bottom = 16.dp)
        ) {
            Text(
                "Continue without selecting",
                fontSize = 14.sp,
                color = SpriteTextMuted
            )
        }
    }
}

@Composable
private fun ProjectCard(
    project: ProjectDto,
    onClick: () -> Unit
) {
    val accentColor = parseProjectColor(project.color)

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick),
        colors = CardDefaults.cardColors(containerColor = SpriteBgElevated),
        shape = RoundedCornerShape(16.dp)
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(20.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            // Color accent dot
            Box(
                modifier = Modifier
                    .size(12.dp)
                    .clip(CircleShape)
                    .background(accentColor)
            )

            Spacer(Modifier.width(16.dp))

            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = project.name,
                    fontSize = 16.sp,
                    fontWeight = FontWeight.SemiBold,
                    color = SpriteTextPrimary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )

                if (project.shortDescription.isNotBlank()) {
                    Spacer(Modifier.height(4.dp))
                    Text(
                        text = project.shortDescription,
                        fontSize = 13.sp,
                        color = SpriteTextMuted,
                        maxLines = 2,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
        }
    }
}

private fun parseProjectColor(color: String): Color {
    if (color.isBlank()) return SpriteAccent
    return try {
        val hex = color.removePrefix("#")
        val colorLong = hex.toLong(16)
        when (hex.length) {
            6 -> Color(0xFF000000 or colorLong)
            8 -> Color(colorLong)
            else -> SpriteAccent
        }
    } catch (_: Exception) {
        SpriteAccent
    }
}
