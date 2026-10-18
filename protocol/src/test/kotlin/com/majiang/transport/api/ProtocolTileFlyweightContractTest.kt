package com.majiang.transport.api

import com.google.gson.JsonParseException
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertSame
import kotlin.test.assertTrue

/**
 * MR-10 ProtocolTile 享元契约与矛盾字段反序列化阻断验证。
 */
class ProtocolTileFlyweightContractTest {

    private val gson = ApiMessageCodec.gson

    @Test
    fun `flyweight cache returns identical instance for all 136 physical tiles`() {
        for (id in 0..135) {
            val t1 = ProtocolTile.fromTileId(id)
            val t2 = ProtocolTile.fromTileId(id)
            assertSame(t1, t2, "ProtocolTile.fromTileId($id) must return identical flyweight instance")

            val fromType = ProtocolTile.fromTypeIndex(id / 4, id % 4)
            assertSame(t1, fromType, "ProtocolTile.fromTypeIndex must return identical flyweight instance")
        }
    }

    @Test
    fun `boundary check rejects invalid tileId and typeIndex`() {
        assertFailsWith<IllegalArgumentException> { ProtocolTile.fromTileId(-1) }
        assertFailsWith<IllegalArgumentException> { ProtocolTile.fromTileId(136) }
        assertFailsWith<IllegalArgumentException> { ProtocolTile.fromTypeIndex(-1, 0) }
        assertFailsWith<IllegalArgumentException> { ProtocolTile.fromTypeIndex(34, 0) }
        assertFailsWith<IllegalArgumentException> { ProtocolTile.fromTypeIndex(0, -1) }
        assertFailsWith<IllegalArgumentException> { ProtocolTile.fromTypeIndex(0, 4) }
    }

    @Test
    fun `deserialization checks contradictory fields before returning flyweight`() {
        // 矛盾花色：tileId=0 应为 MAN，传入 PIN
        val contradictSuitJson = """{"tileId": 0, "suit": "PIN", "rank": 1}"""
        val exSuit = assertFailsWith<JsonParseException> {
            gson.fromJson(contradictSuitJson, ProtocolTile::class.java)
        }
        assertTrue(exSuit.message?.contains("Contradictory suit") == true)

        // 矛盾数字：tileId=0 应为 rank 1，传入 rank 9
        val contradictRankJson = """{"tileId": 0, "suit": "MAN", "rank": 9}"""
        val exRank = assertFailsWith<JsonParseException> {
            gson.fromJson(contradictRankJson, ProtocolTile::class.java)
        }
        assertTrue(exRank.message?.contains("Contradictory rank") == true)

        // 合法反序列化返回享元同一实例
        val validJson = """{"tileId": 0, "suit": "MAN", "rank": 1}"""
        val restored = gson.fromJson(validJson, ProtocolTile::class.java)
        assertSame(ProtocolTile.fromTileId(0), restored)
    }

    @Test
    fun `red five fidelity preserved in flyweight`() {
        val redMan = ProtocolTile.fromTileId(19) // 5万 copy 3
        val redSou = ProtocolTile.fromTileId(55) // 5索 copy 3
        val redPin = ProtocolTile.fromTileId(91) // 5筒 copy 3

        assertTrue(redMan.isRedFive)
        assertTrue(redSou.isRedFive)
        assertTrue(redPin.isRedFive)

        val nonRedMan = ProtocolTile.fromTileId(16) // 5万 copy 0
        assertFalse(nonRedMan.isRedFive)
    }
}
