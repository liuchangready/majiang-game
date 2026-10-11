# Anti：E2 跨局标记状态

先完成 engine 内独立、不可变的跨局状态组件，供随后 MatchSession 和权威原子提交接入。本批次不宣称整个落汗已实现。

Codex 正在独立编写 `TileMarkStateContractTest`，Anti 只写 `engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkState.kt`，不改任何测试。保留已通过检视的模型文件及其他工作区改动。

## 固定接口

新增 `class TileMarkState()`，仅空状态构造公开；内部状态构造可私有。提供只读属性 `version: Long`、`dealtRoundOrdinal: Int`、`dealtRoundIds: List<String>`、`settledRoundIds: Set<String>`、`groups: List<TileMarkGroup>`。

所有方法返回新的不可变状态，不能修改旧实例或外泄可变集合。无变化/合法重复操作返回原实例，version 不变；实际变更 version 加 1。

- `registerDealtRound(roundId: String): TileMarkState`：同一 ID 已登记则返回原实例，不能重复扣有效期，也不能倒退到旧局；新 ID 只能在上一局已结算或初始空状态登记，局次加 1。拒绝空 ID。不负责决定何时发牌，后续协调器只在实际成功发牌时调用。
- `addGroup(group: TileMarkGroup): TileMarkState`：仅当前已登记且未结算局可创建，创建 roundId/ordinal 必须匹配；同一组 ID 已存在且内容相同返回原实例，内容不同拒绝。一个座位各组实体不重叠，最多 12 张/3 组；不同座位允许标记同一实体。所有拒绝抛 IllegalArgumentException，不改变原状态。费用不在本组件扣除，后续与 SessionChange 原子提交。
- `completeRound(roundId: String): TileMarkState`：必须是已登记当前局，已结算 ID 重复调用无变化；未知 ID 拒绝。在结算最后有效局时移除到期组，其他组继续保留。
- `groupsForSeat(seat: Int): List<TileMarkGroup>`：只读该座位组；非法座位拒绝。
- `clearSeat(seat: Int): TileMarkState`：清理该座位全部组，不改变其他座位、发牌局次或结算历史；非法座位拒绝。
- `clearAll(): TileMarkState`：清除全部组但保留局次和历史；本组件由 MatchSession 拥有、最终随整场释放。空组时无变化。

不要公开“任意替换组集合”的方法，不要依赖 server/protocol/client，也不要移动牌。字段和返回集合防御复制且只读，使用 E1 已不可变的 TileMarkGroup。期限仍为创建局加 3；当前局和三个后续局都有效，最后一局结果接受后清除。旧组不能被新组续期。过期后使用原创建局重放 addGroup 必须拒绝，幂等回执由后续权威缓存负责，不能复活旧组。

## 验证与回调

1. 先实际写生产文件，再运行 `./gradlew :engine:test --tests 'com.majiang.cheat.mark.*' --console=plain`。Codex 会先落地测试；不修改、弱化或跳过任何测试。
2. 失败只修本批次生产实现，修复后复测；最终运行 `./gradlew :engine:test --console=plain`，核对 XML 套件和 tests/failures/errors/skipped。
3. 交付写 `doc/tile-mark/DELIVERY-ANTI-E2.md`；进度写 `PROGRESS-ANTI.md`；完成通知写 `CALLBACK-ANTI-E2.json`（taskId E2、recipient codex、status ready_for_review 或 blocked、测试结果及交付路径）。
4. 最后输出 `CODEX_CALLBACK E2 ready_for_review` 或 blocked，退出当前 CLI，由 Codex 收到回执后独立验收。不另外启动 Codex，不委派其他代理，不 Git 提交，不更改任务外代码。

本次允许必要构建和测试，agy 调用带 `--dangerously-skip-permissions`。不要因流程文档中此前的“只读写文件”历史限制而不运行测试，以本任务为准。
