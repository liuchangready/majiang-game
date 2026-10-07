# A1 小批修复

上一轮 CLI 超时，没有交付。请直接实施本小批；最多读必要的 6 个文件，不调查引擎实现。只修改 core/src/test/kotlin/com/majiang/testing/FakeScreenClient.kt 与 core/src/test/kotlin/com/majiang/GdxHeadlessTestRunner.kt。

修复 FakeScreenClient 的 V3 快照和 UI 等待：固定 cursor/roundId、丢失 settlementEvents、在 UI 线程调用 flushUi 自锁。保持公开 API。UI thread 用 HeadlessApplication.create 中记录 Thread，不通过线程名猜测；flushUi 在该线程立即返回，在非 UI 测试线程等待 FIFO barrier。不要在 choose/start 内 flush（whenComplete 此时尚未注册）；测试调用方负责输入后 flush。update/publish 从非 UI 调用时可 drain，UI 调用不阻塞。考虑嵌套 postRunnable 下一帧派发。

快照 update 应增加游标，保留当前 round 的终局恢复事件（TsumoWin/RonWin/ExhaustiveDraw/AbortiveDraw/ScoreSettled 等按实际 GameEvent 类型），后续空事件更新不能清空；进入新局时换 roundId 并清旧结算。参考 engine/src/main/kotlin/com/majiang/access/PlayerSnapshot.kt 和 GameEvent 类型即可。publish 传入快照身份不覆盖。防御性复制。

不修改其它文件，不执行 shell/测试。尽快完成两个文件并写 delivery-ANTI-V2FIX-A1.md，交给 Codex 编译验证。
