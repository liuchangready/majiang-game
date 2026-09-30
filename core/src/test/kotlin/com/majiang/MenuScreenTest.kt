package com.majiang

import com.majiang.screen.GameScreen
import com.majiang.screen.MenuScreen
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class MenuScreenTest : GdxHeadlessTestRunner() {

    @Test
    fun testMahjongGameInstanceCreation() {
        val game = MahjongGame()
        assertNotNull(game, "MahjongGame instance should be created successfully")
    }

    @Test
    fun testMenuScreenInitialization() {
        val game = MahjongGame()
        val menuScreen = MenuScreen(game)
        assertNotNull(menuScreen, "MenuScreen should initialize without exceptions")
    }

    @Test
    fun testGameScreenInitialization() {
        val game = MahjongGame()
        val gameScreen = GameScreen(game)
        assertNotNull(gameScreen, "GameScreen placeholder should initialize without exceptions")
    }
}

