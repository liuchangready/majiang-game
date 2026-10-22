# majiang — 千术麻将 (libGDX 2D 跨平台游戏工程)

全系统默认且唯一生产架构为 **V4 架构**，远端传输已全面切换为 **TCP (MJ-TCP/1 契约，12 字节固定二进制帧头)**，旧 UDP 与旧 TCP/V3 链路已彻底移除。架构与权威规则详见 [AGENTS.md](AGENTS.md)。

一款以**出千绝技**（落汗、袖里藏花、偷梁换柱）为核心玩法的 2D 策略日式麻将游戏。

## 技术栈

| 组件 | 版本 | 说明 |
|---|---|---|
| libGDX | 1.14.2 | 游戏框架（版本与 gdx-liftoff 1.14.2.1 官方生成器一致） |
| 语言 | Kotlin 2.4.10 (JVM 17) | 纯 Kotlin 编写核心业务与界面 |
| Gradle | 9.7.1 (wrapper) | 官方测试稳定版本 |
| AGP | 8.9.3 | **锁定勿升**：更高版本与 Gradle 9.6+ 存在内部 API 冲突 |
| Android | compileSdk 36 / minSdk 21 / targetSdk 36 | 移动端目标 |

## 七模块结构与依赖规则

```
majiang/
├── engine/       纯 JVM 领域规则、41 种役种算分、136 张牌物理守恒、单局与整场状态机、出千经济与判定、洗牌码牌算法
├── protocol/     独立线路契约：MJ-TCP/1 契约、typed DTO、错误码、信封与序列化编解码（不依赖其他模块）
├── client/       客户端门面 (GameSession/LobbySession)、ProtocolViewMapper、有界缓冲有序同步、状态缓存（仅依赖 protocol）
├── server/       权威宿主、房间生命周期 (RoomRegistry)、连接控制 (server.control)、每桌串行权威队列、InProcess/TCP 绑定
├── presentation/ libGDX 表现层 (menu/game/common)，仅依赖 client.api；组合根 MahjongGame 负责装配与单机 tick 驱动
├── lwjgl3/       Windows 桌面版启动薄壳 (1280x720 视口，日常开发主力)
├── android/      Android 移动端沉浸式全屏启动薄壳 + APK 打包
├── assets/       共享资源目录（图片/音频/字体/UI 纹理）
├── STACKING_ALGORITHM.md 码牌算法说明书
├── TILE_OVERLAP_MATRIX_ALGORITHM.md 牌型重叠度矩阵说明书
└── local.properties 本机 Android SDK 路径 (gitignored)
```

## 常用命令

```bash
# 桌面版运行（日常开发主力，秒级启动，无需模拟器）
./gradlew :lwjgl3:run
# 或直接双击运行
run_game.bat

# 运行自动化无头单测
./gradlew test --info
# 或直接双击运行
run_tests.bat

# 构建 Android 调试 APK（产物在 android/build/outputs/apk/debug/）
./gradlew :android:assembleDebug

# 安装到已连接设备并启动
./gradlew :android:installDebug :android:run

# 全量构建检查
./gradlew build
```

## 开发约定

- 虚拟坐标固定 1280x720（横屏模式，PC 窗口与手机横屏自动等比缩放）。
- 资源统一放 `assets/`，用 `Gdx.files.internal("...")` 引用。
- 版本号统一在根 `gradle.properties` 管理，勿在子模块中硬编码。
- 领域事件与状态严格单向投递，UI 严禁直连引擎内部事件：`engine` → `server EngineProtocolMapper` → `protocol DTO` → `client ProtocolViewMapper` → `client 视图模型`。
