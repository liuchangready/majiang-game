# 连接控制归属迁移与架构文档验收

日期：2026-10-07。状态：通过。

用户指定 Anti 开发、Codex 检视，并刷新旧设计文档。Anti 完成生产迁移、模块引用更新、9 项连接控制行为测试和五份文档初稿/返修；Codex 独立检查、删除旧占位文件、维护架构审查并执行验收。Anti 两轮进程均已正常退出，未执行构建或测试。Codex 最后仅补正文档实际流转和验收说明。

## 迁移边界

- `ConnectionControl.kt`、`DisconnectPolicy.kt` 从 engine 迁至 `server/src/main/kotlin/com/majiang/server/control/`，包名为 `com.majiang.server.control`。
- `ControllerRole`、`ConnectionStatus`、`SeatConnectionState`、`DisconnectMode` 随连接控制实现迁入 server；原 engine 文件删除，无旧包别名或转发壳。
- `ServerTableHost`、`UdpTableServer` 和既有宿主测试只更新导入。剥离包声明、注释及空白后，两份迁移文件与基线实现完全一致。
- engine 的 `SeatController`、`ControllerRegistry` 和 `SeatControlView` 保留；无反向依赖、协议更改、规则改写或依赖升级。
- 新增独立架构门槛，要求六个连接类型唯一归属于 server/control，且生产源码不得引用旧连接包。

## 验证结果

| 检查 | 结果 | 证据 |
|---|---|---|
| 不筛选全量 `test` | 928 项，失败/错误/跳过均为 0 | `build/control-migration/full-checks.log`、`full-test-counts.json` |
| 独立 V4 审查 | 189 项，失败/错误/跳过均为 0；其中最终架构门槛 7 项通过 | `build/control-migration/independent-review.log`、`independent-counts.json` |
| 桌面与 Android 构建 | `:lwjgl3:compileKotlin :android:assembleDebug` 成功 | `build/control-migration/full-checks.log` |
| 真实 LWJGL/GL 产品冒烟 | `RENDER_SMOKE_PASS frames=367 commands=4` | `build/control-migration/desktop-smoke.log` |
| 源码与文档审计 | 两文件实现等价、旧文件已删除、五份文档本地链接无失效 | `build/control-migration/source-doc-audit.json` |
| Mermaid 语法 | 五份文档中的 13 张图全部解析成功 | `build/control-migration/mermaid-check.json` |
| 验收后源码冻结 | 323 份 Kotlin/Java 源码哈希无变化 | `build/control-migration/accepted-source-hashes.json` |

全量模块计数：engine 340、protocol 140、client 125、server 130、presentation 193。未改动的 protocol/client 测试由 Gradle 增量检查确认有效，受影响模块测试实际重跑。Gradle 配置任务的 SKIPPED 与 NO-SOURCE 不计作测试跳过。

本次起始提交 `dbe0ecf` 已删除 59 项重复渲染测试，基线为 919 项；新增 9 项后为 928 项。该清理发生于本次任务之前，本次没有删除或跳过有效测试。历史 R6 的 978 项与 188 项记录保持原样。

新增策略测试覆盖立即 AI 接管、宽限到期、批量接管、仅超时模式、重复掉线不延长宽限、重连不自动收回、显式收回与代次拒绝。既有全量与独立用例继续覆盖实际 UDP、进程内绑定、恢复、顶号、座位权限、计时与物理守恒。

## 文档刷新

更新 [开发约定](../../AGENTS.md)、[设计导航](../README.md)、[V4 架构](../GAME_ARCHITECTURE_V4.md)、[整体框架](../FRAMEWORK_ARCHITECTURE.md)、[通信接口](../SERVER_CLIENT_API.md)。编译依赖与运行调用链分开描述，protocol 标明为独立库；修正 UDP、完整快照、独立投影游标和重传窗口；分别绘制房间、整场和单局生命周期。

文档终审按 `MatchSession.prepare` 修正直接提交的整场状态，未把保留枚举误画为必经阶段；按 `RoundTransitionPlanner` 补正直接舍牌、暗杠、加杠抢杠和新局实例语义。客户端映射器、目录归属、观战无 seat、主动离房与 ROOM_CLOSED 的差别均按源码核对。Mermaid 检查验证语法，不代表完成所有渲染器的视觉检查。

本次未重构公开 coordinator 或 client.api 包布局；它们属于先前审查提出的其他封装改进，不在此次连接控制迁移范围内。
