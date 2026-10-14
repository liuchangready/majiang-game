package com.majiang.client.session

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ApiProtocol
import com.majiang.server.api.ServerApiException
import com.majiang.transport.udp.UdpFrameException
import com.majiang.transport.udp.UdpProtocol
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import java.io.IOException
import java.net.SocketException
import java.net.SocketTimeoutException
import java.util.concurrent.CompletionException
import java.util.concurrent.ExecutionException

class ReconnectErrorClassifierTest {

    @Test
    fun testUnwrapPeelsCompletionAndExecutionExceptions() {
        val root = ServerApiException(ApiErrors.INVALID_TOKEN)
        val wrapped1 = CompletionException(root)
        val wrapped2 = ExecutionException(wrapped1)
        val wrapped3 = CompletionException(wrapped2)

        assertSame(root, ReconnectErrorClassifier.unwrap(wrapped3))
        assertSame(root, ReconnectErrorClassifier.unwrap(root))
        assertNull(ReconnectErrorClassifier.unwrap(null))
    }

    @Test
    fun testTransientIoExceptionsNeverPermanentEvenWithClosedOrInvalidText() {
        val io1 = IOException("Socket closed")
        val io2 = IOException("Connection reset by peer: channel closed")
        val io3 = SocketException("Connection closed")
        val io4 = SocketTimeoutException("Read timed out on closed socket")
        val io5 = IOException("INVALID_PACKET_FORMAT")

        assertFalse(ReconnectErrorClassifier.isPermanent(io1), "Socket closed must be transient")
        assertFalse(ReconnectErrorClassifier.isPermanent(io2), "Channel closed must be transient")
        assertFalse(ReconnectErrorClassifier.isPermanent(io3), "Connection closed must be transient")
        assertFalse(ReconnectErrorClassifier.isPermanent(io4), "Timeout on closed socket must be transient")
        assertFalse(ReconnectErrorClassifier.isPermanent(io5), "INVALID packet IO must be transient")

        // Wrapped in CompletionException
        assertFalse(ReconnectErrorClassifier.isPermanent(CompletionException(io1)))
        assertFalse(ReconnectErrorClassifier.isPermanent(ExecutionException(io2)))
    }

    @Test
    fun testTypedServerApiExceptionPermanentCodesTerminate() {
        val invalidToken = ServerApiException(ApiErrors.INVALID_TOKEN, "INVALID_RESUME_TOKEN")
        val roomNotFound = ServerApiException(ApiErrors.ROOM_NOT_FOUND, "ROOM_CLOSED")
        val alreadyBound = ServerApiException(ApiErrors.ALREADY_BOUND)
        val roomClosed = ServerApiException(ApiProtocol.ROOM_CLOSED)
        val displaced = ServerApiException(ApiProtocol.DISPLACED_BY_NEW_CONNECTION)
        val displacedAlias = ServerApiException(ApiProtocol.DISCONNECTED_BY_NEW_CONNECTION)
        val shutdown = ServerApiException(ApiProtocol.SERVER_SHUTDOWN)
        val localClosed = ServerApiException(ApiProtocol.LOCAL_CLOSED)

        assertTrue(ReconnectErrorClassifier.isPermanent(invalidToken))
        assertTrue(ReconnectErrorClassifier.isPermanent(roomNotFound))
        assertTrue(ReconnectErrorClassifier.isPermanent(alreadyBound))
        assertTrue(ReconnectErrorClassifier.isPermanent(roomClosed))
        assertTrue(ReconnectErrorClassifier.isPermanent(displaced))
        assertTrue(ReconnectErrorClassifier.isPermanent(displacedAlias))
        assertTrue(ReconnectErrorClassifier.isPermanent(shutdown))
        assertTrue(ReconnectErrorClassifier.isPermanent(localClosed))

        // Wrapped
        assertTrue(ReconnectErrorClassifier.isPermanent(CompletionException(invalidToken)))
        assertTrue(ReconnectErrorClassifier.isPermanent(ExecutionException(CompletionException(roomNotFound))))
    }

    @Test
    fun testTypedServerApiExceptionTransientCodesRetry() {
        val busy = ServerApiException(ApiErrors.SERVER_BUSY)
        val internalErr = ServerApiException(ApiErrors.INTERNAL_ERROR, "Channel closed unexpectedly")
        val conflict = ServerApiException(ApiErrors.CONFLICT)

        assertFalse(ReconnectErrorClassifier.isPermanent(busy), "SERVER_BUSY must retry")
        assertFalse(ReconnectErrorClassifier.isPermanent(internalErr), "INTERNAL_ERROR even with closed text must retry")
        assertFalse(ReconnectErrorClassifier.isPermanent(conflict), "CONFLICT must retry")
    }

    @Test
    fun testUdpFrameExceptionClassificationReadsReasonCode() {
        val timeout = UdpFrameException(UdpProtocol.REASON_REQUEST_TIMEOUT, "Request timed out")
        val frameCorrupt = UdpFrameException("CRC_MISMATCH", "Corrupted frame")
        val permToken = UdpFrameException(ApiErrors.INVALID_TOKEN, "Token rejected")
        val permNotFound = UdpFrameException(ApiErrors.ROOM_NOT_FOUND, "Room missing")

        assertFalse(ReconnectErrorClassifier.isPermanent(timeout), "UDP timeout is transient")
        assertFalse(ReconnectErrorClassifier.isPermanent(frameCorrupt), "CRC mismatch is transient")
        assertTrue(ReconnectErrorClassifier.isPermanent(permToken), "Explicit INVALID_TOKEN reasonCode is permanent")
        assertTrue(ReconnectErrorClassifier.isPermanent(permNotFound), "Explicit ROOM_NOT_FOUND reasonCode is permanent")
    }

    @Test
    fun testUnknownExceptionsDoNotTriggerPermanentBySubstringContains() {
        val ex1 = RuntimeException("Temporary failure: connection was CLOSED by remote host")
        val ex2 = IllegalStateException("Something failed with INVALID state")
        val ex3 = RuntimeException("Random unexpected error")

        assertFalse(ReconnectErrorClassifier.isPermanent(ex1), "Substring contains CLOSED must NOT terminate")
        assertFalse(ReconnectErrorClassifier.isPermanent(ex2), "Substring contains INVALID must NOT terminate")
        assertFalse(ReconnectErrorClassifier.isPermanent(ex3), "Unknown exception must NOT terminate")
    }

    @Test
    fun testExactPermanentReasonsMatchOnlyExactKeywords() {
        val exExact1 = IllegalStateException("INVALID_RESUME_TOKEN")
        val exExact2 = IllegalStateException("ROOM_CLOSED")
        val exExact3 = IllegalStateException("INVALID_TOKEN")

        assertTrue(ReconnectErrorClassifier.isPermanent(exExact1))
        assertTrue(ReconnectErrorClassifier.isPermanent(exExact2))
        assertTrue(ReconnectErrorClassifier.isPermanent(exExact3))
    }

    @Test
    fun testExtractReason() {
        assertEquals("INVALID_TOKEN", ReconnectErrorClassifier.extractReason(ServerApiException(ApiErrors.INVALID_TOKEN)))
        assertEquals("REQUEST_TIMEOUT", ReconnectErrorClassifier.extractReason(UdpFrameException(UdpProtocol.REASON_REQUEST_TIMEOUT, "Timeout")))
        assertEquals("Socket failed", ReconnectErrorClassifier.extractReason(IOException("Socket failed")))
        assertEquals("RESUME_FAILED", ReconnectErrorClassifier.extractReason(null))
        assertEquals("RESUME_FAILED", ReconnectErrorClassifier.extractReason(RuntimeException("   ")))
    }
}
