package com.majiang.transport.api

import com.google.gson.JsonDeserializationContext
import com.google.gson.JsonDeserializer
import com.google.gson.JsonElement
import com.google.gson.JsonParseException
import java.lang.reflect.Type

/**
 * D-07 v5 结构化算分元素的严格 JsonDeserializer。
 *
 * 严格性边界：
 * - 未知字段、错误 JSON 类型、越界数值、超长/空白码一律拒绝；
 * - `code` / `kind` / `payerRole` 的“未知取值”是冻结契约允许的向前兼容扩展点，
 *   解析不得失败；如何回退由映射/展示层决定；
 * - 集合大小有硬上限，避免损坏载荷放大内存。
 */
internal class ProtocolYakuEntryAdapter : JsonDeserializer<ProtocolYakuEntry> {
    override fun deserialize(
        json: JsonElement,
        typeOfT: Type,
        context: JsonDeserializationContext
    ): ProtocolYakuEntry {
        if (!json.isJsonObject) {
            throw JsonParseException("ProtocolYakuEntry must be a JSON object")
        }
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("code", "displayName", "han"),
            "ProtocolYakuEntry"
        )
        val code = validateStrictString(
            obj.get("code") ?: throw JsonParseException("Missing code in ProtocolYakuEntry"),
            "ProtocolYakuEntry.code"
        )
        if (code.isBlank()) throw JsonParseException("ProtocolYakuEntry.code cannot be blank")
        if (code.length > ProtocolScoreCodes.MAX_CODE_LENGTH) {
            throw JsonParseException("ProtocolYakuEntry.code too long: ${code.length}")
        }
        val displayName = validateStrictString(
            obj.get("displayName") ?: throw JsonParseException("Missing displayName in ProtocolYakuEntry"),
            "ProtocolYakuEntry.displayName"
        )
        if (displayName.isBlank()) {
            throw JsonParseException("ProtocolYakuEntry.displayName cannot be blank")
        }
        if (displayName.length > ProtocolScoreCodes.MAX_DISPLAY_NAME_LENGTH) {
            throw JsonParseException("ProtocolYakuEntry.displayName too long: ${displayName.length}")
        }
        val han = validateStrictInt(
            obj.get("han") ?: throw JsonParseException("Missing han in ProtocolYakuEntry"),
            "ProtocolYakuEntry.han"
        )
        if (han !in 0..ProtocolScoreCodes.MAX_YAKU_HAN) {
            throw JsonParseException("ProtocolYakuEntry.han out of range: $han")
        }
        return ProtocolYakuEntry(code, displayName, han)
    }
}

/**
 * 单付款方金额的严格解析。未知 `payerRole` 允许通过，交由客户端决定是否回退。
 */
internal class ProtocolPaymentComponentAdapter : JsonDeserializer<ProtocolPaymentComponent> {
    override fun deserialize(
        json: JsonElement,
        typeOfT: Type,
        context: JsonDeserializationContext
    ): ProtocolPaymentComponent {
        if (!json.isJsonObject) {
            throw JsonParseException("ProtocolPaymentComponent must be a JSON object")
        }
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("payerRole", "amount", "count"),
            "ProtocolPaymentComponent"
        )
        val payerRole = validateStrictString(
            obj.get("payerRole") ?: throw JsonParseException("Missing payerRole in ProtocolPaymentComponent"),
            "ProtocolPaymentComponent.payerRole"
        )
        if (payerRole.isBlank()) {
            throw JsonParseException("ProtocolPaymentComponent.payerRole cannot be blank")
        }
        if (payerRole.length > ProtocolScoreCodes.MAX_CODE_LENGTH) {
            throw JsonParseException("ProtocolPaymentComponent.payerRole too long: ${payerRole.length}")
        }
        val amount = validateStrictInt(
            obj.get("amount") ?: throw JsonParseException("Missing amount in ProtocolPaymentComponent"),
            "ProtocolPaymentComponent.amount"
        )
        if (amount !in 0..ProtocolScoreCodes.MAX_PAYMENT_AMOUNT) {
            throw JsonParseException("ProtocolPaymentComponent.amount out of range: $amount")
        }
        val count = validateStrictInt(
            obj.get("count") ?: throw JsonParseException("Missing count in ProtocolPaymentComponent"),
            "ProtocolPaymentComponent.count"
        )
        if (count !in 1..ProtocolScoreCodes.MAX_PAYMENT_COUNT) {
            throw JsonParseException("ProtocolPaymentComponent.count out of range: $count")
        }
        return ProtocolPaymentComponent(payerRole, amount, count)
    }
}

/**
 * 基础支付计划严格解析。
 *
 * 结构不变量（非空、总额守恒）在这里只做“可用性”判断，不抛异常：不可用的计划
 * 由 `ProtocolScoreBreakdownAdapter` 丢弃为 null，调用方回退旧 `paymentLines`，
 * 不因可扩展字段损坏整条结算快照。未知 `kind` 同样保留原值，供客户端显式回退。
 */
internal class ProtocolPaymentPlanAdapter : JsonDeserializer<ProtocolPaymentPlan> {
    override fun deserialize(
        json: JsonElement,
        typeOfT: Type,
        context: JsonDeserializationContext
    ): ProtocolPaymentPlan {
        if (!json.isJsonObject) {
            throw JsonParseException("ProtocolPaymentPlan must be a JSON object")
        }
        val obj = json.asJsonObject
        checkUnknownFields(
            obj,
            setOf("kind", "components", "total"),
            "ProtocolPaymentPlan"
        )
        val kind = validateStrictString(
            obj.get("kind") ?: throw JsonParseException("Missing kind in ProtocolPaymentPlan"),
            "ProtocolPaymentPlan.kind"
        )
        if (kind.isBlank()) throw JsonParseException("ProtocolPaymentPlan.kind cannot be blank")
        if (kind.length > ProtocolScoreCodes.MAX_CODE_LENGTH) {
            throw JsonParseException("ProtocolPaymentPlan.kind too long: ${kind.length}")
        }
        val componentsElem = obj.get("components")
            ?: throw JsonParseException("Missing components in ProtocolPaymentPlan")
        if (!componentsElem.isJsonArray) {
            throw JsonParseException("ProtocolPaymentPlan.components must be an array")
        }
        val arr = componentsElem.asJsonArray
        if (arr.size() > ProtocolScoreCodes.MAX_PAYMENT_COMPONENTS) {
            throw JsonParseException("ProtocolPaymentPlan.components too many: ${arr.size()}")
        }
        val components = ArrayList<ProtocolPaymentComponent>(arr.size())
        for (i in 0 until arr.size()) {
            components.add(
                context.deserialize<ProtocolPaymentComponent>(
                    arr.get(i),
                    ProtocolPaymentComponent::class.java
                )
            )
        }
        val total = validateStrictInt(
            obj.get("total") ?: throw JsonParseException("Missing total in ProtocolPaymentPlan"),
            "ProtocolPaymentPlan.total"
        )
        if (total !in 0..ProtocolScoreCodes.MAX_PAYMENT_TOTAL) {
            throw JsonParseException("ProtocolPaymentPlan.total out of range: $total")
        }
        return ProtocolPaymentPlan(kind, components, total)
    }
}
