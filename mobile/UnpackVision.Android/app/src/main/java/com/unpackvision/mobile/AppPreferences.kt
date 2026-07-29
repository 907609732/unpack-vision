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

    val hasCurrentConsent: Boolean
        get() = preferences.getString(KEY_TERMS_VERSION, null) == CURRENT_TERMS_VERSION &&
            preferences.getString(KEY_PRIVACY_VERSION, null) == CURRENT_PRIVACY_VERSION &&
            preferences.getLong(KEY_CONSENT_ACCEPTED_AT, 0L) > 0L

    fun acceptCurrentLegalDocuments() {
        preferences.edit()
            .putString(KEY_TERMS_VERSION, CURRENT_TERMS_VERSION)
            .putString(KEY_PRIVACY_VERSION, CURRENT_PRIVACY_VERSION)
            .putLong(KEY_CONSENT_ACCEPTED_AT, System.currentTimeMillis())
            .putBoolean(KEY_OPTIONAL_USAGE_TELEMETRY, false)
            .apply()
    }

    var optionalUsageTelemetryEnabled: Boolean
        get() = preferences.getBoolean(KEY_OPTIONAL_USAGE_TELEMETRY, false)
        set(value) {
            preferences.edit()
                .putBoolean(KEY_OPTIONAL_USAGE_TELEMETRY, value)
                .putLong(
                    KEY_TELEMETRY_WITHDRAWN_AT,
                    if (value) 0L else System.currentTimeMillis()
                )
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
        const val KEY_TERMS_VERSION = "terms_version"
        const val KEY_PRIVACY_VERSION = "privacy_version"
        const val KEY_CONSENT_ACCEPTED_AT = "consent_accepted_at"
        const val KEY_OPTIONAL_USAGE_TELEMETRY = "optional_usage_telemetry"
        const val KEY_TELEMETRY_WITHDRAWN_AT = "telemetry_withdrawn_at"
        const val CURRENT_TERMS_VERSION = "2026-07-29"
        const val CURRENT_PRIVACY_VERSION = "2026-07-29"
    }
}

internal object AppPreferenceMigration {
    const val DEFAULT_MAIN_CAMERA_ONLY = false

    fun migrateMainCameraOnly(currentValue: Boolean?, userSet: Boolean): Boolean =
        if (userSet) currentValue ?: DEFAULT_MAIN_CAMERA_ONLY else DEFAULT_MAIN_CAMERA_ONLY
}
