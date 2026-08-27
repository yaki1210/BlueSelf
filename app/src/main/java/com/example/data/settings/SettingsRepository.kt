package com.example.data.settings

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** 应用支持的语言。 */
enum class AppLanguage(val tag: String) {
    ZH("zh"),
    EN("en");

    companion object {
        fun fromTag(tag: String?): AppLanguage =
            entries.firstOrNull { it.tag == tag } ?: ZH
    }
}

/** 主题模式。SYSTEM 跟随系统。 */
enum class AppThemeMode {
    SYSTEM,
    LIGHT,
    DARK
}

/**
 * 轻量设置仓库，基于 SharedPreferences 持久化语言与主题偏好，
 * 并用 StateFlow 暴露给 ViewModel 观察。
 */
class SettingsRepository(context: Context) {
    private val prefs = context.applicationContext
        .getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private val _language = MutableStateFlow(
        AppLanguage.fromTag(prefs.getString(KEY_LANGUAGE, null))
    )
    val language: StateFlow<AppLanguage> = _language.asStateFlow()

    private val _themeMode = MutableStateFlow(
        AppThemeMode.valueOf(
            prefs.getString(KEY_THEME, AppThemeMode.SYSTEM.name) ?: AppThemeMode.SYSTEM.name
        )
    )
    val themeMode: StateFlow<AppThemeMode> = _themeMode.asStateFlow()

    /** A5：新消息通知开关（默认开启）。应用层闸门，与系统通知权限双重控制。 */
    private val _notificationsEnabled = MutableStateFlow(prefs.getBoolean(KEY_NOTIFICATIONS, true))
    val notificationsEnabled: StateFlow<Boolean> = _notificationsEnabled.asStateFlow()

    fun setLanguage(language: AppLanguage) {
        prefs.edit().putString(KEY_LANGUAGE, language.tag).apply()
        _language.value = language
    }

    fun setThemeMode(mode: AppThemeMode) {
        prefs.edit().putString(KEY_THEME, mode.name).apply()
        _themeMode.value = mode
    }

    fun setNotificationsEnabled(enabled: Boolean) {
        prefs.edit().putBoolean(KEY_NOTIFICATIONS, enabled).apply()
        _notificationsEnabled.value = enabled
    }

    companion object {
        private const val PREFS_NAME = "selftrans_settings"
        private const val KEY_LANGUAGE = "language"
        private const val KEY_THEME = "theme_mode"
        private const val KEY_NOTIFICATIONS = "notifications_enabled"
    }
}