package com.spritely.remote.ui.screens

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.spritely.remote.ui.theme.*
import com.spritely.remote.viewmodel.ConnectionState

@Composable
fun ConnectScreen(
    state: ConnectionState,
    onHostChange: (String) -> Unit,
    onPortChange: (String) -> Unit,
    onApiKeyChange: (String) -> Unit,
    onConnect: () -> Unit
) {
    var showApiKey by remember { mutableStateOf(false) }

    Column(
        modifier = Modifier
            .fillMaxSize()
            .padding(32.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center
    ) {
        // Logo / Title
        Text(
            text = "Spritely",
            fontSize = 36.sp,
            fontWeight = FontWeight.Bold,
            color = SpriteAccentBright
        )
        Text(
            text = "Remote",
            fontSize = 18.sp,
            color = SpriteTextMuted,
            modifier = Modifier.padding(bottom = 48.dp)
        )

        // Server address
        Text(
            text = "DESKTOP SERVER",
            fontSize = 11.sp,
            color = SpriteAccent,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 12.dp)
        )

        OutlinedTextField(
            value = state.host,
            onValueChange = onHostChange,
            label = { Text("IP Address") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Uri),
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 12.dp),
            colors = OutlinedTextFieldDefaults.colors(
                focusedBorderColor = SpriteAccent,
                unfocusedBorderColor = SpriteTextDisabled,
                cursorColor = SpriteAccent
            )
        )

        Row(
            modifier = Modifier.fillMaxWidth().padding(bottom = 12.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp)
        ) {
            OutlinedTextField(
                value = state.port,
                onValueChange = { if (it.all { c -> c.isDigit() } && it.length <= 5) onPortChange(it) },
                label = { Text("Port") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                modifier = Modifier.weight(1f),
                colors = OutlinedTextFieldDefaults.colors(
                    focusedBorderColor = SpriteAccent,
                    unfocusedBorderColor = SpriteTextDisabled,
                    cursorColor = SpriteAccent
                )
            )
        }

        // API Key
        Text(
            text = "AUTHENTICATION",
            fontSize = 11.sp,
            color = SpriteAccent,
            fontWeight = FontWeight.SemiBold,
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 8.dp)
        )

        OutlinedTextField(
            value = state.apiKey,
            onValueChange = onApiKeyChange,
            label = { Text("API Key") },
            singleLine = true,
            visualTransformation = if (showApiKey) VisualTransformation.None else PasswordVisualTransformation(),
            trailingIcon = {
                TextButton(onClick = { showApiKey = !showApiKey }) {
                    Text(
                        if (showApiKey) "Hide" else "Show",
                        fontSize = 11.sp,
                        color = SpriteAccent
                    )
                }
            },
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 4.dp),
            colors = OutlinedTextFieldDefaults.colors(
                focusedBorderColor = SpriteAccent,
                unfocusedBorderColor = SpriteTextDisabled,
                cursorColor = SpriteAccent
            )
        )

        Text(
            text = "Copy from Spritely Desktop > Remote Server > API Key",
            fontSize = 10.sp,
            color = SpriteTextDisabled,
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 24.dp)
        )

        // Error message
        if (state.error != null) {
            Card(
                colors = CardDefaults.cardColors(containerColor = SpriteDanger.copy(alpha = 0.15f)),
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(bottom = 16.dp)
            ) {
                Text(
                    text = state.error,
                    color = SpriteDanger,
                    fontSize = 12.sp,
                    modifier = Modifier.padding(12.dp)
                )
            }
        }

        // Connect button
        Button(
            onClick = onConnect,
            enabled = !state.isConnecting && state.host.isNotBlank() && state.port.isNotBlank(),
            modifier = Modifier
                .fillMaxWidth()
                .height(52.dp),
            colors = ButtonDefaults.buttonColors(containerColor = SpriteAccent)
        ) {
            if (state.isConnecting) {
                CircularProgressIndicator(
                    modifier = Modifier.size(20.dp),
                    color = MaterialTheme.colorScheme.onPrimary,
                    strokeWidth = 2.dp
                )
                Spacer(Modifier.width(8.dp))
            }
            Text(if (state.isConnecting) "Connecting..." else "Connect")
        }

        Spacer(Modifier.height(16.dp))

        Text(
            text = "Enable 'Remote Server' in Spritely Desktop settings,\nthen enter your PC's local IP address above.",
            fontSize = 11.sp,
            color = SpriteTextDisabled,
            lineHeight = 16.sp
        )
    }
}
