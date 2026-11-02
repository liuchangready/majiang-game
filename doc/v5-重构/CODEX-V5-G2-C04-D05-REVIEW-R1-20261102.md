# G2-C04-D05 首轮独立验收与完整返修清单

日期：2026-11-02，Asia/Shanghai。复核者：当前 Codex 调用方；实现者：GPT-6-Luna。

结论：**NOT_ACCEPTED，1 BLOCKER + 3 MAJOR**。本轮给出全部已知问题，按四个同类组批量返修；不是仅检查第一项后停止。D05 未批准、未集成，正式 C04/G2–G5 未通过，V4 生产边界不变。

## 身份、范围与本轮实际工作

- 被审代码：`47ecf6a6a888f3e167a348393d8aa200b1230282`；docs-only 交付头：`390aa95f430dd6a7de7081f3de477beae502de86`。
- 接收树：`E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1`，原生任务 `/root/g2_c04_d05_luna6`；本轮取证时已交付，无开发轮次并行运行。
- 独立审查树：`E:/majiang-review/caller-d05-review-20261102-r1`，分支 `caller/v5-d05-review-20261102-r1`；证据根以下简写为 `R=该树/build/caller-review`。
- 从任务卡 `e871905ae2e2dd816d1172a622ac824f18dd4c6f` 到交付头共 25 个 tracked 改动，全部属于原 30 个路径加明确补充授权的 `SpectatorChannelPlayModeTests.cs`。取证期间两树 tracked clean；独立 `git diff --check` 通过。
- 父代理 fresh Release 构建并真实执行 LocalHost **154/154**、TableRuntime **483/483**，均 native0、零失败/跳过；命令与起止时间见 `R/execution.json`，不是重复引用接收方执行记录。
- 独立构建实际提交的隐私 verifier，对原 Windows 11 帧记录执行一正四负；运行真实 room 冷恢复探针；提取实际 runner 函数和判断表达式执行短失败探针。
- 独立复算接收方源码清单 2,496 项、Player 包清单 205 项，全部匹配。检查原 PlayMode 1/1、Windows Test Player 1/1 XML；**本轮未重新运行 Unity/Player**，因为廉价阶段已确认阻断。测试 Player 与独立构建的 Mahjong.exe 仍是不同二进制。
- 原交付如实保留的 PARTIAL/BLOCKED、首次失败及 UNKNOWN 记录继续保留；这些记录不因单测全绿转为完成。

## 稳定取证入口

| 文件（均在 R） | SHA-256 |
| --- | --- |
| audit.json | `8BCB44FB92881AC3E9DB24D0DB205A5E65566D41CD40E96CA8EAA354B8353B13` |
| execution.json | `6020414BF44B8D8203851D0C173B0F48A964844E9B9297C9AD6F1DC980EEEA1C` |
| retained-hashes.json | `4198FED2326E89D40D4D6ACBD8234B9BAD446085AF140FCC4DFDBB677E293542` |
| raw-mutations.json | `E1B267A2FBF97658A5C0E25D3C58582BBD382A2312EAAC5DDAFF8F24FCA1E145` |
| room-probes.json | `981EC1AED33DA19A699A4B058FDDCDD633D4F98A8CF9EF5D4632531FE12FB2D4` |
| runner-probes.json | `42FF760521CDAA1460C447EC79383286C151747C6C901E18A725F5DBFFC2A6CD` |

对应脚本、输入、标准输出/错误、XML、探针源码和首次审查夹具编译失败均保留于 R。完整稳定文件清单另见 `R/evidence-sha256.json`；缓存和 bin/obj 不作为稳定证据。

## D05-R1-01：原始隐私验证只核摘要，未解码 wire

严重级别：**BLOCKER**。本轮独立发现，不能用接收方已有五个摘要负例替代。

位置：`dotnet/tools/C04PrivacyMatrixVerifier/Program.cs:64–108`、`CreateFrame:124`，以及 `C04PrivacyMatrixVerifier.csproj`；`C04PrivacyProbe.cs` 的原始帧/身份采集。

复现：在独立审查树执行 `powershell -NoProfile -File build/caller-review/run-raw-mutations.ps1`。脚本使用 fresh 构建的生产 verifier 和接收方 Windows `55d45940b9d448f9a6df1760b3718a26/privacy-evidence/raw-frames.json`。脚本中的变体仅为验证器组件负例，不能标为 GAME。

实际：原始记录 native0；四个应拒绝变体也全部 native0 / `PRIVACY_MATRIX_PASS frames=11`：

1. 所有 raw 替换成非法单字节 `[1]`，同步重算 SHA；
2. 所有 raw 替换成空字节，同步重算 SHA；
3. seat1 的真实 raw 和 SHA 替换 seat0 行，保留 seat0 旁路元数据；
4. 不修改 wire，只将私手计数 13/14 改写为另一个允许值。

原因：仅 Base64/SHA 和 `decodeStatus="Ok"` 等自声明字段被检查；没有使用真实 WireCodec/生成契约从字节解码。自测正例甚至以 `[1]` 作为 raw。Welcome 身份当前也只有旁路事实，缺可重建的原始绑定链。

期望：隐私结论从实际解码的 Welcome/typed 帧、当前 room/session/scope/role/seat/binding/version/owner/audience 及字段内容得到；旁路摘要若不一致必须拒绝。畸形、空帧、错误类型/未知字段、跨身份替换不可通过；非空手牌/袖牌/费用对照必须落到真实内容归属。

证据：`R/raw-mutations.json` 及上表 SHA；每例 `.raw.json`、`.stdout.txt`、`.stderr.txt`；`R/audit.json` 绑定被审版本。

修复要求：复用现有真实 WireCodec/typed generated parser，补采原始 Welcome 与必要身份绑定，重建公私状态后核对摘要和覆盖分母。允许在已授权 verifier csproj 增加现有契约项目引用，不修改 Domain/Client/proto。不得以只检查长度、固定 magic、进一步收紧摘要取值替代解码。

验收断言：合法实际生产序列通过；上述四变体均非零；另覆盖错 Welcome、错 owner/audience/版本/会话/作用域、截断/非法类型/未知字段、公流携带私有值。自测必须用真实合法序列化，不伪造 PASS。

## D05-R1-02：恢复控制者与 room/viewer 生命周期

严重级别：**MAJOR**。主缺陷已真实复现，附带生命周期问题为本轮源码检查发现，需同组批量覆盖。

位置：`LocalTableRoom.cs:178–211`、ticket 注册表；`LocalTableRuntime.cs:134–167` 和 CreateActor；`LocalSnapshotFacts.cs:118`；`AppComposition.cs:238–243, 523–555`；`LocalTableChannelFactory.cs` 的默认共享 room 路径。

复现：真实探针源 `R/probes/Program.cs` 和 `CallerRoomProbes.csproj`，使用既有 P41Harness 和真实 room/channel/TableSession；构建后命令为 `D:/Unity/dotnet/dotnet.exe exec build/caller-review/probes/bin/Release/net8.0/Mahjong.LocalHost.Tests.dll`。探针的内部只读观察使用既有 friend assembly，不反射改权威。

实际与期望：

| 真实探针 | 实际 | 期望 |
| --- | --- | --- |
| all-AI fresh room 关闭，options 改 AllFourHumanSeats 后冷恢复 | `RECOVERY_CONTROL_INVALID ... Seat 0 changes controller without advancing controlEpoch` | 保存的当前 controller/epoch 优先于新初始 SeatPlan，恢复成功 |
| seat0 真人真实 ReleaseControl（Recorded/LOCAL_CONTROL）后关闭、同计划冷恢复，仅观战绑定 | 恢复 seat0 automated=true、epoch=1，但 CanTickAuthority=false | 已 release 的 AI 无需真人绑定即可推进 |
| 普通真人正对照 | Hello 前 false、live 后 true，阶段开始后 detach 仍 true | 保持此已有正确行为与既有 deadline |

根因：CanTickAuthority 仍依据静态 `SeatPlan.HumanSeats` 和进程内 `_releasedSeats`，冷恢复不能恢复这个集合；恢复 actor 的初始 controller 又受新 SeatPlan 影响，与已接受 §4.7 合同冲突。

同组已知源码问题（未冒称运行复现）：

- `CloseRoomViewerAsync` 无条件移出 viewer，即便 ShutdownResult 非 Completed/quarantined；应保留未成功关闭的资源归属，与 primary shutdown 的隔离语义一致。
- 默认 factory 也缓存共享 room，但 primary session 普通 shutdown 只有 APP_QUIT 才 CloseRoom；核对并修复默认单 viewer 拥有整桌的既有合同，防止退出重进后旧 lease/driver 常驻。多 viewer 的显式 room 模式仍只 detach 该 viewer。
- 失效/Detached/Failed ticket 保留在字典直到 room close，反复 attach/detach 可无限累积；在不允许重放的前提下提供有界 retired 状态/清理和重复拒绝证明。

证据：`R/room-probes.json` 及上表 SHA、`room-probes.stdout.txt`、最终探针源码；源码位置可在固定代码 47ecf6a6 复查。

修复要求：依据恢复/当前控制快照、代次、live binding、room phase 和已开放 deadline 决定推进；SeatPlan 仅用于 fresh 初始化及合同允许的发票边界。修复 viewer 隔离/默认 owner 资源释放和 ticket 有界保留；不改 actor/domain/存档 schema，不扩大为自动重连产品功能。

验收断言：上述两个失败转为真实通过；完整 fresh/recovered × human/AI/released/claimed × bound/unbound × preparing/started 矩阵；控制切换旧 decision/epoch 拒绝、不重复开 deadline、不因 viewer 断开暂停已有 deadline。覆盖 viewer 关闭超时/隔离保留、最后一个默认 viewer 释放 lease、其他 viewer/authority 不受单连接关闭影响、ticket 反复退役有界且不可重放。

## D05-R1-03：授权内必需矩阵未完成

严重级别：**MAJOR**，接收方交付已知遗留，本轮集中列出，不算返修引入的新问题。

位置：原实施卡阶段 B–E、边界合同 §6；交付报告“固定 N/U/P 行”“固定 C04-G5 行”与结尾。

复现：逐行对照原交付与固定分母 **N1–N8/U1–U3/P1–P2、C04-G5-01..03**。只有四席/观战/换角色一个 Unity 和 Test Player 场景，各 1/1、11 帧，不能替代其余场景。

实际：N3/N5/N6/U2/U3/P1 部分完成，P2 缺失，三条正式 G5 均 BLOCKED。缺真实非空袖牌/费用 ledger 正对照、慢 consumer 真实饱和、完整 room/controller/deadline 矩阵、Unity/Windows 延迟旧 raw 真正 receive 注入、Windows 恢复后原 scope/request 查询及零重复副作用。结尾“唯一剩余动作父代理 review”与未完成实施矛盾。

期望：授权内可做项全部完成，固定行按实际分母判定。不能把 generic .NET 控制/恢复测试当作新增 room 边界、Unity/Player 或同包证据。

证据：原交付报告 SHA `E52BB3DA25B8BAEEE36771DE4B033CD229F97A83A80BF4A026CECD428DE9942B`；`R/audit.json`；原 source/package 清单在 retained-hashes.json 中绑定。

修复要求：通过已允许的 LocalPrivacyScenario、真实公共合法命令和实际通道建立非空对照；不修改或接管 D04 三绝技人工范围、不反射改权威。若合法命令因外部真实缺陷无法产生对照，须提供最小可复现依赖及精确 BLOCKED 子项，其余独立工作继续；不得只写“无法产生”就宣称实施完成。完成慢队列、真实 raw 延迟投递、恢复对账和相应 .NET/Unity/Windows 路径。先批修廉价阶段全部已知缺陷，再冻结候选执行所需同版本同包矩阵。

验收断言：每个 N/U/P 与 G5 行都有实测 PASS/FAIL/BLOCKED/NOT_RUN 和原件；owner/audience/version 内容对照不靠空值或裸标量巧合；恢复原 request 查询、重放不增加副作用计数，旧角色 raw 不能污染新缓存。旧通过证据保留但不冒充新候选通过；正式结论仍由父代理验收。

## D05-R1-04：runner 来源绑定、成功判断与超时收尾

严重级别：**MAJOR**。本轮独立复现工具组件问题，未对外部 Unity/其他任务进程做取消或终止。

位置：`tools/unity/run-p45-c04-privacy-matrix.ps1:48–62, 107–129`；`tools/tests/C04PrivacyMatrix.Tests.ps1:20`；交付报告包身份。

复现：`powershell -NoProfile -File build/caller-review/run-runner-probes.ps1`。其中直接提取实际 runner AST 的 Invoke-Owned、XML gate 和 finally，使用审查自建 45 秒 sleep 子进程、独占审查 mutex；本轮运行结束已回收该自有辅助进程。复跑应改为新的独占 RunRoot，以免覆盖原证据。

实际：

- 实际 XML gate 接受 `passed=1,failed=0,skipped=7,total=8`，也接受 `errors=3`；没有固定 case 名称/节点数/skip/error 断言。
- build 仅 Test-Path marker 即可成功，不验证 marker 内容、错误数、源/包身份或产物闭包。
- 超时 1 秒 + 正常关闭宽限 15 秒后辅助进程仍活着；实际 finally 运行后仍 alive=true，但 mutexHandleCleared=true，即子进程可能仍持有输出/映射而共享锁已释放。异常路径没有完整失败 summary/稳定封存。
- HEAD/porcelain 只记录不拒绝，复制 live Assets/Packages/ProjectSettings，无最终冻结候选与复制输入闭包一致性门禁；插件来源检查原 repo 不能证明复制后的 Unity 输入一致。实际已保留 manifest 全匹配，不据此指控旧产物造假，但 runner 无法阻止后续错误绑定。
- 工具测试同步读完 stdout 再读 stderr、无有限等待，可能阻塞，需补非零/大输出/超时/取消完整边界。
- 报告将总包 bytes 128,346,555 写成 Mahjong.exe 长度；实际 exe 为 **667,136 bytes**，SHA `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`。应改正文口径而不改写旧 marker/日志。

期望：实际 native/XML/marker/包闭包与冻结输入共同决定成功；所有自有 writer 停止后释放映射/锁并稳定封存。取消/失败不是正常 PASS；无法停止时明确 UNKNOWN 和残留身份，不写正常完成。

证据：`R/runner-probes.json` 及上表 SHA、`R/owned-timeout-probe/`；源码与包身份见 audit/retained-hashes。

修复要求：锁定明确候选和输入闭包、验证复制前后身份；XML 固定名称/分母且零 fail/error/skip/not-run；build marker 内容和文件/包身份严格校验。对自有 PID/start identity 采取有界正常取消、等待/输出 drain、必要的仅自有进程处置，不能强杀他人进程、绕锁或释放资源后继续 writer。成功和失败均收敛完整状态/summary/稳定证据。脚本自测同样有界。

验收断言：短组件矩阵覆盖合法通过、原生非零、dirty/错候选/复制输入变化、缺/伪/失败 marker、空/错名/XML skip/error/count mismatch、大 stdout/stderr、锁等待超时、正常取消、拒绝关闭的自有进程；非零与失败正确上抛，最终无自有 writer，锁与映射归属正确，封存后哈希稳定。通过后才允许新一轮昂贵 Unity/Player。

## 复核检查清单与返修交付

| 必查维度 | 本轮确认 |
| --- | --- |
| 范围、工作树、路径 | 25 路径均授权，diff-check 通过，无另报越界缺陷 |
| 假绿、计数、旧产物 | 实际单测非空无跳过；verifier/runner 存在误 PASS，见 R1-01/04 |
| 规则、终态、136 守恒 | 既有受影响 suite 独立通过；未改领域规则，不代替缺失组合对照 |
| 时序、租约、控制、恢复 | R1-02 已真实失败，R1-03 矩阵缺失，逐项批修 |
| 隐私、票据、公开流 | R1-01 原字节未验证、R1-03 非空对照缺失；不宣称已发现真实跨座位生产泄露 |
| 命令、exit、原件、SHA | 本文与 audit/稳定清单可重建，原首次失败不覆盖 |
| 平台与环境 | 原 Unity/Test Player 只读审计；本轮新 Unity/Android/IL2CPP/设备 NOT_RUN |
| 文档口径 | R1-03 未完成说明和 R1-04 exe/package bytes 需同步修正 |

本轮四组即全部已知缺陷，不把已确认问题留到下一轮。返修须在同一轮内批修所有 BLOCKER/MAJOR，逐组给出修复提交、真实正负例/命令/native exit/计数/XML/日志/SHA 与 clean status；保留有效既有成果、首次失败和 UNKNOWN。按既有 20 轮规则，不提前追加人工等待点，不绕过 D04 既有人工保留，不用 skip/空测试/弱断言/刷新摘要/旧 DLL 得绿。

后续 bridge 任务是原生 D05 的明确接续，不同时唤醒原生执行者；使用用户指定 gpt-6-luna 和独立接续树。不得推送、建/改 PR、合并、部署、改 scheduler 或回写旧 bridge 历史；交付后由父代理独立复核。
