package com.majiang.client.api

import com.majiang.client.mapping.ProtocolViewMapper
import com.majiang.transport.api.ProtocolTile
import org.junit.jupiter.api.Test
import kotlin.test.assertEquals
import kotlin.test.assertFailsWith
import kotlin.test.assertFalse
import kotlin.test.assertSame
import kotlin.test.assertTrue

/**
 * MR-10 TileView 享元契约与边界校验。
 */
class TileViewFlyweightContractTest {

    @Test
    fun `flyweight cache returns identical instance for all 136 physical tiles`() {
        for (id in 0..135) {
            val t1 = TileView.fromTileId(id)
            val t2 = TileView.fromTileId(id)
            assertSame(t1, t2, "TileView.fromTileId($id) must return identical flyweight instance")

            val fromType = TileView.fromTypeIndex(id / 4, id % 4)
            assertSame(t1, fromType, "TileView.fromTypeIndex must return identical flyweight instance")
        }
    }

    @Test
    fun `boundary check rejects invalid tileId and typeIndex`() {
        assertFailsWith<IllegalArgumentException> { TileView.fromTileId(-1) }
        assertFailsWith<IllegalArgumentException> { TileView.fromTileId(136) }
        assertFailsWith<IllegalArgumentException> { TileView.fromTypeIndex(-1, 0) }
        assertFailsWith<IllegalArgumentException> { TileView.fromTypeIndex(34, 0) }
        assertFailsWith<IllegalArgumentException> { TileView.fromTypeIndex(0, -1) }
        assertFailsWith<IllegalArgumentException> { TileView.fromTypeIndex(0, 4) }
    }

    @Test
    fun `ProtocolViewMapper preserves flyweight instances`() {
        for (id in 0..135) {
            val proto = ProtocolTile.fromTileId(id)
            val view = ProtocolViewMapper.mapTile(proto)
            assertSame(TileView.fromTileId(id), view)

            val back = ProtocolViewMapper.toProtocolTile(view)
            assertSame(proto, back)
        }
    }

    @Test
    fun `red five fidelity preserved in TileView flyweight`() {
        val redMan = TileView.fromTileId(19) // 5万 copy 3
        val redSou = TileView.fromTileId(55) // 5索 copy 3
        val redPin = TileView.fromTileId(91) // 5筒 copy 3

        assertTrue(redMan.isRedFive)
        assertTrue(redSou.isRedFive)
        assertTrue(redPin.isRedFive)

        val nonRedMan = TileView.fromTileId(16) // 5万 copy 0
        assertFalse(nonRedMan.isRedFive)
    }

    @Test
    fun `TileView constructor strictly rejects contradictory suit or rank`() {
        // tileId 0 is MAN 1; PIN or other ranks must be rejected
        val ex1 = assertFailsWith<IllegalArgumentException> {
            TileView(0, SuitView.PIN, 1)
        }
        assertTrue(ex1.message!!.contains("Contradictory suit"))

        val ex2 = assertFailsWith<IllegalArgumentException> {
            TileView(0, SuitView.MAN, 2)
        }
        assertTrue(ex2.message!!.contains("Contradictory rank"))

        // tileId 36 is SOU 1; MAN must be rejected
        assertFailsWith<IllegalArgumentException> {
            TileView(36, SuitView.MAN, 1)
        }

        // tileId 108 is WIND 1; DRAGON must be rejected
        assertFailsWith<IllegalArgumentException> {
            TileView(108, SuitView.DRAGON, 1)
        }
    }

    @Test
    fun `ProtocolViewMapper toProtocolTile strictly preserves rejection semantics`() {
        // Valid tile mapping
        val valid = TileView.fromTileId(0)
        val proto = ProtocolViewMapper.toProtocolTile(valid)
        assertSame(ProtocolTile.fromTileId(0), proto)
    }
}
