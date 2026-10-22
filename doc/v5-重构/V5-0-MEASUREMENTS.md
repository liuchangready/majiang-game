# V5-0 实测基线与环境缺口（P0.3）

日期：2026-10-23

状态：**P0.3 实测记录；不是 G0/V5-1 验收报告，不代表 V5 已实现或已验收。**

任务基线：任务书要求 `v5` 分支、提交 `48125ff`。实际测量工作树为 `docs/v5-0-p01-golden-unity-env`，测量时 HEAD 为 `7a9cd98`（P0.1/P0.2 文档提交之后）；`v5` 仍指向 `48125ff`。

本次只新增本文件；未修改生产代码、测试、Unity 工程、Unity 输出或其他文档，未提交、未推送。

> V4（Kotlin/JVM + libGDX + MJ-TCP/1）仍是生产基线。本文件中的数字来自本次会话实际执行的 V4 代码与测量工具；历史 T0 数字、1 MiB 协议上限、既有 Unity/桌面冒烟日志均不作为本次结论，也不是 V5 容量承诺。

---

## 0. 结论摘要

1. **本次观测到的最大 UTF-8 载荷为 11,234 字节**（10.97 KiB，占 1 MiB 的 1.07%）：真实四人 + 三绝技 + 观战整局中，落汗生效、晚局、`CHEAT_WINDOW` 的一次真实 `PLAYER_UPDATE` 推送。最大样本 SHA-256 为 `3AEF38C4F8E60E695002BBAF1C6E86177B88E28443D3A0B341581203AED624A0`。
2. 普通桌最大 **8,727 B**，结算推送最大 **7,413 B**，观战公开推送最大 **3,912 B**；三绝技各自的生效快照为 **6,483–6,856 B**。所有样本均为严格 UTF-8 JSON，未发现可见活动牌重复。
3. 同源官方 T0 工具本次独立运行得到 `PLAYER_UPDATE max=10,026 B`（380 次），与本次更宽的采样工具的 11,234 B 不是同一局；这证明历史 `10,128 B` **不是上界**，也说明单次整局的最大值会随牌局与座位视角变化。不能拿 1 MiB 上限反推“实测安全容量”。
4. **三绝技覆盖不均**：落汗在晚局仍有 4 张标记，达到 11,234 B；袖里藏花/偷梁换柱的挂起 offer 只在早巡出现（牌山 67–68 张），其 6.5–6.9 KB 最大值**不是晚局最坏值**。这是本文件的明确未覆盖项，不能宣称“三绝技最大载荷已测全”。
5. 本机 V4 Windows 桌面构建、Android Debug APK 构建、空 `.NET` 脚手架构建均通过；但 **IL2CPP 因缺 MSVC/Windows SDK 不能构建**，Android 缺 NDK，无 Android 真机/模拟器，无 macOS/iOS 工具链，**跨物理网络 T4 未测**。Unity 仅确认编辑器、许可、工程与既有 Mono 构建日志存在，本次未新建 Unity 构建，不能据此宣称 IL2CPP/Android/iOS 通过。

---

## 1. 分支、提交与工作树

| 项 | 值 | 证据/说明 |
|---|---|---|
| 任务要求基线 | `v5` = `48125fff4ca14b21bb46a067ebd806f510be02d4` | `git rev-parse v5` |
| 实际测量分支 | `docs/v5-0-p01-golden-unity-env` | `git branch --show-current`；未切换分支 |
| 测量时 HEAD | `7a9cd98a5dff2b2d35fa3d19ba1c02bb78f42870` | P0.1 `74db3de`、P0.2 `7a9cd98` 已由其他任务提交 |
| V4 源码是否漂移 | 无 | `git diff --name-only 48125ff..HEAD -- engine/src protocol/src server/src client/src presentation/src lwjgl3/src android/src` 输出为空，exit 0 |
| 工作树 | 测量前后 `git status --porcelain` 为空 | 本文件写入后仅应出现本文件 |
| 未提交的他人改动 | 测量时工作树无未提交改动 | 任务书提到的 `UNITY-ENVIRONMENT.md`、Unity `Resources` 删除、Burst 设置当时包含在 P0.1 提交 `74db3de` 中，本次测量未修改 |

因此，下文的 V4 源码结论适用于 `48125ff` 的 V4 源码；文档/分支状态按实际 HEAD 记录，不假装工作树在 `v5` 分支上。P0.1 的 `V5-0-TRACEABILITY.md`/`V5-0-GOLDEN-MANIFEST.md` 与 P0.2 的三份契约文档已存在，但本任务不复核其内容。

测量完成后的 MR 整理提交 `3581653` 将上述 Unity 环境变更从分支提交中移出；它们目前作为他人的未提交工作树变更保留，本任务与本文件的提交均未暂存或修改这些文件。测量时的 HEAD 与数据仍按上表记录。

---

## 2. 机器与工具链

### 2.1 机器规格

| 项 | 值 |
|---|---|
| 主机名 | `DESKTOP-U2CRNCD` |
| 机型 | HUAWEI `MRGF-XX` |
| OS | Windows 10 专业版 10.0.19045（Build 19045），64 位 |
| CPU | 12th Gen Intel Core i7-1260P，12 核 / 16 逻辑处理器 |
| 内存 | 15.73 GiB（8 × 2 GiB，6400 MT/s） |
| GPU | Intel Iris Xe Graphics，驱动 31.0.101.3729 |
| 磁盘 | C: 17.4 GB 可用 / 100 GB；D: 376.5 GB 可用 / 426.6 GB |
| 网络 | WLAN 与 VPN 隧道接口可用；WLAN 可访问网关与公网。测量复核不记录具体私网/隧道地址 |

C 盘剩余空间偏紧，IL2CPP/Unity 大构建前应先确认空间；本次未做 IL2CPP，未触发该风险。

### 2.2 构建工具

| 组件 | 实测版本/路径 | 状态 |
|---|---|---|
| JDK | JetBrains Runtime 25.0.2（`C:\Program Files\Android\Android Studio\jbr`） | 可用；Gradle 实际使用该 JVM |
| Gradle | 9.7.1 wrapper | 可用 |
| Kotlin | 2.4.10（Gradle 插件）；Gradle 自带 Kotlin 2.4.0 | 可用 |
| AGP | 8.9.3 | 可用 |
| Android SDK | `C:\Users\sncdc\AppData\Local\Android\Sdk`：platform 36、build-tools 35.0.0/36.0.0、platform-tools 37.0.1、cmdline-tools 23.0.0 | 可用；**无 NDK**；`adb` 未加入 PATH，需用绝对路径 |
| .NET SDK | 8.0.426，`D:\Unity\dotnet\dotnet.exe` | 可用；**不在 PATH**，脚本须用绝对路径 |
| Unity Editor | 6000.3.27f1（`D:\Unity\Hub\Editor\6000.3.27f1`） | 可用；Windows IL2CPP/Android 模块目录存在 |
| Unity 许可 | `UnityEntitlementLicense.xml`，2026-10-23 02:11:55，Product=`Unity Personal` | 已激活 |
| MSVC / Windows SDK | 未发现 VS 安装、`cl.exe`、Windows Kits、CMake/Ninja | **缺失** |
| macOS / Xcode | 未发现 `xcodebuild`/`xcrun`/macOS 主机或远程 Mac | **不可用** |

---

## 3. 载荷测量方法

### 3.1 测量对象与序列化路径

- 权威：`InProcessServer(isSingleplayer=false)`，真实 `TableAuthority` 四人桌；1 个观战连接。
- 序列化：`ApiMessageCodec.encodePush`、`encodePublicUpdatePush`、`encodeResponse`、`TcpMessageCodec.encodeRequestEnvelope`；Gson 2.11.0，与 MJ-TCP/1 共用同一组适配器。
- 字节口径：`String.toByteArray(Charsets.UTF_8).size`，即 **JSON 载荷字节数**。不含 12 字节 TCP 帧头；若看线路帧，应加 12 B。V4 不做 zlib 压缩，载荷字节即应用层网络载荷字节；本次不测压缩后大小。
- 场景：
  - **普通桌**：`ProtocolMatchConfig()` 默认配置，千术关闭；2 局打到首次结算，77/82 手。
  - **三绝技**：千术经济开启（每家 100 点，三项各 10 点），落汗/袖里藏花/偷梁换柱全部启用；5 局，每局三项各成功 1 次，均打到首次结算（89/85/89/97/89 手）。
  - **观战**：每个场景都挂 1 个观战连接，接收 `PUBLIC_UPDATE`。
- 计数口径：玩家推送按“每连接一份”计数；同一广播的 4 个座位视角会形成 4 个不同样本，因此普通桌 628 条不是 628 次广播。结算 28 条 = 7 局 × 4 座位。

### 3.2 分类定义

| 类别 | 定义 |
|---|---|
| `NORMAL_PLAYER_PUSH(incr)` | 普通桌（千术关闭）所有非结算、非全量基线的玩家推送 |
| `CHEAT_TILE_MARK_PUSH(incr)` | 三绝技桌中快照 `tileMark.activeTileCount > 0` 的真实玩家推送 |
| `CHEAT_SLEEVE_BLOOM_PUSH(incr)` | 三绝技桌中快照 `sleeveBloom.pendingOffer != null` 的真实玩家推送 |
| `CHEAT_BEAM_SWAP_PUSH(incr)` | 三绝技桌中快照 `beamSwap.pendingOffer != null` 的真实玩家推送 |
| `CHEAT_*_SNAPSHOT` | 该绝技请求返回 APPLIED/RECORDED 后立即 `snapshot()`，再用同一 `encodePush(..., isFullSnapshot=true)` 编码的全量快照；不是从 socket 抓到的帧 |
| `SETTLEMENT_PLAYER_PUSH(incr)` | 快照 `settlementRoundId != null` 的真实玩家推送 |
| `SPECTATOR_PUBLIC_PUSH(incr)` | 观战连接 `onPublicUpdate` 的非全量推送 |
| `RSP_PLAYER_SNAPSHOT` / `RSP_PUBLIC_SNAPSHOT` | `snapshot()`/`publicSnapshot()` 的 `ApiResponse` 全量应答 |
| `RSP_ROUND_RESULT` | `queryRoundResult(roundId)` 的 `ApiResponse` |
| `REQ_ENVELOPE` | 每次动作提交的 `TcpRequestEnvelope` JSON |
| `ROOM_UPDATE` / `RSP_ROOM_INFO` | 房间信息帧/应答 |

`CHEAT_GAME_PLAYER_PUSH(incr)` 是未命中上述绝技状态的其余三绝技桌推送，仅作背景，不作为三绝技结论。

### 3.3 分位口径与可复现性

- 分位采用 nearest-rank：排序后取第 `ceil(p*n)` 个（1-based）。n 很小时（如 5）p50/p95 只能反映样本位置，不应外推总体分布。
- 牌局使用 UUID/`SecureRandom`/洗牌随机，**不同运行的字节内容不会相同**；可复现的是“同场景、同配置、同采样点”，不是逐字节同哈希。
- 每次运行的最大样本已落盘；最大样本的 SHA-256 与状态摘要可用于核对“同一个样本”。本次最大样本已归档到 `build/v5-p03/out/`（被 `.gitignore` 忽略，不随提交交付）；复跑时用 `V5_P03_OUT` 指向该目录，未设置时工具默认写入 `C:\Users\sncdc\AppData\Local\Temp\v5-p03\out`。
- 测量工具源码：`build/v5-p03/src/V5P03PayloadMeasurementTest.kt`，SHA-256 `5FB295AF32A88E6E833367731CDDE42079CC96FEC9AD5AA5DDA184609CA5BD61`。为遵守“只新增/修改本文档”，工具放在忽略的 `build/` 下，不纳入提交。

---

## 4. 实测结果

### 4.1 分布与最大值

本次实际运行：2 局普通桌 + 5 局三绝技桌，7 局全部 `settled=true`；每局三绝技各成功 1 次（共 3 项 × 5 次）；1 个外部测量测试通过。

| 类别 | n | min B | p50 B | p95 B | max B | 占 1 MiB |
|---|---:|---:|---:|---:|---:|---:|
| **普通桌玩家推送（增量）** | 628 | 2,426 | 4,591 | 7,979 | **8,727** | 0.83% |
| **落汗生效玩家推送（增量）** | 444 | 3,651 | 5,965 | 9,669 | **11,234** | **1.07%** |
| 落汗请求后全量快照 | 5 | 6,464 | 6,503 | 6,519 | 6,519 | 0.62% |
| **袖里藏花挂起玩家推送（增量）** | 5 | 6,536 | 6,541 | 6,559 | **6,559** | 0.63% |
| 袖里藏花请求后全量快照 | 5 | 6,460 | 6,465 | 6,483 | 6,483 | 0.62% |
| **偷梁换柱挂起玩家推送（增量）** | 5 | 6,828 | 6,851 | 6,856 | **6,856** | 0.65% |
| 偷梁换柱请求后全量快照 | 5 | 6,752 | 6,775 | 6,780 | 6,780 | 0.65% |
| **结算玩家推送（增量）** | 28 | 6,250 | 6,838 | 7,411 | **7,413** | 0.71% |
| **观战公开推送（增量）** | 608 | 1,178 | 2,542 | 3,734 | **3,912** | 0.37% |
| 玩家全量快照 RSP | 14 | 5,171 | 5,944 | 6,981 | 6,981 | 0.67% |
| 观战全量公开快照 RSP | 14 | 1,136 | 1,138 | 1,355 | 1,355 | 0.13% |
| 观战全量公开推送 | 14 | 1,177 | 1,179 | 1,396 | 1,396 | 0.13% |
| 结算结果 RSP | 7 | 332 | 332 | 349 | 349 | 0.03% |
| 房间更新 | 7 | 1,087 | 1,138 | 1,138 | 1,138 | 0.11% |
| 房间信息 RSP | 7 | 1,143 | 1,193 | 1,193 | 1,193 | 0.11% |
| 动作请求信封 | 583 | 378 | 382 | 385 | 387 | 0.04% |
| 三绝技桌其余玩家推送（背景） | 1,322 | 2,975 | 5,113 | 8,462 | 9,746 | 0.93% |

### 4.2 最大样本

| 类别 | 字节 | 场景/状态摘要 | SHA-256 |
|---|---:|---|---|
| 落汗玩家推送 | **11,234** | `round=ee513314-01e2-4a26-8773-e3090f594783`，cursor 88，`CHEAT_WINDOW`，wall 1，turn 0，`activeTileCount=4` | `3AEF38C4F8E60E695002BBAF1C6E86177B88E28443D3A0B341581203AED624A0` |
| 普通桌玩家推送 | **8,727** | `round=cd961762-19b8-48c5-a26f-6d984a2b7bd4`，cursor 74，`RESPONSE_WINDOW`，wall 7，turn 2 | `1632B4FD664E4C0FE4B69C895062C090535E79754332B05FEBB9FB6D0E9637F1` |
| 结算玩家推送 | **7,413** | `round=174d2434-5ff6-4416-9649-c73933bb89f4`，cursor 98，`ROUND_END`，wall 0，`settlementRoundId` 已置位 | `0D666B4A43AFBC8A24F3F3B763A12C7FF563313805B3569DB9D60433AC584641` |
| 观战公开推送 | **3,912** | `round=b8b72a8c-c481-4e9f-9e90-dc9f59463b98`，cursor 85，`RESPONSE_WINDOW`，wall 0，`syncEpoch=49951c01-44b2-4db2-b61b-24e96224b383-public` | `DADA9511D6D4F8678BCBDDFE9ED5BCE72682E9B053CC28C607C6B9A354B3BF62` |
| 袖里藏花挂起推送 | **6,559** | `round=b8b72a8c-c481-4e9f-9e90-dc9f59463b98`，cursor 4，`CHEAT_WINDOW`，wall 68，turn 1，`pendingOffer != null` | `38639F804E3CC3C4955D3BF16ECE14945B0E22D9E253888D799F8B827F397AAA` |
| 偷梁换柱挂起推送 | **6,856** | `round=7824c2d2-d6f5-4a71-b32c-f831e7af157e`，cursor 7，`CHEAT_WINDOW`，wall 67，turn 2，`pendingOffer != null` | `F004A2B60A1247916ACEC7F353FB1EB40CDC279BEFD1D07EEB9DC1B5D4366B9E` |
| 玩家全量快照 RSP | **6,981** | 初始/终局快照中的最大值 | `59BC462DF749C0D117E39EE9131380380215BE0524F343B079D4E743A140F599` |

其余最大值样本的哈希与大小见 `build/v5-p03/out/summary.csv` 及 `MAX_*.json`。所有 `MAX_*.json` 均通过：
1. 严格 UTF-8 解码（`UTF8Encoding(false, true)`）；
2. JSON 解析；
3. 可见活动牌（暗手牌 + 摸牌 + 四家河 + 副露 + 宝牌指示牌）tileId 唯一性检查；最大落汗样本 83 个引用、0 重复。该检查只证明该视角内无重复，不等于完整 136 张守恒审计。

### 4.3 与历史数字的关系

- 历史 `CLAUDE-TCP-T0-MEASUREMENT.md` 记录 `PLAYER_UPDATE max=10,128 B / 320 次`。本次同源官方 T0 工具独立运行得到 `max=10,026 B / 380 次`，本次宽采样工具得到 `11,234 B / 444 次`（落汗生效的子集）。
- 这些差异来自不同随机牌局、不同座位视角和不同采样窗口，不应把 10,128 或 11,234 任一个写成“V4 全局最大值”。可以确定的是：**历史 10,128 B 已被本次 11,234 B 超过**，因此它不是安全上界。
- 1 MiB 是协议防御性上限；11,234 B 只表示本次观测值，占上限 1.07%，不代表还有 98.9% 的“可用容量”，也不代表 Sleeve/Beam 晚局、超长昵称、多局累计或 V5 Protobuf 的结论。

### 4.4 三绝技覆盖的明确限制

- 落汗在 5 局中都成功，且效果跨回合保留，因此有机会在晚局（wall 1）被采样，形成 11,234 B 最大样本。
- 袖里藏花/偷梁换柱的 `pendingOffer` 只在其触发回合出现；本次 5 局均在早巡（wall 67–68）出现，样本 n=5，最大仅 6.5–6.9 KB。它们**没有**在晚局、牌河更长、副露/宝牌更多时被采样。
- 因此本文件不能回答“若 Sleeve/Beam 在晚局挂起，载荷是否超过落汗的 11,234 B”。补验需要在同一局内把这两种 offer 推到晚局，或构造多局/受控状态；本轮未做。

---

## 5. 实际执行命令与退出码

以下均在 `D:\majiang` 执行；Gradle 使用仓库 wrapper，退出码为 PowerShell `$LASTEXITCODE`。

| # | 命令（摘录） | 结果 | 退出码 |
|---:|---|---|---:|
| 1 | `.\gradlew.bat -I "D:\majiang\build\v5-p03\v5-p03.init.gradle" "-Pkotlin.incremental=false" :server:test --tests "com.majiang.review.V5P03PayloadMeasurementTest" --console=plain --no-daemon` | `BUILD SUCCESSFUL`；1 个测量测试通过；7 局 settled；见 §4 | 0 |
| 2 | `.\gradlew.bat :engine:test --tests "com.majiang.cheat.mark.TileMarkAuthorityContractTest" --tests "com.majiang.cheat.bloom.SleeveBloomContractTest" --tests "com.majiang.cheat.beam.BeamSwapContractTest" --tests "com.majiang.settlement.SettlementServiceContractTest" --tests "com.majiang.projection.PublicProjectionTest" --console=plain --no-daemon` | 5 个测试类 **79 项通过 / 0 失败 / 0 跳过** | 0 |
| 3 | `.\gradlew.bat :protocol:test --tests "com.majiang.transport.api.Mr10TileAndZlibMeasurementTest" --console=plain --no-daemon` | **4 项通过 / 0 失败**；校验 6 个历史样本、zlib 测量、解压炸弹防御 | 0 |
| 4 | `.\gradlew.bat :server:test --tests "com.majiang.server.mr10.Mr10PayloadCaptureTest" --console=plain --no-daemon` | **1 项通过**；6 阶段真实载荷全部捕获；早/中/晚/落汗/候选/结算 = 6,621/4,352/9,150/6,533/6,621/7,419 B | 0 |
| 5 | `.\gradlew.bat -I "D:\majiang\build\v5-p03\v5-p03-t0.init.gradle" "-Pkotlin.incremental=false" :server:test --tests "com.majiang.review.TcpT0PayloadMeasurementTest" --console=plain --no-daemon` | **1 项通过**；官方 T0 报告 `PLAYER_UPDATE max=10,026 B / 380` | 0 |
| 6 | `.\gradlew.bat :lwjgl3:build --console=plain --no-daemon` | `BUILD SUCCESSFUL`；桌面发行包/启动脚本构建 | 0 |
| 7 | `.\gradlew.bat :android:assembleDebug --console=plain --no-daemon` | `BUILD SUCCESSFUL`；APK 81,458,357 B，SHA-256 `D3D20F799DCA49508399EACEDE40DD975A01780F5C59BA83431FFED2C1B9D325` | 0 |
| 8 | `powershell -ExecutionPolicy Bypass -File tools\unity\check-env.ps1` | 必需项全通过；1 个 WARN：Unity 未捆绑 Android SDK/NDK | 0 |
| 9 | `& 'D:\Unity\dotnet\dotnet.exe' build D:\majiang\dotnet\Mahjong.sln -c Release --nologo --artifacts-path 'C:\Users\sncdc\AppData\Local\Temp\v5-p03\dotnet-artifacts'` | 5 个工程构建成功；0 警告 0 错误；但 **0 个 `.cs` 源文件**，Release DLL 各 584 B，为空程序集 | 0 |

### 5.1 工具问题与纠正（真实发生）

| 问题 | 现象 | 处理 | 对应退出码 |
|---|---|---|---:|
| 原 T0 独立套件无法直接运行 | `-I tools/architecture/v4-improvement.init.gradle :server:test --tests ...TcpT0...` 在 `:server:compileTestKotlin` 失败：`Mr03LobbyCacheReviewTest.kt`、`Mr03SessionCacheReviewTest.kt` 引用已删除的 UDP 类（`UdpClock`/`UdpFrame`/`UdpTableServer` 等），`Unresolved reference 'udp'` | 未修改这些陈旧文件；把 T0 单文件复制到忽略目录 `build/v5-p03/src-t0/`，用隔离 init 脚本运行；官方 T0 通过（见 #5） | 原始尝试 1；隔离后 0 |
| 外部临时目录的 Kotlin 增量缓存根不一致 | 把 harness 放在 `%TEMP%` 时，`compileTestKotlin` 报 `this and base files have different roots`，增量缓存无法关闭 | 把 harness 移到仓库内被忽略的 `build/v5-p03/src/`，并加 `"-Pkotlin.incremental=false"`；成功 | 失败尝试 1；成功 0 |
| `-Pkotlin.incremental=false` 被 PowerShell 拆成两个参数 | Gradle 收到 `-Pkotlin` + `.incremental=false`，`android` 工程把 `kotlin` 解析成 String，报 `No such property: compilerOptions` | 给整个参数加双引号后重跑 | 失败尝试 1；成功 0 |
| Kotlin daemon 连接失败 | 多次出现 `Failed connecting to the daemon in 4 retries`，Gradle 自动 fallback 到无 daemon 编译 | 未阻断；仅耗时增加。补验可先 `.\gradlew.bat --stop` | 不影响最终 0 |

这些是**测量工具/环境**问题，不是 V4 业务测试失败；失败的尝试没有计入“通过”。

### 5.2 测试计数边界

本次实际执行并通过的定向测试共 **86 项**：79（engine）+ 4（protocol Mr10）+ 1（server Mr10 capture）+ 1（官方 T0）+ 1（外部 P0.3 测量）。这是 V4 定向抽样与测量，不是全量回归，不是 V5 测试，也不能替代 P0.1 已记录的 V4 证据。未运行 `.\gradlew.bat test` 全量套件。

---

## 6. 构建与环境能力矩阵

| 能力 | 实测结果 | 证据/限制 |
|---|---|---|
| V4 桌面（JVM/LWJGL3）编译与发行包 | **通过** | `:lwjgl3:build` exit 0；本次未启动 GL、未生成/检查截图 |
| V4 Android Debug APK | **通过** | `:android:assembleDebug` exit 0；APK 81,458,357 B；出现 D8/Kotlin metadata 警告与 SDK XML 版本警告，未阻断 |
| V4 单元/定向测试 | **部分通过** | 86 项定向通过；未跑全量 |
| .NET SDK / 共享工程构建 | **空脚手架通过** | .NET 8.0.426；5 工程 0 warning/0 error；`dotnet/src` 下 **0 个 `.cs`**，Unity `Assets/Scripts` 下 **0 个 `.cs`**；Release 产物 5 个 DLL 各 **584 B**，Unity `Assets/Plugins/Mahjong` 下 5 个旧 DLL 各 **3,584 B**。不能据此称 V5 Domain/Contracts 已实现 |
| Unity Editor / 许可 / 工程 | **存在，未新建构建** | Editor 6000.3.27f1；Personal 许可 2026-10-23 02:11；`check-env.ps1` exit 0（1 WARN）；工程 1280×720；本次遵守“避免创建/删除 Unity 工程输出”，未跑 batchmode/import/build |
| Unity Windows Mono 播放器 | **历史日志，非本次通过** | `D:\Unity\logs\build-win2.log`、`build-win3.log` 记录 `Build Finished, Result: Success.`；`build-win.log` 为 Burst 失败。未复跑，不能作为本次 IL2CPP/Unity 结论 |
| Unity Windows IL2CPP | **不可构建** | IL2CPP 模块目录存在，但无 MSVC/`cl.exe`、无 Windows SDK、无 CMake/Ninja；未执行 build |
| Unity Android（IL2CPP/APK） | **不可构建** | AndroidPlayer 模块存在，但无内置 SDK/NDK；V4 SDK 有 platform 36/build-tools，**无 NDK**；未执行 Unity Android build |
| Android 真机/模拟器 | **不可用** | platform-tools 设备检查（`adb devices -l`，绝对路径）为空；无 `system-images`/AVD；未做安装、建连、后台恢复 |
| macOS / iOS | **不可用** | 无 `xcodebuild`/`xcrun`，无 macOS 主机或远程 Mac；未做 iOS 构建 |
| 跨物理网络（T4） | **未测** | 本机 WLAN 通网关与 8.8.8.8，但没有第二台受控物理主机/真机，也没有弱网损伤工具；未做长连、断线、资源回落 |

### 6.1 Unity / .NET 的关键事实

- `tools/unity/check-env.ps1` 必需项全部 PASS，唯一 WARN 是 Unity 未捆绑 Android SDK/NDK；这与“Android 模块已安装”不矛盾。
- V4 Android 构建中出现多组 D8/Kotlin metadata 警告（R8 8.9.35 对 Kotlin 2.4 metadata 报 `Should never be called`）及 SDK XML v4 警告，构建仍成功；这属于工具链版本匹配风险，APK 未安装/未运行，不能据此宣称 Android 运行时通过。
- `.NET` 构建产物来自空工程：`Mahjong.Domain/Contracts/Client/TableRuntime/LocalHost` 均无业务源码，Release DLL 各 584 B。**“dotnet build 成功”只证明工具链和工程骨架可用，不证明 V5 规则核心存在。**
- Unity 工程中 5 个共享 DLL 为 3,584 B 空程序集（与本次 .NET Release 产物不是同一批）；本次未替换、未重建。
- 任务书提到的 `UNITY-ENVIRONMENT.md`、Unity `Resources` 删除、`BurstAotSettings_StandaloneWindows.json` 在测量时包含于 P0.1 提交；之后被 `3581653` 从 MR 分支移出，现保留为未提交工作树变更。本任务没有改动这些文件。

---

## 7. V4 T4 缺口（单列，仍未验证）

V4 验收文档原文把以下项列为**未验证**，本任务独立确认当前环境仍无法执行：

1. **跨物理网络长连/弱网**：两台物理机之间的 MJ-TCP/1 长连、弱网、断线重连与恢复；本机没有第二台受控物理主机。
2. **玩家与观战各至少 3 次真实中断后的应用资源回落**：需要真实 socket/进程中断与资源计数；本次只有 InProcess 与单机测量，不能替代。
3. **Android 真机或模拟器建连和后台恢复**：`adb devices` 设备检查（绝对路径）为空，无 AVD/system image；未安装 APK，未验证后台/前台恢复。
4. 回环测试、构建成功、桌面 GL 快照**都不充当**上述测试的通过证据。

补验所需资源与建议命令（本次未执行，不写通过）：

```powershell
# 两台同一物理 LAN（或受控路由/VPN）主机：主机 A 起 V4 TCP 服务，主机 B 跑客户端/观战
.\gradlew.bat :server:run --args="--port 7777 --bind 0.0.0.0"
# 主机 B（或第二台机器）运行桌面客户端并连接 A 的 LAN IP；
# 用防火墙/路由或 tc/netem 制造中断与弱网，记录玩家/观战各 >=3 次中断后的线程、socket、内存回落。

# Android 真机/模拟器：platform-tools 已安装（37.0.1，但 adb 未加入 PATH）；仍缺 system image/AVD 与真机
$adb = "$env:LOCALAPPDATA\Android\Sdk\platform-tools\adb.exe"
& $adb devices -l
.\gradlew.bat :android:installDebug
& $adb shell am start -n com.majiang/com.majiang.android.AndroidLauncher
# 断网/切后台/恢复后抓 logcat，验证重连与资源回落。
```

---

## 8. 未覆盖范围

1. 未跑全量 `gradlew test`；86 项定向通过不等于全量回归。
2. 未通过真实 TCP socket 抓包测量；载荷来自 InProcess 权威 + 同一 Gson 编码器，不含 12 B 帧头、不含 TLS/压缩、不含慢端排队。
3. 未测整场（东南/半庄多局）累计最大，只测每局到首次结算；未测超长昵称、观战大量成员、极端副露/宝牌组合。
4. Sleeve/Beam 未在晚局挂起采样；三绝技最大值的“最坏组合”未覆盖（见 §4.4）。
5. 未测 V5 Protobuf/WSS/Unity 客户端；本次所有字节都是 V4 JSON。
6. 未新建 Unity 构建/导入，未跑 Windows GL 冒烟；未跑 IL2CPP、Unity Android、iOS。
7. 未做 Android 真机/模拟器、跨物理网络、弱网、后台恢复。
8. 未验证 macOS/iOS 工具链；本机没有相关资源。
9. 未对历史 T0 的 `10,128 B` 做追溯复现；只证明本次同源工具得到 10,026 B，且宽采样得到 11,234 B。

---

## 9. 原始结果与复核入口

| 内容 | 路径 |
|---|---|
| 分布汇总 CSV | `build/v5-p03/out/summary.csv`（SHA-256 `DF852A0F6928CD1DA7491A94278DD99928F693F8D425A7CE4E9FC5C5DC74C8C1`） |
| 最大样本 JSON | `build/v5-p03/out/MAX_*.json`（17 个） |
| P0.3 测量测试 XML | `build/v5-p03/server/test-results/test/TEST-com.majiang.review.V5P03PayloadMeasurementTest.xml` |
| 官方 T0 测试 XML | `build/v5-p03/server-t0/test-results/test/TEST-com.majiang.review.TcpT0PayloadMeasurementTest.xml` |
| Engine 定向测试 XML | `engine/build/test-results/test/TEST-com.majiang.cheat.*.xml`、`TEST-com.majiang.settlement.SettlementServiceContractTest.xml`、`TEST-com.majiang.projection.PublicProjectionTest.xml` |
| Protocol Mr10 XML | `protocol/build/test-results/test/TEST-com.majiang.transport.api.Mr10TileAndZlibMeasurementTest.xml` |
| Server Mr10 capture XML | `server/build/test-results/test/TEST-com.majiang.server.mr10.Mr10PayloadCaptureTest.xml` |
| Android APK | `android/build/outputs/apk/debug/android-debug.apk` |
| 测量工具（忽略目录） | `build/v5-p03/src/V5P03PayloadMeasurementTest.kt`，SHA-256 `5FB295AF32A88E6E833367731CDDE42079CC96FEC9AD5AA5DDA184609CA5BD61` |
| 官方 T0 隔离副本 | `build/v5-p03/src-t0/TcpT0PayloadMeasurementTest.kt`，SHA-256 `73991A96E90E5C02C76F9FEFCEBD1921AA1210C5822DAA161DECE6B531E51EDA` |

复跑测量（工具仍在时）：

```powershell
cd D:\majiang
$env:V5_P03_OUT = "D:\majiang\build\v5-p03\out"
.\gradlew.bat -I "D:\majiang\build\v5-p03\v5-p03.init.gradle" "-Pkotlin.incremental=false" `
  :server:test --tests "com.majiang.review.V5P03PayloadMeasurementTest" --console=plain --no-daemon
```

本次样本已归档到 `build/v5-p03/out/`；复跑前必须设置 `V5_P03_OUT` 才能让工具直接写入该路径。未设置时工具默认输出到 `C:\Users\sncdc\AppData\Local\Temp\v5-p03\out`，不会写入仓库内路径。

如 `build/v5-p03/` 被清理，应按 §3.2 的分类、§3.1 的场景和 §4 的样本哈希重建工具；不可把本文件中的数字直接当作新的运行结果。

---

## 10. 结论边界

- 本文件是 **P0.3 的实测记录**，不是 G0 门槛通过、不是 V5-1 验收、不是 V4 全量回归。
- 本文件只证明：在 2026-10-23 本次机器/提交/命令下，V4 可执行上述定向测试与测量，得到了 §4 的样本、分布和最大值；同时记录了无法执行的环境项与 T4 缺口。
- 没有把历史 10,128 B、1 MiB 上限、Windows Unity Mono 构建日志、空 `.NET` 程序集或 Android Debug APK 构建扩写成 IL2CPP/Android 真机/iOS/跨物理网络通过。
