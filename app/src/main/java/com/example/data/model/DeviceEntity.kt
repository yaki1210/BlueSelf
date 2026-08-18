package com.example.data.model

import androidx.room.Entity
import androidx.room.PrimaryKey

/**
 * Represents a paired/saved Bluetooth device.
 * Includes fields for future expansion: custom notes, icon types, and pinned status.
 */
@Entity(tableName = "devices")
data class DeviceEntity(
    @PrimaryKey val id: String, // UUID or MAC address
    val name: String,
    val macAddress: String,
    val deviceType: String = "OTHER", // "PC", "PHONE", "TABLET", "OTHER"
    val lastConnectedAt: Long = System.currentTimeMillis(),
    val lastKnownState: String = "OFFLINE", // "ONLINE", "CONNECTING", "OFFLINE"
    val isCurrent: Boolean = false,
    val notes: String? = null,
    val isPinned: Boolean = false
)
