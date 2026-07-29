package com.unpackvision.mobile

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.work.CoroutineWorker
import androidx.work.ExistingPeriodicWorkPolicy
import androidx.work.PeriodicWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.WorkerParameters
import java.util.concurrent.TimeUnit

class UpdateCheckWorker(
    context: Context,
    parameters: WorkerParameters
) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        if (!AppPreferences(applicationContext).hasCurrentConsent) {
            return Result.success()
        }
        return when (val update = AppUpdateManager(applicationContext).check(force = false)) {
            is MobileUpdateResult.Available -> {
                showUpdateNotification(update.manifest)
                Result.success()
            }
            is MobileUpdateResult.Failed -> Result.retry()
            else -> Result.success()
        }
    }

    private fun showUpdateNotification(manifest: MobileUpdateManifest) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            ContextCompat.checkSelfPermission(applicationContext, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) return

        val manager = applicationContext.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "软件更新",
                NotificationManager.IMPORTANCE_DEFAULT
            ).apply {
                description = "电商拆包智能录像新版本和安全更新提醒"
            }
        )
        val updateIntent = Intent().apply {
            component = ComponentName(applicationContext, MainActivity::class.java)
            setPackage(applicationContext.packageName)
            action = "${applicationContext.packageName}.action.OPEN_UPDATE"
            flags = Intent.FLAG_ACTIVITY_CLEAR_TOP or Intent.FLAG_ACTIVITY_SINGLE_TOP
        }
        val pendingIntent = PendingIntent.getActivity(
            applicationContext,
            UPDATE_NOTIFICATION_ID,
            updateIntent,
            PendingIntent.FLAG_CANCEL_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )
        val title = if (manifest.critical) "发现安全更新" else "发现新版本"
        val notification = NotificationCompat.Builder(applicationContext, CHANNEL_ID)
            .setSmallIcon(R.mipmap.ic_launcher)
            .setContentTitle("$title ${manifest.versionName}")
            .setContentText("点击打开设置，查看更新说明并下载安装")
            .setContentIntent(pendingIntent)
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(applicationContext)
            .notify(UPDATE_NOTIFICATION_ID, notification)
    }

    companion object {
        private const val CHANNEL_ID = "unpackvision-updates"
        private const val UPDATE_NOTIFICATION_ID = 2200
        private const val UNIQUE_WORK = "unpackvision-daily-update-check"

        fun schedule(context: Context) {
            val request = PeriodicWorkRequestBuilder<UpdateCheckWorker>(1, TimeUnit.DAYS)
                .setInitialDelay(6, TimeUnit.HOURS)
                .build()
            WorkManager.getInstance(context).enqueueUniquePeriodicWork(
                UNIQUE_WORK,
                ExistingPeriodicWorkPolicy.UPDATE,
                request
            )
        }
    }
}
