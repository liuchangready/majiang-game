# V5 巡检与 CI 审计验收（2026-10-28 04:18 Asia/Shanghai）

本轮接受 `46232baa2b09fc71c8e1f4c59efcd01b52ff1012` 的 **CI 证据报告**，不接受或发布 Unity B2b 在途实现。G1 APPROVED；G2–G5 未批准；V4 仍为生产基线。

## 基线、范围与发布

- 本轮 fetch 后 `origin/v5=8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`；PR19 的合并义务已履行，不重开。
- 唯一现有后续批次 PR 为 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，目标 `v5`；检查时源分支 `feat/v5-post-g1-batch=91365086c4ae75978a96d85e1a35ff8ab3d70770`。
- 新隔离工作树 `D:/majiang-wt/codex-progress-20261028-0445` / `wt/codex-progress-20261028-0445` 从本轮 `origin/v5` 创建，先 fast-forward 到已接受的 PR20 头，再 fast-forward 到仅新增一份报告的 `46232ba`。
- 为独立测试曾将 B2b `27f1122ba09f29368f1c7d0be30a6959f1e041ab` 无提交合入本隔离树；复制测试工程并完成 .NET 检查后已 `git merge --abort`。正式发布树不含该候选，也不含任何 receiver 未提交内容。
- 最终增量仅 CI 审计报告及本巡检记录；`git diff --exit-code 9136508 HEAD -- dotnet tools unity .github` 为 0。主 checkout 的 `CLAUDE.md`、UI prompts、未跟踪文档和所有用户资源未改。

## 已接受 CI 审计

线程 `thread_20261028_034458_e06d` 原路由 DeepSeek / `deepseek/deepseek-v4.1-flash`；报告 [V5-PR20-9136508-CI-EVIDENCE.md](V5-PR20-9136508-CI-EVIDENCE.md) 已由 caller 独立核对后 approve，发布及 MR 登记仍由 caller 完成。

| 检查 | 本轮结论 | 独立依据 |
|---|---|---|
| [push 40564851868](https://github.com/liuchangready/majiang-game/actions/runs/40564851868) | PASS | 实际 checkout `9136508`；11 个 raw runner meta/log 独立重算 2,286 total、0 errors/failed/skipped/not-run，native exits 全 0；live API completed/success。 |
| [PR 40564866626](https://github.com/liuchangready/majiang-game/actions/runs/40564866626) | PASS | 实际 checkout `0247000d29276b75d9d21a4d888acd918f378905`；同样 2,286/0/0/0/0。API source SHA 与实际合成 checkout 分开记录。 |
| 合成对象 | PASS | 两个 parents 精确为 `8fd7c6c`、`9136508`；合成及源树均为 `244de125550919e83fe921aeeaa50fd44fff9dce`。 |
| 完整性 | PASS | 473 份文件逐项重算大小/SHA256；原 manifest SHA `F41508993BB0575DE4BDAB91506E57FB8ADA30E3C4A9ECEE82FC34ACBD6BA0C4`，0 mismatch。 |
| 构建/闭包/伴随样本 | PASS | 原始记录为 0 warnings/errors、12 DLL（19 files）及四伴随样本；historical git pins 仍 NOT_RUN。 |
| 真实 PG 16.10 | PASS，历史组件实跑、本轮读证据 | 两 run 的原始 35/35 XML、writer 强杀 exit -1、PG 自动恢复日志、cold reader、空库 restore；state1/event2、cheatVersion1、outbox 内容匹配、恢复 ownerEpoch2→3 及 cleanup。13 targeted 为重复子集，不追加总数。 |
| Android | 本次 CI NOT_RUN | 原已接受 55/55 与 33,978,023-byte Wire AOT APK 是历史组件证据；本轮未重建 APK、未跑设备，未将其算进 CI 总数。 |

11 个 direct runners 只有 meta 与原始 console log，没有逐 runner XML；PG XML 则真实存在。报告准确区分这一限制。原 `b5c1c17` push FAIL 和 `39970580589` 缺失 Client framework log 的 UNRESOLVED 保留；新绿灯不关闭它们。

caller 本轮命令：`powershell.exe -NoProfile -File build/caller-review/audit.ps1`（最终修订运行 exit0）、`gh api repos/liuchangready/majiang-game/actions/runs/<id>`、精确对象 `git show -s --format='%H %T %P'`。初次清单校验受 PowerShell 长路径限制失败，原日志保留；改为长路径前缀后全部 473 项通过，未变更源证据或 pin。

## B2b 独立检查与并发返修归属

旧候选为 `27f1122b`。对其临时合入当前接受树的内容，caller 使用 `D:/Unity/dotnet/dotnet.exe build -c Release --artifacts-path build/caller-review/dotnet-fresh`，再 `dotnet exec` 本次 fresh DLL：

| 项目 | build / runner exit | total / passed / errors / failed / skipped / not-run |
|---|---|---|
| Client | 0 / 0 | 182 / 182 / 0 / 0 / 0 / 0 |
| Unity TableModel | 0 / 0 | 138 / 138 / 0 / 0 / 0 / 0 |

`run-dotnet.ps1`、每项目 `build.log/run.log/results.xml` 均在证据根 E。两构建成功后才运行 DLL。另逐项核对 receiver 原 256 份交付文件的大小和 SHA：原 Unity EditMode62/62、PlayMode113/113 的 XML 完整，但这些是其历史交付证据，不等同本轮新 Unity 运行通过。

caller 在 E/project 复制工程，仅向隔离测试加入普通解绑及乱序 capture 探针，fresh `build-shared.ps1 -PluginOutputDir E/project/Assets/Plugins/Mahjong` 成功（19 files）。本轮 Unity EditMode 尚处初始化时发现原 receiver 工作树有新的未提交修复，直接账本核对确认 **原 B2b 线程已由另一调用者进入第4轮返修**。其完整已有反馈已动态复现暂停/失焦期间接受、恢复后才完成 capture，以及普通解绑的三项旧动作复活；并要求补齐真实授权 baseline 与两阶段身份矩阵。

因此本轮不重复请求返修、不覆盖其源码、不接受 R3，也不将其工作树新增内容拼入发布。已验证 wrapper PID47168 与 Editor PID14768 的精确 E/project 归属后，只结束本轮这两个进程，wrapper exit -1；本轮 Unity 结果记 **CANCELLED / 未得出功能结论**，新增三探针未执行完毕，不声称复现或通过。`CANCELLED.txt`、ownership JSON、初始化日志、探针差异和脚本保留。生产复制比较与 receiver 当时的差异来自其已启动 R4 修改，未将该比较失败当产品缺陷。后续只审原线程的正式新交付。

## 任务分配、依赖与保留责任

全局 recent bridge tasks 本轮仅调用一次；随后只在发现 receiver 实际文件变化时读取相关本地账本，确认原线程新 round，未轮询未变进度。

| 路由 | 活跃 / 目标 | 当前线程与状态 |
|---|---:|---|
| DeepSeek Flash | 1 / 2 | `thread_20261027_230256_426c`，B2b R4 reworking；CI审计 `thread_20261028_034458_e06d` 本轮 completed。 |
| GLM Flash | 1 / 1 | `thread_20261027_230302_93fb`，持久 WSS 宿主接线 reworking。 |
| Anti 默认模型 | 1 / 1 | `thread_20261027_154439_11d2`，原子存档/真实业务冷恢复 R4 reworking。 |
| 合计 | **3 / 4** | 无新派单、无重复返修、无路由替代。 |

第二个 DeepSeek 槽位留空：P4.2-B/P4.5 等待 accepted LocalHost 与 B2b；完整远端 P5.4b 后续批次等待 accepted 持久宿主；P6.1/P6.2 等待 G2+G3；P7 等待 G4。P4.1a 与 parent 失败线程及其文件保留责任未消失，仍不接受；P4.1c/d 为用户手动暂停，未恢复或另建替代任务。Anti 现有槽位占用期间不把其失败任务改派给 DeepSeek 填槽。

LocalHost 的最新返修仍要求可规范回放的真实三绝技/账本负载、两次权威冷恢复及强杀后的非初始状态精确比较；PG 存储组件通过不代替 GLM 的持久 WSS 宿主接线验收。

Windows 调度 `Mahjong-V5-0-1-Progress` 已核实 PT30M / IgnoreNew，未更改；complete.flag 不存在。181 classified 仍未覆盖；Android 完整游戏与设备、跨物理网络 NOT_RUN，iOS BLOCKED；本轮不批准 G2/G3/G4/G5，不部署、不合并 v5。

## 本轮证据

E=`D:/majiang-wt/codex-progress-20261028-0445/build/caller-review/`（ignored）。

- `evidence-sha256.csv`：`A349560F2ACDB9EB5FFE0E2BCE0F862163A9AE0CE0A92764711F30D448A96077`。
- `audit-v2.log`：`63FD33670BAA9655DD6F362AD6C5214B308E758B374B0A96D39E71C476D7E0E8`。
- `Mahjong.Client.Tests/results.xml`：`2763D51603DE1F89F44009A17F295A953558BEB5883371A76807946D43549795`。
- `Mahjong.Unity.TableModel.Tests/results.xml`：`013DEB50186BA6322B93208EEBD8FA32F043A0A5C604A5D675197F745847EA67`。

受管发布文件仅两份 Markdown，原始日志/脚本/二进制不入 Git。测试本身不构成未接受 B2b 的验收；其原线程继续保有 merge-back 责任。
