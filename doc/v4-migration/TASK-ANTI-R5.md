# R5 表现层与 V4 产品入口切换

仅在 R4 验收后执行。Anti 开发，Codex 独立构建、规则回归与真实 GL 验收。用户不要求旧启动入口兼容。

用户现已授权继续R5/R6；先阅读[R5/R6准备审计](R5-R6-PREPARATION-AUDIT.md)，尤其Android网络权限、后台连接、主动leave、共享皮肤所有权、中文字体和独立观战画面。该清单来自当前源码只读审计，不代表R4通过或本任务已经派发。实际并行前由Codex冻结公共UI/导航/client展示契约并明确各路所有权。

表现层测试迁移同时遵守 [旧链测试保留清单](R6-TEST-PRESERVATION-INVENTORY.md) 的表现层段落；R5就要保留点击/动画/结算/计时/资源生命周期行为，不能等R6删除旧测试时再发现缺失。

## 授权范围

presentation 全部主源码及其测试、应用组合根、lwjgl3/android 启动器和必要 Gradle 配置，server 默认启动 main；client.api 必要展示适配与模块测试。tools/architecture 中旧 GL 工具只读，列出需要迁移的 API，由 Codex 调整工具。不改审查断言或领域规则。

## 交付

- 拆分 presentation.menu、presentation.game、presentation.common；字体/皮肤/按钮等共享资源职责清晰。不只是改包名：这些包不得 import engine、protocol、server 实现或旧 GameClient。
- 菜单通过 LobbySession 完成单机建桌、赛制/规则配置、UDP 地址端口连接、列房、建房、加入、房间详情/准备/开赛和观战。菜单只显示用户需要的信息，不把内部组件名展示成操作流程。
- GameScreen 仅消费 client 自有快照/候选/事件/回执，提交 optionId；保持中文字体与皮肤、理牌、红五、对家/上下家副露布局、鸣牌/立直物理选牌、出牌动画门控、20+3 计时、码牌私有准备、听牌待牌、结算符番役种与最终排名/历史翻页。
- 应用组合根装配 ServerConnectionSession/客户端 runtime 与 InProcessServer 或 UdpClient 工厂，默认 launchGame 走 V4。删除 SessionAccess 启动重载与 LocalHost 直连路径，不增兼容重载。动画忙提示由 server 提供的非权威节奏提示接口传递，不允许 UI 延迟权威超时。
- 断线自动重连显示明确状态，保持最后可信视图、禁用不能提交的输入；恢复不重复播放历史动画。主动返回菜单正确 leave/close，临时掉线不关闭远端比赛，窗口关闭幂等释放。
- 默认 :server:run 启动 UDP 实现，并保持 --bind/--port 运行参数明确。桌面/Android 组合根均可编译；Android 主线程无阻塞网络调用。server 依赖由组合根装配，展示包不能借此访问实现。
- 迁移展示测试，所有既有有效规则/渲染行为断言保留。列出所有旧类删除和最终入口，不保留 V3GameClientAdapter 的兼容壳。

交付 delivery-ANTI-R5.md 与本阶段 GL 工具所需接线变化。Codex 验证桌面实际主菜单、单机全链路、两端 UDP 开赛/掉线恢复、已有 GL 场景和 Android APK。生产入口未切换即不通过。
