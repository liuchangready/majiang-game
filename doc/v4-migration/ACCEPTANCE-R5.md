# R5 表现层与产品入口验收（2026-10-07）

R5 功能迁移已达到进入 R6 清理的门槛。默认单机入口和菜单单机入口均通过 V4 大厅建房、开赛并进入 V4 玩家画面；菜单、游戏画面、观战画面、共用渲染只依赖 client.api 展示契约。旧 presentation V3 主源与适配器已移除，菜单位于 `presentation.menu`，游戏画面位于 `presentation.game`，共用资源位于 `presentation.common`。

验证证据：

- `build/v4-migration/R5-direct-final-checks.log`：不筛选的 `test`、桌面 `compileKotlin` 和 Android `assembleDebug` 同次构建成功。模块测试 engine 369、protocol 136、client 147、server 111、presentation 252，共 1015 项，零失败、错误和跳过。
- `build/v4-migration/R5-direct-gl-final-*.log`：desktop、v3 默认入口、round-session、riichi-choice、furiten、meld、meld-display、meld-choice、discard-input-gate、settlement-detail、end-tenpai 共 11 组真实 GL 冒烟通过。默认入口场景验证码牌选择、100 点初始余额与 40 点权威扣费、实体牌出牌及返回菜单释放。
- `build/v4-migration/R5-direct-review.log`：独立审查 188 项中 183 项通过，5 项失败均是尚未完成的最终架构收口：当时 menu 尚未搬包（现已修复），以及 R6 旧 TCP/V3 类型、跨层依赖和模块依赖。此日志不是 R6 通过证据，须在 R6 后重跑。

R5 期间修复了快速开赛连接代次、迟到 UDP 连接关闭、大厅订阅刷新、观战退出所有权、立直振听悬停、弃牌事件时序与悬停清理、终局无事件恢复、旧结算分页、Android BACK 捕获恢复。旧展示行为测试迁至 V4 并保留实体牌、规则、计时、结算和渲染断言。

仍需 R6：删除旧 TCP/V3 运行链并收紧 protocol/client/presentation 生产依赖；迁移旧链测试中的有效隐私、守恒、计时与恢复断言；最终重跑独立审查及双端和 UDP 检查。真实菜单 UDP 的完整交互与掉线恢复仍需最终产品验收，不以本次单机 GL 冒烟代替。
