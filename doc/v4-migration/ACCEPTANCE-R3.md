# R3 验收准备

当前尚未开发/验收，R2 通过后由 Anti 实现。以下是 Codex 的行为验证矩阵，不是通过记录。

| 场景 | 必须观察到的结果 |
|---|---|
| 订阅时同步早包、建房响应晚到 | 先注册监听，绑定后按相同身份排序；响应不能覆盖已安装的新快照 |
| 同代次 cursor 2 先于 cursor 1 | 未补齐前不开放未来决策；补齐后按顺序发布，恢复请求在途去重 |
| 全量替换标记、窗口外恢复 | 建立新基线并清理失效缓冲；未标记完整替换的缺口不能被跳过 |
| 切房、同房新 epoch、旧连接晚包 | 只有当前绑定的 sessionId/seat/epoch 和回调代次有效 |
| 恢复失败后再次收包 | 释放恢复在途标记，后续仍能重试，不能永久挂起 |
| 拒绝、异常、取消、关闭和晚回执 | Future 全部有终态；pending 正确恢复；晚回执不污染新会话 |
| 注入手动 dispatcher | UI 更新和回执消费均经指定派发；持锁时不调用外部监听；关闭后待派发旧消息无效 |
| 临时掉线、假时钟退避 | 保留 UI 视图与恢复身份，按退避连接并恢复绑定/订阅，不显式 claim 则不抢回控制 |
| 主动退出、被顶号、房间关闭、停服 | 不自动恢复旧席位，不创建多重重连循环 |
| 本地进程内连接 | 相同业务运行时，不创建心跳或网络重连线程 |
| 真实 UDP | 连接工厂恢复后从权威当前状态继续，托管动作与时钟不回滚 |

保留旧 ClientSynchronizationAcceptanceTest/RecoveryProjectionAcceptanceTest 的有效行为覆盖，尤其跨局同内容事件不能错误去重、集合不能跨监听器污染。模块测试迁移须记录替代覆盖。独立测试待 R3 API 成形后接入；全量模块测试和平台构建仍是放行条件。

## 开发前失败基线

2026-10-05 20:17，V4RuntimeAcceptanceTest 五项均编译成功、行为失败：重复/外来消息过滤、缺口顺序、早推送与晚绑定对账、关闭结束在途请求、关闭后回调失效。日志 build/v4-migration/R3-runtime-baseline.log，详情 FEEDBACK-R3.md。这是 R3 待实现行为，不覆盖 R2 已归档的通过结果。

后续扩展首批失败基线到 7 项（R3-runtime-baseline-2.log）。R3 开发期间再增加假时钟、派发、取消、旧代次、退出与全量标记等用例，目前 V4RuntimeAcceptanceTest 共 19 项；V4RuntimeUdpAcceptanceTest 两项分别验证真实两次断网恢复和最终关闭释放接收线程。新增用例尚待编译后执行，不能当作通过记录。

20:31 Anti R3 首次交付返回 SUCCESS，但 20:33 的 R3-delivery-check.log 仍因 finalSnapshot 未初始化无法编译。当前统一运行时与模块测试已经落盘，旧产品入口未变。20:33 交 R3FIX1 修编译及 socket 清理与共享 scheduler 关闭顺序；完整反馈见 FEEDBACK-R3.md。尚不允许进入 R4。

20:38 R3FIX1 期间生产编译通过，20 项假连接测试全绿；client 模块测试编译失败，新增用例引用不存在的 ProtocolGameEvent.RoundStarted。20:40 R3FIX1-full-review.log：74 项独立检查全部通过，包含 R1/R2 的 52 项、R3 假连接 20 项及真实 UDP 2 项（连续两次断网恢复、最终接收线程释放）。R3FIX1-module-interim.log：其余模块测试及桌面/Android 构建执行成功，但整体仍被 client 测试编译阻挡，尚未放行。

20:41 补验同 controlEpoch 内决策晚回包、UI 队列中的查询成功在关闭后失效、排队的活跃状态在关闭后失效。3 项均失败，原 20 项仍通过（R3FIX1-late-response-check.log）。这是既定 R3 单调同步与关闭语义，完整复现已交 FEEDBACK-R3FIX1.md。当前 23 项假连接用例 + 2 项真实 UDP；R3 继续返修，R4 尚未启动。

20:44 模块测试已可编译，client 76/80。发现无进展恢复响应导致递归重复请求及数百次相同基线通知；另外几个模块失败涉及新增假连接未设置挂起 leave、重复游标测试仍期望透传、状态初始重放和 KICK 终态旧断言。分别要求修生产与测试接线，不能修改假数据掩盖恢复循环。

20:46 R3FIX1-interim-3.log：独立假连接 22/24，新增无进展恢复测试确认递归 769 次；另一个失败是 close 直接完成 Future 绕过 dispatcher。同 epoch 决策晚包和关闭后活跃状态已通过。client 仍 76/80。完整最新反馈在 FEEDBACK-R3FIX1.md；尚未验收。

## 最终验收：通过（2026-10-05 20:51）

R3FIX1 已修复上述回归，包含主动 leave 断线时清除旧绑定。R3FIX1-final-review.log：78 项独立检查全部通过（R1/R2 52 + 假连接运行时 24 + 真实 UDP 2），零失败零跳过。真实 UDP 通过代理双向丢包触发实际心跳超时，连续恢复两次，第一次恢复后跨过旧 transport 清理窗口；另验证最终 runtime.close 后接收线程退出。

R3FIX1-final-checks.log：全部模块 test、桌面 compileJava 与 Android assembleDebug 成功。模块 XML 总计 745 项：engine 340、protocol 99、client 83、server 40、presentation 183，零失败、零错误、零跳过。engine/protocol 为 UP-TO-DATE（protocol 本阶段已单独运行），client/server/presentation 本次执行。计数 R3-accepted-test-counts.json，源码哈希 R3-accepted-source-hashes.json。

R3 接受唯一 V4 ServerConnectionSession 运行时、明确全量标记、缓存/缺口恢复/派发/生命周期及自动重连实现。现有产品入口仍待 R5 接入，旧 GameClient 仅阶段共存到 R6。当前代码可进入 R4；需等待本轮 Anti 调用结束，避免与下一阶段写入重叠。
