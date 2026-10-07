package com.majiang.server.api

import com.majiang.transport.api.ProtocolMatchConfig
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

class ApiDtoValidationTest {

    private val sampleSnapshot = FakeServerConnection.createDefaultSnapshot()

    // === CreateRoomRequest ===

    @Test
    fun `CreateRoomRequest succeeds with valid parameters`() {
        val req1 = CreateRoomRequest()
        assertNull(req1.playerName)
        assertNull(req1.config)
        assertEquals(ApiProtocol.PROTOCOL_VERSION, req1.protocolVersion)

        val config = ProtocolMatchConfig()
        val req2 = CreateRoomRequest(playerName = "HostMaster", config = config, protocolVersion = 4)
        assertEquals("HostMaster", req2.playerName)
        assertEquals(config, req2.config)
        assertEquals(4, req2.protocolVersion)
    }

    @Test
    fun `CreateRoomRequest rejects invalid protocolVersion`() {
        assertThrows(IllegalArgumentException::class.java) {
            CreateRoomRequest(protocolVersion = 0)
        }
        assertThrows(IllegalArgumentException::class.java) {
            CreateRoomRequest(protocolVersion = -1)
        }
    }

    @Test
    fun `CreateRoomRequest rejects blank playerName`() {
        assertThrows(IllegalArgumentException::class.java) {
            CreateRoomRequest(playerName = "")
        }
        assertThrows(IllegalArgumentException::class.java) {
            CreateRoomRequest(playerName = "   ")
        }
    }

    // === JoinRoomRequest ===

    @Test
    fun `JoinRoomRequest succeeds with valid parameters`() {
        val req1 = JoinRoomRequest(roomId = "ROOM1234")
        assertEquals("ROOM1234", req1.roomId)
        assertNull(req1.playerName)
        assertNull(req1.preferSeat)

        val req2 = JoinRoomRequest(roomId = "ROOM1234", playerName = "Player2", preferSeat = 2)
        assertEquals("ROOM1234", req2.roomId)
        assertEquals("Player2", req2.playerName)
        assertEquals(2, req2.preferSeat)
    }

    @Test
    fun `JoinRoomRequest rejects blank roomId`() {
        assertThrows(IllegalArgumentException::class.java) {
            JoinRoomRequest(roomId = "")
        }
        assertThrows(IllegalArgumentException::class.java) {
            JoinRoomRequest(roomId = "   ")
        }
    }

    @Test
    fun `JoinRoomRequest rejects blank playerName`() {
        assertThrows(IllegalArgumentException::class.java) {
            JoinRoomRequest(roomId = "ROOM1234", playerName = "")
        }
        assertThrows(IllegalArgumentException::class.java) {
            JoinRoomRequest(roomId = "ROOM1234", playerName = "   ")
        }
    }

    @Test
    fun `JoinRoomRequest rejects invalid preferSeat`() {
        assertThrows(IllegalArgumentException::class.java) {
            JoinRoomRequest(roomId = "ROOM1234", preferSeat = -1)
        }
        assertThrows(IllegalArgumentException::class.java) {
            JoinRoomRequest(roomId = "ROOM1234", preferSeat = 4)
        }
        for (validSeat in 0..3) {
            val req = JoinRoomRequest(roomId = "ROOM1234", preferSeat = validSeat)
            assertEquals(validSeat, req.preferSeat)
        }
    }

    // === ResumeRequest ===

    @Test
    fun `ResumeRequest succeeds with valid parameters`() {
        val req1 = ResumeRequest(roomId = "ROOM1234", token = "token-secret")
        assertEquals("ROOM1234", req1.roomId)
        assertEquals("token-secret", req1.token)
        assertNull(req1.cursor)

        val req2 = ResumeRequest(roomId = "ROOM1234", token = "token-secret", cursor = 100L)
        assertEquals(100L, req2.cursor)

        val req3 = ResumeRequest(roomId = "ROOM1234", token = "token-secret", cursor = 0L)
        assertEquals(0L, req3.cursor)
    }

    @Test
    fun `ResumeRequest rejects blank roomId or token`() {
        assertThrows(IllegalArgumentException::class.java) {
            ResumeRequest(roomId = "", token = "token-secret")
        }
        assertThrows(IllegalArgumentException::class.java) {
            ResumeRequest(roomId = "   ", token = "token-secret")
        }
        assertThrows(IllegalArgumentException::class.java) {
            ResumeRequest(roomId = "ROOM1234", token = "")
        }
        assertThrows(IllegalArgumentException::class.java) {
            ResumeRequest(roomId = "ROOM1234", token = "   ")
        }
    }

    @Test
    fun `ResumeRequest rejects negative cursor`() {
        assertThrows(IllegalArgumentException::class.java) {
            ResumeRequest(roomId = "ROOM1234", token = "token-secret", cursor = -1L)
        }
    }

    @Test
    fun `ResumeRequest toString masks token`() {
        val secretToken = "very-sensitive-resume-token-12345"
        val req = ResumeRequest(roomId = "ROOM1234", token = secretToken, cursor = 42L)
        val str = req.toString()
        assertFalse(str.contains(secretToken), "toString must not expose plaintext token")
        assertTrue(str.contains("token=***"), "toString must contain masked token")
        assertTrue(str.contains("ROOM1234"))
        assertTrue(str.contains("42"))
    }

    // === RoomBinding ===

    @Test
    fun `RoomBinding succeeds with valid parameters`() {
        val binding = RoomBinding(
            roomId = "ROOM1234",
            seat = 0,
            resumeToken = "secret-token",
            snapshot = sampleSnapshot
        )
        assertEquals("ROOM1234", binding.roomId)
        assertEquals(0, binding.seat)
        assertEquals("secret-token", binding.resumeToken)
        assertSame(sampleSnapshot, binding.snapshot)
    }

    @Test
    fun `RoomBinding rejects blank roomId or resumeToken`() {
        assertThrows(IllegalArgumentException::class.java) {
            RoomBinding(roomId = "", seat = 0, resumeToken = "token", snapshot = sampleSnapshot)
        }
        assertThrows(IllegalArgumentException::class.java) {
            RoomBinding(roomId = "   ", seat = 0, resumeToken = "token", snapshot = sampleSnapshot)
        }
        assertThrows(IllegalArgumentException::class.java) {
            RoomBinding(roomId = "ROOM1234", seat = 0, resumeToken = "", snapshot = sampleSnapshot)
        }
        assertThrows(IllegalArgumentException::class.java) {
            RoomBinding(roomId = "ROOM1234", seat = 0, resumeToken = "   ", snapshot = sampleSnapshot)
        }
    }

    @Test
    fun `RoomBinding rejects invalid seat`() {
        assertThrows(IllegalArgumentException::class.java) {
            RoomBinding(roomId = "ROOM1234", seat = -1, resumeToken = "token", snapshot = sampleSnapshot)
        }
        assertThrows(IllegalArgumentException::class.java) {
            RoomBinding(roomId = "ROOM1234", seat = 4, resumeToken = "token", snapshot = sampleSnapshot)
        }
        for (seat in 0..3) {
            val binding = RoomBinding(roomId = "ROOM1234", seat = seat, resumeToken = "token", snapshot = sampleSnapshot)
            assertEquals(seat, binding.seat)
        }
    }

    @Test
    fun `RoomBinding toString masks resumeToken`() {
        val secretToken = "my-private-resume-token-xyz"
        val binding = RoomBinding(
            roomId = "ROOM8888",
            seat = 1,
            resumeToken = secretToken,
            snapshot = sampleSnapshot
        )
        val str = binding.toString()
        assertFalse(str.contains(secretToken), "toString must not expose plaintext resumeToken")
        assertTrue(str.contains("resumeToken=***"), "toString must contain masked resumeToken")
        assertTrue(str.contains("ROOM8888"))
        assertTrue(str.contains("seat=1"))
    }

    @Test
    fun `RoomBinding deepCopy creates isolated snapshot copy`() {
        val binding = RoomBinding(
            roomId = "ROOM1234",
            seat = 2,
            resumeToken = "token-abc",
            snapshot = sampleSnapshot
        )
        val copy = binding.copy(snapshot = binding.snapshot.copy())
        assertEquals(binding.roomId, copy.roomId)
        assertEquals(binding.seat, copy.seat)
        assertEquals(binding.resumeToken, copy.resumeToken)
        assertEquals(binding.snapshot.sessionId, copy.snapshot.sessionId)
        assertNotSame(binding.snapshot, copy.snapshot)
    }

    // === RoomSummary ===

    @Test
    fun `RoomSummary succeeds with valid parameters`() {
        val summary = RoomSummary(
            roomId = "ROOM1234",
            roomName = "雀庄一号",
            hostPlayerName = "HostPlayer",
            playerCount = 1,
            maxPlayers = 4,
            matchStarted = false,
            configSummary = "东风战"
        )
        assertEquals("ROOM1234", summary.roomId)
        assertEquals("雀庄一号", summary.roomName)
        assertEquals("HostPlayer", summary.hostPlayerName)
        assertEquals(1, summary.playerCount)
        assertEquals(4, summary.maxPlayers)
        assertFalse(summary.matchStarted)
        assertEquals("东风战", summary.configSummary)
    }

    @Test
    fun `RoomSummary rejects blank string fields`() {
        assertThrows(IllegalArgumentException::class.java) {
            RoomSummary("", "Name", "Host", 1, 4, false)
        }
        assertThrows(IllegalArgumentException::class.java) {
            RoomSummary("ROOM", "", "Host", 1, 4, false)
        }
        assertThrows(IllegalArgumentException::class.java) {
            RoomSummary("ROOM", "Name", "", 1, 4, false)
        }
    }

    @Test
    fun `RoomSummary rejects invalid maxPlayers and playerCount`() {
        assertThrows(IllegalArgumentException::class.java) {
            RoomSummary("ROOM", "Name", "Host", 1, 0, false)
        }
        assertThrows(IllegalArgumentException::class.java) {
            RoomSummary("ROOM", "Name", "Host", 0, 4, false)
        }
        assertThrows(IllegalArgumentException::class.java) {
            RoomSummary("ROOM", "Name", "Host", 5, 4, false)
        }
    }

    // === ApiErrors & ApiProtocol ===

    @Test
    fun `ApiErrors contains all error codes from specification table`() {
        val expected = setOf(
            "INVALID_PROTOCOL_VERSION",
            "MALFORMED_REQUEST",
            "ALREADY_BOUND",
            "ROOM_NOT_FOUND",
            "ROOM_FULL",
            "SERVER_FULL",
            "MISSING_ROOM_ID",
            "MISSING_ROOM_OR_TOKEN",
            "MISSING_ACTION_CHOICE",
            "MISSING_SHUFFLE_PLAN",
            "UNAUTHENTICATED_SESSION",
            "INVALID_TOKEN",
            "UNKNOWN_REQUEST_TYPE",
            "INTERNAL_ERROR",
            "CONFLICT",
            "SERVER_BUSY"
        )
        assertEquals(expected, ApiErrors.ALL)
    }

    @Test
    fun `ApiProtocol declares expected protocol version and kick reasons`() {
        assertEquals(4, ApiProtocol.PROTOCOL_VERSION)
        assertEquals("DISPLACED_BY_NEW_CONNECTION", ApiProtocol.DISPLACED_BY_NEW_CONNECTION)
        assertEquals("DISCONNECTED_BY_NEW_CONNECTION", ApiProtocol.DISCONNECTED_BY_NEW_CONNECTION)
        assertEquals("ROOM_CLOSED", ApiProtocol.ROOM_CLOSED)
        assertEquals("SERVER_SHUTDOWN", ApiProtocol.SERVER_SHUTDOWN)
        assertEquals("READ_TIMEOUT", ApiProtocol.READ_TIMEOUT)
        assertEquals("HEARTBEAT_TIMEOUT", ApiProtocol.HEARTBEAT_TIMEOUT)
        assertEquals("LOCAL_CLOSED", ApiProtocol.LOCAL_CLOSED)
    }

    // === JoinSpectatorRequest ===

    @Test
    fun `JoinSpectatorRequest succeeds with valid parameters`() {
        val req1 = JoinSpectatorRequest("ROOM1234")
        assertEquals("ROOM1234", req1.roomId)
        assertNull(req1.spectatorName)
        assertEquals(ApiProtocol.PROTOCOL_VERSION, req1.protocolVersion)

        val req2 = JoinSpectatorRequest("ROOM1234", "Watcher1", 4)
        assertEquals("ROOM1234", req2.roomId)
        assertEquals("Watcher1", req2.spectatorName)
        assertEquals(4, req2.protocolVersion)
    }

    @Test
    fun `JoinSpectatorRequest rejects blank roomId or spectatorName`() {
        assertThrows(IllegalArgumentException::class.java) {
            JoinSpectatorRequest("")
        }
        assertThrows(IllegalArgumentException::class.java) {
            JoinSpectatorRequest("   ")
        }
        assertThrows(IllegalArgumentException::class.java) {
            JoinSpectatorRequest("ROOM1", "")
        }
        assertThrows(IllegalArgumentException::class.java) {
            JoinSpectatorRequest("ROOM1", "   ")
        }
    }

    @Test
    fun `JoinSpectatorRequest rejects invalid protocolVersion`() {
        assertThrows(IllegalArgumentException::class.java) {
            JoinSpectatorRequest("ROOM1", protocolVersion = 0)
        }
        assertThrows(IllegalArgumentException::class.java) {
            JoinSpectatorRequest("ROOM1", protocolVersion = -1)
        }
    }

    // === ResumeSpectatorRequest ===

    @Test
    fun `ResumeSpectatorRequest succeeds with valid parameters`() {
        val req1 = ResumeSpectatorRequest("ROOM1234", "spec-1", "token-secret")
        assertEquals("ROOM1234", req1.roomId)
        assertEquals("spec-1", req1.spectatorId)
        assertEquals("token-secret", req1.resumeToken)
        assertNull(req1.checkpoint)
        assertEquals(ApiProtocol.PROTOCOL_VERSION, req1.protocolVersion)

        val cp = PublicCheckpoint("sess-1", "epoch-1", 5L)
        val req2 = ResumeSpectatorRequest("ROOM1234", "spec-1", "token-secret", cp, 4)
        assertEquals(cp, req2.checkpoint)
    }

    @Test
    fun `ResumeSpectatorRequest rejects blank fields and invalid version`() {
        assertThrows(IllegalArgumentException::class.java) {
            ResumeSpectatorRequest("", "spec-1", "token-secret")
        }
        assertThrows(IllegalArgumentException::class.java) {
            ResumeSpectatorRequest("ROOM1", "", "token-secret")
        }
        assertThrows(IllegalArgumentException::class.java) {
            ResumeSpectatorRequest("ROOM1", "spec-1", "")
        }
        assertThrows(IllegalArgumentException::class.java) {
            ResumeSpectatorRequest("ROOM1", "spec-1", "token-secret", protocolVersion = 0)
        }
    }

    @Test
    fun `ResumeSpectatorRequest toString masks resumeToken`() {
        val secretToken = "super-secret-spectator-token"
        val req = ResumeSpectatorRequest("ROOM1", "spec-1", secretToken)
        val str = req.toString()
        assertFalse(str.contains(secretToken), "toString must not expose plaintext resumeToken")
        assertTrue(str.contains("resumeToken=***"))
        assertTrue(str.contains("ROOM1"))
        assertTrue(str.contains("spec-1"))
    }

    // === PublicCheckpoint ===

    @Test
    fun `PublicCheckpoint succeeds with valid parameters`() {
        val cp1 = PublicCheckpoint("sess-1", "epoch-1", 0L)
        assertEquals("sess-1", cp1.sessionId)
        assertEquals("epoch-1", cp1.syncEpoch)
        assertEquals(0L, cp1.cursor)

        val cp2 = PublicCheckpoint("sess-2", "epoch-2", Long.MAX_VALUE)
        assertEquals(Long.MAX_VALUE, cp2.cursor)
    }

    @Test
    fun `PublicCheckpoint rejects blank fields and negative cursor`() {
        assertThrows(IllegalArgumentException::class.java) {
            PublicCheckpoint("", "epoch-1", 0L)
        }
        assertThrows(IllegalArgumentException::class.java) {
            PublicCheckpoint("sess-1", "", 0L)
        }
        assertThrows(IllegalArgumentException::class.java) {
            PublicCheckpoint("sess-1", "epoch-1", -1L)
        }
    }

    // === SpectatorBinding ===

    @Test
    fun `SpectatorBinding succeeds with valid parameters and masks token`() {
        val samplePubSnapshot = FakeServerConnection.createDefaultPublicSnapshot()
        val secretToken = "spectator-token-12345"
        val binding = SpectatorBinding("ROOM1", "spec-1", secretToken, samplePubSnapshot)
        assertEquals("ROOM1", binding.roomId)
        assertEquals("spec-1", binding.spectatorId)
        assertEquals(secretToken, binding.resumeToken)
        assertSame(samplePubSnapshot, binding.snapshot)

        val str = binding.toString()
        assertFalse(str.contains(secretToken), "toString must not expose plaintext resumeToken")
        assertTrue(str.contains("resumeToken=***"))
        assertTrue(str.contains("ROOM1"))
        assertTrue(str.contains("spec-1"))
    }

    @Test
    fun `SpectatorBinding rejects blank fields`() {
        val samplePubSnapshot = FakeServerConnection.createDefaultPublicSnapshot()
        assertThrows(IllegalArgumentException::class.java) {
            SpectatorBinding("", "spec-1", "token", samplePubSnapshot)
        }
        assertThrows(IllegalArgumentException::class.java) {
            SpectatorBinding("ROOM1", "", "token", samplePubSnapshot)
        }
        assertThrows(IllegalArgumentException::class.java) {
            SpectatorBinding("ROOM1", "spec-1", "", samplePubSnapshot)
        }
    }
}
