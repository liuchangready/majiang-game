# B1 小批修复

上一轮 CLI 超时，没有交付。只修改 core/src/test/kotlin/com/majiang/screen/GameScreenTest.kt，不要读超过必要的 6 个文件，不调查算法。

第一步立即补 GameState/PlayerView 缺失导入并保存。接着将 realScreen 及前2个测试迁移到真正 TableHost→GameClient→V3GameClientAdapter（参考 core/src/test/kotlin/com/majiang/v3/ScreenRecoveryAcceptanceTest.kt 和 MahjongGame.launchGame）。保留原4个测试。

目前 realScreen 返回 fake，而打牌测试调用 fake.choose 只记录动作没推进引擎。必须通过 screen.submitActionAsync 或真实UI点击提交，并断言手牌物理张数/弃牌确实进入本人牌河，不能只检查APPLIED或假计数。使用真实等待回执和 UI flush；不要 UI 线程 join UI 派发的 Future。辅助 FakeScreenClient.flushUi 由 A1 维护可沿用；无需更改共享 helper。启动按钮按现布局处理（不要固定过期坐标）。各测试释放 screen/host。

不改其它文件，不执行终端/测试。完成后写 delivery-ANTI-V2FIX-B1.md，Codex 运行测试。
