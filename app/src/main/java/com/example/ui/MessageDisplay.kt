package com.example.ui

/**
 * Shared inbox/detail display helpers so empty file-only messages don't look like
 * a successfully delivered blank text bubble.
 */
object InboxPreview {
    sealed class Kind {
        data class Body(val text: String) : Kind()
        data class Attachments(val count: Int) : Kind()
        data object Empty : Kind()
    }

    fun of(content: String, fileCount: Int): Kind {
        val text = content.trim()
        return when {
            text.isNotEmpty() -> Kind.Body(text)
            fileCount > 0 -> Kind.Attachments(fileCount)
            else -> Kind.Empty
        }
    }
}

fun isPdfFile(mimeType: String, fileName: String): Boolean =
    mimeType.equals("application/pdf", ignoreCase = true) ||
        fileName.endsWith(".pdf", ignoreCase = true)

fun inboundStatusLabel(
    isOutgoing: Boolean,
    messageStatus: String,
    fileStatuses: List<String>
): InboundStatus {
    if (isOutgoing) return InboundStatus.SENT
    if (fileStatuses.any { it == "RECEIVING" } || messageStatus == "RECEIVING") return InboundStatus.RECEIVING
    if (fileStatuses.any { it == "FAILED" } || messageStatus == "FAILED") return InboundStatus.FAILED
    return InboundStatus.RECEIVED
}

enum class InboundStatus { SENT, RECEIVED, RECEIVING, FAILED }
