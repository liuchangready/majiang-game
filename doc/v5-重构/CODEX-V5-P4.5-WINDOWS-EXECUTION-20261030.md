# P4.5 Windows Player 实测：恢复失败与整场阻断

日期：2026-10-30（Asia/Shanghai）。执行者：Codex 调用方。结论：**NOT PASS；P4.5/G2 不放行。**

本次从当前 PR20 候选重新构建 Windows Player，执行真实进程的开局、正常退出、强杀冷启动和五种存档副本实验。发现一个确定的恢复阻断：**主文件缺失、有效 `.save.prev` 存在时，Player 静默开新局，未恢复原来的已确认状态。** 完整整场、三绝技整场覆盖和正式恢复 Compare 尚未完成，不能用本次局部实测替代。

## 输入、构建和环境

- 冻结候选：`7ade107bda5efe171ac5ba06c3414980d1c93ae9`；已接受 `v5` 基线仍为 `8fd7c6c`，V4 仍生产。
- 工作树：`D:/majiang-wt/p45-windows-validation-20261030`；分支 `wt/v5-p45-windows-validation-20261030`。
- Windows 10 Pro 10.0.19045 x64；i7-1260P；Intel Iris Xe，驱动 31.0.101.3729；Windows PowerShell 5.1。
- Unity `6000.3.27f1`；.NET SDK `8.0.426`；Windows Mono Player，1280×720，后台隐藏运行。
- `build-shared.ps1 -Configuration Release`：native **0**，148 个来源输入、19 个输出；`check-shared-plugins.ps1`：**PASS / 0**。
- fresh Unity `-batchmode -quit -nographics -projectPath ... -buildWindows64Player ... -logFile ...`：**Build Successful / native 0**。`-nographics` 用于 Editor 构建；运行 Player 使用现有图形参数。
- Player：`build/p45-windows-validation/player/Mahjong.exe`。Unity EXE 是通用启动壳，其哈希相同不证明业务代码相同；本次保存了整个 Player 文件清单及业务 DLL 哈希。
- `Mahjong.Unity.App.dll`：`1125A36B0B777FF7E82146A775776C954C75D373B6323F683507DE5F5D611254`。
- `Mahjong.LocalHost.dll`：`D64168C661320223B75DABAA8D97AE51D21D780E1380697B6F6380AD1F302314`。

构建按现有脚本刷新了跟踪中的插件 DLL/PDB；Unity 导入还更新字体、slnx 并移除性能测试资源。这些生成差异已保存为 `generated-unity-assets.patch` 和文件清单，未伪称 raw checkout 始终 clean。`dotnet/src`、Unity 业务脚本和既有工具相对冻结提交的 diff exit **0**。结束后已恢复本工作树中的生成差异，仅提交本报告。

## 实际执行结果

主证据根：`D:/majiang-wt/p45-windows-validation-20261030/build/p45-windows-validation/`（下称 `R`）。正式结果在 `R/final-audit.json`；原始进程记录保存在各 run 的 `attempts/` 和 `log/`。

| 场景 | 实际结果 | 判定边界 |
|---|---|---|
| 新目录：菜单→大厅→真实本地牌桌→一次动作→正常退出 | Player native **0**；存档版本 3，3 条回执；136 张守恒 | 开局和单动作路径通过；不是完整一局/整场 |
| 同一 run 重新打开后强杀 | 首个真实快照版本 3；校验 PID/创建时间/路径/hash/运行目录绑定后强杀 | `FORCE_KILLED`，不能把进程记录中的 native 0 当优雅退出 |
| 强杀后冷启动 | 首快照恢复版本 3；随后落盘版本 5；旧 3 条回执逐身份、字段及载荷摘要均保留；前后 136 张守恒 | Player 后续确认事件检查 **native 31 / tool 10**；恢复整体不通过 |
| 截断主文件＋有效备份 | 首快照版本 3；旧 3 条回执保留，随后版本 5 | 备份装载成立；同样 **native 31**，后续确认事件检查失败 |
| 坏 payload SHA，且无备份 | `LOCAL_HOST_START_FAILED: kind=CorruptState`；native **25** | 正确拒绝，原文件 SHA 不变 |
| 不支持的 envelopeVersion=2，且无备份 | `CorruptState`；native **25** | 正确拒绝，原文件 SHA 不变 |
| rulesetVersion=999、重算有效 envelope checksum，且无备份 | `CorruptState`；native **25** | 正确拒绝，原文件 SHA 不变；这不是旧版本迁移覆盖 |
| 主文件不存在、仅有效备份 | 首快照竟为 **0**；旧玩家回执未恢复，物理布局重新生成；烟测仍 native **0** | **FAIL：静默新局；烟测成功不能证明恢复成功** |

所有游戏实验使用当前构建的真实 Player、真实 LocalHost、真实 Wire/Client 路径及隔离目录；没有测试通道、预设胜负/牌山、改权威状态或伪造候选。存档故障注入只修改从本次真实对局复制的独立副本。`execute-r2.exit.txt=0` 只表示实验脚本完成，绝不表示表中游戏运行或 P4.5 门禁通过。

## BLOCKER：缺主文件时忽略有效备份

第一次 `series-r2/runs/backup-only` 的烟测正常结束，却从版本 0 开局。随后进行了**独立诊断，不是重试取绿**：在新目录中放入相同的有效备份，原主文件不存在；在首个真实 `LOCAL_SNAPSHOT` 处暂停并强杀自有 Player，阻止烟测后续动作干扰诊断。

证据目录：`R/backup-only-confirmation/`。

- 输入 `.prev` 与本次正常退出的有效存档逐字节相同，SHA-256：`8C867AA83D1AC540A8E0266DDD62DC1FE2C8491FCAA1D3DA8061ACFB5C6E7A44`。
- 用冻结源码的实际存档 codec 验证 envelope checksum；输入版本 **3**，已确认回执 **3**。
- Player 日志首快照：`stateVersion=0`。生成主文件通过 checksum，版本 **0**，回执 **0**，物理位置摘要与输入不同。
- 3 条旧回执全部未进入新局；其中包含一条此前真实 Player 提交并 Applied 的 `smoke-*` 玩家动作。此处是“未恢复已确认状态”，不是声称只比较数字即可证明丢牌。
- 原始输入、生成主文件、只读解码 JSON、PID/强杀记录及日志均保留；报告/公共清单不包含 opaque ticket。

代码原因已定位于 `dotnet/src/Mahjong.LocalHost/LocalTableStore.cs:180` 的 `LoadEnvelopeWithRecovery()`：`TryLoad` 对不存在的主文件返回 null 且 error=null；有效备份也 error=null。第 189 行仅检查两个 error 都为 null 就返回 null，尚未采用已经读到的非 null 备份；上层据此初始化新局。后面的备份发布分支未执行。

需要后续修复区分“两文件均不存在”和“主文件缺失但备份有效”，并补齐真实文件/冷进程回归。本次验证未修改生产实现。

## 冷恢复后的确认事件检查失败

`series-r2/runs/normal-and-crash/log/attempt-0003.log` 与 `runs/truncated-with-backup/log/attempt-0001.log` 均出现：

```text
SMOKE_ERROR: No incremental confirmed visible event after the legal action
```

现有 `GameBootstrapper.RunLocalHostSmokeSequence` 返回 native **31**。离线检查真实保存文件，能找到新玩家请求的 **Applied** 回执；冷启动前的 3 条回执也全部保留。因此不能把该失败解释成“权威动作没有提交”。当前烟测轮询 `coordinator.Current.VisibleEventBatch`；瞬时 batch 被后续更新替换是待验证的可能原因，**未证明具体交错，也未证明 UI 丢事件或 UI 正常**。需要接收/消费时序证据定位，不能放宽断言后算通过。

## 整场与其他验收缺口

以下为代码核对和能力缺口，不冒称已动态完成这些流程：

1. **整场推进 BLOCKED**：默认 `LocalTableChannelFactory.CreateCoreAsync` 直接构造 `LocalTableHostOptions` 并调用单局 `LocalTableHost.CreateChannelAsync`，未装配 `LocalMatchCoordinator`。Table UI 会构造 `CommandKind.NextRound`，但 `LocalTableRuntime.HandleCommandAsync` 未提供相应整场推进分支；其通用动作映射缺 actionType 时会拒绝。没有通过修改存档或直接调用整场协调器来替代真实 Unity 路径。
2. **托管/收回 UI BLOCKED**：`TableCommandIntentFactory.CreateClaimControl/CreateReleaseControl` 明确抛出 `CONTROL_PAYLOAD_FIELD_COLLISION`；当前 `RoundIdField` 与 `ExpectedControlEpoch` 都是 canonical field 1。没有绕过门面补造不同负载来宣称 UI 可用。
3. **正式恢复 Compare BLOCKED**：当前 Player 无完整结构化导出器；现有 P4.5 工具对真实来源仍有硬闸门。本次额外的只读存档解码用于核对原始文件的 checksum、状态载荷、回执和 136 张守恒，**未导入为可信 Player 导出，也未修改 Compare 闸门**。账本仅提取最近 checkpoint，不证明全量绝技账本恢复。
4. **完整一局/多局/终局、三绝技、观战隐私、LOCAL 资产入口**：本次未执行；既有单动作烟测不能覆盖。下一步应先处理恢复和默认整场装配问题，再建立真实输入/观测路径执行整场。
5. **视觉证据无效**：后台隐藏模式下，正常 run 的 4 张截图 SHA 相同；实际查看牌桌图为全黑。未用它们证明布局、动画、输入或隐私正确。
6. Android/iOS/IL2CPP/真机/跨物理网络：本次 **NOT_RUN**，不从 Windows 推断。

## 辅助观测与证据审计

`R/observer/Program.cs` 是只读辅助程序，调用当前源码真实 `LocalSaveCodec.DecodeEnvelope/DecodeCheckpoint` 和 `RoundRecoveryCodec`，不打开宿主、不取得 lease、不推进游戏、不回写文件。最终输出在 `R/observations-final/`；5 个保存样本均通过 envelope 和当前 state payload hash 验证，物理实体分配 136、守恒检查通过。回执按 account/scope/request 的身份摘要比较，避免把不同 scope 下复用的 `local-ai-1` 当同一请求。

保留的辅助脚本问题：首次 observer 构建相对引用路径错误；早期摘要误用 digest.ToString，已改为 ToHex；首次进程实验因深度序列化 Process 对象延误而错过强杀窗口，记为 harness failure，未冒称强杀成功；诊断汇总最初未兼容空回执集合，实际 Player 证据不受影响，修正后仅重新汇总既有文件，未重跑 Player。早期 JSON/失败日志均保留，最终取证以 `observations-final/`、诊断输入/生成观测和 `final-audit.json` 为准。

最终 observer 构建 0 warnings / 0 errors。原 P4.5 collection verifier 实际校验 **10 份 collection、147 个 artifact entries**，均通过长度/SHA/manifest sidecar；这只是证据完整性通过。最终无本次启动的 Mahjong Player 残留，Windows 定时任务仍 **Disabled**。

## 可复现入口

以下命令在本报告工作树运行，路径指向本次保留的实验脚本；输出使用新 Series/EvidenceName，不能覆盖旧证据。

```powershell
# 当前冻结源码构建：本次已执行；原始日志/exit 均在 R/logs/。
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/build-shared.ps1 -Configuration Release
# Unity.exe -batchmode -quit -nographics -projectPath <本工作树/unity/Mahjong.Unity>
#   -buildWindows64Player <R/player/Mahjong.exe> -logFile <新的构建日志>

# 辅助源码与执行脚本保存在 ignored R 内，纳入证据 SHA 清单。
D:/Unity/dotnet/dotnet.exe build build/p45-windows-validation/observer/SaveObserver.csproj -c Release
D:/Unity/dotnet/dotnet.exe build build/p45-windows-validation/observer/SaveObserver.csproj -c Release -o build/p45-windows-validation/observer-final
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/p45-windows-validation/execute.ps1 -Series independent-01
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/p45-windows-validation/confirm-backup-only.ps1 -BaselineSave series-independent-01/private-saves/baseline.save -EvidenceName backup-only-independent-01
```

主索引：`R/final-audit.json`、`R/manifest-audit.json`、`R/source-identity.json`、`R/player-files.json`、`R/environment.json`、`R/evidence-sha256.csv`。原始存档/outbox/原始日志只在本地 private 证据目录，不提交、不公开。证据 SHA 清单不含自身与其 sidecar，清单校验结果见同目录的 audit JSON。

本次仅落地验证报告；未修生产代码、未推送/合并 PR、未批准 G2–G5、未恢复定时器、未生成 complete.flag。
