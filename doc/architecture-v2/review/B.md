# B 第二轮返修（小范围独占）

你只负责 core/src/main/kotlin/com/majiang/rules/WinningScoreEvaluator.kt。不要修改其他生产文件，D 正在修改规则上下文与引擎，C 正在修改事件与会话。

Codex 实测 WinningScoreContractTest 的 repeated dora indicators 用例失败：2张5万+3张3筒，对两个4万指示和一个2筒指示，应得到7翻，目前得到5；再加一个4万里宝应9。countDora 不应使用 in 去重，同一物理牌可被多个指示重复计数。按每个指示牌对应的命中张数累计，保留副露计数与里宝只在立直时使用。

其余该类测试已通过：多种拆解最高最终得点、无役宝牌不能和。只做必要修复，更新 delivery/B.md，不要声称你运行了测试（测试由 Codex 执行）。
