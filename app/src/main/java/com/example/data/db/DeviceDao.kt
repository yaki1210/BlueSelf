package com.example.data.db

import androidx.room.Dao
import androidx.room.Delete
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Transaction
import androidx.room.Update
import com.example.data.model.DeviceEntity
import kotlinx.coroutines.flow.Flow

@Dao
interface DeviceDao {
    @Query("SELECT * FROM devices ORDER BY isPinned DESC, lastConnectedAt DESC")
    fun getAllDevices(): Flow<List<DeviceEntity>>

    @Query("SELECT * FROM devices WHERE isCurrent = 1 LIMIT 1")
    fun getCurrentDevice(): Flow<DeviceEntity?>

    @Query("SELECT * FROM devices WHERE id = :id LIMIT 1")
    suspend fun getDeviceById(id: String): DeviceEntity?

    @Query("SELECT * FROM devices WHERE macAddress = :macAddress LIMIT 1")
    suspend fun getDeviceByMac(macAddress: String): DeviceEntity?

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertDevice(device: DeviceEntity)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertDevices(devices: List<DeviceEntity>)

    @Update
    suspend fun updateDevice(device: DeviceEntity)

    @Delete
    suspend fun deleteDevice(device: DeviceEntity)

    @Query("DELETE FROM devices WHERE id = :id")
    suspend fun deleteDeviceById(id: String)

    @Query("UPDATE devices SET isCurrent = 0")
    suspend fun clearCurrentDevice()

    @Query("UPDATE devices SET isCurrent = 1 WHERE id = :deviceId")
    suspend fun markAsCurrent(deviceId: String)

    @Transaction
    suspend fun setCurrentDevice(deviceId: String) {
        clearCurrentDevice()
        markAsCurrent(deviceId)
    }

    @Query("UPDATE devices SET lastKnownState = :status, lastConnectedAt = :timestamp WHERE id = :deviceId")
    suspend fun updateDeviceStatus(deviceId: String, status: String, timestamp: Long = System.currentTimeMillis())

    @Query("SELECT COUNT(*) FROM devices")
    suspend fun getDeviceCount(): Int
}
