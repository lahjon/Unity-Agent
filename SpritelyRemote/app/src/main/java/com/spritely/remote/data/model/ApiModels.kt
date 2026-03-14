package com.spritely.remote.data.model

import com.google.gson.annotations.SerializedName

data class ApiResponse<T>(
    @SerializedName("success") val success: Boolean = true,
    @SerializedName("data") val data: T? = null,
    @SerializedName("error") val error: String? = null
)

data class ServerStatus(
    @SerializedName("name") val name: String = "",
    @SerializedName("version") val version: String = "",
    @SerializedName("activeTasks") val activeTasks: Int = 0,
    @SerializedName("maxConcurrentTasks") val maxConcurrentTasks: Int = 0
)

data class AppSettings(
    @SerializedName("autoCommit") val autoCommit: Boolean = false,
    @SerializedName("autoQueue") val autoQueue: Boolean = false,
    @SerializedName("autoVerify") val autoVerify: Boolean = false,
    @SerializedName("maxConcurrentTasks") val maxConcurrentTasks: Int = 10,
    @SerializedName("taskTimeoutMinutes") val taskTimeoutMinutes: Int = 120,
    @SerializedName("opusEffortLevel") val opusEffortLevel: String = "high"
)

data class ProjectDto(
    @SerializedName("name") val name: String = "",
    @SerializedName("path") val path: String = "",
    @SerializedName("shortDescription") val shortDescription: String = "",
    @SerializedName("color") val color: String = "",
    @SerializedName("isGame") val isGame: Boolean = false
)

data class TaskDto(
    @SerializedName("id") val id: String = "",
    @SerializedName("taskNumber") val taskNumber: Int = 0,
    @SerializedName("description") val description: String = "",
    @SerializedName("status") val status: String = "",
    @SerializedName("statusText") val statusText: String = "",
    @SerializedName("projectPath") val projectPath: String = "",
    @SerializedName("projectName") val projectName: String = "",
    @SerializedName("model") val model: String = "",
    @SerializedName("priority") val priority: String = "",
    @SerializedName("isFeatureMode") val isFeatureMode: Boolean = false,
    @SerializedName("startTime") val startTime: String = "",
    @SerializedName("endTime") val endTime: String? = null,
    @SerializedName("currentIteration") val currentIteration: Int = 0,
    @SerializedName("maxIterations") val maxIterations: Int = 0,
    @SerializedName("isVerified") val isVerified: Boolean = false,
    @SerializedName("isCommitted") val isCommitted: Boolean = false,
    @SerializedName("changedFiles") val changedFiles: List<String> = emptyList(),
    @SerializedName("output") val output: String? = null
) {
    val isRunning get() = status in listOf("Running", "Planning", "Verifying", "Committing")
    val isFinished get() = status in listOf("Completed", "Cancelled", "Failed")
    val isPaused get() = status == "Paused"
    val isQueued get() = status in listOf("Queued", "InitQueued")
}

data class CreateTaskRequest(
    @SerializedName("description") val description: String,
    @SerializedName("projectPath") val projectPath: String = "",
    @SerializedName("model") val model: String? = null,
    @SerializedName("priority") val priority: String = "Normal",
    @SerializedName("isFeatureMode") val isFeatureMode: Boolean = true,
    @SerializedName("useMcp") val useMcp: Boolean = false,
    @SerializedName("autoDecompose") val autoDecompose: Boolean = false,
    @SerializedName("extendedPlanning") val extendedPlanning: Boolean = false,
    @SerializedName("autoQueue") val autoQueue: Boolean? = null
)
