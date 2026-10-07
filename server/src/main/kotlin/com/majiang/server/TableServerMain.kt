package com.majiang.server

import com.majiang.server.udp.UdpTableServer
import kotlin.system.exitProcess

/**
 * 权威联网服务命令行启动入口 (TableServerMain)
 *
 * 遵循 V4 规范与 TASK-ANTI-R5ROOT 要求：
 * 1. 默认 run 改为 UDP 协议实现 (UdpTableServer)；
 * 2. 命令行参数 `--bind, -b` 与 `--port, -p` 明确有效；
 * 3. 启动就绪后必须打印实际监听端口。
 *
 * 启动参数说明：
 *   --port, -p  指定 UDP 监听端口 (默认: 8080，0 表示随机可用端口)
 *   --bind, -b  指定监听地址 (默认: 127.0.0.1；局域网对战可绑定 0.0.0.0)
 *
 * 运行方式（推荐 Gradle，自动组装完整运行时类路径）：
 *   ./gradlew :server:run
 *   ./gradlew :server:run -Pport=8080 -Pbind=0.0.0.0
 */
fun main(args: Array<String>) {
    var port = 8080
    var bindHost = "127.0.0.1"

    var i = 0
    while (i < args.size) {
        when (args[i]) {
            "--port", "-p" -> {
                if (i + 1 < args.size) {
                    port = args[i + 1].toIntOrNull() ?: port
                    i++
                }
            }
            "--bind", "-b" -> {
                if (i + 1 < args.size) {
                    bindHost = args[i + 1]
                    i++
                }
            }
            "--help", "-h" -> {
                println("🀄 千术麻将 (Mahjong Cheater) - V4 UDP TableServer")
                println("用法 (推荐): ./gradlew :server:run [-Pport=<端口>] [-Pbind=<IP>]")
                println("等价主类参数: com.majiang.server.TableServerMainKt [--port <端口>] [--bind <IP>]")
                println("选项:")
                println("  --port, -p <端口>   指定 UDP 监听端口 (默认: 8080)")
                println("  --bind, -b <IP>     指定绑定 IP (默认: 127.0.0.1)")
                exitProcess(0)
            }
        }
        i++
    }

    println("==================================================================")
    println("  🀄 千术麻将 (Mahjong Cheater) - V4 UDP 权威房间服务器")
    println("==================================================================")
    println("正在启动 UDP 服务...")
    println("绑定网络地址: $bindHost")
    println("配置目标端口: $port")

    val server = UdpTableServer(bindHost = bindHost, port = port)
    try {
        server.start()
        println("服务启动就绪！实际监听端口: ${server.boundPort}")
        println("等待客户端 UDP 连接... (按 Ctrl+C 退出)")
    } catch (t: Throwable) {
        println("服务启动失败: ${t.message}")
        server.close()
        exitProcess(1)
    }

    Runtime.getRuntime().addShutdownHook(Thread({
        println("正在关闭 UdpTableServer...")
        server.close()
        println("UdpTableServer 已安全停止。")
    }, "UdpTableServer-ShutdownHook"))

    // 主线程常驻等待
    try {
        while (!server.closed) {
            Thread.sleep(1000L)
        }
    } catch (_: InterruptedException) {
        server.close()
    }
}
