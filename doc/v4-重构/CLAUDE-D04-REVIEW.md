# D-04 菜单子组件拆分 验收记录（Claude 独立核定）

- 基线 SHA：`8910e5b`；交付：ANTI 第 1 轮（thread_20261019_100215_e28a）
- 简报：`tools/architecture/v4-improvement/ANTI-D04-TASK.txt`
- 交付说明：`doc/v4-重构/ANTI-D04-DELIVERY.md`（由 Anti 撰写，以下结论为验收方独立核定）

## 验收证据

1. **编译**：`:presentation:compileTestKotlin` BUILD SUCCESSFUL（主源码 + 测试源）。
   注：并行任务 D-05 的测试文件曾有一处 `emptySet()` 类型错误阻塞模块编译，
   由验收方自修为 `emptyList()`（一行，见 CLAUDE-D05 记录），与 D-04 无关。
2. **测试**：`:presentation:test` 316 项中 314 通过；仅有的 2 项失败均在
   D-05 的 `TableBoardRenderModelContractTest`（D-05 范围，另行处理）。
   - `MenuScreenTest`：14/14 PASSED（该文件逐行未改，坐标/状态机契约原样成立）
   - `MenuScreenSubViewsContractTest`：4/4 PASSED
3. **静态深审**（验收方逐文件对照基线）：
   - 公共 API 保留：构造签名、`currentState`/`errorMessage`/`activeLobby`、
     `MenuState` 九值、`touchDown/keyDown/keyTyped/dispose/show/hide` 均原样。
   - 像素级布局：六组按钮矩形、弹窗、面板、字号、颜色、文案与基线逐值一致
     （MainMenuView/LobbyListView/ConnectDialogView/CreateRoomDialogView/
     JoinRoomDialogView/RoomPreparationView/MenuScreen 弹窗段逐一比对）。
   - 行为不变量：`requestGeneration` 迟到完成守卫（连接取消关迟到会话）、
     `hasNavigatedToTable` 三处一次性导航、刷新后选中重置首行、
     断开返回顺序（detach → close → leaveToMenu）、ESC 分支表、
     dispose 幂等均逐字等价。
   - 所有权边界：订阅（attach/detachLobby）、角色、业务流全部唯一归属父层；
     六个子视图零 `LobbySession`/`TableNavigation` 依赖（读文件核实 +
     新增反射契约测试双保险）。
   - 状态生命周期：对话框实例随父层长期持有，输入/开关跨开关保留；
     「输入房号加入」进入时清空语义保留。

## 验收方注记（非缺陷）

- `keyTyped` 路由由 activeInput 全局字段改为按 currentState 分发：弹窗关闭后
  残留焦点点键不再误改输入框内容（原基线可编辑 MAIN 态下的隐藏输入）。
  属拆分后的合理收敛，无测试依赖旧行为，接受。
- `ConnectDialogView.resetActiveInput()` 当前无调用方（预留 API），无害保留。
- 遗留（计划内）：秘籍/设置弹窗未拆（简报明确不在 D-04 范围）。

## 结论

**通过**。D-04 独立提交；与 D-05 无文件交集。
