# 连接控制归属迁移与当前架构文档刷新交付记录 (DELIVERY-ANTI-CONNECTION-OWNERSHIP)

> Codex 终审：本交付已接受，两个 engine 占位文件已删除，测试与文档校对已完成，见 [验收记录](ACCEPTANCE-CONNECTION-OWNERSHIP.md)。下文“待执行”保留为 Anti 交付时的历史清单。Codex 在 Anti 退出后补正整场实际提交路径、单局分支、投影授权表述和验收注记，未改动生产实现。

- **交付日期**：2026-10-07
- **执行角色**：Antigravity (Anti)
- **监督与验收**：Codex (独立构建、测试与最终验收)
- **交付范围**：连接控制类完整迁入 server 模块、server 引用更新与策略测试补齐、5 份架构与规范文档全面精确刷新
- **状态说明**：R1~R6 为历史已由 Codex 验收记录；本次连接控制所有权迁移与文档精确返修已全部完成，生产代码与测试均已冻结，待 Codex 独立验收。

---

## 1. 源码与测试所有权变动清单

### 1.1 迁入 server 的生产代码
| 文件路径 | 新包名 | 说明 |
|---|---|---|
| `server/src/main/kotlin/com/majiang/server/control/DisconnectPolicy.kt` | `com.majiang.server.control` | 包含 `DisconnectMode` 与 `DisconnectPolicy`，连接宿主策略配置 |
| `server/src/main/kotlin/com/majiang/server/control/ConnectionControl.kt` | `com.majiang.server.control` | 包含 `ControllerRole`、`ConnectionStatus`、`SeatConnectionState` 与 `ConnectionControl` 核心逻辑 |

### 1.2 待物理删除的原 engine 文件
由于本任务限制 Anti 仅使用内置文件编辑工具，原 engine 两文件内容已被清空并标注迁移标记（确保 engine 不再对外导出此类，杜绝隐式引用）：
- `D:/majiang/engine/src/main/kotlin/com/majiang/control/ConnectionControl.kt`
- `D:/majiang/engine/src/main/kotlin/com/majiang/control/DisconnectPolicy.kt`
- 待删除空目录：`D:/majiang/engine/src/main/kotlin/com/majiang/control/`
> **待 Codex 执行操作**：在验证目标后物理删除上述两文件并确认目录为空后清理。无任何旧包 typealias 或兼容壳残留。

### 1.3 更新引用的生产代码与模块测试
| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `server/src/main/kotlin/com/majiang/server/host/ServerTableHost.kt` | 生产引用更新 | 导入改为 `com.majiang.server.control.ConnectionControl`、`ControllerRole`、`DisconnectPolicy` |
| `server/src/main/kotlin/com/majiang/server/udp/UdpTableServer.kt` | 生产引用更新 | 导入改为 `com.majiang.server.control.DisconnectPolicy` |
| `server/src/test/kotlin/com/majiang/server/host/ServerTableHostTest.kt` | 测试引用更新 | 导入改为 `com.majiang.server.control.DisconnectPolicy` |
| `server/src/test/kotlin/com/majiang/server/host/ServerTableHostCoverageTest.kt` | 测试引用更新 | 导入改为 `com.majiang.server.control.DisconnectPolicy` |

### 1.4 新增的连接控制直接行为测试
在 server 模块补充了全面的单元回归用例（9 项）：
- `server/src/test/kotlin/com/majiang/server/control/ConnectionControlTest.kt`
  - `immediate ai mode transitions to takeover immediately on disconnect`：立即接管并自增 `controlEpoch`；
  - `grace then ai mode waits for deadline before takeover`：宽限期内保持 HUMAN，到期后原子切换为 TAKEOVER 并自增代次；
  - `pollPendingTakeovers finds all expired seats`：批量轮询到期座位；
  - `timeout only mode never triggers ai takeover`：仅超时策略不触发接管；
  - `repeated disconnect is idempotent and does not extend grace deadline`：重复断线幂等防抖，不顺延截止时间；
  - `reconnect without reclaim keeps takeover and requires explicit reclaimControl`：重连不自动收回控制权，显式调用 `reclaimControl` 时自增代次；
  - `reconnect with reclaim restores human role and increments epoch`：重连时带 reclaim 立即恢复真人并自增代次；
  - `stale expected epoch is rejected on grace check`：过期代次检查安全防御；
  - `requestControl and setRole behave correctly`：控制权切换与显式角色设置。

### 1.5 engine 领域控制权保留说明
`engine` 模块**严禁反向依赖 server**。本次迁移后，`engine` 保留以下完整的领域控制概念：
- `com.majiang.state.SeatController`（领域控制器密封类：LocalHuman / RemoteHuman / Computer / Takeover）；
- `com.majiang.application.ControllerRegistry`（权威领域代次维护者，`controlEpoch` 递增）；
- `com.majiang.access.SeatControlView`（供 `TableAuthority` 构造快照投影使用的座位控制视图）。

---

## 2. 行为等价性与契约不变性核对

| 契约项 | 迁移前后核对 | 是否改变 |
|---|---|---|
| **断线模式** | `IMMEDIATE_AI`、`GRACE_THEN_AI` (10s)、`TIMEOUT_ONLY` 行为完全一致 | 否 |
| **宽限期计时** | 基于 `MonotonicClock` 绝对时间戳计算 `graceDeadlineMillis` | 否 |
| **重连控制权** | 重连默认不收回控制权，真人重连后须显式调用 `claimControl()` 收回 | 否 |
| **controlEpoch 递增** | 任何角色变更或真人收回均自增 `controlEpoch`，废止旧决策凭据 | 否 |
| **幂等防抖** | 已断线座位重复触发 `disconnect` 幂等返回，不重置断线时刻与截止期 | 否 |
| **20+3 权威计时** | 20s 储备 + 3s 思考时间，权威 Deadline 优先裁决不变 | 否 |
| **公开接口与版本** | 未改动 coordinator 公开接口、未改动 client.api、未改动协议定义与库依赖版本 | 否 |

---

## 3. 架构与规范文档刷新清单

依据源码实际落地事实全面刷新了 5 份核心设计规范文档（经 Codex 初审意见逐条精确核实返修）：

1. **[`AGENTS.md`](../../AGENTS.md)**：
   - 彻底删除已废弃的 V3 兼容入口要求；
   - 更新七模块分工：连接控制属于 `server/control`，`presentation` 仅菜单/对局/共享依赖 `client.api`，组合根 `MahjongGame` 显式装配服务端与客户端；
   - 区分服务端 256 推送重传滑动窗口与客户端 64 有界接收缓冲（256 游标去重）；
   - 更新数据映射流水线（`ProtocolViewMapper`）；
   - 串行队列规范说明：统一入队排队，校验 deadline，tick 先处理超时再调度 AI，响应窗口允许多座位提交，动画仅门控客户端自动推进节奏。
2. **[`doc/README.md`](../README.md)**：
   - 标注连接控制归属 `server/control`，注明待 Codex 独立验收；
   - 修正队列描述（删除`动作 > 超时 > AI 托管 > 控制权切换`），统一为串行入队、先超时后 AI；
   - 守恒机制写明 TilePool 绑定时由 StateInvariants 审计容器与账本，不使用“作弊穿帮”代指内部审计；
   - 修正客户端映射器为 `ProtocolViewMapper`。
3. **[`doc/GAME_ARCHITECTURE_V4.md`](../GAME_ARCHITECTURE_V4.md)**：
   - 修正数据映射与时钟类型（`ProtocolViewMapper`，时钟 `ClockInfo`，UI 方法 `submit`）；
   - 拆分为 3 套独立状态图，彻底杜绝 `FINISHED` 重名冲突；
   - 修正 `SessionLifecycle` 状态（使用真实 `ROUND_ACTIVE`，无 `IN_ROUND`）；
   - 修正 `RoundTransitionPlanner` 真实流转：发牌原子经 `DEALING` 直达 `CHEAT_WINDOW`（庄家 14 张）、普通摸牌 `DRAW -> CHEAT_WINDOW`、加杠主动打开 `CHANKAN_WINDOW`、荣和或全员 Pass 摸岭上等完整路径；
   - 删除“局域网发现”已实现宣称，仅保留指定 IP/端口与房号加入；
   - 标注 R6 为历史验收，本次变更待 Codex 独立验收。
4. **[`doc/FRAMEWORK_ARCHITECTURE.md`](../FRAMEWORK_ARCHITECTURE.md)**：
   - 源码树结构精确匹配实际路径（`PlayerHand`、`ProtocolViewMapper`、`PushSequencer` 归属，client 无 `mapping/` 或 `sync/`，presentation 组合根在根包）；
   - 移除 `AnimationQueue` 类宣称，修正为 `GameScreen` 消费 `GameUpdateView` 的完整 `snapshot + visibleEvents`，由 `DiscardAnimation`、`TileDrawBuffer` 驱动显示；
   - 守恒审计准确描述为：`TilePool` 绑定状态时调用 `StateInvariants.assertValid` 执行真实容器、唯一实体 (0..135) 与账本一致性审计；
   - 声东击西修正为：制造响动使 AI 视线偏转，但出千合法性与判定仍由领域规则裁决；
   - 同步刷新 3 套独立生命周期状态图。
5. **[`doc/SERVER_CLIENT_API.md`](../SERVER_CLIENT_API.md)**：
   - 纠正总体架构图：`ServerConnectionSession` 使用 `ProtocolViewMapper` 并直接调用 `ServerConnection` 接口，InProcessBinding 与 UdpClientBinding 实现该接口，明确 `com.majiang.server.api` 属于 protocol 模块；
   - 准确离房语义：仅主动离房成功者回到未绑定大厅；房主离房时其他成员收到 `ROOM_CLOSED` 并终结连接；
   - 修正观战绑定：`SpectatorBinding` 严格不含 `seat` 字段，拥有独立 `spectatorId`，禁止写操作但可 `leaveRoom()` 与查询；
   - 明确对外接口为无参 `claimControl()` 与 `releaseControl()`，`ConnectionControl` 内部保留 `requestControl(seat, takeover)` 状态实现；
   - 修正时钟类型为 `ClockInfo`。

---

## 4. 尚待 Codex 执行的检查与验收工作

Anti 独占本任务生产代码与文档编辑，未执行任何构建、测试或 shell 命令，现转交 Codex 执行以下操作：

1. **更新独立审查用例 import**：
   - 更新 `tools/architecture/v4-review` 下的审查用例（如 `V4ServerHostAcceptanceTest.kt`）中涉及 `ConnectionControl` / `DisconnectPolicy` 的 import 改为 `com.majiang.server.control.*`。
2. **物理删除原 engine 空文件及空目录**：
   ```powershell
   # 物理删除两个已清空的占位文件
   Remove-Item -Path "D:/majiang/engine/src/main/kotlin/com/majiang/control/ConnectionControl.kt" -Force
   Remove-Item -Path "D:/majiang/engine/src/main/kotlin/com/majiang/control/DisconnectPolicy.kt" -Force

   # 确认目录已为空后安全移除空目录
   if ((Get-ChildItem -Path "D:/majiang/engine/src/main/kotlin/com/majiang/control").Count -eq 0) {
       Remove-Item -Path "D:/majiang/engine/src/main/kotlin/com/majiang/control" -Force
   }
   ```
3. **编译与自动化测试验证**：
   ```bash
   # 1. 验证 server 模块 (包含新增 ConnectionControlTest 及既有宿主测试)
   ./gradlew :server:test --info

   # 2. 运行无头全量测试
   ./gradlew test --info

   # 3. 运行独立架构审查用例 (tools/architecture/v4-review)

   # 4. 双端平台编译验证
   ./gradlew :lwjgl3:compileKotlin :android:assembleDebug
   ```
4. **验收核对**：
   - 确认无 engine 反向依赖 server；
   - 确认无旧 V3 兼容壳或残留 import 错误；
   - 确认 136 张物理守恒与有效规则测试全绿通过；
   - 确认 5 份规范文档内容严谨真实，与当前 V4 生产代码完全契合。
