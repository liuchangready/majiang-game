# R5/R6 接续准备审计（只读，未执行阶段开发）

用户已授权 Anti 开发并继续 R5/R6；前置仍为 R4 完整验收。目前 Anti SERVERRESUME/CLIENTRESUME 持有 R4 文件范围，本审计不授权提前跨写。Codex及只读agent核对实际源码，未运行本阶段构建或产品验证。

## R5 实际迁移范围

- `presentation/.../MahjongGame.kt`：createLocalHost/localHost/ownedLocalAccess、launchGame(SessionAccess)、render/pause/resume仍走旧TableHost。组合根改装配InProcessServer/ServerConnectionSession和UDP工厂，单机tick不阻塞调用方；删除旧启动重载。
- `screen/MenuScreen.kt`：当前无LobbySession流程，仅单机/皮肤/秘籍/设置；补地址端口、连接状态、列房/建房/加入、赛制规则、成员/准备/开赛/观战，去掉设置里的V3内部组件说明。画面只收窄门面与导航回调，不能把MahjongGame当server/engine服务容器。
- `screen/GameScreen.kt`、MeldSelectionModel、RiichiSelectionModel：替换旧GameScreenClient/GameClient/V3GameClientAdapter及领域动作、快照、候选提供器。直接消费client快照、presentationPreview和optionId，保留decisionId/controlEpoch/物理牌ID；移除旧同步ActionResult入口，删除旧适配壳。
- `presentation/ClockDisplay`、ShufflePreparationModel/Panel：迁移时钟、私有码牌预算/阶段/回执到client自有类型。
- `screen/SettlementDetailPresenter/Renderer`、FinalResultRenderer、TenpaiPanelRenderer：迁移完整符番役、支付、供托、历史/排名/分页与恢复状态，不能丢字段或搬回领域算法。
- `render/TileSkin/TileArranger/DiscardAnimation/DoraSparkle/MeldDisplayLayout/MeldDisplayRenderer`：改client物理牌/副露类型，保留红五、理牌、各方位副露和动画门控；SkinManager/TileDrawBuffer/GhostHandTexture明确common资源所有权。
- `server/TableServerMain.kt`及server/build.gradle：默认run改真实UDP并保持bind/port和实际端口输出。
- 两平台启动器、Gradle和AndroidManifest：装配与依赖随默认入口迁移，画面边界不因组合根依赖server而放宽。

## 建议派发顺序与所有权

先串行冻结common资源/导航接口、画面构造器及client展示字段；再按互斥范围并行：

1. common/渲染：现render目录、ClockDisplay、结算/听牌渲染器及对应测试；不写GameScreen/Menu/组合根。
2. game/观战：GameScreen、鸣牌/立直选择、码牌model/panel、观战画面及相关行为测试/假会话；旧GameScreenClient/V3GameClientAdapter删除归本路。
3. menu/装配：MenuScreen、MahjongGame、平台/manifest/Gradle、server main与组合根测试；只消费冻结构造器。

client.api若有真实展示缺口仅指定一个拥有者，其他路报告缺口；不允许三路同时改ViewModels/PreviewModels/runtime。R4写入结束且验收通过前不得派发。

## 必须保留的真实运行行为

- Android当前缺INTERNET权限；APK成功不证明网络可用。
- UdpClient.connect同步创建socket/解析InetSocketAddress并connect，runtime.connectUdp立即factory()；初始连接须后台执行，UI经Gdx.app.postRunnable派发，不能仅凭Future声称非阻塞。连接失败也须释放已创建socket/自有调度器。
- 主动返回大厅先完成leaveRoom语义，不能以close冒充离房；切画面不应关闭仍需复用的大厅runtime，临时掉线不能关远端比赛。
- SkinManager拥有共享皮肤，画面借用资源不能重复dispose；验证连续进退桌、换皮肤再进桌。
- 新中文大厅/房间/错误信息不能使用无中文字形的默认BitmapFont；保留NotoSansSC资源与freetype装配。
- 全量/重连恢复保留可信画面、禁旧输入、清旧决策与动画，不重放历史弃牌、不自动claimControl；观战用独立SpectatorSession/PublicSnapshotView，不能伪造玩家快照后隐藏按钮。

## Codex GL 工具迁移

SmokeScreenClient目前实现旧GameScreenClient，需要换client.api假会话；禁止为测试保留生产兼容壳。DesktopRenderSmoke/RoundSessionRenderSmoke/V3RenderSmoke使用localHost，必须改真实V4入口观察并保留开局/自动推进/回菜单释放断言。Meld/MeldChoice/MeldDisplay/RiichiChoice/Furiten/DiscardInputGate/SettlementDetail/EndTenpai相关Smoke及init脚本需按冻结API迁移；EndTenpai还依赖旧presentation测试fixture。此范围由Codex持有，Anti仅报告API变更。

## R6 删除前必须有的替代证据

- 旧TableHost/LocalSession/SessionAccess/MahjongGameAccess/LocalGameSession/PlayerEndpoint/LocalTableRuntime：先证实V4默认单机、AI、计时、暂停、码牌、结算续局和旧有效断言已迁移。
- 旧GameClient/sync/network TCP、TableServer/ServerRoom/ClientConnectionHandler：先证实真实菜单UDP双端开赛、断线恢复、显式leave/停服、本地同契约及默认server入口。
- 旧network/protocol编码器按真实引用审计，不能依据包名全删。
- 禁止批量清空engine/access、session、host：V4实际仍用PlayerSnapshot/ActionReceipt/ClockView/SessionView/PlayerView/SessionUpdate，以及host.ai.TsumogiriAIStrategy；可复用领域模型/算法保留或明确归位。
- EngineDtoMapper空文件可列残留；ProtocolViewMapper是实际替代。每个旧测试逐项记录保留/迁移/等价替代，不按旧类名删除规则、守恒、隐私、并发或计时断言。

## 最终独立门槛需补强

当前V4FinalArchitectureTest不能单独证明最终依赖：仅import扫描漏FQN（GameScreen.SettlementData有全限定ScoreBreakdown）；package归属聚合对同包跨模块会误豁免；UI放行所有presentation/client.api类型会容许通过组合根或runtime/mapper绕过门面。应按实际类归属检查import与FQN，递归覆盖SpectatorSession/父接口/公开属性/函数泛型与新导航契约；检查真实Gradle主源compile/runtime图，不只单行文本。旧链门槛还应覆盖引用、旧GameClient/TCP/codec和真实启动链，防三个空包通过存在性检查。按R5/R6最终API适配后实际执行，当前不声称通过。
