package com.majiang.transport.tcp

import com.google.gson.Gson
import com.google.gson.GsonBuilder
import com.google.gson.JsonDeserializationContext
import com.google.gson.JsonDeserializer
import com.google.gson.JsonElement
import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.google.gson.JsonParser
import com.google.gson.JsonSerializationContext
import com.google.gson.JsonSerializer
import com.majiang.transport.api.ApiMessageCodec
import com.majiang.transport.api.ApiRequest
import com.majiang.transport.api.ApiResponse
import com.majiang.transport.api.ProtocolPlayerUpdate
import com.majiang.transport.api.ProtocolPublicUpdate
import com.majiang.transport.api.ProtocolRoomInfo
import com.majiang.transport.api.checkUnknownFields
import com.majiang.transport.api.validateStrictInt
import com.majiang.transport.api.validateStrictLong
import com.majiang.transport.api.validateStrictString
import java.lang.reflect.Type
import java.security.SecureRandom
import java.util.Random

// ============================================================================
// 1. DTO 定义 (字段按 D5/D6/D7/D9 冻结，不得增删改名)
// ============================================================================

/**
 * 客户端到服务端：握手发起消息 (HELLO, type=1)
 */
data class TcpHello(
    val wireVersion: Int = TcpWire.WIRE_VERSION,
    val businessVersion: Int = TcpWire.BUSINESS_VERSION,
    val lobbyResumeToken: String? = null
) {
    init {
        require(wireVersion >= 1) { "wireVersion must be >= 1: $wireVersion" }
        require(businessVersion >= 1) { "businessVersion must be >= 1: $businessVersion" }
        if (lobbyResumeToken != null) {
            require(lobbyResumeToken.isNotBlank()) { "lobbyResumeToken cannot be blank" }
        }
    }
}

/**
 * 服务端颁发的作用域凭据
 */
data class TcpScope(
    val scope: String,
    val expiresAtMillis: Long
) {
    init {
        require(scope.isNotBlank()) { "scope cannot be blank" }
        require(expiresAtMillis >= 0L) { "expiresAtMillis must be >= 0: $expiresAtMillis" }
    }
}

/**
 * 服务端通知客户端的传输预算与超时约束 (D6 冻结字段)
 */
data class TcpLimits(
    val maxInFlightRequests: Int = 64,
    val requestTimeoutMillis: Long = 10000L,
    val heartbeatIntervalMillis: Long = 3000L,
    val idleDeadMillis: Long = 10000L
) {
    init {
        require(maxInFlightRequests > 0) { "maxInFlightRequests must be > 0: $maxInFlightRequests" }
        require(requestTimeoutMillis > 0L) { "requestTimeoutMillis must be > 0: $requestTimeoutMillis" }
        require(heartbeatIntervalMillis > 0L) { "heartbeatIntervalMillis must be > 0: $heartbeatIntervalMillis" }
        require(idleDeadMillis > 0L) { "idleDeadMillis must be > 0: $idleDeadMillis" }
    }
}

/**
 * 服务端到客户端：握手就绪应答消息 (WELCOME, type=2, D6 冻结字段)
 */
data class TcpWelcome(
    val wireVersion: Int = TcpWire.WIRE_VERSION,
    val businessVersion: Int = TcpWire.BUSINESS_VERSION,
    val connectionEpoch: String,
    val lobbyResumeToken: String,
    val lobbyScope: TcpScope,
    val limits: TcpLimits = TcpLimits()
) {
    init {
        require(wireVersion >= 1) { "wireVersion must be >= 1: $wireVersion" }
        require(businessVersion >= 1) { "businessVersion must be >= 1: $businessVersion" }
        require(connectionEpoch.isNotBlank()) { "connectionEpoch cannot be blank" }
        require(lobbyResumeToken.isNotBlank()) { "lobbyResumeToken cannot be blank" }
    }
}

/**
 * 客户端到服务端：业务请求信封 (REQ, type=3, D7 冻结字段)
 */
data class TcpRequestEnvelope(
    val requestScope: String,
    val requestId: String,
    val request: ApiRequest
) {
    init {
        require(requestScope.isNotBlank()) { "requestScope cannot be blank" }
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
    }

    /**
     * 校验外层 requestId 与内层 request.requestId 是否严格一致
     */
    fun isRequestIdConsistent(): Boolean = requestId == request.requestId

    /**
     * 校验一致性；若不一致则抛出 PROTOCOL_VIOLATION 异常（供 T2 连接层直接判定）
     */
    fun validateRequestIdConsistency() {
        if (!isRequestIdConsistent()) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Envelope requestId '$requestId' does not match inner request.requestId '${request.requestId}'"
            )
        }
    }
}

/**
 * 服务端到客户端：业务响应信封 (RSP, type=4, D7 冻结字段)
 */
data class TcpResponseEnvelope(
    val requestScope: String,
    val requestId: String,
    val response: ApiResponse,
    val grantedScope: TcpScope? = null
) {
    init {
        require(requestScope.isNotBlank()) { "requestScope cannot be blank" }
        require(requestId.isNotBlank()) { "requestId cannot be blank" }
    }
}

/**
 * PING 心跳探测消息 (type=8, D5 冻结字段)
 */
data class TcpPing(val nonce: String) {
    init {
        TcpNonce.validate(nonce)
    }
}

/**
 * PONG 心跳回显消息 (type=9, D5 冻结字段)
 */
data class TcpPong(val nonce: String) {
    init {
        TcpNonce.validate(nonce)
    }
}

/**
 * 连接关闭通知消息 (CLOSE, type=10, D4 冻结字段)
 */
data class TcpClose(
    val reason: String,
    val message: String? = null
) {
    init {
        require(reason.isNotBlank()) { "reason cannot be blank" }
    }
}

// ============================================================================
// 2. Nonce 编解码与校验工具 (D5 冻结契约)
// ============================================================================

/**
 * PING / PONG 8 字节 (16 hex 字符) nonce 工具 (D5)
 */
object TcpNonce {
    private const val HEX_CHARS = "0123456789abcdef"

    /**
     * 判断 nonce 是否符合严格 16 位 16 进制字符规范
     */
    fun isValid(nonce: String): Boolean {
        if (nonce.length != 16) return false
        for (ch in nonce) {
            val isHex = (ch in '0'..'9') || (ch in 'a'..'f') || (ch in 'A'..'F')
            if (!isHex) return false
        }
        return true
    }

    /**
     * 校验 nonce，若非法当场抛出 [TcpProtocolException] (PROTOCOL_VIOLATION)
     */
    fun validate(nonce: String) {
        if (!isValid(nonce)) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Invalid nonce: '$nonce'; must be exactly 16 hex characters (8 bytes)"
            )
        }
    }

    /**
     * 生成一个随机的 16 位小写 hex 字符串 (8 字节随机数)
     */
    fun generate(random: Random = SecureRandom()): String {
        val bytes = ByteArray(8)
        random.nextBytes(bytes)
        val sb = java.lang.StringBuilder(16)
        for (b in bytes) {
            val v = b.toInt() and 0xFF
            sb.append(HEX_CHARS[v ushr 4])
            sb.append(HEX_CHARS[v and 0x0F])
        }
        return sb.toString()
    }
}

// ============================================================================
// 3. 严格 TypeAdapters (严格未知字段检查、严格数据类型)
// ============================================================================

private class TcpHelloAdapter : JsonSerializer<TcpHello>, JsonDeserializer<TcpHello> {
    override fun serialize(src: TcpHello, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("wireVersion", src.wireVersion)
        obj.addProperty("businessVersion", src.businessVersion)
        if (src.lobbyResumeToken != null) {
            obj.addProperty("lobbyResumeToken", src.lobbyResumeToken)
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpHello {
        if (!json.isJsonObject) throw JsonParseException("TcpHello must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("wireVersion", "businessVersion", "lobbyResumeToken"), "TcpHello")

        val wireVersionElem = obj.get("wireVersion") ?: throw JsonParseException("TcpHello missing wireVersion")
        val wireVersion = validateStrictInt(wireVersionElem, "TcpHello.wireVersion")

        val businessVersionElem = obj.get("businessVersion") ?: throw JsonParseException("TcpHello missing businessVersion")
        val businessVersion = validateStrictInt(businessVersionElem, "TcpHello.businessVersion")

        val lobbyResumeToken = if (obj.has("lobbyResumeToken") && !obj.get("lobbyResumeToken").isJsonNull) {
            val token = validateStrictString(obj.get("lobbyResumeToken"), "TcpHello.lobbyResumeToken")
            if (token.isBlank()) throw JsonParseException("TcpHello.lobbyResumeToken cannot be blank")
            token
        } else null

        return try {
            TcpHello(wireVersion, businessVersion, lobbyResumeToken)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpHello: ${e.message}", e)
        }
    }
}

private class TcpScopeAdapter : JsonSerializer<TcpScope>, JsonDeserializer<TcpScope> {
    override fun serialize(src: TcpScope, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("scope", src.scope)
        obj.addProperty("expiresAtMillis", src.expiresAtMillis)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpScope {
        if (!json.isJsonObject) throw JsonParseException("TcpScope must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("scope", "expiresAtMillis"), "TcpScope")

        val scopeElem = obj.get("scope") ?: throw JsonParseException("TcpScope missing scope")
        val scope = validateStrictString(scopeElem, "TcpScope.scope")

        val expiresElem = obj.get("expiresAtMillis") ?: throw JsonParseException("TcpScope missing expiresAtMillis")
        val expiresAtMillis = validateStrictLong(expiresElem, "TcpScope.expiresAtMillis")

        return try {
            TcpScope(scope, expiresAtMillis)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpScope: ${e.message}", e)
        }
    }
}

private class TcpLimitsAdapter : JsonSerializer<TcpLimits>, JsonDeserializer<TcpLimits> {
    override fun serialize(src: TcpLimits, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("maxInFlightRequests", src.maxInFlightRequests)
        obj.addProperty("requestTimeoutMillis", src.requestTimeoutMillis)
        obj.addProperty("heartbeatIntervalMillis", src.heartbeatIntervalMillis)
        obj.addProperty("idleDeadMillis", src.idleDeadMillis)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpLimits {
        if (!json.isJsonObject) throw JsonParseException("TcpLimits must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("maxInFlightRequests", "requestTimeoutMillis", "heartbeatIntervalMillis", "idleDeadMillis"), "TcpLimits")

        val maxInFlightElem = obj.get("maxInFlightRequests") ?: throw JsonParseException("TcpLimits missing maxInFlightRequests")
        val maxInFlightRequests = validateStrictInt(maxInFlightElem, "TcpLimits.maxInFlightRequests")

        val reqTimeoutElem = obj.get("requestTimeoutMillis") ?: throw JsonParseException("TcpLimits missing requestTimeoutMillis")
        val requestTimeoutMillis = validateStrictLong(reqTimeoutElem, "TcpLimits.requestTimeoutMillis")

        val heartbeatElem = obj.get("heartbeatIntervalMillis") ?: throw JsonParseException("TcpLimits missing heartbeatIntervalMillis")
        val heartbeatIntervalMillis = validateStrictLong(heartbeatElem, "TcpLimits.heartbeatIntervalMillis")

        val idleDeadElem = obj.get("idleDeadMillis") ?: throw JsonParseException("TcpLimits missing idleDeadMillis")
        val idleDeadMillis = validateStrictLong(idleDeadElem, "TcpLimits.idleDeadMillis")

        return try {
            TcpLimits(maxInFlightRequests, requestTimeoutMillis, heartbeatIntervalMillis, idleDeadMillis)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpLimits: ${e.message}", e)
        }
    }
}

private class TcpWelcomeAdapter : JsonSerializer<TcpWelcome>, JsonDeserializer<TcpWelcome> {
    override fun serialize(src: TcpWelcome, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("wireVersion", src.wireVersion)
        obj.addProperty("businessVersion", src.businessVersion)
        obj.addProperty("connectionEpoch", src.connectionEpoch)
        obj.addProperty("lobbyResumeToken", src.lobbyResumeToken)
        obj.add("lobbyScope", context.serialize(src.lobbyScope, TcpScope::class.java))
        obj.add("limits", context.serialize(src.limits, TcpLimits::class.java))
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpWelcome {
        if (!json.isJsonObject) throw JsonParseException("TcpWelcome must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("wireVersion", "businessVersion", "connectionEpoch", "lobbyResumeToken", "lobbyScope", "limits"), "TcpWelcome")

        val wireVersionElem = obj.get("wireVersion") ?: throw JsonParseException("TcpWelcome missing wireVersion")
        val wireVersion = validateStrictInt(wireVersionElem, "TcpWelcome.wireVersion")

        val businessVersionElem = obj.get("businessVersion") ?: throw JsonParseException("TcpWelcome missing businessVersion")
        val businessVersion = validateStrictInt(businessVersionElem, "TcpWelcome.businessVersion")

        val connEpochElem = obj.get("connectionEpoch") ?: throw JsonParseException("TcpWelcome missing connectionEpoch")
        val connectionEpoch = validateStrictString(connEpochElem, "TcpWelcome.connectionEpoch")

        val tokenElem = obj.get("lobbyResumeToken") ?: throw JsonParseException("TcpWelcome missing lobbyResumeToken")
        val lobbyResumeToken = validateStrictString(tokenElem, "TcpWelcome.lobbyResumeToken")

        val scopeElem = obj.get("lobbyScope") ?: throw JsonParseException("TcpWelcome missing lobbyScope")
        val lobbyScope = context.deserialize<TcpScope>(scopeElem, TcpScope::class.java)
            ?: throw JsonParseException("Failed to parse lobbyScope in TcpWelcome")

        val limitsElem = obj.get("limits") ?: throw JsonParseException("TcpWelcome missing limits")
        val limits = context.deserialize<TcpLimits>(limitsElem, TcpLimits::class.java)
            ?: throw JsonParseException("Failed to parse limits in TcpWelcome")

        return try {
            TcpWelcome(wireVersion, businessVersion, connectionEpoch, lobbyResumeToken, lobbyScope, limits)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpWelcome: ${e.message}", e)
        }
    }
}

private class TcpRequestEnvelopeAdapter : JsonSerializer<TcpRequestEnvelope>, JsonDeserializer<TcpRequestEnvelope> {
    override fun serialize(src: TcpRequestEnvelope, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("requestScope", src.requestScope)
        obj.addProperty("requestId", src.requestId)
        obj.add("request", ApiMessageCodec.gson.toJsonTree(src.request))
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpRequestEnvelope {
        if (!json.isJsonObject) throw JsonParseException("TcpRequestEnvelope must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("requestScope", "requestId", "request"), "TcpRequestEnvelope")

        val scopeElem = obj.get("requestScope") ?: throw JsonParseException("TcpRequestEnvelope missing requestScope")
        val requestScope = validateStrictString(scopeElem, "TcpRequestEnvelope.requestScope")

        val idElem = obj.get("requestId") ?: throw JsonParseException("TcpRequestEnvelope missing requestId")
        val requestId = validateStrictString(idElem, "TcpRequestEnvelope.requestId")

        val requestElem = obj.get("request") ?: throw JsonParseException("TcpRequestEnvelope missing request")
        if (!requestElem.isJsonObject) throw JsonParseException("TcpRequestEnvelope.request must be a JSON object")

        val request = try {
            ApiMessageCodec.decodeRequest(requestElem.toString())
        } catch (e: JsonParseException) {
            throw e
        } catch (e: Exception) {
            throw JsonParseException("Failed to parse ApiRequest in envelope: ${e.message}", e)
        }

        val envelope = try {
            TcpRequestEnvelope(requestScope, requestId, request)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpRequestEnvelope: ${e.message}", e)
        }

        return envelope
    }
}

private class TcpResponseEnvelopeAdapter : JsonSerializer<TcpResponseEnvelope>, JsonDeserializer<TcpResponseEnvelope> {
    override fun serialize(src: TcpResponseEnvelope, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("requestScope", src.requestScope)
        obj.addProperty("requestId", src.requestId)
        obj.add("response", ApiMessageCodec.gson.toJsonTree(src.response))
        if (src.grantedScope != null) {
            obj.add("grantedScope", context.serialize(src.grantedScope, TcpScope::class.java))
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpResponseEnvelope {
        if (!json.isJsonObject) throw JsonParseException("TcpResponseEnvelope must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("requestScope", "requestId", "response", "grantedScope"), "TcpResponseEnvelope")

        val scopeElem = obj.get("requestScope") ?: throw JsonParseException("TcpResponseEnvelope missing requestScope")
        val requestScope = validateStrictString(scopeElem, "TcpResponseEnvelope.requestScope")

        val idElem = obj.get("requestId") ?: throw JsonParseException("TcpResponseEnvelope missing requestId")
        val requestId = validateStrictString(idElem, "TcpResponseEnvelope.requestId")

        val responseElem = obj.get("response") ?: throw JsonParseException("TcpResponseEnvelope missing response")
        if (!responseElem.isJsonObject) throw JsonParseException("TcpResponseEnvelope.response must be a JSON object")

        val response = try {
            ApiMessageCodec.decodeResponse(responseElem.toString())
        } catch (e: JsonParseException) {
            throw e
        } catch (e: Exception) {
            throw JsonParseException("Failed to parse ApiResponse in envelope: ${e.message}", e)
        }

        val grantedScope = if (obj.has("grantedScope") && !obj.get("grantedScope").isJsonNull) {
            context.deserialize<TcpScope>(obj.get("grantedScope"), TcpScope::class.java)
        } else null

        return try {
            TcpResponseEnvelope(requestScope, requestId, response, grantedScope)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpResponseEnvelope: ${e.message}", e)
        }
    }
}

private class TcpPingAdapter : JsonSerializer<TcpPing>, JsonDeserializer<TcpPing> {
    override fun serialize(src: TcpPing, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("nonce", src.nonce)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpPing {
        if (!json.isJsonObject) throw JsonParseException("TcpPing must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("nonce"), "TcpPing")

        val nonceElem = obj.get("nonce") ?: throw JsonParseException("TcpPing missing nonce")
        val nonce = validateStrictString(nonceElem, "TcpPing.nonce")

        return try {
            TcpPing(nonce)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpPing: ${e.message}", e)
        }
    }
}

private class TcpPongAdapter : JsonSerializer<TcpPong>, JsonDeserializer<TcpPong> {
    override fun serialize(src: TcpPong, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("nonce", src.nonce)
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpPong {
        if (!json.isJsonObject) throw JsonParseException("TcpPong must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("nonce"), "TcpPong")

        val nonceElem = obj.get("nonce") ?: throw JsonParseException("TcpPong missing nonce")
        val nonce = validateStrictString(nonceElem, "TcpPong.nonce")

        return try {
            TcpPong(nonce)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpPong: ${e.message}", e)
        }
    }
}

private class TcpCloseAdapter : JsonSerializer<TcpClose>, JsonDeserializer<TcpClose> {
    override fun serialize(src: TcpClose, typeOfSrc: Type, context: JsonSerializationContext): JsonElement {
        val obj = JsonObject()
        obj.addProperty("reason", src.reason)
        if (src.message != null) {
            obj.addProperty("message", src.message)
        }
        return obj
    }

    override fun deserialize(json: JsonElement, typeOfT: Type, context: JsonDeserializationContext): TcpClose {
        if (!json.isJsonObject) throw JsonParseException("TcpClose must be a JSON object: $json")
        val obj = json.asJsonObject
        checkUnknownFields(obj, setOf("reason", "message"), "TcpClose")

        val reasonElem = obj.get("reason") ?: throw JsonParseException("TcpClose missing reason")
        val reason = validateStrictString(reasonElem, "TcpClose.reason")

        val message = if (obj.has("message") && !obj.get("message").isJsonNull) {
            validateStrictString(obj.get("message"), "TcpClose.message")
        } else null

        return try {
            TcpClose(reason, message)
        } catch (e: Exception) {
            throw JsonParseException("Invalid TcpClose: ${e.message}", e)
        }
    }
}

// ============================================================================
// 4. 统一编解码门面 (TcpMessageCodec)
// ============================================================================

/**
 * TCP 协议消息与信封严格编解码器 (TcpMessageCodec)
 *
 * 采用独立 Gson 实例注册显式 TypeAdapter，严格禁止未知字段，杜绝运行时类名反射分发。
 * 提供 DTO <-> JSON 及 DTO <-> TcpFrame 的完整双向编解码能力。
 */
object TcpMessageCodec {

    val gson: Gson = GsonBuilder()
        .registerTypeAdapter(TcpHello::class.java, TcpHelloAdapter())
        .registerTypeAdapter(TcpScope::class.java, TcpScopeAdapter())
        .registerTypeAdapter(TcpLimits::class.java, TcpLimitsAdapter())
        .registerTypeAdapter(TcpWelcome::class.java, TcpWelcomeAdapter())
        .registerTypeAdapter(TcpRequestEnvelope::class.java, TcpRequestEnvelopeAdapter())
        .registerTypeAdapter(TcpResponseEnvelope::class.java, TcpResponseEnvelopeAdapter())
        .registerTypeAdapter(TcpPing::class.java, TcpPingAdapter())
        .registerTypeAdapter(TcpPong::class.java, TcpPongAdapter())
        .registerTypeAdapter(TcpClose::class.java, TcpCloseAdapter())
        .create()

    // --- JSON 编解码 ---

    fun encodeHello(hello: TcpHello): String = gson.toJson(hello)
    fun decodeHello(json: String): TcpHello = parseJson(json, TcpHello::class.java, "TcpHello")

    fun encodeWelcome(welcome: TcpWelcome): String = gson.toJson(welcome)
    fun decodeWelcome(json: String): TcpWelcome = parseJson(json, TcpWelcome::class.java, "TcpWelcome")

    fun encodeRequestEnvelope(envelope: TcpRequestEnvelope): String = gson.toJson(envelope)
    fun decodeRequestEnvelope(json: String): TcpRequestEnvelope {
        val env = parseJson(json, TcpRequestEnvelope::class.java, "TcpRequestEnvelope")
        env.validateRequestIdConsistency()
        return env
    }

    fun encodeResponseEnvelope(envelope: TcpResponseEnvelope): String = gson.toJson(envelope)
    fun decodeResponseEnvelope(json: String): TcpResponseEnvelope =
        parseJson(json, TcpResponseEnvelope::class.java, "TcpResponseEnvelope")

    fun encodePing(ping: TcpPing): String = gson.toJson(ping)
    fun decodePing(json: String): TcpPing = parseJson(json, TcpPing::class.java, "TcpPing")

    fun encodePong(pong: TcpPong): String = gson.toJson(pong)
    fun decodePong(json: String): TcpPong = parseJson(json, TcpPong::class.java, "TcpPong")

    fun encodeClose(close: TcpClose): String = gson.toJson(close)
    fun decodeClose(json: String): TcpClose = parseJson(json, TcpClose::class.java, "TcpClose")

    // --- 帧转换 (DTO -> TcpFrame) ---

    fun toFrame(hello: TcpHello): TcpFrame =
        TcpFrame(TcpWire.FRAME_TYPE_HELLO, encodeHello(hello).toByteArray(Charsets.UTF_8))

    fun toFrame(welcome: TcpWelcome): TcpFrame =
        TcpFrame(TcpWire.FRAME_TYPE_WELCOME, encodeWelcome(welcome).toByteArray(Charsets.UTF_8))

    fun toFrame(envelope: TcpRequestEnvelope): TcpFrame {
        envelope.validateRequestIdConsistency()
        return TcpFrame(TcpWire.FRAME_TYPE_REQ, encodeRequestEnvelope(envelope).toByteArray(Charsets.UTF_8))
    }

    fun toFrame(envelope: TcpResponseEnvelope): TcpFrame =
        TcpFrame(TcpWire.FRAME_TYPE_RSP, encodeResponseEnvelope(envelope).toByteArray(Charsets.UTF_8))

    fun toFrame(ping: TcpPing): TcpFrame =
        TcpFrame(TcpWire.FRAME_TYPE_PING, encodePing(ping).toByteArray(Charsets.UTF_8))

    fun toFrame(pong: TcpPong): TcpFrame =
        TcpFrame(TcpWire.FRAME_TYPE_PONG, encodePong(pong).toByteArray(Charsets.UTF_8))

    fun toFrame(close: TcpClose): TcpFrame =
        TcpFrame(TcpWire.FRAME_TYPE_CLOSE, encodeClose(close).toByteArray(Charsets.UTF_8))

    fun toPlayerUpdateFrame(update: ProtocolPlayerUpdate, isFullSnapshot: Boolean = false): TcpFrame {
        val json = ApiMessageCodec.encodePush(update, isFullSnapshot = isFullSnapshot)
        return TcpFrame(TcpWire.FRAME_TYPE_PLAYER_UPDATE, json.toByteArray(Charsets.UTF_8))
    }

    fun toPublicUpdateFrame(update: ProtocolPublicUpdate): TcpFrame {
        val json = ApiMessageCodec.encodePublicUpdatePush(update)
        return TcpFrame(TcpWire.FRAME_TYPE_PUBLIC_UPDATE, json.toByteArray(Charsets.UTF_8))
    }

    fun toRoomUpdateFrame(roomInfo: ProtocolRoomInfo): TcpFrame {
        val json = ApiMessageCodec.gson.toJson(roomInfo)
        return TcpFrame(TcpWire.FRAME_TYPE_ROOM_UPDATE, json.toByteArray(Charsets.UTF_8))
    }

    // --- 帧解析 (TcpFrame -> DTO) ---

    fun parseHello(frame: TcpFrame): TcpHello {
        requireFrameType(frame, TcpWire.FRAME_TYPE_HELLO)
        return decodeHello(frame.payloadAsString())
    }

    fun parseWelcome(frame: TcpFrame): TcpWelcome {
        requireFrameType(frame, TcpWire.FRAME_TYPE_WELCOME)
        return decodeWelcome(frame.payloadAsString())
    }

    fun parseRequestEnvelope(frame: TcpFrame): TcpRequestEnvelope {
        requireFrameType(frame, TcpWire.FRAME_TYPE_REQ)
        return decodeRequestEnvelope(frame.payloadAsString())
    }

    fun parseResponseEnvelope(frame: TcpFrame): TcpResponseEnvelope {
        requireFrameType(frame, TcpWire.FRAME_TYPE_RSP)
        return decodeResponseEnvelope(frame.payloadAsString())
    }

    fun parsePing(frame: TcpFrame): TcpPing {
        requireFrameType(frame, TcpWire.FRAME_TYPE_PING)
        return decodePing(frame.payloadAsString())
    }

    fun parsePong(frame: TcpFrame): TcpPong {
        requireFrameType(frame, TcpWire.FRAME_TYPE_PONG)
        return decodePong(frame.payloadAsString())
    }

    fun parseClose(frame: TcpFrame): TcpClose {
        requireFrameType(frame, TcpWire.FRAME_TYPE_CLOSE)
        return decodeClose(frame.payloadAsString())
    }

    fun parsePlayerUpdate(frame: TcpFrame): ProtocolPlayerUpdate {
        requireFrameType(frame, TcpWire.FRAME_TYPE_PLAYER_UPDATE)
        return try {
            ApiMessageCodec.decodePush(frame.payloadAsString())
        } catch (e: Exception) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Failed to parse ProtocolPlayerUpdate from frame: ${e.message}",
                e
            )
        }
    }

    fun parsePublicUpdate(frame: TcpFrame): ProtocolPublicUpdate {
        requireFrameType(frame, TcpWire.FRAME_TYPE_PUBLIC_UPDATE)
        return try {
            ApiMessageCodec.decodePublicUpdatePush(frame.payloadAsString())
        } catch (e: Exception) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Failed to parse ProtocolPublicUpdate from frame: ${e.message}",
                e
            )
        }
    }

    fun parseRoomUpdate(frame: TcpFrame): ProtocolRoomInfo {
        requireFrameType(frame, TcpWire.FRAME_TYPE_ROOM_UPDATE)
        return try {
            val elem = JsonParser.parseString(frame.payloadAsString())
            if (!elem.isJsonObject) throw JsonParseException("ProtocolRoomInfo must be a JSON object")
            ApiMessageCodec.gson.fromJson(elem, ProtocolRoomInfo::class.java)
                ?: throw JsonParseException("empty roomInfo payload")
        } catch (e: Exception) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Failed to parse ProtocolRoomInfo from frame: ${e.message}",
                e
            )
        }
    }

    // --- 内部辅助方法 ---

    private fun requireFrameType(frame: TcpFrame, expectedType: Int) {
        if (frame.type != expectedType) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Frame type mismatch: expected ${TcpWire.frameTypeName(expectedType)}($expectedType), got ${TcpWire.frameTypeName(frame.type)}(${frame.type})"
            )
        }
    }

    private fun <T> parseJson(json: String, clazz: Class<T>, typeName: String): T {
        val elem = try {
            JsonParser.parseString(json)
        } catch (e: Exception) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Malformed JSON for $typeName: ${e.message}",
                e
            )
        }
        if (!elem.isJsonObject) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "$typeName payload must be a JSON object"
            )
        }
        return try {
            gson.fromJson(elem, clazz)
                ?: throw TcpProtocolException(
                    TcpCloseReasons.PROTOCOL_VIOLATION,
                    "Empty payload for $typeName"
                )
        } catch (e: TcpProtocolException) {
            throw e
        } catch (e: JsonParseException) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Strict validation failed for $typeName: ${e.message}",
                e
            )
        } catch (e: Exception) {
            throw TcpProtocolException(
                TcpCloseReasons.PROTOCOL_VIOLATION,
                "Mapping error for $typeName: ${e.message}",
                e
            )
        }
    }
}
