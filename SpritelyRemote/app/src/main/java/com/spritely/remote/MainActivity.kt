package com.spritely.remote

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.*
import androidx.lifecycle.viewmodel.compose.viewModel
import com.spritely.remote.ui.screens.*
import com.spritely.remote.ui.theme.SpritelyRemoteTheme
import com.spritely.remote.viewmodel.ConnectionViewModel
import com.spritely.remote.viewmodel.TaskViewModel

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        setContent {
            SpritelyRemoteTheme {
                SpritelyApp(applicationContext = this)
            }
        }
    }
}

@Composable
fun SpritelyApp(
    applicationContext: android.content.Context,
    connectionVM: ConnectionViewModel = viewModel(),
    taskVM: TaskViewModel = viewModel()
) {
    val connState by connectionVM.state.collectAsState()
    val taskState by taskVM.state.collectAsState()

    // Initialize connection VM with context for DataStore
    LaunchedEffect(Unit) {
        connectionVM.initialize(applicationContext)
    }

    // When connected, start polling tasks
    LaunchedEffect(connState.isConnected) {
        if (connState.isConnected) {
            val port = connState.port.toIntOrNull() ?: 7923
            taskVM.setConnection(connState.host, port, connState.apiKey)
            // Sync auto-queue from desktop settings if available, default to true
            val desktopAutoQueue = connState.appSettings?.autoQueue ?: true
            taskVM.setAutoQueue(desktopAutoQueue)
            taskVM.startPolling()
        } else {
            taskVM.stopPolling()
        }
    }

    // Track whether user has passed through project selection
    var hasSelectedProject by remember { mutableStateOf(false) }
    var showSettings by remember { mutableStateOf(false) }

    // Reset project selection when disconnected
    LaunchedEffect(connState.isConnected) {
        if (!connState.isConnected) {
            hasSelectedProject = false
            showSettings = false
        }
    }

    // Navigation state
    if (!connState.isConnected) {
        // Connection screen
        ConnectScreen(
            state = connState,
            onHostChange = connectionVM::updateHost,
            onPortChange = connectionVM::updatePort,
            onApiKeyChange = connectionVM::updateApiKey,
            onConnect = connectionVM::connect
        )
    } else if (!hasSelectedProject) {
        // Project selection screen (shown once after connecting)
        ProjectSelectionScreen(
            projects = taskState.projects,
            isLoading = taskState.projects.isEmpty() && taskState.error == null,
            onSelectProject = { project ->
                taskVM.selectProject(project)
                hasSelectedProject = true
            },
            onSkip = {
                taskVM.selectProject(null)
                hasSelectedProject = true
            }
        )
    } else if (showSettings) {
        // Settings screen
        SettingsScreen(
            appSettings = connState.appSettings,
            autoQueueEnabled = taskState.autoQueueEnabled,
            onAutoQueueChange = { taskVM.setAutoQueue(it) },
            onRefresh = { connectionVM.refreshSettings() },
            onBack = { showSettings = false }
        )
    } else if (taskState.selectedTask != null) {
        // Task detail screen
        val task = taskState.selectedTask!!
        TaskDetailScreen(
            task = task,
            onBack = { taskVM.clearSelectedTask() },
            onCancel = { taskVM.cancelTask(task.id); taskVM.clearSelectedTask() },
            onPause = { taskVM.pauseTask(task.id) },
            onResume = { taskVM.resumeTask(task.id) },
            onRefresh = { taskVM.loadTaskDetail(it) }
        )
    } else {
        // Dashboard
        DashboardScreen(
            state = taskState,
            onTaskClick = { taskVM.loadTaskDetail(it.id) },
            onCreateTask = { taskVM.showCreateDialog() },
            onCancelTask = taskVM::cancelTask,
            onPauseTask = taskVM::pauseTask,
            onResumeTask = taskVM::resumeTask,
            onDisconnect = { connectionVM.disconnect() },
            onRefresh = { taskVM.refresh() },
            onSettings = { showSettings = true }
        )

        // Create task dialog
        if (taskState.showCreateDialog) {
            CreateTaskDialog(
                projects = taskState.projects,
                selectedProject = taskState.selectedProject,
                onDismiss = { taskVM.hideCreateDialog() },
                onCreate = { taskVM.createTask(it) }
            )
        }
    }
}
