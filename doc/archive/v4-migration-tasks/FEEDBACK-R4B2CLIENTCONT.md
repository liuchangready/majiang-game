# R4B2 CLIENT CONT 运行中检视反馈（Codex）

18:12 静态查看新增 UDP PUBLIC 分支，已看到合法重复包 ACK、有界重排、完整 RESEND 和带 bindingEpoch 的快照采纳。本批仍在开发、尚未构建，下面是交付前仍需修正的具体路径：

1. UdpClientBinding 的 full 分支用 snap.cursor >= curCursor，每次同游标重试都会 toDispatch.add；ReplayGuard.accept 的返回值随后被忽略。合法重复 full 应 ACK 但不能再次回调。ServerConnectionSession.handleInboundPublicUpdate 的 full 分支也使用 >= 并再次派发，需端到端避免重复回调，不能依赖某一层碰巧吞掉。
2. triggerPublicSnapshotSelfHeal.finally 仍无条件 isHealingPublicSnapshot.set(false)；旧请求离房/换绑后完成可清除新恢复标志，或旧在途恢复未结束阻止新绑定恢复。需要每次恢复请求身份/绑定代次拥有标志，只有对应请求可结束自身恢复；原 FEEDBACK 中要求同样适用于 runtime 的 isPublicResuming。
3. PUBLIC 分支先独立锁检查角色，后进入另一个锁更新 lastPublic*，出锁到 dispatchPublicUpdate 又无捕获身份保护。leave/rejoin 可插在这些间隙；dispatchPublicUpdate 的直接监听/early buffer 不带 bindingEpoch。请让消费与所有延迟派发携带原绑定身份并核验，新绑定不得收到旧推送，不能用“派发时再读取当前 epoch”给旧数据赋新身份。runtime notifyPublicUpdateListeners 同样要使用来源绑定的 epoch/room generation，而不是在通知入口重新捕获当前 epoch。

仍需完成原 FEEDBACK-R4B2CLIENT.md 与 TASK 的全部行为和模块回归；以上只是当前已落盘实现的具体问题，不代替整体验收。

## 18:17 GLM额度退出后的实际验证

GLM两路均因429/1308五小时额度退出，未交付。R4B2-GLM-quota-compile.log 中 client 主源码编译成功；server 单独失败不属于你的文件范围。R4B2-GLM-quota-client-tests.log 在 compileTestKotlin 失败，未运行测试：ProtocolViewMapperTest 第38/376行使用不存在的 ProtocolPublicCheckpoint，真实协议类型为 com.majiang.server.api.PublicCheckpoint（可按需 import alias 避免 client 同名）；第573行 TileView 传入第四个 false，真实构造仅 tileId/suit/rank，isRed 由 tileId 派生。修正模块 fixture 的真实签名并保留断言，不给生产加兼容构造或删除用例。

Codex已加强独立UDP超窗full用例：代理重复真实full原seq/分片两次，要求ACK且用户回调一次；其余首消息/首ACK丢失、缺口、两次自动恢复与停服清理测试仍保留。当前测试未通过不等于可删除或缩小要求。

## 18:21 独立运行时实测：4项中2项失败

Codex新增V4SpectatorRuntimeIsolationTest.kt，只用真实ServerConnectionSession和可控协议传输Future/dispatcher，不依赖server或开发者模块fixtures。经v4-public-client-review.init.gradle实际执行，R4B2-GLM-quota-runtime-review.log及同名-results.json：4项，2通过、2失败，零错误/跳过。

- repeatedFullBaselineNotifiesOnlyOnce失败：相同cursor=3 full发两次，回调实际[3,3]，应[3]。
- queuedPublicCallbackCannotRunAfterSuccessfulLeave失败：先暂存PUBLIC通知，再优先执行leave成功收尾（缓存已null），之后执行旧通知仍调用监听器。这是已退回大厅仍泄漏旧房间UI通知，不是只靠竞态推测。
- oldRecoveryCompletionCannotClearNewBindingsRecoveryOwnership通过：当前roomEpoch校验能挡住顺序完成的旧Future，不等于已证明所有锁间并发及UDP isHealingPublicSnapshot归属。
- foreignOrOlderFullPushCannotReplaceCurrentPublicStream通过。

上述测试为Codex拥有，不修改或删除。修复后同时运行4项与完整real InProcess/UDP/模块回归；完整独立预计171项，以最终XML为准。
