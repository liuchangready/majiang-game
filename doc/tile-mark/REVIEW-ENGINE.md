# 落汗 Engine 独立检视

2026-10-11。Codex 编写测试并执行。当前只收到 E1 领域类型文件，不是整个 engine 功能交付。

## 执行状态与范围

Anti 会话 `85217479-d7a4-4c44-8c71-0e199277b7e2` 已结束并返回 E1_CODE_READY，报告 duration_seconds 为 1769.99（约 29 分 30 秒）。文件 `engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkModels.kt` 已实际存在，包含配置、请求、标记组、牌河位置四种类型。

此前 120 秒等待超时不能等同于任务完成或停止，遗留 RUNNING 文件也不能单独证明持续执行。最终回执与实际源码是本次确认完成 E1 编码的依据。日志出现过同会话历史摘要 `.pb` 文件缺失，但没有足够证据将长耗时归因于此；不能断言卡在某个工具或权限步骤。

## 验证与发现

命令：`./gradlew :engine:test --tests com.majiang.cheat.mark.TileMarkModelContractTest --console=plain`。

生产代码、测试代码编译成功；9 项测试实际执行，8 项通过、1 项失败。证据为 `engine/build/test-results/test/TEST-com.majiang.cheat.mark.TileMarkModelContractTest.xml`，未跳过测试。

发现 E1-01：`TileMarkGroup` 构造校验只比较 `expiresAfterRoundOrdinal == createdRoundOrdinal + 3`，没有约束创建局次至少为 1，也没有保护 Int 加法溢出。测试以 `createdRoundOrdinal = Int.MIN_VALUE` 构造标记组时，本应抛出 IllegalArgumentException，实际却成功。源码同样允许零局次及接近 Int.MAX_VALUE 的加法回绕，无法保证“已发牌局次”的状态不变量。

修复要求：将创建局次限制为 `1..Int.MAX_VALUE - 3` 或采用等价的不溢出校验，再验证到期值。保持正常局次和防御复制行为不变。由 Anti 改生产实现，Codex 保留失败测试并复验。详见 [修复任务](TASK-ANTI-E1FIX1.md)。

## 修复复验与完成回执

Anti 已补充 `createdRoundOrdinal in 1..(Int.MAX_VALUE - 3)` 前置校验，修复 E1-01。按用户更新要求，本次调用带 `--dangerously-skip-permissions`，允许 Anti 运行测试，但仍禁止修改测试或扩大生产代码范围。

Anti 会话 `8e982dff-4c2d-40a2-bb43-ff6bae2d55d9` 正常退出，向当前 Codex 返回 `CODEX_CALLBACK E1FIX1 ready_for_review`，并提交 [交付说明](DELIVERY-ANTI-E1FIX1.md) 与 [完成通知](CALLBACK-ANTI-E1.json)。这不是独立验收通过声明。

Codex 在独立重跑前已读取全量 XML：49 个套件、349 项测试，失败/错误/跳过均为 0，与 Anti 的交付数字一致。随后独立重跑模型测试，`:engine:test` 实际执行，9 项全部通过，退出码 0。测试文件 SHA-256 前后一致：`6C2222ACD55C1E4D9DF53D5DF609C5CE6261562F78B459953F5E81BA8DB72CF0`。Anti 未改测试。完整 engine 测试由 Anti 执行、Codex 核对报告；独立定向复验由 Codex 执行，不混同这两种证据。定向复验会覆盖默认测试报告目录，349 项全量结果保留于已核对的交付记录中。

## 当前结论

E1 模型批次及 E1-01 修复通过检视。费用原子提交、12 张容量、真实发牌局次、权威 deadline、私有投影与牌河实际定位、protocol/server/client 接线和 UI 均尚未交付，不能凭模型测试宣称落汗可用。本次未进行全模块或 UI 验收。

## E2 不可变跨局状态检视

Anti 会话 `449ea8ba-6cf6-4b70-8ab5-ab81d906db8e` 返回 E2 完成通知。Codex 阅读 `TileMarkState.kt`，核对只读集合、登记/结算幂等、每座位三组上限、跨座位共享实体及独立到期。

Codex 核对 Anti 实际全量 XML：50 套件、359 测试，失败/错误/跳过均为 0；随后独立运行 `./gradlew :engine:test --tests 'com.majiang.cheat.mark.*' --console=plain`，19 项测试实际执行全部通过（E1 9 项，E2 10 项）。E2 状态组件通过检视。

E2 尚未接入 MatchSession 和真实发牌。E3 已派发，Codex 新增 `TileMarkAuthorityContractTest` 验证完整权威行为；E3 交付前不能把 E2 的纯状态转换测试视为真实跨局功能验收。

## E3 首次交付：回调关闭死锁，退回修复

Anti 会话 `ce956219-b0af-42ad-9880-9683a070ea45` 正常退出并返回 `CODEX_CALLBACK E3 ready_for_review`。Codex 核对当时完整 XML 为 52 套件、376 项、失败/错误/跳过均为 0，四个测试文件哈希与 Codex 维护的版本一致。过程中 Codex 修正了准备阶段测试时钟（须等准备 deadline 才发牌），补充身份指纹、跨座位相同请求 ID、下一局准备阶段回执隔离；Anti 相应修复生产实现。

独立源码检视发现新 `ApplicationCoordinator.close()` 存在读锁升级写锁：提交 finally 清空 currentExecutingThread 后，在 read lock 下调用订阅者；订阅者关闭 TableAuthority 会阻塞请求同线程持有的 write lock。Codex 新增 `TileMarkCloseCallbackTest`，独立重跑落汗包实际 37 项，36 项通过、1 项因 TimeoutException 失败，复现死锁。原 376 项全通过记录没有覆盖这个新增场景，不能据此宣布 E3 验收。

已派发 [E3FIX1 修复任务](TASK-ANTI-E3FIX1.md)。要求关闭意图真正生效，提交正常返回且 CLOSED/标记清空，保留原子提交和发布隔离；不允许忽略关闭或修改测试。当前 E3 结论为未通过，协议生产接线仍未开始。

## E3FIX1 独立复验：Engine 阶段通过

Anti 会话 `58db7bf7-f88c-403a-b2f2-8bdd7eb55540` 返回 `CODEX_CALLBACK E3FIX1 ready_for_review` 并正常退出。修复通过 pendingClose 记录通知回调关闭意图，在所有广播入口释放读锁后、解除动作守卫前完成关闭；TableAuthority 在关闭后跳过成功分支的计时/回执回写，关闭入口可重复调用。

Codex 先核对全量 XML：53 套件、377 项，失败/错误/跳过均为 0；五个测试文件哈希均与 Codex 版本一致。随后独立运行 `:engine:test --tests 'com.majiang.cheat.mark.*' --console=plain`，37 项全部实际执行通过，退出码 0。新增关闭测试还验证 CLOSED、标记清空、无活动时钟、关闭后不能重放刚完成的成功回执。

Codex 进一步运行全模块 `test --console=plain`，Gradle 输出 BUILD SUCCESSFUL（31 秒）。报告合计 125 套件、965 项，无失败/错误/跳过：engine 377、protocol 140、server 130、client 125、presentation 193；其中 protocol/client 为 UP-TO-DATE，不能把这 265 项说成本次重新执行。lwjgl3 无测试源，Android test 聚合任务 UP-TO-DATE。PowerShell 全流重定向包装返回 1，但日志明确记录 Gradle BUILD SUCCESSFUL 和批处理 exit 0，实际 XML 无失败；不把包装状态误报为 Gradle 测试失败。日志在 `build/tile-mark/verification-engine-all-modules-E3.log`（本机构建产物）。

E3 及 E3FIX1 通过 Engine 阶段验收。已验证费用与标记原子生效、实体守恒、12 张上限、真实四局寿命、牌河到副露定位、私有投影、时钟延续及请求隔离。协议/客户端/表现层尚未接入，不代表用户已经可以在现有 UI 中使用落汗。下一阶段 A1 线路契约测试与任务卡已准备。
