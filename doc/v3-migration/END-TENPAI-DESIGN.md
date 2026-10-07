# 击飞终局与听牌提示设计

2026-10-02。Codex 设计、测试和验收，Antigravity 开发生产代码。本文是本任务的唯一实施契约；PLAN.md 中历史迁移任务不在本次范围内。

## 问题与目标

用户反馈有人击飞后比赛没有结束，且进入听牌后看不到待牌。当前 MatchProgression 已有按配置执行的击飞判定；GameScreen 的结算渲染同时检查 MATCH_END 和 Session FINISHED，但点击分支只检查 MATCH_END，存在终局按钮错误请求下一局并隐藏结算的问题。需通过 V3 实际宿主回归定位其余断点，不把单局终态重写成整场状态。当前 RiichiPreviewProvider 只服务立直选牌，缺少常驻听牌提示。

开发前实测补充：BankruptcyHostAcceptanceTest 的自摸击飞、流局罚符击飞及零分阈值对照均通过。Coordinator.queryView 已将 FINISHED 投影为 MATCH_END；正常 V3 路径的直接缺陷是点击 MATCH ENDED 只关闭明细、留在已结束的牌桌，没有总成绩或明确退出流程。ROUND_END + FINISHED 的不同步判断作为恢复及表现边界回归一并修正，无需改变权威规则。

## 击飞终局行为

1. 沿用 MatchConfig 的 bankruptcyEnabled、threshold、inclusive。默认小于 0 击飞，等于 0 不击飞，不擅自改变规则。和牌或流局结算后先完成整局记账，再判断击飞；击飞优先于连庄、和止与下一局准备。剩余供托只结算一次。
2. MatchSession FINISHED 与其 finalResult 是 V3 整场终局依据；单局 RoundStateMachine 保持 ROUND_END。旧入口兼容 MATCH_END。在 GameScreen 提供统一只读 isMatchFinished，由渲染、点击、可操作状态共用。不得用 UI 分数自行推断权威终局。
3. 最后一局明细仍可查看。底部终局按钮明确显示“比赛结束”或“查看总成绩”，点击进入总成绩模态，显示终局原因（击飞等）、四人名次/最终点数，并提供“返回菜单”。此路径不得调用 nextRound、start 或提交游戏动作。返回菜单复用已有退出回调释放宿主。可提供“本局明细”返回明细且不恢复比赛。
4. FINISHED 快照即使没有实时终局事件，或没有 settlementEvents，也能显示总成绩；重连与同局重复快照保持合理模态，不自动隐藏已显示的总成绩。总成绩只使用 session.finalResult；兼容入口缺失时可显示“比赛已结束”及公开分数，不能伪造终局原因。
5. 尚未终局的下一局按钮在异步请求被拒绝/失败时保留原结算。成功等待新局权威快照清理。提交期间禁止重复点击。终局无下一局，无准备倒计时，无自动行动继续推进。

## 听牌提示行为

1. 自己进入听牌即显示手牌上方常驻小面板“听牌”及所有待牌牌面，使用现有 TileSkin。普通默听、立直、已副露听牌均支持；不要求轮到自己，不依赖 DeclareRiichi 合法动作。
2. 新增 engine/session/TenpaiPreviewProvider.kt，固定接口 `object TenpaiPreviewProvider { fun waits(view: PlayerView): List<Tile> }`。只读本人视图，复用 FuritenRules.waits，不复制分解算法。输出按 typeIndex 去重排序、普通非赤展示牌、不可修改列表。只用本人暗牌/副露，不读未知牌山、他家暗牌或精确剩余张数。
3. 有效等待形为暗牌数 + 副露数 * 3 == 13。自己摸牌时若 myDrawnTile 非空，使用 myClosedTiles 展示摸牌前等待形，标题注明“原听牌”；打牌后改用新等待形。吃碰后尚未弃牌、其余 14 张等效形、准备/终局阶段不显示旧待牌。DRAW/DISCARD/CHEAT_WINDOW/RESPONSE 等正常打牌阶段按此逻辑处理。杠按一个面子计数并排除本人已经持有四张的第五张候选。
4. 面板说明“牌型待牌，和牌仍需满足役种与振听规则”；振听时清楚提示“振听：不能荣和”。不把理论待牌宣传为必可荣和，不新增概率/番数/未知剩余数。既有振听提示保留。
5. 所有待牌均显示，包括国士 13 面；合理换行，不能遮住手牌、操作按钮/排序按钮。立直选牌与副露选择面板打开时暂时隐藏常驻面板，结算、总成绩、洗牌准备时隐藏；恢复/换局从最新视图重算。缓存或随快照更新，禁止逐帧重复分解。

## 开发范围与验证

Anti 负责 GameScreen.kt、必要的新增 core 表现/渲染类、TenpaiPreviewProvider.kt。确有权威终局断点可最小修复 engine/match、application、host，但不改既有规则默认值/网络 DTO，不修改测试/构建文件；记录原因与改动。保留工作区所有现有未提交变更。

Codex 独立验证：V3 宿主从和牌/流局至击飞 FINISHED、finalResult、下一局拒绝、roundId 不变及重连恢复；0 分阈值对照；屏幕 ROUND_END + FINISHED 终局点击无 nextRound，缺事件恢复总成绩，失败下一局保留明细；待牌覆盖默听/立直/副露/七对/国士13面/四张上限/摸打刷新/非零座位/不变量；已有多模块测试、桌面编译、Android 构建，以及真实 GL 截图检查两项 UI。

交付报告：doc/v3-migration/delivery-ANTI-END-TENPAI.md。禁止执行 shell、构建与测试，仅用内置读写编辑工具；由 Codex 执行验证并反馈修复。
