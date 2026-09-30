package com.majiang.render

import com.badlogic.gdx.Gdx
import com.badlogic.gdx.utils.Disposable

/**
 * 皮肤持久化存储接口。
 *
 * 允许在不同环境（运行时 libGDX Preferences、内存测试环境）间切换。
 */
interface SkinPreferences {
    fun loadSkinId(): SkinId
    fun saveSkinId(skinId: SkinId)
}

/**
 * 基于 libGDX Gdx.app.getPreferences 的持久化实现。
 */
class GdxSkinPreferences(private val prefsName: String = "majiang") : SkinPreferences {
    companion object {
        const val PREF_KEY_SKIN = "tile_skin"
    }

    override fun loadSkinId(): SkinId {
        return try {
            val app = Gdx.app ?: return SkinId.WIKIPEDIA
            val prefs = app.getPreferences(prefsName)
            val name = prefs.getString(PREF_KEY_SKIN, SkinId.WIKIPEDIA.name)
            SkinId.valueOf(name)
        } catch (_: Exception) {
            SkinId.WIKIPEDIA
        }
    }

    override fun saveSkinId(skinId: SkinId) {
        try {
            val app = Gdx.app ?: return
            val prefs = app.getPreferences(prefsName)
            prefs.putString(PREF_KEY_SKIN, skinId.name)
            prefs.flush()
        } catch (_: Exception) {
        }
    }
}

/**
 * 内存持久化实现，用于单元测试或纯逻辑环境。
 */
class InMemorySkinPreferences(initialSkin: SkinId = SkinId.WIKIPEDIA) : SkinPreferences {
    private var current = initialSkin

    override fun loadSkinId(): SkinId = current

    override fun saveSkinId(skinId: SkinId) {
        current = skinId
    }
}

/**
 * 皮肤管理器：维护当前皮肤引用、皮肤注册表以及切换与持久化机制。
 *
 * 默认皮肤为 WIKIPEDIA。
 */
class SkinManager(
    private val preferences: SkinPreferences = GdxSkinPreferences()
) : Disposable {

    private val skins = mutableMapOf<SkinId, TileSkin>()

    var currentSkinId: SkinId = SkinId.WIKIPEDIA
        private set

    val currentSkin: TileSkin
        get() = getSkin(currentSkinId)

    init {
        registerSkin(WikipediaSkin())
        registerSkin(ClassicSkin())
        currentSkinId = preferences.loadSkinId()
    }

    fun registerSkin(skin: TileSkin) {
        skins[skin.id] = skin
    }

    fun getSkin(id: SkinId): TileSkin {
        return skins[id] ?: throw IllegalArgumentException("Skin not registered: $id")
    }

    fun setSkin(id: SkinId) {
        if (!skins.containsKey(id)) {
            throw IllegalArgumentException("Skin not registered: $id")
        }
        currentSkinId = id
        preferences.saveSkinId(id)
    }

    fun toggleSkin(): SkinId {
        val next = when (currentSkinId) {
            SkinId.WIKIPEDIA -> SkinId.CLASSIC
            SkinId.CLASSIC -> SkinId.WIKIPEDIA
        }
        setSkin(next)
        return next
    }

    override fun dispose() {
        skins.values.forEach { it.dispose() }
    }
}
