package com.majiang.desktop

import com.badlogic.gdx.ApplicationListener
import com.badlogic.gdx.Gdx
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3Application
import com.badlogic.gdx.backends.lwjgl3.Lwjgl3ApplicationConfiguration
import com.badlogic.gdx.files.FileHandle
import com.badlogic.gdx.graphics.PixmapIO
import com.badlogic.gdx.utils.ScreenUtils
import com.majiang.MahjongGame
import com.majiang.presentation.game.GameScreen
import com.majiang.presentation.menu.MenuScreen
import java.io.File

/**
 * 自动化屏幕快照捕获器。
 * 启动游戏并依序导航截取主要菜单、规则弹窗、码牌出千以及单机牌桌对局画面，保存至 D:/majiang/snapshot/。
 */
object SnapshotLauncher {
    @JvmStatic
    fun main(args: Array<String>) {
        val snapshotDir = File("D:/majiang/snapshot").apply { mkdirs() }
        println("[SnapshotLauncher] 输出快照目标目录: ${snapshotDir.absolutePath}")

        val config = Lwjgl3ApplicationConfiguration().apply {
            setTitle("千术麻将 (Mahjong Cheater) - 快照捕获")
            setWindowedMode(1280, 720)
            setForegroundFPS(60)
            useVsync(true)
        }

        val game = MahjongGame()
        var frame = 0
        var phase = 0

        fun capture(name: String) {
            val file = File(snapshotDir, name)
            val w = Gdx.graphics.backBufferWidth
            val h = Gdx.graphics.backBufferHeight
            val pixmap = ScreenUtils.getFrameBufferPixmap(0, 0, w, h)
            PixmapIO.writePNG(FileHandle(file), pixmap, -1, true)
            pixmap.dispose()
            println("[SnapshotLauncher] 成功捕获截屏: ${file.name} (${w}x${h})")
        }

        fun setMenuState(screen: MenuScreen, state: MenuScreen.MenuState) {
            try {
                val field = MenuScreen::class.java.getDeclaredField("currentState")
                field.isAccessible = true
                field.set(screen, state)
            } catch (e: Exception) {
                println("[SnapshotLauncher] 设置菜单状态异常: ${e.message}")
            }
        }

        val listener = object : ApplicationListener {
            override fun create() {
                game.create()
            }

            override fun resize(w: Int, h: Int) {
                game.resize(w, h)
            }

            override fun render() {
                game.render()
                frame++

                val screen = game.screen
                when (phase) {
                    0 -> {
                        // Phase 0: 等待主菜单初始帧渲染稳定 (第 35 帧)
                        if (frame >= 35 && screen is MenuScreen) {
                            capture("01_main_menu.png")
                            setMenuState(screen, MenuScreen.MenuState.SECRETS_MODAL)
                            phase = 1
                        }
                    }
                    1 -> {
                        // Phase 1: 雀圣千术绝技秘籍弹窗 (第 55 帧)
                        if (frame >= 55 && screen is MenuScreen) {
                            capture("02_cheat_secrets.png")
                            setMenuState(screen, MenuScreen.MenuState.SETTINGS_MODAL)
                            phase = 2
                        }
                    }
                    2 -> {
                        // Phase 2: 游戏设置与皮肤弹窗 (第 75 帧)
                        if (frame >= 75 && screen is MenuScreen) {
                            capture("03_settings.png")
                            setMenuState(screen, MenuScreen.MenuState.CREATE_ROOM_DIALOG)
                            phase = 3
                        }
                    }
                    3 -> {
                        // Phase 3: 创建房间规则配置弹窗 (第 95 帧)
                        if (frame >= 95 && screen is MenuScreen) {
                            capture("04_create_room.png")
                            // 恢复主菜单并启动单机权威对局
                            setMenuState(screen, MenuScreen.MenuState.MAIN)
                            println("[SnapshotLauncher] 正在启动单机权威对局...")
                            game.launchGame()
                            phase = 4
                        }
                    }
                    4 -> {
                        // Phase 4: 等待进入牌桌 GameScreen 并加载完成 (洗牌码牌千术准备弹窗)
                        if (screen is GameScreen && !screen.isLoading && screen.currentSnapshot != null) {
                            if (frame >= 180) {
                                capture("05_shuffle_cheat_modal.png")
                                // 点击跳过/关闭码牌弹窗，直接进入正式打牌阶段
                                println("[SnapshotLauncher] 进入正式打牌对局阶段...")
                                screen.shufflePreparationPanel.touchDown(750f, 120f)
                                phase = 5
                            }
                        }
                    }
                    5 -> {
                        // Phase 5: 截取正式打牌对局进行中画面 (手牌、牌河、宝牌指示牌、AI视线、千术操作栏)
                        if (screen is GameScreen && !screen.shufflePreparationPanel.isActive) {
                            if (frame >= 250) {
                                capture("06_game_table_playing.png")
                                println("[SnapshotLauncher] 所有主要画面快照捕获完毕！")
                                phase = 6
                                Gdx.app.exit()
                            }
                        }
                    }
                }
            }

            override fun pause() { game.pause() }
            override fun resume() { game.resume() }
            override fun dispose() { game.dispose() }
        }

        Lwjgl3Application(listener, config)
    }
}
