# SB-KAN-DORA：雀魂式杠宝牌时机与袖里藏花联动

日期：2026-10-13。用户授权 Anti 开发，Codex 编写独立测试并最终检视。阅读根 AGENTS.md。本次在现有 sleeve-bloom 上修正规则，不重新开发已验收功能，不实现 beam-swap。

## 规则契约

1. 暗杠成立后立即翻开本次杠宝牌，再摸岭上牌。大明杠/加杠成立后先补牌，本次杠宝牌延迟到该玩家弃牌时翻开；弃牌响应/荣和算分前已经生效，不等待弃牌无人荣和才翻。
2. 加杠先完成抢杠裁决，被抢杠的这次杠不增加杠宝牌、不补牌。连续杠时，前一次已成立明杠的待翻指示牌，在后一次杠实际成立、下一次岭上摸牌前翻开；后一次若为暗杠立即再翻本次，若为明杠/加杠则留下本次待翻。后续加杠被抢时不得提前揭示前一次仍待翻的指示牌。
3. 明杠/加杠后直接岭上自摸，本次尚未翻开的杠宝不计入；暗杠后自摸计入新杠宝。里宝数量仅对应已翻表宝。最多五张表宝，终局清除待翻状态但不得补翻，新局复位。
4. 待翻状态属于单局 GameState，应正确深拷贝、copyFrom、reset、事务提交/回滚，添加合理守恒/范围审计。不要通过 totalKanCount 推导为全部已翻，不能因快照/恢复自动翻开或泄露隐藏实体。
5. sleeve-bloom 激活、FIRST/SECOND、取消、拒绝与快照查询均不触发翻宝，第二候选仍仅下一张未取岭上牌，第四杠仍拒绝且不扣费。超时先保持 FIRST 再默认弃牌，此弃牌正常触发待翻宝牌；选择 SECOND 后岭上和牌用已翻宝牌计算。
6. 服务端权威状态、私有/公开快照、可见事件、客户端与 UI 同步遵守真实翻开时点；禁止仅 UI 隐藏、实际算分提前生效。立直弃牌入口与普通弃牌均须审计，不破坏既有抢杠、四杠散了、136 张守恒、deadline 和幂等。

资料：雀魂规则整理（非官方） https://mahjongsoul.club/content/rank-0 ，天凤同类规则的官方说明 https://tenhou.net/man/#RULE 。本项目只采用上述杠宝时机，不宣称完整对齐雀魂所有规则。

## 分工、验证及回调

- Anti 修改必要生产代码及 doc/RIICHI_RULES_DESIGN.md、doc/sleeve-bloom/DESIGN.md / STATUS.md（历史验收只作历史），必要时更新 API 文档。Codex 独占所有测试及 tools 脚本；不得删改/跳过测试以凑通过，测试疑问先写反馈。
- Codex 正在新增 `SleeveBloomKanDoraTest.kt`。开发完成先等 `build/sb-kan-dora-tests-ready.json`，再运行 :engine:test --tests '*SleeveBloom*'，然后全量 test :lwjgl3:classes :android:assembleDebug --rerun-tasks --no-build-cache。开发期间 Anti 独占 Gradle，Codex 不并行运行；回调后交还。失败先修复再交付，不得带已知失败报告完成。
- 保留用户既有 doc/README.md、lwjgl3/build.gradle、SnapshotLauncher.kt、snapshot/、doc/beam-swap/ 工作。不 Git 提交/推送，不启动其他代理或新 Codex 进程。
- 完成写 `doc/sleeve-bloom/DELIVERY-ANTI-KAN-DORA.md` 和 `doc/sleeve-bloom/CALLBACK-ANTI-KAN-DORA.json`，回调字段 recipient=codex、taskId=SB-KAN-DORA、status=ready_for_review、测试命令/真实结果/日志/变更列表/限制。最后 stdout 输出 `CODEX_CALLBACK SB-KAN-DORA ready_for_review` 并退出。这是回调当前 Codex 检视的交付信号，不代表最终验收；Codex 发现问题将要求改进后重新回调。
