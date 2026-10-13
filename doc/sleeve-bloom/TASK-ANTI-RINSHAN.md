# SB-RINSHAN：杠后候选仅限岭上牌

进度补充：Codex已处理首轮测试的夹具时钟遗漏，见[首轮反馈](FEEDBACK-RINSHAN-INITIAL.md)。不要为该失败放宽生产时钟门禁；请重跑最新测试后继续文案、文档及交付。

用户最新规则覆盖旧设计：正常摸牌规则不变；杠后袖里藏花只能在刚摸到的岭上牌和下一张未取岭上牌之间二选一，不能从活牌山头尾或王牌替补取第二候选。第四杠后禁止施术，不扣费。此前正常牌山头端两张的讨论已被用户最新指令替代，不实现。

## 实现契约

- 第一候选保持 drawnTile，第二候选为 deadWall[rinshanPointer]；必须 rinshanPointer in 1..3、totalKanCount < 4，校验原摸牌等于 consumed slot deadWall[rinshanPointer - 1] 且在本人手牌，下一岭上牌物理归属 DEAD_WALL。上层拒绝沿用 SLEEVE_BLOOM_WALL_EMPTY，UI 给出适用于候选不足的文案。
- 激活仍不移动牌，原子扣10并私有揭示；选择 FIRST 零物理变化；SECOND 将下一岭上牌移入 drawnTile，原摸牌写回下一岭上槽。rinshanPointer 不前进也不回退；普通牌山两端指针、内容、剩余数与 deadWallReplacements 完全不动。actual kan count、dora、时间、turnNumber 和 isRinshanDraw 不变。下一次真实补牌应取回未选原牌，第四次之后禁止施术/第五杠。
- 用 TileMovement.swapDrawnWithNextRinshan 替换旧 swapDrawnWithRinshanReplacement；前置校验在任何突变前完成，136实体守恒、有效王牌14、宝牌槽不动。删除旧错误原语，更新所有调用。
- 同步 TableAuthority 的激活和 stale offer 校验、ApplicationCoordinator 的执行校验、SleeveBloomProjection 私有投影门禁。不要只改UI或只改一个检查点。保持幂等、deadline、控制代次、私有推送、超时保留FIRST等既有契约。
- 更新面板候选标签为“岭上次张”、秘籍解释与中文字形覆盖，明确第四杠不可用。最新 DESIGN.md、VISUAL-DESIGN.md、doc/SERVER_CLIENT_API.md、doc/README.md、STATUS.md 反映新规则；历史任务/旧验收报告保留并注明旧规则已被本次替代，不把旧测试记录当作新验收。

## 分工与交付

Codex已修改 engine/src/test/kotlin/com/majiang/cheat/bloom/SleeveBloomWallAndRulesTest.kt，当前14项（原9项更新+5项新增，包含第三杠第四岭上槽/宝牌边界）。此文件及全部测试、tools脚本只读，不删改测试以凑通过；如认为测试错误，报告Codex处理。Codex将独立检视。

可修改必要生产代码与上述设计文档、本任务专属交付回调文档。不改用户原有 lwjgl3/build.gradle、SnapshotLauncher.kt、snapshot/。不Git提交或推送，不递归启动Codex，不委派其他agent。

先实现并跑 :engine:test --tests '*SleeveBloom*'，再 test :lwjgl3:classes :android:assembleDebug --rerun-tasks --no-build-cache。全部通过后跑 tools/architecture/sleeve-bloom-smoke.init.gradle 的 sleeveBloomSmoke（1280与-PsmokeWidth=960），核查秘籍文案排版。有缺陷先修复再交付，不得带已知失败标记完成。不要和Codex并行运行Gradle；本任务开始时Codex无Gradle进程，开发期间由你独占运行，结束回调后释放。

写 DELIVERY-ANTI-RINSHAN.md、CALLBACK-ANTI-RINSHAN.json，包含真实测试计数/命令/结果/未验证边界，recipient=codex、taskId=SB-RINSHAN。最终输出 CODEX_CALLBACK SB-RINSHAN ready_for_review 并退出，表示提交Codex独立检视，非最终验收。
