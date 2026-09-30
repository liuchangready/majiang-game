package com.majiang

import com.badlogic.gdx.ApplicationAdapter
import com.badlogic.gdx.Gdx
import com.badlogic.gdx.backends.headless.HeadlessApplication
import com.badlogic.gdx.backends.headless.HeadlessApplicationConfiguration
import com.badlogic.gdx.graphics.GL20
import org.junit.jupiter.api.BeforeAll
import java.lang.reflect.Proxy

/**
 * Headless libGDX 测试基类。
 * 在内存中初始化 Gdx.app、Gdx.files 与 Mock GL20，无需启动物理图形窗口即可执行单元测试。
 */
open class GdxHeadlessTestRunner {

    companion object {
        private var initialized = false

        @BeforeAll
        @JvmStatic
        fun initGdx() {
            if (!initialized) {
                val config = HeadlessApplicationConfiguration()
                HeadlessApplication(object : ApplicationAdapter() {}, config)

                val mockGL = Proxy.newProxyInstance(
                    GL20::class.java.classLoader,
                    arrayOf(GL20::class.java)
                ) { _, method, _ ->
                    when (method.returnType) {
                        Int::class.javaPrimitiveType -> 1
                        Boolean::class.javaPrimitiveType -> true
                        Float::class.javaPrimitiveType -> 0f
                        String::class.java -> ""
                        else -> null
                    }
                } as GL20

                Gdx.gl = mockGL
                Gdx.gl20 = mockGL
                initialized = true
            }
        }
    }
}

