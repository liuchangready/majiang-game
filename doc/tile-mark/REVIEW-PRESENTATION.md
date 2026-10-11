# 落汗表现层独立检视

## 最终结论：通过

2026-10-11 23:57，P1FIX2回调完成。Codex 强制执行全量测试及双端构建，86个任务实际执行，1000项零失败/错误/跳过；独立执行真实产品GL和1280×720、960×540可视GL，并实际查看最终牌面、混合副露、清除及菜单截图。生产源码验证前后无变化，测试/夹具指纹核对一致。全部阻塞问题已修正，详见[最终验收](ACCEPTANCE.md)。下文保留各阶段的历史状态和失败证据。

## 阶段记录：P1FIX2 显示收尾

23:43 收到 P1FIX1 的文件/标准输出回调：11项、presentation204项、全量1000项及两个GL自测通过。最终源码和23:38截图确认单调时钟/pending作废/副露汗滴/翻页入口已修正，对家识别区已移至x500，混合副露不重叠。仍有两处显示未达设计：对手仍是分行牌名而非小牌面，秘籍第三行越出弹窗。已下发 TASK-ANTI-P1FIX2.md，范围仅这两处；最后由Codex强制独立重跑全量、双端构建和缩放GL。P1FIX1续接终态JSON仍携带首轮429错误字段，但该次进程退出码0、23:40回调与测试报告已写入；状态不以此混合元数据替代实际验证。

23:28 已到额度提示恢复窗口，使用 `--conversation 2050a6a0-a76b-4db8-85b9-ee681e12030b --dangerously-skip-permissions` 续接原任务，要求完成全部修正与测试后再回调。

23:19 更新：agy 在修正任务中返回 RESOURCE_EXHAUSTED/429，提示9分3秒后恢复（按错误结束时间预计23:27:37）。此次调用退出码1，没有修正完成回调。保留会话2050a6a0-a76b-4db8-85b9-ee681e12030b，额度恢复后使用 `--conversation` 继续；当前未提交代码。Codex 不以首轮自测通过替代独立验收。

23:22 独立补充：新 pending 解除断言已复现失败（选择模型5项中4通过1失败，日志 build/tile-mark/verification-P1-pending-regression.log）。可视GL六场景在1280×720及960×540均运行成功、实际点击三组可达，但人工截图仍确认入口未绘、识别文字越界、副露缺少汗滴。缩放命令附加 `-PsmokeWidth=960 -PsmokeHeight=540 -PsmokeVariant=-scaled`；截图分别保存 screenshots 与 screenshots-scaled。此为缺陷证据，不是修复后验收。

23:15 已收到 P1 的文件与标准输出回调。检查 XML：presentation 204 项零失败/错误；落汗11项和产品真实GL三组/费30/牌河索引0通过。但是源码/截图发现单调时钟未实现、对手12张越界、翻页入口未绘、副露无标记及文档错误，故未验收。已通过带 `--dangerously-skip-permissions` 的 agy 下发 TASK-ANTI-P1FIX1.md，会话 2050a6a0-a76b-4db8-85b9-ee681e12030b。详见 REVIEW-P1-WORKING.md。Codex 随后补充 pending 解除不复活旧选择断言（用例数不变），并将独立可视GL扩为六场景、包含真实翻页和公开副露。

以下为 P1 开始时的记录与当前测试指纹；“尚未运行”为当时状态，后续验收以上方与最终复跑记录为准。

2026-10-11 22:33。Engine 与接口/API 生产链路已验收，详见 REVIEW-ENGINE.md、REVIEW-API.md。P1 现在通过 agy 下发，包含 `--dangerously-skip-permissions`，会话 831eee8c-1f11-436f-93de-49d2ba16b85d。Anti 负责 presentation 生产代码与文档纠偏，Codex 独立维护测试和检视。尚未通过 P1，不宣称完整玩法交付。

## Codex 已激活测试

选择模型 5 项（实体 ID、四张限制、排序保持、身份/候选变化、截止时间、缓存不延时、权限门控及恢复后不复活旧选择），位置/期限展示 2 项（四席位、换行边界、从一开始的描述、对手授权牌和独立期限），真实屏幕输入/异步拒绝/决策失效/无新快照时重连门控/字体 4 项，共 11 项。FakeGameSession 新增提交录制与延迟 Future。GL 用例走实际菜单与权威服务，连续三次施术、双击不重复、费用30/12实体/不刷新deadline、标记牌入牌河索引0、返回菜单释放资源。另有 TileMarkVisibilitySmoke 六场景渲染夹具，检查对手12张、四家牌河第7张、公开副露及到期清除，需人工检视截图。

这些用例在 P1 实现前只是已激活的验收契约，尚未编译通过或运行成功。

| 文件 | SHA-256 |
|---|---|
| TileMarkSelectionModelContractTest.kt | 0C46D805736CFB534A47E251ABC0B1F3F6F3953F89CDB2B73203379B7FA6B0B0 |
| TileMarkPresentationContractTest.kt | C51208E2E1B9A77A66641B7BA8E9EAEA4442E1E4EF0525594A01D492C99E3513 |
| TileMarkScreenContractTest.kt | CFA26AC215E205ABA86B74B07915A48A4803F4E654FA6B926C0CD29437EAD291 |
| FakeGameSession.kt | 74A800B79AC3FB06FB39527B064B013FA0E1FCDDFE2B2568B2FBC830BA3F4DA1 |
| TileMarkRenderSmoke.java | FBB5028836779761147E8016628587C4C7FB4E9C2F26AD88517E28290A2AC8B6 |
| tile-mark-smoke.init.gradle | E4B52A13C677CEC4E568A482904583EFA07CE916DCCB3F04E145D5E6BEDBD966 |
| TileMarkVisibilitySmoke.java | 00AF66B8028EE620A3F6A0A3DA0ABDC86DE9753EF72E521CF75FB7EBF05FD4AC |
| tile-mark-visibility-smoke.init.gradle | 8A831730A2836562E7E19F98B99E831131E6D3393CE46D1666B96D53918E832F |

A2/A3 交付报告中的虚构字段、helper、线程机制与 0 费用描述已退回要求按源码更正；DESIGN.md、已验收生产与独立测试才是开发依据。
