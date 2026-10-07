# GLM 接续：修复本地房间绑定竞态

2026-10-06 用户指定 Claude Code 的 GLM 接续开发，Codex 监督、执行测试和验收。此前 Anti 的配额阻塞不再阻塞这一调用。只完成当前 ROOM 小批，之后再派发 UDP。

阅读 RESUME.md、FEEDBACK-R4B1ROOMFIX3.md，直接检查实际源码。保留已完成的 DOMAIN 和 hostSeat 装配。当前独立109项100通过9失败；server86项83通过3失败。不要误把历史21项单次成功作为当前验收。

## 修改范围与交付要求

允许修改 server 的 InProcessBinding、必要的 SharedRoom/RoomRegistry/InProcessServer，以及相关 server 模块测试。不要修改 reviewer 测试、协议、UDP、客户端、engine、展示或构建配置。不要执行构建；Codex 在交付后执行。

1. 修复离房竞态。leave 返回 APPLIED 后连接仍应存活，可以同连接反复创建并启动新房。旧 seat update 和 BindingRoomObserver 回调必须先校验捕获的连接本地 bindingGeneration 与房间引用，再判断 shared seat generation。来自旧房间或旧绑定的回调直接丢弃，绝不能关闭新绑定。显式离房期间 generation 变更不能把自己误判为被顶号。保留真实新连接替换的旧连接失效行为。增加可控回调/屏障回归覆盖，避免只靠概率。
2. InProcessBindingTest replacement resume 测试漏调用 host.setReady(true)，通过真实 API 完成房主准备；保留旧连接不可操作、新连接身份和实际服务端游标验证。不要放宽生产准备条件。
3. SharedRoomTest observer 测试要求全部3个中间快照，违反允许有界最新完整视图合并的契约。修为观察到的版本严格单调、最终收到最新 ready=true 及对应 version；保持观察者隔离与非阻塞保证。不删除有效测试。
4. 核对已部分落盘的 humanSeat != 0 装配。实际真人/admin、默认 AI、快照 viewerSeat 和开局操作应一致，加入非0真人座位的有效模块覆盖。不得新增 LOCAL 固定占位房间/token 兼容路径。

交付到 delivery-GLM-R4B1ROOMFIX4.md，列明修改、尚未验证的事项。当前任务不得擅自推进 UDP/B2/R5/R6；等待 Codex 实测后再接下一批。
