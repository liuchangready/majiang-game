# G2-C04-D05 隐私通道交付记录

日期：2026-11-02。执行者：GPT-6-Luna。独立复核者：当前调用方（父代理）。本记录供独立验收，不表示正式 G2/C04 批准。

## 交付身份与证据封存

- 工作树：`E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1`；分支：`wt/v5-g2-c04-d05-luna6-20261102-c04d05a1`。
- 接受基线：`957ad475e90a99e83fd7c413c0cbe6e30c00620f`；实现起点任务卡提交：`e871905ae2e2dd816d1172a622ac824f18dd4c6f`；最终代码 HEAD：`47ecf6a6a888f3e167a348393d8aa200b1230282`。实现提交依次为 `cd1428ff0afdbccd97f42dffe55c3bc7ffda2dd2`、`ecbc516499a8cac46f2eab811ab9a83cbbcaea14`、`5a4a2db0e092b6ee131320ccf4891620d4541872`、`47ecf6a6a888f3e167a348393d8aa200b1230282`。
- 从任务卡提交到代码 HEAD 共改 24 个 tracked 文件（2324 insertions、81 deletions），未编辑 engine/domain、wire/client、TableActor 或 asmdef。额外授权文件为 `unity/Mahjong.Unity/Assets/Tests/PlayMode/SpectatorChannelPlayModeTests.cs`：仅将 `_bannerRoot`、`_bannerText` UI fixture 注入由 `UnityEditor.SerializedObject` 改为限定字段反射；原 PlayMode 测试与断言保留，解决 Standalone Player 测试编译错误。
- 源码身份清单：对 HEAD `47ecf6a…` 下 2,496 个 tracked 文件逐文件 SHA-256，清单 `build/7b45f498504e47c199407b14d50bf006/tracked-source.sha256`，清单 SHA-256 `32AC4A988CEE5642F9737A740BAF9F8B380D8E627C7913A219BA05F662112F32`。
- Windows Player 包清单：205 个文件，`build/7b45f498504e47c199407b14d50bf006/player-package.sha256`，清单 SHA-256 `74D6BF6AAC6F543461AC78487A325F2681F801DF72C8FF99B8DF0B288B040BCE`。`Mahjong.exe` 为 128,346,555 字节，SHA-256 `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807`。
- Player build marker/log SHA-256 `4359E653B746EB0890A877B9B06D2B71624FD540E39C873C05B00F25E896DE6A`；Player Unity editor log SHA-256 `3E6EA6115D102057B72D05F905CE766F54F58D2513B7C7146435A8E2F04B0C67`；插件来源 manifest SHA-256 `D7F8AB3638B211F82B8FBA25338ACF3E9253FF51D299008E8BC14012E44D7A8A`。
- .NET 全量 xUnit v3 XML：154/154，failed/errors/skipped/not-run 均为 0，实际运行 30.146s；`build/22df1f8f5f064d1bb77852adf2a5230f/results/localhost.xml` SHA-256 `DD1B86F129FB437E8CA47BCBE7659077EDB13262B5BDFB0224484673A22BFE46`。按 `-parallelMode none` 串行执行真实测试 DLL；非仅构建或 Total 0。先前默认并行 run 的原 XML/log 保留于 `build/2c3c82de0457457c994ea3d3e8b67e43/`、`build/d124600e2da24c6ca1c01104ec20f499/`；其既有 Riichi fixture 精确 stateVersion 断言曾失败，未改动授权外文件，串行全量复跑绿。
- Unity PlayMode：过滤到真实 `C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions`，1/1 Passed，Unity native exit 0。XML `build/083ec8dbe22342c6b79dd2caee267692/playmode-results.xml` SHA-256 `E78C76C32BF08E67379F92CAD2DAA11117CAD0F6EBE944767A311AD343BFF6FA`；raw 真实生产帧 11 个，`privacy-evidence/raw-frames.json` SHA-256 `DF3E2A2C8E3D58D617322250459D60A547A42C8B34FC5315BA59CF6AAAFA4B6F`；Unity log SHA-256 `4AB103CAA5324E427CA6289619B9A834EBAA1B66C83E928F1EF4B9C9FFB286F7`；来源 manifest SHA-256 `0ABFC636202C164FEDDFB75D84F9D10912A06D75499FA3C0CC5B70E554997EF4`。
- Windows Test Player：同一过滤场景 1/1 Passed，Unity native exit 0。XML `build/55d45940b9d448f9a6df1760b3718a26/windows-player-results.xml` SHA-256 `568282021F65E3DC59E34E28CE2692793D3A3C5B6628C542E4D4B0BAF5ADCD1D`；raw 真实生产帧 11 个，`privacy-evidence/raw-frames.json` SHA-256 `C74C4BC040571FA84588BB369538ECE997800D95D809C5BA2E141160843B5C82`；Unity log SHA-256 `938F2776647CF4216E5E09E8CC23255289C06E47FA6514BAF4C87D1F2BBB5972`；来源 manifest SHA-256 `90333AD993440EF9DF9A477E1DBCF6B8ABE090AB98437A6FFE541E164B8D0ACF`。该 Unity 生成的测试 Player 与独立 `Mahjong.exe` 不是同一个二进制。真实帧 verifier 总体 PASS，自测 1 个正例/5 个负例；修改 capture hash 的负例 native exit 2 并被拒绝，摘要位于 `build/55d45940b9d448f9a6df1760b3718a26/verifier-tests/summary.json`（SHA-256 `448B0F7B04697B78EE2217FB4EFFB6D0B54A05DE2BFA5C2C8439489CBAC28197`）。
- 固定工具：Unity `6000.3.27f1`（`6000.3.27.6761814`），`D:/Unity/dotnet/dotnet.exe` 文件版本 `8.0.3226.50405`，SDK 输出 8.0.426。共享资源锁 `Local\MahjongV5P45UnityValidation`；各 run 用 E 盘独立目录、正常 runner 收尾，PlayMode/Windows Player 清理锁与临时 `S:` 映射。

## 实际命令及阶段结果

- .NET：在本工作树 fresh 构建正常 `bin/Release/net8.0` 和恢复用 CrashWorker/fixtures，再用 xUnit v3 直接运行 `Mahjong.LocalHost.Tests.dll`、串行模式 `-parallelMode none`，写 XML 至 `build/22df1f8f5f064d1bb77852adf2a5230f/results/localhost.xml`；最终全量 native exit 0，154/154。
- Unity PlayMode 由 `tools/unity/run-p45-c04-privacy-matrix.ps1` 调度；核心调用为 Unity `-batchmode -projectPath S:/staging-project -runTests -testPlatform PlayMode -testFilter Mahjong.Unity.Tests.PlayMode.C04PrivacyRoleSwitchPlayModeTests.RealRoom_FourPlayersSpectatorAndRoleSwitchUseIndependentSessions -testResults <E独占run>/playmode-results.xml -c04PrivacyEvidence <E独占run>/privacy-evidence`，native exit 0。
- Windows Test Player 使用同 runner 的 `-testPlatform StandaloneWindows64` 和同一过滤场景；native exit 0，Test Player 进入 Lobby 并产出 raw frames。
- 独立正式 Player build 使用 Unity `-batchmode -quit -projectPath S:/staging-project -executeMethod C04PrivacyBuild.Build`，输出绑定 `sourceHead=buildCommit=47ecf6…`；BuildReport `Succeeded`，errors=0、warnings=193、场景 6 个，runner 记录 PID 28740 在 235s 后 `unity-player-build exit=0`，并记录 `RUNNER_FINALLY_COMPLETE`。
- 早期 Player build run `build/ff67a4a0d69c42a48bce575f600e53cd` 的 PID 20744 只有到 151s 心跳，之后无 exit/stage summary/Player 产物；按 UNKNOWN 保留，不以之后成功 run 覆盖。外部旧 Unity PID 12824 启动时间 2026-11-02 18:18:32、路径为 Unity 6000.3.27f1，未操作。

## 固定 N/U/P 行状态

| 行 | 状态 | 已证证据与边界 |
|---|---|---|
| N1 | PASS | 真实 LocalHost 四玩家同房、owner 私手非空正对照；一条合法 discard 经真实通道推进，席位快照到同一 stateVersion。默认 seat0 + AI 保持。 |
| N2 | PASS | 观战只见公开 typed stream；真实 spectator command 得到 `SPECTATOR_READ_ONLY`，不产生业务副作用。 |
| N3 | PARTIAL | 每名 owner 非空手牌及公开字段正对照、audience/version 可见性判定通过；未能以既有合法公共命令产生真实袖牌私有状态及费用/ledger 非空正对照，故袖牌和费用子项 BLOCKED。 |
| N4 | PASS | 捕获 seat0 生产序列化 `SyncResponse` 原始字节，经 seat1 新绑定实际 receive 注入旧 session/seat/binding 帧；真实 `TableSession` 拒绝并保持私有快照不变。既有 ticket 过期/重放/取消负例通过。 |
| N5 | PARTIAL | 四玩家 + 两 spectator 的基线/游标独立；关闭 spectator/player 后其他连接仍 Ready；新 spectator 可重新 attach。容量上限实现存在，但没有真实填满有界慢 consumer 队列并断言 overflow 的测试，慢消费者子项未通过验收。 |
| N6 | PARTIAL | 既有真实连接控制 deadline、stale decision、stale epoch 负例通过。D05 完整 room 初始/恢复 controller、release 后 AI 推进且无人类 binding、未连真人不重开 decision 等全矩阵证据不齐。 |
| N7 | PASS | 实际 `LocalTableRoom` close/reopen；原 account/scope/request/canonical bytes 仅做 receipt query，command frame 发送计数为零；原 Applied receipt/digest 返回，state/event/state fingerprint、ledger 和 136 tile hashes 前后稳定。 |
| N8 | PASS | 独立 room 互不影响；同目录第二 owner 遇 lease conflict，释放后可接管。 |
| U1 | PASS | 生产 `AppComposition`、room/channel factory、四玩家 + spectator 的真实 PlayMode 场景通过。 |
| U2 | PARTIAL | production role-switch 创建新 session/coordinator/baseline、清除旧私有 cache 且现有 owner/public typed frames verifier 通过；未有真实 sleeve/fee 正对照，也未在 Unity 上投递延迟旧 wire 原帧。 |
| U3 | PARTIAL | Unity 承载真实 room 与控制/恢复相关现有场景，但没有完整 D05 room-controller/deadline 矩阵的 PlayMode 证据。 |
| P1 | PARTIAL | StandaloneWindows64 正式 build 成功，Windows Test Player 真实场景 1/1；仅定向 role-switch，不代表 Windows Player 全场景矩阵。 |
| P2 | BLOCKED | Windows Player 没有恢复/原请求 query reconciliation 场景；.NET N7 不能替代 Player 证据。 |

## 固定 C04-G5 行状态

| 行 | 状态 | 判定 |
|---|---|---|
| C04-G5-01 | BLOCKED | hands/private 与 public 非空正对照通过，但 sleeve 私有内容正对照缺失；该 row 不能整体 PASS。 |
| C04-G5-02 | BLOCKED | 没有从合法公共命令得到真实非空私有 fee/ledger 对照；不能以空字段、相等 scalar 或 verifier 自测替代。 |
| C04-G5-03 | BLOCKED | role-switch/cache clearing 与 .NET 原始旧 wire rejection 有证据；Unity/Windows Player 的延迟旧真实 wire 投递负例未执行。 |

## D05 边界映射和剩余事项

- D05-01/02/08：N1、N2、N5、N8 覆盖 room、seat/role ticket、一次性消费与 attach/detach。D05-03/09：N2 覆盖 spectator 公共视图与只读拒绝。D05-04/12：N5 覆盖 viewer 关闭不销毁 room、viewer 独立游标和人数限制；队列饱和测试缺失。
- D05-05/06/13：席位控制计划、claim/release epoch 和 deadline 的部分实现与既有控制测试存在；N6/U3 全矩阵仍 PARTIAL。D05-07：N4/U2 覆盖 binding/session fencing。D05-14：N7 通过，恢复原 scope 的实现级证据通过，但 P2 未覆盖。D05-15 的 `SpectatorSession`/跨平台行为仍按已接受边界，不扩展为本轮实现范围。
- G5-01/02 未完成的最小依赖是能够由既有合法公共命令产生袖牌内容与费用的真实游戏状态；不修改或代做 D04 三绝技人工范围。G5-03/P2 的最小缺项是将旧 wire 延迟投递及请求恢复场景加入真实 Unity/Windows Player producer 路径。

## 首次失败与重试记录

1. 旧 Player Test 编译首次因 `SpectatorChannelPlayModeTests.cs` 无条件使用 `UnityEditor.SerializedObject` 失败；按父代理明确批准仅改两个 UI fixture 字段注入方式，保留 case/所有断言后重跑，Windows Player 测试通过。
2. MTP 并行全量一次触发既有 Riichi fixture exact+1 stateVersion 断言失败；另一次 N7 测试 comparator 对 digest 字母大小写敏感，实际 canonical digest 正确。修正本轮测试比较后，串行完整 xUnit 154/154 通过；未改任务范围外的 Riichi 测试文件。原 XML/log 保留。
3. `ff67…` build 未知退出状态按 UNKNOWN 留档；不清理、不覆盖。最终新鲜 `7b45…` run 正常完成，才形成正式 Player 产物。

## 交接判定

实现、可重复的 .NET/Unity/Player 实际验证、raw verifier 和清单已封存。未覆盖项如上显式 BLOCKED/PARTIAL；没有把准备阶段、自测或摘要值宣称为完整 C04-G5 通过，也不宣称正式 G2/C04 已批准。唯一剩余动作是父代理独立 review/验收。代码提交与本 docs-only 报告提交分开；不推送、不创建 PR/MR、不合并、不部署、不改 scheduler。