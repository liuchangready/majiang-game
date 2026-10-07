package com.majiang.transport.udp

import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertTrue

class UdpFrameCodecTest {

    @Test
    fun `encode and decode roundtrip for all 9 packet types`() {
        val types = UdpPacketType.entries
        assertEquals(9, types.size, "Should have exactly 9 packet types defined in §7.3")

        for ((index, type) in types.withIndex()) {
            val payload = "Test payload for ${type.name} index=$index".toByteArray(Charsets.UTF_8)
            val original = UdpFrame(
                packetType = type,
                connectionId = 0x12345678,
                seq = 100 + index,
                ackSeq = 50 + index,
                payload = payload,
                flagZlib = index % 2 == 0,
                flagEncrypted = index % 3 == 0,
                version = UdpProtocol.VERSION
            )

            val bytes = UdpFrame.encode(original)
            assertEquals(UdpProtocol.HEADER_SIZE + payload.size, bytes.size)

            val decoded = UdpFrame.decode(bytes)
            assertEquals(original, decoded)
            assertEquals(original.packetType, decoded.packetType)
            assertEquals(original.connectionId, decoded.connectionId)
            assertEquals(original.seq, decoded.seq)
            assertEquals(original.ackSeq, decoded.ackSeq)
            assertTrue(original.payload.contentEquals(decoded.payload))
            assertEquals(original.flagZlib, decoded.flagZlib)
            assertEquals(original.flagEncrypted, decoded.flagEncrypted)
            assertEquals(original.version, decoded.version)
        }
    }

    @Test
    fun `encode and decode empty payload like ACK`() {
        val ackFrame = UdpFrame(
            packetType = UdpPacketType.ACK,
            connectionId = 999,
            seq = 42,
            ackSeq = 101,
            payload = ByteArray(0)
        )

        val bytes = UdpFrameCodec.encode(ackFrame)
        assertEquals(22, bytes.size, "ACK without payload must be exactly 22 bytes")

        val decoded = UdpFrameCodec.decode(bytes)
        assertEquals(ackFrame, decoded)
        assertEquals(0, decoded.payload.size)
    }

    @Test
    fun `encode and decode maximum allowed payload 1178 bytes`() {
        val maxPayload = ByteArray(UdpProtocol.MAX_PAYLOAD_SIZE) { (it % 256).toByte() }
        val frame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 12345,
            seq = 1,
            payload = maxPayload
        )

        val bytes = UdpFrame.encode(frame)
        assertEquals(1200, bytes.size, "Max datagram must equal exactly 1200 bytes")

        val decoded = UdpFrame.decode(bytes)
        assertEquals(frame, decoded)
    }

    @Test
    fun `reject datagram exceeding 1200 bytes on encode`() {
        val oversizedPayload = ByteArray(UdpProtocol.MAX_PAYLOAD_SIZE + 1)
        val frame = UdpFrame(
            packetType = UdpPacketType.PUSH,
            connectionId = 12345,
            seq = 1,
            payload = oversizedPayload
        )

        val ex = assertFailsWith<IllegalArgumentException> {
            UdpFrame.encode(frame)
        }
        assertTrue(ex is UdpFrameException)
        assertEquals(UdpProtocol.REASON_DATAGRAM_TOO_LARGE, ex.reasonCode)
        assertTrue(ex.message!!.contains(UdpProtocol.REASON_DATAGRAM_TOO_LARGE))
    }

    @Test
    fun `reject datagram exceeding 1200 bytes on decode`() {
        val bytes = ByteArray(1201)
        val ex = assertFailsWith<IllegalArgumentException> {
            UdpFrame.decode(bytes)
        }
        assertTrue(ex is UdpFrameException)
        assertEquals(UdpProtocol.REASON_DATAGRAM_TOO_LARGE, ex.reasonCode)
    }

    @Test
    fun `reject truncated datagram smaller than 22 bytes header`() {
        val bytes = ByteArray(21)
        val ex = assertFailsWith<IllegalArgumentException> {
            UdpFrame.decode(bytes)
        }
        assertTrue(ex is UdpFrameException)
        assertEquals(UdpProtocol.REASON_DATAGRAM_TOO_SHORT, ex.reasonCode)
    }

    @Test
    fun `reject invalid magic bytes`() {
        val frame = UdpFrame(
            packetType = UdpPacketType.PING,
            connectionId = 1,
            seq = 1
        )
        val bytes = UdpFrame.encode(frame)
        // Corrupt magic from 0x4D47 to 0x0000
        bytes[0] = 0
        bytes[1] = 0

        val ex = assertFailsWith<IllegalArgumentException> {
            UdpFrame.decode(bytes)
        }
        assertTrue(ex is UdpFrameException)
        assertEquals(UdpProtocol.REASON_INVALID_MAGIC, ex.reasonCode)
    }

    @Test
    fun `reject invalid protocol version`() {
        val frame = UdpFrame(
            packetType = UdpPacketType.PING,
            connectionId = 1,
            seq = 1
        )
        val bytes = UdpFrame.encode(frame)
        // Corrupt version at offset 2 to 3 (or 99)
        bytes[2] = 3

        val ex = assertFailsWith<IllegalArgumentException> {
            UdpFrame.decode(bytes)
        }
        assertTrue(ex is UdpFrameException)
        assertEquals(UdpProtocol.REASON_INVALID_VERSION, ex.reasonCode)
    }

    @Test
    fun `reject unknown packet type`() {
        val frame = UdpFrame(
            packetType = UdpPacketType.PING,
            connectionId = 1,
            seq = 1
        )
        val bytes = UdpFrame.encode(frame)
        // Corrupt type byte at offset 3 to 0x3F (undefined type ID 63)
        bytes[3] = 0x3F

        val ex = assertFailsWith<IllegalArgumentException> {
            UdpFrame.decode(bytes)
        }
        assertTrue(ex is UdpFrameException)
        assertEquals(UdpProtocol.REASON_UNKNOWN_PACKET_TYPE, ex.reasonCode)
    }

    @Test
    fun `reject payload length mismatch`() {
        val frame = UdpFrame(
            packetType = UdpPacketType.REQ,
            connectionId = 1,
            seq = 1,
            payload = "Hello".toByteArray(Charsets.UTF_8)
        )
        val bytes = UdpFrame.encode(frame)
        // Truncate last 2 bytes of payload from datagram
        val truncated = bytes.copyOf(bytes.size - 2)

        val ex = assertFailsWith<IllegalArgumentException> {
            UdpFrame.decode(truncated)
        }
        assertTrue(ex is UdpFrameException)
        assertEquals(UdpProtocol.REASON_PAYLOAD_LENGTH_MISMATCH, ex.reasonCode)
    }

    @Test
    fun `reject CRC mismatch when payload or header is corrupted`() {
        val frame = UdpFrame(
            packetType = UdpPacketType.REQ,
            connectionId = 1,
            seq = 1,
            payload = "Normal data".toByteArray(Charsets.UTF_8)
        )
        val bytes = UdpFrame.encode(frame)
        // Tamper with a payload byte
        bytes[bytes.size - 1] = (bytes[bytes.size - 1] + 1).toByte()

        val ex = assertFailsWith<IllegalArgumentException> {
            UdpFrame.decode(bytes)
        }
        assertTrue(ex is UdpFrameException)
        assertEquals(UdpProtocol.REASON_CRC_MISMATCH, ex.reasonCode)
    }

    @Test
    fun `verify header offsets and big-endian layout`() {
        val frame = UdpFrame(
            packetType = UdpPacketType.REQ,
            connectionId = 0x01020304,
            seq = 0x05060708,
            ackSeq = 0x090A0B0C,
            payload = byteArrayOf(0x11, 0x22),
            flagZlib = true,
            flagEncrypted = false,
            version = 4
        )
        val bytes = UdpFrame.encode(frame)

        // Offset 0..1: Magic 0x4D, 0x47
        assertEquals(0x4D.toByte(), bytes[0])
        assertEquals(0x47.toByte(), bytes[1])

        // Offset 2: Version 4
        assertEquals(4.toByte(), bytes[2])

        // Offset 3: packetType REQ (0x01) with bit7 FLAG_ZLIB (0x80) -> 0x81
        assertEquals(0x81.toByte(), bytes[3])

        // Offset 4..7: connectionId 0x01020304 (Big Endian)
        assertEquals(0x01.toByte(), bytes[4])
        assertEquals(0x02.toByte(), bytes[5])
        assertEquals(0x03.toByte(), bytes[6])
        assertEquals(0x04.toByte(), bytes[7])

        // Offset 8..11: seq 0x05060708
        assertEquals(0x05.toByte(), bytes[8])
        assertEquals(0x06.toByte(), bytes[9])
        assertEquals(0x07.toByte(), bytes[10])
        assertEquals(0x08.toByte(), bytes[11])

        // Offset 12..15: ackSeq 0x090A0B0C
        assertEquals(0x09.toByte(), bytes[12])
        assertEquals(0x0A.toByte(), bytes[13])
        assertEquals(0x0B.toByte(), bytes[14])
        assertEquals(0x0C.toByte(), bytes[15])

        // Offset 16..17: payloadLen 2 (0x0002)
        assertEquals(0x00.toByte(), bytes[16])
        assertEquals(0x02.toByte(), bytes[17])

        // Offset 22..23: payload
        assertEquals(0x11.toByte(), bytes[22])
        assertEquals(0x22.toByte(), bytes[23])
    }
}
