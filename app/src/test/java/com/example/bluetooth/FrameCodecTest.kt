package com.example.bluetooth

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test
import java.io.ByteArrayInputStream
import java.io.EOFException

class FrameCodecTest {

    @Test
    fun `roundtrip text frame`() {
        val payload = """{"type":"TEXT","content":"你好 BlueSelf"}""".toByteArray(Charsets.UTF_8)
        val frame = MessageProtocol.Frame(MessageProtocol.FT_TXT, 42, payload)
        val bytes = FrameCodec.encode(frame)
        val decoded = FrameCodec.decode(ByteArrayInputStream(bytes))

        assertEquals(MessageProtocol.FT_TXT, decoded.type)
        assertEquals(42L, decoded.seq)
        assertArrayEquals(payload, decoded.payload)
    }

    @Test
    fun `roundtrip binary chunk frame`() {
        val payload = ByteArray(32 * 1024) { (it % 251).toByte() }
        val frame = MessageProtocol.Frame(MessageProtocol.FT_FILE_CHUNK, 7, payload)
        val decoded = FrameCodec.decode(ByteArrayInputStream(FrameCodec.encode(frame)))
        assertEquals(MessageProtocol.FT_FILE_CHUNK, decoded.type)
        assertArrayEquals(payload, decoded.payload)
    }

    @Test
    fun `two frames back to back decode correctly`() {
        val f1 = MessageProtocol.Frame(MessageProtocol.FT_FILE_START, 1, "first".toByteArray())
        val f2 = MessageProtocol.Frame(MessageProtocol.FT_FILE_END, 2, "second".toByteArray())
        val stream = ByteArrayInputStream(FrameCodec.encode(f1) + FrameCodec.encode(f2))

        val d1 = FrameCodec.decode(stream)
        val d2 = FrameCodec.decode(stream)
        assertEquals("first", d1.payload.toString(Charsets.UTF_8))
        assertEquals("second", d2.payload.toString(Charsets.UTF_8))
    }

    @Test
    fun `corrupted payload fails crc check`() {
        val frame = MessageProtocol.Frame(MessageProtocol.FT_TXT, 1, "hello".toByteArray())
        val bytes = FrameCodec.encode(frame)
        // Flip a bit inside the payload.
        bytes[MessageProtocol.HEADER_SIZE] = (bytes[MessageProtocol.HEADER_SIZE].toInt() xor 0x01).toByte()
        assertThrows(IllegalArgumentException::class.java) {
            FrameCodec.decode(ByteArrayInputStream(bytes))
        }
    }

    @Test
    fun `bad magic rejected`() {
        val frame = MessageProtocol.Frame(MessageProtocol.FT_TXT, 1, ByteArray(0))
        val bytes = FrameCodec.encode(frame)
        bytes[0] = 0x00
        assertThrows(IllegalArgumentException::class.java) {
            FrameCodec.decode(ByteArrayInputStream(bytes))
        }
    }

    @Test
    fun `truncated stream throws eof`() {
        val frame = MessageProtocol.Frame(MessageProtocol.FT_TXT, 1, "abc".toByteArray())
        val bytes = FrameCodec.encode(frame)
        val truncated = bytes.copyOf(bytes.size - 2)
        assertThrows(EOFException::class.java) {
            FrameCodec.decode(ByteArrayInputStream(truncated))
        }
    }

    @Test
    fun `file meta json roundtrip`() {
        val payload = FileMetaJson.encodeStart(
            id = "f1", msgId = "m1", name = "photo.jpg", mime = "image/jpeg",
            size = 1024, md5 = "deadbeef", chunkSize = 32768, totalChunks = 1
        )
        val start = FileMetaJson.decodeStart(payload)
        assertEquals("f1", start.id)
        assertEquals("m1", start.msgId)
        assertEquals("photo.jpg", start.name)
        assertEquals("image/jpeg", start.mime)
        assertEquals(1024L, start.size)
        assertEquals("deadbeef", start.md5)
    }
}
