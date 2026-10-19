package com.majiang.transport.api

import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.google.gson.JsonParseException
import com.majiang.server.api.FakeServerConnection
import com.majiang.transport.tcp.TcpMessageCodec
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

/**
 * D-07 v5 结构化算分严格编解码与安全回退矩阵。
 *
 * 严格性：未知字段、错误类型、越界/超限、非整数均拒绝整条载荷。
 * 向前兼容：稳定码“未知取值”不拒绝；结构不一致（条目错位、支付总额不守恒）
 * 降级为旧字符串等待消费方回退，不把可扩展结构问题当成整条快照损坏。
 */
class StructuredScoreCodecTest {

    private fun baseBreakdown(): ProtocolScoreBreakdown {
        val tile = ProtocolTile.fromTileId(0)
        return ProtocolScoreBreakdown(
            yakuLines = listOf("平和" to 1, "断幺九" to 1),
            yakumanCount = 0,
            dora = ProtocolDoraBreakdown(
                indicatorCount = 0, uraCount = 0, redCount = 0, totalHan = 0,
                indicatorTiles = emptyList(), uraTiles = emptyList(), redTiles = emptyList(),
                indicatorHits = emptyList(), uraHits = emptyList()
            ),
            fu = ProtocolFuBreakdown(
                lines = listOf(ProtocolFuLine("底符", 30)),
                subtotal = 30, roundedUpTo = 30, finalFu = 30, specialNote = null
            ),
            score = ProtocolScoreResult(
                fu = 30, han = 2, basePoints = 2000, title = "満貫",
                isYakuman = false, yakumanMultiplier = 0, rawBasePoints = 2000,
                capReason = "満貫封頂", formulaText = "30符 × 2^(2+2) = 2000",
                rawBasePointsExact = "2000", capCode = "MANGAN"
            ),
            paymentLines = listOf("荣和：P3 支付 8000"),
            honbaAddition = 0, riichiAddition = 0, totalPoints = 8000,
            uraEligible = false, isDealer = false, isTsumo = false,
            yakuEntries = listOf(
                ProtocolYakuEntry("PINFU", "平和", 1),
                ProtocolYakuEntry("TANYAO", "断幺九", 1)
            ),
            paymentPlan = ProtocolPaymentPlan(
                "NON_DEALER_RON",
                listOf(ProtocolPaymentComponent("DISCARDER", 8000, 1)),
                8000
            )
        )
    }

    private fun baseJson(): JsonObject =
        ApiMessageCodec.gson.toJsonTree(baseBreakdown()).asJsonObject

    private fun scoreJson(): JsonObject =
        baseJson().getAsJsonObject("score")

    private fun decodeBreakdown(json: JsonObject): ProtocolScoreBreakdown =
        ApiMessageCodec.gson.fromJson(json, ProtocolScoreBreakdown::class.java)

    private fun decodeScore(json: JsonObject): ProtocolScoreResult =
        ApiMessageCodec.gson.fromJson(json, ProtocolScoreResult::class.java)

    private fun assertRejected(block: () -> Unit, message: String) {
        assertThrows(JsonParseException::class.java, block, message)
    }

    @Test
    fun `score result capCode is optional but strictly typed and bounded`() {
        val without = scoreJson().apply { remove(ProtocolScoreCodes.FIELD_CAP_CODE) }
        assertNull(decodeScore(without).capCode)

        val unknown = scoreJson().apply { addProperty(ProtocolScoreCodes.FIELD_CAP_CODE, "FUTURE_CAP") }
        assertEquals("FUTURE_CAP", decodeScore(unknown).capCode)

        assertRejected({
            decodeScore(scoreJson().apply { addProperty(ProtocolScoreCodes.FIELD_CAP_CODE, 7) })
        }, "capCode must be a string")
        assertRejected({
            decodeScore(scoreJson().apply { addProperty(ProtocolScoreCodes.FIELD_CAP_CODE, " ") })
        }, "blank capCode must be rejected")
        assertRejected({
            decodeScore(scoreJson().apply {
                addProperty(ProtocolScoreCodes.FIELD_CAP_CODE, "X".repeat(ProtocolScoreCodes.MAX_CODE_LENGTH + 1))
            })
        }, "oversized capCode must be rejected")
    }

    @Test
    fun `yakuEntries rejects wrong types bounds and oversized collections`() {
        assertRejected({
            decodeBreakdown(baseJson().apply { add(ProtocolScoreCodes.FIELD_YAKU_ENTRIES, JsonObject()) })
        }, "yakuEntries must be an array")

        assertRejected({
            decodeBreakdown(baseJson().apply { addProperty(ProtocolScoreCodes.FIELD_YAKU_ENTRIES, "x") })
        }, "yakuEntries must not be a primitive")

        fun withEntry(mutator: JsonObject.() -> Unit): JsonObject {
            val json = baseJson()
            val entry = (json.getAsJsonArray(ProtocolScoreCodes.FIELD_YAKU_ENTRIES).get(0)).asJsonObject
            entry.mutator()
            return json
        }

        assertRejected({ decodeBreakdown(withEntry { addProperty("code", 1) }) }, "code must be string")
        assertRejected({ decodeBreakdown(withEntry { addProperty("displayName", 1) }) }, "displayName must be string")
        assertRejected({ decodeBreakdown(withEntry { addProperty("han", "1") }) }, "han must be int")
        assertRejected({ decodeBreakdown(withEntry { addProperty("han", -1) }) }, "negative han")
        assertRejected({
            decodeBreakdown(withEntry { addProperty("han", ProtocolScoreCodes.MAX_YAKU_HAN + 1) })
        }, "han above limit")
        assertRejected({ decodeBreakdown(withEntry { addProperty("code", " ") }) }, "blank code")
        assertRejected({
            decodeBreakdown(withEntry { addProperty("code", "X".repeat(ProtocolScoreCodes.MAX_CODE_LENGTH + 1)) })
        }, "oversized code")
        assertRejected({ decodeBreakdown(withEntry { remove("displayName") }) }, "missing displayName")

        assertRejected({
            val json = baseJson()
            val oversized = JsonArray()
            repeat(ProtocolScoreCodes.MAX_YAKU_ENTRIES + 1) {
                oversized.add(JsonObject().apply {
                    addProperty("code", "PINFU")
                    addProperty("displayName", "平和")
                    addProperty("han", 1)
                })
            }
            json.add(ProtocolScoreCodes.FIELD_YAKU_ENTRIES, oversized)
            decodeBreakdown(json)
        }, "oversized yakuEntries array")
    }

    @Test
    fun `paymentPlan rejects wrong types bounds and oversized collections`() {
        fun mutatePlan(mutator: JsonObject.() -> Unit): JsonObject {
            val json = baseJson()
            json.getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN).mutator()
            return json
        }

        assertRejected({
            decodeBreakdown(baseJson().apply { addProperty(ProtocolScoreCodes.FIELD_PAYMENT_PLAN, "x") })
        }, "paymentPlan must be an object")
        assertRejected({ decodeBreakdown(mutatePlan { addProperty("kind", 1) }) }, "kind must be string")
        assertRejected({ decodeBreakdown(mutatePlan { addProperty("kind", " ") }) }, "blank kind")
        assertRejected({ decodeBreakdown(mutatePlan { add("components", JsonObject()) }) }, "components must be array")
        assertRejected({ decodeBreakdown(mutatePlan { addProperty("total", "1") }) }, "total must be int")
        assertRejected({ decodeBreakdown(mutatePlan { addProperty("total", -1) }) }, "negative total")

        fun mutateComponent(mutator: JsonObject.() -> Unit): JsonObject = mutatePlan {
            getAsJsonArray("components").get(0).asJsonObject.mutator()
        }

        assertRejected({ decodeBreakdown(mutateComponent { addProperty("amount", "1") }) }, "amount must be int")
        assertRejected({ decodeBreakdown(mutateComponent { addProperty("amount", -1) }) }, "negative amount")
        assertRejected({ decodeBreakdown(mutateComponent { addProperty("count", 0) }) }, "count must be >= 1")
        assertRejected({
            decodeBreakdown(mutateComponent { addProperty("count", ProtocolScoreCodes.MAX_PAYMENT_COUNT + 1) })
        }, "count above limit")
        assertRejected({ decodeBreakdown(mutateComponent { addProperty("payerRole", 1) }) }, "payerRole must be string")
        assertRejected({ decodeBreakdown(mutateComponent { remove("payerRole") }) }, "missing payerRole")

        assertRejected({
            val json = baseJson()
            val components = JsonArray()
            repeat(ProtocolScoreCodes.MAX_PAYMENT_COMPONENTS + 1) {
                components.add(JsonObject().apply {
                    addProperty("payerRole", "NON_DEALER")
                    addProperty("amount", 0)
                    addProperty("count", 1)
                })
            }
            json.getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN).add("components", components)
            decodeBreakdown(json)
        }, "oversized components array")

        assertRejected({
            decodeBreakdown(baseJson().apply {
                getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
                    .addProperty("total", ProtocolScoreCodes.MAX_PAYMENT_TOTAL + 1)
            })
        }, "total above limit")
    }

    @Test
    fun `scoreDetailVersion is optional bounded and strictly typed`() {
        fun caps(mutator: JsonObject.() -> Unit): ProtocolAccessCapabilities {
            val json = ApiMessageCodec.gson
                .toJsonTree(ProtocolAccessCapabilities(protocolVersion = 5, scoreDetailVersion = 1))
                .asJsonObject
            json.mutator()
            return ApiMessageCodec.gson.fromJson(json, ProtocolAccessCapabilities::class.java)
        }

        assertEquals(1, caps {}.scoreDetailVersion)
        assertEquals(0, caps { remove(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION) }.scoreDetailVersion)
        assertRejected({ caps { addProperty(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION, "1") } }, "string version")
        assertRejected({ caps { addProperty(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION, -1) } }, "negative version")
        assertRejected({
            caps { addProperty(ProtocolScoreCodes.FIELD_SCORE_DETAIL_VERSION, ProtocolScoreCodes.MAX_SCORE_DETAIL_VERSION + 1) }
        }, "oversized version")
    }

    @Test
    fun `unknown extensible codes decode without rejecting the whole settlement snapshot`() {
        val json = baseJson()
        json.getAsJsonObject("score").addProperty(ProtocolScoreCodes.FIELD_CAP_CODE, "FUTURE_CAP")
        val entries = json.getAsJsonArray(ProtocolScoreCodes.FIELD_YAKU_ENTRIES)
        entries.get(0).asJsonObject.addProperty("code", "FUTURE_YAKU")
        val plan = json.getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
        plan.addProperty("kind", "FUTURE_KIND")
        plan.getAsJsonArray("components").get(0).asJsonObject.addProperty("payerRole", "FUTURE_ROLE")

        val decoded = decodeBreakdown(json)
        assertEquals("FUTURE_CAP", decoded.score.capCode)
        assertEquals("FUTURE_YAKU", decoded.yakuEntries[0].code)
        assertEquals("平和", decoded.yakuEntries[0].displayName, "unknown code must keep legacy display name")
        assertEquals("FUTURE_KIND", decoded.paymentPlan!!.kind)
        assertEquals("FUTURE_ROLE", decoded.paymentPlan!!.components[0].payerRole)
        assertEquals(listOf("平和" to 1, "断幺九" to 1), decoded.yakuLines)
        assertEquals(listOf("荣和：P3 支付 8000"), decoded.paymentLines)
    }

    @Test
    fun `inconsistent structured collections degrade to legacy fields instead of rejecting snapshot`() {
        // 条目数量/内容与旧 yakuLines 不一致：整组结构化条目降级为空，旧文本保留。
        val missingEntry = baseJson().apply {
            getAsJsonArray(ProtocolScoreCodes.FIELD_YAKU_ENTRIES).remove(1)
        }
        val decodedMissing = decodeBreakdown(missingEntry)
        assertTrue(decodedMissing.yakuEntries.isEmpty())
        assertEquals(listOf("平和" to 1, "断幺九" to 1), decodedMissing.yakuLines)

        val renamedEntry = baseJson().apply {
            getAsJsonArray(ProtocolScoreCodes.FIELD_YAKU_ENTRIES)
                .get(0).asJsonObject.addProperty("displayName", "改名后的役")
        }
        val decodedRenamed = decodeBreakdown(renamedEntry)
        assertTrue(decodedRenamed.yakuEntries.isEmpty())
        assertEquals("平和", decodedRenamed.yakuLines[0].first)

        val wrongHan = baseJson().apply {
            getAsJsonArray(ProtocolScoreCodes.FIELD_YAKU_ENTRIES)
                .get(0).asJsonObject.addProperty("han", 9)
        }
        assertTrue(decodeBreakdown(wrongHan).yakuEntries.isEmpty())

        // 总额不守恒 / 空 components：paymentPlan 降级为 null，旧 paymentLines 保留。
        val badTotal = baseJson().apply {
            getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN).addProperty("total", 1999)
        }
        val decodedBadTotal = decodeBreakdown(badTotal)
        assertNull(decodedBadTotal.paymentPlan)
        assertEquals(listOf("荣和：P3 支付 8000"), decodedBadTotal.paymentLines)

        val emptyComponents = baseJson().apply {
            getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN).add("components", JsonArray())
        }
        val decodedEmpty = decodeBreakdown(emptyComponents)
        assertNull(decodedEmpty.paymentPlan)
        assertEquals(8000, decodedEmpty.totalPoints)

        // 缺失结构化字段：全部回退旧字段，不抛异常。
        val legacy = baseJson().apply {
            remove(ProtocolScoreCodes.FIELD_YAKU_ENTRIES)
            remove(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
            getAsJsonObject("score").remove(ProtocolScoreCodes.FIELD_CAP_CODE)
        }
        val decodedLegacy = decodeBreakdown(legacy)
        assertTrue(decodedLegacy.yakuEntries.isEmpty())
        assertNull(decodedLegacy.paymentPlan)
        assertNull(decodedLegacy.score.capCode)
        assertEquals(listOf("平和" to 1, "断幺九" to 1), decodedLegacy.yakuLines)
    }

    @Test
    fun `defensive copies prevent mutable list pollution`() {
        val entries = arrayListOf(ProtocolYakuEntry("PINFU", "平和", 1))
        val components = arrayListOf(ProtocolPaymentComponent("DISCARDER", 2000, 1))
        val plan = ProtocolPaymentPlan("NON_DEALER_RON", components, 2000)
        val breakdown = baseBreakdown().copy(yakuEntries = entries, paymentPlan = plan)

        entries.add(ProtocolYakuEntry("RIICHI", "立直", 1))
        components.clear()

        assertEquals(1, breakdown.yakuEntries.size)
        assertEquals(1, breakdown.paymentPlan!!.components.size)
        assertEquals(2000, breakdown.paymentPlan!!.total)

        assertThrows(UnsupportedOperationException::class.java) {
            (breakdown.yakuEntries as MutableList).add(ProtocolYakuEntry("X", "x", 0))
        }
        assertThrows(UnsupportedOperationException::class.java) {
            (breakdown.paymentPlan!!.components as MutableList).add(
                ProtocolPaymentComponent("DEALER", 1, 1)
            )
        }
        assertThrows(UnsupportedOperationException::class.java) {
            (breakdown.yakuLines as MutableList).add("x" to 1)
        }
        assertThrows(UnsupportedOperationException::class.java) {
            (breakdown.paymentLines as MutableList).add("x")
        }
    }

    @Test
    fun `known payment plan is dropped when kind shape role amount totalPoints or paymentLines disagree`() {
        fun JsonObject.replacePaymentLines(vararg lines: String) {
            val arr = JsonArray()
            for (line in lines) arr.add(line)
            add("paymentLines", arr)
        }

        fun mutated(mutator: JsonObject.() -> Unit): ProtocolScoreBreakdown {
            val json = baseJson()
            json.mutator()
            return decodeBreakdown(json)
        }

        // 复验反例 1：非庄荣和却使用 DEALER 付款方，且旧文本随之矛盾。
        val wrongRole = mutated {
            val plan = getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
            plan.addProperty("kind", ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_RON)
            val component = plan.getAsJsonArray("components").get(0).asJsonObject
            component.addProperty("payerRole", ProtocolScoreCodes.PAYER_ROLE_DEALER)
            component.addProperty("amount", 2000)
            component.addProperty("count", 1)
            plan.addProperty("total", 2000)
            addProperty("honbaAddition", 0)
            addProperty("riichiAddition", 0)
            addProperty("totalPoints", 2000)
            replacePaymentLines("荣和：放铳者 支付 2000")
        }
        assertNull(wrongRole.paymentPlan)
        assertEquals(listOf("荣和：放铳者 支付 2000"), wrongRole.paymentLines)
        assertEquals(0, wrongRole.honbaAddition)
        assertEquals(0, wrongRole.riichiAddition)

        // 复验反例 2：金额/合计与权威总得点冲突。
        val inconsistentTotal = mutated {
            val plan = getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
            val component = plan.getAsJsonArray("components").get(0).asJsonObject
            component.addProperty("amount", 1)
            plan.addProperty("total", 1)
            addProperty("totalPoints", 2000)
            replacePaymentLines("荣和：P3 支付 1")
        }
        assertNull(inconsistentTotal.paymentPlan)
        assertEquals(2000, inconsistentTotal.totalPoints)
        assertEquals(listOf("荣和：P3 支付 1"), inconsistentTotal.paymentLines)

        // kind 与 isDealer/isTsumo 形态冲突：庄家自摸形状套在闲家荣和上。
        val wrongForm = mutated {
            val plan = getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
            plan.addProperty("kind", ProtocolScoreCodes.PAYMENT_KIND_DEALER_TSUMO)
            val components = JsonArray().apply {
                add(JsonObject().apply {
                    addProperty("payerRole", ProtocolScoreCodes.PAYER_ROLE_NON_DEALER)
                    addProperty("amount", 4000)
                    addProperty("count", 3)
                })
            }
            plan.add("components", components)
            plan.addProperty("total", 12000)
            addProperty("honbaAddition", 0)
            addProperty("riichiAddition", 0)
            addProperty("totalPoints", 12000)
            addProperty("isDealer", false)
            addProperty("isTsumo", false)
            replacePaymentLines("自摸：闲家各付 4000")
        }
        assertNull(wrongForm.paymentPlan)

        // 人数/角色组合错误：闲家自摸缺少庄家分组。
        val wrongShape = mutated {
            val plan = getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
            plan.addProperty("kind", ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_TSUMO)
            val components = JsonArray().apply {
                add(JsonObject().apply {
                    addProperty("payerRole", ProtocolScoreCodes.PAYER_ROLE_NON_DEALER)
                    addProperty("amount", 2000)
                    addProperty("count", 2)
                })
            }
            plan.add("components", components)
            plan.addProperty("total", 4000)
            addProperty("honbaAddition", 0)
            addProperty("riichiAddition", 0)
            addProperty("totalPoints", 4000)
            addProperty("isDealer", false)
            addProperty("isTsumo", true)
            replacePaymentLines("自摸：庄家付 4000、闲家各付 2000")
        }
        assertNull(wrongShape.paymentPlan)

        // 取整金额不匹配：非庄荣和应为 8000，伪造 7999。
        val wrongAmount = mutated {
            val plan = getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
            val component = plan.getAsJsonArray("components").get(0).asJsonObject
            component.addProperty("amount", 7999)
            plan.addProperty("total", 7999)
            addProperty("honbaAddition", 0)
            addProperty("riichiAddition", 0)
            addProperty("totalPoints", 7999)
            replacePaymentLines("荣和：P3 支付 7999")
        }
        assertNull(wrongAmount.paymentPlan)

        // 旧 paymentLines 是权威回退文本：金额不一致时降级计划。
        val wrongLines = mutated {
            replacePaymentLines("荣和：P3 支付 7999")
        }
        assertNull(wrongLines.paymentPlan)
        assertEquals(listOf("荣和：P3 支付 7999"), wrongLines.paymentLines)

        // 已知 kind 下的未知 payerRole 不能消费；旧文本保留。
        val unknownRole = mutated {
            val plan = getAsJsonObject(ProtocolScoreCodes.FIELD_PAYMENT_PLAN)
            plan.getAsJsonArray("components").get(0).asJsonObject
                .addProperty("payerRole", "FUTURE_ROLE")
        }
        assertNull(unknownRole.paymentPlan)
        assertEquals(listOf("荣和：P3 支付 8000"), unknownRole.paymentLines)

        // 本场/立直相加不守恒。
        val wrongAdditions = mutated {
            addProperty("honbaAddition", 300)
            addProperty("riichiAddition", 1000)
            addProperty("totalPoints", 9301)
            replacePaymentLines("荣和：P3 支付 8000")
        }
        assertNull(wrongAdditions.paymentPlan)
        assertEquals(300, wrongAdditions.honbaAddition)
        assertEquals(1000, wrongAdditions.riichiAddition)
        assertEquals(9301, wrongAdditions.totalPoints)
    }

    @Test
    fun `validator accepts ordinary caps yakuman and honba riichi totals`() {
        // basePoints → 非庄荣和应付（round100(base×4)）与役满标记。
        val cases = listOf(
            Triple(240, 1000, false),      // 普通 30符1番
            Triple(2000, 8000, false),     // 满贯
            Triple(3000, 12000, false),    // 跳满
            Triple(4000, 16000, false),    // 倍满
            Triple(6000, 24000, false),    // 三倍满
            Triple(8000, 32000, true),     // 役满
            Triple(16000, 64000, true)     // 双倍役满
        )
        for ((basePoints, payment, yakuman) in cases) {
            val score = baseBreakdown().score.copy(
                basePoints = basePoints,
                isYakuman = yakuman,
                yakumanMultiplier = if (basePoints == 16000) 2 else if (yakuman) 1 else 0,
                capReason = null,
                capCode = null
            )
            val total = payment + 300 + 1000
            val breakdown = baseBreakdown().copy(
                score = score,
                paymentLines = listOf("荣和：P3 支付 $payment"),
                honbaAddition = 300,
                riichiAddition = 1000,
                totalPoints = total,
                paymentPlan = ProtocolPaymentPlan(
                    ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_RON,
                    listOf(ProtocolPaymentComponent(ProtocolScoreCodes.PAYER_ROLE_DISCARDER, payment, 1)),
                    payment
                )
            )
            val decoded = ApiMessageCodec.gson.fromJson(
                ApiMessageCodec.gson.toJson(breakdown),
                ProtocolScoreBreakdown::class.java
            )
            assertEquals(payment, decoded.paymentPlan?.total, "base=$basePoints")
            assertEquals(total, decoded.totalPoints)
            assertEquals(300, decoded.honbaAddition)
            assertEquals(1000, decoded.riichiAddition)
        }
    }

    @Test
    fun `all four frozen payment shapes are accepted with matching legacy text and totalPoints`() {
        data class Case(
            val kind: String,
            val dealer: Boolean,
            val tsumo: Boolean,
            val components: List<Triple<String, Int, Int>>,
            val lines: List<String>,
            val total: Int
        )
        val cases = listOf(
            Case(
                ProtocolScoreCodes.PAYMENT_KIND_DEALER_TSUMO, true, true,
                listOf(Triple(ProtocolScoreCodes.PAYER_ROLE_NON_DEALER, 4000, 3)),
                listOf("自摸：闲家各付 4000"), 12000
            ),
            Case(
                ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_TSUMO, false, true,
                listOf(
                    Triple(ProtocolScoreCodes.PAYER_ROLE_DEALER, 4000, 1),
                    Triple(ProtocolScoreCodes.PAYER_ROLE_NON_DEALER, 2000, 2)
                ),
                listOf("自摸：庄家付 4000、闲家各付 2000"), 8000
            ),
            Case(
                ProtocolScoreCodes.PAYMENT_KIND_DEALER_RON, true, false,
                listOf(Triple(ProtocolScoreCodes.PAYER_ROLE_DISCARDER, 12000, 1)),
                listOf("荣和：P1 支付 12000"), 12000
            ),
            Case(
                ProtocolScoreCodes.PAYMENT_KIND_NON_DEALER_RON, false, false,
                listOf(Triple(ProtocolScoreCodes.PAYER_ROLE_DISCARDER, 8000, 1)),
                listOf("荣和：P3 支付 8000"), 8000
            )
        )
        for (case in cases) {
            val breakdown = baseBreakdown().copy(
                paymentLines = case.lines,
                honbaAddition = 0,
                riichiAddition = 0,
                totalPoints = case.total,
                isDealer = case.dealer,
                isTsumo = case.tsumo,
                paymentPlan = ProtocolPaymentPlan(
                    case.kind,
                    case.components.map { ProtocolPaymentComponent(it.first, it.second, it.third) },
                    case.total
                )
            )
            val decoded = ApiMessageCodec.gson.fromJson(
                ApiMessageCodec.gson.toJson(breakdown),
                ProtocolScoreBreakdown::class.java
            )
            assertEquals(case.kind, decoded.paymentPlan?.kind, "valid plan dropped: $case")
            assertEquals(case.total, decoded.paymentPlan?.total)
            assertEquals(case.lines, decoded.paymentLines)
        }
    }

    @Test
    fun `payment plan structural consistency is explicit for consumers`() {
        val plan = baseBreakdown().paymentPlan!!
        assertTrue(plan.isStructurallyConsistent())
        val inconsistent = plan.copy(total = plan.total + 1)
        assertFalse(inconsistent.isStructurallyConsistent())
        assertFalse(ProtocolPaymentPlan("X", emptyList(), 0).isStructurallyConsistent())
    }

    @Test
    fun `full player snapshot and public snapshot preserve structured score fields across the frame`() {
        val breakdown = baseBreakdown()
        val winEvent = ProtocolGameEvent.TsumoWin(
            turnNumber = 12,
            player = 2,
            winTile = ProtocolTile.fromTileId(0),
            yakuList = listOf("平和", "断幺九"),
            han = 2,
            fu = 30,
            score = 2000,
            breakdown = breakdown
        )

        val basePlayer = FakeServerConnection.createDefaultSnapshot(cursor = 42L)
        val playerSnapshot = ProtocolPlayerSnapshot(
            sessionId = basePlayer.sessionId,
            roundId = basePlayer.roundId,
            syncEpoch = basePlayer.syncEpoch,
            cursor = basePlayer.cursor,
            sessionVersion = basePlayer.sessionVersion,
            roundVersion = basePlayer.roundVersion,
            view = basePlayer.view,
            decision = basePlayer.decision,
            clocks = basePlayer.clocks,
            cheatPoints = basePlayer.cheatPoints,
            session = basePlayer.session,
            controls = basePlayer.controls,
            paused = basePlayer.paused,
            settlementRoundId = "round-1",
            settlementEvents = listOf(winEvent)
        )
        val playerUpdate = ProtocolPlayerUpdate(
            snapshot = playerSnapshot,
            fromCursor = 42L,
            visibleEvents = emptyList(),
            isFullSnapshot = true
        )
        val parsedPlayer = TcpMessageCodec.parsePlayerUpdate(
            TcpMessageCodec.toPlayerUpdateFrame(playerUpdate, isFullSnapshot = true)
        )
        assertTrue(parsedPlayer.isFullSnapshot)
        assertEquals(42L, parsedPlayer.snapshot.cursor)
        val parsedPlayerWin = parsedPlayer.snapshot.settlementEvents.single() as ProtocolGameEvent.TsumoWin
        assertEquals(breakdown.paymentPlan, parsedPlayerWin.breakdown!!.paymentPlan)
        assertEquals(breakdown.yakuEntries, parsedPlayerWin.breakdown!!.yakuEntries)
        assertEquals("MANGAN", parsedPlayerWin.breakdown!!.score.capCode)

        val publicSnapshot = FakeServerConnection.createDefaultPublicSnapshot(cursor = 15L).copy(
            settlementRoundId = "round-1",
            settlementEvents = listOf(
                ProtocolGameEvent.RonWin(
                    turnNumber = 12,
                    winner = 2,
                    loser = 0,
                    winTile = ProtocolTile.fromTileId(0),
                    yakuList = listOf("平和", "断幺九"),
                    han = 2,
                    fu = 30,
                    score = 2000,
                    breakdown = breakdown
                )
            )
        )
        val publicUpdate = ProtocolPublicUpdate(
            snapshot = publicSnapshot,
            fromCursor = 15L,
            visibleEvents = emptyList(),
            isFullSnapshot = true
        )
        val parsedPublic = TcpMessageCodec.parsePublicUpdate(
            TcpMessageCodec.toPublicUpdateFrame(publicUpdate)
        )
        assertTrue(parsedPublic.isFullSnapshot)
        assertEquals(15L, parsedPublic.snapshot.cursor)
        val parsedPublicRon = parsedPublic.snapshot.settlementEvents.single() as ProtocolGameEvent.RonWin
        assertEquals("NON_DEALER_RON", parsedPublicRon.breakdown!!.paymentPlan!!.kind)
        assertEquals(8000, parsedPublicRon.breakdown!!.paymentPlan!!.total)
        assertEquals(breakdown.yakuEntries.size, parsedPublicRon.breakdown!!.yakuEntries.size)
    }
}
