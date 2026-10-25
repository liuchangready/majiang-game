# N3 AI 真实执行与导出状态绑定：第 2 轮独立验收

日期：2026-10-25。Bridge `thread_20261025_104846_3120` 已由调用者成功执行 `approve_task`，状态 COMPLETED；沿用原线程、callerSession 与 receiverSession，没有新建或转派任务。结论仅为本消费者组件 PASS，**完整 N3/G-049/G1 未关闭，G5 未完成，V4 仍为唯一生产架构**。

接受交付 `7da818dc12d19739e2bd0026f90990b2aff098fc`，包含第一轮 `6d168123b3115ae52ced6bf834035176a7c2e600`，共同冻结基线 `900a043cfa276c0e172672b8b7cc7d6d4ebc4767`。交付工作树干净，累计 diff-check exit0；仅原定四个文件，未修改生产源码、fixture、catalog/manifest、统一 verifier、项目或用户环境文件。原三个 Fact 和有效断言保留。

## 返修结论

- **D-R1-01 PASS**：删除任意非空 EligibleSeats 与任意补集。独立阅读真实 V4 `V5AiGoldenExporterTest.kt:99-124`、`testFixtures/.../TestTableBuilder.kt:20-76`、`LegalActionProvider.kt:153-229`，核对先 reserve 手牌，再 reserve wallPrefix `[68,112]`，按 seat0..3 填充、取14张王牌、构造活牌山的顺序。C# 重建136实体，使用真实 `HandDecomposer.CanWin` 与有源码锚点的碰/明杠/吃谓词推导资格 `[0]`，传给真实 `RoundCommand.Discard` 并检查实际窗口。seat2 精确手牌、无响应、seat0三张同种、重复预留失败都有断言/负例。资格不由 expected 后态倒推，也不硬编码为命令输入。
- **D-R1-02 PASS**：动作/观察阶段只接受精确合法枚举符号；`"254"` 不再被 Enum.Parse 放过。全部14份 before/after 的 turnNumber 必须为非负 Int32，小数/超范围/错型/缺失真实失败，保留无 Domain TurnNumber 端口的声明。
- 真实执行链为 Replay → Submit → DeadlineArbiter → 同一已接纳记录动作 → RoundStateMachine.Execute。六拒绝项实际 Submit 拒绝、无 Submission；真实 Domain 对象/版本/事件/指纹/物理状态保持，前后投影分别与导出比较。接受项检查真实前后状态、回执和事件序列，未从 expected 后态设置运行结果。
- C# `[TileDiscarded, ResponseWindowOpened]` 与 V4 receipt 只含 `TileDiscarded` 的表示差异有源锚点，C#顺序/ordinal精确断言，弃牌载荷逐值比较。墙下标以固定发牌偏移53映射；四席、点数、独立账本余额、棒数和最后弃牌均绑定实际投影。

首轮失败记录继续由 Bridge 第2轮返修指令及其原始证据保存；本次只在新目录执行，没有覆盖旧失败或混用旧 DLL。旧的本地未验收集成候选 `4476881`/`be56f7a`/`c227f87` 不纳入发布。

## 独立实测

CLI `D:/Unity/dotnet/dotnet.exe`。交付目录 `D:/majiang-wt/n3-ai-replay-binding`。原始证据根 E：

`C:/Users/sncdc/AppData/Local/Temp/codex-n3-ai-r2-review-1792898635863`

| 实测 | 结果 | native exit |
|---|---|---:|
| solution Release `--no-incremental` build | 0 warning / 0 error | 0 |
| Ai 全量 | 438 passed / 0 failed / 0 skipped | 0 |
| Domain 全量 | 385 passed / 0 failed / 0 skipped | 0 |
| Contracts 全量 | 149 passed / 0 failed / 0 skipped | 0 |
| Client 全量 | 132 passed / 0 failed / 0 skipped | 0 |
| 交付探针矩阵 | 48/48符合预期，5正控制+43负例，unexpected0/toolFailure=false | 0 |
| 原 reviewer extra-byte-enums 复现 | 8/8符合预期；原样6PASS；254动作Failed4；12.5/1e30各Failed1；其余缺失/错型/schema/phase均失败 | 脚本0；负例各1 |

四模块共1104项全部通过，Errors/Not Run均0。每次负例 Total6且有真实框架失败，未以缺fixture/Total0/构建失败冒充预期FAIL。48例中的N01/N02覆盖原caller两个scores反例，N39/N40覆盖新重建输入，N41-N43覆盖严格枚举/巡目。原fixture raw SHA256保持为 `E898CDB6E1034D1DC8229E807A919E7F64E03062D3E9AD64DED36317402375C9`；LF归一化 `4ECE5EBA95851FF2BBCED3A109A16DFE1E308494D7557583EF192BC6BECE52A2`，Git SHA1 `032d79e95e45bde9cb29046cad5f49f338fa78f3`，三者不混用。

可复制命令（E替换为上述绝对路径；Out必须是新目录）：

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
foreach ($name in @('Ai','Domain','Contracts','Client')) {
    & D:/Unity/dotnet/dotnet.exe "dotnet/tests/Mahjong.$name.Tests/bin/Release/net8.0/Mahjong.$name.Tests.dll" -noLogo
    if ($LASTEXITCODE -ne 0) { throw "test failure: $name" }
}
powershell -NoProfile -ExecutionPolicy Bypass -File tools/v5-golden/probe-ai-replay-binding.ps1 -EvidenceRoot '<新TEMP>/probes'
powershell -NoProfile -ExecutionPolicy Bypass -File 'E/extra-byte-enums.ps1' -Repo D:/majiang-wt/n3-ai-replay-binding -Out '<新TEMP>/extra'
```

E中还保存完整 `reproduce.ps1`、原reviewer `extra-byte-enums.ps1` 正文、所有副本/原始日志/逐例CSV及完整 SHA256清单。reproduce.ps1为本轮实测命令的整理版，未额外运行；原 extra-byte-enums 脚本已真实运行。首次证据清单管道把正在输出的自身纳入而产生归档工具exit1；构建/测试/探针原始exit均0。随后先完整快照并排除清单自身重建成功，未重跑测试覆盖事实。

## 隔离集成与唯一 PR19

fetch及ls-remote确认发布头仍900a043后，在干净的 `D:/majiang-wt/codex-n3-r2-integration-20261025` 正常 no-ff 合并接受交付，得到 `2609f9c3446735afe93d30dd819e4d9c7fad425d`。`git diff 7da818d HEAD --exit-code`为0，合并树与交付内容完全一致。

在该精确合并头重新非增量构建 solution，0 warning/error；Ai438+Domain385+Contracts149+Client132共1104项全绿。另显式非增量构建solution外的TableRuntime.Tests，0 warning/error，再执行355项全绿。集成合计 **1459 passed / 0 failed / 0 skipped / 0 Errors / 0 Not Run**，所有命令native0。证据均为E下 `integrated-*`。

```powershell
& D:/Unity/dotnet/dotnet.exe build dotnet/Mahjong.sln -c Release --no-incremental
# 同上运行四个模块runner
& D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental
& D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.TableRuntime.Tests/bin/Release/net8.0/Mahjong.TableRuntime.Tests.dll -noLogo
```

本验收记录只新增文档。统一发布目标为既有 [PR19](https://github.com/liuchangready/majiang-game/pull/19)，`feat/v5-1-integration → v5`，包含两轮尚未发布开发提交及调用者集成/验收记录，只做一次普通快进push，不创建子任务PR、不合并PR、不部署。最近12任务账本核对：本任务为唯一已完成但尚未登记MR的交付；同批出千绑定thread_20261025_104846_b58a仍running、字段审计thread_20261025_051932_5eeb仍reworking，均排除。主checkout与用户未提交内容未修改。

## 证据 SHA256

| E相对路径 | SHA256 |
|---|---|
| solution-build.log | 99D13DE4552FA483D28A09AC319A3BA885FC7BA976FADD10E8B16C8F95652DAA |
| Ai-tests.log | 7ACF56669F3EA723B4EEAFC7CA8EE2EB0C5045DD563A9034CDDD9AB46E160B65 |
| Domain-tests.log | 70B5740DE66C72E2B9E33683EEFD9AA1D919FD64BC14C27A1D60FEB2A59DC413 |
| probes/matrix.csv | 219E0B8E68FDF1BFEC7BBBE162177D85D30F17EA161D2B2E8C1C1DF765E7D8BD |
| independent-extra/summary.json | CE9834998A2658AE9496D19D0B4F646C490F7C35E589907C15803F4ACD6788D5 |
| integrated-build.log | 6E9E55A24506DF749617902DB09B95277215BA8AB33473326977C486D3A492D2 |
| integrated-TableRuntime-tests.log | C9B512B0DAE8141A662F2AF9DAEF9628B743FEF2D36191907F77AA66361692FA |
| evidence-sha256.csv | C1B87A942231997CAC85645C57D2D006F1AA648ECF1734AB10E42B864A3603D9 |

## 审查口径及剩余范围

PASS为本轮两个MAJOR修复、真实单个接受弃牌、六拒绝准入与零副作用、136守恒、字段/事件表示映射、严格解析和相关模块回归。没有新增已知BLOCKER/MAJOR。新增仅消费者测试，未引入生产并发、恢复、网络或私有数据发布路径；既有epoch/deadline拒绝有效。

BLOCKED/未覆盖仍包括：C#无TurnNumber端口；V4未导出window资格map及全桌身份oracle；结构和牌时的yaku/furiten资格端口未在这里组合，触发即显式失败；事件文本/逐弃牌摸切和立直标记无C#字段；cheatPoints通过独立账本且本fixture为全零，未证明TableRuntime组合；无V4 canonical hash；fixture只含一个接受弃牌，未覆盖所有AI动作。详见交付范围报告§5，不能将这些边界计为G-049 full。

NOT_RUN：PG/WSS、Unity/IL2CPP、真机、物理网络、V4 Gradle重型全量及本次发布远端CI；本地通过不代表这些验证通过。V4唯一生产、完整N3/G1未批准、G5未完成。
