package com.example.ui

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class MessageDisplayTest {

    @Test
    fun `inbox preview prefers body text`() {
        val preview = InboxPreview.of("hello", fileCount = 2)
        assertEquals(InboxPreview.Kind.Body("hello"), preview)
    }

    @Test
    fun `inbox preview falls back to attachment count when body is blank`() {
        val preview = InboxPreview.of("  ", fileCount = 1)
        assertEquals(InboxPreview.Kind.Attachments(1), preview)
    }

    @Test
    fun `inbox preview empty when no body and no files`() {
        assertEquals(InboxPreview.Kind.Empty, InboxPreview.of("", 0))
    }

    @Test
    fun `pdf detected by mime or extension`() {
        assertTrue(isPdfFile("application/pdf", "notes.bin"))
        assertTrue(isPdfFile("application/octet-stream", "paper.PDF"))
        assertFalse(isPdfFile("application/octet-stream", "photo.jpg"))
    }

    @Test
    fun `inbound badge stays receiving while any file is in flight`() {
        assertEquals(
            InboundStatus.RECEIVING,
            inboundStatusLabel(false, "RECEIVED", listOf("RECEIVING", "COMPLETE"))
        )
        assertEquals(
            InboundStatus.FAILED,
            inboundStatusLabel(false, "RECEIVED", listOf("FAILED"))
        )
        assertEquals(
            InboundStatus.RECEIVED,
            inboundStatusLabel(false, "RECEIVED", listOf("COMPLETE"))
        )
        assertEquals(InboundStatus.SENT, inboundStatusLabel(true, "SENT", emptyList()))
    }
}
