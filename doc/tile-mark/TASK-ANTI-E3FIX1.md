# E3FIX1：关闭通知回调死锁

前置交付 E3 的 36 项落汗/376 项 engine 测试及哈希已由 Codex 核对。Codex 新增 `TileMarkCloseCallbackTest`，独立运行整个落汗包实际 37 项、36 通过、1 失败：`completion.get(3, SECONDS)` 超时。当前尚未验收，禁止进入 protocol 和 Git 提交。

## 必修复缺陷

新 `ApplicationCoordinator.close()` 使用阻塞的 `rwLock.writeLock().withLock`。提交成功的 finally 已将 currentExecutingThread 清空，持有 read lock 调用 broadcastEvents；订阅者调用 TableAuthority.close → coordinator.close 时企图从自己的 read lock 升级 write lock，永久阻塞。原 currentExecutingThread 检查无法覆盖这段。

修复必须让通知回调里的 close 安全生效：施术提交返回，关闭最终完成且清空标记。不允许用直接 return 忽略关闭意图，不允许后台任意写 session，不允许破坏既有串行写守卫、原子提交和通知快照隔离。请自行选择与原事务发布兼容的延迟关闭/安全关闭机制，并检查 TableAuthority 成功分支 rekey 与关闭清理顺序，不能在关闭后重新创建时钟或回执。还应检查其他能发布事件的入口，避免仅特殊修补落汗回调而留下同一锁问题。

生产修改范围 engine。测试仍全部由 Codex 维护，禁止修改任何测试、删除测试或跳过用例。最新新增测试在读取完成后将断言提交 APPLIED、session CLOSED、groups 清空和再次 close 安全。请运行 `:engine:test --tests 'com.majiang.cheat.mark.*'` 以及完整 `:engine:test`，修生产代码直到通过；若测试本身有真实夹具缺陷，向 Codex 准确报告。

任务结束写 `DELIVERY-ANTI-E3FIX1.md`、`CALLBACK-ANTI-E3FIX1.json`（recipient=codex、status=ready_for_review 或 blocked），更新进度，输出 `CODEX_CALLBACK E3FIX1 ready_for_review` 并退出。保留原 E3 交付数字作为历史，不伪造覆盖。调用继续带 `--dangerously-skip-permissions`。不得改其他模块、Git 提交或新开代理。
