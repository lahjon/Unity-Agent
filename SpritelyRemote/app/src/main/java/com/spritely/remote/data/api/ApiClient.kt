package com.spritely.remote.data.api

import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.logging.HttpLoggingInterceptor
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import java.util.concurrent.TimeUnit

object ApiClient {

    private var currentBaseUrl: String = ""
    private var currentApiKey: String = ""
    private var api: SpritelyApi? = null

    fun getApi(host: String, port: Int, apiKey: String = ""): SpritelyApi {
        val baseUrl = "http://$host:$port/"

        if (baseUrl != currentBaseUrl || apiKey != currentApiKey || api == null) {
            currentBaseUrl = baseUrl
            currentApiKey = apiKey

            val logging = HttpLoggingInterceptor().apply {
                level = HttpLoggingInterceptor.Level.BASIC
            }

            val authInterceptor = Interceptor { chain ->
                val request = if (apiKey.isNotBlank()) {
                    chain.request().newBuilder()
                        .addHeader("X-Api-Key", apiKey)
                        .build()
                } else {
                    chain.request()
                }
                chain.proceed(request)
            }

            val client = OkHttpClient.Builder()
                .connectTimeout(5, TimeUnit.SECONDS)
                .readTimeout(10, TimeUnit.SECONDS)
                .writeTimeout(10, TimeUnit.SECONDS)
                .addInterceptor(authInterceptor)
                .addInterceptor(logging)
                .build()

            val retrofit = Retrofit.Builder()
                .baseUrl(baseUrl)
                .client(client)
                .addConverterFactory(GsonConverterFactory.create())
                .build()

            api = retrofit.create(SpritelyApi::class.java)
        }

        return api!!
    }

    fun clearClient() {
        api = null
        currentBaseUrl = ""
        currentApiKey = ""
    }
}
