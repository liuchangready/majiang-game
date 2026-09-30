# Codex 审查 A：必须修正

1. drawRinshan 只检查 rinshanPointer < deadWall.size，会允许第 5 次从宝牌指示区域摸牌。必须限制为 0..3 四个岭上槽位，并在失败前保持所有容器/账本不变。
2. StateInvariants 的初始 containersEmpty 判断漏掉 deadWallReplacements，初始态藏有替补牌仍可能被误判为合法 UNASSIGNED。补全检查。
3. 已开局的有效王牌必须恰好 14，不能用 `if (deadWall.size >= 14)` 放过长度 0..13 的损坏牌桌。
4. 阶段手牌计数不能仅为 13..14：DRAW 为全员13；DISCARD/CHEAT_WINDOW 为行动者14其余13；RESPONSE_WINDOW 为全员13；KAN_DRAW 为待岭上摸牌者13。事务中间态不审计或明确使用专门阶段。最终抢杠窗口由任务 D 的独立阶段定义适配。
5. TestTableBuilder 是测试夹具，应由 Codex 在 test 源集编写。删除你新增的生产 model/TestTableBuilder.kt，不要迁移到 test 或替我写测试；将需要的构造接口写在交付中即可。

修正仅限 A 文件。交付中不要宣称已运行测试，构建由 Codex 执行。

后续构建发现：GameState.deepCopy 调用 tilePool.copyFrom，但 TilePool 没有该方法，当前独立领域编译失败。请补齐真正复制账本的接口。另 wallEndExclusive 的 getter 将合法值 0 隐式改成 wall.size，会隐藏空活牌山；使用显式初始化，不用兼容回退。
