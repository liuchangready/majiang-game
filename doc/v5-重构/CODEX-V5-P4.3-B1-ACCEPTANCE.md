# V5 P4.3-B1 玩家场景独立验收

日期：2026-10-27，Asia/Shanghai。结论：**P4.3-B1 APPROVED**。

桥接线程 `thread_20261027_092727_16bd`，第 6 轮；沿用原 caller/receiver 线程，无新任务或转派。
候选 `a4ba03766d9aab5dbf1937c7b9778eb3ee4a5081`，冻结实现起点
`9d4a0400b44cc2ce598c391b39539ddf69ffe829`。
任务工作树 `D:/majiang-wt/p43b-scenes-anti-20261027`；调用者隔离收口树
`D:/majiang-wt/codex-p43b-closeout-20261027` 从现有 PR20 远端头 `f2896e0` 建立。

## 本范围实现与结论

本包提供可加载的 Table 场景、四席公开/私有展示、普通动作和三绝技候选选择、码牌准备、倒计时、
控制与导航、Client 操作队列绑定及确认快照驱动的动画。输入沿已接受的投影/交互/意图工厂与
`TableSessionOperationQueue` 提交；测试验证 request identity、pending/Unknown、确认回执、
重绘去重、隐私清除、generation/controlEpoch 与动画栅栏。

第 5 轮遗留 **R5-A** 已关闭。到期判断使用 Presenter 自身单调时钟，静默期间逐帧关闭过期弹窗，
重绘同一确认样本不会延长 deadline，打开/提交入口也拒绝过期 offer。仅清理对应绝技选择，
普通决策和其他未过期 offer 保持有效；无乐观实体移动或扣费。

本轮复核未发现新的本任务 BLOCKER/MAJOR。五个已接受纯模型、`dotnet/`、原边界策略与冻结起点
保持零差异。既有回归测试未改或跳过。Unity 自动生成差异保存到 ignored 证据目录，按精确路径
恢复；任务树最终 status 干净，`git diff --check 9d4a040..HEAD` exit 0，无本次 Unity 驻留进程。

## 调用者实际执行

以下命令在任务工作树运行，均为本轮新证据。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/test-p43-scenes.ps1 -OutputDir build/codex-p43b-r6-independent-20261027
```

外层 native exit **0**，耗时 **442.57s**。runner 对 Unity native exit、非零选中和完整 XML 状态作验证。

| 项目 | 实测 |
| --- | --- |
| EditMode | 61/61；本任务 TableSceneEditModeTests 5/5 |
| PlayMode | 96/96；本任务 TablePlayModeTests 47/47 |
| failed / skipped / inconclusive | 两份 XML 均全部为 0 |
| static / StrictCompiled | 候选精确策略 PASS |
| 原负例变异 | 13/13 blocked，未弱化断言 |
| 截图 | 要求的 10/10，PNG 签名、尺寸及互异完整 SHA256 全部通过 |
| Windows Player fresh build | native exit 0，667136 bytes |

截图包含四席、候选、三绝技、码牌、倒计时、重连、结算和超宽视口；9 张 1280×720、1 张
2560×1080。调用者视觉抽检四席、本席/他席可见性、袖里藏花弹窗和 21:9 布局，文字可读、控件可达。
完整逐文件 SHA 保存在该目录 `evidence-sha256.json`；P4.4 额外截图不计入本任务 10 张分母。

恢复提交中的原始 Table 场景后，调用者运行原封不动的 24 条 R4/R5 探针及新增 6 条 R6 矩阵：

```powershell
$env:P44_SCREENSHOT_DIR=[IO.Path]::GetFullPath('build/codex-p43b-r6-extra-20261027/screenshots')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/codex-p43b-r6-extra-20261027/Run-R6Probes.ps1 -OutputName independent
```

Unity native exit **0**、外层 exit **0**；XML **30/30**，failed/skipped/inconclusive 均 0。
R6 覆盖 immediate sleeve、off-turn sleeve、beam × 有/无普通 Decision：真实按钮打开、无推送自然到期、
重复确认样本不续期、显式 0ms 拒绝、过期真实点击零 outbox、有效 offer 真实点击产生一个 canonical
outbox、普通决策及其他未过期 offer 不受影响。临时测试注入在 finally 中恢复原始字节，未提交探针或 DLL。

| 证据 | SHA256 |
| --- | --- |
| EditMode XML | `4499313A0156865C45F40D6F92508DBEB12ADACDDFC400768D0311F2F62F7A04` |
| PlayMode XML | `45B04FDFB4EDE57A60FE042C314BC5147A3A279E25B6D4E1F71F46ECB03A8303` |
| 全 runner manifest | `B091D99D6416BE171C3E6CB0DC55D1D7174CFAC35EFF284E837484A4BF99D659` |
| 原 R5 snippet（未改） | `86ED32DEAC3BF61EF243A9CED76938D845E20C28D689B5F907A7532EEF23F22E` |
| 新增 R6 snippet | `E0DE70377FC29BA25BC2A5AB4A5F0B5ABE63A8A07BB17EB8B53E65BCB0070148` |
| 合并 probe snippet | `30CB833DD3E409DDFC8197C7618FB95D04534DC6CF3FE6DACC16FBED92CF2D2B` |
| 独立 30 条 XML | `C41F95534FAD57117D84A4ECCF332AFC94F6244BF305D97E50C01D6E76268514` |
| Windows Player exe | `F083B0E879A9298CDCF9933A8F3B4F2B013E13F7D3E51221FC0D756B79E28807` |

## 调用者统一集成登记

原冻结策略真实 exit **1**，仅两个测试 asmdef 新增 `Mahjong.Unity.Table` 引用失配。
该已知共享登记由调用者在隔离收口树完成：只给 EditMode.Tests 与 PlayMode.Tests 各增加这一条测试边；
没有改变生产依赖、禁用规则或反射策略。候选策略与提交策略语义相同，原 13 负例保持原强度。

调用者对收口树运行 static 检查，并用同一提交策略对任务树编译产物运行 StrictCompiled，两者均 exit **0**：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-unity-boundaries.ps1 -Project D:/majiang-wt/codex-p43b-closeout-20261027/unity/Mahjong.Unity -Policy D:/majiang-wt/codex-p43b-closeout-20261027/tools/unity/p42-boundary-policy.json -Report build/codex-p43b-r6-independent-20261027/integrated-boundary.json
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/check-unity-boundaries.ps1 -Project unity/Mahjong.Unity -Policy D:/majiang-wt/codex-p43b-closeout-20261027/tools/unity/p42-boundary-policy.json -StrictCompiled -Report build/codex-p43b-r6-independent-20261027/integrated-strict.json
```

收口树 `unity/`、`dotnet/` 与候选提交完全一致，无额外实现合并差异。
同批此前已接受的 P5.2、companion、P4.2-core、P4.3-A、P4.4 已在 PR20，无遗漏的未推送接受组件。
本次只更新既有 [PR20](https://github.com/liuchangready/majiang-game/pull/20)
（`feat/v5-post-g1-batch` → `v5`），不建组件 PR，不合入未接受 P4.1/P5.1。

## 保留的边界

- APPROVED 仅指 P4.3-B1；完整 P4.3、P4.2-B、真实本地整场与 typed visible events 上游供给仍有后续工作。
- 受控 Client/Wire 测试通道没有安装为生产工厂，不作为真实本地/远端 E2E 证据。
- Windows Player 构建 PASS；本轮 Player **运行冒烟 NOT_RUN**，未将构建冒称运行。
- 新收口提交远端 CI 待独立执行/核实，不能继承旧 `f2896e0` 的 2096/2096 结果。
- G1 APPROVED、G2–G5 未批准；V4 继续生产。181 classified 缺口、Android FAIL、iOS BLOCKED、设备/跨网 NOT_RUN 保留。
