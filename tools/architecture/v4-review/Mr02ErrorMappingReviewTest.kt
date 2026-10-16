package com.majiang.review

import com.majiang.server.api.ApiErrors
import com.majiang.server.api.ServerApiException
import com.majiang.server.api.CreateRoomRequest
import com.majiang.server.inprocess.InProcessServer
import com.majiang.server.udp.UdpRoom
import com.majiang.transport.udp.UdpFrameException
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Test
import java.io.IOException
import java.util.concurrent.CompletionException
import java.util.concurrent.ExecutionException
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/** Independent MR-02 contract probes. Failures must be fixed, not relaxed. */
class Mr02ErrorMappingReviewTest {
    @Test fun structuredCodeWinsOverMisleadingMessageThroughNestedWrappers() {
        val failure = ServerApiException(ApiErrors.SERVER_BUSY, "INVALID_TOKEN ROOM_CLOSED")
        assertEquals(ApiErrors.SERVER_BUSY, UdpRoom.mapError(CompletionException(ExecutionException(failure))))
    }

    @Test fun frameReasonSurvivesNestedWrappers() {
        assertEquals("REQUEST_TIMEOUT", UdpRoom.mapError(ExecutionException(
            CompletionException(UdpFrameException("REQUEST_TIMEOUT", "INVALID_TOKEN")))))
    }

    @Test fun ioFailureCannotBecomePermanentTokenRejectionFromItsMessage() {
        assertEquals(ApiErrors.INTERNAL_ERROR, UdpRoom.mapError(CompletionException(
            IOException("temporary upstream failure while checking INVALID_TOKEN"))))
    }

    @Test fun unknownFailureCannotBecomeMissingRoomFromSubstring() {
        assertEquals(ApiErrors.INTERNAL_ERROR, UdpRoom.mapError(ExecutionException(
            IllegalStateException("temporary failure while processing ROOM_CLOSED notification"))))
    }

    @Test fun exactLegacyBusinessReasonRemainsSupported() {
        assertEquals(ApiErrors.INVALID_TOKEN, UdpRoom.mapError(IllegalStateException("INVALID_RESUME_TOKEN")))
        assertEquals(ApiErrors.ROOM_NOT_FOUND, UdpRoom.mapError(IllegalStateException("ROOM_CLOSED")))
    }

    @Test fun inProcessQueuedSnapshotRetainsTypedStaleSessionCodeAfterClose() {
        val server = InProcessServer(isSingleplayer = false)
        val release = CountDownLatch(1)
        try {
            val connection = server.createConnection()
            val binding = connection.createRoom(CreateRoomRequest("Reviewer")).get(3, TimeUnit.SECONDS)
            val room = server.registry.getRoom(binding.roomId)!!
            val entered = CountDownLatch(1)
            val blocker = room.tableHost.enqueueOperation {
                entered.countDown()
                check(release.await(5, TimeUnit.SECONDS))
            }
            assertTrue(entered.await(3, TimeUnit.SECONDS))
            val pending = connection.snapshot()
            connection.close()
            release.countDown()
            blocker.get(3, TimeUnit.SECONDS)
            val failure = assertThrows(ExecutionException::class.java) { pending.get(3, TimeUnit.SECONDS) }
            val root = ServerApiException.unwrap(failure)
            assertTrue(root is ServerApiException, "Public in-process API leaked ${root?.javaClass?.name}: ${root?.message}")
            assertEquals(ApiErrors.INVALID_TOKEN, (root as ServerApiException).code)
        } finally {
            release.countDown()
            server.close()
        }
    }
}
