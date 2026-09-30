package com.majiang.desktop

import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration
import com.majiang.MahjongGame

/**
 * PC (Windows/Mac/Linux) 桌面端启动器。
 * 启动命令：gradlew :lwjgl3:run
 */
object DesktopLauncher {
    @JvmStatic
    fun main(args: Array<String>) {
        val config = Lwjgl3ApplicationConfiguration().apply {
            setTitle("千术麻将 (Mahjong Cheater)")
            setWindowedMode(1280, 720)
            setForegroundFPS(60)
            useVsync(true)
        }
        Lwjgl3Application(MahjongGame(), config)
    }
}

