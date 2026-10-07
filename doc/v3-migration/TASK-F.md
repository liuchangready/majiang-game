# F：默认游戏入口接入V3宿主、异步Client与计时展示

依赖D宿主和C2客户端已完成，D2正在修正host细节，E即将机械迁移。现在可以并行开发，你拥有的core文件都不会移动；如果engine/client文件还没有移动到新位置，先从core/src/main读取同包类。读取即可，不要修改它们。所有权core/src/main/kotlin/com/majiang/MahjongGame.kt、screen/**、新增core中的presentation/**桥接；不修改engine/client/server/Gradle/测试。

目标：菜单进入游戏必须真正走TableHost → SessionAccess → GameClient → Screen，不允许开发了模块而默认仍旧LocalTableRuntime。保留现有牌桌、立直/副露选牌、振听、算番明细、动画顺序与输入门控。

1. MahjongGame作为组合根创建TableHost及seat0 SessionAccess、GameClient，持有宿主生命周期，render时调用host.tick(screen.isDiscardPlaybackBusy)。网络Access不创建本地权威；`launchGame(access:SessionAccess)`可进入共享同一GameScreen。关闭/回菜单释放本地宿主；network端点只关客户端不直接关远程比赛。
2. Screen使用异步GameClient（可以有纯展示适配器），不得在UI线程get/join等待future。GameClient.dispatch使用Gdx.app.postRunnable或保证当前展示线程，使用其deep-copied更新。构造时尚无snapshot显示加载态、不可操作。
3. 改造提交链：UI选择使用client.choose(action)，pending时禁用出牌/立直/副露按钮，直到收到回执或失败。只有权威更新才能改变手牌副露；RECORDED只表示等待裁决。所有失败都要可见提示并恢复交互，不无声丢弃。不要伪造ActionResult.Accepted(emptyList)冒充已经提交。
4. 旧GameScreen(PlayerEndpoint/GameSession)构造可在迁移期为原展示测试保留可明确识别的兼容适配器，产品默认不走该链；同步public submitAction若保留只能服务同步适配，无网络阻塞，内部UI必须用统一async方法。Codex会迁移必要测试，不通过修改测试绕过要求。
5. 单次更新的AuthorityView立即接收、动画仍按事件顺序展示；在展示尚落后于决策所需状态时禁止操作。局切换、resume新syncEpoch时清空旧动画/候选，安装一致快照。不再仅按座位差异重建跨轮次顺序。
6. 显示本座位当前倒计时（免费时间与储备含义清楚）、连接/托管状态、异步等待/恢复状态。计时由ClockView截止和serverNow采样估算，不能发送客户端自认超时命令或重置时间。单机暂停策略可由宿主明确接口处理，不能仅暂停动画但继续偷偷扣时。
7. 为三模式入口提供可操作的接口：至少默认单机启动；网络Access已可由应用入口launchGame接入。若server任务已完成，可在菜单提供远程host/port/room的连接UI和创建房间选项；不得自己依赖server类（Android是客户端）。桌面自建服务器启动由独立server:run或桌面launcher组合根处理。
8. GameScreen仍不可公开GameState/Coordinator/Host写权限，AI名字/玩家名字不假设其他座位一定是AI。图形布局保持1280×720与现有皮肤兼容。

交付delivery-F.md说明默认路径、生命周期、旧兼容入口剩余范围。Codex运行所有原Screen测试与真实GL冒烟，不可删除输入门控/立直/副露功能以简化开发。
