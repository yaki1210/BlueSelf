package com.example

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.slideInHorizontally
import androidx.compose.animation.slideOutHorizontally
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.material3.Surface
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.example.data.settings.AppThemeMode
import com.example.ui.screens.AddDeviceScreen
import com.example.ui.screens.HomeScreen
import com.example.ui.screens.InboxScreen
import com.example.ui.screens.MessageDetailScreen
import com.example.ui.screens.SettingsScreen
import com.example.ui.theme.MyApplicationTheme
import com.example.ui.viewmodel.MainViewModel
import java.util.Locale

sealed interface Screen {
    data object Home : Screen
    data object AddDevice : Screen
    data object Inbox : Screen
    data object Settings : Screen
    data class MessageDetail(val messageId: String) : Screen
}

class MainActivity : ComponentActivity() {
    private val viewModel: MainViewModel by viewModels()

    /** A3：通知权限单独申请（Android 13+）。拒绝不阻塞蓝牙流程，A4 发通知时静默跳过。 */
    private val notificationPermissionLauncher =
        registerForActivityResult(ActivityResultContracts.RequestPermission()) { }

    private fun requestNotificationPermissionIfNeeded() {
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermissionLauncher.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    /** A5：App 内开关打开但系统级通知被关闭时，引导跳系统通知设置。 */
    fun openSystemNotificationSettings() {
        startActivity(
            Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).apply {
                putExtra(Settings.EXTRA_APP_PACKAGE, packageName)
            }
        )
    }

    /**
     * Applies the user-selected in-app language to this Activity's base context
     * so resources resolve in the chosen locale.
     */
    override fun attachBaseContext(newBase: Context) {
        val tag = newBase.getSharedPreferences("selftrans_settings", Context.MODE_PRIVATE)
            .getString("language", "zh") ?: "zh"
        val locale = Locale(tag)
        Locale.setDefault(locale)
        val config = Configuration(newBase.resources.configuration)
        config.setLocale(locale)
        super.attachBaseContext(newBase.createConfigurationContext(config))
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // A4：确保通知渠道存在（幂等，仅创建一次）。
        com.example.notifications.MessageNotifier.ensureChannel(this)
        requestNotificationPermissionIfNeeded()

        // A4：前台标志 —— App 在前台时不发状态栏通知（走 Snackbar），退后台才发。
        lifecycle.addObserver(androidx.lifecycle.LifecycleEventObserver { _, event ->
            when (event) {
                androidx.lifecycle.Lifecycle.Event.ON_RESUME -> viewModel.setForeground(true)
                androidx.lifecycle.Lifecycle.Event.ON_PAUSE -> viewModel.setForeground(false)
                else -> Unit
            }
        })

        setContent {
            val themeMode by viewModel.themeMode.collectAsStateWithLifecycle()
            val darkTheme = when (themeMode) {
                AppThemeMode.SYSTEM -> isSystemInDarkTheme()
                AppThemeMode.LIGHT -> false
                AppThemeMode.DARK -> true
            }
            MyApplicationTheme(darkTheme = darkTheme) {
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
            Screen.Settings -> Screen.Home
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
                    onNavigateToAddDevice = { currentScreen = Screen.AddDevice },
                    onNavigateToSettings = { currentScreen = Screen.Settings }
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
            Screen.Settings -> {
                val activity = LocalContext.current
                SettingsScreen(
                    viewModel = viewModel,
                    onNavigateBack = { currentScreen = Screen.Home },
                    onLanguageChanged = {
                        if (activity is ComponentActivity) {
                            activity.recreate()
                        }
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