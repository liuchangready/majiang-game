# R5 ROOT：菜单、大厅、组合根与双端启动

只在 Codex 接受整个 R4 后派发。先读 CONTRACT.md、TASK-ANTI-R5.md、R5-R6-PREPARATION-AUDIT.md、R5-UI-INTERFACE-PREPARATION.md、R6-TEST-PRESERVATION-INVENTORY.md。用户授权默认V4入口/无V3兼容，Anti开发、Codex验收。

独占 `presentation/src/main/kotlin/com/majiang/MahjongGame.kt`、`screen/MenuScreen.kt`、新 `presentation/common/UiAssets.kt` 与 `TableNavigation.kt`、对应menu/root测试，`lwjgl3`/`android` 启动器与AndroidManifest、必要Gradle、`server/TableServerMain.kt`/server Gradle，以及**唯一client.api拥有权**（只针对R5真实缺字段/方法及其必要测试）。另授权 `client/src/main/kotlin/com/majiang/client/transport/udp/UdpClient.kt` 中仅限初次连接失败的socket清理及其必要测试（当前该文件创建DatagramSocket后解析/连接异常时catch只结束Future，漏关socket），由ROOT唯一拥有，不与其它两路重叠。其他两路负责game与render，不改他们的文件。根实现冻结于R5-UI-INTERFACE-PREPARATION的导航/资源边界；构造名字变化必须及时说明，以便另外两路同步，不能写GameScreen/其适配器或render。

`MahjongGame` 仅作组合根，装配 `InProcessServer(true).createConnection()` + `ServerConnectionSession` 统一大厅，单机真实经 `LobbySession.createRoom()`；远端 `connectUdp` 初始socket/DNS在后台调用，成功/错误回GL线程，失败释放socket和自有scheduler。菜单支持单机/UDP地址端口连接、列表、建房/加入、roomInfo、赛制规则、准备、开赛、观战；菜单开赛需在 `LobbySession` 补同名 `start(): CompletableFuture<Receipt>` 并由现有runtime实现，不能通过cast向玩家画面借动作。屏幕只收门面，根保存具体runtime并负责导航、tick、pause/resume、leave与close语义。主动回菜单先成功离房，断线保可信视图不自动抢占。共享中文NotoSansSC字体、`SkinManager`/纹理由根按所有权一次释放；连续进退桌和换肤验证不可遗漏。Android加网络权限；默认 `:server:run` 改UDP，`--bind/--port` 有效并打印真实端口。桌面/Android启动链只走V4，无旧SessionAccess重载/LocalHost直连。

与game/common两路互斥，不修改其源码/测试或Codex reviewer。只用内置文件工具，不运行shell/build/test/其它agent/提交。交付 `delivery-ANTI-R5ROOT.md` 列实际文件、菜单场景测试及平台权限/字体检查、未完成项、未运行测试声明；Codex串行整体验收。
