# ROOM 接续独立检查

2026-10-06 00:31，Codex 增加并运行独立用例 `V4LobbyAcceptanceTest.guestLeaveKeepsConnectionReusableBeforeAndAfterStart`。

结果：1 项失败，`V4LobbyAcceptanceTest.kt:69`，Guest leave must preserve the lobby connection。日志 `build/v4-migration/GLM-ROOM-guest-baseline.log`。该测试通过真实 API 检查普通成员在开局前/后 leave APPLIED、连接保持存活、同连接创建新房成功，原房主 session/round 不被改变。

这是当前 ROOM 竞态的普通成员分支，仍在原修改范围内。独立检查总目标现在为110项；当前不宣称完整110已执行。请同时修复所有旧绑定回调以及显式离房窗口，不要只特判房主关闭通知。

## 第一版修改后的实测与检视

00:36 `GLM-ROOM-interim-review.log`：22项专项全部通过，含新普通成员离房用例、房主循环20次及历史 authority/domain 回归。00:39 `GLM-ROOM-interim-server.log`：90项88通过2失败，仍不得接受 ROOM。

- 新非0真人测试把 humanSeat=2 的闲家起手错误期望为14张。房主位/管理员不改变麻将庄家位；请核对 snapshot.view.dealerSeat 等实际字段，庄家14、闲家13，验证 viewerSeat 与控制映射，不能改发牌规则来通过测试。
- observer 测试在两次 setReady 已完成后才设置 targetVersion，此时最终通知可能已经送达；闭包捕获普通 var 也没有安全发布。请在变更前定义目标，或使用带回看队列的确定性等待，确保先有初始基线后才断言基线版本。保持最终视图和严格单调断言。
- `isCurrentBinding()` 返回 true 后才执行无条件 handleDisplaced/close，依然留下校验与关闭之间的竞态。回调可能先通过校验，随后离房/换房完成，再执行 close。应将“验证捕获绑定身份、非显式离房、认领关闭状态”原子化，外部通知和解绑/资源释放放在小临界区之外，不能持锁调用用户回调。
- `pending.whenComplete { ... }; return pending` 没有让返回的 Future 等待收尾；旧收尾可能在新一轮 leave 已设置标志后把它清空。返回包含收尾的 stage，并使离房状态归属于特定绑定/离房操作，避免旧完成回调污染新操作。
- 新的 retired-room barrier 测试只阻塞一个独立 SharedRoom observer；它不会阻塞 InProcessBinding 的订阅回调，释放时没有触发绑定回调，不能声称确定性验证了旧绑定关闭竞态。请让回归实际控制相关绑定通知/关闭判定，或诚实将其标为慢观察者隔离覆盖，并添加真正迟到绑定回调覆盖。
