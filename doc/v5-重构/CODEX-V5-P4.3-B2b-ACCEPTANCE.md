# P4.3-B2b 调用方独立验收

日期：2026-10-28（Asia/Shanghai）。任务：`thread_20261027_230256_426c`，沿用原线程及 caller 会话，未另派任务。

结论：**本任务范围 PASS，D-R1-03 关闭**。同批 LocalHost 与持久宿主在途工作不参与本任务否决；本次只收口已验收改动至现有唯一 Draft PR [#20](https://github.com/liuchangready/majiang-game/pull/20)，目标 `v5`，源分支 `feat/v5-post-g1-batch`。

## 实现与审查范围

冻结起点 `c0f16eb77c70296978f658dd5382b9288d556e05`；receiver R11 候选 `421169575bdf340bd49de14079b4a6123639e382`。本包净改动为 Unity 协调器、会话视图、typed 事件映射、确认动画队列及 Presenter、source-linked 模型测试、Unity 测试与交付报告，共 15 个路径。没有修改 Client/Wire/Domain/LocalHost 生产实现、solution、asmdef、统一 verifier、边界策略或 scheduler。

调用方复核确认：snapshot/batch 身份一起捕获，异步 capture 不给旧事件换 binding；接受序号与 accepted authority 阻止旧 capture 回退撤权、新角色及新 round；生命周期栅栏清队列与已显示 banner；角色快照按当前 batch/binding 取源。真实增量只消费 `PlayableEvents`，包括合法的空增量；baseline 不播历史事件，旧 fake 的 `Kind=None` 保留快照差分回退。公开结算不误判为私有，对手暗杠不补造实体，state-only 事件不伪造动画。

receiver 的正式 `B2bFormalProbe34/35` 覆盖 spectator→player 的 capture pending 与 publication 已发布未派发两个阶段，逐条派发时检查真实队列/banner，新授权 private draw 单次播放且重复帧不再入队。调用方另提交三项窄补充，不新增返修轮：

- `04b0ff5`：source-linked 永久测试两例，以真实 Wire 握手取得 Player/Spectator batch；两份缓存故意保留不同 round，分别验证 `SessionAuthority.FromAccepted/FromView` 的当前角色 round、seat 与私有授权。TableModel 由 138 增至 140。
- `9655589`：在正式反向两阶段中显式断言残留 Player `v1/event101`、Spectator `v2/event102`，及 Current/Presenter 的同角色基线坐标。
- `1136887`：修正文档退出码口径。R11 Editor 两测试阶段为 0，但 wrapper 整体因可选 Player 构建 300 秒超时退出 1；不把整体写成绿色。历史 R10 Player 构建与 Player 运行 NOT_RUN 分开保留。

## 本次实际验证

证据根 `R = D:/majiang-wt/p43-b2b-events-ds-20261027/build/codex-b2b-r11-1793154687532/`。

| 检查 | 实际结果 |
|---|---|
| 任务树 fresh Rebuild / 直接执行 DLL | Client 182/182；初始 TableModel 138/138；补充后 fresh Rebuild 140/140，native 0 |
| 集成树 Release solution build | native 0，0 warnings / 0 errors |
| 集成后 fresh DLL 直接运行 | Client 182/182、TableModel 140/140、Architecture 92/92，native 均 0；errors/failed/skipped/not-run 均 0 |
| Unity EditMode | native 0，62/62；failed/skipped/inconclusive 0 |
| Unity PlayMode | native 0，143/143 = 正式 131 + 原调用方独立 12；其中 B2b 正式 35 全部运行，未删除或弱化原探针；failed/skipped/inconclusive 0 |
| 原边界策略负例 | native 0，13/13 正确阻断 |
| 原策略 StrictCompiled | native 0，通过，无白名单变更 |
| 截图 | 原要求 10 张 + typed 动画 1 张，11/11 PNG 签名/尺寸/不同 SHA256 通过；本轮动画图已实际查看，弃牌 banner 可见 |
| receiver R11 证据 | 64/64 长度和 SHA256 匹配，原 manifest 未覆写 |

实际运行入口为 `R/verify.ps1`、`R/verify-integrated.ps1`、`R/sequential/run-unity.ps1` 与 `R/play-retry/run-unity.ps1`。集成树为 `D:/majiang-wt/codex-b2b-closeout-1793155236600`；从当前已发布 PR20 头 `c6941408667caac6148db23d7631d43a49b2504b` 合并 R11，再接入上述调用方补充。集成构建使用 `dotnet build dotnet/Mahjong.sln -c Release --nologo -v minimal -m:1 /nodeReuse:false /p:UseSharedCompilation=false`，三个项目另行 build 后 `dotnet exec ... -result-xml ...`。

Unity 编译前实际执行既有 `build-shared.ps1 -Configuration Release`，native 0，复制 19 个 fresh 文件。49 份生产 C# 与集成树 Git 内容一致；原始文件 SHA 48/49 相同，唯一差异为新 mapper 的 LF/CRLF，换行归一后 49/49 完全一致。原 12 项探针仅在验证时临时注入既有测试类，原文与编译输入已保存在证据根；结束后逐字恢复正式测试。EditMode 生成的三个场景先归档，再于最终 PlayMode 进入测试前恢复提交版本；未提交字体、Resources、插件、URP、slnx 或场景生成差异。最终两树 tracked clean，冻结至任务头及集成增量 diff-check 均为 0。

首次 Unity 编译受内存压力影响，达到 600 秒上限且无测试 XML；调用方主动停止另一个尚未执行测试的并行实例。后续 EditMode 62/62 通过。紧接的 PlayMode 启动遇到 Unity 清理其已结束 ILPP runner 的内部 `Win32Exception`，未进入用例，也未生成 XML；日志保留，停止该已核实归属的进程后，单独重启取得上述 143/143。没有把这些未完成运行记为 PASS，没有修改源码、断言、编译器/边界策略绕过它们。原生成功退出及测试计数只取最终 XML 与 exit 文件。

主要证据 SHA256：

| 路径（相对 R） | SHA256 |
|---|---|
| `sequential/EditMode.xml` | `61DD72DAFC91656E269125EBD377BDDF2EFFD873AFCCE2D5B9E93E60738EEE15` |
| `play-retry/PlayMode.xml` | `AA13DB309967586345A8D8AB820839656EF9E04F92584CD391D81F0712EAFB23` |
| `Mahjong.Client.Tests-integrated.xml` | `C70F772D710EF2CFB466CDFFD5223397A1702310B124B66040D0C03D71D26CED` |
| `Mahjong.Unity.TableModel.Tests-integrated.xml` | `328C8050B06194D30E58563620EB5819F3682ED6BFC6DA323F8E9A33F954F424` |
| `Mahjong.Architecture.Tests-integrated.xml` | `DFBF500816193EAF9E2EF772B564F3C2DB51F4C0DFDE0768003CC30C9599994B` |
| `evidence.csv`（82 文件） | `183C6EFB03B82936FBE5418D83A75CBB56A006832CF80EC335C1A03D08E58C7D` |

## 收口边界

只接受 B2b 已确认事件消费、身份/生命周期门控与动画去重。测试采用真实 TableSession/WireCodec、协调器、主线程派发和 renderer，传输端仍为受控测试通道，不宣称生产整场 E2E。最新远端 CI 由本次普通 push 另行触发，本地验收不继承历史 CI 结果。

其余本批已接受任务在账本均已有 MR 记录；未把在途 P4.1a、持久宿主及手动暂停的 P4.1c/d 混入本次增量。复用 PR20，不创建组件 MR，不合并至 `v5`、部署或生成 complete.flag。V4 仍生产，G1 已批准，G2–G5 未批准，181 classified 与 Player 运行、游戏 Android/iOS/真机/跨网等原有缺口继续保留；本轮不重复 Windows Player 构建。
