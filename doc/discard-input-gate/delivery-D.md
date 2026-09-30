# Task D 交付说明：出牌演出输入门禁

- 在 GameScreen 新增只读 `isDiscardPlaybackBusy`（动画播放或排队时为 true）。
- SessionUpdate 订阅中同步调用 `checkDiscardsDiff()`，消除 submit 至下一帧的抢点窗口。
- touchDown 位于返回/结算后、SORT/游戏操作前拦截并吞掉点击；busy 期间清除悬停高亮。
- `isHumanTurn` 严格要求 `!busy` 且持有合法 `DiscardTile`；所有鸣牌/和牌/Pass查询增加 `!busy`。
- `renderActionButtons` 在 busy 时显示 `WAITING FOR DISCARDS...` 并暂时隐藏选牌面板。
- `checkDiscardsDiff` 自动清理被吃碰杠取走的牌河待播项；开局/结算/离开场景清空动画释放锁。
