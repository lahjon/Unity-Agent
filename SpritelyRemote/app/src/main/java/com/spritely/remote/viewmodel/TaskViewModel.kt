package com.spritely.remote.viewmodel

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.spritely.remote.data.api.ApiClient
import com.spritely.remote.data.model.*
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch

data class TaskListState(
    val activeTasks: List<TaskDto> = emptyList(),
    val historyTasks: List<TaskDto> = emptyList(),
    val projects: List<ProjectDto> = emptyList(),
    val isLoading: Boolean = false,
    val error: String? = null,
    val selectedTask: TaskDto? = null,
    val showCreateDialog: Boolean = false
)

class TaskViewModel : ViewModel() {

    private val _state = MutableStateFlow(TaskListState())
    val state: StateFlow<TaskListState> = _state.asStateFlow()

    private var pollJob: Job? = null
    private var host: String = ""
    private var port: Int = 7923

    fun setConnection(host: String, port: Int) {
        this.host = host
        this.port = port
    }

    fun startPolling() {
        pollJob?.cancel()
        pollJob = viewModelScope.launch {
            while (true) {
                refresh()
                delay(3000) // Poll every 3 seconds
            }
        }
    }

    fun stopPolling() {
        pollJob?.cancel()
        pollJob = null
    }

    fun refresh() {
        viewModelScope.launch {
            try {
                val api = ApiClient.getApi(host, port)

                val activeResp = api.getTasks("active")
                val historyResp = api.getTasks("history")
                val projectsResp = api.getProjects()

                _state.update {
                    it.copy(
                        activeTasks = activeResp.body()?.data ?: it.activeTasks,
                        historyTasks = historyResp.body()?.data ?: it.historyTasks,
                        projects = projectsResp.body()?.data ?: it.projects,
                        isLoading = false,
                        error = null
                    )
                }
            } catch (e: Exception) {
                _state.update { it.copy(isLoading = false, error = e.message) }
            }
        }
    }

    fun loadTaskDetail(taskId: String) {
        viewModelScope.launch {
            try {
                val api = ApiClient.getApi(host, port)
                val resp = api.getTask(taskId)
                if (resp.isSuccessful) {
                    _state.update { it.copy(selectedTask = resp.body()?.data) }
                }
            } catch (e: Exception) {
                _state.update { it.copy(error = "Failed to load task: ${e.message}") }
            }
        }
    }

    fun createTask(request: CreateTaskRequest) {
        viewModelScope.launch {
            try {
                val api = ApiClient.getApi(host, port)
                api.createTask(request)
                _state.update { it.copy(showCreateDialog = false) }
                refresh()
            } catch (e: Exception) {
                _state.update { it.copy(error = "Failed to create task: ${e.message}") }
            }
        }
    }

    fun cancelTask(taskId: String) {
        viewModelScope.launch {
            try {
                ApiClient.getApi(host, port).cancelTask(taskId)
                delay(500)
                refresh()
            } catch (_: Exception) {}
        }
    }

    fun pauseTask(taskId: String) {
        viewModelScope.launch {
            try {
                ApiClient.getApi(host, port).pauseTask(taskId)
                delay(500)
                refresh()
            } catch (_: Exception) {}
        }
    }

    fun resumeTask(taskId: String) {
        viewModelScope.launch {
            try {
                ApiClient.getApi(host, port).resumeTask(taskId)
                delay(500)
                refresh()
            } catch (_: Exception) {}
        }
    }

    fun showCreateDialog() {
        _state.update { it.copy(showCreateDialog = true) }
    }

    fun hideCreateDialog() {
        _state.update { it.copy(showCreateDialog = false) }
    }

    fun clearSelectedTask() {
        _state.update { it.copy(selectedTask = null) }
    }

    fun clearError() {
        _state.update { it.copy(error = null) }
    }
}
