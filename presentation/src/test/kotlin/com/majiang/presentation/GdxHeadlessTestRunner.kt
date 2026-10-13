package com.majiang.presentation

import com.badlogic.gdx.ApplicationAdapter
import com.badlogic.gdx.Gdx
import com.badlogic.gdx.backends.headless.HeadlessApplication
import com.badlogic.gdx.backends.headless.HeadlessApplicationConfiguration
import com.badlogic.gdx.graphics.GL20
import org.junit.jupiter.api.BeforeAll
import java.lang.reflect.Proxy
import java.nio.IntBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

/**
 * Headless libGDX 测试基类。
 * 在内存中初始化 Gdx.app、Gdx.files 与 Mock GL20，无需启动物理图形窗口即可执行单元测试。
 */
open class GdxHeadlessTestRunner {

    companion object {
        private var initialized = false

        @Volatile
        var uiThread: Thread? = null
            private set

        fun isUiThread(): Boolean = uiThread != null && Thread.currentThread() === uiThread

        @BeforeAll
        @JvmStatic
        fun initGdx() {
            if (!initialized) {
                val initLatch = CountDownLatch(1)
                val config = HeadlessApplicationConfiguration()
                HeadlessApplication(object : ApplicationAdapter() {
                    override fun create() {
                        uiThread = Thread.currentThread()
                        initLatch.countDown()
                    }
                }, config)

                val acquired = initLatch.await(3, TimeUnit.SECONDS)
                check(acquired) { "Failed to initialize Gdx HeadlessApplication within 3 seconds" }

                val mockGL = Proxy.newProxyInstance(
                    GL20::class.java.classLoader,
                    arrayOf(GL20::class.java)
                ) { _, method, args ->
                    // ShaderProgram and Mesh read status/handles from output buffers.
                    // Returning 1 from the proxy alone leaves those buffers at zero.
                    if (method.name in setOf("glGetShaderiv", "glGetProgramiv", "glGetIntegerv", "glGenBuffers", "glGenTextures")) {
                        (args?.lastOrNull() as? IntBuffer)?.let { buffer ->
                            for (index in buffer.position() until buffer.limit()) buffer.put(index, 1)
                        }
                    }
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
