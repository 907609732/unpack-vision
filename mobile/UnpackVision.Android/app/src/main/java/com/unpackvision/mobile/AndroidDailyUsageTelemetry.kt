package com.unpackvision.mobile

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.security.KeyStore
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.TimeZone
import javax.crypto.KeyGenerator
import javax.crypto.Mac
import javax.crypto.SecretKey

class AndroidDailyUsageTelemetry(private val context: Context) {
    private val state = context.applicationContext.getSharedPreferences(STATE_PREFERENCES, Context.MODE_PRIVATE)

    fun trackIfEnabled(preferences: AppPreferences) {
        if (!preferences.optionalUsageTelemetryEnabled) {
            deleteIdentity()
            return
        }
        val endpoint = BuildConfig.TELEMETRY_ENDPOINT.trim()
        if (!endpoint.startsWith("https://")) return
        val day = beijingDay()
        if (state.getString(KEY_LAST_SENT_DAY, null) == day) return
        val dailyId = hmacHex("dau:v1|$day|android")
        val body = JSONObject()
            .put("day", day)
            .put("dailyId", dailyId)
            .put("platform", "android")
            .put("appVersion", BuildConfig.VERSION_NAME)
            .put("channel", "stable")
            .toString()
            .toByteArray(Charsets.UTF_8)
        if (body.size > 1024) return

        runCatching {
            val connection = (URL(endpoint).openConnection() as HttpURLConnection).apply {
                requestMethod = "POST"
                connectTimeout = 3_000
                readTimeout = 3_000
                doOutput = true
                instanceFollowRedirects = false
                setRequestProperty("Content-Type", "application/json")
                setFixedLengthStreamingMode(body.size)
            }
            connection.outputStream.use { it.write(body) }
            val succeeded = connection.responseCode == HttpURLConnection.HTTP_NO_CONTENT
            connection.disconnect()
            if (succeeded) {
                state.edit().putString(KEY_LAST_SENT_DAY, day).apply()
            }
        }
    }

    fun deleteIdentity() {
        state.edit().clear().apply()
        runCatching {
            val keyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }
            if (keyStore.containsAlias(KEY_ALIAS)) keyStore.deleteEntry(KEY_ALIAS)
        }
    }

    private fun hmacHex(value: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(getOrCreateKey())
        return mac.doFinal(value.toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02X".format(it) }
    }

    private fun getOrCreateKey(): SecretKey {
        val keyStore = KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }
        (keyStore.getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return KeyGenerator.getInstance(
            KeyProperties.KEY_ALGORITHM_HMAC_SHA256,
            ANDROID_KEY_STORE
        ).run {
            init(
                KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_SIGN)
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .build()
            )
            generateKey()
        }
    }

    private fun beijingDay(): String = SimpleDateFormat("yyyy-MM-dd", Locale.ROOT).run {
        timeZone = TimeZone.getTimeZone("GMT+08:00")
        format(Date())
    }

    private companion object {
        const val ANDROID_KEY_STORE = "AndroidKeyStore"
        const val KEY_ALIAS = "unpackvision-anonymous-dau-v1"
        const val STATE_PREFERENCES = "anonymous-dau-state"
        const val KEY_LAST_SENT_DAY = "last-sent-day"
    }
}
