@file:JvmName("Format")

package com.example.ui

import java.util.Locale

/**
 * Formats a byte count into a human-readable string.
 * Examples: 0 → "0 B", 345 → "345 B", 353484 → "345.2 KB", 5.3 MB.
 */
fun formatSize(size: Long): String {
    if (size <= 0) return "0 B"
    val units = arrayOf("B", "KB", "MB", "GB")
    var value = size.toDouble()
    var unit = 0
    while (value >= 1024 && unit < units.size - 1) {
        value /= 1024
        unit++
    }
    return if (unit == 0) "${size} B" else String.format(Locale.getDefault(), "%.1f %s", value, units[unit])
}
