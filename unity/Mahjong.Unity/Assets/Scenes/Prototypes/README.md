# 🀄 千术麻将 V5 美术与 2.5D 牌桌原型说明文档 (README)

## 1. 场景与工程资产路径

- **V5 美术原型场景**：[`Assets/Scenes/Prototypes/V5ArtPrototype.unity`](V5ArtPrototype.unity)
- **V5 美术原型 Prefab**：[`Assets/Prefabs/Prototypes/V5ArtPrototype.prefab`](../../Prefabs/Prototypes/V5ArtPrototype.prefab)
- **运行控制组件**：[`V5ArtPrototype.cs`](../../Scripts/UI/Prototype/V5ArtPrototype.cs)
- **2.5D 牌面组件**：[`Tile2D5View.cs`](../../Scripts/UI/Prototype/Tile2D5View.cs)
- **九宫格切片支持**：[`PrototypeNineSliceImage.cs`](../../Scripts/UI/Prototype/PrototypeNineSliceImage.cs)
- **Editor 一键创建与打开菜单**：[`V5ArtPrototypeBuilder.cs`](../../Editor/V5ArtPrototypeBuilder.cs)
- **参考 V4 UI 原型场景**：[`Assets/Scenes/Prototypes/V4UiPrototype.unity`](V4UiPrototype.unity)

---

## 2. 打开与操作方式

### 2.1 Editor 快速打开
在 Unity Editor 顶部菜单点击：
- **`Tools > Mahjong > V5 Art Prototype > Create and Open Preview`**
  - 若场景和 Prefab 已存在，将直接打开现有场景；
  - 若尚未生成，将自动配置纹理、生成 Prefab 并打开场景。
- **`Tools > Mahjong > V5 Art Prototype > Rebuild Scene and Prefab (Force)`**：强制重新配置纹理并重建 Prefab/场景。

### 2.2 手动打开与运行
1. 在 Unity Project 窗口中定位至 `Assets/Scenes/Prototypes/V5ArtPrototype.unity`。
2. 双击打开场景。
3. 点击 Unity Editor 顶部的 **Play Mode** 按钮（或快捷键 `Ctrl + P`）开始预览。

### 2.3 鼠标交互与操作
- **主菜单界面**：
  - 点击 **【单机对局】**：即时切换进入 2.5D 麻将桌原型。
  - 点击 **【牌面图鉴】**：打开 136 张实体牌图鉴与 CC BY-SA 4.0 完整授权说明弹窗。
  - 点击 **【联机对战】 / 【千术秘籍】 / 【游戏设置】 / 【退出游戏】**：弹出预览提示占位弹窗。
- **2.5D 麻将桌界面**：
  - **手牌交互**：
    - 光标悬停在手牌上方：牌身即时平滑升起 18px 2.5D 高度，底部投影等比放大并柔化，倒角高光显现。
    - 点击任意手牌：选中该牌（上浮 24px 并点亮金色 2.5D 轮廓外发光）；再次点击取消选中。
  - **顶部悬浮演示控制栏（Demo Controls Bar）**：
    - **`[▶ 自动演示 / ⏹ 停止自动]`**：循环自动演示【摸牌 -> 选牌悬停 -> 打牌入河 -> 理牌】全流程。
    - **`[⏸ 暂停 / ▶ 继续]`**：随时冻结当前正在播放的摸牌或打牌动画，便于在 Editor 中从容细看；再次点击平滑继续。
    - **`[🔄 重置]`**：将牌桌恢复为初始起手发牌状态。
    - **`[摸牌 [1]]`**：单独演示从牌山抽取 1 张牌抛物线飞入手牌摸牌位。
    - **`[选牌 [2]]`**：手牌波浪式逐张上浮 18px 并点亮倒角反光。
    - **`[打牌 [3]]`**：将选中的手牌（或摸入手牌）打出入河，牌身由立姿 2.5D 过渡为平卧 2.5D 并精确落入河中槽位。
    - **`[立直 [4]]`**：演示立直横置打牌入河，并在桌面中央放置 1000 点立直棒。
    - **`[1.0x / 0.5x / 2.0x]`**：切换动画播放速度，0.5x 慢放可清晰观察 2.5D 厚度、分层材质与投影变化。
    - **`[主菜单]`**：即时切回主菜单，并播放六大入口按钮 420ms 阶梯级联滑入动画。

### 2.4 键盘快捷键
- **`Space`**：暂停 / 继续当前所有动画
- **`R`**：重置 / 重播初始状态
- **`1`**：触发摸牌演示
- **`2`**：触发选牌/悬停波浪演示
- **`3`**：触发打牌入河演示
- **`4`**：触发立直出牌演示
- **`Tab` / `Esc`**：切换主菜单与 2.5D 牌桌（或关闭图鉴弹窗）
- **`F1`**：快捷切至主菜单
- **`F2`**：快捷切至牌桌
- **`F3`**：快捷打开 136 牌面图鉴与授权说明

---

## 3. 动画与视觉特性列表

| 动画 / 视觉元素 | 表现与实现细节 |
| :--- | :--- |
| **2.5D 牌身厚度 (Thickness)** | 平卧牌（牌河弃牌、副露吃碰杠、牌墙牌山）展示明显的斜角视角厚度：包含上层象牙骨质层（38%）与下层竹黄/绿胶底板层（62%），两层之间有清晰的接缝微线。 |
| **2.5D 倒角斜角 (Bevel / Chamfer)** | 立牌（手牌）正面采用精细倒角处理：顶部高光边缘、侧边反光边缘、底部微接触暗面以及微凸弧面象牙白微渐变，在深绿翡翠桌布上立体感鲜明。 |
| **真实动态投影 (Drop Shadow)** | 每张牌配备柔化椭圆接触投影。手牌立起时投影向下方扩展并散开，落桌时投影收拢贴地。 |
| **摸牌动画 (Draw Tile)** | 牌山顶部砖垛抽取一张牌，在空中划出平滑抛物线飞向手牌摸牌位，飞行过程中由平卧微倾斜转为立体直立。摸牌位与原有 13 张手牌保留 18px 独立视觉间隙。 |
| **选牌与悬停动画 (Select / Hover)** | 悬停时光标所在牌平滑上浮 18px，倒角高光增强，接触投影柔化放大；选中后上浮 24px 并点亮金色 2.5D 发光外框与倒角轮廓。 |
| **打牌入河动画 (Discard Tile)** | 选中手牌高高升起并沿平滑三维弧线飞向对应席位的牌河网格槽位；在空中由立牌翻转为 2.5D 平卧姿态并精确落入河中槽位。 |
| **理牌顺移 (Hand Re-alignment)** | 当打出中间某张手牌时，空缺右侧的所有手牌顺畅左滑闭合间隙；若有刚摸入的摸牌，则自然并入手牌列。 |
| **立直宣告与横置牌 (Riichi)** | 打出的牌横向 90 度旋转落河，并在桌面中心生成千点立直棒。 |
| **牌墙与双层牌垛 (2-High Wall)** | 桌面中央上方展示 10 垛双层叠放的 2.5D 牌山；王牌区第三垛上层牌翻开，露出宝牌指示牌（赤五万）。 |
| **副露区 (Melds: 吃/碰/杠)** | 清晰展示吃（4-5-6条，左牌横置）、碰（發-發-發，中牌横置）与暗杠（1-1-1-1筒，外侧两张牌背朝上展示竹黄底、内侧两张明牌展示），位于牌桌右下角（手牌托盘上方，互不遮挡）。 |
| **菜单与牌桌过渡 (Menu Switch)** | 主菜单与 2.5D 牌桌支持即时切换；切回主菜单时六大入口按钮重新执行 420ms 阶梯级联滑入动画。 |

---

## 4. 资源来源与版权署名说明

### 4.1 V4 既有美术资产
- **主菜单背景 (`club-background.png`)** 与 **章鱼主角 (`octopus-hero.png`)**：项目内置美术资产，使用完整透明原图，避免边缘硬切。
- **主菜单按钮 (`button-primary.png`, `button-secondary.png`, `button-focus.png`)** 与 **入口图标 (`menu-icons.png`)**：原样复用 V4 UI 资产。
- **牌桌环境背景 (`club-environment.png`)**、**翡翠牌桌桌面 (`jade-table-board.png`)**、**翡翠饰条面板 (`jade-panel-skin.png`)**、**动物头像徽章 (`animal-medallions.png`)**、**风圈罗盘 (`round-compass.png`)**：原样复用 V4 牌桌素材，九宫格边框依规范独立映射。

### 4.2 37 张麻将牌面资产与 CC BY-SA 4.0 署名
- **牌面列表**：34 种标准牌型（万子1~9、筒子1~9、索子1~9、东南西北风、白發中）+ 3 张关东赤五宝牌（赤五万、赤五筒、赤五索），共 37 张高精度 PNG 纹理，存储于 `Assets/Art/UI/V4Prototype/Tiles/`。
- **来源**：Wikimedia Commons 现代麻将单牌插画系列（[https://zh.wikipedia.org/wiki/麻将](https://zh.wikipedia.org/wiki/%E9%BA%BB%E5%B0%86)）
- **授权协议**：**Creative Commons Attribution-ShareAlike 4.0 International (CC BY-SA 4.0)**
- **作者署名**：
  - **Jerry Crimson Mann** (原始设计)
  - **Cangjie6** (重新绘制 Remake)
  - **User:Dewclouds** (矢量化 Vectorisation)
- **授权全文**：<https://creativecommons.org/licenses/by-sa/4.0/legalcode>
- **署名保留**：原始说明与版权文件完整保留在 [`Assets/Art/UI/V4Prototype/Tiles/CREDITS.md`](../../Art/UI/V4Prototype/Tiles/CREDITS.md)，并在原型运行时的【牌面图鉴】弹窗中常驻提供。

### 4.3 2.5D 厚度、斜角与光影程序化纹理
生成并保存在 `Assets/Art/UI/V4Prototype/Tiles2D5/`：
- `tile_standing_front.png`：2.5D 立牌正面底模（象牙白渐变、上边斜角强高光、左右反光、下边微暗部）。
- `tile_flat_front.png`：2.5D 平卧牌正面底模（带右侧与下侧 38% 象牙骨层 + 62% 竹黄底板与拼缝）。
- `tile_flat_back.png`：2.5D 平卧牌背底模（带竹黄底面与侧边厚度）。
- `tile_shadow_soft.png`：柔化多阶椭圆接触投影。
- `tile_glow.png`：金色 2.5D 选中发光轮廓。
- `riichi_stick.png`：白骨红心千点立直棒。

---

## 5. 未接入产品逻辑说明

> [!NOTE]
> 本场景为纯前端视觉与交互美术原型，仅用于在 Unity Editor 中检视 2.5D 牌身立体度、高光斜角、接触投影及动画流水线节奏。

1. **未接入服务端与网络**：没有连接 TCP 或本地权威服务，不产生网络信包或心跳。
2. **未接入游戏规则与经济**：不计算 41 种立直麻将番种、点数与出千点数；牌桌上的点棒（25000）、牌山余量（69）、局风（東1局）等均为原型视觉样例。
3. **未改动权威与牌张守恒**：游戏核心 `TableAuthority`、`RoundStateMachine`、物理守恒律（136张闭环）及七模块边界保持独立，不受本原型任何影响。

---

## 6. 验证环境与平台状态声明

- **Unity Editor Play Mode (Windows 11, Direct3D 11 / D3D11)**：**VERIFIED**（已完成 Unity 编译、16 项组件级 Play Mode 自动化交互与状态探针全量 PASS，包括场景加载、弹窗关闭、飞牌所有权清理、菜单入场状态恢复、选牌理牌、协程句柄自动释放、3 轮自然完整自动循环及 0 异常验证；人工肉眼视觉审美观察与截图已在 Windows Editor D3D11 视口中核验）。
- **Windows Standalone Player**：**NOT_RUN**（本次任务按要求仅限 Editor 内直接观察与轻量探针，不构建发布包）。
- **Android APK / 模拟器 / 真机**：**NOT_RUN**（不适用本美术原型任务）。
- **WebGL / macOS / Linux**：**NOT_RUN**。
