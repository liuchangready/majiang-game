package com.majiang.render

import com.majiang.presentation.common.render.*

import com.majiang.GdxHeadlessTestRunner
import org.junit.jupiter.api.Assertions.*
import org.junit.jupiter.api.Test

/**
 * SkinManager 切换与持久化一致性测试。
 */
class SkinManagerTest : GdxHeadlessTestRunner() {

    @Test
    fun `默认皮肤为 WIKIPEDIA`() {
        val prefs = InMemorySkinPreferences()
        val manager = SkinManager(prefs)

        assertEquals(SkinId.WIKIPEDIA, manager.currentSkinId)
        assertEquals(SkinId.WIKIPEDIA, manager.currentSkin.id)
        assertEquals("WIKIPEDIA", manager.currentSkin.displayName)
    }

    @Test
    fun `皮肤切换与反转生效`() {
        val prefs = InMemorySkinPreferences()
        val manager = SkinManager(prefs)

        // 切换到 CLASSIC
        manager.setSkin(SkinId.CLASSIC)
        assertEquals(SkinId.CLASSIC, manager.currentSkinId)
        assertEquals(SkinId.CLASSIC, manager.currentSkin.id)
        assertEquals("CLASSIC", manager.currentSkin.displayName)
        assertEquals(SkinId.CLASSIC, prefs.loadSkinId())

        // 切换回 WIKIPEDIA
        manager.setSkin(SkinId.WIKIPEDIA)
        assertEquals(SkinId.WIKIPEDIA, manager.currentSkinId)
        assertEquals(SkinId.WIKIPEDIA, manager.currentSkin.id)
        assertEquals(SkinId.WIKIPEDIA, prefs.loadSkinId())

        // 测试 toggleSkin()
        val toggled = manager.toggleSkin()
        assertEquals(SkinId.CLASSIC, toggled)
        assertEquals(SkinId.CLASSIC, manager.currentSkinId)

        val toggledBack = manager.toggleSkin()
        assertEquals(SkinId.WIKIPEDIA, toggledBack)
        assertEquals(SkinId.WIKIPEDIA, manager.currentSkinId)
    }

    @Test
    fun `InMemoryPreferences持久化生命周期一致性`() {
        val sharedPrefs = InMemorySkinPreferences()

        val manager1 = SkinManager(sharedPrefs)
        assertEquals(SkinId.WIKIPEDIA, manager1.currentSkinId)

        manager1.setSkin(SkinId.CLASSIC)
        assertEquals(SkinId.CLASSIC, manager1.currentSkinId)

        // 模拟下次启动游戏，创建新的 SkinManager 实例并读取持久化
        val manager2 = SkinManager(sharedPrefs)
        assertEquals(SkinId.CLASSIC, manager2.currentSkinId, "新实例应当正确读出之前保存的 CLASSIC 皮肤配置")
        assertEquals(SkinId.CLASSIC, manager2.currentSkin.id)
    }

    @Test
    fun `GdxSkinPreferences真实环境持久化读写测试`() {
        val testPrefsName = "majiang_test_prefs"
        val gdxPrefs = GdxSkinPreferences(testPrefsName)

        gdxPrefs.saveSkinId(SkinId.CLASSIC)
        val loaded1 = gdxPrefs.loadSkinId()
        assertEquals(SkinId.CLASSIC, loaded1)

        gdxPrefs.saveSkinId(SkinId.WIKIPEDIA)
        val loaded2 = gdxPrefs.loadSkinId()
        assertEquals(SkinId.WIKIPEDIA, loaded2)
    }

    @Test
    fun `查找未注册皮肤抛出异常`() {
        val manager = SkinManager(InMemorySkinPreferences())
        assertDoesNotThrow { manager.getSkin(SkinId.WIKIPEDIA) }
        assertDoesNotThrow { manager.getSkin(SkinId.CLASSIC) }
    }
}
