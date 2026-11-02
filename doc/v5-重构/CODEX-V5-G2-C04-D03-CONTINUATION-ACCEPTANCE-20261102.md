# G2-C04-D03 接续验收与统一候选集成

日期：2026-11-02（Asia/Shanghai）。用户明确授权同步 D03 接续验收、集成统一候选，并将 D05 交给 Luna6、由当前调用方验收。

## 结论与范围

D03 接续实现与冻结 Windows Player 四动作专项接受；本次已将通过的提交合入最新 PR20 头的独立本地统一候选。D05 可在此基线上串行修改已释放的 D03 交集文件，但仍须保留 D03 功能与回归。

本结论不批准正式 C04/G2–G5，不批准三绝技人工保留范围，不代表最终组合候选已完成所有平台验证。V4 仍是生产架构。本次未推送、修改 PR、合并 v5、部署或修改 scheduler。

## 接续关系与冻结身份

| 项目 | 精确身份 |
|---|---|
| 原桥接任务 | `thread_20261101_152052_d8c3`；原第 6 轮 `no rollout found` / failed 保留为历史 |
| 新执行与验收 | 当前会话明确授权的 Luna6 接续子任务；当前调用方独立代码/原件复核及必要重新执行 |
| D03 受测代码 | `ba9c83a319389bb872eaced0f2e39d66c5995f30` |
| D03 docs-only 交付头 | `093ea72aea2950382064f6429c08d10258da36d1` |
| 集成前 PR20 头 | `2832795ab1cbc85a08fe35398dd218e0843a69f4`；本次 fresh fetch `refs/pull/20/head` 所得 |
| 统一代码候选 | `2bbb4e9c3be42ea65a02af9625fb826700280cd2`；上述两个分支无冲突 merge，18 条 D03 路径 |
| 独立集成工作树 | `E:/majiang-review/g2-unified-d03-20261102-c04d03a1` |
| 本地统一分支 | `caller/v5-d03-unified-20261102-c04d03a1` |

本报告与流程模板作为后续文档提交，不改变该已测代码候选。新 D05 任务绑定最终文档提交的完整 HEAD，并记录代码候选关系；不把原桥接第 6 轮改成正常恢复或假批准。

## D03 冻结专项与调用方复核

接收方封存根：`E:/majiang-review/g2-c04-player-actions-luna6-20261101-1520/build/1a5d6a8a9bb94341ad5626f2f1d81a0c`。

- 同一冻结源码/Windows Player 包：Pon、Chi、Riichi、ClosedKan 四个真实 Player 和 raw protobuf/authority verifier 均 native exit 0；PlayMode 原始 XML 4/4；LocalHost 138/138；TableRuntime 483/483，均零失败/跳过。
- Riichi 的 canonical discard 124 与空事件牌集合分别校验；目标事件 seq 1 与 receipt high-water 3 分开绑定。精确持久化 Commit 的请求/scope/版本/事件/实体态与同 publication 后态绑定；latest checkpoint 单独验证。
- ClosedKan 0..3、副露类型、replacement 73、dead wall 14→13 均核验；136 张实体完整且唯一。
- 调用方已逐项核对 manifest 3,286 条长度/SHA，0 错误，独立复跑四份 GAME 原始记录验证器通过，foreign request 与 wrong event 两项负例均拒绝；检查关键代码改动、docs-only 范围、diff-check 与 tracked clean。调用方未独立重跑原四项 Unity/Player，不把原件复核称为重新执行。
- caller 审计：`build/caller-c04-final-038dfa340d3a4fbc8fecbd9947ab302b/audit.json`（位于上述 D03 工作树）；SHA-256 `C9B89B1116C1145B225815284617668000E204ED96C57F463722447315C3AFFF`。
- 原成功 manifest SHA-256 `982AF4E38CA47331E058A26F55F64891830CD00C263E83B78DDEDDEFDF38FBC0`；summary SHA-256 `2E1B93E30A65D2980D2755304A7C9E7D744BCEB8D925F668A167EA2D090C16CF`。原始 FAIL 与中断恢复封存不改写。

本专项 R1-02/03/04 的相关实现与实际证据闭环已接受；跨候选、跨平台的未测边界继续独立记录。

## 统一候选新执行

实际命令：`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File build/caller-d03-integration-r2/run.ps1`。

该脚本从代码候选 `2bbb4e9c...` fresh Release 构建，使用审计过的本地 NuGet seed 与 E 盘隔离缓存，直接运行实际 xUnit DLL，再构建并运行四份动作原件验证器。支持 worker/fixture 均来自本次 fresh 正常输出路径。

| 检查 | 调用方本次实际结果 |
|---|---|
| LocalHost Release build | native 0，0 warning/error |
| LocalHost 全套（包含 D01 与 D03） | 147/147，0 failed/skipped/errors，native 0 |
| TableRuntime 全套 | 483/483，0 failed/skipped/errors，native 0 |
| fresh C04 raw verifier | build native 0；四个新生成真实通道 TOOL_FIXTURE 均 native 0 |
| Unity/Player | 本次未重跑；历史 D03 Player 证据只绑定原冻结版本 |

证据根：本集成工作树 `build/caller-d03-integration-r2/`。summary SHA-256 `7E4EF18AC422FD16BF61DF7675AD7F47558EBE4643EFF1DD301F7C0CF216DEA7`；LocalHost XML `14A63F91FDFB321D1243F11CFE5207F09C87F242D20188F03895B4ABF71B51B3`；TableRuntime XML `3D6A536BA3E3CEA47A52BD51D63F07A945F557F7A187527F93115FDE1110676F`。

首次调用方构建使用仅 artifacts-path 输出，13 项恢复/辅助 fixture 用例因找不到既有测试约定的正常路径 DLL 失败（147 total / 134 pass / 13 fail）。核定均为构建输入闭包路径后改用本隔离树正常 Release 路径 fresh 构建，原生完整套件才全绿；未改产品或测试断言。首失败 `build/caller-d03-integration/` 保留。其更早 seed JSON 数组处理错误同样保留为 caller harness 失败，不计产品缺陷。

## D05 交接与流程同步

D05 实现沿已接受的 [`通道边界合同`](V5-G2-C04-D05-CHANNEL-BOUNDARY-20261102.md)（其 §12 关闭前置报告缺陷），固定 N1–N8、U1–U3、P1–P2 与 C04-G5-01..03 分母。D03 的 `LocalSnapshotFacts.cs`、`LocalTableChannelFactory.cs` 现在可在本基线上串行改动。

先验证纯 .NET 单 room/多 connection、票据、四席/观战、控制/deadline、lease、恢复及幂等，再接 Unity 生产装配和证据工具，最后冻结同候选同包执行。默认 10 分钟资源等待、30 秒心跳、正常取消和稳定封存，详见 [`通用模板`](../LONG_VALIDATION_TASK_TEMPLATE.md)。

本次同步 `AGENTS.md` 的模板入口、通用模板以及主仓现有复核协议的单条“同一个缺陷连续 20 轮未关闭”规则；主仓其他未提交改动保留，不混入集成提交。该规则不解除已有人工裁决或授权自动扩大范围。
