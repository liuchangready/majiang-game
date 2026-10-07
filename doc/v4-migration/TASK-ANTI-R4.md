# R4 权威职责分离与统一房间执行

仅在 R3 验收后执行。Anti 负责生产实现与模块测试迁移，Codex 独立构建、竞争/超时和规则验收。遵循 CONTRACT，保留原有麻将算法。

## 授权范围

engine 的 host/access/control/application 集成边界，新 engine authority 入口；server 的 authority/room/session/inprocess/udp 与协议映射；protocol/client 的大厅契约及接线；相关模块测试、testFixtures、必要 Gradle 依赖配置。不得变更算分/役种算法来适配架构，不得削弱规则测试。大规模路径移动先写清单/脚本供 Codex 检查执行，不运行命令。

## 领域与宿主

- engine 保留 RoundStateMachine、MatchSession、规则服务、ApplicationCoordinator 和纯策略/时间计算；建立不感知 socket、token、连接集合、订阅或 GUI 的 TableAuthority 领域入口。领域可接收可信 seat/controlEpoch/now，不能自行管理网络代次。
- 将 TableHost 中连接/访问端点、客户端同步流、订阅缓存、后台 AI executor、掉线及发布调度迁到 server 权威宿主。不得仅改名或把全部 2000 行搬到新类后称完成；交付逐项职责映射。
- 为保持阶段可编译，R5 尚未替换的旧产品链及旧 TableHost/SessionAccess 源码可原样暂留到 R6 统一删除；这不要求兼容新 API，也不允许为它们新增反向依赖、适配壳或新功能。R4 新 V4 运行链必须真正使用 server 宿主 + TableAuthority，不能内部继续委托旧 TableHost。最终 engine 去连接职责由 R6 全源集门禁检查。所有暂留文件列入删除清单。
- server 每桌一个可注入的串行执行器/命令队列。客户端调用仅提交命令立即返回 Future，不在调用线程等待权威锁。tick/玩家动作/控制/局间操作在同一执行序中提交，权威 deadline 不被动画或持续请求无限推迟。AI 锁外计算并在提交时二次验证。
- 订阅通知、映射重计算和网络发送不能把调用方或全服务器接收循环锁死；清理不产生房间锁/宿主锁反转。房间关闭时在途任务、Future、队列及资源有明确终态。

## 本地远端一致性与大厅

- InProcessServer 和 UdpTableServer 复用同一房间/权威服务，不保留两套不同业务校验。传输只搬运契约。
- 实现明确 LOBBY/PREPARING/PLAYING/SETTLED/FINISHED/CLOSED 生命周期、roomInfo、ready、updateConfig、start 及成员事件。默认单机自动准备并填 3 AI；联机按房主权限、配置阶段、真人准备状态裁决。
- 只读观战使用独立公开投影，不占玩家席位，不接受动作/控制/码牌请求，不暴露任一暗手、袖牌或私有余额。四席位限制保持，非玩家身份不能伪造 seat 获权。
- 显式 leave 与掉线区分，统一 token 撤销、AI 接管、房主离开解散、终局历史投影。单机 leave 不再返回 LOCAL_TABLE_UNSUPPORTED。暂停/恢复为房间策略允许时的宿主接口，避免表现绕过 server 调 engine。
- 码牌、时钟、听牌/立直/副露预览和结算 UI 所需数据通过协议契约供给；client/presentation 不移植领域规则算法。

## 验收

保留/迁移所有宿主、状态不变量、计时和响应竞争测试至其归属模块；纯领域测试继续 engine，宿主/连接测试迁 server。必须验证入队调用不等待被占用的权威执行器、竞争动作只有一次成功、过期 AI/旧连接拒绝、掉线时钟保留、跨局唯一 roundId、按座/观战隐私、两种绑定业务一致。

交付 delivery-ANTI-R4.md，附职责迁移表、测试移动表、旧引用待删清单。该阶段不保留旧接口转发壳；尚未迁移展示由 R5 更换调用点，不以改规则逻辑规避编译或回归失败。
