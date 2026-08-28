package com.example.notifications

import android.Manifest
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import com.example.MainActivity
import com.example.R

/**
 * 收到消息/文件时的通知器（A4）。
 * 前置条件：通知渠道已创建、系统通知开关开启、（API 33+）POST_NOTIFICATIONS 已授权。
 * 任一条件不满足时静默跳过，绝不抛异常影响蓝牙流程。
 *
 * 点击行为：携带 EXTRA_MESSAGE_ID 打开 MainActivity，导航到该消息详情页。
 */
object MessageNotifier {

    private const val CHANNEL_ID = "incoming_messages"
    private var channelReady = false

    const val EXTRA_MESSAGE_ID = "com.example.EXTRA_MESSAGE_ID"

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

    /** 点击通知 → 打开 App 并落到该消息详情页。 */
    private fun contentIntent(context: Context, messageId: String): PendingIntent {
        val intent = Intent(context, MainActivity::class.java).apply {
            flags = Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP
            putExtra(EXTRA_MESSAGE_ID, messageId)
        }
        var flags = PendingIntent.FLAG_UPDATE_CURRENT
        if (Build.VERSION.SDK_INT >= 23) flags = flags or PendingIntent.FLAG_IMMUTABLE
        return PendingIntent.getActivity(context, messageId.hashCode(), intent, flags)
    }

    /** 文本消息通知。 */
    fun notifyMessage(context: Context, senderName: String, preview: String, messageId: String) {
        if (!canNotify(context)) return
        val text = if (preview.length > 60) preview.take(60) + "…" else preview
        val n = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)
            .setContentTitle(context.getString(R.string.notif_message_title, senderName))
            .setContentText(text)
            .setStyle(NotificationCompat.BigTextStyle().bigText(preview))
            .setContentIntent(contentIntent(context, messageId))
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .build()
        runCatching { NotificationManagerCompat.from(context).notify(messageId.hashCode(), n) }
    }

    /** 文件接收完成通知（文件作为消息入库，同样带详情跳转）。 */
    fun notifyFile(context: Context, senderName: String, fileName: String, messageId: String) {
        if (!canNotify(context)) return
        val n = NotificationCompat.Builder(context, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_notify)
            .setContentTitle(context.getString(R.string.notif_file_title, senderName))
            .setContentText(fileName)
            .setContentIntent(contentIntent(context, messageId))
            .setAutoCancel(true)
            .setCategory(NotificationCompat.CATEGORY_MESSAGE)
            .build()
        runCatching { NotificationManagerCompat.from(context).notify(messageId.hashCode(), n) }
    }
}
