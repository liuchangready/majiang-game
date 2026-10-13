# SB-KAN-DORA 独立验收

日期：2026-10-13。结论：**Codex 独立检视与复测通过，未发现阻塞问题**。代码与文档保留在工作区，未提交或推送。

## 分工与回调

Anti 按[任务契约](TASK-ANTI-KAN-DORA.md)开发生产代码与文档；Codex 编写全部新增测试并独立检视。[首轮反馈](FEEDBACK-KAN-DORA-REVIEW1.md)指出流程图与正文冲突、里宝资格表述过宽、历史验收边界及无关代码修改，Anti 已修正并通过[第二版回调](CALLBACK-ANTI-KAN-DORA.json)交回检视，CLI 正常退出并输出 `CODEX_CALLBACK SB-KAN-DORA ready_for_review`。

## 验收行为

- 暗杠成立即翻本次杠宝；大明杠、加杠补牌后暂不翻，弃牌后、响应裁决前翻开。
- 明杠、加杠后直接岭上自摸不计尚未翻开的杠宝；暗杠岭上自摸计入新杠宝。里宝仅立直和牌可用，数量对应已翻表宝。
- 连续杠在后一次杠成立、补牌前处理前一次待翻；被抢杠不补牌、不增杠宝，也不提前翻前一次待翻指示牌。终局清除待翻状态而不补翻，新局复位。
- 袖里藏花激活和选择不翻宝；超时保持第一张后弃牌正常翻宝。候选仍为原岭上牌与下一张未取岭上牌，第四杠拒绝且不扣费。
- 待翻状态正确复制，玩家/观战快照及实际算分只使用已翻指示牌；服务端重复请求不重复翻牌。136张物理守恒及原有规则测试保留。

## Codex 独立验证

新增 [SleeveBloomKanDoraTest.kt](../../engine/src/test/kotlin/com/majiang/cheat/bloom/SleeveBloomKanDoraTest.kt) 15项和 [SleeveBloomKanDoraHostTest.kt](../../server/src/test/kotlin/com/majiang/server/bloom/SleeveBloomKanDoraHostTest.kt) 1项。覆盖真实三类杠、FIRST/SECOND、超时、连续四杠、混合连续杠、抢杠、实际自摸/荣和/里宝算分、复制与终局复位、服务端队列、协议编解码、公开推送及重放。

独立执行 `./gradlew test :lwjgl3:classes :android:assembleDebug --rerun-tasks --no-build-cache`，退出码0，86个任务全部执行，**1131项通过，0失败/错误/跳过**：

| 模块 | 测试数 |
|---|---:|
| engine | 427 |
| protocol | 158 |
| server | 142 |
| client | 136 |
| presentation | 268 |

本地复测证据：`build/sb-kan-dora-codex-full.log`、`build/sb-kan-dora-codex-results.json`（构建目录不纳入版本控制）。

独立执行 `./gradlew -I tools/architecture/sleeve-bloom-smoke.init.gradle sleeveBloomSmoke -PsmokeWidth=960`，退出码0，结果 `SLEEVE_BLOOM_GL_PASS choices=2 fee=20 frames=573`；日志 `build/sb-kan-dora-codex-gl960.log`。检查960×540候选面板截图，按钮与两张候选可见。Anti另完成1280与960视口冒烟，见[开发交付](DELIVERY-ANTI-KAN-DORA.md)。GL验证正常摸牌的真实施术流程；本次杠宝时序由领域与服务端测试验证，不将该GL记录表述为实际杠牌演示。

## 文档与边界

Codex最终补齐流程图的连续杠循环及规则文档旧归档链接；同步[偷梁换柱设计](../beam-swap/DESIGN.md)中的待翻指示槽候选语义和最多13张暗候选。偷梁换柱仍是设计稿，本次没有实现该千术。相关Markdown链接、编码及差异空白检查通过。

本次仅对齐雀魂式杠宝翻开时机，不宣称项目全部日麻规则与雀魂一致。Android APK构建通过，实体机触控未验证。用户既有截图工具、截图目录与文档索引改动保留。
