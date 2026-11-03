# G2 CI 控制回执对账/Ready 时序修复交付

日期：2026-11-03  
任务：`G2-CI-CONTROL-RECONCILE-01`（唯一 MAJOR：`D-1-01`）  
工作树：`E:/majiang-review/g2-ci-control-reconcile-ds-20261103-1415`  
冻结候选：`61ec1353fa7b548457b34187d5b89330d365db36`。这是 PR20 候选；`origin/v5` 仍是 `8fd7c6cb`，本文不将其称为已验收基线。

## 缺陷与修复

**ID / 级别：** `G2-CI-CONTROL-RECONCILE-01` / MAJOR，原始 Fresh CI 新信息；不是重开已接受的 D05 控制组件。

**位置：** `dotnet/src/Mahjong.Client/TableSession.cs` 的 `DrainEarlyBufferAsync`；真实回归场景为 `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs` 的 `DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect`。

**复现与原实际：** 原始 CI 的 `Mahjong.LocalHost.Tests` 为 178/177/1/0/0/0，失败发生在对账后的合法 Claim：`SubmitAsync` 因 session 非 Ready 而拒绝提交。保留的第一次 fresh Release 证据在 `build/reviewer-1793687399841/target.log`：binding 2 的 baseline floor/confirmed cursor 为 `2/1`，early buffer 留有同 binding 的增量帧 `from=1/1, cursor=1/2`，其 stream version 已旧而 projection sequence 数值更大。排空器将它判成 gap，发起 `EARLY_BUFFER_GAP`，导致对账 baseline 后又进入 Synchronizing。原始目标 XML、SHA 和源码/DLL 身份也保存在该目录。

**根因：** 排空器对非 full 增量帧只用 projection sequence 判断是否过期。重连建立新 baseline 后，旧 stream version 的帧即使 projection counter 数值更大，也不能继续新 baseline 的流；旧帧因此走入 gap 恢复。Ready gate、cursor floor 与产品幂等逻辑本身不需要放宽。

**修复映射：** `TableSession.cs` 在 drain 阶段把 `bufferedCursor.StreamVersion < confirmed.StreamVersion` 与原 projection stale 条件合并为过期帧移除。该判断只用于缓冲帧排空；真正的当前流 gap 仍走既有 full baseline 恢复，baseline 的 cursor/state/event floor、binding 过滤及 Ready gate 未改。

**真实通道时序测试：** `LocalControlTests.cs` 用 `LocalTableHost` 通道包装器，在新 binding 的 SYNC_REQUEST 后、对应 SYNC_RESPONSE 返回前插入旧流增量帧。测试以“新 binding 的 SYNC_REQUEST → 注入帧 → 对账 ReceiptQuery → 后续 CONTROL_APPLIED baseline → Ready”为显式同步链，检查注入帧的 StreamVersion 低于 baseline 且 ProjectionSeq 高于 baseline，确认没有因该帧产生 `EARLY_BUFFER_GAP`、缓冲清空、最终 Ready、Pending 清空，并继续用原 epoch 提交 Claim。原 release 的 request、kind、scope 与 digest 对账断言保留；release 只记录一次，claim 后 controlVersion/controlEpoch 均为 2。

## 统一负例矩阵

| 场景 | 断言与证据 |
|---|---|
| 旧流版本、较大 projection、同一新 binding（原回归） | 本次真实通道确定性注入；被丢弃，不误触发 gap recovery；目标用例固定 5 次通过。 |
| 当前 binding 的有效连续更新/合法后续控制操作 | 对账后 Claim 使用 epoch 1 成功，结果使 controlVersion/controlEpoch 达到 2；原 digest/kind/scope/request 与 outbox 清空断言保留。 |
| 真正 cursor gap | 已有 `Mahjong.Client.Tests.TableSessionTests.ReorderedPushesAreAppliedInCursorOrderAfterGap` 验证重排后按 cursor 有序恢复；本次完整 Client 工程 fresh 运行通过。 |
| 旧 binding 消息 | 已有 `TableSessionTests.OldBindingMessagesDoNotOverwriteTheNewBaseline` 与 `IndependentRound3RegressionTests.BufferedFullFromAnotherBindingIsNeverStoredOrApplied`。 |
| full baseline cursor/state/event floor 回退或旧 baseline | 已有 `IndependentRound3RegressionTests.BufferedFullWithRegressedVersionsIsRejected`、`BufferedOldAndDuplicateFullsAreRejectedWithoutLosingTheBaseline` 与 `OldFullAfterAnAppliedNewerFullCannotRollBack`。 |

上述 Client 负例源未修改；它们由本次完整 `Mahjong.Client.Tests` fresh DLL 182/182 的实际运行覆盖。未跳过、删断言、放松 floor 或以旧 DLL/旧 XML 充绿。

## 验证结果

SDK：`D:/Unity/dotnet/dotnet.exe`，.NET SDK 8.0.426；原生 direct runner。最终目标、类与客户端 DLL 均来自本树 Release 输出，路径与 XML assembly 路径一致。

| 检查 | 实际命令/次数 | native / XML | 结果 |
|---|---|---|---|
| 必要 LocalHost Release build | `& 'D:/Unity/dotnet/dotnet.exe' build dotnet/tests/Mahjong.LocalHost.Tests/Mahjong.LocalHost.Tests.csproj -c Release --nologo` | 0；0 warning、0 error | PASS |
| 目标单案（最终输入） | `& 'D:/Unity/dotnet/dotnet.exe' '<fresh Mahjong.LocalHost.Tests.dll>' -noLogo -reporter default -method Mahjong.LocalHost.Tests.LocalControlTests.DroppedControlReceiptIsReconciledFromPersistentOutboxOnReconnect -result-xml '<独占根>/stable-ready-target-N.xml'`，固定 5 次 | 每次 1/1，failed/error/skip/not-run=0，native 0 | PASS 5/5 |
| 完整 LocalControl 类（最终输入） | 同一 fresh DLL，`-class Mahjong.LocalHost.Tests.LocalControlTests -result-xml '<独占根>/stable-ready-local-control-class.xml'` | 23/23，failed/error/skip/not-run=0，native 0 | PASS |
| 完整 LocalHost 工程 | 同一 fresh DLL，不带 `-method/-class`，`-result-xml '<独占根>/full-localhost.xml'` | 178/177/1/0/0/0，native 1 | **FAIL**：单独新出现的 C04 slow-channel test 在 `C04PrivacyStreamMatrixTests.cs:361` 断言 Ready 时看到 Synchronizing。原 full run 保留在 `full-localhost.log/XML`。 |
| C04 失败项单独定位 | 同一 fresh DLL，`-method Mahjong.LocalHost.Tests.C04PrivacyStreamMatrixTests.Slow_real_channel_queue_saturates_without_stopping_room_or_other_bindings` | 1/1，failed/error/skip/not-run=0，native 0 | 单案 PASS；不能抵销完整 LocalHost 失败，也未盲目重跑完整工程。 |
| Client Release build 与完整 Client 工程 | `dotnet build dotnet/tests/Mahjong.Client.Tests/Mahjong.Client.Tests.csproj -c Release --nologo`；之后 fresh Client DLL direct runner | build 0 warning/error；182/182，failed/error/skip/not-run=0，native 0 | PASS |

原始与中间失败均保留，包括原始 `InvalidOperationException` 首败、完整 LocalControl 类首次运行中未注入（后来以 binding-bound SYNC_REQUEST 同步修正）、bounded barrier 首次编译错误、Ready 过早断言失败，以及 600 秒未输出的类运行与后续逐用例定位运行；见独占证据根清单。所有自有 runner 已退出。

## 范围门禁请求与结论

完整 LocalHost 的新失败来自未授权文件 `dotnet/tests/Mahjong.LocalHost.Tests/C04PrivacyStreamMatrixTests.cs:357–361`。该用例收到 `ReceiptResolved` 后立即断言 Ready；`TableSession.HandleReceiptAsync` 先触发 resolution 回调，再调用 `RequestFullBaselineAsync("CONTROL_APPLIED")`（`TableSession.cs:1459–1467`），所以观察者可合法看到 Synchronizing。当前完整 suite 的唯一失败与其单案 1/1 成功均按原件保留。

若要求本轮完整 LocalHost 门禁 0 fail，最小范围请求仅为允许修改 `C04PrivacyStreamMatrixTests.cs` 该测试：在发送控制命令前注册 full-baseline 同步点；receipt resolution 后等待对应的 CONTROL_APPLIED baseline 完成，再断言稳定 Ready。不得改生产逻辑、控制断言或其它测试。此请求获准前不扩围，也不重复跑完整 suite 凑绿。

因此：`G2-CI-CONTROL-RECONCILE-01` 的目标回归、LocalControl 类及完整 Client 工程已修复并通过；但指定的完整 LocalHost 工程当前 FAIL，且最小修复文件不在 expectedFiles。**本交付不标记为任务整体 PASS/approve，也不宣称 G2 通过。** 由 caller 决定是否批准上述精确 scope 调整；此树未 commit/push/建 MR/合并/部署。

未运行 PG、Runtime、Unity、Player、设备测试；Runtime/PG 输入未变，不机械重跑。历史 D05 控制组件接受结论与历史测试结果继续有效，不在本轮重审。

## 源码、DLL 与证据

最终源码 SHA-256：

- `dotnet/src/Mahjong.Client/TableSession.cs`：`0CE8E399064A1AE16486568849D038A698C8F6859DFBEA9BFE08198A6DCFAB9E`
- `dotnet/tests/Mahjong.LocalHost.Tests/LocalControlTests.cs`：`E07369D78C865167FD864DF9C6D21C204248A74F8973865CA04469533A0D1394`

最终 Release DLL SHA-256：

- `Mahjong.Client.dll`：`2C49C8350059ED598E9179305D5E9342145757DD524E7E9A92455532BB6FFB43`
- `Mahjong.LocalHost.Tests.dll`：`F6CEDE274626F35BCA76363D0641B435EF06753F5FA633E97092E30B47F85546`
- `Mahjong.Client.Tests.dll`：`EBA23DDD266DA23EF064DBB2A2A252EB4F3F5EE897F13D793514163A66EDDD94`

独占忽略证据根：`E:/majiang-review/g2-ci-control-reconcile-ds-20261103-1415/build/g2-ci-control-reconcile-2ab4be132ff74fbe8283abe21b94311b`。其中 `manifest.json` 列出原始 CI 日志/XML/summary/GH metadata、历史首次失败、fresh 首败、所有本轮 build/runner/XML/失败及 DLL/source SHA。原始敏感数据保持在其 ignored 原件位置，未复制到 tracked 文档。

最终 `git diff --check` 通过；tracked 修改仅为本任务三个 expectedFiles 中的 TableSession、LocalControlTests 和本文档。无 skip、空测试、断言弱化、全局并发关闭、随机 sleep、生产异常吞噬或副作用重发。

## 第 2 轮独立复核结论（调用方，2026-11-03）

调用方在同一工作树按最终源码实际 fresh Release 构建 LocalHost/Client，均 native 0、0 warnings/errors。源码与 DLL SHA 和上文最终身份一致。独立 direct runner：固定目标 5/5、LocalControl 23/23、完整 Client 182/182、完整 LocalHost 178/178，native 全为 0；逐项核对原生 XML 的 assembly 路径、case 数量和结果，均无 failed/errors/skipped/not-run。

独立证据根：E:/majiang-review/g2-ci-control-reconcile-ds-20261103-1415/build/reviewer-r2-1793688967241；TASK-CARD.txt、identity.json、target-1..5.log/xml、control-class.log/xml、full-client.log/xml、full-localhost.log/xml、xml-review.json。开发方首次完整 LocalHost 177/178 的 C04 失败和单案成功保留在前述原件中；本次另一次全量通过不抹除该失败，也不据此声称 C04 receipt 回调与 baseline 完成的潜在测试同步缺口已修复。该文件和生产 receipt 时序均未修改，无需为本任务扩大 scope。

复核确认生产 diff 仅增加缓冲增量帧的旧 StreamVersion 丢弃条件，保留现有 Ready/binding/cursor/state/event floor 与幂等边界；测试使用真实 LocalTableHost 通道及显式注入/ReceiptQuery 同步，保留原 request/scope/kind/digest/claim/epoch/outbox 断言，无 skip 或随机 sleep。临时生产诊断代码已移除。三个 expectedFiles 范围符合，git diff --check 通过。

按本任务自身范围验收，G2-CI-CONTROL-RECONCILE-01 / D-1-01 关闭；组件修复接受。前文开发方未自行标记整体 PASS 是其当时全量失败的真实记录，本节是调用方独立实测与验收结论。不宣称 G2–G5、远端 CI、PG/Runtime/Unity/Player/设备或最新统一发布头已通过。后续仅更新既有唯一 Draft PR20，集成头身份与批次门禁另行记录。
