package com.spritely.remote.data.api

import okhttp3.OkHttpClient
import okhttp3.logging.HttpLoggingInterceptor
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import java.util.concurrent.TimeUnit

object ApiClient {

    private var currentBaseUrl: String = ""
    private var api: SpritelyApi? = null

    fun getApi(host: String, port: Int): SpritelyApi {
        val baseUrl = "http://$host:$port/"

        if (baseUrl != currentBaseUrl || api == null) {
            currentBaseUrl = baseUrl

            val logging = HttpLoggingInterceptor().apply {
                level = HttpLoggingInterceptor.Level.BASIC
            }

            val client = OkHttpClient.Builder()
                .connectTimeout(5, TimeUnit.SECONDS)
                .readTimeout(10, TimeUnit.SECONDS)
                .writeTimeout(10, TimeUnit.SECONDS)
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
    }
}
