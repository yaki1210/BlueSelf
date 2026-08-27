package com.example.notifications

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.example.R

/**
 * 收到消息/文件时的通知器（A4）。
 * 前置条件：通知渠道已创建、系统通知开关开启、（API 33+）POST_NOTIFICATIONS 已授权。
 * 任一条件不满足时静默跳过，绝不抛异常影响蓝牙流程。
 */
object MessageNotifier {

    private const val CHANNEL_ID = "incoming_messages"
    private var channelReady = false

    /** 在 App 启动时调用一次（MainActivity onCreate）。 */
    fun ensureChannel(context: Context) {
        if (channelReady) return
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                context.getString(R.string.notif_channel_name),
                NotificationManager.IMPORTANCE_DEFAULT
            ).apply {
                description = context.getString(R.string.notif_channel_desc)
            }
        )
        channelReady = true
    }

    fun canNotify(context: Context): Boolean {
        if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return false
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS)
            != PackageManager.PERMISSION_GRANTED
        ) return false
        return true
    }

    /** 文本消息通知。 */
    fun notifyMessage(context: Context, senderName: String, preview: String) {
        if (!canNotify(context)) return
        val text = if (preview.length > 60) preview.take(60) + "…" else preview
        val n = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)
            .setContentTitle(context.getString(R.string.notif_message_title, senderName))
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(preview))
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .build()
        runCatching { NotificationManagerCompat.from(context).notify(text.hashCode(), n) }
    }

    /** 文件接收完成通知。 */
    fun notifyFile(context: Context, senderName: String, fileName: String) {
        if (!canNotify(context)) return
        val n = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)
            .setContentTitle(context.getString(R.string.notif_file_title, senderName))
            .setContentText(fileName)
            .setAutoCancel(true)
            .build()
        runCatching { NotificationManagerCompat.from(context).notify(fileName.hashCode(), n) }
    }
}
