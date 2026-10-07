package com.majiang.v4lobby

import com.majiang.client.transport.udp.UdpClient
import com.majiang.server.api.JoinSpectatorRequest
import com.majiang.server.api.PublicCheckpoint
import com.majiang.server.api.ResumeSpectatorRequest
import com.majiang.server.api.SpectatorBinding
import com.majiang.server.udp.UdpTableServer
import com.majiang.transport.api.ProtocolPublicUpdate
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CountDownLatch
import java.util.concurrent.ExecutionException
import java.util.concurrent.TimeUnit
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import kotlin.test.assertFalse
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue
import kotlin.test.assertFailsWith

/** Actual UDP binding completions held through a public SharedRoom notification hook. */
@Timeout(35)
class V4SpectatorUdpBindingRaceTest {
    private fun <T> CompletableFuture<T>.await(): T = get(5, TimeUnit.SECONDS)

    private fun connect(server: UdpTableServer) = UdpClient.connect(
        "127.0.0.1", server.boundPort, closeSchedulerOnShutdown = true
    ).await()

    private fun SpectatorBinding.resumeRequest() = ResumeSpectatorRequest(
        roomId, spectatorId, resumeToken,
        PublicCheckpoint(snapshot.sessionId, snapshot.syncEpoch, snapshot.cursor)
    )

    /**
     * Host shutdown interrupts its completion workers. Retain this deliberately slow
     * observer until the test releases it, so shutdown cannot accidentally release
     * the pending transport installation before the closed-state assertion.
     */
    private class CompletionGate : AutoCloseable {
        val entered = CountDownLatch(1)
        private val released = CountDownLatch(1)

        fun hold() {
            entered.countDown()
            var interrupted = false
            try {
                while (true) {
                    try {
                        released.await()
                        return
                    } catch (_: InterruptedException) {
                        interrupted = true
                    }
                }
            } finally {
                if (interrupted) Thread.currentThread().interrupt()
            }
        }

        override fun close() {
            released.countDown()
        }
    }

    @Test fun lastPublicUpdateDuringResumeInstallationArrivesWithoutAnotherGameAction() {
        UdpTableServer().use { server ->
            server.start()
            val room = server.createRoom("binding-race-host")
            connect(server).use { original ->
                val binding = original.joinSpectator(JoinSpectatorRequest(room.roomId)).await()
                connect(server).use { replacement ->
                    val recoveredPausedView = CompletableFuture<ProtocolPublicUpdate>()
                    replacement.onPublicUpdate { update ->
                        if (update.snapshot.paused && update.snapshot.cursor > binding.snapshot.cursor) {
                            recoveredPausedView.complete(update)
                        }
                    }.use {
                        CompletionGate().use { gate ->
                            room.sharedRoom.onSpectatorDisplaced { id, _ ->
                                if (id == binding.spectatorId) gate.hold()
                            }.use {
                                val pending = replacement.resumeSpectator(binding.resumeRequest())
                                assertTrue(gate.entered.await(5, TimeUnit.SECONDS), "Resume must reach the completion gate")
                                assertFalse(pending.isDone, "The binding reply must still be pending")

                                // Registered after UdpRoom's PUBLIC subscription: this callback is
                                // a dispatcher barrier proving the room already saw the last update.
                                val publishedPause = CompletableFuture<ProtocolPublicUpdate>()
                                room.sharedRoom.subscribePublic { update ->
                                    if (update.snapshot.paused) publishedPause.complete(update)
                                }.use {
                                    assertTrue(room.tableHost.pause().await())
                                    val finalUpdate = publishedPause.await()
                                    val published = assertNotNull(room.getPublishedPublicSnapshot())
                                    assertTrue(published.cursor >= finalUpdate.snapshot.cursor)
                                    assertTrue(finalUpdate.snapshot.cursor > binding.snapshot.cursor)

                                    gate.close()
                                    val resumed = pending.await()
                                    // A refreshed binding reply or a reliable full PUBLIC push may
                                    // install the baseline. No snapshot query or extra action repairs it.
                                    if (!resumed.snapshot.paused || resumed.snapshot.cursor < finalUpdate.snapshot.cursor) {
                                        val recovered = recoveredPausedView.await()
                                        assertTrue(recovered.snapshot.cursor >= finalUpdate.snapshot.cursor)
                                    }
                                    assertTrue(room.isSpectatorBindingCurrent(
                                        assertNotNull(room.getSpectatorSession(binding.spectatorId))
                                    ))
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    @Test fun roomCloseBeforeResumeCompletionRejectsLateUdpBindingInstallation() {
        UdpTableServer().use { server ->
            server.start()
            val room = server.createRoom("closing-race-host")
            connect(server).use { original ->
                val binding = original.joinSpectator(JoinSpectatorRequest(room.roomId)).await()
                connect(server).use { replacement ->
                    CompletionGate().use { gate ->
                        room.sharedRoom.onSpectatorDisplaced { id, _ ->
                            if (id == binding.spectatorId) gate.hold()
                        }.use {
                            val pending = replacement.resumeSpectator(binding.resumeRequest())
                            assertTrue(gate.entered.await(5, TimeUnit.SECONDS), "Resume must reach the completion gate")
                            assertFalse(pending.isDone)

                            room.close()
                            assertTrue(room.closed)
                            assertNull(room.getSpectatorSession(binding.spectatorId))
                            gate.close()

                            // A timeout is a failure: the live UDP server must send a rejection,
                            // not silently orphan the request after its room has terminated.
                            assertFailsWith<ExecutionException> { pending.await() }
                            assertNull(room.getSpectatorSession(binding.spectatorId),
                                "A delayed completion must not resurrect the room's spectator binding")
                            assertFalse(room.sharedRoom.spectators.containsKey(binding.spectatorId))
                        }
                    }
                }
            }
        }
    }
}
