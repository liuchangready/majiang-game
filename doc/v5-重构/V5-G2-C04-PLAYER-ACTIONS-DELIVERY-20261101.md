# V5 G2-C04-D03 Player 动作确认返修交付（2026-11-01）

## 候选身份与边界

- acceptedBase：`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`
- 冻结实现前候选：`734b3775fb7e658fcac33d3d6ce07cf195e96001`
- 本轮复核前冻结候选：`2740ff6ecf91539afc125d4c8cce4a66fa9d9de2`
- 修复代码提交与最终报告 SHA-256：待最终验证及提交后补录。
- 受测范围仅是本候选的 C04-D03。V4 仍为唯一生产架构；G2–G5 未批准。本报告不改 census，不宣称正式 C04/G2 通过。D01/C05 增量未读取、未合入。P4.4、UI15、C01、C03 和 2a3a359 仅作已接受历史前置，本轮不重开。

固定分母为 C04-G2-01..04：Pon、Chi、Riichi、Closed Kan。Kan 明确使用暗杠；不宣称覆盖加杠或 chankan。

## 完整缺陷清单与修复映射

本轮共 4 组（2 BLOCKER / 2 MAJOR），均由调用方首次独立审查发现；没有其他已知问题移交下一轮。下列命令与哈希为调用方返修前复现，非本轮修复后验证。

### D03-R1-01 — BLOCKER：预置 round 与 LocalHost 初始 checkpoint/context 版本错位

- 位置：`LocalSnapshotFacts.cs::CreateInitialCheckpoint`、`LocalPlayerActionScenario.BuildCallWindow`。
- 复现：调用方 `build/caller-review/run-tests.ps1`；直接 LocalHost XML：138 total / 136 passed / 2 failed（Pon、Chi）/ 0 skipped，native exit 1。
- 实际 vs 期望：round 经 13 个已接受合法领域命令后，初始投影 context 仍为 stateVersion 0，真实订阅报 `Context state version 0 predates committed round version 13`；预期是 opt-in 初始 checkpoint/context 与预置 round 对齐，仍保持真实空事件游标并严格校验。
- 返修前证据：调用方 `build/caller-review/localhost.xml` SHA-256 `B1092C8A08AD880CB7444A1EC24866CC573E27FF625D89736E38E049FF55BD43`；调用方 REVIEW SHA-256 `232EC30F03792F35B8CEE758D98675875AD723D5E5D075AF616091D3E8E33893`。
- 修复映射：仅在被显式注入的 round 初态建立 checkpoint 时用 `round.Version` 同时设置 checkpoint 与投影 context 的 StateVersion，EventSeq/high-water 仍为 checkpoint 的 0；不伪造 event/history/receipt，不改变普通 round0 默认值。调用方明确授权新增此文件。
- 验收断言：四场景真实 subscribe 与目标 action 成功；空事件游标和后续真实 event high-water 连续；完整 LocalHost 普通启动/恢复旧例无回归。

### D03-R1-02 — BLOCKER：Unity 新增 Player/Factory/PlayMode 源码不符合当前合同，不能编译

- 位置：`C04PlayerActionProbe.cs`、`LocalTableChannelFactory.cs`、`C04PlayerActionScenarioTests.cs`。
- 复现：调用方 `compile-app.ps1` 使用已接受 Unity App RSP/reference closure、当前 App 源码和 fresh LocalHost/Contracts/Client 输出执行 Roslyn 编译。
- 实际 vs 期望：原诊断包括缺 `Task`、旧 `Cursor.Value`、缺 UI.Session namespace、nullable `ControlEpoch`、事件 tile 读取路径错误、`yield return void`；Factory 暴露 Domain 返回类型导致 CS0012；PlayMode 缺 LocalHost import。期望是 Unity 源码使用当前编译合同，且不增加 Domain asmdef 引用。
- 返修前证据：`app-csc.stdout.log` SHA-256 `1381FBC1C8CD6DA9DEB01F666224971A0B1C3824F5F0D98AFBA96D20DEC6C66E`；import-only 后续诊断 SHA-256 `4766B96C5F0F72B5446AC3589C5FE1327D49901E975F91AE21F1232E8AE6801E`；调用方 REVIEW 中保留逐条编译错误。
- 修复映射：补 Task/UI.Session imports；使用 `StreamCursor.StreamVersion/ProjectionSeq`；明确处理可空 epoch；从 typed `Payload.TileIds` 读取事件；协程正常结束不 yield void；PlayMode 显式引用 LocalHost；Factory 改用 `void ValidateScenario` 门面，不暴露 Domain 返回值。
- 验收断言：隔离 fresh shared plugin 后实际 Unity PlayMode 编译通过；精确四个唯一测试名均 Passed、零 skip；随后 fresh Windows Player build 成功。

### D03-R1-03 — MAJOR：确认摘要可自洽造绿，且没有验证动作实体效果

- 位置：Player probe 成功分支、runner `Test-C04CaseSummary`、PlayMode/.NET 动作断言。
- 复现：调用方 `probe-runner.ps1`；原 positive 的 command version 9/baseline 11 仍通过；将 command version 改成 1、epoch 改成 999、游标改成 99→0 且 decisionId 置空、payload 改为任意 ASCII 并重算 SHA 仍通过。
- 实际 vs 期望：runner 将 ExpectedPayloadSha256 从同一 summary 传回自身，Probe 在收到事件后立即 PASS；无精确 Pon/Chi tile ownership、Riichi 1000 点及供托、Kan 实体 73 补牌和真实后态证据。期望为 canonical SubmitAction 解码、Applied receipt、同身份同命令版本增量事件和同一接受快照/实体变化相互约束。
- 返修前证据：`runner-probes.json` SHA-256 `A63E04C9493750434C2D4CB1171049783DAD17F1EE86061783AF1DEA2ABC2E11`；调用方 REVIEW SHA-256 同 D03-R1-01。
- 修复映射：Probe 用现有 `CanonicalPayloadParser` 检查命令意图；runner 独立按 MJC1 canonical 结构解析实际 base64 payload，校验 action/round/decision/control epoch/固定 tiles；验证 Applied command/state version 大于 baseline 且相等、event sequence/high-water/cursor 前进并匹配、room/session/table/scope/seat/round/role/binding/connection/owner identity 一致。Probe 只在 same-publication 快照与 observer 确认批次相等时报告 PASS。PlayMode/.NET 对 Pon `[0,1,2]`、Chi `[0,4,8]` meld/hand 转移、Riichi 25000→24000 + 供托、Closed Kan `[0,1,2,3]` 与 replacement draw 73 作值断言。
- 统一负例矩阵：Applied 缺事件、错误 round/seat/request/scope、错误 binding/connection/owner/control epoch、decision 空或错误、命令版本不高于 baseline、事件命令版本不匹配、游标回退/receipt high-water 不符、任意文本重算 SHA、错误动作/实体、缺失动作后态一律拒绝。工具夹具标识为 TOOL_FIXTURE，不冒充 GAME。
- 验收断言：每个真实 case 的规范 payload、候选、receipt、增量事件及动作后状态逐值相符；Kan replacement 为实体 73；失败样例均在同一轮 fixture 中被拒绝。

### D03-R1-04 — MAJOR：runner 来源隔离、进程超时/drain 与阶段门禁不完整

- 位置：runner `gitStatus/source-copy/Invoke-OwnedNative/build/XML/closure` 门禁及 `C04PlayerActions.Tests.ps1`。
- 复现：调用方 `probe-runner.ps1`：clean 状态 `.Trim()` 空值报错；robocopy `/XD .git` 复制 `.git` 文件指针；Timeout 1 秒自有 4 秒进程约 4.91 秒才返回 124，stdout drain 无界；旧 PlayMode 只核 4 个 Passed，不核精确唯一 case 集合；旧工具 fixture 没有实际文件，不能证明保留 foreign sentinel。
- 实际 vs 期望：prebuild closure 未覆盖 fresh plugin/Unity 生成输入；build 仅看 native 0 与 exe；工具负例没有真实非空封存证据。期望是来源无 `.git` 指针、fresh provenance 与 postbuild inputs 可复核、timeout 与输出等待有界、build marker 和精确 PlayMode case matrix fail-closed、封存与 foreign 文件所有权可验证。
- 返修前证据：同 `runner-probes.json` SHA-256 `A63E04C9493750434C2D4CB1171049783DAD17F1EE86061783AF1DEA2ABC2E11`。
- 修复映射：空 git 输出按数组安全处理并检查 native exit；以 `git archive` + `tar` 构建固定 commit staging，不复制 `.git`；记录 prebuild source、postbuild Assets、fresh shared plugin/provenance、ScriptAssemblies 与 Player package hashes；自有 PID/start time、scoped tree kill 和 3 秒有界 drain 均进入阶段记录；build marker 校验 Windows/Succeeded/errors/Boot scene；PlayMode 核对四个固定唯一名字、零 skip；工具夹具创建非空文件与 foreign sentinel，另启动自有 timeout/descendant fixture 并检查限时 kill/drain；既有 RunRoot 一律拒绝覆盖。
- 统一负例矩阵：clean/dirty status、伪 `.git` 来源、错误 marker/scene、PlayMode 缺失/重复/skip、native 非零/timeout/drain、重复或非 GUID root、重复证据、foreign sentinel、canonical/身份/效果错误均应拒绝且不变更封存输入。
- 验收断言：runner 顺序执行 fresh .NET → targeted PlayMode → Windows Player 四 case；阶段失败立即 fail closed，所有输入/输出只在本次 GUID root，封存 summary 后再 hash manifest。

## 修复验证与证据

本节在修复提交冻结后填写实际命令、原生退出、测试计数、原始 XML、runner logs、artifact/source/package manifest SHA-256 和每个 case 的 PASS/FAIL/BLOCKED/NOT_RUN。未经本轮实际执行的阶段不得沿用 P4.4/UI15/C01/C03 历史结论。Unity/Player/shared plugin 阶段必须持 `Local\MahjongV5P45UnityValidation` mutex；若无法取得，明确记录等待与 NOT_RUN。

| 固定 case | .NET | PlayMode | Windows Player | 备注 |
|---|---|---|---|---|
| C04-G2-01 Pon | 待运行 | 待运行 | 待运行 | seat0 实际命令与 PonDeclared |
| C04-G2-02 Chi | 待运行 | 待运行 | 待运行 | seat0 实际命令与 ChiDeclared |
| C04-G2-03 Riichi | 待运行 | 待运行 | 待运行 | 扣点与供托 |
| C04-G2-04 Closed Kan | 待运行 | 待运行 | 待运行 | 暗杠、实体 73 补牌；不覆盖 chankan |

完整 LocalHost fresh test/TRX、工具负例、PlayMode XML、build marker、四份 Player summary/log、package/provenance/source closure 及最终 manifest 均须位于独占 `build/<GUID>`。Unity 6000.3.27f1 与当前 Windows Player 执行之外的平台/版本保持未覆盖；Android 属 C05，iOS/远端 transport 不在本任务范围。

## 文件范围

返修仅在调用方授权的 13 个代码/runner/test 路径和原交付报告内；无 verifier 新文件。提交暂存区逐路径核对，生成物不入库。D01/C05 合并后的最终候选仍须重绑并复验；本任务不 push、不修改 PR/MR、不 approve、不 merge、不 deploy、不改 scheduler/complete.flag。
