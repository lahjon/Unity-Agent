package com.spritely.remote.data.api

import com.spritely.remote.data.model.*
import retrofit2.Response
import retrofit2.http.*

interface SpritelyApi {

    @GET("/api/status")
    suspend fun getStatus(): Response<ApiResponse<ServerStatus>>

    @GET("/api/projects")
    suspend fun getProjects(): Response<ApiResponse<List<ProjectDto>>>

    @GET("/api/tasks")
    suspend fun getTasks(@Query("filter") filter: String = "active"): Response<ApiResponse<List<TaskDto>>>

    @GET("/api/tasks/{id}")
    suspend fun getTask(@Path("id") id: String): Response<ApiResponse<TaskDto>>

    @POST("/api/tasks")
    suspend fun createTask(@Body request: CreateTaskRequest): Response<ApiResponse<TaskDto>>

    @POST("/api/tasks/{id}/cancel")
    suspend fun cancelTask(@Path("id") id: String): Response<ApiResponse<Any>>

    @POST("/api/tasks/{id}/pause")
    suspend fun pauseTask(@Path("id") id: String): Response<ApiResponse<Any>>

    @POST("/api/tasks/{id}/resume")
    suspend fun resumeTask(@Path("id") id: String): Response<ApiResponse<Any>>
}
