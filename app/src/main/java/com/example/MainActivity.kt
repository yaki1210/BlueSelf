package com.example

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.viewModels
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import com.example.ui.screens.AddDeviceScreen
import com.example.ui.screens.HomeScreen
import com.example.ui.screens.InboxScreen
import com.example.ui.screens.MessageDetailScreen
import com.example.ui.theme.MyApplicationTheme
import com.example.ui.viewmodel.MainViewModel

sealed interface Screen {
    data object Home : Screen
    data object AddDevice : Screen
    data object Inbox : Screen
    data class MessageDetail(val messageId: String) : Screen
}

class MainActivity : ComponentActivity() {
    private val viewModel: MainViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        setContent {
            MyApplicationTheme {
                Surface(modifier = Modifier.fillMaxSize()) {
                    AppNavigation(viewModel = viewModel)
                }
            }
        }
    }
}

@Composable
fun AppNavigation(viewModel: MainViewModel) {
    var currentScreen by remember { mutableStateOf<Screen>(Screen.Home) }

    BackHandler(enabled = currentScreen != Screen.Home) {
        currentScreen = when (currentScreen) {
            is Screen.MessageDetail -> Screen.Inbox
            Screen.Inbox -> Screen.Home
            Screen.AddDevice -> Screen.Home
            Screen.Home -> Screen.Home
        }
    }

    AnimatedContent(
        targetState = currentScreen,
        transitionSpec = {
            if (targetState == Screen.Home) {
                (slideInHorizontally { -it } + fadeIn()).togetherWith(slideOutHorizontally { it } + fadeOut())
            } else {
                (slideInHorizontally { it } + fadeIn()).togetherWith(slideOutHorizontally { -it } + fadeOut())
            }
        },
        label = "screen_transition"
    ) { target ->
        when (target) {
            Screen.Home -> {
                HomeScreen(
                    viewModel = viewModel,
                    onNavigateToInbox = { currentScreen = Screen.Inbox },
                    onNavigateToAddDevice = { currentScreen = Screen.AddDevice }
                )
            }
            Screen.AddDevice -> {
                AddDeviceScreen(
                    viewModel = viewModel,
                    onNavigateBack = { currentScreen = Screen.Home }
                )
            }
            Screen.Inbox -> {
                InboxScreen(
                    viewModel = viewModel,
                    onNavigateBack = { currentScreen = Screen.Home },
                    onOpenMessageDetail = { msg ->
                        currentScreen = Screen.MessageDetail(msg.id)
                    }
                )
            }
            is Screen.MessageDetail -> {
                MessageDetailScreen(
                    messageId = target.messageId,
                    viewModel = viewModel,
                    onNavigateBack = { currentScreen = Screen.Inbox },
                    onUseAsInput = { content ->
                        viewModel.onTextInputChange(content)
                        currentScreen = Screen.Home
                    }
                )
            }
        }
    }
}
