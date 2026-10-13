# BS-E1：偷梁换柱领域实现

阅读AGENTS.md、[设计](DESIGN.md)与[接口约定](INTERFACES.md)。用户授权Anti开发完整beam-swap，Codex负责测试与检视。本任务为第一阶段engine，完成并回调后由Codex下发网络及表现层阶段；不以E1完成冒充整项完成。

## 必须实现

- 所有engine状态、请求/视图、权威入口、原子扣费、私有投影、时钟重键、去重、超时及终局清理、控制代次恢复，严格遵守DESIGN。引用已生效的本地SB-KAN-DORA实现，不恢复旧的全部立即翻宝。
- 正常候选最多8个活牌山槽；杠后候选为有效死山未展示槽，含替补，最多13张。只交换原有closedTiles；drawnTile与所有副露禁用。已取岭上前缀是历史引用，不按indexOf实体定位。第四杠仍可beam但不能sleeve-bloom。
- 未标记不泄漏身份；仅本人有效落汗标记候选亮面。PlayerSnapshot交叉校验/防御复制、公共投影安全。激活前无新增隐藏信息，结束后不残留槽位定位。
- 袖里藏花与beam同一次取牌二选一、各自已用字段联合判定。全局唯一offer。落汗专用authority/coordinator/projection均加挂起门禁；普通动作/AI/超时/立直自动摸切全路径处理。
- 请求错误offerId/非法hand/slot仅拒绝且保留当前offer；真实状态漂移清理offer且不退款、不重置限次/deadline、不提前弃牌。KEEP_ORIGINAL不含选牌字段。
- 物理交换与offer事务必须满足136实体/有效死山14，不改摸牌槽、副露、指针、剩余量、待翻杠宝计数。选择后重算动作与听牌/立直预览及实际算分。
- 保持全项目编译；此阶段不接protocol/client/UI，默认配置关闭。新增字段默认值保持既有调用兼容；必要独立辅助文件可拆分，避免进一步堆叠单个巨型类。

## 分工与验证

Anti只修改engine生产代码和本阶段交付文档。Codex独占全部测试和tools；不得删除、改写、跳过测试，不得创建弱化验证的副本。构造器/模型遵守INTERFACES，发现测试错误写反馈并等Codex修正。

Codex开始编写领域行为测试；看到 `build/beam-swap-e1-tests-ready.json` 后运行 `:engine:test --tests '*BeamSwap*' --tests '*SleeveBloom*' --tests '*TileMark*'`，再运行不筛选的 `:engine:test`。你独占Gradle直到回调，Codex不并行运行。失败先修复；如果测试源码编译或夹具错误，应及时写 `doc/beam-swap/FEEDBACK-ANTI-E1.md`，不要自己修改测试。

不要Git提交/推送，不启动其他代理或Codex进程。工作区此前有未提交的SB-KAN-DORA（已独立验收），须在其基础上叠加，绝不能git checkout/reset恢复这些文件。保留doc/README.md、lwjgl3/build.gradle、SnapshotLauncher.kt、snapshot/及其他用户既有改动。

## 完成回调

先修复所有已知失败，再写 `doc/beam-swap/DELIVERY-ANTI-E1.md` 和 `CALLBACK-ANTI-E1.json`，字段recipient=codex、taskId=BS-E1、status=ready_for_review、变更列表、接口说明、真实测试数量/命令/结果/未验证边界。stdout最终输出 `CODEX_CALLBACK BS-E1 ready_for_review` 并退出，将Gradle交回Codex。回调是请求检视，不是最终验收；发现问题后须按Codex反馈改进并重新回调。
