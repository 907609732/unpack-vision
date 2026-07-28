package com.unpackvision.mobile

import android.content.Context

class AppPreferences(context: Context) {
    private val preferences = context.applicationContext.getSharedPreferences(
        PREFERENCES_NAME,
        Context.MODE_PRIVATE
    )

    init {
        migrateMainCameraDefault()
    }

    var mainCameraOnly: Boolean
        get() = preferences.getBoolean(KEY_MAIN_CAMERA_ONLY, false)
        set(value) {
            preferences.edit()
                .putBoolean(KEY_MAIN_CAMERA_ONLY, value)
                .putBoolean(KEY_MAIN_CAMERA_ONLY_USER_SET, true)
                .apply()
        }

    private fun migrateMainCameraDefault() {
        if (preferences.getBoolean(KEY_MAIN_CAMERA_ONLY_DEFAULT_MIGRATED, false)) return
        val userSet = preferences.getBoolean(KEY_MAIN_CAMERA_ONLY_USER_SET, false)
        val current = if (preferences.contains(KEY_MAIN_CAMERA_ONLY)) {
            preferences.getBoolean(KEY_MAIN_CAMERA_ONLY, AppPreferenceMigration.DEFAULT_MAIN_CAMERA_ONLY)
        } else {
            null
        }
        preferences.edit().apply {
            putBoolean(KEY_MAIN_CAMERA_ONLY, AppPreferenceMigration.migrateMainCameraOnly(current, userSet))
            putBoolean(KEY_MAIN_CAMERA_ONLY_DEFAULT_MIGRATED, true)
        }.apply()
    }

    private companion object {
        const val PREFERENCES_NAME = "unpack_vision_preferences"
        const val KEY_MAIN_CAMERA_ONLY = "main_camera_only"
        const val KEY_MAIN_CAMERA_ONLY_USER_SET = "main_camera_only_user_set"
        const val KEY_MAIN_CAMERA_ONLY_DEFAULT_MIGRATED = "main_camera_only_default_migrated"
    }
}

internal object AppPreferenceMigration {
    const val DEFAULT_MAIN_CAMERA_ONLY = false

    fun migrateMainCameraOnly(currentValue: Boolean?, userSet: Boolean): Boolean =
        if (userSet) currentValue ?: DEFAULT_MAIN_CAMERA_ONLY else DEFAULT_MAIN_CAMERA_ONLY
}
