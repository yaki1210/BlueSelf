package com.example.data.repository

import com.example.data.db.DeviceDao
import com.example.data.model.DeviceEntity
import kotlinx.coroutines.flow.Flow

class DeviceRepository(private val deviceDao: DeviceDao) {
    val allDevices: Flow<List<DeviceEntity>> = deviceDao.getAllDevices()
    val currentDevice: Flow<DeviceEntity?> = deviceDao.getCurrentDevice()

    suspend fun getDeviceById(id: String): DeviceEntity? = deviceDao.getDeviceById(id)

    suspend fun getDeviceByMac(mac: String): DeviceEntity? = deviceDao.getDeviceByMac(mac)

    suspend fun saveDevice(device: DeviceEntity) {
        deviceDao.insertDevice(device)
    }

    suspend fun saveDevices(devices: List<DeviceEntity>) {
        deviceDao.insertDevices(devices)
    }

    suspend fun setCurrentDevice(deviceId: String) {
        deviceDao.setCurrentDevice(deviceId)
    }

    suspend fun updateDeviceStatus(deviceId: String, status: String) {
        deviceDao.updateDeviceStatus(deviceId, status)
    }

    suspend fun deleteDevice(deviceId: String) {
        deviceDao.deleteDeviceById(deviceId)
    }

    suspend fun getDeviceCount(): Int = deviceDao.getDeviceCount()
}
