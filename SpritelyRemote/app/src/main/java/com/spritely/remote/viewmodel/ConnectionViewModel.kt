package com.spritely.remote.viewmodel

import android.content.Context
import androidx.datastore.core.DataStore
import androidx.datastore.preferences.core.*
import androidx.datastore.preferences.preferencesDataStore
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.spritely.remote.data.api.ApiClient
import com.spritely.remote.data.model.ServerStatus
import kotlinx.coroutines.flow.*
import kotlinx.coroutines.launch

private val Context.dataStore: DataStore<Preferences> by preferencesDataStore(name = "spritely_settings")

data class ConnectionState(
    val host: String = "192.168.1.100",
    val port: String = "7923",
    val isConnected: Boolean = false,
    val isConnecting: Boolean = false,
    val serverStatus: ServerStatus? = null,
    val error: String? = null
)

class ConnectionViewModel : ViewModel() {

    private val _state = MutableStateFlow(ConnectionState())
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private var appContext: Context? = null

    companion object {
        private val KEY_HOST = stringPreferencesKey("server_host")
        private val KEY_PORT = stringPreferencesKey("server_port")
    }

    fun initialize(context: Context) {
        appContext = context.applicationContext
        viewModelScope.launch {
            context.applicationContext.dataStore.data.first().let { prefs ->
                _state.update {
                    it.copy(
                        host = prefs[KEY_HOST] ?: it.host,
                        port = prefs[KEY_PORT] ?: it.port
                    )
                }
            }
        }
    }

    fun updateHost(host: String) {
        _state.update { it.copy(host = host) }
    }

    fun updatePort(port: String) {
        _state.update { it.copy(port = port) }
    }

    fun connect() {
        val s = _state.value
        val port = s.port.toIntOrNull() ?: return

        _state.update { it.copy(isConnecting = true, error = null) }

        viewModelScope.launch {
            try {
                val api = ApiClient.getApi(s.host, port)
                val response = api.getStatus()

                if (response.isSuccessful && response.body()?.success == true) {
                    _state.update {
                        it.copy(
                            isConnected = true,
                            isConnecting = false,
                            serverStatus = response.body()?.data,
                            error = null
                        )
                    }
                    saveSettings()
                } else {
                    _state.update {
                        it.copy(
                            isConnected = false,
                            isConnecting = false,
                            error = "Server returned error: ${response.body()?.error ?: response.code()}"
                        )
                    }
                }
            } catch (e: Exception) {
                _state.update {
                    it.copy(
                        isConnected = false,
                        isConnecting = false,
                        error = "Connection failed: ${e.message}"
                    )
                }
            }
        }
    }

    fun disconnect() {
        ApiClient.clearClient()
        _state.update {
            it.copy(isConnected = false, serverStatus = null, error = null)
        }
    }

    private suspend fun saveSettings() {
        appContext?.dataStore?.edit { prefs ->
            prefs[KEY_HOST] = _state.value.host
            prefs[KEY_PORT] = _state.value.port
        }
    }
}
