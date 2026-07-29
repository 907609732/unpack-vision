package com.unpackvision.mobile

import android.app.DownloadManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import androidx.core.content.FileProvider
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

data class MobileUpdateManifest(
    val versionName: String,
    val versionCode: Int,
    val apkUrl: String,
    val sha256: String,
    val notesUrl: String?,
    val releaseNotesUrl: String?,
    val minimumSupportedVersion: String?,
    val critical: Boolean,
    val publishedAt: String?
)

internal object MobileUpdateManifestParser {
    fun parse(body: String): MobileUpdateManifest {
        val json = JSONObject(body)
        val versionName = json.getString("versionName").trim()
        val versionCode = json.getInt("versionCode")
        val apkUrl = json.optString("apkUrl", AppUpdateManager.APK_URL).trim()
        val sha256 = json.getString("sha256").trim().lowercase()
        require(versionName.length in 1..40 && versionCode > 0) { "更新版本信息无效" }
        require(sha256.matches(Regex("^[0-9a-f]{64}$"))) { "更新 SHA256 无效" }
        val apkUri = URI(apkUrl)
        require(
            apkUri.scheme.equals("https", ignoreCase = true) &&
                apkUri.host.equals("github.com", ignoreCase = true)
        ) { "更新地址不是受信任的 GitHub HTTPS 地址" }
        return MobileUpdateManifest(
            versionName = versionName,
            versionCode = versionCode,
            apkUrl = apkUrl,
            sha256 = sha256,
            notesUrl = json.optString("notesUrl").takeIf { it.isNotBlank() },
            releaseNotesUrl = json.optString("releaseNotesUrl").takeIf { it.isNotBlank() },
            minimumSupportedVersion =
                json.optString("minimumSupportedVersion").takeIf { it.isNotBlank() },
            critical = json.optBoolean("critical", false),
            publishedAt = json.optString("publishedAt").takeIf { it.isNotBlank() }
        )
    }
}

sealed interface MobileUpdateResult {
    data object Skipped : MobileUpdateResult
    data object Current : MobileUpdateResult
    data class Available(val manifest: MobileUpdateManifest) : MobileUpdateResult
    data class Failed(val message: String) : MobileUpdateResult
}

class AppUpdateManager(private val context: Context) {
    companion object {
        const val REPOSITORY_URL = "https://github.com/907609732/unpack-vision"
        const val RELEASE_URL = "$REPOSITORY_URL/releases/latest"
        const val APK_URL = "$RELEASE_URL/download/EcommerceUnpackRecorder-Android.apk"
        const val MANIFEST_URL = "$RELEASE_URL/download/mobile-update.json"
        private const val PREFS = "app_updates"
        private const val LAST_CHECK = "last_check"
        private val CHECK_INTERVAL = TimeUnit.DAYS.toMillis(1)
        private const val MAX_APK_BYTES = 150L * 1024L * 1024L
    }

    suspend fun check(force: Boolean = false): MobileUpdateResult = withContext(Dispatchers.IO) {
        val preferences = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val now = System.currentTimeMillis()
        if (!force && now - preferences.getLong(LAST_CHECK, 0L) < CHECK_INTERVAL) {
            return@withContext MobileUpdateResult.Skipped
        }
        preferences.edit().putLong(LAST_CHECK, now).apply()

        runCatching {
            val connection = URL(MANIFEST_URL).openConnection() as HttpURLConnection
            connection.connectTimeout = 10_000
            connection.readTimeout = 15_000
            connection.setRequestProperty("Accept", "application/json")
            connection.instanceFollowRedirects = true
            connection.inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
        }.mapCatching(MobileUpdateManifestParser::parse).fold(
            onSuccess = {
                if (it.versionCode > BuildConfig.VERSION_CODE) MobileUpdateResult.Available(it)
                else MobileUpdateResult.Current
            },
            onFailure = { MobileUpdateResult.Failed(it.message ?: "无法连接 GitHub 更新服务") }
        )
    }

    suspend fun downloadAndVerify(
        manifest: MobileUpdateManifest,
        onProgress: (Int) -> Unit
    ): File = withContext(Dispatchers.IO) {
        val manager = context.getSystemService(Context.DOWNLOAD_SERVICE) as DownloadManager
        val request = DownloadManager.Request(Uri.parse(manifest.apkUrl))
            .setTitle("电商拆包智能录像 ${manifest.versionName}")
            .setDescription("正在下载手机端更新")
            .setNotificationVisibility(DownloadManager.Request.VISIBILITY_VISIBLE_NOTIFY_COMPLETED)
            .setAllowedOverMetered(true)
            .setAllowedOverRoaming(false)
        val id = manager.enqueue(request)

        var downloadUri: Uri? = null
        while (downloadUri == null) {
            manager.query(DownloadManager.Query().setFilterById(id)).use { cursor ->
                if (!cursor.moveToFirst()) error("更新下载任务不存在")
                val status = cursor.getInt(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_STATUS))
                val total = cursor.getLong(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_TOTAL_SIZE_BYTES))
                val downloaded = cursor.getLong(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_BYTES_DOWNLOADED_SO_FAR))
                if (total > MAX_APK_BYTES || downloaded > MAX_APK_BYTES) {
                    manager.remove(id)
                    error("更新文件超过 150MB 安全上限")
                }
                if (total > 0) onProgress(((downloaded * 100L) / total).toInt().coerceIn(0, 100))
                when (status) {
                    DownloadManager.STATUS_SUCCESSFUL -> downloadUri = manager.getUriForDownloadedFile(id)
                    DownloadManager.STATUS_FAILED -> {
                        val reason = cursor.getInt(cursor.getColumnIndexOrThrow(DownloadManager.COLUMN_REASON))
                        error("更新下载失败（错误 $reason）")
                    }
                }
            }
            if (downloadUri == null) delay(500)
        }

        val targetDir = File(context.cacheDir, "updates").apply { mkdirs() }
        val target = File(targetDir, "EcommerceUnpackRecorder-Android.apk")
        val digest = MessageDigest.getInstance("SHA-256")
        val completedUri = downloadUri
        context.contentResolver.openInputStream(completedUri)
            ?.use { input ->
                target.outputStream().use { output ->
                    val buffer = ByteArray(DEFAULT_BUFFER_SIZE)
                    while (true) {
                        val count = input.read(buffer)
                        if (count < 0) break
                        digest.update(buffer, 0, count)
                        output.write(buffer, 0, count)
                    }
                }
            } ?: error("无法读取已下载的 APK")

        val actual = digest.digest().joinToString("") { "%02x".format(it) }
        if (!actual.equals(manifest.sha256, ignoreCase = true)) {
            target.delete()
            error("APK 校验失败，已阻止安装")
        }
        onProgress(100)
        target
    }

    fun requestInstallPermission(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O ||
            context.packageManager.canRequestPackageInstalls()
        ) return true

        context.startActivity(
            Intent(
                Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES,
                Uri.parse("package:${context.packageName}")
            ).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
        return false
    }

    fun install(apk: File) {
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", apk)
        context.startActivity(
            Intent(Intent.ACTION_VIEW)
                .setDataAndType(uri, "application/vnd.android.package-archive")
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }

    fun open(url: String) {
        context.startActivity(
            Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        )
    }
}
