package com.example.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.DeleteSweep
import androidx.compose.material.icons.filled.DoneAll
import androidx.compose.material.icons.filled.Inbox
import androidx.compose.material.icons.filled.PhoneAndroid
import androidx.compose.material.icons.filled.Tablet
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.example.data.model.MessageEntity
import com.example.ui.theme.MinimalBackground
import com.example.ui.theme.MinimalOnSurface
import com.example.ui.theme.MinimalOnSurfaceVariant
import com.example.ui.theme.MinimalOutline
import com.example.ui.theme.MinimalPrimary
import com.example.ui.theme.MinimalSurface
import com.example.ui.theme.MinimalSurfaceVariant
import com.example.ui.theme.StatusError
import com.example.ui.viewmodel.MainViewModel
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun InboxScreen(
    viewModel: MainViewModel,
    onNavigateBack: () -> Unit,
    onOpenMessageDetail: (MessageEntity) -> Unit
) {
    val inboxMessages by viewModel.inboxMessages.collectAsStateWithLifecycle()
    val unreadCount by viewModel.unreadCount.collectAsStateWithLifecycle()

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            text = "收件箱",
                            style = MaterialTheme.typography.titleLarge.copy(
                                fontWeight = FontWeight.Bold,
                                letterSpacing = (-0.3).sp
                            ),
                            color = MinimalOnSurface
                        )
                        if (unreadCount > 0) {
                            Spacer(modifier = Modifier.width(8.dp))
                            Surface(
                                shape = CircleShape,
                                color = StatusError
                            ) {
                                Text(
                                    text = "$unreadCount",
                                    style = MaterialTheme.typography.labelSmall.copy(
                                        color = Color.White,
                                        fontWeight = FontWeight.Bold
                                    ),
                                    modifier = Modifier.padding(horizontal = 7.dp, vertical = 2.dp)
                                )
                            }
                        }
                    }
                },
                navigationIcon = {
                    IconButton(
                        onClick = onNavigateBack,
                        modifier = Modifier.testTag("inbox_back_button")
                    ) {
                        Icon(
                            imageVector = Icons.AutoMirrored.Filled.ArrowBack,
                            contentDescription = "返回",
                            tint = MinimalPrimary
                        )
                    }
                },
                actions = {
                    if (inboxMessages.isNotEmpty()) {
                        IconButton(
                            onClick = { viewModel.markAllInboxRead() },
                            modifier = Modifier.testTag("mark_all_read_button")
                        ) {
                            Icon(
                                imageVector = Icons.Default.DoneAll,
                                contentDescription = "全部标为已读",
                                tint = MinimalPrimary
                            )
                        }

                        IconButton(
                            onClick = { viewModel.clearAllMessages() },
                            modifier = Modifier.testTag("clear_all_messages_button")
                        ) {
                            Icon(
                                imageVector = Icons.Default.DeleteSweep,
                                contentDescription = "清空收件箱",
                                tint = MinimalOnSurfaceVariant
                            )
                        }
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MinimalBackground
                )
            )
        },
        containerColor = MinimalBackground,
        modifier = Modifier
            .fillMaxSize()
            .statusBarsPadding()
            .testTag("inbox_screen")
    ) { paddingValues ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .padding(paddingValues)
                .padding(horizontal = 24.dp, vertical = 8.dp)
        ) {
            if (inboxMessages.isEmpty()) {
                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f),
                    contentAlignment = Alignment.Center
                ) {
                    Column(
                        horizontalAlignment = Alignment.CenterHorizontally,
                        verticalArrangement = Arrangement.Center
                    ) {
                        Surface(
                            shape = CircleShape,
                            color = MinimalSurfaceVariant,
                            modifier = Modifier.size(68.dp)
                        ) {
                            Box(contentAlignment = Alignment.Center) {
                                Icon(
                                    imageVector = Icons.Default.Inbox,
                                    contentDescription = null,
                                    tint = MinimalPrimary,
                                    modifier = Modifier.size(32.dp)
                                )
                            }
                        }
                        Spacer(modifier = Modifier.height(16.dp))
                        Text(
                            text = "收件箱空空如也",
                            style = MaterialTheme.typography.titleMedium.copy(fontWeight = FontWeight.SemiBold),
                            color = MinimalOnSurface
                        )
                        Spacer(modifier = Modifier.height(6.dp))
                        Text(
                            text = "接收与发送的文本消息将在此处显示",
                            style = MaterialTheme.typography.bodyMedium,
                            color = MinimalOnSurfaceVariant
                        )
                    }
                }
            } else {
                LazyColumn(
                    modifier = Modifier
                        .fillMaxWidth()
                        .weight(1f),
                    verticalArrangement = Arrangement.spacedBy(12.dp)
                ) {
                    items(inboxMessages, key = { it.id }) { message ->
                        InboxMessageCard(
                            message = message,
                            onClick = {
                                viewModel.openMessage(message)
                                onOpenMessageDetail(message)
                            }
                        )
                    }
                }
            }
        }
    }
}

@Composable
fun InboxMessageCard(
    message: MessageEntity,
    onClick: () -> Unit
) {
    val isOutgoing = message.isOutgoing
    val isUnread = !isOutgoing && message.readAt == null
    val displayName = if (isOutgoing) message.receiverDeviceName else message.senderDeviceName
    val timeFormatted = remember(message.createdAt) {
        val sdf = SimpleDateFormat("HH:mm", Locale.getDefault())
        val dateSdf = SimpleDateFormat("MM-dd", Locale.getDefault())
        val now = System.currentTimeMillis()
        val isToday = (now - message.createdAt) < 24 * 60 * 60 * 1000
        if (isToday) sdf.format(Date(message.createdAt)) else "${dateSdf.format(Date(message.createdAt))} ${sdf.format(Date(message.createdAt))}"
    }

    Card(
        modifier = Modifier
            .fillMaxWidth()
            .clip(RoundedCornerShape(24.dp))
            .clickable(onClick = onClick)
            .testTag("inbox_message_item_${message.id}"),
        shape = RoundedCornerShape(24.dp),
        colors = CardDefaults.cardColors(
            containerColor = if (isUnread) MinimalSurface else MinimalSurfaceVariant
        ),
        border = androidx.compose.foundation.BorderStroke(
            width = if (isUnread) 1.5.dp else 1.dp,
            color = if (isUnread) MinimalPrimary.copy(alpha = 0.5f) else MinimalOutline
        )
    ) {
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(18.dp)
        ) {
            // Header Row: Device Name + Time + Unread indicator
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Row(
                    verticalAlignment = Alignment.CenterVertically,
                    modifier = Modifier.weight(1f)
                ) {
                    val deviceIcon = when {
                        displayName.contains("电脑") || displayName.contains("Windows") -> Icons.Default.Computer
                        displayName.contains("平板") || displayName.contains("iPad") -> Icons.Default.Tablet
                        else -> Icons.Default.PhoneAndroid
                    }

                    Icon(
                        imageVector = deviceIcon,
                        contentDescription = null,
                        tint = if (isUnread) MinimalPrimary else MinimalOnSurfaceVariant,
                        modifier = Modifier.size(18.dp)
                    )

                    Spacer(modifier = Modifier.width(8.dp))

                    if (isOutgoing) {
                        Text(
                            text = "我 → ",
                            style = MaterialTheme.typography.titleSmall.copy(
                                fontWeight = if (isUnread) FontWeight.Bold else FontWeight.SemiBold
                            ),
                            color = MinimalPrimary
                        )
                    }

                    Text(
                        text = displayName,
                        style = MaterialTheme.typography.titleSmall.copy(
                            fontWeight = if (isUnread) FontWeight.Bold else FontWeight.SemiBold
                        ),
                        color = MinimalOnSurface,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )

                    if (isUnread) {
                        Spacer(modifier = Modifier.width(8.dp))
                        Box(
                            modifier = Modifier
                                .size(8.dp)
                                .background(color = StatusError, shape = CircleShape)
                        )
                    }
                }

                Text(
                    text = timeFormatted,
                    style = MaterialTheme.typography.bodySmall,
                    color = MinimalOnSurfaceVariant
                )
            }

            Spacer(modifier = Modifier.height(10.dp))

            // Message Content Preview
            Text(
                text = message.content,
                style = MaterialTheme.typography.bodyMedium.copy(
                    fontWeight = if (isUnread) FontWeight.Medium else FontWeight.Normal,
                    fontSize = 15.sp,
                    lineHeight = 22.sp
                ),
                color = if (isUnread) MinimalOnSurface else MinimalOnSurfaceVariant,
                maxLines = 3,
                overflow = TextOverflow.Ellipsis
            )
        }
    }
}
