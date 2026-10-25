# G1 当前冻结头证据汇编独立验收

日期：2026-10-26。任务：`thread_20261026_061703_d877`，沿用原 caller/session；第 2 轮交付 `8846fd282513364490594c499898a1ddc39934bc`，冻结源码 `6c3573620f61e8cbb6a13c8c4b81abef0b173fd9`。

本任务的证据汇编验收通过，Bridge 已登记 COMPLETED。此结论只接受报告，不批准门2、G1 或 G5；V4 仍是唯一生产架构。

## 返修销项与集成修正

- D-R1-01：相对冻结头恰一个授权新增文件 `V5-G1-CURRENT-EVIDENCE-20261026.md`，旧路径不存在，交付工作树 clean、`git diff --check` exit 0。
- D-R1-02：§4 恰九门，7 PASS + 门2 BLOCKED + 门5 未闭；原关闭公式保留，N-2 接线属于 G1 判定前置，SF-1 与 N-2 组件残余分列，未重开已接受组件。
- D-R1-03：插件清单为 12 DLL，其中 7 个 Mahjong 生产程序集、Google.Protobuf 和 4 个 System 兼容程序集，零 Tests DLL；V5 HTTPS/WSS 与 V4 MJ-TCP/1 分清；并行任务文件集互不重叠。
- 调用者在最终集成检查中删除 §5 批准后清单误列的门2改账项，并将注释明确为“门2改账与 N-2 接线均为 G1 判定前置”。这是本次集成的最小文档修正，未改变门槛、共享台账或冻结证据。

交付原文件 SHA256：`E95F6BBCEF2EEB76243D5C4BA2F5469A82C452593660D603763F7015D20E0A0C`。集成修正以 Git diff 留痕。

## 当前工作区实际验证

证据根：`D:/majiang-wt/g1-current-evidence/artifacts/TEMP/caller-r2-review-ccb9243d5fa24fdd9ffa4a53160f82aa`。TEMP/TMP 仅在测试进程内指向此 D 盘目录；原第 1 轮证据未覆盖，findings.log/schema.xml SHA 均保持原值。

| 验证 | 实际命令与结果 | 日志 SHA256 |
|---|---|---|
| Contracts fresh build | `D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Contracts.Tests/Mahjong.Contracts.Tests.csproj -c Release --no-incremental --no-restore -v:minimal`；native 0，0 警告、0 错误 | build.log `320E34641B4D122BD34D3B6007D9764E70AE0C0C3C09701ECBD0560E89C838CE` |
| Schema 定向实跑 | 直接执行同项目 `bin/Release/net8.0/Mahjong.Contracts.Tests.dll -class '*SchemaCompatibilityTests' -result-xml <证据根>/schema.xml -noLogo -noColor`；native 0；XML 精确 13 PASS，Errors/Failed/Skipped/NotRun 全 0，含 `ProtocGenerationIsRepeatable` | schema.xml `65046F3AB42843E124AC1CA52B3F98FAF9D60CAFA4FEBB36F0132017D5766EC7` |
| 黄金目录 | `powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1 -RepoRoot D:/majiang-wt/g1-current-evidence`；native 0；6 fixtures / 73 requirements / 10389 cases / 10000 random / 45 Facts；full17/partial21/none32/v5-only3 | catalog.log `416D92D3FD43FCD067DC31F4C5DCEE15B446BA471B49CD85788A72F1375C73FA` |
| 原始证据逐项哈希 | `powershell -NoProfile -ExecutionPolicy Bypass -File artifacts/TEMP/verify-evidence-csv.ps1`；native 0；127 OK、0 BAD | hashes.log `13233BB6517DCC8973C2981ED529EDD22CE25FEEFB62C1B18E0430E77BA71F20` |
| 历史双 CI 只读重检 | `powershell -NoProfile -ExecutionPolicy Bypass -File D:/majiang-wt/codex-g1-progress-20261026/artifacts/review-ci.ps1`；native 0；两 run 各 1767/0/0，共14 meta、全部 framework 日志 SHA、各10 PG stage 与合成头整树等价通过 | ci-review.log `0B9CC2606D4B8D7A0A68261C69123C85B8B0BBEA7C7B1B75527A4E8E529C56AA` |
| 文档断言 | 授权路径/旧路径、九门表、合计、前置公式、插件清单、传输与并行条件、原证据不变、clean/diff-check 全通过 | assertions.log `49EE98DB29084A95BBB5ACA206F12E3E91CF865848BEA30A7083A2C096CA926E` |

CI 来源是远端实跑、本轮读取；本轮没有重跑远端 CI、PG、Unity/IL2CPP、Gradle、设备或物理网络。插件清单和双 CI 全量结果的读取不冒充本地执行。集成仅新增文档，`dotnet/tools/tests/unity` 树与冻结源码和已测试交付一致，故不重复全套构建测试。

## 唯一 MR 收口与保留项

沿用唯一 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，`feat/v5-1-integration -> v5`。调用者在隔离工作树基于已发布 `f2708c56db3c33648257d0c99f4b7bb82d63b742` 合入本任务两个交付提交，再提交上述修正和本验收记录，统一普通快进推送；不改主 checkout 的未提交文件、不创建组件 MR。

同批 AI 文本证据已验收并在原远端头中。note 任务尚待独立验收，本次不纳入其未验收产物，也不据此否决本报告。共享 N3 台账仍为 411 bound / 82 classified / 99 pending；16 proposed classified / 2 proposed pending 的 AI 裁决尚未应用到共享台账，classified 不计 bound/full。

G1 仍需调用者处置门2与 N-2 接线并作最终九门重审；本次不派单、不改台账/校验器、不创建 complete.flag、不合并 PR 到 v5、不声明新 CI 已通过。
