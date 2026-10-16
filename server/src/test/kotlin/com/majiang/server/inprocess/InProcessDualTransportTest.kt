package com.majiang.server.inprocess

import com.majiang.server.api.ApiErrors
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiTags
import com.majiang.transport.tcp.TcpRequestEnvelope
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.util.concurrent.TimeUnit

/**
 * 进程内单机模式双传输幂等对齐测试 (R14 / F12)
 *
 * 验证 InProcessBinding 接入 RequestScopeReceiptStore，
 * 使得单机直连具备与 TcpTableServer 完全等价的业务幂等、同键冲突、
 * 作用域获取、回执重放与错误语义。
 */
@Timeout(15)
class InProcessDualTransportTest {

    @Test
    fun `R14 inprocess binding integrates receipt store and performs idempotent replay`() {
        val server = InProcessServer(isSingleplayer = true)
        val binding = server.createConnection() as InProcessBinding

        try {
            val scope = binding.getRequestScope().get(2, TimeUnit.SECONDS)
            assertNotNull(scope)

            val req = ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "req-list-1")
            val env = TcpRequestEnvelope(scope.scope, req.requestId, req)

            // 第一次执行
            val rsp1 = binding.executeRequest(env).get(2, TimeUnit.SECONDS)
            assertTrue(rsp1.response.success)

            // 第二次同键同载荷执行：命中幂等回执重放
            val rsp2 = binding.executeRequest(env).get(2, TimeUnit.SECONDS)
            assertTrue(rsp2.response.success)
            assertEquals(rsp1.response.rooms?.size, rsp2.response.rooms?.size)
        } finally {
            binding.close()
            server.close()
        }
    }

    @Test
    fun `R14 inprocess binding detects duplicate key payload conflict`() {
        val server = InProcessServer(isSingleplayer = true)
        val binding = server.createConnection() as InProcessBinding

        try {
            val scope = binding.getRequestScope().get(2, TimeUnit.SECONDS)

            val req1 = ApiRequest(api = ApiTags.SET_READY, requestId = "ready-conflict-1", ready = true)
            val env1 = TcpRequestEnvelope(scope.scope, req1.requestId, req1)
            val rsp1 = binding.executeRequest(env1).get(2, TimeUnit.SECONDS)

            // 同键不同载荷（SET_READY 改为 false，或不同 API）
            val req2 = ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "ready-conflict-1")
            val env2 = TcpRequestEnvelope(scope.scope, req2.requestId, req2)
            val rsp2 = binding.executeRequest(env2).get(2, TimeUnit.SECONDS)

            assertFalse(rsp2.response.success)
            assertEquals(ApiErrors.CONFLICT, rsp2.response.error, "Duplicate requestId with conflicting payload must return CONFLICT")
        } finally {
            binding.close()
            server.close()
        }
    }

    @Test
    fun `R14 inprocess binding handles GET_REQUEST_SCOPE and rate limiting`() {
        val server = InProcessServer(isSingleplayer = true)
        val binding = server.createConnection() as InProcessBinding

        try {
            val scopeReq = ApiRequest(api = "GET_REQUEST_SCOPE", requestId = "scope-1")
            val env1 = TcpRequestEnvelope("initial-scope", scopeReq.requestId, scopeReq)
            val rsp1 = binding.executeRequest(env1).get(2, TimeUnit.SECONDS)
            assertTrue(rsp1.response.success)
            assertNotNull(rsp1.grantedScope)

            // 紧接着再次请求（超速）
            val scopeReq2 = ApiRequest(api = "GET_REQUEST_SCOPE", requestId = "scope-2")
            val env2 = TcpRequestEnvelope("initial-scope", scopeReq2.requestId, scopeReq2)
            val rsp2 = binding.executeRequest(env2).get(2, TimeUnit.SECONDS)
            assertFalse(rsp2.response.success)
            assertEquals(ApiErrors.SERVER_BUSY, rsp2.response.error)
        } finally {
            binding.close()
            server.close()
        }
    }

    @Test
    fun `R14 inprocess binding explicitly rejects malformed payload with MALFORMED_REQUEST without polluting receipt store`() {
        val server = InProcessServer(isSingleplayer = true)
        val binding = server.createConnection() as InProcessBinding

        try {
            val scope = binding.getRequestScope().get(2, TimeUnit.SECONDS)
            assertNotNull(scope)

            // 构造必定导致 ApiMessageCodec.encodeRequest 抛出异常的真实请求对象
            val unsafeField = sun.misc.Unsafe::class.java.getDeclaredField("theUnsafe").apply { isAccessible = true }
            val unsafe = unsafeField.get(null) as sun.misc.Unsafe
            val malformedReq = ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "malformed-req-1")
            val roomConfigField = ApiRequest::class.java.getDeclaredField("roomConfig")
            val offset = unsafe.objectFieldOffset(roomConfigField)
            unsafe.putObject(malformedReq, offset, malformedReq)

            // 验证 ApiMessageCodec.encodeRequest 确实会抛出异常 (ClassCastException / Exception)
            assertThrows(Throwable::class.java) {
                com.majiang.transport.api.ApiMessageCodec.encodeRequest(malformedReq)
            }

            // 发起请求：生产代码捕获编码异常并立即返回 MALFORMED_REQUEST，且不准入 receiptStore
            val envMalformed = TcpRequestEnvelope(scope.scope, malformedReq.requestId, malformedReq)
            val rspMalformed = binding.executeRequest(envMalformed).get(2, TimeUnit.SECONDS)
            assertFalse(rspMalformed.response.success)
            assertEquals(ApiErrors.MALFORMED_REQUEST, rspMalformed.response.error)

            // 关键断言：回执存储未被该 requestId 污染，使用相同 requestId 发起合法请求不报 CONFLICT
            val validReq = ApiRequest(api = ApiTags.LIST_ROOMS, requestId = "malformed-req-1")
            val envValid = TcpRequestEnvelope(scope.scope, validReq.requestId, validReq)
            val rspValid = binding.executeRequest(envValid).get(2, TimeUnit.SECONDS)
            assertTrue(rspValid.response.success, "Subsequent valid request with same requestId must succeed because store was not polluted")
        } finally {
            binding.close()
            server.close()
        }
    }

    @Test
    fun `R14 inprocess binding detects conflict for same api with different valid payloads`() {
        val server = InProcessServer(isSingleplayer = true)
        val binding = server.createConnection() as InProcessBinding

        try {
            val scope = binding.getRequestScope().get(2, TimeUnit.SECONDS)

            val req1 = ApiRequest(api = ApiTags.SET_READY, requestId = "same-api-conflict-1", ready = true)
            val env1 = TcpRequestEnvelope(scope.scope, req1.requestId, req1)
            val rsp1 = binding.executeRequest(env1).get(2, TimeUnit.SECONDS)
            assertNotNull(rsp1)

            // 相同 API，不同载荷（ready = false）
            val req2 = ApiRequest(api = ApiTags.SET_READY, requestId = "same-api-conflict-1", ready = false)
            val env2 = TcpRequestEnvelope(scope.scope, req2.requestId, req2)
            val rsp2 = binding.executeRequest(env2).get(2, TimeUnit.SECONDS)

            assertFalse(rsp2.response.success)
            assertEquals(ApiErrors.CONFLICT, rsp2.response.error, "Same API with conflicting payload must return CONFLICT")
        } finally {
            binding.close()
            server.close()
        }
    }
}
