# V5 导入 V4 UI 资源与可预览原型（Luna）

状态：UI 资源导入和 Unity 原型已完成；本报告只验收这批 UI 原型，不代表 G2 或 V5 产品整体验收通过。

## 基线与范围

- Unity 接收模型：`gpt-6-luna`。未改模型、未委派。
- 来源：`D:/majiang-wt/g2_base`，实际起始 commit `4523485cfd4ec2b32185a7da5c13f6cbaf54a1e1`。
- 隔离工作树：`C:/Users/sncdc/.agent-mesh-bridge/worktrees/thread_20261108_193839_c801`，分支 `mesh/task/thread_20261108_193839_c801`，从同一 commit 起步；修改只在本任务工作树。
- 未接管前序 Windows G2 验证线程；未改正式 Boot/导航/C04 测试对象与事件协议、共享 DLL、engine/client/server、玩法规则或经济；未提交、推送、合并或发布。
- 最终候选仍是此隔离工作树的未提交快照，基点仍为上述 commit；交由桥接生命周期创建提交。关键交付文件 SHA-256：`V4UiPrototype.cs`=`f704dc2db44bb5fb19d39406bb306d639ce872fb6ba2c1c805b55ee8f5b14095`，`V4UiPrototypeBuilder.cs`=`587ff438a2568411a185bd758818215c99484ea583034c596aa5dfd96d33bb0f`，场景=`1be503f90225bb53351f427305fa7082f595085e974026889860b54d47903eb7`，Prefab=`1408c8bcb5605af8f3f7017e9825c1fc0cf811ec58f2ecc27ecc103968a5848f`。
- Unity Editor：`D:/game_dev_tools/unity/Editor/Unity.exe`，Unity `6000.3.27f1`。使用共享互斥锁 `Local\MahjongV5P45UnityValidation`，Player 输出使用任务专属 GUID 目录 `C:/v4uifinal-cf0f68fe270c4e9ebb2e23f4d73d5edd`；Player 由独立验证副本构建，正式 Unity 工程未被 Player 构建覆盖。

## 原型入口

工程：`unity/Mahjong.Unity`

- 场景：[`Assets/Scenes/Prototypes/V4UiPrototype.unity`](../../unity/Mahjong.Unity/Assets/Scenes/Prototypes/V4UiPrototype.unity)
- Prefab：[`Assets/Prefabs/Prototypes/V4UiPrototype.prefab`](../../unity/Mahjong.Unity/Assets/Prefabs/Prototypes/V4UiPrototype.prefab)
- 运行组件：[`V4UiPrototype.cs`](../../unity/Mahjong.Unity/Assets/Scripts/UI/Prototype/V4UiPrototype.cs)
- 导入器与场景创建器：[`V4UiPrototypeBuilder.cs`](../../unity/Mahjong.Unity/Assets/Editor/V4UiPrototypeBuilder.cs)

直接预览：在 Unity 打开 `Assets/Scenes/Prototypes/V4UiPrototype.unity` 并按 Play。编辑器菜单 `Tools > Mahjong > V4 UI Prototype > Create and Open Preview` 首次创建场景和 Prefab；二者已存在时菜单会打开现有场景，不覆写场景或 Prefab，若只有其中一个存在则报错停止。菜单仅导入/配置新增的原型资源，不改写正式场景。可在 Player 上按 F1–F5 切菜单、准备、目标抽屉、确认回执、发牌牌桌；F6/F7 切到 960×540/1600×600；F8 截图；鼠标执行菜单/返回/选择/看破/确认/发牌。按 Escape 返回上一层。

主菜单包含标题、章鱼、六个入口和 420ms 轻量入场动画；单机进入准备页，联机/皮肤/秘籍/设置/退出给出明确预览提示。准备页不展示未发出的手牌；目标抽屉支持六卡片选择/取消、看破、0/10/30/40 点预览、返回/确认；回执可返回准备或进入发牌牌桌。牌桌包含四头像、罗盘、座位公开信息、真实牌面、河区与手牌。所有昵称、点棒、倒计时、牌、费用均为组件内静态样例，仅用于预览，不写入真实会话、权威、牌谱或经济。

## V4 来源与 V5 目标映射

目录内的来源资源文件原样复制，哈希列为源文件 SHA-256；Alpha 列中 `6` 表示 PNG RGBA、`2` 表示 RGB（PNG IHDR color type）。Unity 纹理使用 Sprite、sRGB、无 mipmap、Clamp、Bilinear、未压缩；保留 PNG Alpha。所有表格贴图保留各自纹理文件，多个图集区域导入为命名 Sprite，不把拼接参考图铺成唯一 UI。

| V4 来源 | Unity 目标 / 使用位置 | 尺寸 / Alpha / 切片 | SHA-256（源文件） |
| --- | --- | --- | --- |
| `assets/ui/main-menu/club-background.png` | 复用 `Assets/Art/UI/MainMenu/club-background.png`；菜单背景 | 1672×941 / RGB / cover | `149a2a3c8b35bb6c14030d4e789f3c1c45fb4995f59b65b67d41aa2170e8d86a` |
| `assets/ui/main-menu/octopus-hero.png` | 复用 `Assets/Art/UI/MainMenu/octopus-hero.png`；菜单章鱼 | 1284×1225 / RGBA；V4 manifest 左上裁剪 `[96,64,530,506]`，原型采用完整透明 Sprite，避免局部裁剪出现矩形边缘 | `9b6ae628742220a8146665f71c828d65567001c25c8d406ffbe849cdf8339eea` |
| `assets/ui/main-menu/button-primary.png` | 复用 `Assets/Art/UI/MainMenu/button-primary.png`；朱红按钮 | 96×96 / RGBA / 九宫格 18px 四边 | `781ecc51f840b8f018fb70c566744f41794f22242b5bc749c5e4527e8c586a77` |
| `assets/ui/main-menu/button-secondary.png` | 复用 `Assets/Art/UI/MainMenu/button-secondary.png`；翡翠按钮 | 96×96 / RGBA / 九宫格 18px 四边 | `33eef6c140bd3642007c11e25c10ca41070c083ecb1834dce6b1c9d2a0289423` |
| `assets/ui/main-menu/button-focus.png` | 复用 `Assets/Art/UI/MainMenu/button-focus.png`；hover/press/selected SpriteSwap 状态 | 96×96 / RGBA / 九宫格 18px 四边 | `8c47d0543fe1d64e3f1928a34330ff0a6e5cb483434c8fb6228e0656d916b050` |
| `assets/ui/main-menu/menu-icons.png` | 复用 `Assets/Art/UI/MainMenu/menu-icons.png`；菜单六入口图标 | 384×64 / RGBA / 6×64×64 UV 分片（solo/online/skin/secrets/settings/exit） | `a6249ed8968d7334c872f3ddafe731295a606a342cb5608fc2c4c12ec9687fbc` |
| `assets/ui/table/club-environment.png` | 新增 `Assets/Art/UI/V4Prototype/Table/club-environment.png`；牌桌环境底图 | 1672×941 / RGB / cover | `32f05e536b4cb79da989811620242d242a5afd1805effebf122480f41400983f` |
| `assets/ui/table/jade-table-board.png` | 新增 `.../Table/jade-table-board.png`；独立翡翠桌面图层 | 1536×1024 / RGBA / 固定 1216×620 目标矩形 | `610878722c40b31c50dd2ca27304980ac4787b74003c691fe27954a06061b42d` |
| `assets/ui/table/jade-panel-skin.png` | 新增 `.../Table/jade-panel-skin.png`；准备栏、抽屉、回执、牌手托盘 | 1254×1254 / RGBA / manifest 源边 340px；Unity 九宫格目标边 44px | `4dda0d8a74a46218039fbc7a62cacb49e38ba43c70501ad7df3a9c158b09557b` |
| `assets/ui/table/button-skins.png` | 新增 `.../Table/button-skins.png`；牌桌朱红/翡翠按钮 | 1536×1024 / RGBA / 两个 1456×384 Sprite：primary `[40,96,1456,384]`、secondary `[40,544,1456,384]`；manifest 源边 `[248,248,120,120]`，目标边 `[32,32,18,18]` | `ce49322ed2b56449fce48cf5f9fce1dc833ad2fecf5583e21f31c4133b70653f` |
| `assets/ui/table/animal-medallions.png` | 新增 `.../Table/animal-medallions.png`；四个座位头像 | 1254×1254 / RGBA / 4×627×627：octopus、fox、panda、magpie | `c457e074ba4fb7431d75559dcaecba471cfa516f99360427149a3fe36b1e9149` |
| `assets/ui/table/target-card-scenes.png` | 新增 `.../Table/target-card-scenes.png`；六张目标卡 | 1536×1024 / RGBA / 6×744×324：CHINITSU_MAN/SOU/PIN、DAISANGEN、KOKUSHI、DAISUUSHII | `0000aaec911e09c9ed4aedc787deeb382045a421b30da09891ac1fba69062c37` |
| `assets/ui/table/round-compass.png` | 新增 `.../Table/round-compass.png`；发牌后公开局况罗盘 | 1254×1254 / RGBA | `b34c8db1bf00d0c6f687ffbbcd2bdcd3d8393cbe81f05b7d32c43546f656ea7f` |
| `assets/skins/wikipedia/{man,pin,sou}1..9.png`、`wind1..4.png`、`dragon1..3.png`、`man5r.png`、`pin5r.png`、`sou5r.png` | 新增 `Assets/Art/UI/V4Prototype/Tiles/`；发牌样例手牌与四组河区 | 共 37 张，各 250×304 / RGBA；按真实 Tile 系列导入并逐张引用，不导入花牌/皇牌等无关皮肤 | 每张 SHA-256 见下方牌面清单 |
| `assets/skins/wikipedia/CREDITS.md` | 复制到 `.../Tiles/CREDITS.md`；随资源保留来源与授权 | Wikimedia Commons 现代麻将牌插画，CC BY-SA 4.0；Cangjie6 remake、Jerry Crimson Mann original、User:Dewclouds vectorisation；注明本项目牌序映射；未修改原 PNG | 文档随导入资源保留 |
| `assets/fonts/NotoSansSC-Regular.ttf` 与 `OFL-NotoSansSC.txt` | 不重复导入；复用 `Assets/Art/UI/Fonts/NotoSansSC-Regular.ttf` 和现有 `UiTheme.asset` 字体引用 | V4 源字体 SHA `A3041811A78C361B1DE50F953C805E0244951C21C5BD412F7232EF0D899AF0DA`；Unity 既有 TTF SHA `1C3CAA21F03275894691B6C692FE2CF90799753D11D4FE761C5A8938C238634A`；两者字节不同，预览使用 V5 当前主题引用版本，运行时实测中文可读 | SIL Open Font License 1.1；上游 Google Fonts Noto Sans SC |

已有六张主菜单图文件与 V4 源文件逐字节 SHA 相同，因此保持 V5 原有 `.meta` GUID 与 `UiTheme.asset` 引用；不重复复制、不重生成 GUID。表格切片坐标来自 `assets/ui/table/manifest.json` 的左上原点；导入器逐片转换为 Unity Sprite 的左下坐标 `y = textureHeight - top - height`。所有目标牌、头像、牌面都是真实切片/PNG；文字单独以 V5 当前主题的 Noto Sans SC 动态绘制。V4 菜单图标图集用 RawImage UV 的六等分区域。

来源和许可记录：`assets/ui/main-menu/README.md` 记载背景/章鱼为项目内置 imagegen 创作，控件与图标由程序绘制；`assets/ui/table/README.md` 记载七张牌桌美术于 2026-10-22 在项目内生成，列出 Alpha、用途及 manifest 切片规则。两套 README 未声明外部图像许可或第三方作者；对应原始 README/manifest 仍保留在仓库 `assets/` 源目录。本次复制不改像素。牌面素材单独按 `CREDITS.md` 保留 Wikimedia 来源、作者和 CC BY-SA 4.0 许可；字体复用 V5 既有文件并附有仓库中的 SIL OFL 1.1 许可。

### Wikipedia 37 张牌面哈希

全体均为 250×304、RGBA、CC BY-SA 4.0；此处 SHA-256 对应 `assets/skins/wikipedia/` 原文件。相同文件名已复制至 `Assets/Art/UI/V4Prototype/Tiles/`，并保留上述 `CREDITS.md`。

| 文件 | SHA-256 | 文件 | SHA-256 |
| --- | --- | --- | --- |
| `man1.png` | `2e2956a93ceade592c0a4930348724309f5a7e11c54bd4d614684d0a82c69bff` | `pin1.png` | `c5a61ab116278f0a4959c130ad526f21a45141d756f57d655cbe6744867daea3` |
| `man2.png` | `3cf7896d79185536df56618369db3fac265db35b919db18454766e660e85d1e1` | `pin2.png` | `57d92a63009516708933611b7a30c06ef8195ef913483c945c147ef7b80888bc` |
| `man3.png` | `915f98c12d9b7eb1285efa47a6c5c26e2ee68e9b9d13ae72886993893a1a1849` | `pin3.png` | `c9b2f91d5d98b90d95ee4c5cb5db8230b6f510dfb13c7ac8737eb215b04358d2` |
| `man4.png` | `06a4859c16635c3fcea84dc7c201fa1d2661f35b3aab0d8a20463e7ee7d6d04e` | `pin4.png` | `1c278f0d5f1e20512e83a065178c2bb69240be184745aa0c606e6f13f382f5cd` |
| `man5.png` | `21dde1e7b4928f4ef94c8f50fef4ca4a5fb2f5001567238af2c9d5c2ec8955e7` | `pin5.png` | `2a306471d805cf1e84ffe8dadd16b0d62a134267b2bef8609df6907a5a3e6b05` |
| `man6.png` | `161884ac16848e19d7641215d568696e07eb4f22222b18522497bd509aadf021` | `pin6.png` | `f19dc0a2f686924ee1d81a83fab58bf9eb4c234c7b470247d3342c735ef78b5e` |
| `man7.png` | `13d368f728591a153536c8e734843291cffe619f74eb5c10e8e3d6cae5c383ce` | `pin7.png` | `a29d218692710cd6dce12697f4afe82f8e887e97bbe090ecaaed8b53aa7177be` |
| `man8.png` | `e77510f86bc3831ee3d9348c968221e4fd7988e96e07fb01ca25bbb407bdf569` | `pin8.png` | `90067f8bc25d5e69717c6056bb8f542fe1222e6e7a1bca73e4993cb35a712f75` |
| `man9.png` | `65f2c3822bfc4300e26a5d956667dcac1328d83764272f764b4a4285f969556f` | `pin9.png` | `0e70fb3a3506b126a9737e282f2cd8b785d34e3bff2e53477a5735614c4db655` |
| `sou1.png` | `56e4bef5b23291a380677e90b34f5fe122f68ee33c54284a6a60e21d57c74c98` | `wind1.png` | `fa8f6fc6ea72d0b5d356ea4c59bb25e30ca33efd95d715c3a4a384ef27afd468` |
| `sou2.png` | `2d07d434683e9bb0d7b51ae3f91909f631dc7df36c8bdd2d01d8c133262eb6e0` | `wind2.png` | `8a6476e2e167059fa222cfcf3f070791ad290fce540970cf7a9e860769d94cf0` |
| `sou3.png` | `c8fb1a432ff2de5955a27c21615f6ab7c3f086e20f4f6185a8403adb9e2c5468` | `wind3.png` | `8ae42ecde8d91561dd32879e83da248236711ce57632b27d3b5ab48fc74d9d71` |
| `sou4.png` | `d719ff09519f73fb679b54f8757f8e3f239be3c6c1f31970882ab19ed07772b9` | `wind4.png` | `229c440c0fb7f9b55ca2523358b3d352a9b3b325aefdf7ac5fa5e937b3200a17` |
| `sou5.png` | `0d23a37a1d22dc807e9480d5badc431f964f7d42bfc1295c1fec4e9ae7edd07a` | `dragon1.png` | `d2cc44ddc76bf65f46e8a1487743d313501149ab2dab5e1f53737f9ec4ce4798` |
| `sou6.png` | `db8d8769951ff31ba9faa80e39c7cbfe541c51617737b1cafa6eee5d69229660` | `dragon2.png` | `ce232b90c8768633312e4176d7cacf821ab061bc32dcb87d186b297bef24f0dc` |
| `sou7.png` | `5e31588fc92eeee88937a26a54fe8cd5c9e959d6a91b63df51bca96630df9c79` | `dragon3.png` | `234231d23920a61f648abd84b5e7b9619f3de6b5490647c207c55c1b6bafcfc8` |
| `sou8.png` | `7dcb3341ee49787fa7dc9881ec13dac684d3d8db6fe83ea563c06667bc8bc289` | `man5r.png` | `d2da52a9bb877c2f3419509a374a3f4931d0ff2ddca61cdcbaea6839959969d6` |
| `sou9.png` | `fe569dcffa90f09ef82d5393dac804e8718e7082526d9ed6fea364ee671bb46e` | `pin5r.png` | `946b41d672b1e39e7c8b16513ca952ea10141f547f282831514a724989ea7604` |
| — | — | `sou5r.png` | `8d90d416c5ebec7eea058c0806ffa651c70fd46d0f9d8997158468b0f5f4d162` |

### V4 设计参考

- 主菜单：`doc/ui/main-menu/DESIGN.md`、`LAYOUT-1280.svg`、`MAIN-MENU-EXPECTED.png`、`DELIVERY.md`。
- 牌桌准备/目标选择/回执/发牌：`doc/ui/round-preparation/DESIGN.md`、`ASSET-HANDOFF.md`、`VISUAL-COMPOSITION.md`、`VISUAL-DELIVERY.md`、`RESPONSIVE-DESIGN.md`、`RESPONSIVE-APP-DELIVERY.md`、`ASSEMBLY-01-entry` 至 `ASSEMBLY-04-playing` 的 SVG/PNG，以及 `runtime-01-entry.png`、`runtime-02-target-picker.png`、`runtime-08-dealt.png` 等稳定运行图。
- 期望图与 ASSEMBLY 拼接图只作设计参考；桌面 V4 后续 `VISUAL-DELIVERY`、响应式交付及 `runtime-*` 截图已经显示实际落地状态。早期文档里的“尚未接入”是当时状态，不按当前事实引用。
- 只读检查 V4 `presentation/menu`、`presentation/game`、`presentation/common` 的布局/绘制与 `SkinManager`/`TileSkin`，没有迁入领域规则、运行时皮肤管理或 V4 菜单回调。
- 当前原型保留动物麻将馆、章鱼、翡翠桌、朱红按钮、金边、头像、罗盘和实际牌面。与 V4 运行图的差异：使用完整透明章鱼原图而非 manifest 局部裁片；若干静态示例的座位/牌/河牌布局是原型自有展示；目标确认用 Unity 内简单状态切换。没有声称等同于生产牌桌流程。

## 实际验证

已完成短资源预检、Unity 导入/编译、Editor 场景生成和 Windows Player 运行。没有重跑 G2 32 项或 Android 构建。

| 检查 | 实际执行与结果 |
| --- | --- |
| 正式 V5 工程导入/编译 | 共享锁内使用 Unity batchmode 执行 `Mahjong.Unity.EditorTools.V4UiPrototypeBuilder.BuildAndOpenPrototype`，退出码 0；日志 `C:/v4uifinal-cf0f68fe270c4e9ebb2e23f4d73d5edd/unity-main-editor.log`（首次生成 `READY`），最终复核日志同目录 `unity-main-final-editor.log`（`OPENED existing scene`，退出码 0）。 |
| Player 构建 | Unity 对独立验证副本构建新增原型场景，日志 `C:/v4uifinal-cf0f68fe270c4e9ebb2e23f4d73d5edd/unity-build18.log`，包含 `V4_UI_PROTOTYPE_PLAYER_PASS` 与 batchmode 成功退出。输出在该目录的 `player18.exe`；未替换正式入口。 |
| 实际运行 | 启动构建的 Windows Player，在实际窗口点击菜单、准备、看破、目标、取消/返回、选择、费用预览、确认、回执返回、发牌与回准备；额外用 F1–F7 检查键盘状态入口。1280×720 和 1600×600 使用最终候选运行完整矩阵；960×540 运行准备/抽屉/回执/发牌/返回状态，最终候选另实际启动并捕获 960×540 菜单。Player 正常退出；运行日志 `player18-1280.log`、`player18-wide.log` 与 `player15-960.log` 保存在同目录。 |
| 截图尺寸与外观 | 真实 Player ScreenCapture 输出经 PNG IHDR 核对：1280×720、960×540、1600×600。已视觉检查菜单、准备、目标卡、回执、发牌后的头像/罗盘/牌面、中文、Alpha、桌面背景覆盖和裁切。点击使用 DPI 感知窗口坐标，与游戏实际分辨率一致。 |
| 最终复核 | 修改 Editor 创建器使重复菜单操作改为打开现有场景、保护手工改动后，再在正式工程共享锁内运行 Unity Editor 导入/编译；退出码 0，确认 `V4_UI_PROTOTYPE_OPENED`，无编译错误。 |

Unity 日志和 Player 不放入交付树；截图是从真实运行 Player 捕获，不是 V4 参考原图、Unity 静态 Scene 预览或合成图。Evidence 目录保留 27 张代表截图；每张文件 SHA-256 列在下表。

| 截图 | SHA-256 | 截图 | SHA-256 | 截图 | SHA-256 |
| --- | --- | --- | --- | --- | --- |
| `menu-1280x720.png` | `6207388cd9a100537a2ecb6378ba035f56604fe54e2ecdadba1f18cc8a40d72d` | `menu-960x540.png` | `b5d4da883d90f0c1c36dafd33a33d6a02f10e622002b868649b860bfe64878c4` | `menu-1600x600.png` | `d4f0e30f37322cfe991bb8d158a915e842f555751f3d73d254fde72f968ba37d` |
| `preparation-1280x720-cost0.png` | `a871be3d1f2b9d88dbb1a01e4e4da614b752a9d8353229aed454d8dab880957a` | `preparation-960x540-cost0.png` | `9d293e8c2e268aafe3470ec610b66d3ec3c0bcf3308c638125da31692648a829` | `preparation-1600x600-cost0.png` | `f8647a4279c4717591e34407ef657e1b618c238c22d181f8ca254c39775f36c4` |
| `preparation-1280x720-cost10.png` | `387335bc35bfa4a410021971bdb53b7262f76140ee263733aabb3e8c4a453cba` | `preparation-960x540-cost10.png` | `f7c1b8ae9372dc39ecffcfd3944f383a1b31871c62be0cbf9ff6cbdee5c717e0` | `return-preparation-1280x720.png` | `a871be3d1f2b9d88dbb1a01e4e4da614b752a9d8353229aed454d8dab880957a` |
| `targets-1280x720-cost0.png` | `ce26b40bbe3a108361c9f0d50ad4b0a498b4d9c654a6d313a7a539d57203692e` | `targets-960x540-cost0.png` | `5b759e6601322051f211fe62a55861bff6ccb31eeadd4786791e282ef8b5be5b` | `targets-1600x600-cost0.png` | `d8c9311631a82bd498288205c542134fecb539e7ed3c6ac4d5a37d6122a26762` |
| `targets-1280x720-cost30.png` | `5ea5adc8f493872ee3e8e35d79ddcdea9122573ad62996d9313d0126a6d9b8da` | `targets-960x540-cost30.png` | `ec8601b855bc9ff220afe3e9007017b301816b8510d45c071fe782eb02ac0b9f` | `targets-1600x600-cost30.png` | `660b7b80f5543d925f5ae7d1c3a22088ba4c6d2dda04898687db571d8dcbe614` |
| `targets-1280x720-cost40.png` | `73844aaee4e6a63c63038b1f7262134131204f5551a2fb2f9daa0d0c5bdd4514` | `targets-960x540-cost40.png` | `d2236e8b5c8a73520d100c495305de7ca9fb6a4da47c12ad8ba6aa1196798b99` | `targets-1600x600-cost40.png` | `efea0972946e05ce0fe053a1cfd95eab2e34eee0c0074dcd64143bb6bbbf20b4` |
| `receipt-1280x720-cost40.png` | `57213915e8aaae1d25693cb5b11f9207b9c423901d9780b8399a4cf0f70ceb6d` | `receipt-960x540-cost40.png` | `9787d9860e3ab38b4868b9c0d6789f1b6a5ccd6d7326116d7e7b97fe1ca3f1d5` | `receipt-1600x600-cost40.png` | `b5569d1af944562c9fec3a40c8a3ce9d780871d22311e1e41c55f3a9ccd559c2` |
| `receipt-1280x720-cost0.png` | `6a9a17e022fde7e7ebaff3efdd327145e51eabc33070ac30309de931c582c0d5` | `playing-960x540.png` | `b1a46e2b51cf14a5636318d0501ccf0472348dd8c302c62136a5091a7173fbde` | `playing-1280x720.png` | `4da02aeebd14c52e0bc62cf3f54f7f46a7fed8a871ffd76669138e315cdb479b` |
| `playing-1600x600.png` | `b44f366b96eaf48d3a1e9406cc013a8f2694f6127f86c9db59ea8bfa15e702e5` | `return-preparation-960x540.png` | `f7c1b8ae9372dc39ecffcfd3944f383a1b31871c62be0cbf9ff6cbdee5c717e0` | `return-preparation-1600x600.png` | `e8e10f849fa14d0520181a7301104119ba98bb17539426d41fb10d6aa260da68` |

证据目录：[`ui-prototype-evidence/`](ui-prototype-evidence/)。1280×720 最终运行截图覆盖准备 0/10、目标抽屉 0/30/40、回执 0/40、返回和发牌；960×540 覆盖菜单、准备 0/10、抽屉 0/30/40、回执 40、发牌和返回；1600×600 覆盖菜单、准备、抽屉 0/30/40、回执 40、发牌和返回。看破 10 点和目标 30 点叠加后 40 点均实际点击切换。截图在超宽视口按画布高度缩放并居中，桌面背景 cover 填充。

## 边界与剩余验证

- 在线、大厅、皮肤、秘籍、设置、退出入口只有提示面板/预览反馈；没有 V4 流程、规则、账户、网络、存档、结算、计费或 AI。
- 牌桌中的牌/点棒/姓名/计时都是静态视觉样例；没有动态对局、牌张守恒验证或经济效果。
- 原型代码支持目标抽屉中不选牌、仅开看破的 10 点价格，以及相应确认回执；本轮自动点击坐标未能把抽屉切换至该组合，因此没有将该组合或 10 点回执列为实际运行证据。0/10/30/40 的费用显示和 0/10 准备、0/30/40 抽屉组合由以上真实截图覆盖；建议独立人工点验抽屉 10 点组合。
- 只验证了 Windows Editor 和 Player；未验证 Android 真机、移动触控、任意窗口连续拖拽缩放、正式 V5 桌面流程与前序 G2 任务。
- V4 源码中其他没有已落地设计的功能没有擅自拓展。现有桌面预览不应当作 G2 或 V5 产品验收证据。
- 交付仍在本隔离工作树，等待桥接生命周期创建提交并按 V5 复核协议独立审查；此处没有人工 `approve_task`、合并、发布或提交操作。
