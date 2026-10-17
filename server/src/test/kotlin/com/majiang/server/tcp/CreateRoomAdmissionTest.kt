package com.majiang.server.tcp

import com.majiang.server.room.RoomCapacityPolicy
import com.majiang.server.room.RoomRegistry
import com.majiang.transport.api.ProtocolRoomLifecycle
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.Callable
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicLong

/**
 * 未绑定建房准入与原子预留测试 (CreateRoomAdmissionTest)
 *
 * 覆盖验收规范：
 * - R06-T6: 单 IP 配额（同 IP 第 4 个未开局建房被拒 QuotaExceeded，开局或关闭后名额释放）
 * - R06-T7: 多 IP 耗尽全局预算（到达 registry.maxRooms 边界时精确拦截，不产生半创建房间）
 * - R06-T8: 共享 NAT 语义（同 IP 不同 principal 共享 IP 预算，任一达限即拒）
 * - R06-T9: 并发预留一致性（高并发预留不超额，未 commit 的 release 路径名额立即可用）
 * - R06-T12: 循环建房与回收后，各级配额与记账精确回归基线
 * - R06-T14: 全局建房速率预算（滑动窗口超频得 RateLimited，时间滑动后恢复允许）
 */
@Timeout(15)
class CreateRoomAdmissionTest {

    @Test
    fun `R06-T6 single IP quota of 3 unstarted rooms and quota release`() {
        val registry = RoomRegistry()
        val policy = RoomCapacityPolicy(maxUnstartedRoomsPerIp = 3, maxUnstartedRoomsPerPrincipal = 10)
        val admission = CreateRoomAdmission(registry, policy)

        val ip = "192.168.1.100"
        val principal = "lobby:user-1"

        // 1. 成功建 3 个未开局房间
        val r1 = admission.tryReserve(principal, ip)
        assertNotNull(r1)
        val room1 = registry.createRoom()
        r1!!.commit(room1.roomId)

        val r2 = admission.tryReserve(principal, ip)
        assertNotNull(r2)
        val room2 = registry.createRoom()
        r2!!.commit(room2.roomId)

        val r3 = admission.tryReserve(principal, ip)
        assertNotNull(r3)
        val room3 = registry.createRoom()
        r3!!.commit(room3.roomId)

        assertEquals(3, admission.unstartedRoomsForIp(ip))

        // 2. 第 4 个被配额拒绝 (QuotaExceeded)
        val outcome4 = admission.tryReserveOutcome(principal, ip)
        assertTrue(outcome4 is CreateRoomAdmission.Outcome.QuotaExceeded)
        assertNull(admission.tryReserve(principal, ip))

        // 3. 房间 1 关闭 -> 名额释放
        room1.close()
        assertEquals(2, admission.unstartedRoomsForIp(ip))

        // 4. 现在可以再建 1 个
        val r4 = admission.tryReserve(principal, ip)
        assertNotNull(r4)
        val room4 = registry.createRoom()
        r4!!.commit(room4.roomId)
        assertEquals(3, admission.unstartedRoomsForIp(ip))

        // 5. 房间 2 开赛 (脱离 LOBBY 态) -> 名额释放
        // 模拟通过 join + ready + startMatch 使生命周期变为 PLAYING
        room2.setReady(0, true).get()
        room2.startMatch(0).get()
        assertTrue(room2.lifecycle != ProtocolRoomLifecycle.LOBBY)

        assertEquals(2, admission.unstartedRoomsForIp(ip))

        val r5 = admission.tryReserve(principal, ip)
        assertNotNull(r5)
        r5!!.release()
    }

    @Test
    fun `R06-T7 multiple IPs exhaust global budget and registry count remains exact`() {
        // 设置全局最大房间数为 5
        val registry = RoomRegistry(maxRooms = 5)
        val policy = RoomCapacityPolicy(maxUnstartedRoomsPerIp = 10, maxUnstartedRoomsPerPrincipal = 10)
        val admission = CreateRoomAdmission(registry, policy)

        val rooms = mutableListOf<com.majiang.server.room.SharedRoom>()
        for (i in 1..5) {
            val ip = "10.0.0.$i"
            val res = admission.tryReserve("lobby:$i", ip)
            assertNotNull(res, "Reservation $i must succeed")
            val room = registry.createRoom()
            res!!.commit(room.roomId)
            rooms.add(room)
        }

        assertEquals(5, registry.count())
        assertEquals(5, admission.committedRoomCount)

        // 第 6 个建房由新 IP 发起，由于全局 5 满额必须被拒绝
        val outcome6 = admission.tryReserveOutcome("lobby:6", "10.0.0.6")
        assertTrue(outcome6 is CreateRoomAdmission.Outcome.QuotaExceeded)

        // 注册表数量保持精确为 5，未产生半创建对象
        assertEquals(5, registry.count())

        // 关闭 1 个房间后，新建房允许
        rooms[0].close()
        registry.removeRoom(rooms[0].roomId)
        admission.reconcile(registry)

        val res7 = admission.tryReserve("lobby:7", "10.0.0.7")
        assertNotNull(res7)
        res7!!.release()
    }

    @Test
    fun `R06-T8 shared NAT policy checks both IP and principal limits`() {
        val registry = RoomRegistry()
        val policy = RoomCapacityPolicy(
            maxUnstartedRoomsPerIp = 3,
            maxUnstartedRoomsPerPrincipal = 3
        )
        val admission = CreateRoomAdmission(registry, policy)

        val sharedIp = "203.0.113.1"
        val principalA = "lobby:client-A"
        val principalB = "lobby:client-B"

        // 客户端 A 建 2 个房间
        val resA1 = admission.tryReserve(principalA, sharedIp)!!
        val roomA1 = registry.createRoom()
        resA1.commit(roomA1.roomId)

        val resA2 = admission.tryReserve(principalA, sharedIp)!!
        val roomA2 = registry.createRoom()
        resA2.commit(roomA2.roomId)

        assertEquals(2, admission.unstartedRoomsForPrincipal(principalA))
        assertEquals(2, admission.unstartedRoomsForIp(sharedIp))

        // 客户端 B 在同 IP 下建 1 个房间
        val resB1 = admission.tryReserve(principalB, sharedIp)!!
        val roomB1 = registry.createRoom()
        resB1.commit(roomB1.roomId)

        assertEquals(1, admission.unstartedRoomsForPrincipal(principalB))
        // 此时同 IP 下已达到 3 个
        assertEquals(3, admission.unstartedRoomsForIp(sharedIp))

        // 客户端 B 此时名下仅 1 个房间，但因共享 IP 额度已达 3，再次建房必须被拒绝
        val outcomeB2 = admission.tryReserveOutcome(principalB, sharedIp)
        assertTrue(outcomeB2 is CreateRoomAdmission.Outcome.QuotaExceeded)
    }

    @Test
    fun `R06-T9 concurrent reservation and release without commit`() {
        val registry = RoomRegistry(maxRooms = 10)
        val policy = RoomCapacityPolicy(
            maxUnstartedRoomsPerIp = 20,
            maxUnstartedRoomsPerPrincipal = 20,
            globalCreateRoomRatePerSecond = 1000 // 调高限速以专测容量并发
        )
        val admission = CreateRoomAdmission(registry, policy)

        val threadCount = 20
        val pool = Executors.newFixedThreadPool(threadCount)
        try {
            val tasks = (1..threadCount).map { i ->
                Callable {
                    admission.tryReserve("lobby:$i", "192.168.0.$i")
                }
            }

            val futures = pool.invokeAll(tasks)
            val successfulReservations = futures.mapNotNull { it.get() }

            // 全局 maxRooms = 10，因此即使 20 个线程并发发起，最多也只有 10 个预留成功
            assertEquals(10, successfulReservations.size)
            assertEquals(10, admission.activeReservationCount)

            // 全部 release 而不 commit
            for (res in successfulReservations) {
                val released = res.release()
                assertTrue(released)
                // 重复 release 为无操作 (精确一次)
                assertFalse(res.release())
            }

            assertEquals(0, admission.activeReservationCount)

            // release 后配额应完全恢复可用
            val retryRes = admission.tryReserve("lobby:new", "192.168.0.254")
            assertNotNull(retryRes)
            retryRes!!.release()
        } finally {
            pool.shutdownNow()
        }
    }

    @Test
    fun `R06-T12 cyclic create and recycle returns accounting to baseline`() {
        val registry = RoomRegistry(maxRooms = 50)
        val policy = RoomCapacityPolicy(
            maxUnstartedRoomsPerIp = 10,
            maxUnstartedRoomsPerPrincipal = 10,
            globalCreateRoomRatePerSecond = 500
        )
        val admission = CreateRoomAdmission(registry, policy)

        val ip = "172.16.0.1"
        val principal = "lobby:cycler"

        // 执行 5 轮建房 -> 回收
        for (round in 1..5) {
            val reservations = mutableListOf<CreateRoomAdmission.Reservation>()
            val createdRooms = mutableListOf<com.majiang.server.room.SharedRoom>()

            for (i in 1..5) {
                val res = admission.tryReserve(principal, ip)
                assertNotNull(res)
                val room = registry.createRoom()
                res!!.commit(room.roomId)
                createdRooms.add(room)
            }

            assertEquals(5, registry.count())
            assertEquals(5, admission.committedRoomCount)
            assertEquals(5, admission.unstartedRoomsForIp(ip))

            // 模拟回收所有房间
            for (room in createdRooms) {
                room.close()
                registry.removeRoom(room.roomId)
                admission.onRoomClosed(room.roomId)
            }
            admission.reconcile(registry)

            // 验证每轮结束后全部计数精准归零回到基线
            assertEquals(0, registry.count())
            assertEquals(0, admission.committedRoomCount)
            assertEquals(0, admission.activeReservationCount)
            assertEquals(0, admission.unstartedRoomsForIp(ip))
            assertEquals(0, admission.unstartedRoomsForPrincipal(principal))
        }
    }

    @Test
    fun `R06-T14 sliding window rate limiter returns RateLimited and recovers`() {
        val currentTime = AtomicLong(10_000L)
        val registry = RoomRegistry()
        val policy = RoomCapacityPolicy(globalCreateRoomRatePerSecond = 3)
        val admission = CreateRoomAdmission(
            registry = registry,
            capacityPolicy = policy,
            clockMillis = { currentTime.get() }
        )

        // 1秒内前 3 次成功
        val r1 = admission.tryReserveOutcome("u1", "1.1.1.1")
        assertTrue(r1 is CreateRoomAdmission.Outcome.Accepted)

        val r2 = admission.tryReserveOutcome("u2", "1.1.1.2")
        assertTrue(r2 is CreateRoomAdmission.Outcome.Accepted)

        val r3 = admission.tryReserveOutcome("u3", "1.1.1.3")
        assertTrue(r3 is CreateRoomAdmission.Outcome.Accepted)

        // 同一秒内第 4 次被限速拒绝 (RateLimited)
        val r4 = admission.tryReserveOutcome("u4", "1.1.1.4")
        assertTrue(r4 is CreateRoomAdmission.Outcome.RateLimited)

        // 滑动窗口推进 1001 毫秒
        currentTime.addAndGet(1001L)

        // 窗口滑动后恢复允许建房
        val r5 = admission.tryReserveOutcome("u5", "1.1.1.5")
        assertTrue(r5 is CreateRoomAdmission.Outcome.Accepted)
    }
}
