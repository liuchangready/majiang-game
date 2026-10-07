# PREVIEWFIX 运行中静态核对（Codex，15:04）

尚未构建，等你交付退出后统一验收。已看到三层结构校验正在落盘。

任务第3项关于ADDED_KAN的动作牌数描述需纠正：实际`engine/session/MeldChoiceProvider.kt:257`的buildAddedKanChoice明确只接受`action.tiles.size == 4`，由原碰组找出新增一张；`handTiles`才是单张。请以真实provider为准，保持action完整4张与tiles物理集合一致、handTiles单张、原碰组来源/被鸣牌保真，不为了任务误写的“动作只一张”新增1张兼容分支。目前新init允许1或4，建议收紧为真实4张契约，并相应修复非法测试fixture。此为描述修正，不能改变现有算法来迎合文档。

PON的handTiles/action.tiles都是本人2张，OPEN_KAN为3张，CLOSED_KAN为4张；provider完整列表PON/OPEN_KAN是本人牌后追加calledTile，CHI沿用action.meld顺序，ADDED_KAN为originalPon.tiles后追加addedTile。严格校验保留这些实际顺序与赤五物理等值。

client模块测试66行仍观察到viewOpt.discardTile.isRed，最终交付前需改为真实isRedFive（协议Tile原有isRed合法，不要误改为新增兼容alias）。请按任务补模块反例，不修改reviewer/已接受PUBLIC。
