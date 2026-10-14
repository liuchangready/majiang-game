# majiang — 千术麻将 (libGDX 2D 跨平台游戏工程)

当前最高优先级：用户授权继续 V4 迁移，由 Anti 开发、Codex 测试验收，完成后删除无用 V3，不保留旧入口兼容性。按 [V4 计划](doc/v4-migration/PLAN.md)、[最终契约](doc/v4-migration/CONTRACT.md) 和 R 系列任务执行；下文 V3 描述仅代表迁移起点。禁止通过削弱有效测试完成迁移。

一款以**出千绝技**（落汗、袖里藏花、偷梁换柱）为核心玩法的跨平台麻将游戏。

## 技术栈

| 组件 | 版本 | 说明 |
|---|---|---|
| libGDX | 1.14.2 | 游戏框架（版本与 gdx-liftoff 1.14.2.1 官方生成器一致） |
| 语言 | Kotlin (JVM 17) | 纯 Kotlin 编写核心业务与界面 |
| Gradle | 9.7.1 (wrapper) | 官方测试稳定版本 |
| AGP | 8.9.3 | **锁定勿升**：更高版本与 Gradle 9.6+ 存在内部 API 冲突 |
| Android | compileSdk 36 / minSdk 21 / targetSdk 36 | 移动端目标 |

## 模块结构

```
majiang/
├── engine/     纯 Kotlin 权威引擎、Round/Session、规则、计时、Host 与共享 access 接口
├── protocol/   共享契约：server.api 接口与 DTO、UDP 协议栈、V4 消息编解码（client 与 server 共同依赖）
├── client/     GameClient、同步器与网络客户端/协议
├── server/     纯 JVM 房间和 TCP 服务
├── presentation/ libGDX 展示、Screen、渲染与组合根（原 core，V3 迁移后更名）
│   └── src/main/kotlin/com/majiang/
│       ├── MahjongGame.kt   主类 (Game 状态机)
│       └── screen/          场景集合 (MenuScreen 菜单, GameScreen 牌桌)
├── lwjgl3/     Windows 桌面版启动器（日常开发调试主力，秒级启动）
├── android/    Android 版启动器 + APK 打包
├── assets/     共享资源目录（图片/音频/字体）
├── STACKING_ALGORITHM.md 码牌与牌河时空影响算法设计说明书
└── local.properties  本机 Android SDK 路径
```

当前正在执行用户授权的 V3 模块迁移：规则与权威业务放 `engine/`，客户端放 `client/`，展示放 `presentation/`；`lwjgl3/` 和 `android/` 保持薄壳。迁移状态见 `doc/v3-migration/STATUS.md`。并行开发必须遵守各任务文件所有权，不能因旧路径注释把代码重新搬回 presentation。

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
