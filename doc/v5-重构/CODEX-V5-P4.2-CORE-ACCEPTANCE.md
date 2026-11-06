# P4.2-core 调用者独立验收

日期：2026-10-27。结论：**ACCEPTED（仅本任务范围）**。

任务 `thread_20261026_163027_3b00` 第 9 轮候选为
`4ced10f2689f07d4b38deade974312ed74421bf3`，冻结基线为
`8fd7c6cb701dfbb41c28938de4186749bdd1d6c7`。
调用者已执行 `approve_task`，桥接返回 COMPLETED 并归档。

## 范围与缺陷关闭

Boot → Menu → Lobby、唯一 EventSystem、Input System、主线程派发、
会话 owner 关闭与隔离、返回键、字体与安全区域属于本次验收范围。
D-1-03 的首次 Submit 被旧 pointer/cancelled 状态吞掉问题及
D-1-07 的负例矩阵计数残留均关闭。新增测试保留两种真实组件次序，
验证尾点击后的第三个事件以及已 armed 指针暂停/失焦恢复后的首次 Submit。

181 个交付文件均匹配原 expectedFiles 与第 2、3 轮精确补充授权。
没有新增已确认的范围内 BLOCKER/MAJOR。基线到候选 `git diff --check` 通过。

## 调用者实际验证

原任务工作区：`D:/majiang-wt/p42-core-anti-20261026`。
证据根：`build/codex-p42-r9-4ced10f`，以下结果均为本轮实际执行。

| 验证 | 结果 |
| --- | --- |
| 正式 `test-p42-shell.ps1`，独占 OutputDir/LogDir | native exit 0，127.69 秒 |
| Unity EditMode | 35/35，failed/skipped=0 |
| Unity PlayMode | 26/26，failed/skipped=0 |
| 负例变异矩阵 | 13/13，横幅及摘要真实显示 13 |
| 编译后的 StrictCompiled 边界检查 | PASS |
| 全新 Windows Player 构建与 smoke | PASS，4 张有效 PNG |
| 原独立 Unity 探针，原样复制到本轮证据目录执行 | 54/54，native 0，failed/skipped=0 |
| 源码链接输入矩阵 | 14/14，native 0 |
| capability 正例/禁止源码/asmdef 环 | 3/3，预期退出码 0/1/1 与状态匹配 |
| strict 缺目录/有效程序集/native PE | 3/3，预期退出码 1/0/1 与状态匹配 |
| fresh `build-shared.ps1` 到 scratch | native 0，12 个运行时 DLL |

复现入口：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/unity/test-p42-shell.ps1 -OutputDir build/codex-p42-r9-4ced10f/suite -LogDir build/codex-p42-r9-4ced10f/logs
powershell.exe -NoProfile -ExecutionPolicy Bypass -File build/codex-p42-r9-4ced10f/probes/run-probes.ps1 -Filter CodexP42
D:/Unity/dotnet/dotnet.exe run --project build/codex-p42-r9-4ced10f/input-source-probe/InputProbe.csproj -c Release
```

已目视检查菜单、大厅与退出弹窗截图。大厅明确显示本地宿主不可用且开始按钮禁用。
本地真实工厂属于 P4.1/P4.2-B，未以占位实现冒充已接通。

共享 .NET 源码与冻结基线一致。7 个自身程序集的 TypeDef、方法签名及方法 IL
与本轮 fresh scratch 构建一致，5 个第三方 DLL 的 SHA256 完全相同。
自身 DLL 的整文件 SHA256 不同：提交版本信息为冻结基线 SHA，fresh 版本信息为候选 SHA；
指定冻结 SourceRevisionId 再构建的整文件比较也不相同，此尝试原样保留。
不把这些比较声明为二进制可重复构建通过，不用 DLL 整文件哈希替代源代码/方法核验。

原始日志、XML、探针、截图及比较结果在上述证据根。
`evidence-sha256.csv` SHA256：
`F305FDED028127A72D54F84574E2835B964B05D1CEF4AA2DF128FEE962179E09`。
Unity 自动生成的 8 个 tracked 差异已保存 patch 后按精确路径恢复；
SceneTemplateSettings 先备份再删除。临时探针程序集按自有精确路径清理，原任务工作区最终 clean。

## 批次集成与边界

复用唯一草稿 PR20（`feat/v5-post-g1-batch` → `v5`），保留已接受的
P5.2 与伴随黄金验证入口改动。此次以 merge 纳入候选完整历史，未创建组件 PR。
集成后 `unity`、`tools/unity`、交付报告与接受候选整树一致。
集成工作区静态边界检查 native 0；fresh Release 架构测试 build
0 warning/0 error，direct runner **83/83**，errors/failed/skipped/not-run=0。
相关集成证据位于 `artifacts/p42-acceptance`。
集成工作区未另跑 Unity Editor；编译检查及 Player 证据来自上述相同 Unity 树的原任务工作区。

P4.1 仍在返修，P5.1 当前 failed，均未纳入。PR20 保持 draft，后续接受交付继续汇入同一 PR。
本次没有重跑整批 PostgreSQL/全量 .NET，也不把旧 CI 成功归给新增提交。
新远端 CI 结果另行记录。V4 继续唯一生产；G2–G5 未批准；
Android/iOS、真机、跨网络及服务端端到端缺口按原台账保留。未 merge 到 v5、未部署、未创建 complete.flag。
